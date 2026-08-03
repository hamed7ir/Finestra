using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace Finestra.Helpers
{
    /// <summary>
    /// Physical display detection for "Native" resolution. Read AFTER SetProcessDpiAwareness (the startup
    /// invariant): in a system-DPI-aware process, <see cref="Screen"/>.Bounds returns PHYSICAL pixels (not the
    /// DPI-scaled/effective size) — which is what the remote desktop should match to fill the panel.
    ///
    /// RT / 8.1 + scaled-display CAVEAT: this is the same OS/DPI-sensitive class as the accent branch — it reads
    /// correctly on x64, but its RT/8.1 correctness (and its behavior when the display is set to a non-native
    /// mode) is CONFIRMED on the device pass, not assumed here. The detected value is tee'd to the log
    /// (<c>[DISPLAY]</c>) so REL-2 can read it on the RT.
    /// </summary>
    public static class DisplayInfo
    {
        /// <summary>Physical resolution of the primary monitor (0×0 if unreadable).</summary>
        public static Size PhysicalPrimary()
        {
            try { var b = Screen.PrimaryScreen.Bounds; return new Size(b.Width, b.Height); }
            catch { return new Size(0, 0); }
        }

        /// <summary>Physical resolution of the monitor containing <paramref name="screenPoint"/> (the fullscreen
        /// host's target monitor); falls back to the primary.</summary>
        public static Size PhysicalAt(Point screenPoint)
        {
            try { var b = Screen.FromPoint(screenPoint).Bounds; return new Size(b.Width, b.Height); }
            catch { return PhysicalPrimary(); }
        }

        private static bool _logged;
        /// <summary>Logs the detected physical primary once via the Debug/Trace tee → the Finestra log.</summary>
        public static void LogOnce()
        {
            if (_logged) return;
            _logged = true;
            var s = PhysicalPrimary();
            Debug.WriteLine("[DISPLAY] physical primary = " + s.Width + "x" + s.Height);
        }
    }
}
