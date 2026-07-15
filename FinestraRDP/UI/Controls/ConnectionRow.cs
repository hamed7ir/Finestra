using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Finestra.Core;
using Finestra.Helpers;
using Finestra.UI;

namespace Finestra.UI.Controls
{
    /// <summary>
    /// One connection in the list: a themed rounded card showing the name + target, with Connect / Edit /
    /// Delete actions (Delete is Danger-red, never accent). Colors follow <see cref="ThemeHelper"/> and recolor
    /// live. Owner-painted card so it looks identical on RT 8.1 and 10/11.
    /// </summary>
    public sealed class ConnectionRow : Control
    {
        public ConnectionProfile Profile { get; }
        public event Action<ConnectionProfile> ConnectClicked;
        public event Action<ConnectionProfile> EditClicked;
        public event Action<ConnectionProfile> DeleteClicked;
        /// <summary>FRDP-POLISH-4 — right-click → Duplicate.</summary>
        public event Action<ConnectionProfile> DuplicateClicked;

        private readonly RoundedButton _connect, _edit, _delete, _copy;

        public ConnectionRow(ConnectionProfile cp)
        {
            Profile = cp;
            Height = 78;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            // RoundedButton clears its own corners to Parent.BackColor so they blend into whatever's actually
            // behind them (see RoundedButton.OnPaint). This row paints on TWO layers — an outer page background,
            // then an inset rounded "card" surface the buttons actually sit on (OnPaint below) — and BackColor
            // was never set at all, so children reading Parent.BackColor got Control's default instead of either
            // layer: the buttons' corners rendered as visibly mismatched squares. Must track the CARD color, not
            // the (darker) outer one, since that's the surface the buttons visually sit on.
            BackColor = CardColor(ThemeHelper.IsDark);

            _connect = Btn("Connect", RoundedButtonKind.Primary, () => ConnectClicked?.Invoke(Profile));
            _edit = Btn("Edit", RoundedButtonKind.Neutral, () => EditClicked?.Invoke(Profile));
            _delete = Btn("Delete", RoundedButtonKind.Danger, () => DeleteClicked?.Invoke(Profile));
            // FRDP — Duplicate previously lived ONLY on the right-click menu below, which touch users (no
            // right-click gesture) have no way to reach — the same discoverability gap already solved for SSH
            // tabs via a visible "⋮" glyph. A real button here is more consistent with this row's OWN existing
            // architecture (RoundedButton child controls, not SessionTabBar's manual hit-test rectangles).
            _copy = Btn("Copy", RoundedButtonKind.Neutral, () => DuplicateClicked?.Invoke(Profile));
            Controls.Add(_connect);
            Controls.Add(_edit);
            Controls.Add(_delete);
            Controls.Add(_copy);

            ThemeHelper.ThemeChanged += OnTc;
            MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) ShowContextMenu(PointToScreen(e.Location)); };
        }

        private void ShowContextMenu(Point screen)
        {
            var menu = new ThemedContextMenuStrip { Font = FontHelper.Ui(9.5f) };
            menu.Items.Add(new ToolStripMenuItem("Duplicate", null, (s, e) => DuplicateClicked?.Invoke(Profile)));
            menu.Show(screen);
        }

        private RoundedButton Btn(string text, RoundedButtonKind kind, Action onClick)
        {
            var b = new RoundedButton { Text = text, Kind = kind, Height = 34, Font = FontHelper.Ui(9.5f, FontStyle.Bold) };
            b.Click += (s, e) => onClick();
            return b;
        }

        /// <summary>The single source of truth for the card surface color — OnPaint's fill AND BackColor (so
        /// child RoundedButtons' corner-clear matches it) both derive from here, never duplicated.</summary>
        private static Color CardColor(bool dark) => dark ? Color.FromArgb(44, 44, 50) : Color.White;

        private void OnTc()
        {
            if (IsDisposed) return;
            Color card = CardColor(ThemeHelper.IsDark);
            // A handle-less control has nothing to marshal BeginInvoke through anyway — set directly (matches the
            // fix already applied to ThemedContextMenuStrip's identical BeginInvoke-throws-when-no-handle gap).
            if (!IsHandleCreated) { BackColor = card; return; }
            try { BeginInvoke((Action)(() => { BackColor = card; Invalidate(); })); } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ThemeHelper.ThemeChanged -= OnTc;
            base.Dispose(disposing);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_connect == null || _edit == null || _delete == null || _copy == null) return;   // Height set in ctor fires layout early
            const int m = 10;
            int cw = 96, ew = 66, dw = 72, pw = 66, gap = 8;
            int cy = (Height - 34) / 2;
            int x = Width - m - cw;
            _connect.SetBounds(x, cy, cw, 34);
            x -= gap + ew; _edit.SetBounds(x, cy, ew, 34);
            x -= gap + dw; _delete.SetBounds(x, cy, dw, 34);
            x -= gap + pw; _copy.SetBounds(x, cy, pw, 34);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool dark = ThemeHelper.IsDark;
            Color pageBg = dark ? Color.FromArgb(32, 32, 36) : Color.FromArgb(245, 245, 248);
            Color card = CardColor(dark);
            Color fg = dark ? Color.FromArgb(234, 234, 238) : Color.FromArgb(28, 28, 32);
            Color sub = dark ? Color.FromArgb(150, 150, 156) : Color.FromArgb(112, 112, 120);

            g.Clear(pageBg);
            var card_r = new Rectangle(2, 2, Math.Max(1, Width - 4), Height - 6);
            using (var b = new SolidBrush(card))
            using (var p = DrawHelper.RoundedRect(card_r, 10)) g.FillPath(b, p);

            // per-type glyph tile — size derives from the row height (DPI-safe), cache-owned bitmap
            int tile = Math.Max(24, Height - 38);
            var glyph = TypeGlyph.Get(TypeGlyph.KindOf(Profile.Type), tile);
            g.DrawImageUnscaled(glyph, 14, card_r.Top + (card_r.Height - tile) / 2);

            int textLeft = 14 + tile + 12;
            int textRight = _copy.Left - 12;
            var nameR = new Rectangle(textLeft, 12, Math.Max(1, textRight - textLeft), 24);
            var subR = new Rectangle(textLeft, 38, Math.Max(1, textRight - textLeft), 22);
            using (var nf = FontHelper.Ui(12f, FontStyle.Bold))
                TextRenderer.DrawText(g, string.IsNullOrEmpty(Profile.Name) ? Profile.Host : Profile.Name, nf, nameR, fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            using (var sf = FontHelper.Ui(9.5f))
                TextRenderer.DrawText(g, Profile.DisplayTarget, sf, subR, sub,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }
}
