using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Finestra.Core;
using Finestra.Helpers;
using Finestra.UI.Controls;

namespace Finestra.UI
{
    /// <summary>
    /// FRDP-FTP-BUILD-1/2 — the STANDALONE dual-pane file-browser window. Now a THIN shell around the reusable
    /// <see cref="FtpBrowserControl"/> (the same content the SessionHost tab hosts via <see cref="FtpContent"/>), so
    /// the browser is built once. Kept reachable, but the tabbed shell is the product path.
    /// </summary>
    public sealed class FtpBrowserForm : Form
    {
        private const int Frame = 6, BarH = 30;
        private readonly FtpBrowserControl _browser;
        private readonly Panel _header;
        private readonly string _title;

        public FtpBrowserForm(ConnectionProfile cp)
        {
            _title = string.IsNullOrEmpty(cp.Name) ? cp.Host : cp.Name;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(720, 460);
            ClientSize = new Size(1040, 640);
            BackColor = ThemeHelper.GetWindowsAccentColor();
            Padding = new Padding(Frame);
            Text = _title + " — Files";
            ThemedChrome.SetAppIcon(this);
            DoubleBuffered = true;

            _header = new Panel { Dock = DockStyle.Top, Height = BarH };
            _header.Paint += HeaderPaint;
            _header.MouseDown += HeaderMouseDown;

            _browser = new FtpBrowserControl(cp) { Dock = DockStyle.Fill };
            _browser.StatusChanged += b => { try { if (!IsDisposed) SetHeader(_title + "  —  " + b.StatusText); } catch { } };
            _browser.ConnectFailed += b => { try { if (!IsDisposed) Close(); } catch { } };

            Controls.Add(_browser);   // Fill (added first)
            Controls.Add(_header);    // Top
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            SetHeader("Connecting to " + _title + " …");
            _browser.Start();
        }

        private void SetHeader(string s) { _header.Tag = s; Text = s; _header.Invalidate(); }

        private void HeaderPaint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            Color accent = ThemeHelper.GetWindowsAccentColor();
            using (var b = new SolidBrush(accent)) g.FillRectangle(b, _header.ClientRectangle);
            string t = _header.Tag as string ?? Text;
            using (var f = FontHelper.Ui(9.5f, FontStyle.Bold))
                TextRenderer.DrawText(g, t, f, new Rectangle(10, 0, _header.Width - 46, BarH), Color.White,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            int cx = _header.Width - 23, cy = BarH / 2;
            using (var p = new Pen(Color.White, 1.7f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
            { g.DrawLine(p, cx - 6, cy - 6, cx + 6, cy + 6); g.DrawLine(p, cx + 6, cy - 6, cx - 6, cy + 6); }
        }

        private void HeaderMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (e.X >= _header.Width - 40) { Close(); return; }
            ReleaseCapture();
            SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0084 && WindowState == FormWindowState.Normal)   // WM_NCHITTEST
            {
                int lp = unchecked((int)(long)m.LParam);
                var p = PointToClient(new Point(unchecked((short)(lp & 0xFFFF)), unchecked((short)((lp >> 16) & 0xFFFF))));
                int w = ClientSize.Width, h = ClientSize.Height;
                bool l = p.X <= Frame, r = p.X >= w - Frame, t = p.Y <= Frame, b = p.Y >= h - Frame;
                int ht = 0;
                if (t && l) ht = 13; else if (t && r) ht = 14; else if (b && l) ht = 16; else if (b && r) ht = 17;
                else if (l) ht = 10; else if (r) ht = 11; else if (t) ht = 12; else if (b) ht = 15;
                if (ht != 0) { m.Result = (IntPtr)ht; return; }
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            FileLog.Line("[FTP] window closed");
            base.OnFormClosed(e);   // disposing the form disposes _browser (a child control) → disposes the remote fs
        }

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
