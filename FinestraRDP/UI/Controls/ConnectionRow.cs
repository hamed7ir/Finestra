using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Finestra.Core;
using Finestra.Helpers;

namespace Finestra.UI.Controls
{
    /// <summary>
    /// One connection in the list: a themed rounded card showing the name + target, with Connect / Edit /
    /// Delete actions (Delete is Danger-red, never accent). Colors follow <see cref="ThemeHelper"/> and recolor
    /// live. Owner-painted card so it looks identical on RT 8.1 and 10/11.
    /// </summary>
    public sealed class ConnectionRow : Control
    {
        public ConnectionProfile Profile { get; }
        public event Action<ConnectionProfile> ConnectClicked;
        public event Action<ConnectionProfile> EditClicked;
        public event Action<ConnectionProfile> DeleteClicked;

        private readonly RoundedButton _connect, _edit, _delete;

        public ConnectionRow(ConnectionProfile cp)
        {
            Profile = cp;
            Height = 78;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            _connect = Btn("Connect", RoundedButtonKind.Primary, () => ConnectClicked?.Invoke(Profile));
            _edit = Btn("Edit", RoundedButtonKind.Neutral, () => EditClicked?.Invoke(Profile));
            _delete = Btn("Delete", RoundedButtonKind.Danger, () => DeleteClicked?.Invoke(Profile));
            Controls.Add(_connect);
            Controls.Add(_edit);
            Controls.Add(_delete);

            ThemeHelper.ThemeChanged += OnTc;
        }

        private RoundedButton Btn(string text, RoundedButtonKind kind, Action onClick)
        {
            var b = new RoundedButton { Text = text, Kind = kind, Height = 34, Font = FontHelper.Ui(9.5f, FontStyle.Bold) };
            b.Click += (s, e) => onClick();
            return b;
        }

        private void OnTc() { if (!IsDisposed && IsHandleCreated) { try { BeginInvoke((Action)(() => Invalidate())); } catch { } } }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ThemeHelper.ThemeChanged -= OnTc;
            base.Dispose(disposing);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_connect == null || _edit == null || _delete == null) return;   // Height set in ctor fires layout early
            const int m = 10;
            int cw = 96, ew = 66, dw = 72, gap = 8;
            int cy = (Height - 34) / 2;
            int x = Width - m - cw;
            _connect.SetBounds(x, cy, cw, 34);
            x -= gap + ew; _edit.SetBounds(x, cy, ew, 34);
            x -= gap + dw; _delete.SetBounds(x, cy, dw, 34);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool dark = ThemeHelper.IsDark;
            Color pageBg = dark ? Color.FromArgb(32, 32, 36) : Color.FromArgb(245, 245, 248);
            Color card = dark ? Color.FromArgb(44, 44, 50) : Color.White;
            Color fg = dark ? Color.FromArgb(234, 234, 238) : Color.FromArgb(28, 28, 32);
            Color sub = dark ? Color.FromArgb(150, 150, 156) : Color.FromArgb(112, 112, 120);

            g.Clear(pageBg);
            var card_r = new Rectangle(2, 2, Math.Max(1, Width - 4), Height - 6);
            using (var b = new SolidBrush(card))
            using (var p = DrawHelper.RoundedRect(card_r, 10)) g.FillPath(b, p);

            // per-type glyph tile — size derives from the row height (DPI-safe), cache-owned bitmap
            int tile = Math.Max(24, Height - 38);
            var glyph = TypeGlyph.Get(TypeGlyph.KindOf(Profile.Type), tile);
            g.DrawImageUnscaled(glyph, 14, card_r.Top + (card_r.Height - tile) / 2);

            int textLeft = 14 + tile + 12;
            int textRight = _delete.Left - 12;
            var nameR = new Rectangle(textLeft, 12, Math.Max(1, textRight - textLeft), 24);
            var subR = new Rectangle(textLeft, 38, Math.Max(1, textRight - textLeft), 22);
            using (var nf = FontHelper.Ui(12f, FontStyle.Bold))
                TextRenderer.DrawText(g, string.IsNullOrEmpty(Profile.Name) ? Profile.Host : Profile.Name, nf, nameR, fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            using (var sf = FontHelper.Ui(9.5f))
                TextRenderer.DrawText(g, Profile.DisplayTarget, sf, subR, sub,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }
}
