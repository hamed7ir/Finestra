using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Finestra.Core;
using Finestra.Helpers;
using Finestra.UI.Controls;

namespace Finestra.UI
{
    /// <summary>
    /// FRDP-SSH-BUILD-1 — a STANDALONE themed SSH terminal window (borderless accent header + resizable frame),
    /// hosting a <see cref="TerminalControl"/> wired to an <see cref="SshSession"/>. Opened from
    /// <see cref="MainForm"/> when the connection's Type is Ssh. It is NOT the RDP <see cref="SessionHost"/> (no
    /// wfreerdp child, no tabs/tear-off/fullscreen — that shell integration is the next batch). Connects off the
    /// UI thread; marshals RX repaints back; disposes the session on close.
    /// </summary>
    public sealed class SshTerminalForm : Form
    {
        private const int Frame = 6, BarH = 30;
        private readonly ConnectionProfile _cp;
        private readonly TerminalControl _term;
        private readonly Panel _header;
        private SshSession _session;
        private string _title;
        private bool _live;

        public SshTerminalForm(ConnectionProfile cp)
        {
            _cp = cp;
            _title = (string.IsNullOrEmpty(cp.Name) ? cp.Host : cp.Name);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            MinimumSize = new Size(480, 260);
            ClientSize = new Size(900, 560);
            BackColor = ThemeHelper.GetWindowsAccentColor();   // the 6px frame reads as accent chrome
            Padding = new Padding(Frame);
            Text = _title + " — SSH";
            ThemedChrome.SetAppIcon(this);
            DoubleBuffered = true;

            _term = new TerminalControl { Dock = DockStyle.Fill };
            _header = new Panel { Dock = DockStyle.Top, Height = BarH };
            _header.Paint += HeaderPaint;
            _header.MouseDown += HeaderMouseDown;

            Controls.Add(_term);       // Fill (added first → below the Top header)
            Controls.Add(_header);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _term.Focus();
            SetHeader("Connecting to " + _cp.Host + " …");
            // connect off the UI thread (the handshake blocks up to ~20s)
            int cols = _term.Cols, rows = _term.Rows, w = _term.ClientSize.Width, h = _term.ClientSize.Height;
            Task.Run(() =>
            {
                var s = new SshSession(_cp);
                // TOFU host-key verification + passphrase entry are UI decisions — marshal them to the UI thread
                // (this connect runs off it, so a blocking Invoke here is safe and shows the modal dialog).
                s.HostKeyVerifier = prompt =>
                {
                    try { return IsDisposed ? HostKeyDecision.Reject : (HostKeyDecision)Invoke(new Func<HostKeyDecision>(() => HostKeyDialog.Ask(this, prompt))); }
                    catch { return HostKeyDecision.Reject; }
                };
                s.PassphraseProvider = () =>
                {
                    try { return IsDisposed ? null : (string)Invoke(new Func<string>(() => PassphrasePrompt.Ask(this, _cp.Host, _cp.Ssh?.PrivateKeyPath))); }
                    catch { return null; }
                };
                try
                {
                    s.Connect(cols, rows, w, h);
                    BeginInvoke((Action)(() => GoLive(s)));
                }
                catch (Exception ex)
                {
                    FileLog.Line("[SSH] connect failed: " + ex.GetType().Name + ": " + ex.Message);
                    try { s.Dispose(); } catch { }
                    BeginInvoke((Action)(() => Fail(ex)));
                }
            });
        }

        private void GoLive(SshSession s)
        {
            if (IsDisposed) { try { s.Dispose(); } catch { } return; }
            _session = s;
            _live = true;
            SetHeader(_title + "  —  " + (s.ServerVersion ?? "SSH"));
            s.Received += bytes => { try { if (!IsDisposed) BeginInvoke((Action)(() => _term.Feed(bytes))); } catch { } };
            s.Closed += reason => { try { if (!IsDisposed) BeginInvoke((Action)(() => OnSessionClosed(reason))); } catch { } };
            _term.Input += bytes => s.Send(bytes);
            _term.Resized += (c, r, pw, ph) => s.Resize(c, r, pw, ph);
            // push the current size to the server now that it's live
            s.Resize(_term.Cols, _term.Rows, _term.ClientSize.Width, _term.ClientSize.Height);
            _term.Focus();
        }

        private void Fail(Exception ex)
        {
            if (IsDisposed) return;
            // classified, plain-language message — never a raw exception/stack dump (shared with the in-shell SshContent)
            MessageBox.Show(this, SshErrors.Explain(ex, _cp.Host), "Finestra — SSH", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }

        private void OnSessionClosed(string reason)
        {
            _live = false;
            SetHeader(_title + "  —  disconnected (" + reason + ")");
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
            // close ✕
            int cx = _header.Width - 23, cy = BarH / 2;
            using (var p = new Pen(Color.White, 1.7f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
            { g.DrawLine(p, cx - 6, cy - 6, cx + 6, cy + 6); g.DrawLine(p, cx + 6, cy - 6, cx - 6, cy + 6); }
        }

        private void HeaderMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (e.X >= _header.Width - 40) { Close(); return; }   // ✕ hit
            ReleaseCapture();
            SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);  // WM_NCLBUTTONDOWN / HTCAPTION → native move
        }

        // borderless-but-resizable frame
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
            try { _session?.Dispose(); } catch { }
            FileLog.Line("[SSH] window closed" + (_live ? " (was live)" : ""));
            base.OnFormClosed(e);
        }

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
