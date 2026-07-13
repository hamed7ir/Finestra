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
    }
}
