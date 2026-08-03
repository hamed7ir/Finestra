using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Finestra.Helpers;

namespace Finestra.Core
{
    /// <summary>
    /// FIN-KEYBOARD — touch-keyboard detection for RT: publishes the on-screen keyboard's SCREEN rectangle
    /// (empty when hidden) so each surface can inset its own content. ONE service, consumers compute their
    /// own overlap — the same feed serves a windowed form, a maximized form and a fullscreen host.
    ///
    /// MECHANISM (mirrored from TelegArm's proven RT implementation — MainForm "Part B" + TouchKeyboard):
    ///   • A guarded WinForms Timer poller — 350 ms idle, 120 ms while the keyboard is up (TelegArm's
    ///     adaptive cadence; "not a new always-on high-freq timer"). Stopped entirely when no window is
    ///     registered or the setting is off (Tegra 3 — don't burn a timer for nothing).
    ///   • Detection = the legacy TabTip window: FindWindow("IPTip_Main_Window") + IsWindowVisible +
    ///     NOT DWM-cloaked (DwmGetWindowAttribute(DWMWA_CLOAKED), guarded — cloaked ≡ dismissed on 8.1)
    ///     + GetWindowRect with TelegArm's sanity checks (height ≥ 150, bottom-docked within 80 px of the
    ///     screen bottom). The WinRT InputPane/OccludedRect branch TelegArm tries first is DELIBERATELY
    ///     OMITTED: InputPane returns E_NOINTERFACE on .NET 4.7 on the RT (Hamed's confirmed device
    ///     finding) — the TabTip-window path below is the branch that actually fires there (via=TabTip).
    ///   • Hardware-keyboard suppression (TelegArm's TouchKeyboard lesson): the original Surface RT
    ///     reports slate mode permanently and auto-pops TabTip on any text-field focus even with a Type
    ///     Cover attached — so when a PHYSICAL keyboard is present (SetupAPI present-keyboard-class count
    ///     ≥ 2 or a "… Cover …" device; the single phantom "HID Keyboard Device" is always there), the
    ///     keyboard rect is NOT published. Cached ~1 s.
    ///   • Debounce (load-bearing, not polish): a state CHANGE must be confirmed by the NEXT tick before
    ///     it publishes (the pending tick runs at the 120 ms armed cadence ⇒ ~120–150 ms confirm). Touch
    ///     keyboards flicker on focus changes; an undebounced feed is a resize storm — every RDP resize
    ///     renegotiates Dynamic resolution and every terminal resize sends a PTY window-change.
    ///
    /// Crash-surface rule: the whole tick body is caught — detection is best-effort and must NEVER break
    /// the app. The timer is a WinForms Timer, so events already arrive on the UI thread; consumers still
    /// guard IsDisposed (the B11 lesson).
    /// </summary>
    public static class KeyboardInset
    {
        private const int IdleTickMs = 350;    // TelegArm's idle cadence
        private const int ArmedTickMs = 120;   // TelegArm's armed cadence — also the debounce confirm window

        /// <summary>The published keyboard rect in SCREEN coordinates; Rectangle.Empty when hidden.
        /// Raised on the UI thread, debounced. Consumers intersect with their own client area.</summary>
        public static event Action<Rectangle> KeyboardRectChanged;

        public static Rectangle CurrentRect { get; private set; }

        private static Timer _timer;
        private static int _refs;              // registered consumers — timer runs only while > 0
        private static Rectangle _pending;     // candidate state awaiting its confirming tick
        private static bool _hasPending;

        /// <summary>The standard consumer computation: how much of <paramref name="f"/>'s CLIENT area the
        /// keyboard covers from the bottom (0 when hidden/minimized/no overlap). Screen-space intersection,
        /// so the same math serves windowed, maximized and fullscreen. Capped at 70% of the client so a
        /// usable strip always remains.</summary>
        public static int ComputeBottomInset(Form f, Rectangle kb)
        {
            try
            {
                if (kb.IsEmpty || f == null || f.IsDisposed || f.WindowState == FormWindowState.Minimized) return 0;
                Rectangle client = f.RectangleToScreen(f.ClientRectangle);
                if (!client.IntersectsWith(kb)) return 0;
                int inset = client.Bottom - Math.Max(kb.Top, client.Top);
                return Math.Max(0, Math.Min(inset, (int)(client.Height * 0.7)));
            }
            catch { return 0; }
        }

