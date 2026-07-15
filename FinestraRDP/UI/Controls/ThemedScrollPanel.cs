using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
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
    /// panel when the cursor is over it); the thumb is draggable and the track is click-to-page. Dragging directly
    /// over the CONTENT (any row, not just the thumb) also pans — this is what makes it usable on a touchscreen,
    /// where hitting an 8px-wide thumb with a finger isn't realistic. Windows promotes single-finger touch drags
    /// into ordinary mouse messages automatically, so this needs no WM_TOUCH/WM_GESTURE handling of its own — a
    /// small movement threshold distinguishes an intentional pan from a tap/click on a row's own control.
    ///
    /// FIN-DLG-SCROLL-2 — wheel/drag targeting is a real TOP-MOST hit-test (<see cref="CursorOverThisPanel"/>),
    /// not just "is the cursor inside my rectangle". The message filter is app-wide and runs one instance per
    /// panel in registration order, so a panel whose rectangle merely sits UNDER a stacked dialog (the connection
    /// editor's body under its nested "Advanced settings" dialog; the main list under any dialog) would otherwise
    /// steal the wheel/drag and scroll itself behind the dialog on top. The hit-test fixes that: only the panel
    /// actually under the pointer (same form as the window under the cursor) responds. A content drag that starts
    /// on a text field pans only when the drag is clearly VERTICAL — a mouse's horizontal drag-select is preserved,
    /// while a finger/pen (always a near-vertical scroll here) pans the textbox-dense editor/settings forms too.
    /// </summary>
    public sealed class ThemedScrollPanel : Panel, IMessageFilter
    {
        private const int Gutter = 12;      // right-edge scrollbar lane width
        private const int ThumbW = 8;
        private const int MinThumb = 28;
        private const int WheelStep = 54;
        private const int DragThreshold = 12;   // px of movement before a content press is treated as a pan, not a click

        private readonly Panel _view;       // the scrolled content surface; callers add to this
        private int _scroll;                // current offset [0.._max]
        private int _max;                   // scrollable range (contentHeight - viewport), >= 0
        private bool _thumbHover, _thumbDrag;
        private int _dragGrabDy;
        private bool _filtering;

        // content-area drag-to-pan (mouse OR touch, via the same message filter that routes wheel)
        private bool _contentDragCandidate, _contentDragConfirmed, _contentDragOverTextBox;
        private int _contentDragStartX, _contentDragStartY, _contentDragStartScroll;

        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

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

        // wheel over ANY child: route to this panel when the cursor is genuinely over IT (top-most hit-test,
        // not just inside its rectangle — see CursorOverThisPanel). Also drives content-area drag-to-pan (mouse
        // OR touch — Windows promotes single-finger touch into these same messages) for presses that start on a
        // row but aren't over the thumb/track, which the panel's own OnMouseDown/Move/Up already own.
        public bool PreFilterMessage(ref Message m)
        {
            const int WM_MOUSEWHEEL = 0x020A;
            const int WM_LBUTTONDOWN = 0x0201;
            const int WM_MOUSEMOVE = 0x0200;
            const int WM_LBUTTONUP = 0x0202;
            const int MK_LBUTTON = 0x0001;

            if (!IsHandleCreated || !Visible) return false;

            if (m.Msg == WM_MOUSEWHEEL)
            {
                if (_max <= 0) return false;                      // nothing to scroll → let another panel try
                try
                {
                    if (!CursorOverThisPanel()) return false;    // a panel merely UNDER a stacked dialog must not steal it
                    int delta = (short)((long)m.WParam >> 16);
                    ScrollTo(_scroll - (delta / 120) * WheelStep);
                    return true;                                 // consumed
                }
                catch { return false; }
            }

            if (m.Msg == WM_LBUTTONDOWN)
            {
                _contentDragCandidate = false;
                _contentDragConfirmed = false;
                if (_max <= 0 || m.HWnd == Handle) return false;   // panel's own thumb/track click: unrelated
                try
                {
                    if (!CursorOverThisPanel()) return false;      // press belongs to whatever dialog is really on top
                    // A press landing on a text field keeps click-drag text SELECTION for a MOUSE: such a press
                    // only pans on a clearly-vertical drag (decided on move, below). Touch/pen never drag-select,
                    // and their scroll is always near-vertical, so this still pans the textbox-dense forms by finger.
                    _contentDragOverTextBox = Control.FromChildHandle(m.HWnd) is TextBoxBase;
                    _contentDragCandidate = true;
                    _contentDragStartX = Cursor.Position.X;
                    _contentDragStartY = Cursor.Position.Y;
                    _contentDragStartScroll = _scroll;
                    return false;   // don't consume — a plain tap/click must still reach the row
                }
                catch { return false; }
            }

            if (m.Msg == WM_MOUSEMOVE)
            {
                if (!_contentDragCandidate) return false;
                if (((long)m.WParam & MK_LBUTTON) == 0) { _contentDragCandidate = false; _contentDragConfirmed = false; return false; }
                int dy = Cursor.Position.Y - _contentDragStartY;
                if (!_contentDragConfirmed)
                {
                    if (Math.Abs(dy) < DragThreshold) return false;
                    // Over a text field, require a clearly VERTICAL drag so a mouse's horizontal drag-select is left
                    // to the box; a finger/pen scroll is near-vertical and passes this, so touch pans it anyway.
                    if (_contentDragOverTextBox && Math.Abs(dy) <= Math.Abs(Cursor.Position.X - _contentDragStartX)) return false;
                    _contentDragConfirmed = true;   // crossed the slop threshold: this is a pan, not a click
                    ReleaseCapture();               // free a text box/button that grabbed the mouse on the press → smooth pan
                }
                ScrollTo(_contentDragStartScroll - dy);
                return true;   // consumed — the row underneath shouldn't also react to this move
            }

            if (m.Msg == WM_LBUTTONUP)
            {
                bool wasConfirmed = _contentDragConfirmed;
                _contentDragCandidate = false;
                _contentDragConfirmed = false;
                return wasConfirmed;   // swallow the "release" only if we actually panned, so a real tap still clicks
            }

            return false;
        }

        /// <summary>True only when the cursor is over THIS panel's own client area AND the TOP-MOST window under
        /// the cursor belongs to the SAME form as this panel. The second half is the fix for stacked dialogs: the
        /// app-wide filter runs one instance per panel in registration order, so a panel whose screen rectangle
        /// merely sits UNDER a dialog on top (the connection editor's body under its nested "Advanced settings"
        /// dialog; the main list under any dialog) would otherwise consume the wheel/drag and scroll itself behind
        /// the dialog. WindowFromPoint gives the real top-most target, so only the panel actually under the pointer
        /// responds.</summary>
        private bool CursorOverThisPanel()
        {
            Point sp = Cursor.Position;
            if (!RectangleToScreen(ClientRectangle).Contains(sp)) return false;
            Form myForm = FindForm();
            if (myForm == null) return true;   // not hosted on a form (shouldn't happen while filtering) — rect test above stands
            POINT p; p.X = sp.X; p.Y = sp.Y;
            Control top = Control.FromChildHandle(WindowFromPoint(p));
            return top != null && top.FindForm() == myForm;
        }
    }
}
