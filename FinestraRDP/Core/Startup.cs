using System;
using System.Windows.Forms;
using Microsoft.Win32;
using Finestra.Helpers;

namespace Finestra.Core
{
    /// <summary>What the MAIN window's ✕ (or Alt-F4) does. Persisted as a string in <see cref="AppSettings"/> so
    /// a "remembered" choice is never a one-way trap — the same value is editable in Settings.</summary>
    public enum CloseAction { Ask, MinimizeToTray, Exit }

    /// <summary>
    /// Opt-in "run at Windows startup" via the per-user Run key (no admin, no scheduled task). ON writes the exe
    /// path plus the <see cref="MinimizedArg"/> flag so the app starts MINIMIZED TO TRAY (not a visible window,
    /// and it does NOT auto-connect anything). OFF removes the value. HKCU\...\Run is honored on Windows 10/11;
    /// on RT 8.1 it is the same "verify on the device" class as the tray itself — NOT claimed from an x64 run.
    /// </summary>
    public static class Startup
    {
        public const string MinimizedArg = "/tray";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Finestra";
        private const string LegacyValueName = "FinestraRDP";   // pre-1.0 Run value — cleaned up on every Apply

        /// <summary>Was this process launched with the start-minimized flag (from the Run key)?</summary>
        public static bool LaunchedMinimized()
        {
            try
            {
                foreach (var a in Environment.GetCommandLineArgs())
                    if (string.Equals(a, MinimizedArg, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            catch { }
            return false;
        }

        /// <summary>Write (on) or remove (off) the Run value so it matches the setting. Idempotent; never throws.</summary>
        public static void Apply(bool enabled)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
                {
                    if (key == null) return;
                    // 1.0 rename migration: the pre-1.0 value pointed at FinestraRDP.exe (which no longer ships) —
                    // remove it unconditionally; the setting below re-creates the entry under the new name/path.
                    if (key.GetValue(LegacyValueName) != null)
                        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
                    if (enabled)
                        key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\" " + MinimizedArg);
                    else if (key.GetValue(ValueName) != null)
                        key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
                FileLog.Line("[STARTUP] run-on-startup " + (enabled ? "ENABLED" : "disabled"));
            }
            catch (Exception ex) { FileLog.Line("[STARTUP] apply failed: " + ex.Message); }
        }

        /// <summary>True if the Run value currently exists.</summary>
        public static bool IsEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key?.GetValue(ValueName) != null;
            }
            catch { return false; }
        }

        public static CloseAction ParseCloseAction(string s)
        {
            CloseAction a;
            return Enum.TryParse(s, true, out a) ? a : CloseAction.Ask;
        }
    }
}
