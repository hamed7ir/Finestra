using System;
using System.Drawing;
using System.Windows.Forms;
using Finestra.Helpers;
using Finestra.UI.Controls;

namespace Finestra.UI
{
    /// <summary>
    /// Base for the app's modal dialogs: the same borderless accent-title-bar chrome as the main window (owner-
    /// painted title + close + drag), a scrollable <see cref="Body"/> (owner-drawn scrollbar via
    /// <see cref="ThemedScrollPanel"/>) and a right-aligned <see cref="Footer"/> for action buttons. Uses the
    /// Dock-based chrome (Top header + Fill content) so it scales correctly under DPI, and recolors live on
    /// <see cref="ThemeHelper.ThemeChanged"/>. Every visual comes from ThemeHelper (accent 8.1/10/11-branched).
    /// </summary>
    public class ThemedDialog : Form
    {
        private const int BarH = 46;

        private readonly Panel _header, _content;
        private readonly GlyphButton _close;
        private readonly string _title;
        private bool _drag;
        private Point _dragStart;

        protected readonly ThemedScrollPanel Body;
        protected readonly FlowLayoutPanel Footer;

        public ThemedDialog(string title, int width, int height)
        {
            _title = title ?? "";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Font;
            Font = FontHelper.Ui(9.75f);
            ClientSize = new Size(width, height);
            DoubleBuffered = true;
            ThemedChrome.SetAppIcon(this);

            _header = new Panel { Dock = DockStyle.Top, Height = BarH };
            _header.Paint += HeaderPaint;
            _header.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { _drag = true; _dragStart = e.Location; } };
            _header.MouseMove += (s, e) => { if (_drag) Location = new Point(Location.X + e.X - _dragStart.X, Location.Y + e.Y - _dragStart.Y); };
            _header.MouseUp += (s, e) => _drag = false;

            _close = new GlyphButton(GlyphButton.Glyph.Close) { Dock = DockStyle.Right, Width = 48, TabStop = false };
            _close.Click += (s, e) => { if (DialogResult == DialogResult.None) DialogResult = DialogResult.Cancel; Close(); };
            _header.Controls.Add(_close);

            Footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 60, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 11, 14, 11) };
            Body = new ThemedScrollPanel { Dock = DockStyle.Fill };

            _content = new Panel { Dock = DockStyle.Fill };
            _content.Controls.Add(Body);      // Fill (added first)
            _content.Controls.Add(Footer);    // Bottom

            Controls.Add(_content);           // Fill (added first) → docks around the Top header
            Controls.Add(_header);

            ApplyDialogTheme();
            ThemeHelper.ThemeChanged += ApplyDialogTheme;

            // FIN-KEYBOARD — a short centered dialog can sit UNDER the touch keyboard; an inset isn't
            // enough. Shift the whole form up by the overlap (shrink only if the title bar would leave
            // the screen), and restore the exact original bounds when the keyboard goes away.
            Core.KeyboardInset.KeyboardRectChanged += OnKeyboardRect;
            Core.KeyboardInset.Register();
            Disposed += (s, e) => { Core.KeyboardInset.KeyboardRectChanged -= OnKeyboardRect; Core.KeyboardInset.Unregister(); };
        }

        private bool _kbShifted;              // capture guard: bounds saved once per keyboard cycle (no drift)
        private Rectangle _kbSavedBounds;

        private void OnKeyboardRect(Rectangle kb)
        {
            try
            {
                if (IsDisposed || !Visible) return;
                if (kb.IsEmpty)
                {
                    if (_kbShifted) { _kbShifted = false; Bounds = _kbSavedBounds; }   // EXACT restore
                    return;
                }
                int need = Bounds.Bottom - kb.Top;
                if (need <= 0 || !Bounds.IntersectsWith(kb)) return;   // already clear of the keyboard
                if (!_kbShifted) { _kbSavedBounds = Bounds; _kbShifted = true; }   // save ONCE (guard)
                Rectangle wa;
                try { wa = Screen.FromControl(this).WorkingArea; } catch { wa = Screen.PrimaryScreen.WorkingArea; }
                int newTop = Math.Max(wa.Top, Top - need);
                int remain = need - (Top - newTop);
                int newH = remain > 0 ? Math.Max(220, Height - remain) : Height;   // shrink only if shifting isn't enough
                SuspendLayout();
                SetBounds(Left, newTop, Width, newH);
                ResumeLayout(true);
            }
            catch { /* best-effort — never break a dialog over the keyboard */ }
        }

        private void HeaderPaint(object sender, PaintEventArgs e)
        {
            var rect = new Rectangle(14, 0, Math.Max(1, _header.Width - _close.Width - 20), BarH);
            using (var f = FontHelper.Ui(12.5f, FontStyle.Bold))
                TextRenderer.DrawText(e.Graphics, _title, f, rect, Color.White,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        protected virtual void ApplyDialogTheme()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke((Action)ApplyDialogTheme); } catch { } return; }
            bool dark = ThemeHelper.IsDark;
            Color accent = ThemeHelper.GetWindowsAccentColor();
            Color bg = dark ? Color.FromArgb(32, 32, 36) : Color.FromArgb(245, 245, 248);
            BackColor = bg;
            _header.BackColor = accent;
            _close.Accent = accent; _close.BackColor = accent;
            _content.BackColor = bg;
            Footer.BackColor = dark ? Color.FromArgb(38, 38, 43) : Color.FromArgb(236, 236, 240);
            _header.Invalidate(); _close.Invalidate();
        }

        /// <summary>Adds a right-aligned action button to the footer. First added = rightmost. When <paramref
        /// name="dr"/> is not None the click sets DialogResult (and closes for a non-OK result).</summary>
        protected RoundedButton AddFooterButton(string text, RoundedButtonKind kind, DialogResult dr)
        {
            var b = new RoundedButton
            {
                Text = text,
                Kind = kind,
                Width = 116,
                Height = 38,
                Margin = new Padding(8, 0, 0, 0),
                Font = FontHelper.Ui(10f, FontStyle.Bold)
            };
            if (dr != DialogResult.None) b.Click += (s, e) => { DialogResult = dr; };
            Footer.Controls.Add(b);
            return b;
        }

        /// <summary>Adds all rows in order to the scrollable body (top-to-bottom) and refreshes the scrollbar.</summary>
        protected void PopulateBody(params Control[] rows)
        {
            int y = 6;
            foreach (var r in rows)
            {
                r.Location = new Point(0, y);
                r.Width = Math.Max(10, Body.Host.ClientSize.Width);
                r.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                Body.Host.Controls.Add(r);
                y += r.Height;
            }
            Body.Host.Height = y + 8;
            Body.RelayoutContent();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // width was 0 at populate time; stretch rows to the now-laid-out body width
            foreach (Control r in Body.Host.Controls) r.Width = Body.Host.ClientSize.Width;
            Body.RelayoutContent();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            ThemeHelper.ThemeChanged -= ApplyDialogTheme;
            base.OnFormClosed(e);
        }

        // FIN-KEYBOARD — mirrors TelegArm's WM_ACTIVATE fix: on a NON-click reactivation (WA_ACTIVE — the
        // keyboard-close returning activation, alt-tab, or the dialog's own Show), WinForms auto-restores
        // focus to the last-focused control. If that's a text field, Windows then auto-re-shows the touch
        // keyboard for it — the "tap the keyboard's ✕, it reappears" loop. Clear that AUTO-restored focus;
        // a real user TAP on a field arrives via WM_LBUTTONDOWN (never plain WA_ACTIVE) and still focuses
        // it normally. ThemedDialog is the base of every text-input dialog (connection editor, prompts,
        // settings), so one guard here covers all of them.
        private const int WM_ACTIVATE = 0x0006;
        private const long WA_ACTIVE = 1;   // (low word) 0=inactive 1=active-non-click 2=click-active

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_ACTIVATE && (m.WParam.ToInt64() & 0xFFFF) == WA_ACTIVE)
                try { BeginInvoke((Action)ClearAutoRefocusedTextField); } catch { }
            base.WndProc(ref m);
        }

        private void ClearAutoRefocusedTextField()
        {
            try
            {
                if (IsDisposed) return;
                if (ActiveControl is TextBoxBase) ActiveControl = null;
            }
            catch { }
        }
    }
}
