using System;
using System.Drawing;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Finestra.Helpers
{
    /// <summary>
    /// Central font provider. Bundles Roboto (UI) as an embedded font loaded into a process-wide
    /// <see cref="PrivateFontCollection"/>, so the app looks the same on any machine without
    /// installing fonts (works on RT â€” private-loaded, not system). Every call returns a fresh
    /// <see cref="Font"/> (callers may dispose it); the family stays alive for the app's life.
    /// Any load failure falls back to "Segoe UI" so the UI never breaks.
    /// </summary>
    public static class FontHelper
    {
        [DllImport("gdi32.dll")]
        private static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, out uint pcFonts);

        private static readonly PrivateFontCollection _pfc = new PrivateFontCollection();
        private static FontFamily _ui;
        private static FontFamily _mono;

        static FontHelper()
        {
            try
            {
                AddFont("Finestra.Fonts.Roboto-Regular.ttf");
                AddFont("Finestra.Fonts.Roboto-Medium.ttf");
                AddFont("Finestra.Fonts.Roboto-Bold.ttf");

                _ui = Find("Roboto");
            }
            catch { /* fall back to Segoe UI below */ }
        }

        /// <summary>A Roboto (UI) font, or Segoe UI if unavailable.</summary>
        public static Font Ui(float size, FontStyle style = FontStyle.Regular) => Make(_ui, size, style);

        /// <summary>A MONOSPACE font for the SSH terminal grid — the first installed of Consolas / Cascadia /
        /// Lucida Console / Courier New, else the generic monospace family. Consolas ships in-box on Windows
        /// (incl. RT 8.1), so the grid metrics are stable across devices. Every cell is exactly one advance wide.</summary>
        public static Font Mono(float size, FontStyle style = FontStyle.Regular)
        {
            if (_mono == null)
            {
                foreach (var name in new[] { "Consolas", "Cascadia Mono", "Cascadia Code", "Lucida Console", "Courier New" })
                    try { var fam = new FontFamily(name); if (fam.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { _mono = fam; break; } } catch { }
                if (_mono == null) _mono = FontFamily.GenericMonospace;
            }
            try { return new Font(_mono, size, style, GraphicsUnit.Point); }
            catch { return new Font(FontFamily.GenericMonospace, size, style); }
        }

        private static Font Make(FontFamily family, float size, FontStyle style)
        {
            try { if (family != null) return new Font(family, size, style); } catch { }
            try { if (family != null) return new Font(family, size, FontStyle.Regular); } catch { }
            return new Font("Segoe UI", size, style);
        }

        private static void AddFont(string resourceName)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (var s = asm.GetManifestResourceStream(resourceName))
                {
                    if (s == null) return;
                    var data = new byte[s.Length];
                    int read = 0;
                    while (read < data.Length)
                    {
                        int n = s.Read(data, read, data.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    // GDI+ does not copy the buffer â€” it must stay allocated for the app's
                    // lifetime (the static collection), so we intentionally never free it.
                    IntPtr p = Marshal.AllocCoTaskMem(data.Length);
                    Marshal.Copy(data, 0, p, data.Length);
                    _pfc.AddMemoryFont(p, data.Length);   // GDI+ (Graphics.DrawString)
                    // ALSO register with GDI so TextRenderer/GDI can see the font WITHOUT installing it â€”
                    // the app's Labels/controls render with UseCompatibleTextRendering=false (GDI TextRenderer),
                    // which otherwise resolves `new Font("Roboto",â€¦)` to a SYSTEM substitute. Shares the same
                    // never-freed buffer. RT-safe (gdi32 present).
                    try { uint _c; AddFontMemResourceEx(p, (uint)data.Length, IntPtr.Zero, out _c); } catch { }
                }
            }
            catch { }
        }

        private static FontFamily Find(string namePart)
        {
            foreach (var f in _pfc.Families)
                if (f.Name.IndexOf(namePart, StringComparison.OrdinalIgnoreCase) >= 0) return f;
            return null;
        }
    }
}
