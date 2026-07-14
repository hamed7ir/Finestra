using System;
using System.Drawing;
using System.Windows.Forms;
using Finestra.Helpers;

namespace Finestra.UI
{
    /// <summary>
    /// A ContextMenuStrip themed entirely from <see cref="ThemeHelper"/> — background/text follow dark/light,
    /// the hover/selected highlight is the Windows accent (8.1 AND 10/11, via ThemeHelper's OS-branched read),
    /// separators/borders are derived shades. Re-themes live on <see cref="ThemeHelper.ThemeChanged"/>; rows are
    /// touch-sized. (TelegArm's emoji/Persian menu rendering is intentionally omitted — not needed here.)
    /// </summary>
    public class ThemedContextMenuStrip : ContextMenuStrip
    {
        private static readonly Padding RowPad = new Padding(4, 7, 14, 7);   // taller/wider rows for touch

        static ThemedContextMenuStrip()
        {
            // One renderer for ALL menus + their sub-menus (ManagerRenderMode is the default).
            ToolStripManager.Renderer = new ThemedMenuRenderer();
        }

        public ThemedContextMenuStrip()
        {
            RenderMode = ToolStripRenderMode.ManagerRenderMode;
            BackColor = ThemedMenuColors.Background;
            ForeColor = ThemedMenuColors.Text;
            ThemeHelper.ThemeChanged += OnThemeChanged;
            Opening += (s, e) => TouchSize(this);
        }

        private static void TouchSize(ToolStrip strip)
        {
            if (strip == null) return;
            foreach (ToolStripItem it in strip.Items)
            {
                var mi = it as ToolStripMenuItem;
                if (mi == null) continue;
                mi.Padding = RowPad;
                if (mi.HasDropDownItems) TouchSize(mi.DropDown);
            }
        }

        private void OnThemeChanged()
        {
            if (IsDisposed) return;
            try
            {
                // A ContextMenuStrip only HAS a handle while actually popped up — closed (the common case; a
                // tray/tab menu isn't usually held open while the OS theme flips) means BeginInvoke throws
                // "cannot call BeginInvoke... until the window handle has been created", which the old code
                // swallowed silently, leaving BackColor/ForeColor stuck at whatever they were when this instance
                // was constructed — forever, since nothing else ever refreshes them. No handle means nothing to
                // marshal through anyway, so just set directly; the next Show() then already reflects the theme.
                if (IsHandleCreated) BeginInvoke((Action)ApplyThemeColors);
                else ApplyThemeColors();
            }
            catch { }
        }

        private void ApplyThemeColors()
        {
            if (IsDisposed) return;
            BackColor = ThemedMenuColors.Background;
            ForeColor = ThemedMenuColors.Text;
            try { Invalidate(true); } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ThemeHelper.ThemeChanged -= OnThemeChanged;
            base.Dispose(disposing);
        }
    }

    /// <summary>Menu palette derived from ThemeHelper (no hard-coded theme colors).</summary>
    internal static class ThemedMenuColors
    {
        public static bool Dark { get { return ThemeHelper.IsDark; } }
        public static Color Background { get { return Dark ? Color.FromArgb(43, 43, 46) : Color.FromArgb(245, 245, 247); } }
        public static Color Text { get { return Dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(25, 25, 25); } }
        public static Color Disabled { get { return Dark ? Color.FromArgb(120, 120, 120) : Color.FromArgb(165, 165, 165); } }
        public static Color Accent { get { return ThemeHelper.GetWindowsAccentColor(); } }
        public static Color Highlight { get { return Blend(Accent, Background, Dark ? 0.42f : 0.28f); } }
        public static Color Border { get { return Blend(Background, Dark ? Color.White : Color.Black, 0.16f); } }

        public static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R * t + b.R * (1 - t)), (int)(a.G * t + b.G * (1 - t)), (int)(a.B * t + b.B * (1 - t)));
        }
    }

    /// <summary>ProfessionalColorTable whose colors all come from <see cref="ThemedMenuColors"/>.</summary>
    internal sealed class ThemedColorTable : ProfessionalColorTable
    {
        public ThemedColorTable() { UseSystemColors = false; }
        public override Color ToolStripDropDownBackground { get { return ThemedMenuColors.Background; } }
        public override Color ImageMarginGradientBegin { get { return ThemedMenuColors.Background; } }
        public override Color ImageMarginGradientMiddle { get { return ThemedMenuColors.Background; } }
        public override Color ImageMarginGradientEnd { get { return ThemedMenuColors.Background; } }
        public override Color MenuBorder { get { return ThemedMenuColors.Border; } }
        public override Color MenuItemBorder { get { return ThemedMenuColors.Accent; } }
        public override Color MenuItemSelected { get { return ThemedMenuColors.Highlight; } }
        public override Color MenuItemSelectedGradientBegin { get { return ThemedMenuColors.Highlight; } }
        public override Color MenuItemSelectedGradientEnd { get { return ThemedMenuColors.Highlight; } }
        public override Color MenuItemPressedGradientBegin { get { return ThemedMenuColors.Highlight; } }
        public override Color MenuItemPressedGradientEnd { get { return ThemedMenuColors.Highlight; } }
        public override Color SeparatorDark { get { return ThemedMenuColors.Border; } }
        public override Color SeparatorLight { get { return ThemedMenuColors.Border; } }
        public override Color CheckBackground { get { return ThemedMenuColors.Highlight; } }
        public override Color CheckSelectedBackground { get { return ThemedMenuColors.Highlight; } }
        public override Color CheckPressedBackground { get { return ThemedMenuColors.Highlight; } }
    }

    /// <summary>Renderer that paints item text in the themed color (and dims disabled items).</summary>
    internal sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
    {
        public ThemedMenuRenderer() : base(new ThemedColorTable()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? ThemedMenuColors.Text : ThemedMenuColors.Disabled;
            base.OnRenderItemText(e);
        }
    }
}
