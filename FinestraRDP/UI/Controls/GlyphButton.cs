using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Finestra.UI.Controls
{
    /// <summary>A flat title-bar glyph button (hamburger / close / back). NO accent fill of its own — it sits ON
    /// the accent title bar; hover adds a subtle lighter wash. The glyph is drawn with GDI+ lines (no font/emoji
    /// dependency) so it renders identically on RT 8.1 and Windows 10/11. Set <see cref="Accent"/> to the bar
    /// color and Invalidate on theme change.</summary>
    public sealed class GlyphButton : Control
    {
        public enum Glyph { Menu, Close, Back }
        private readonly Glyph _glyph;
        private bool _hover;

        public Color Accent { get; set; } = Color.FromArgb(0, 120, 215);

        public GlyphButton(Glyph glyph)
        {
            _glyph = glyph;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(_hover ? Blend(Accent, Color.White, 0.18f) : Accent))
                g.FillRectangle(b, ClientRectangle);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            int cx = Width / 2, cy = Height / 2;
            using (var pen = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                switch (_glyph)
                {
                    case Glyph.Menu:
                        int hw = 9;
                        g.DrawLine(pen, cx - hw, cy - 6, cx + hw, cy - 6);
                        g.DrawLine(pen, cx - hw, cy, cx + hw, cy);
                        g.DrawLine(pen, cx - hw, cy + 6, cx + hw, cy + 6);
                        break;
                    case Glyph.Close:
                        int r = 6;
                        g.DrawLine(pen, cx - r, cy - r, cx + r, cy + r);
                        g.DrawLine(pen, cx + r, cy - r, cx - r, cy + r);
                        break;
                    case Glyph.Back:
                        g.DrawLine(pen, cx + 5, cy - 6, cx - 4, cy);
                        g.DrawLine(pen, cx - 4, cy, cx + 5, cy + 6);
                        break;
                }
            }
        }

        private static Color Blend(Color a, Color b, float t)
            => Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }
}
