using System;
using System.Drawing;
using System.Windows.Forms;

namespace Finestra.UI.Controls
{
    /// <summary>
    /// FIN-KBD-FREEZE-SCROLL — a narrow (<see cref="ClipScrollbar.Gutter"/>-wide) strip that draws and drives the
    /// shared <see cref="ClipScrollbar"/> for a session Finestra does NOT own the pixels of (the embedded RDP
    /// child is a foreign native window — unlike SSH's <see cref="TerminalControl"/>, which paints its own scroll
    /// overlay directly). Sized/positioned by the owner (<c>SessionHost</c>) to sit exactly over the active
    /// session's right edge, and shown only while that session is frozen AND clipped (content taller than the
    /// embed area). The owner is also responsible for keeping this control's z-order ABOVE the native child —
    /// a foreign HWND paints above WinForms siblings by default, so an unmanaged z-order would leave this invisible.
    /// </summary>
    public sealed class ClipPanOverlay : Control
    {
        /// <summary>The pixel height of the frozen content being panned (e.g. the RDP child's actual HWND height).</summary>
        public int ContentHeight;
        /// <summary>Current pan (0 = bottom-anchored, see <see cref="ClipScrollbar"/>'s convention).</summary>
        public int Pan;
        /// <summary>Furthest the content can be panned (0 = nothing to scroll — draws/hit-tests nothing).</summary>
        public int MaxPan;

        /// <summary>Fires when a drag/click changes <see cref="Pan"/> — the owner applies it to the real content
        /// (e.g. repositioning the RDP child) and should also update <see cref="Pan"/> here to match afterward.</summary>
        public event Action<int> PanChanged;

        private bool _dragging, _hover;
        private int _dragGrabDy;

        public ClipPanOverlay()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (MaxPan <= 0) return;
            ClipScrollbar.Draw(e.Graphics, ClientSize, Math.Max(1, ContentHeight), Pan, MaxPan, _hover || _dragging);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (MaxPan <= 0 || e.Button != MouseButtons.Left) return;
            var thumb = ClipScrollbar.ThumbRect(ClientSize, Math.Max(1, ContentHeight), Pan, MaxPan);
            if (thumb.Contains(e.Location)) { _dragging = true; _dragGrabDy = e.Y - thumb.Y; Capture = true; return; }
            if (ClipScrollbar.TrackRect(ClientSize).Contains(e.Location))
                SetPan(e.Y < thumb.Y ? Pan + ClientSize.Height : Pan - ClientSize.Height);   // page toward the click
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging)
            {
                SetPan(ClipScrollbar.PanFromDrag(ClientSize, Math.Max(1, ContentHeight), MaxPan, e.Y, _dragGrabDy));
                return;
            }
            if (MaxPan <= 0) return;
            bool hover = ClipScrollbar.ThumbRect(ClientSize, Math.Max(1, ContentHeight), Pan, MaxPan).Contains(e.Location);
            if (hover != _hover) { _hover = hover; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left) { _dragging = false; Capture = false; }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover) { _hover = false; Invalidate(); }
        }

        private void SetPan(int newPan)
        {
            newPan = Math.Max(0, Math.Min(MaxPan, newPan));
            if (newPan == Pan) return;
            Pan = newPan;
            Invalidate();
            PanChanged?.Invoke(Pan);
        }
    }
}
