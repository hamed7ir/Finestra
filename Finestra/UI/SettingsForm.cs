using System;
using System.Windows.Forms;
using Finestra.Core;
using Finestra.UI.Controls;

namespace Finestra.UI
{
    /// <summary>
    /// Per-connection RDP settings (Part C). Every control maps 1:1 to a real wfreerdp flag (see
    /// port\wfreerdp-help.txt); there are NO curated defaults — toggles start at wfreerdp's own default and
    /// value-choices default to "Default" (emit nothing). The only smart default is the display resolution
    /// (detected primary), applied at launch by <see cref="RdpLauncher"/>. Edits a CLONE and returns it via
    /// <see cref="Result"/> only on OK, so Cancel discards.
    /// </summary>
    public sealed class SettingsForm : ThemedDialog
    {
        // option labels — SAME ORDER as the enums, so index == (int)enumValue
        private static readonly string[] ColorDepthOptions = { "Default", "32-bit", "24-bit", "16-bit", "15-bit", "8-bit" };
        private static readonly string[] NetworkOptions = { "Default", "Modem", "Broadband (low)", "Broadband", "Broadband (high)", "WAN", "LAN", "Auto" };
        private static readonly string[] AudioOptions = { "Default", "Play on this PC", "Play on server", "No audio" };
        private static readonly string[] SecurityOptions = { "Default (negotiate)", "Force NLA", "NLA off", "TLS only", "RDP (legacy)" };
        private static readonly string[] CompressionLevelOptions = { "Default", "0", "1", "2" };
        private static readonly string[] GfxOptions = { "Default", "H.264 (AVC444)", "H.264 (AVC420)", "RemoteFX", "Progressive" };

        public SettingsProfile Result { get; private set; }
        private readonly SettingsProfile _s;

        public SettingsForm(SettingsProfile src) : base("Connection settings", 520, 660)
        {
            _s = src != null ? src.Clone() : new SettingsProfile();
            BuildRows();
            var ok = AddFooterButton("OK", RoundedButtonKind.Primary, DialogResult.None);
            ok.Click += (s, e) => { Result = _s; DialogResult = DialogResult.OK; };
            AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.Cancel);
        }

        private void BuildRows()
        {
            // ── Display ──
            // Resolution now lives in the ResolutionPicker on the editor / Defaults — not here.
            // Drives the HOST presentation when embedding: on = borderless fullscreen + hover overlay;
            // off = resizable window with a persistent tab/title bar. (In shell-out mode it is wfreerdp's +f.)
            var fullscreen = Toggle("Fullscreen session (off = resizable window)", _s.Fullscreen, v => _s.Fullscreen = v);
            var depth = Choice("Color depth", ColorDepthOptions, (int)_s.ColorDepth, i => _s.ColorDepth = (ColorDepthOpt)i);
            var multimon = Toggle("Use multiple monitors", _s.MultiMon, v => _s.MultiMon = v);
            var span = Toggle("Span across monitors", _s.Span, v => _s.Span = v);
            // Smart-sizing / dynamic-resolution are no longer raw bools here: they are the two opt-in choices of
            // SettingsProfile.OversizeMode, edited next to the resolution picker (which is what they act on).

            // ── Experience ── (toggles start at wfreerdp defaults: wallpaper/themes/fonts/compression ON)
            var wallpaper = Toggle("Desktop wallpaper", _s.Wallpaper, v => _s.Wallpaper = v);
            var themes = Toggle("Visual themes", _s.Themes, v => _s.Themes = v);
            var fonts = Toggle("Font smoothing (ClearType)", _s.FontSmoothing, v => _s.FontSmoothing = v);
            var menuAnims = Toggle("Menu animations", _s.MenuAnimations, v => _s.MenuAnimations = v);
            var winDrag = Toggle("Full window drag", _s.WindowDrag, v => _s.WindowDrag = v);
            var aero = Toggle("Desktop composition (Aero)", _s.Aero, v => _s.Aero = v);
            var compression = Toggle("Bitmap compression", _s.Compression, v => _s.Compression = v);
            var compLevel = Choice("Compression level", CompressionLevelOptions, (int)_s.CompressionLevel, i => _s.CompressionLevel = (CompressionLevelOpt)i);
            var network = Choice("Network type", NetworkOptions, (int)_s.Network, i => _s.Network = (NetworkOpt)i);
            var gfx = Choice("Graphics pipeline", GfxOptions, (int)_s.Gfx, i => _s.Gfx = (GfxOpt)i);

            // ── Connection ──
            var timeout = Num("Timeout (ms, 0 = default)", _s.TimeoutMs, v => _s.TimeoutMs = v);
            var reconnect = Toggle("Auto-reconnect", _s.AutoReconnect, v => _s.AutoReconnect = v);
            var trustCert = Toggle("Trust server certificate", _s.TrustCertificate, v => _s.TrustCertificate = v);
            var gateway = TextField("RD Gateway host (optional)", _s.GatewayHost, v => _s.GatewayHost = v);

            // ── Local resources ──
            var audio = Choice("Audio", AudioOptions, (int)_s.Audio, i => _s.Audio = (AudioOpt)i);
            var clipboard = Toggle("Clipboard", _s.Clipboard, v => _s.Clipboard = v);
            var drives = Toggle("Redirect drives", _s.Drives, v => _s.Drives = v);
            var printer = Toggle("Redirect default printer", _s.Printer, v => _s.Printer = v);

            // ── Security ──
            var security = Choice("Security", SecurityOptions, (int)_s.Security, i => _s.Security = (SecurityOpt)i);

            PopulateBody(
                new SectionHeader("Display"), fullscreen, depth, multimon, span,
                new SectionHeader("Experience"), wallpaper, themes, fonts, menuAnims, winDrag, aero, compression, compLevel, network, gfx,
                new SectionHeader("Connection"), timeout, reconnect, trustCert, gateway,
                new SectionHeader("Local resources"), audio, clipboard, drives, printer,
                new SectionHeader("Security"), security);
        }

        // ── row factories ──
        private static ToggleRow Toggle(string label, bool val, Action<bool> set)
        {
            var r = new ToggleRow(label, val);
            r.Changed += () => set(r.On);
            return r;
        }
        private static ChoiceRow Choice(string label, string[] opts, int idx, Action<int> set)
        {
            var r = new ChoiceRow(label, opts, idx);
            r.Changed += () => set(r.SelectedIndex);
            return r;
        }
        private static TextRow TextField(string label, string val, Action<string> set)
        {
            var r = new TextRow(label, val);
            r.Changed += () => set(r.Value);
            return r;
        }
        private static TextRow Num(string label, int val, Action<int> set)
        {
            var r = new TextRow(label, val > 0 ? val.ToString() : "", numeric: true);
            r.Changed += () => { int v; set(int.TryParse((r.Value ?? "").Trim(), out v) && v > 0 ? v : 0); };
            return r;
        }
    }
}
