using System;
using System.Drawing;

namespace Finestra.Helpers
{
    /// <summary>
    /// The Windows display scale the UI is drawn at: 1 at 100%, 1.5 at 150%. Fonts are in points, so they grow
    /// with it on their own; a pixel layout constant does not, and has to be multiplied by it, or the text runs
    /// into the next control. Read once, after SetProcessDpiAwareness (the startup invariant). In a DPI-unaware
    /// process (the "Proportional scaling" setting) it is 1, because Windows stretches the whole window instead.
    /// Graphics.DpiY rather than Control.DeviceDpi, which RT 8.1's .NET does not have.
    /// </summary>
    public static class UiScale
    {
        private static float _factor;

        public static float Factor
        {
            get
            {
                if (_factor == 0f)
                {
                    try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) _factor = Math.Max(1f, g.DpiY / 96f); }
                    catch { _factor = 1f; }
                }
                return _factor;
            }
        }

        /// <summary>A 100%-scale pixel length at the current scale. Exactly <paramref name="px"/> at 100%.</summary>
        public static int Px(int px) => (int)Math.Round(px * Factor);
    }
}