        /// <summary>A consumer window starts caring (call from OnShown; pair with <see cref="Unregister"/>).</summary>
        public static void Register()
        {
            _refs++;
            RefreshRunning();
        }

        public static void Unregister()
        {
            if (_refs > 0) _refs--;
            RefreshRunning();
        }

        /// <summary>Re-evaluate after the KeyboardAutoResize setting changes (Settings dialog calls this).
        /// Off ⇒ the timer stops entirely and a final empty rect restores every consumer.</summary>
        public static void RefreshRunning()
        {
            try
            {
                bool want = _refs > 0 && AppSettings.Instance.KeyboardAutoResize;
                if (want)
                {
                    if (_timer == null) { _timer = new Timer { Interval = IdleTickMs }; _timer.Tick += OnTick; }
                    if (!_timer.Enabled) _timer.Start();
                }
                else
                {
                    if (_timer != null && _timer.Enabled) _timer.Stop();
                    _hasPending = false;
                    if (!CurrentRect.IsEmpty) Publish(Rectangle.Empty);   // restore everyone
                }
            }
            catch { /* best-effort — never break the app over the keyboard */ }
        }

        private static void OnTick(object sender, EventArgs e)
        {
            try
            {
                Rectangle raw = ReadRawState();

                if (raw == CurrentRect) { _hasPending = false; }
                else if (_hasPending && raw == _pending) { _hasPending = false; Publish(raw); }   // confirmed by a 2nd tick
                else { _pending = raw; _hasPending = true; }                                       // candidate — confirm next tick

                // adaptive cadence: fast while the keyboard is up OR a change is pending confirmation
                int want = (!CurrentRect.IsEmpty || _hasPending) ? ArmedTickMs : IdleTickMs;
                if (_timer != null && _timer.Interval != want) _timer.Interval = want;
            }
            catch { /* never throw into the message loop */ }
        }

        private static Rectangle ReadRawState()
        {
#if DEBUG
            if (_injectActive) return _injectRect;
#endif
            Rectangle r = DetectTabTip();
            if (r.IsEmpty) return r;
            // FIN-KBD-FREEZE — diagnostic override (Settings, #if DEBUG UI): confirms/rules out candidate (b)
            // (HasHardwareKeyboard() suppressing a correctly-detected rect) in ONE device trip, no rebuild.
            bool ignoreHw = false;
            try { ignoreHw = AppSettings.Instance.KeyboardIgnoreHardwareCheck; } catch { }
            if (ignoreHw) { LogHwBypass(); return r; }
            if (HasHardwareKeyboard()) return Rectangle.Empty;   // Type Cover attached → don't inset
            return r;
        }

        private static bool _hwBypassLogged;
        private static void LogHwBypass()
        {
            if (_hwBypassLogged) return;
            _hwBypassLogged = true;
            FileLog.Line("[KBD] hwkbd check BYPASSED (KeyboardIgnoreHardwareCheck) — publishing the detected rect regardless");
        }

        private static void Publish(Rectangle r)
        {
            CurrentRect = r;
            FileLog.Line("[KBD] keyboard " + (r.IsEmpty ? "HIDE" : "SHOW rect=" + r.X + "," + r.Y + "," + r.Width + "x" + r.Height));
            var h = KeyboardRectChanged;
            if (h != null) { try { h(r); } catch { } }
        }

#if DEBUG
        // ── synthetic-rect injection — DEV-BOX TESTS ONLY, compiled out of Release (Batch-3 hygiene rule).
        //    Feeds the RAW state (upstream of the debounce) so tests exercise the real confirm machinery.
        private static bool _injectActive;
        private static Rectangle _injectRect;

