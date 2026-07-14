using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using Finestra.Helpers;

namespace Finestra.UI.Controls
{
    /// <summary>
    /// FIN-KBD-FREEZE-SCROLL — shared geometry/draw/drag math for the "pan a frozen-but-clipped session" scrollbar,
    /// used by <see cref="TerminalControl"/> (SSH, drawn as an inset overlay on the terminal's own paint) and
    /// <see cref="ClipPanOverlay"/> (RDP, a standalone narrow-strip control — the RDP child is a foreign native
    /// window Finestra can't paint into directly). One place for the geometry/colors/drag math so both stay
    /// pixel-identical and a future third consumer doesn't reinvent it.
    ///
    /// Convention: pan=0 is BOTTOM-anchored (shows the end of the content — the SSH cursor/prompt, or whatever's
    /// most likely relevant); pan=maxPan is TOP-anchored. Callers pass a "clientSize" that is either the FULL host
    /// control's size (the scrollbar draws as a right-edge inset, e.g. TerminalControl) or exactly the gutter width
    /// (the scrollbar draws as the control's own full bounds, e.g. a dedicated narrow overlay) — both work with the
    /// same math since TrackRect insets by exactly <see cref="Gutter"/> either way.
    /// </summary>
    internal static class ClipScrollbar
    {
        public const int Gutter = 12, ThumbW = 8, MinThumb = 28;

        public static int ThumbHeight(Size clientSize, int contentH)
        {
            int viewH = Math.Max(1, clientSize.Height);
            return Math.Min(viewH, Math.Max(MinThumb, (int)((long)viewH * viewH / Math.Max(1, contentH))));
        }

        public static Rectangle TrackRect(Size clientSize) => new Rectangle(clientSize.Width - Gutter, 0, Gutter, clientSize.Height);

        public static Rectangle ThumbRect(Size clientSize, int contentH, int pan, int maxPan)
        {
            int viewH = Math.Max(1, clientSize.Height);
            int thumbH = ThumbHeight(clientSize, contentH);
            int clamped = Math.Max(0, Math.Min(maxPan, pan));
            float frac = maxPan > 0 ? 1f - (float)clamped / maxPan : 0f;   // 0 = viewing top, 1 = viewing bottom
            int thumbY = (int)(frac * (viewH - thumbH));
            return new Rectangle(clientSize.Width - Gutter + (Gutter - ThumbW) / 2, thumbY, ThumbW, thumbH);
        }

        /// <summary>Given a drag's current mouse Y and the offset it grabbed the thumb at, the new pan value
        /// (unclamped — callers clamp to [0, maxPan] since that's also needed on the non-drag paths).</summary>
        public static int PanFromDrag(Size clientSize, int contentH, int maxPan, int mouseY, int grabDy)
        {
            int thumbH = ThumbHeight(clientSize, contentH);
            int denom = Math.Max(1, clientSize.Height - thumbH);
            float frac = Math.Max(0f, Math.Min(1f, (float)(mouseY - grabDy) / denom));
            return (int)((1f - frac) * maxPan);
        }

        /// <summary>Paints the track + thumb. Caller is responsible for only invoking this when there's actually
        /// something to scroll (maxPan &gt; 0) — this doesn't check.</summary>
        public static void Draw(Graphics g, Size clientSize, int contentH, int pan, int maxPan, bool hoverOrDrag)
        {
            bool dark = ThemeHelper.IsDark;
            Color trackColor = dark ? Color.FromArgb(48, 48, 52) : Color.FromArgb(228, 228, 232);
            Color thumbColor = hoverOrDrag ? ThemeHelper.GetWindowsAccentColor()
                : (dark ? Color.FromArgb(96, 96, 102) : Color.FromArgb(176, 176, 182));
            var oldSmoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var trackRect = new Rectangle(clientSize.Width - Gutter + (Gutter - ThumbW) / 2, 2, ThumbW, clientSize.Height - 4);
            using (var trackBrush = new SolidBrush(trackColor))
            using (var trackPath = DrawHelper.RoundedRect(trackRect, ThumbW / 2))
                g.FillPath(trackBrush, trackPath);
            using (var thumbBrush = new SolidBrush(thumbColor))
            using (var thumbPath = DrawHelper.RoundedRect(ThumbRect(clientSize, contentH, pan, maxPan), ThumbW / 2))
                g.FillPath(thumbBrush, thumbPath);
            g.SmoothingMode = oldSmoothing;
        }
    }
}
