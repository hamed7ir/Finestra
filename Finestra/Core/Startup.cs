using System;
using System.IO;
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
                    // ⚠ DELETE ONLY WHAT THIS EXE OWNS. There is ONE Run value name but there can be several
                    // copies of Finestra, and since portable copies keep their own settings.json they no longer
                    // agree about this setting. A freshly extracted portable copy has no settings file at all, so
                    // RunOnStartup defaults to false and Program.cs calls Apply(false) on its first launch — which,
                    // deleting blind, would silently disarm the INSTALLED copy's run-at-startup entry. Compare the
                    // stored command line against our own path and leave anyone else's alone: a stale entry is
                    // recoverable, another install's deleted entry is not.
                    else if (OwnedByThisExe(key.GetValue(ValueName) as string))
                        key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
                FileLog.Line("[STARTUP] run-on-startup " + (enabled ? "ENABLED" : "disabled"));
            }
            catch (Exception ex) { FileLog.Line("[STARTUP] apply failed: " + ex.Message); }
        }

        /// <summary>True when the stored Run command line launches THIS executable. The value is written quoted
        /// ("C:\...\Finestra.exe" /tray), so strip quotes and compare the leading path rather than parsing a
        /// command line. Anything unreadable, empty or different counts as NOT ours — the safe answer, because
        /// it leaves the value alone.</summary>
        private static bool OwnedByThisExe(string runValue)
        {
            if (string.IsNullOrEmpty(runValue)) return false;
            try
            {
                string cmd = runValue.Trim();
                if (cmd.StartsWith("\""))
                {
                    int end = cmd.IndexOf('"', 1);
                    cmd = end > 0 ? cmd.Substring(1, end - 1) : cmd.Trim('"');
                }
                else
                {
                    int sp = cmd.IndexOf(' ');
                    if (sp > 0) cmd = cmd.Substring(0, sp);
                }
                return string.Equals(Path.GetFullPath(cmd), Path.GetFullPath(Application.ExecutablePath),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
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
