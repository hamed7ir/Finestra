using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Finestra.Core;
using Finestra.Helpers;
using Finestra.UI.Controls;

namespace Finestra.UI
{
    /// <summary>
    /// App-level settings (NOT per-connection): the wfreerdp.exe path and the DPI-awareness rescue toggle.
    /// Theme mode lives in the hamburger menu. Persists to <see cref="AppSettings"/> (Documents\Finestra\
    /// settings.json). The DPI toggle takes effect on next launch (awareness is declared once at startup).
    /// </summary>
    public sealed class AppSettingsForm : ThemedDialog
    {
        public bool WfreerdpChanged { get; private set; }

        private readonly TextRow _path;
        private readonly ToggleRow _dpi;
        private readonly ChoiceRow _connectMode;
        private readonly ChoiceRow _oversize;
        private readonly ChoiceRow _closeAction;
        private readonly ToggleRow _startup;
        private readonly ResolutionPicker _resPicker;
        private readonly SettingsProfile _defaults;
        private readonly ToggleRow _termColors;         // FRDP-POLISH-2 — global terminal defaults for new SSH connections
        private readonly TextRow _termFont, _termScrollback;
        private readonly string _origPath;

        // SAME ORDER as the CloseAction enum → index == (int)value.
        private static readonly string[] CloseActionOptions = { "Ask each time", "Minimize to tray", "Exit" };

