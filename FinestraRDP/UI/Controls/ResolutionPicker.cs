using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Finestra.Core;
using Finestra.Helpers;

namespace Finestra.UI.Controls
{
    /// <summary>
    /// FRDP-UI-RES — the ONE reusable resolution picker, hosted identically in Add/Edit-server and global-Defaults.
    /// Three-way mode {Native / Preset / Custom} (exactly one active; the others gray out — single source of truth):
    ///  • Native  = a MODE, not a number — resolves the device's PHYSICAL resolution live at connect (shown here for
    ///              THIS machine as a hint).
    ///  • Preset  = aspect-ratio TABS (4:3 · 5:4 · 16:9 · 16:10 · 21:9), each listing that ratio's common resolutions.
    ///  • Custom  = numeric W×H.
    ///  • Portrait swaps W↔H (Preset/Custom); the effective value is shown. Note: in landscape fullscreen-embed the
    ///    session fills the physical screen, so portrait may letterbox / be overridden.
    /// Reads/writes a bound <see cref="SettingsProfile"/> live. Built from the existing themed stack (RoundedButton +
    /// owner-paint) — no new UI library.
    /// </summary>
    public sealed class ResolutionPicker : Control
    {
        private static readonly string[] AspectNames = { "4:3", "5:4", "16:9", "16:10", "21:9" };
        private static readonly Size[][] AspectRes =
        {
            new[] { new Size(1024,768),  new Size(1280,960),  new Size(1400,1050), new Size(1600,1200) },
            new[] { new Size(1280,1024) },
            new[] { new Size(1280,720),  new Size(1366,768),  new Size(1600,900),  new Size(1920,1080), new Size(2560,1440) },
            new[] { new Size(1280,800),  new Size(1440,900),  new Size(1680,1050), new Size(1920,1200) },
            new[] { new Size(2560,1080), new Size(3440,1440) },
        };

        public event Action Changed;

        private SettingsProfile _s;
        private readonly RoundedButton _mNative, _mPreset, _mCustom;
        private readonly RoundedButton[] _tabs = new RoundedButton[5];
        private readonly FlowLayoutPanel _chipFlow;
        private readonly List<RoundedButton> _chips = new List<RoundedButton>();
        private readonly Panel _wWrap, _hWrap;
        private readonly TextBox _wBox, _hBox;
        private int _activeAspect = 2;   // 16:9 default
        private bool _loading;
        private Rectangle _portraitPill;
        private readonly Size _native;

        public ResolutionPicker(SettingsProfile s)
        {
            _s = s ?? new SettingsProfile();
            _native = DisplayInfo.PhysicalPrimary();
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 300;

            _mNative = ModeButton("Native", () => SetMode(ResolutionMode.Native));
            _mPreset = ModeButton("Preset", () => SetMode(ResolutionMode.Preset));
            _mCustom = ModeButton("Custom", () => SetMode(ResolutionMode.Custom));
            Controls.Add(_mNative); Controls.Add(_mPreset); Controls.Add(_mCustom);

            for (int i = 0; i < 5; i++)
            {
                int idx = i;
                _tabs[i] = new RoundedButton { Text = AspectNames[i], Kind = RoundedButtonKind.Neutral, Height = 28, Radius = 6, Font = FontHelper.Ui(9f, FontStyle.Bold) };
                _tabs[i].Click += (a, b) => SelectAspect(idx);
                Controls.Add(_tabs[i]);
            }

            _chipFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoScroll = false };
            Controls.Add(_chipFlow);

            _wBox = NumBox(); _hBox = NumBox();
            _wWrap = BoxWrap(_wBox); _hWrap = BoxWrap(_hBox);
            Controls.Add(_wWrap); Controls.Add(_hWrap);
            _wBox.TextChanged += (a, b) => { if (_loading) return; _s.Width = ParseInt(_wBox.Text); Invalidate(); Changed?.Invoke(); };
            _hBox.TextChanged += (a, b) => { if (_loading) return; _s.Height = ParseInt(_hBox.Text); Invalidate(); Changed?.Invoke(); };

            ThemeHelper.ThemeChanged += OnTheme;
            LoadFromProfile();
        }

