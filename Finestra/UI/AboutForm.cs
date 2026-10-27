using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;
using Finestra.Helpers;
using Finestra.UI.Controls;

namespace Finestra.UI
{
    /// <summary>
    /// The themed About box — app identity + version, a PROMINENTLY FEATURED acknowledgment of FreeRDP (the ported
    /// open-source RDP engine the whole RDP path is built on), the source/license links, and the open-source credits.
    /// Card layout on the app's <see cref="ThemedDialog"/> chrome (accent title bar + ✕ + scroll), dark/accent themed.
    /// Links open in the browser (Process.Start wrapped in try/catch). Styled to match the sibling app's About.
    /// </summary>
    public sealed class AboutForm : ThemedDialog
    {
        private const string GitHubUrl = "https://github.com/hamed7ir/Finestra";
        private const string FreeRdpUrl = "https://github.com/FreeRDP/FreeRDP";
        private const string SshNetUrl = "https://github.com/sshnet/SSH.NET";
        private const string FluentFtpUrl = "https://github.com/robinrodricks/FluentFTP";
        private const string LicenseUrl = "https://github.com/hamed7ir/Finestra/blob/main/LICENSE";

        private readonly Color _bg, _card, _border, _title, _sub, _accent;

        public AboutForm() : base("About Finestra", 470, 612)
        {
            bool dark = ThemeHelper.IsDark;
            _accent = ThemeHelper.GetWindowsAccentColor();
            _bg = dark ? Color.FromArgb(40, 40, 44) : Color.FromArgb(248, 248, 250);
            _card = dark ? Color.FromArgb(50, 50, 55) : Color.White;
            _border = dark ? Color.FromArgb(62, 62, 68) : Color.FromArgb(226, 226, 230);
            _title = dark ? Color.FromArgb(236, 236, 240) : Color.FromArgb(33, 33, 38);
            _sub = dark ? Color.FromArgb(158, 158, 165) : Color.FromArgb(110, 110, 116);
            Color featBg = Blend(_accent, _card, 0.16f);
            Color featBrd = Blend(_accent, _card, 0.55f);

            var host = Body.Host;
            host.BackColor = _bg;

            string ver;   // 3-part display version, read from the assembly at runtime — no hardcoded string to rot
            try
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                ver = v != null ? v.Major + "." + v.Minor + "." + v.Build : "1.0.0";
            }
            catch { ver = "1.0.0"; }

            const int M = 22, CW = 424;   // left margin, content width
            int y = 22;

            // Every text height below is the larger of its 100% value and what its font needs (Fit): the fonts grow
            // with the Windows scale and these heights did not, so at 150% the name ran into the line under it.

            // ── Header: icon + name + version + description (centered) ──
            int icon = UiScale.Px(96);
            var pic = new PictureBox
            {
                Size = new Size(icon, icon), Location = new Point(M + (CW - icon) / 2, y),
                SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent
            };
            try { var app = ThemedChrome.AppIcon; if (app != null) pic.Image = new Icon(app, icon, icon).ToBitmap(); } catch { }
            host.Controls.Add(pic);
            y += icon + 10;

            y += Add(host, "Finestra", M, y, CW, _title, FontHelper.Ui(20f, FontStyle.Bold), 34, _bg, ContentAlignment.MiddleCenter);
            y += Add(host, "Remote Connection Manager", M, y, CW, _accent, FontHelper.Ui(10.5f, FontStyle.Bold), 22, _bg, ContentAlignment.MiddleCenter) + 2;
            y += Add(host, "Version " + ver, M, y, CW, _sub, FontHelper.Ui(9.5f), 20, _bg, ContentAlignment.MiddleCenter) + 2;
            y += Add(host, "An ARM32-ready RDP · SSH · SFTP / FTP client for Windows RT 8.1 and Windows 10/11.", M, y, CW, _sub, FontHelper.Ui(9f), 44, _bg, ContentAlignment.TopCenter) + 6;   // 2 lines even at 120+ DPI

