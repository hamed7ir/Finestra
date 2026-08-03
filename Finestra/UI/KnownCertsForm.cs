using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Finestra.Core;
using Finestra.Helpers;
using Finestra.UI.Controls;

namespace Finestra.UI
{
    /// <summary>
    /// FRDP-POLISH-4 — view / remove the FTPS TOFU <see cref="KnownCerts"/> entries. Mirrors <see
    /// cref="KnownHostsForm"/> exactly: before this, a wrongly-trusted (or since-revoked) FTPS certificate could
    /// never be un-trusted from the UI — the JSON store had no management view at all. Opened from
    /// <see cref="AppSettingsForm"/>.
    /// </summary>
    public sealed class KnownCertsForm : ThemedDialog
    {
        private const int W = 560;

        public KnownCertsForm() : base("Known FTPS certificates", W, 520)
        {
            Rebuild();
            AddFooterButton("Close", RoundedButtonKind.Neutral, DialogResult.Cancel);
        }

        private void Rebuild()
        {
            KnownCerts.Reload();
            var items = KnownCerts.Instance.Items;
            int w = W - 40;
            Body.Host.Controls.Clear();

            if (items.Count == 0)
            {
                PopulateBody(new InfoBlock(w,
                    "No FTPS certificates have been remembered yet.\n\nWhen you accept a server's certificate on " +
                    "first connect, it's stored here so a later change can be flagged.", loud: false));
                return;
            }

            var rows = new List<Control>
            {
                new InfoBlock(w, "These are the FTPS servers whose certificates you've trusted. Remove one to be " +
                                 "prompted fresh next time you connect — use this if a server's certificate was " +
                                 "legitimately renewed, or if one was trusted by mistake.", loud: false)
            };
            foreach (var e in items.OrderBy(x => x.Host).ThenBy(x => x.Port))
            {
                var row = new KnownCertRow(e, w);
                row.RemoveClicked += ent => { KnownCerts.Instance.Remove(ent.Host, ent.Port); Rebuild(); };
                rows.Add(row);
            }
            PopulateBody(rows.ToArray());
        }
    }

    /// <summary>One known-cert line: host:port + subject + fingerprint + first-seen, with a Remove button.</summary>
    internal sealed class KnownCertRow : Control
    {
        private readonly KnownCertEntry _e;
        private readonly RoundedButton _remove;
        public event Action<KnownCertEntry> RemoveClicked;

        public KnownCertRow(KnownCertEntry e, int width)
        {
            _e = e;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Width = width; Height = 68;
            _remove = new RoundedButton { Text = "Remove", Kind = RoundedButtonKind.Danger, Width = 96, Height = 34, Font = FontHelper.Ui(9.5f, FontStyle.Bold) };
            _remove.Click += (s, a) => RemoveClicked?.Invoke(_e);
            Controls.Add(_remove);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            _remove?.SetBounds(Width - 108, (Height - 34) / 2, 96, 34);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            bool dark = ThemeHelper.IsDark;
            Color bg = dark ? Color.FromArgb(32, 32, 36) : Color.FromArgb(245, 245, 248);
            Color fg = dark ? Color.FromArgb(234, 234, 238) : Color.FromArgb(28, 28, 32);
            Color sub = dark ? Color.FromArgb(150, 150, 156) : Color.FromArgb(112, 112, 120);
            g.Clear(bg);
            int rightReserve = 120;
            using (var tf = FontHelper.Ui(11f, FontStyle.Bold))
                TextRenderer.DrawText(g, _e.Host + ":" + _e.Port + "    " + _e.Subject, tf,
                    new Rectangle(10, 6, Width - rightReserve, 20), fg,
                    TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            using (var mf = FontHelper.Mono(8.75f))
                TextRenderer.DrawText(g, _e.Sha256, mf, new Rectangle(10, 28, Width - rightReserve, 18), sub,
                    TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            using (var sf = FontHelper.Ui(8.5f))
                TextRenderer.DrawText(g, "first trusted " + _e.FirstSeen.ToString("yyyy-MM-dd HH:mm"), sf,
                    new Rectangle(10, 47, Width - rightReserve, 16), sub,
                    TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            using (var p = new Pen(dark ? Color.FromArgb(52, 52, 58) : Color.FromArgb(224, 224, 230)))
                g.DrawLine(p, 8, Height - 1, Width - 8, Height - 1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _remove?.Dispose();
            base.Dispose(disposing);
        }
    }
}
