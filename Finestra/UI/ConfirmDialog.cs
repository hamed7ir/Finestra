using System;
using System.Drawing;
using System.Windows.Forms;
using Finestra.Helpers;
using Finestra.UI.Controls;

namespace Finestra.UI
{
    /// <summary>
    /// FRDP-POLISH-3 — a themed Yes/No confirm on <see cref="ThemedDialog"/> (accent title bar, themed buttons, live
    /// recolor), replacing the raw white system <c>MessageBox</c> confirms so they match the rest of the app. Yes =
    /// Primary, No = Neutral; the ✕ and Esc = No, Enter = Yes. Returns true only on Yes.
    /// </summary>
    public sealed class ConfirmDialog : ThemedDialog
    {
        private const int W = 460;

        public static bool Ask(IWin32Window owner, string message, string title = "Finestra", string yesText = "Yes", string noText = "No")
        {
            using (var d = new ConfirmDialog(title, message ?? "", yesText, noText))
                return d.ShowDialog(owner) == DialogResult.Yes;
        }

        /// <summary>FRDP-FIXSWEEP — a single-OK themed info dialog (no raw MessageBox for NEW prompts).</summary>
        public static void Info(IWin32Window owner, string message, string title = "Finestra")
        {
            using (var d = new ConfirmDialog(title, message ?? "", "OK", null))
                d.ShowDialog(owner);
        }

        private ConfirmDialog(string title, string message, string yesText, string noText)
            : base(title, W, 46 + 60 + BodyH(message) + 20)
        {
            PopulateBody(new MessageRow(message) { Height = BodyH(message) });
            AddFooterButton(yesText, RoundedButtonKind.Primary, DialogResult.Yes);   // first added = rightmost
            if (!string.IsNullOrEmpty(noText)) AddFooterButton(noText, RoundedButtonKind.Neutral, DialogResult.No);
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { DialogResult = DialogResult.Yes; }        // RoundedButton isn't an
                else if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.No; }   // IButtonControl → do it here
            };
        }

        /// <summary>Body height for the wrapped message (measured a touch narrower than the body so it never clips).</summary>
        private static int BodyH(string message)
        {
            using (var f = FontHelper.Ui(10.5f))
                return Math.Max(56, TextRenderer.MeasureText(message, f, new Size(W - 40, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + 30);
        }

        /// <summary>Owner-drawn message so it reads ThemeHelper live (recolors on a mid-dialog theme flip).</summary>
        private sealed class MessageRow : Control
        {
            private readonly string _text;
            public MessageRow(string text)
            {
                _text = text ?? "";
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                ThemeHelper.ThemeChanged += OnTc;
            }
            private void OnTc() { if (!IsDisposed) { try { BeginInvoke((Action)Invalidate); } catch { } } }
            protected override void OnPaint(PaintEventArgs e)
            {
                bool dark = ThemeHelper.IsDark;
                Color bg = dark ? Color.FromArgb(32, 32, 36) : Color.FromArgb(245, 245, 248);
                Color fg = dark ? Color.FromArgb(228, 228, 232) : Color.FromArgb(32, 32, 36);
                using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, ClientRectangle);
                using (var f = FontHelper.Ui(10.5f))
                    TextRenderer.DrawText(e.Graphics, _text, f, new Rectangle(14, 10, Math.Max(20, Width - 28), Height - 14), fg,
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Top);
            }
            protected override void Dispose(bool disposing) { if (disposing) ThemeHelper.ThemeChanged -= OnTc; base.Dispose(disposing); }
        }
    }
}
