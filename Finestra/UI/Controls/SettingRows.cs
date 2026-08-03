using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Finestra.Helpers;

namespace Finestra.UI.Controls
{
    /// <summary>Shared base for the themed setting rows: colors from <see cref="ThemeHelper"/>, live re-theme,
    /// a common owner-painted background. All rows are custom-painted (no OS theming) so they look identical on
    /// RT 8.1 and Windows 10/11.</summary>
    public abstract class SettingRow : Control
    {
        protected string LabelText;

        protected SettingRow(string label)
        {
            LabelText = label ?? "";
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 46;
            ThemeHelper.ThemeChanged += OnTc;
        }

        private void OnTc()
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)(() => { OnThemeApplied(); Invalidate(); })); } catch { }
        }

        protected virtual void OnThemeApplied() { }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ThemeHelper.ThemeChanged -= OnTc;
            base.Dispose(disposing);
        }

        protected bool Dark => ThemeHelper.IsDark;
        protected Color Accent => ThemeHelper.GetWindowsAccentColor();
        protected Color Bg => Dark ? Color.FromArgb(32, 32, 36) : Color.FromArgb(245, 245, 248);
        protected Color Fg => Dark ? Color.FromArgb(232, 232, 236) : Color.FromArgb(30, 30, 34);
        protected Color Sub => Dark ? Color.FromArgb(150, 150, 156) : Color.FromArgb(112, 112, 120);

        protected void PaintLeftLabel(Graphics g, int rightReserved)
        {
            var r = new Rectangle(6, 0, Math.Max(1, Width - rightReserved - 10), Height);
            using (var f = FontHelper.Ui(10.5f))
                TextRenderer.DrawText(g, LabelText, f, r, Fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>A section title with an underline — groups related rows.</summary>
    public sealed class SectionHeader : SettingRow
    {
        public SectionHeader(string label) : base(label) { Height = 40; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Bg);
            var r = new Rectangle(6, 0, Width - 12, Height);
            using (var f = FontHelper.Ui(11f, FontStyle.Bold))
                TextRenderer.DrawText(g, LabelText, f, r, Accent,
                    TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.NoPrefix);
            using (var p = new Pen(Dark ? Color.FromArgb(58, 58, 64) : Color.FromArgb(220, 220, 226)))
                g.DrawLine(p, 6, Height - 4, Width - 8, Height - 4);
        }
    }

    /// <summary>Label + iOS-style on/off switch (accent when on). Maps to a boolean flag.</summary>
    public sealed class ToggleRow : SettingRow
    {
        private bool _on;
        public event Action Changed;
        public bool On { get => _on; set { if (_on != value) { _on = value; Invalidate(); Changed?.Invoke(); } } }

        public ToggleRow(string label, bool on) : base(label) { _on = on; Cursor = Cursors.Hand; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
            PaintLeftLabel(g, 62);
            int pw = 46, ph = 24, px = Width - pw - 10, py = (Height - ph) / 2;
            var pill = new Rectangle(px, py, pw, ph);
            using (var b = new SolidBrush(_on ? Accent : (Dark ? Color.FromArgb(74, 74, 80) : Color.FromArgb(200, 200, 206))))
            using (var p = DrawHelper.RoundedRect(pill, ph / 2)) g.FillPath(b, p);
            int kd = ph - 6, kx = _on ? px + pw - kd - 3 : px + 3;
            using (var kb = new SolidBrush(Color.White)) g.FillEllipse(kb, new Rectangle(kx, py + 3, kd, kd));
        }

        protected override void OnMouseClick(MouseEventArgs e) { On = !On; base.OnMouseClick(e); }
    }

    /// <summary>Label + a value that opens a themed dropdown menu of choices. Maps to an enum/value option.</summary>
    public sealed class ChoiceRow : SettingRow
    {
        private readonly string[] _opts;
        private int _idx;
        public event Action Changed;

        public int SelectedIndex
        {
            get => _idx;
            set { if (value >= 0 && value < _opts.Length && _idx != value) { _idx = value; Invalidate(); Changed?.Invoke(); } }
        }
        public string SelectedText => (_idx >= 0 && _idx < _opts.Length) ? _opts[_idx] : "";

        /// <summary>Width of the right-hand value area. Widen it for options whose text carries a tradeoff, so the
        /// collapsed row shows the choice in full instead of ellipsizing it.</summary>
        public int ValueWidth { get; set; } = 168;

        public ChoiceRow(string label, string[] options, int index) : base(label)
        {
            _opts = options ?? new string[0];
            _idx = Math.Max(0, Math.Min(index, _opts.Length - 1));
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
            PaintLeftLabel(g, ValueWidth + 32);
            var vr = new Rectangle(Width - ValueWidth - 28, 0, ValueWidth, Height);
            using (var f = FontHelper.Ui(10.5f))
                TextRenderer.DrawText(g, SelectedText, f, vr, Accent,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            int cx = Width - 16, cy = Height / 2;
            using (var p = new Pen(Sub, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            { g.DrawLine(p, cx - 5, cy - 2, cx, cy + 3); g.DrawLine(p, cx + 5, cy - 2, cx, cy + 3); }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            var menu = new ThemedContextMenuStrip { Font = FontHelper.Ui(9.75f) };
            for (int i = 0; i < _opts.Length; i++)
            {
                int ii = i;
                var it = new ToolStripMenuItem(_opts[i]) { Checked = (i == _idx) };
                it.Click += (s, ev) => SelectedIndex = ii;
                menu.Items.Add(it);
            }
            menu.Show(this, new Point(Width - ValueWidth - 28, Height - 6));
            base.OnMouseClick(e);
        }
    }

    /// <summary>Stacked label + themed single-line text box (flat, 1px accentable border). Optional numeric-only
    /// or password masking. Maps to a string/number flag value.</summary>
    public sealed class TextRow : SettingRow
    {
        private readonly Panel _wrap;
        private readonly TextBox _box;
        public event Action Changed;

        public string Value { get => _box.Text; set => _box.Text = value ?? ""; }

        public TextRow(string label, string value, bool numeric = false, bool password = false) : base(label)
        {
            Height = 62;
            _box = new TextBox { BorderStyle = BorderStyle.None, Font = FontHelper.Ui(11f) };
            if (password) _box.UseSystemPasswordChar = true;
            _box.TextChanged += (s, e) => Changed?.Invoke();
            if (numeric) _box.KeyPress += (s, e) => { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
            _wrap = new Panel { Padding = new Padding(8, 6, 8, 6) };
            _wrap.Controls.Add(_box);
            _box.Dock = DockStyle.Fill;
            Controls.Add(_wrap);
            _box.Text = value ?? "";
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); StyleBox(); }
        protected override void OnThemeApplied() { StyleBox(); }

        private void StyleBox()
        {
            _wrap.BackColor = Dark ? Color.FromArgb(72, 72, 78) : Color.FromArgb(204, 204, 210);   // 1px border ring
            _box.BackColor = Dark ? Color.FromArgb(44, 44, 50) : Color.White;
            _box.ForeColor = Fg;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_wrap == null || _box == null) return;   // base ctor sets Height, firing layout before fields exist
            int boxH = _box.PreferredHeight + 12;
            _wrap.SetBounds(6, Height - boxH - 6, Math.Max(40, Width - 12), boxH);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Bg);
            var r = new Rectangle(8, 4, Width - 16, 20);
            using (var f = FontHelper.Ui(9.5f))
                TextRenderer.DrawText(g, LabelText, f, r, Sub,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }
}
