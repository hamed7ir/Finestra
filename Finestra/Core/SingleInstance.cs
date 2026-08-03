using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Finestra.Helpers;

namespace Finestra.Core
{
    /// <summary>
    /// FIN-SINGLETON — process-level single instance + launcher activation. Clicking the icon while
    /// Finestra runs must not start a second process; it brings the running app forward instead.
    ///
    /// Gate = a named mutex scoped to the EXE'S FULL PATH (a stable hash of the lowercased, normalized
    /// Assembly.Location): the desktop icon and Start Menu both launch the installed exe so they collapse
    /// to one instance, while a portable copy in another folder deliberately runs as its own instance.
    /// Signal = a named pipe (same path hash + the current user's SID so two users on one machine don't
    /// collide): the owner runs a listener; a second launch grants the owner foreground rights
    /// (AllowSetForegroundWindow — without it the taskbar just flashes orange), sends one ACTIVATE line,
    /// and exits 0. The pipe name is DISJOINT from the engine's stats pipe (finestrardp.&lt;pid&gt; —
    /// compiled into wfreerdp, untouchable).
    ///
    /// The multi-window architecture is untouched: tabs, tear-off, and multiple session hosts all live
    /// INSIDE the one process — this is a process gate, not a window gate.
    ///
    /// Trap handling: the mutex is rooted in a static field (the GC must never finalize+release it
    /// mid-run); a hung/dead owner is handled by a 2s pipe-connect timeout after which a normal instance
    /// starts anyway (a click must never do nothing); the listener thread's whole body is caught and it
    /// marshals to the UI thread with BeginInvoke + disposal guards before touching any form.
    ///
    /// KNOWN LIMITATION (by design): installed + portable instances share Documents\Finestra — saves are
    /// atomic so nothing corrupts, but the last writer wins on settings/connections.
    /// </summary>
    public static class SingleInstance
    {
        private static Mutex _mutex;        // rooted for process lifetime — NEVER local (GC would release the gate)
        private static string _pipeName;

        [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int dwProcessId);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_RESTORE = 9;
        private const int ASFW_ANY = -1;

        /// <summary>Run FIRST in Main. True → continue normal startup (we own the gate, or the owner is
        /// hung/dead and we take over so the click still yields a working app). False → we activated the
        /// running instance; exit 0 immediately (no forms, no JobGuard, no paths init, no log).</summary>
        public static bool TryBecomeOwner()
        {
            string exe;
            try { exe = Path.GetFullPath(Assembly.GetExecutingAssembly().Location).ToLowerInvariant(); }
            catch { return true; }   // cannot identify ourselves → never block the launch
            string hash = Hash16(exe);
            _pipeName = "Finestra.Activate." + hash + "." + UserSid();

            bool createdNew;
            try { _mutex = new Mutex(true, @"Local\Finestra.SingleInstance." + hash, out createdNew); }
            catch { return true; }   // mutex API failure → run normally rather than dead-click
            if (createdNew) return true;

            // Not the owner: activate the running instance and exit. If IT is hung/gone (trap 4), the
            // 2s connect times out and we start normally — a click must never do nothing.
            return !SignalExisting();
        }

        /// <summary>Grant the owner foreground rights, then send one ACTIVATE line. True = delivered.</summary>
        private static bool SignalExisting()
        {
            try
            {
                GrantForegroundToOwner();   // trap 1 — else the manager only flashes on the taskbar
                using (var c = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out))
                {
                    c.Connect(2000);
                    byte[] b = Encoding.ASCII.GetBytes("ACTIVATE\n");
                    c.Write(b, 0, b.Length);
                    c.Flush();
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>The foreground grant: find the running Finestra (same name, same session, not us,
        /// earliest start) and AllowSetForegroundWindow(pid); fall back to ASFW_ANY if the lookup fails.
        /// Only a process that OWNS the foreground (us — freshly launched by the click) may grant it.</summary>
        private static void GrantForegroundToOwner()
        {
            try
            {
                var me = Process.GetCurrentProcess();
                Process best = null;
                DateTime bestStart = DateTime.MaxValue;
                foreach (var p in Process.GetProcessesByName(me.ProcessName))
                {
                    try
                    {
                        if (p.Id == me.Id || p.SessionId != me.SessionId) continue;
                        if (p.StartTime < bestStart) { bestStart = p.StartTime; best = p; }
                    }
                    catch { /* StartTime can deny — skip that candidate */ }
                }
                AllowSetForegroundWindow(best != null ? best.Id : ASFW_ANY);
            }
            catch { try { AllowSetForegroundWindow(ASFW_ANY); } catch { } }
        }

        // ── owner side ───────────────────────────────────────────────────────────────────────────────

        /// <summary>Start the ACTIVATE listener (owner only; call once MainForm exists and has a handle).
        /// Background thread; whole body caught (the audit's crash-surface rule); marshals via BeginInvoke
        /// with disposal guards (the B11 lesson) before touching any form.</summary>
        public static void StartListener(Form manager)
        {
            var t = new Thread(() => Listen(manager)) { IsBackground = true, Name = "SingleInstance" };
            t.Start();
        }

        private static void Listen(Form manager)
        {
            while (true)
            {
                try
                {
                    using (var s = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1))
                    {
                        s.WaitForConnection();
                        using (var r = new StreamReader(s, Encoding.ASCII))
                        {
                            string line = r.ReadLine();
                            if (line != null && line.StartsWith("ACTIVATE", StringComparison.OrdinalIgnoreCase))
                                Dispatch(manager);
                        }
                    }
                }
                catch { try { Thread.Sleep(500); } catch { } }   // never throw into the void; brief backoff
            }
        }

        private static void Dispatch(Form manager)
        {
            try
            {
                Form target = (manager != null && !manager.IsDisposed) ? manager : UI.SessionHost.LastActiveOrAny();
                if (target == null || target.IsDisposed || !target.IsHandleCreated) return;   // app is exiting — nothing to surface
                target.BeginInvoke((Action)(() =>
                {
                    try
                    {
                        var mf = target as UI.MainForm;
                        if (mf != null && !mf.IsDisposed) { mf.ActivateFromSecondInstance(); return; }
                        ActivateGeneric(target);   // fallback: the last active session host
                    }
                    catch { }
                }));
            }
            catch { }
        }

        /// <summary>Generic surface-a-window: show, un-minimize to its PREVIOUS state, take foreground.</summary>
        private static void ActivateGeneric(Form f)
        {
            try
            {
                if (f == null || f.IsDisposed) return;
                if (!f.Visible) f.Show();
                if (f.WindowState == FormWindowState.Minimized) ShowWindow(f.Handle, SW_RESTORE);
                f.Activate();
                f.BringToFront();
                SetForegroundWindow(f.Handle);
                FileLog.Line("[SINGLETON] activated fallback window \"" + f.Text + "\"");
            }
            catch { }
        }

        // ── identity helpers ─────────────────────────────────────────────────────────────────────────

        private static string Hash16(string s)
        {
            try
            {
                using (var sha = SHA256.Create())
                {
                    byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                    var sb = new StringBuilder(16);
                    for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return "default"; }
        }

        private static string UserSid()
        {
            try { using (var id = WindowsIdentity.GetCurrent()) return id.User != null ? id.User.Value : id.Name.Replace('\\', '_'); }
            catch { return Environment.UserName; }
        }
    }
}