        /// <summary>DEBUG ONLY: override detection with a synthetic keyboard rect (empty = hidden).</summary>
        public static void InjectTestRect(Rectangle rect) { _injectActive = true; _injectRect = rect; }

        /// <summary>DEBUG ONLY: return to real detection.</summary>
        public static void ClearInjection() { _injectActive = false; _injectRect = Rectangle.Empty; }
#endif

        // ── detection: the Win8.1/RT touch-keyboard window (TelegArm's via=TabTip branch) ──────────────

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string win);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect r);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int val, int size);
        private const int DWMWA_CLOAKED = 14;

        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }

        // FIN-KBD-FREEZE Part 2 — instrumented exactly like TelegArm's own [KBD] tick/signal-dump logging (the
        // pattern that let them debug this SAME class of problem on this SAME hardware): every branch says WHY
        // it returned Hidden or Show, not just a silent Rectangle.Empty. Change-gated (log only when the
        // diagnostic string differs from last time) so a healthy, unchanging state doesn't spam the log every
        // 120-350ms tick — matching TelegArm's own "log on CHANGE, not every tick" discipline.
        private static string _lastDetectDiag = "";

        private static Rectangle DetectTabTip()
        {
            string diag;
            Rectangle result = DetectTabTipCore(out diag);
            if (diag != _lastDetectDiag) { _lastDetectDiag = diag; FileLog.Line("[KBD] detect " + diag); }
            return result;
        }

        private static Rectangle DetectTabTipCore(out string diag)
        {
            try
            {
                IntPtr h = FindWindow("IPTip_Main_Window", null);   // the Win8.1/RT touch-keyboard window class
                if (h == IntPtr.Zero) { diag = "hwnd=NOT-FOUND -> HIDDEN"; return Rectangle.Empty; }
                string hwndStr = "hwnd=0x" + h.ToInt64().ToString("X");
                if (!IsWindowVisible(h)) { diag = hwndStr + " visible=False -> HIDDEN"; return Rectangle.Empty; }
                if (IsCloaked(h)) { diag = hwndStr + " visible=True cloaked=True -> HIDDEN (dismissed, TelegArm 0.2(d))"; return Rectangle.Empty; }
                NativeRect r;
                if (!GetWindowRect(h, out r)) { diag = hwndStr + " GetWindowRect=FAILED -> HIDDEN"; return Rectangle.Empty; }
                int height = r.Bottom - r.Top;
                string rectStr = "rect=" + r.Left + "," + r.Top + "," + (r.Right - r.Left) + "x" + height;
                if (height < 150) { diag = rectStr + " -> HIDDEN (height<150, too small to be the OSK)"; return Rectangle.Empty; }
                Rectangle screen;
                try { screen = Screen.FromPoint(new Point((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2)).Bounds; }
                catch { screen = Screen.PrimaryScreen.Bounds; }
                string screenStr = "screenBottom=" + screen.Bottom;
                if (r.Top >= screen.Bottom - 80) { diag = rectStr + " " + screenStr + " -> HIDDEN (top>=screenBottom-80: dismissed/off-bottom)"; return Rectangle.Empty; }
                if (r.Bottom < screen.Bottom - 80) { diag = rectStr + " " + screenStr + " -> HIDDEN (bottom<screenBottom-80: not bottom-docked, e.g. floating/compact layout)"; return Rectangle.Empty; }
                diag = rectStr + " " + screenStr + " -> SHOW";
                return new Rectangle(r.Left, r.Top, r.Right - r.Left, height);
            }
            catch (Exception ex) { diag = "EXCEPTION " + ex.GetType().Name + ": " + ex.Message + " -> HIDDEN"; return Rectangle.Empty; }
        }

        private static bool IsCloaked(IntPtr hwnd)
        {
            try { int c; return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out c, sizeof(int)) == 0 && c != 0; }
            catch { return false; }   // dwmapi/attribute unavailable → fall through to the rect checks
        }

        // ── hardware-keyboard presence (TelegArm's TouchKeyboard SetupAPI lesson, cached ~1 s) ─────────
        // The RT always enumerates one phantom "HID Keyboard Device"; a REAL keyboard is the SECOND
        // present keyboard-class device, or any whose name contains "Cover" (Type/Touch Cover).

        private static bool _hwCached;
        private static int _hwAt;

        private static bool HasHardwareKeyboard()
        {
            int now = Environment.TickCount;
            if (_hwAt != 0 && (uint)(now - _hwAt) < 1000) return _hwCached;
            _hwAt = now;
            try { _hwCached = QueryHardwareKeyboard(); } catch { _hwCached = false; }
            return _hwCached;
        }

        private static readonly Guid GUID_DEVCLASS_KEYBOARD =
            new Guid(0x4d36e96b, 0xe325, 0x11ce, 0xbf, 0xc1, 0x08, 0x00, 0x2b, 0xe1, 0x03, 0x18);
        private const int DIGCF_PRESENT = 0x02;
        private const int SPDRP_DEVICEDESC = 0x00;
        private const int SPDRP_FRIENDLYNAME = 0x0C;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, string Enumerator, IntPtr hwndParent, int Flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data,
            int prop, out int type, byte[] buf, int bufSize, out int required);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

        // FIN-KBD-FREEZE Part 2 — same change-gated [KBD] discipline as DetectTabTip: log the full enumerated
        // device list + verdict WHENEVER a real query runs (the ~1s cache means this is at most once/second),
        // but only WRITE the line if the diagnostic actually differs from last time (a steady "count=1, no
        // cover" on every cache refresh must not spam the log).
        private static string _lastHwDiag = "";

        private static bool QueryHardwareKeyboard()
        {
            Guid kbClass = GUID_DEVCLASS_KEYBOARD;   // local copy — can't pass a static readonly by ref
            IntPtr set = SetupDiGetClassDevs(ref kbClass, null, IntPtr.Zero, DIGCF_PRESENT);
            if (set == IntPtr.Zero || set == INVALID_HANDLE_VALUE)
            {
                LogHw("setupapi=UNAVAILABLE -> not suppressing (fails toward showing the OSK inset)");
                return false;   // SetupAPI unavailable → don't suppress
            }
            try
            {
                int count = 0; bool cover = false;
                var names = new System.Text.StringBuilder();
                var did = new SP_DEVINFO_DATA();
                did.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref did); i++)
                {
                    count++;
                    string name = GetDeviceProp(set, ref did, SPDRP_FRIENDLYNAME) ?? GetDeviceProp(set, ref did, SPDRP_DEVICEDESC) ?? "(unnamed)";
                    if (names.Length > 0) names.Append("; ");
                    names.Append(name);
                    if (name.IndexOf("Cover", StringComparison.OrdinalIgnoreCase) >= 0) cover = true;
                }
                bool suppress = cover || count >= 2;
                LogHw("count=" + count + " cover=" + cover + " devices=[" + names + "] -> "
                    + (suppress ? "SUPPRESSING (treated as a real hardware keyboard)" : "not suppressing"));
                return suppress;
            }
            catch (Exception ex) { LogHw("EXCEPTION " + ex.GetType().Name + ": " + ex.Message + " -> not suppressing"); return false; }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        private static void LogHw(string diag)
        {
            if (diag == _lastHwDiag) return;
            _lastHwDiag = diag;
            FileLog.Line("[KBD] hwkbd " + diag);
        }

        private static string GetDeviceProp(IntPtr set, ref SP_DEVINFO_DATA did, int prop)
        {
            int type, req;
            SetupDiGetDeviceRegistryProperty(set, ref did, prop, out type, null, 0, out req);
            if (req <= 0) return null;
            var buf = new byte[req];
            if (!SetupDiGetDeviceRegistryProperty(set, ref did, prop, out type, buf, buf.Length, out req)) return null;
            return System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0');
        }
    }
}
