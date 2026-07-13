using System;
using System.IO;
using Newtonsoft.Json;

namespace Finestra.Core
{
    /// <summary>
    /// App-level settings (NOT RDP flags) â€” theme mode + DPI awareness + the wfreerdp.exe path.
    /// Load/Save is FILE-ONLY (Documents\Finestra\settings.json): Program.Main reads DpiUnaware/ThemeMode
    /// here BEFORE any window or Screen metric is touched, per the DPI invariant.
    /// </summary>
    public sealed class AppSettings
    {
        public string ThemeMode { get; set; } = "System";      // System | Light | Dark
        public bool   DpiUnaware { get; set; } = false;         // opt-in "proportional scaling" rescue
        public string WfreerdpPath { get; set; } = "";          // path to wfreerdp.exe (auto-detected if empty)
        public string ConnectMode { get; set; } = "Embed";      // Embed = chromeless fullscreen host (tabs/overlay); Window = shell-out (UI-1B)
        public string CloseAction { get; set; } = "Ask";        // Ask | MinimizeToTray | Exit — what the ✕ does (resettable in Settings)
        public bool   RunOnStartup { get; set; } = false;       // opt-in HKCU\...\Run key (starts minimized to tray)

        /// <summary>The "defaults for new servers" profile — a new Connection inherits this (resolution + the
        /// rest). Editing it in Settings does NOT affect existing servers (they carry their own SettingsProfile).</summary>
        public SettingsProfile Defaults { get; set; } = new SettingsProfile();

        /// <summary>FRDP-POLISH-2 — terminal appearance defaults NEW SSH connections copy (colors / font / scrollback).
        /// Editing this does NOT touch existing connections (each carries its own <see cref="TerminalPrefs"/>).</summary>
        public TerminalPrefs TerminalDefaults { get; set; } = new TerminalPrefs();

        private static AppSettings _instance;
        public static AppSettings Instance { get { return _instance ?? (_instance = Load()); } }

        private static AppSettings Load()
        {
            try
            {
                var path = StoragePaths.SettingsFile;
                if (File.Exists(path))
                {
                    var s = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
                    // FRDP-FIXSWEEP B18 — Json.NET overwrites the C# field initializer when the JSON has an explicit
                    // null, so re-establish fresh defaults or consumers (the editor) NRE on Defaults/TerminalDefaults.
                    if (s.Defaults == null) s.Defaults = new SettingsProfile();
                    if (s.TerminalDefaults == null) s.TerminalDefaults = new TerminalPrefs();
                    // the new-server Defaults profile carries the same legacy bools as a connection does
                    s.Defaults.MigrateOversize();
                    return s;
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SETTINGS] load failed: " + ex.Message); }
            return new AppSettings();
        }

        public void Save()
        {
            // FRDP-FIXSWEEP B15 — atomic temp-then-replace (like connections.json / known_hosts.json), so a crash or
            // power-loss mid-write can't corrupt settings.json.
            try
            {
                string path = StoragePaths.SettingsFile;
                string json = JsonConvert.SerializeObject(this, Formatting.Indented);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[SETTINGS] save failed: " + ex.Message); }
        }
    }
}
