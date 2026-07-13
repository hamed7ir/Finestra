using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Finestra.Core;
using Finestra.Helpers;
using Finestra.UI;

namespace Finestra
{
    internal static class Program
    {
        /// <summary>Physical system DPI captured at startup (96 = 100%). Read by the settings "DPI rescue"
        /// affordance. Stays 96 until the [DPI] probe runs (and reads 96 when already unaware).</summary>
        public static int SystemDpi { get; private set; } = 96;

        // PROCESS_SYSTEM_DPI_AWARE = 1. MaterialSkin.2 has no per-monitor support â†’ deliberately system-aware.
        [DllImport("shcore.dll")] private static extern int SetProcessDpiAwareness(int value);
        [DllImport("shcore.dll")] private static extern int GetProcessDpiAwareness(IntPtr hProcess, out int value);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")]  private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);
        private const int LOGPIXELSX = 88;

        [STAThread]
        static void Main()
        {
            // Surface otherwise-silent crashes (record + show). ThreadException is single-slot; UnhandledException multicasts.
            Application.ThreadException += (s, e) =>
            {
                try { Trace.WriteLine("[CRASH] UI: " + e.Exception); } catch { }
                MessageBox.Show(e.Exception.ToString(), "Finestra â€” unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try { Trace.WriteLine("[CRASH] fatal: " + (e.ExceptionObject as Exception)); } catch { }
                MessageBox.Show((e.ExceptionObject as Exception)?.ToString() ?? "Unknown error",
                    "Finestra â€” fatal error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            // File logging first, so the earliest [PATHS]/[DPI]/[ACCENT] traces are captured on RT (no DebugView there).
            FileLog.Init();

            // FRDP-FIXSWEEP B1 — kill-on-close job created at startup; every wfreerdp child is assigned to it at launch,
            // so no orphaned engine process survives the app (normal exit OR taskkill).
            JobGuard.Init();

            // DPI: declared at runtime (no manifest). Settings read is FILE-ONLY (AppSettings/StoragePaths touch files,
            // never Screen/UI) â€” INVARIANT: nothing before this call may create UI or read Screen metrics.
            var settings = AppSettings.Instance;
            try { SetProcessDpiAwareness(settings.DpiUnaware ? 0 : 1); } catch { /* shcore unavailable */ }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Apply the persisted theme mode (non-notifying) before any form reads the theme.
            ThemeMode mode;
            if (!Enum.TryParse(settings.ThemeMode, true, out mode)) mode = ThemeMode.System;
            ThemeHelper.InitMode(mode);

            // [DPI] truth line (permanent): effective awareness + system DPI â€” a manifest/compat-flag/library call can
            // silently override the declaration above, and that class of regression must be diagnosable from the log alone.
            try
            {
                int aw;
                if (GetProcessDpiAwareness(IntPtr.Zero, out aw) == 0)
                {
                    int dpi = 96;
                    IntPtr dc = GetDC(IntPtr.Zero);
                    if (dc != IntPtr.Zero) { dpi = GetDeviceCaps(dc, LOGPIXELSX); ReleaseDC(IntPtr.Zero, dc); }
                    SystemDpi = dpi;
                    Debug.WriteLine("[DPI] awareness=" + (aw == 0 ? "unaware" : aw == 1 ? "system" : "per-monitor")
                        + " systemDpi=" + dpi);
                }
            }
            catch { /* shcore unavailable (RT 8.1 has it) */ }

            DisplayInfo.LogOnce();   // [DISPLAY] physical primary — read after DpiAwareness; RT/scaled correctness is device-pass

            // Keep the HKCU Run key in sync with the setting every launch (self-heals a stale key), then honor the
            // start-minimized flag the Run key passes (starts hidden to the tray; auto-connects nothing).
            Startup.Apply(settings.RunOnStartup);
            bool startHidden = Startup.LaunchedMinimized();

            Application.Run(new MainForm(startHidden));
        }
    }
}
