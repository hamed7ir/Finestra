using System;
using System.Runtime.InteropServices;
using Finestra.Helpers;

namespace Finestra.Core
{
    /// <summary>
    /// SDL-ENGINE — touch mode vs mouse mode for an RDP session (the SDL engine's per-session switch).
    ///
    /// TOUCH MODE: fingers go to the server as real multi-touch input (MS-RDPEI) — a swipe scrolls, a pinch
    /// zooms, exactly as on a touch PC. MOUSE MODE: Windows turns touch into mouse input, as it did for the
    /// wfreerdp engine's window — a tap clicks, a drag drags, press-and-hold is the right button. A real mouse,
    /// touchpad and keyboard work the same in both modes; the mode only decides what a FINGER means.
    ///
    /// A new session starts in <see cref="AppSettings.RdpInputMode"/>: Auto = touch mode when this machine has a
    /// touch screen. The bar toggle switches the running session and remembers the choice for the next one.
    /// </summary>
    internal static class TouchInput
    {
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        private const int SM_MAXIMUMTOUCHES = 95;   // Windows 7+; 0 when there is no touch digitizer

        /// <summary>Does this machine have a touch screen? Read every time (an external touch monitor can come
        /// and go); the call is a cheap metric read with no UI.</summary>
        public static bool HasTouchScreen()
        {
            try { return GetSystemMetrics(SM_MAXIMUMTOUCHES) > 0; }
            catch { return false; }
        }

        /// <summary>The mode a NEW session starts in.</summary>
        public static bool StartInTouchMode()
        {
            string m = AppSettings.Instance.RdpInputMode ?? "Auto";
            if (m.Equals("Touch", StringComparison.OrdinalIgnoreCase)) return true;
            if (m.Equals("Mouse", StringComparison.OrdinalIgnoreCase)) return false;
            return HasTouchScreen();
        }

        /// <summary>The user switched a session: the next session starts the same way.</summary>
        public static void Remember(bool touch)
        {
            try
            {
                string m = touch ? "Touch" : "Mouse";
                if (string.Equals(AppSettings.Instance.RdpInputMode, m, StringComparison.OrdinalIgnoreCase)) return;
                AppSettings.Instance.RdpInputMode = m;
                AppSettings.Instance.Save();
            }
            catch (Exception ex) { FileLog.Line("[TOUCH] could not remember the input mode: " + ex.Message); }
        }
    }
}
