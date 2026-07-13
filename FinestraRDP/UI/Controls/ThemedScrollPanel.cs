using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Finestra.Helpers;

namespace Finestra.UI.Controls
{
    /// <summary>
    /// A vertically scrollable container with an OWNER-DRAWN scrollbar (custom-painted thumb + track), themed
    /// from <see cref="ThemeHelper"/>. Unlike SetWindowTheme (which only styles native scrollbars on Win10 1809+
    /// and is a no-op on RT 8.1), this control paints its own scrollbar, so the look is IDENTICAL on Windows 8.1
    /// / RT and on 10/11 — required for a consistent UI across every target.
    ///
    /// Usage: add child controls to <see cref="Host"/>, then call <see cref="RelayoutContent"/> (or set
    /// <see cref="Host"/>.Height). The wheel works over any child (an app-wide message filter routes wheel to the
    /// panel when the cursor is over it); the thumb is draggable and the track is click-to-page.
    /// </summary>
    public sealed class ThemedScrollPanel : Panel, IMessageFilter
    {
        private const int Gutter = 12;      // right-edge scrollbar lane width
        private const int ThumbW = 8;
        private const int MinThumb = 28;
        private const int WheelStep = 54;

        private readonly Panel _view;       // the scrolled content surface; callers add to this
        private int _scroll;                // current offset [0.._max]
        private int _max;                   // scrollable range (contentHeight - viewport), >= 0
        private bool _thumbHover, _thumbDrag;
        private int _dragGrabDy;
        private bool _filtering;

        public Panel Host => _view;

        public ThemedScrollPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            _view = new Panel { Location = new Point(0, 0), Height = 0 };
            Controls.Add(_view);
            ThemeHelper.ThemeChanged += OnThemeChanged;
        }

        private void OnThemeChanged()
        {
            if (IsDisposed) return;
            try { BeginInvoke((Action)(() => { ApplyColors(); Invalidate(); _view.Invalidate(true); })); } catch { }
        }

        private void ApplyColors()
        {
            bool dark = ThemeHelper.IsDark;
            Color bg = dark ? Color.FromArgb(32, 32, 36) : Color.FromArgb(245, 245, 248);
            BackColor = bg;
            _view.BackColor = bg;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyColors();
            if (!_filtering) { Application.AddMessageFilter(this); _filtering = true; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ThemeHelper.ThemeChanged -= OnThemeChanged;
                if (_filtering) { Application.RemoveMessageFilter(this); _filtering = false; }
            }
            base.Dispose(disposing);
        }

        /// <summary>Recomputes content height from the tallest child, fits the view width to the gutter, and
        /// refreshes the scrollbar. Call after adding/removing/resizing children.</summary>
        public void RelayoutContent()
        {
            int contentH = 0;
            foreach (Control c in _view.Controls) contentH = Math.Max(contentH, c.Bottom);
            _view.Height = contentH;
            LayoutView();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            LayoutView();
        }

        private void LayoutView()
        {
            if (_view == null) return;   // guard early layout during construction
            int viewport = ClientSize.Height;
            _view.Width = Math.Max(0, ClientSize.Width - Gutter);
            _max = Math.Max(0, _view.Height - viewport);
            if (_scroll > _max) _scroll = _max;
            if (_scroll < 0) _scroll = 0;
            _view.Top = -_scroll;
            Invalidate();
        }

        private void ScrollTo(int value)
        {
            int v = Math.Max(0, Math.Min(_max, value));
            if (v == _scroll) return;
            _scroll = v;
            _view.Top = -_scroll;
            Invalidate();
        }

        /// <summary>Scroll the minimum needed so the content-coordinate range [y, y+h] is visible (for keyboard
        /// navigation in a hosted list — FRDP-FTP-POLISH).</summary>
        public void EnsureVisible(int y, int h)
        {
            int viewport = ClientSize.Height;
            if (y < _scroll) ScrollTo(y);
            else if (y + h > _scroll + viewport) ScrollTo(y + h - viewport);
        }

        // ── owner-drawn scrollbar ──────────────────────────────────────────────
        private Rectangle TrackRect() => new Rectangle(ClientSize.Width - Gutter, 0, Gutter, ClientSize.Height);

        private Rectangle ThumbRect()
        {
            if (_max <= 0) return Rectangle.Empty;
            int viewport = ClientSize.Height;
            int content = _view.Height;
            int trackH = viewport;
            int thumbH = Math.Max(MinThumb, (int)((long)viewport * viewport / Math.Max(1, content)));
            thumbH = Math.Min(thumbH, trackH);
            int y = (int)((long)_scroll * (trackH - thumbH) / _max);
            int x = ClientSize.Width - Gutter + (Gutter - ThumbW) / 2;
            return new Rectangle(x, y, ThumbW, thumbH);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_max <= 0) return;   // nothing to scroll → no scrollbar
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool dark = ThemeHelper.IsDark;
            Color track = dark ? Color.FromArgb(48, 48, 52) : Color.FromArgb(228, 228, 232);
            Color thumb = _thumbHover || _thumbDrag
                ? ThemeHelper.GetWindowsAccentColor()
                : (dark ? Color.FromArgb(96, 96, 102) : Color.FromArgb(176, 176, 182));

            var tr = TrackRect();
            using (var tb = new SolidBrush(track))
            using (var tp = DrawHelper.RoundedRect(new Rectangle(tr.X + (Gutter - ThumbW) / 2, 2, ThumbW, tr.Height - 4), ThumbW / 2))
                g.FillPath(tb, tp);

            var th = ThumbRect();
            if (th != Rectangle.Empty)
                using (var b = new SolidBrush(thumb))
                using (var p = DrawHelper.RoundedRect(th, ThumbW / 2))
                    g.FillPath(b, p);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_max <= 0) return;
            var th = ThumbRect();
            if (th.Contains(e.Location)) { _thumbDrag = true; _dragGrabDy = e.Y - th.Y; Capture = true; }
            else if (TrackRect().Contains(e.Location))
                ScrollTo(_scroll + (e.Y < th.Y ? -ClientSize.Height : ClientSize.Height));   // page
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_thumbDrag)
            {
                int trackH = ClientSize.Height;
                int thumbH = ThumbRect().Height;
                int denom = Math.Max(1, trackH - thumbH);
                ScrollTo((int)((long)(e.Y - _dragGrabDy) * _max / denom));
                return;
            }
            bool hover = _max > 0 && ThumbRect().Contains(e.Location);
            if (hover != _thumbHover) { _thumbHover = hover; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_thumbDrag) { _thumbDrag = false; Capture = false; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_thumbHover) { _thumbHover = false; Invalidate(); }
        }

        // wheel over ANY child: route to this panel when the cursor is within our bounds
        public bool PreFilterMessage(ref Message m)
        {
            const int WM_MOUSEWHEEL = 0x020A;
            if (m.Msg != WM_MOUSEWHEEL || _max <= 0 || !IsHandleCreated || !Visible) return false;
            try
            {
                if (!RectangleToScreen(ClientRectangle).Contains(Cursor.Position)) return false;
                int delta = (short)((long)m.WParam >> 16);
                ScrollTo(_scroll - (delta / 120) * WheelStep);
                return true;   // consumed
            }
            catch { return false; }
        }
    }
}