        public AppSettingsForm() : base("Settings", 540, 640)
        {
            var s = AppSettings.Instance;
            _origPath = s.WfreerdpPath ?? "";
            _defaults = s.Defaults != null ? s.Defaults.Clone() : new SettingsProfile();   // edited via the picker, committed on Save

            string resolved = _origPath;
            if (string.IsNullOrWhiteSpace(resolved))
            {
                string auto = RdpLauncher.ResolveWfreerdpPath();   // show what auto-detect would use
                resolved = auto != null ? auto + "   (auto-detected)" : "";
            }

            _path = new TextRow("Path to wfreerdp.exe", _origPath);
            var browse = new RoundedButton { Text = "Browse…", Kind = RoundedButtonKind.Neutral, Height = 40, Font = FontHelper.Ui(10f, FontStyle.Bold) };
            browse.Click += (a, b) =>
            {
                using (var ofd = new OpenFileDialog { Filter = "wfreerdp.exe|wfreerdp.exe|Executables|*.exe|All files|*.*", Title = "Locate wfreerdp.exe" })
                {
                    try { if (!string.IsNullOrEmpty(_path.Value) && File.Exists(_path.Value)) ofd.InitialDirectory = Path.GetDirectoryName(_path.Value); } catch { }
                    if (ofd.ShowDialog(this) == DialogResult.OK) _path.Value = ofd.FileName;
                }
            };

            var autodetect = new TextRow("Currently resolves to", string.IsNullOrEmpty(resolved) ? "(not found — set a path)" : resolved) { Enabled = false };

            _dpi = new ToggleRow("Proportional scaling — DPI-unaware (restart required)", AppSettings.Instance.DpiUnaware);
            _connectMode = new ChoiceRow("Connect mode",
                new[] { "Embed (tabbed) — Finestra hosts the session", "Window (single, shell-out)" },
                string.Equals(s.ConnectMode, "Window", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
            var connectHint = new TextRow("What each mode does",
                "Embed: tabs + live stats; each connection's \"Fullscreen\" setting picks borderless fullscreen (hover bar) or a resizable window (persistent tab bar).   Window: wfreerdp opens its own plain window — no tabs, no stats.")
                { Enabled = false };

            _resPicker = new ResolutionPicker(_defaults);
            _oversize = new ChoiceRow(OversizeModeUi.Label, OversizeModeUi.Options, (int)_defaults.OversizeMode) { ValueWidth = 240 };
            _oversize.Changed += () => _defaults.OversizeMode = (OversizeMode)_oversize.SelectedIndex;

            // App behaviour: the ✕ action (this is what makes a "remembered" close choice resettable — no trap) and
            // run-at-startup. The folder button opens the Documents\Finestra data dir in Explorer.
            _closeAction = new ChoiceRow("When I close the window", CloseActionOptions, (int)Startup.ParseCloseAction(s.CloseAction)) { ValueWidth = 200 };
            _startup = new ToggleRow("Run at Windows startup (starts minimized to tray)", s.RunOnStartup);
            var openFolder = new RoundedButton { Text = "Open settings folder", Kind = RoundedButtonKind.Neutral, Height = 40, Font = FontHelper.Ui(10f, FontStyle.Bold) };
            openFolder.Click += (a, b) => { try { Process.Start("explorer.exe", StoragePaths.AppDataDir); } catch { } };

            // SSH host-key trust store (TOFU) — view / remove remembered keys so a rekeyed server is recoverable.
            var knownHosts = new RoundedButton { Text = "Manage known hosts…", Kind = RoundedButtonKind.Neutral, Height = 40, Font = FontHelper.Ui(10f, FontStyle.Bold) };
            knownHosts.Click += (a, b) => { using (var f = new KnownHostsForm()) f.ShowDialog(this); };

            // Terminal appearance defaults — copied into each NEW SSH connection; editing here doesn't touch existing.
            var td = s.TerminalDefaults ?? new TerminalPrefs();
            _termColors = new ToggleRow("Terminal colours", td.Colors);
            _termFont = new TextRow("Terminal font size (" + TerminalPrefs.MinFont + "–" + TerminalPrefs.MaxFont + ")", td.FontSize.ToString(), numeric: true);
            _termScrollback = new TextRow("Scrollback lines", td.ScrollbackLines.ToString(), numeric: true);

            PopulateBody(
                new SectionHeader("wfreerdp"), _path, browse, autodetect,
                new SectionHeader("Session"), _connectMode, connectHint,
                new SectionHeader("Default resolution (new servers)"), _resPicker, _oversize,
                new SectionHeader("App behaviour"), _closeAction, _startup, openFolder,
                new SectionHeader("SSH security"), knownHosts,
                new SectionHeader("Terminal defaults (new SSH connections)"), _termColors, _termFont, _termScrollback,
                new SectionHeader("Display"), _dpi);

            var save = AddFooterButton("Save", RoundedButtonKind.Primary, DialogResult.None);
            save.Click += (a, b) => OnSave();
            AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.Cancel);
        }

        private void OnSave()
        {
            var s = AppSettings.Instance;
            string newPath = (_path.Value ?? "").Trim();
            if (newPath.Length > 0 && !File.Exists(newPath))
            {
                MessageBox.Show(this, "That file does not exist.", "Finestra", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            s.WfreerdpPath = newPath;
            s.DpiUnaware = _dpi.On;
            s.ConnectMode = _connectMode.SelectedIndex == 1 ? "Window" : "Embed";
            s.CloseAction = ((CloseAction)_closeAction.SelectedIndex).ToString();
            s.RunOnStartup = _startup.On;
            s.Defaults = _defaults;   // the picker mutated this clone → commit the new-server resolution default
            s.TerminalDefaults = new TerminalPrefs
            {
                Colors = _termColors.On,
                FontSize = ParseInt(_termFont.Value, 11),
                ScrollbackLines = ParseInt(_termScrollback.Value, 5000)
            };
            s.Save();
            Startup.Apply(s.RunOnStartup);   // write/remove the HKCU Run key to match the setting
            WfreerdpChanged = !string.Equals(newPath, _origPath, StringComparison.OrdinalIgnoreCase);
            DialogResult = DialogResult.OK;
        }

        private static int ParseInt(string s, int def) { int v; return int.TryParse((s ?? "").Trim(), out v) ? v : def; }
    }
}
