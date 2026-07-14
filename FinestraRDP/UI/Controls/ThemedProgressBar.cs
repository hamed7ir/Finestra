using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Finestra.Helpers;

namespace Finestra.UI.Controls
{
    /// <summary>
    /// FRDP-POLISH-4 — owner-drawn progress bar (dark track, accent fill), replacing the native <see
    /// cref="ProgressBar"/> in the FTP transfer strip. A native ProgressBar renders WHITE in dark theme on
    /// Windows 8.1/RT — <c>SetWindowTheme</c> is a no-op there (the same lesson already learned for
    /// <see cref="ThemedFileList"/>'s list/header/scrollbar), so this is owner-drawn from the start rather than
    /// styled after the fact. Recolors live on theme flip like every other themed control in this codebase.
    /// </summary>
    internal sealed class ThemedProgressBar : Control
    {
        private int _value;

        /// <summary>0-100. Clamped; repaints on change.</summary>
        public int Value
        {
            get => _value;
            set
            {
                int v = Math.Max(0, Math.Min(100, value));
                if (v == _value) return;
                _value = v;
                Invalidate();
            }
        }

        public ThemedProgressBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            ThemeHelper.ThemeChanged += OnTc;
        }

        private void OnTc()
        {
            if (IsDisposed) return;
            if (!IsHandleCreated) { Invalidate(); return; }
            try { BeginInvoke((Action)Invalidate); } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ThemeHelper.ThemeChanged -= OnTc;
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool dark = ThemeHelper.IsDark;
            Color track = dark ? Color.FromArgb(50, 50, 56) : Color.FromArgb(222, 222, 228);
            Color accent = ThemeHelper.GetWindowsAccentColor();
            int r = Math.Min(Height / 2, 6);

            using (var p = DrawHelper.RoundedRect(ClientRectangle, r))
            using (var b = new SolidBrush(track))
                g.FillPath(b, p);

            if (_value > 0)
            {
                int w = Math.Max(0, (int)((long)ClientRectangle.Width * _value / 100));
                if (w > 0)
                {
                    var fillRect = new Rectangle(0, 0, w, Height);
                    // Clip to the track's own rounded outline so the fill never pokes past rounded corners at
                    // low percentages, then draw a plain rect inside — cheaper than a second rounded path per frame.
                    using (var clip = DrawHelper.RoundedRect(ClientRectangle, r))
                    {
                        var oldClip = g.Clip;
                        g.SetClip(clip, CombineMode.Intersect);
                        using (var b = new SolidBrush(accent)) g.FillRectangle(b, fillRect);
                        g.Clip = oldClip;
                    }
                }
            }
        }
    }
}