        private RoundedButton ModeButton(string text, Action onClick)
        {
            var b = new RoundedButton { Text = text, Kind = RoundedButtonKind.Neutral, Height = 32, Font = FontHelper.Ui(10f, FontStyle.Bold) };
            b.Click += (s, e) => onClick();
            return b;
        }
        private static TextBox NumBox()
        {
            var t = new TextBox { BorderStyle = BorderStyle.None, Font = FontHelper.Ui(10.5f), Dock = DockStyle.Fill };
            t.KeyPress += (s, e) => { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
            return t;
        }
        private static Panel BoxWrap(TextBox box) { var p = new Panel { Padding = new Padding(6, 5, 6, 5) }; p.Controls.Add(box); return p; }
        private static int ParseInt(string s) { int v; return int.TryParse((s ?? "").Trim(), out v) && v > 0 ? v : 0; }

        /// <summary>Re-point the picker at a (possibly replaced) profile — e.g. after the editor's Advanced dialog
        /// returns a cloned SettingsProfile — so subsequent edits reach the live object.</summary>
        public void Rebind(SettingsProfile s) { if (s != null) _s = s; LoadFromProfile(); }

        // ── load current profile state ──
        private void LoadFromProfile()
        {
            _loading = true;
            if (_s.ResolutionMode == ResolutionMode.Preset)
            {
                int a = FindAspect(_s.Width, _s.Height);
                if (a >= 0) _activeAspect = a;
            }
            _wBox.Text = _s.Width > 0 ? _s.Width.ToString() : "";
            _hBox.Text = _s.Height > 0 ? _s.Height.ToString() : "";
            _loading = false;
            PopulateChips();
            UpdateModeButtons();
            UpdateTabs();
            UpdateEnabled();
            StyleBoxes();
        }

        private static int FindAspect(int w, int h)
        {
            for (int i = 0; i < AspectRes.Length; i++)
                foreach (var r in AspectRes[i]) if (r.Width == w && r.Height == h) return i;
            return -1;
        }

        private void SetMode(ResolutionMode m)
        {
            _s.ResolutionMode = m;
            UpdateModeButtons();
            UpdateEnabled();
            Invalidate();
            Changed?.Invoke();
        }
        private void UpdateModeButtons()
        {
            _mNative.Kind = _s.ResolutionMode == ResolutionMode.Native ? RoundedButtonKind.Primary : RoundedButtonKind.Neutral;
            _mPreset.Kind = _s.ResolutionMode == ResolutionMode.Preset ? RoundedButtonKind.Primary : RoundedButtonKind.Neutral;
            _mCustom.Kind = _s.ResolutionMode == ResolutionMode.Custom ? RoundedButtonKind.Primary : RoundedButtonKind.Neutral;
        }
        private void UpdateTabs()
        {
            for (int i = 0; i < 5; i++) _tabs[i].Kind = i == _activeAspect ? RoundedButtonKind.Primary : RoundedButtonKind.Secondary;
        }
        private void UpdateEnabled()
        {
            bool preset = _s.ResolutionMode == ResolutionMode.Preset;
            bool custom = _s.ResolutionMode == ResolutionMode.Custom;
            foreach (var t in _tabs) t.Enabled = preset;
            foreach (var c in _chips) c.Enabled = preset;
            _wBox.Enabled = _hBox.Enabled = custom;
            _wWrap.Enabled = _hWrap.Enabled = custom;
        }

        private void SelectAspect(int i)
        {
            _activeAspect = i;
            UpdateTabs();
            PopulateChips();
            Invalidate();
        }
        private void PopulateChips()
        {
            _chipFlow.Controls.Clear();
            _chips.Clear();
            foreach (var size in AspectRes[_activeAspect])
            {
                var sz = size;
                var chip = new RoundedButton
                {
                    Text = sz.Width + "×" + sz.Height,
                    Kind = (sz.Width == _s.Width && sz.Height == _s.Height) ? RoundedButtonKind.Primary : RoundedButtonKind.Neutral,
                    Width = 96, Height = 30, Radius = 6, Margin = new Padding(0, 0, 6, 6),
                    Font = FontHelper.Ui(9.5f, FontStyle.Bold),
                    Enabled = _s.ResolutionMode == ResolutionMode.Preset
                };
                chip.Click += (a, b) =>
                {
                    _s.Width = sz.Width; _s.Height = sz.Height;
                    _loading = true; _wBox.Text = sz.Width.ToString(); _hBox.Text = sz.Height.ToString(); _loading = false;
                    foreach (var c in _chips) c.Kind = c == (RoundedButton)a ? RoundedButtonKind.Primary : RoundedButtonKind.Neutral;
                    Invalidate();
                    Changed?.Invoke();
                };
                _chipFlow.Controls.Add(chip);
                _chips.Add(chip);
            }
        }

        // ── theming ──
        private void OnTheme() { if (!IsDisposed && IsHandleCreated) { try { BeginInvoke((Action)(() => { StyleBoxes(); Invalidate(); })); } catch { } } }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); StyleBoxes(); }
        protected override void Dispose(bool disposing) { if (disposing) ThemeHelper.ThemeChanged -= OnTheme; base.Dispose(disposing); }

        private bool Dark => ThemeHelper.IsDark;
        private Color Bg => Dark ? Color.FromArgb(32, 32, 36) : Color.FromArgb(245, 245, 248);
        private Color Fg => Dark ? Color.FromArgb(232, 232, 236) : Color.FromArgb(30, 30, 34);
        private Color Sub => Dark ? Color.FromArgb(150, 150, 156) : Color.FromArgb(112, 112, 120);
        private Color Accent => ThemeHelper.GetWindowsAccentColor();

        private void StyleBoxes()
        {
            BackColor = Bg;
            _chipFlow.BackColor = Bg;
            Color border = Dark ? Color.FromArgb(72, 72, 78) : Color.FromArgb(204, 204, 210);
            Color surf = Dark ? Color.FromArgb(44, 44, 50) : Color.White;
            _wWrap.BackColor = _hWrap.BackColor = border;
            _wBox.BackColor = _hBox.BackColor = surf;
            _wBox.ForeColor = _hBox.ForeColor = Fg;
        }

        // ── layout ──
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Relayout(); }
        private void Relayout()
        {
            if (_mNative == null || _wWrap == null) return;   // Height set in ctor fires layout before children exist
            const int pad = 8;
            int w = ClientSize.Width;
            int bw = (w - 2 * pad - 2 * 6) / 3;
            _mNative.SetBounds(pad, 8, bw, 32);
            _mPreset.SetBounds(pad + bw + 6, 8, bw, 32);
            _mCustom.SetBounds(pad + 2 * (bw + 6), 8, w - pad - (pad + 2 * (bw + 6)), 32);

            int tw = (w - 2 * pad - 4 * 4) / 5;
            for (int i = 0; i < 5; i++) _tabs[i].SetBounds(pad + i * (tw + 4), 74, tw, 28);

            _chipFlow.SetBounds(pad, 108, w - 2 * pad, 66);

            _wWrap.SetBounds(30, 182, 100, 30);
            _hWrap.SetBounds(166, 182, 100, 30);

            _portraitPill = new Rectangle(pad, 226, 46, 24);
        }

        // ── portrait toggle (owner-drawn; hit-tested) ──
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            bool portraitEnabled = _s.ResolutionMode != ResolutionMode.Native;
            if (portraitEnabled && _portraitPill.Contains(e.Location))
            {
                _s.Portrait = !_s.Portrait;
                Invalidate();
                Changed?.Invoke();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Bg);
            const int pad = 8;
            bool native = _s.ResolutionMode == ResolutionMode.Native;
            bool custom = _s.ResolutionMode == ResolutionMode.Custom;
            bool portraitOn = _s.Portrait && !native;

            // Native line (bright when active, dim otherwise)
            string nativeStr = _native.Width > 0 ? "Native: " + _native.Width + " × " + _native.Height + "   (resolved per-device at connect)" : "Native: (undetected)";
            DrawText(g, nativeStr, pad + 2, 46, ClientSize.Width - pad - 4, 20, native ? Fg : Sub, FontHelper.Ui(10f, native ? FontStyle.Bold : FontStyle.Regular));

            // Custom labels (compact W / H before each box)
            DrawText(g, "W", 8, 188, 18, 20, custom ? Fg : Sub, FontHelper.Ui(11f, FontStyle.Bold));
            DrawText(g, "H", 144, 188, 18, 20, custom ? Fg : Sub, FontHelper.Ui(11f, FontStyle.Bold));

            // Portrait pill + label + effective
            bool portraitEnabled = !native;
            Color pillOn = portraitOn ? Accent : (Dark ? Color.FromArgb(74, 74, 80) : Color.FromArgb(200, 200, 206));
            if (!portraitEnabled) pillOn = Dark ? Color.FromArgb(55, 55, 58) : Color.FromArgb(224, 224, 228);
            using (var b = new SolidBrush(pillOn))
            using (var p = DrawHelper.RoundedRect(_portraitPill, _portraitPill.Height / 2)) g.FillPath(b, p);
            int kd = _portraitPill.Height - 6, kx = portraitOn ? _portraitPill.Right - kd - 3 : _portraitPill.Left + 3;
            using (var kb = new SolidBrush(Color.White)) g.FillEllipse(kb, new Rectangle(kx, _portraitPill.Top + 3, kd, kd));
            DrawText(g, "Portrait (swap W↔H)", _portraitPill.Right + 10, _portraitPill.Top, 200, _portraitPill.Height, portraitEnabled ? Fg : Sub, FontHelper.Ui(10f));

            var eff = EffectiveSize();
            string effStr = eff.Width > 0 ? "→ " + eff.Width + " × " + eff.Height : "→ —";
            DrawText(g, effStr, ClientSize.Width - pad - 150, _portraitPill.Top, 150, _portraitPill.Height, Accent, FontHelper.Ui(10.5f, FontStyle.Bold), right: true);

            // Note
            DrawText(g, "In landscape fullscreen-embed the session fills the physical screen, so portrait may letterbox or be overridden.",
                pad, 258, ClientSize.Width - 2 * pad, 34, Sub, FontHelper.Ui(8.5f), wrap: true);
        }

        private Size EffectiveSize()
        {
            if (_s.ResolutionMode == ResolutionMode.Native) return _native;
            int w = _s.Width, h = _s.Height;
            if (_s.Portrait) { int t = w; w = h; h = t; }
            return new Size(w, h);
        }

        private static void DrawText(Graphics g, string text, int x, int y, int w, int h, Color color, Font f, bool right = false, bool wrap = false)
        {
            var flags = TextFormatFlags.NoPrefix | (right ? TextFormatFlags.Right : TextFormatFlags.Left)
                        | (wrap ? TextFormatFlags.WordBreak | TextFormatFlags.Top : TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            using (f) TextRenderer.DrawText(g, text, f, new Rectangle(x, y, w, h), color, flags);
        }
    }
}
