using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Finestra.Helpers;

namespace Finestra.UI.Controls
{
    /// <summary>
    /// FRDP-TEAROFF — the little accent chip that follows the cursor while a tab is being torn out. A borderless,
    /// topmost, no-activation tool window (so it never steals focus or a click) painted in the Windows accent with
    /// the session name. Deliberately simple: a drag affordance, not a live thumbnail.
    /// </summary>
    public sealed class DragGhost : Form
    {
        private string _text = "";
        private bool _merge;

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x00000020;   // WS_EX_TRANSPARENT — click-through, never intercepts the drop
                return cp;
            }
        }

        public DragGhost()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(190, 34);
            Opacity = 0.9;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        /// <summary><paramref name="merge"/> = the cursor is over another host's tab bar (drop = merge, not new window).</summary>
        public void Track(string text, Point screenPt, bool merge)
        {
            _text = text ?? ""; _merge = merge;
            SetBounds(screenPt.X + 14, screenPt.Y + 18, Width, Height);
            if (!Visible) Show();
            Invalidate();
        }

        public void Stop() { if (Visible) Hide(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color accent = ThemeHelper.GetWindowsAccentColor();
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var b = new SolidBrush(accent))
            using (var path = DrawHelper.RoundedRect(r, 6)) g.FillPath(b, path);
            string label = (_merge ? "→ " : "⧉ ") + _text;
            using (var f = FontHelper.Ui(9.5f, FontStyle.Bold))
                TextRenderer.DrawText(g, label, f, new Rectangle(8, 0, Width - 12, Height), Color.White,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }
}
