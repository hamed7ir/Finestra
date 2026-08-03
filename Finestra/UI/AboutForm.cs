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

            // ── Header: icon + name + version + description (centered) ──
            var pic = new PictureBox
            {
                Size = new Size(96, 96), Location = new Point(M + (CW - 96) / 2, y),
                SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent
            };
            try { var app = ThemedChrome.AppIcon; if (app != null) pic.Image = new Icon(app, 96, 96).ToBitmap(); } catch { }
            host.Controls.Add(pic);
            y += 96 + 10;

            host.Controls.Add(Text2("Finestra", M, y, CW, _title, FontHelper.Ui(20f, FontStyle.Bold), 34, _bg, ContentAlignment.MiddleCenter)); y += 34;
            host.Controls.Add(Text2("Remote Connection Manager", M, y, CW, _accent, FontHelper.Ui(10.5f, FontStyle.Bold), 22, _bg, ContentAlignment.MiddleCenter)); y += 24;
            host.Controls.Add(Text2("Version " + ver, M, y, CW, _sub, FontHelper.Ui(9.5f), 20, _bg, ContentAlignment.MiddleCenter)); y += 22;
            host.Controls.Add(Text2("An ARM32-ready RDP · SSH · SFTP / FTP client for Windows RT 8.1 and Windows 10/11.", M, y, CW, _sub, FontHelper.Ui(9f), 44, _bg, ContentAlignment.TopCenter)); y += 50;   // 2 lines even at 120+ DPI

            // ── FEATURED — the three engines (accent-tinted, emphasized; identical card geometry) ──
            var feat = Card(M, y, CW, 152, featBg, featBrd);
            host.Controls.Add(feat);
            feat.Controls.Add(Text2("RDP — built on FreeRDP", 16, 14, CW - 32, _accent, FontHelper.Ui(12f, FontStyle.Bold), 24, featBg, ContentAlignment.MiddleLeft));
            feat.Controls.Add(Text2("The RDP engine is a ported build of FreeRDP 3.28.0 (with WinPR), cross-compiled for ARM32 Windows RT. Apache-2.0 licensed.",
                16, 42, CW - 32, _title, FontHelper.Ui(9f), 68, featBg, ContentAlignment.TopLeft));
            feat.Controls.Add(Link("→  github.com/FreeRDP/FreeRDP", 16, 118, CW - 32, FreeRdpUrl, FontHelper.Ui(9.5f, FontStyle.Bold), featBg));
            y += 152 + 14;

            var sshCard = Card(M, y, CW, 152, featBg, featBrd);
            host.Controls.Add(sshCard);
            sshCard.Controls.Add(Text2("SSH && SFTP — SSH.NET", 16, 14, CW - 32, _accent, FontHelper.Ui(12f, FontStyle.Bold), 24, featBg, ContentAlignment.MiddleLeft));
            sshCard.Controls.Add(Text2("The SSH transport for the terminal and the SFTP browser is SSH.NET (Renci.SshNet) 2025.1.0 — MIT licensed. Terminal emulation: VtNetCore 1.0.30 (MIT).",
                16, 42, CW - 32, _title, FontHelper.Ui(9f), 68, featBg, ContentAlignment.TopLeft));
            sshCard.Controls.Add(Link("→  github.com/sshnet/SSH.NET", 16, 118, CW - 32, SshNetUrl, FontHelper.Ui(9.5f, FontStyle.Bold), featBg));
            y += 152 + 14;

            var ftpCard = Card(M, y, CW, 152, featBg, featBrd);
            host.Controls.Add(ftpCard);
            ftpCard.Controls.Add(Text2("FTP && FTPS — FluentFTP", 16, 14, CW - 32, _accent, FontHelper.Ui(12f, FontStyle.Bold), 24, featBg, ContentAlignment.MiddleLeft));
            ftpCard.Controls.Add(Text2("The FTP and FTPS engine is FluentFTP 54.2.0, a self-contained pure-.NET client with TLS via the in-box SslStream — MIT licensed.",
                16, 42, CW - 32, _title, FontHelper.Ui(9f), 68, featBg, ContentAlignment.TopLeft));
            ftpCard.Controls.Add(Link("→  github.com/robinrodricks/FluentFTP", 16, 118, CW - 32, FluentFtpUrl, FontHelper.Ui(9.5f, FontStyle.Bold), featBg));
            y += 152 + 14;

            // ── Source + license ──
            var meta = Card(M, y, CW, 92, _card, _border);
            host.Controls.Add(meta);
            MetaRow(meta, 0, "Source code", "github.com/hamed7ir/Finestra", GitHubUrl, CW);
            meta.Controls.Add(new Panel { Location = new Point(14, 46), Size = new Size(CW - 28, 1), BackColor = _border });
            MetaRow(meta, 46, "License", "GPL-3.0-only", LicenseUrl, CW);
            y += 92 + 14;

            // ── Open-source credits — versions read from the SHIPPED bits (engine string-scan + DLL FileVersions),
            //    not from memory. OpenSSL/zlib differ per arch: the ARM32 engine links the foundry-built pair. ──
            string[][] credits =
            {
                new[] { "FreeRDP 3.28.0 · WinPR", "Apache-2.0" },
                new[] { "OpenSSL 3.6.3 (3.3.2 on ARM32)", "Apache-2.0" },
                new[] { "zlib 1.3.2 (1.3.1 on ARM32)", "zlib" },
                new[] { "SSH.NET (Renci.SshNet) 2025.1.0", "MIT" },
                new[] { "BouncyCastle.Cryptography 2.6.2", "MIT" },
                new[] { "FluentFTP 54.2.0", "MIT" },
                new[] { "VtNetCore 1.0.30", "MIT" },
                new[] { "Newtonsoft.Json 13.0.4", "MIT" },
                new[] { "Roboto font 3.009", "SIL OFL 1.1" },   // per the TTF's own name table (roboto-classic)
            };
            int ch = 40 + credits.Length * 24 + 8;
            var cr = Card(M, y, CW, ch, _card, _border);
            host.Controls.Add(cr);
            cr.Controls.Add(Text2("OPEN-SOURCE CREDITS", 16, 12, CW - 32, _accent, FontHelper.Ui(8.25f, FontStyle.Bold), 18, _card, ContentAlignment.MiddleLeft));
            int ry = 38;
            foreach (var row in credits)
            {
                cr.Controls.Add(Text2(row[0], 16, ry, CW - 170, _title, FontHelper.Ui(9f), 22, _card, ContentAlignment.MiddleLeft));
                cr.Controls.Add(Text2(row[1], CW - 152, ry, 136, _sub, FontHelper.Ui(8.5f), 22, _card, ContentAlignment.MiddleRight));
                ry += 24;
            }
            y += ch + 14;

            // ── Privacy + footer ──
            host.Controls.Add(Text2("Privacy: collects no data and makes no network calls except to the servers you configure.", M, y, CW, _sub, FontHelper.Ui(8.5f), 44, _bg, ContentAlignment.TopCenter)); y += 46;   // 2 lines even at 120+ DPI
            host.Controls.Add(Text2("Full license texts: THIRD-PARTY-NOTICES.txt", M, y, CW, _sub, FontHelper.Ui(8.25f), 18, _bg, ContentAlignment.MiddleCenter)); y += 20;
            host.Controls.Add(Text2("© 2026 Hamed Ghorbani · Licensed under GPL-3.0-only", M, y, CW, _sub, FontHelper.Ui(8.25f), 18, _bg, ContentAlignment.MiddleCenter)); y += 22;

            host.Controls.Add(new Panel { Location = new Point(0, y), Size = new Size(4, 10), BackColor = _bg });   // extend the scroll extent
            host.Height = y + 10;

            AddFooterButton("Close", RoundedButtonKind.Primary, DialogResult.OK);
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel; };
        }

        // Absolute-positioned card layout → skip ThemedDialog's full-width row resize; just refresh the scrollbar.
        protected override void OnShown(EventArgs e) { try { Body.RelayoutContent(); } catch { } }

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

        private Panel Card(int x, int y, int w, int h, Color fill, Color brd)
        {
            var p = new Panel { Location = new Point(x, y), Size = new Size(w, h), BackColor = fill };
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