            // ── FEATURED — the three engines (accent-tinted, emphasized; identical card geometry) ──
            y += EngineCard(host, M, y, CW, featBg, featBrd, "RDP — built on FreeRDP",
                "The RDP engine is FreeRDP 3.32.2 (its SDL3 client, with WinPR), built for x64 and for ARM32 Windows RT. Apache-2.0 licensed.",
                "→  github.com/FreeRDP/FreeRDP", FreeRdpUrl) + 14;
            y += EngineCard(host, M, y, CW, featBg, featBrd, "SSH && SFTP — SSH.NET",
                "The SSH transport for the terminal and the SFTP browser is SSH.NET (Renci.SshNet) 2026.0.0 — MIT licensed. Terminal emulation: VtNetCore 1.0.30 (MIT).",
                "→  github.com/sshnet/SSH.NET", SshNetUrl) + 14;
            y += EngineCard(host, M, y, CW, featBg, featBrd, "FTP && FTPS — FluentFTP",
                "The FTP and FTPS engine is FluentFTP 55.1.0, a self-contained pure-.NET client with TLS via the in-box SslStream — MIT licensed.",
                "→  github.com/robinrodricks/FluentFTP", FluentFtpUrl) + 14;

            // ── Source + license ──
            var meta = Card(M, y, CW, 92, _card, _border);
            host.Controls.Add(meta);
            MetaRow(meta, 0, "Source code", "github.com/hamed7ir/Finestra", GitHubUrl, CW);
            meta.Controls.Add(new Panel { Location = new Point(14, 46), Size = new Size(CW - 28, 1), BackColor = _border });
            MetaRow(meta, 46, "License", "GPL-3.0-only", LicenseUrl, CW);
            y += 92 + 14;

            // ── Open-source credits — versions read from the SHIPPED bits (engine string-scan + DLL FileVersions),
            //    not from memory. Since 2.0.0 both engines link the same OpenSSL 3.5.9 / zlib 1.3.2 pins. ──
            string[][] credits =
            {
                new[] { "FreeRDP 3.32.2 · WinPR", "Apache-2.0" },
                new[] { "OpenSSL 3.5.9", "Apache-2.0" },
                new[] { "zlib 1.3.2", "zlib" },
                new[] { "SDL3 3.2.14 · SDL3_ttf 3.2.2", "zlib" },
                new[] { "FreeType 2.15.0", "FreeType License" },
                new[] { "Open Sans font (in the RDP engine)", "SIL OFL 1.1" },
                new[] { "SSH.NET (Renci.SshNet) 2026.0.0", "MIT" },
                new[] { "BouncyCastle.Cryptography 2.7.0", "MIT" },
                new[] { "FluentFTP 55.1.0", "MIT" },
                new[] { "VtNetCore 1.0.30", "MIT" },
                new[] { "Newtonsoft.Json 13.0.4", "MIT" },
                new[] { "Roboto font 3.009", "SIL OFL 1.1" },   // per the TTF's own name table (roboto-classic)
            };
            var cr = new Panel();   // sized once its rows are in
            int ry = 12;
            ry += Add(cr, "OPEN-SOURCE CREDITS", 16, ry, CW - 32, _accent, FontHelper.Ui(8.25f, FontStyle.Bold), 18, _card, ContentAlignment.MiddleLeft) + 8;
            foreach (var row in credits)
            {
                int rh = Math.Max(Fit(row[0], FontHelper.Ui(9f), CW - 170, 22), Fit(row[1], FontHelper.Ui(8.5f), 136, 22));
                cr.Controls.Add(Text2(row[0], 16, ry, CW - 170, _title, FontHelper.Ui(9f), rh, _card, ContentAlignment.MiddleLeft));
                cr.Controls.Add(Text2(row[1], CW - 152, ry, 136, _sub, FontHelper.Ui(8.5f), rh, _card, ContentAlignment.MiddleRight));
                ry += rh + 2;
            }
            int ch = ry + 10;
            MakeCard(cr, M, y, CW, ch, _card, _border);
            host.Controls.Add(cr);
            y += ch + 14;

            // ── Privacy + footer ──
            y += Add(host, "Privacy: collects no data and makes no network calls except to the servers you configure.", M, y, CW, _sub, FontHelper.Ui(8.5f), 44, _bg, ContentAlignment.TopCenter) + 2;   // 2 lines even at 120+ DPI
            y += Add(host, "Full license texts: THIRD-PARTY-NOTICES.txt", M, y, CW, _sub, FontHelper.Ui(8.25f), 18, _bg, ContentAlignment.MiddleCenter) + 2;
            y += Add(host, "© 2026 Hamed Ghorbani · Licensed under GPL-3.0-only", M, y, CW, _sub, FontHelper.Ui(8.25f), 18, _bg, ContentAlignment.MiddleCenter) + 4;

            host.Controls.Add(new Panel { Location = new Point(0, y), Size = new Size(4, 10), BackColor = _bg });   // extend the scroll extent
            host.Height = y + 10;

            AddFooterButton("Close", RoundedButtonKind.Primary, DialogResult.OK);
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel; };
        }

        // Absolute-positioned card layout → skip ThemedDialog's full-width row resize; just refresh the scrollbar.
        protected override void OnShown(EventArgs e) { try { Body.RelayoutContent(); } catch { } }

        /// <summary>One engine card: heading, a wrapped paragraph and a link, each as tall as its font needs (at
        /// 100%: 24, 68 and 22 tall, the card 152). Returns the card's height.</summary>
        private int EngineCard(Control host, int x, int y, int w, Color fill, Color brd, string heading, string body, string linkText, string url)
        {
            var card = new Panel();
            int cy = 14;
            cy += Add(card, heading, 16, cy, w - 32, _accent, FontHelper.Ui(12f, FontStyle.Bold), 24, fill, ContentAlignment.MiddleLeft) + 4;
            cy += Add(card, body, 16, cy, w - 32, _title, FontHelper.Ui(9f), 68, fill, ContentAlignment.TopLeft) + 8;
            var link = Link(linkText, 16, cy, w - 32, url, FontHelper.Ui(9.5f, FontStyle.Bold), fill);
            link.Height = Fit(linkText, link.Font, w - 32, 22);
            card.Controls.Add(link);
            cy += link.Height + 12;
            MakeCard(card, x, y, w, cy, fill, brd);
            host.Controls.Add(card);
            return cy;
        }

        /// <summary>Adds a label as tall as <see cref="Fit"/> says and returns that height.</summary>
        private static int Add(Control parent, string text, int x, int y, int w, Color color, Font font, int h, Color parentBg, ContentAlignment align)
        {
            var l = Text2(text, x, y, w, color, font, Fit(text, font, w, h), parentBg, align);
            parent.Controls.Add(l);
            return l.Height;
        }

        /// <summary>The larger of a label's 100%-scale height and the height its wrapped text needs in this font.</summary>
        private static int Fit(string text, Font font, int w, int h)
        {
            var need = TextRenderer.MeasureText(text.Replace("&&", "&"), font, new Size(w, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
            return Math.Max(h, need.Height);
        }

        private void MetaRow(Panel card, int cy, string left, string linkText, string url, int cw)
        {
            card.Controls.Add(Text2(left, 16, cy, 150, _title, FontHelper.Ui(9.5f), 46, _card, ContentAlignment.MiddleLeft));
            var link = Link(linkText, 160, cy, cw - 176, url, FontHelper.Ui(9.5f, FontStyle.Bold), _card);
            link.Height = 46; link.TextAlign = ContentAlignment.MiddleRight;
            card.Controls.Add(link);
        }

        private static Label Text2(string text, int x, int y, int w, Color color, Font font, int h, Color parentBg, ContentAlignment align)
        {
            return new Label
            {
                Text = text, Location = new Point(x, y), AutoSize = false, Size = new Size(w, h),
                ForeColor = color, BackColor = parentBg, Font = font, TextAlign = align
            };
        }

        private Label Link(string text, int x, int y, int w, string url, Font font, Color parentBg)
        {
            var l = new Label
            {
                Text = text, Location = new Point(x, y), AutoSize = false, Size = new Size(w, 22),
                ForeColor = _accent, BackColor = parentBg, Font = font, Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleLeft
            };
            l.Click += (s, e) => OpenUrl(url);
            return l;
        }

        private Panel Card(int x, int y, int w, int h, Color fill, Color brd) => MakeCard(new Panel(), x, y, w, h, fill, brd);

        private static Panel MakeCard(Panel p, int x, int y, int w, int h, Color fill, Color brd)
        {
            p.Location = new Point(x, y); p.Size = new Size(w, h); p.BackColor = fill;
            using (var path = DrawHelper.RoundedRect(new Rectangle(0, 0, w, h), 12))
                p.Region = new Region(path);
            p.Paint += (s, e) =>
            {
                var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(brd))
                using (var pa = DrawHelper.RoundedRect(new Rectangle(0, 0, w - 1, h - 1), 12))
                    g.DrawPath(pen, pa);
            };
            return p;
        }

        private static void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try { System.Diagnostics.Process.Start(url); } catch { /* RT-safe: no default browser / blocked → ignore */ }
        }

        private static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R * t + b.R * (1 - t)),
                (int)(a.G * t + b.G * (1 - t)),
                (int)(a.B * t + b.B * (1 - t)));
        }
    }
}
