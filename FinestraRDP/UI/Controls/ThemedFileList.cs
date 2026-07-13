using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Finestra.Core;
using Finestra.Helpers;

namespace Finestra.UI.Controls
{
    /// <summary>
    /// FRDP-FTP-POLISH — a fully OWNER-DRAWN file list (glyph · Name · Size · Modified · Type), replacing the native
    /// <see cref="ListView"/> in the FTP panes. It reuses the app's owner-drawn <see cref="ThemedScrollPanel"/> for
    /// the scrollbar and owner-draws its own fixed header + rows + folder/file glyphs — so the look is IDENTICAL on
    /// Windows 8.1 / RT and on 10/11 (no <c>SetWindowTheme</c> dependency, no native scrollbar/header, no white
    /// anywhere). Rendering only; the pane's navigate/context/transfer logic is unchanged.
    /// </summary>
    internal sealed class ThemedFileList : Panel
    {
        private const int RowH = 24, HeaderH = 26, Gutter = 12;   // Gutter matches ThemedScrollPanel's scrollbar lane

        private readonly Panel _header;
        private readonly ThemedScrollPanel _scroll;
        private readonly RowsSurface _rows;

        public event Action SelectionChanged;
        public event Action<RemoteEntry> ItemActivated;   // double-click / Enter
        public event Action<Point> ContextRequested;      // right-click → screen point
        public event Action ReloadRequested;              // F5

        public ThemedFileList()
        {
            DoubleBuffered = true;
            _header = new Panel { Dock = DockStyle.Top, Height = HeaderH };
            _header.Paint += HeaderPaint;
            _header.Resize += (s, e) => _header.Invalidate();   // repaint column labels on width change (Panel won't by default)

            _scroll = new ThemedScrollPanel { Dock = DockStyle.Fill };
            _rows = new RowsSurface(this) { Dock = DockStyle.Top, Height = 0 };
            _scroll.Host.Controls.Add(_rows);

            _rows.SelectionChanged += () => SelectionChanged?.Invoke();
            _rows.ItemActivated += e => ItemActivated?.Invoke(e);
            _rows.ContextRequested += p => ContextRequested?.Invoke(p);
            _rows.ReloadRequested += () => ReloadRequested?.Invoke();
            _rows.EnsureVisible += (y, h) => _scroll.EnsureVisible(y, h);

            Controls.Add(_scroll);    // Fill (added first → under the Top header)
            Controls.Add(_header);
            ThemeHelper.ThemeChanged += OnTheme;
        }

        private void OnTheme() { if (IsDisposed) return; try { BeginInvoke((Action)(() => { _header.Invalidate(); _rows.Invalidate(); })); } catch { } }

        public void SetEntries(IReadOnlyList<RemoteEntry> entries)
        {
            _rows.SetRows(entries);
            _rows.Width = Math.Max(10, _scroll.Host.ClientSize.Width);
            _rows.Height = Math.Max(0, entries.Count * RowH);
            _scroll.RelayoutContent();
            SelectionChanged?.Invoke();
        }

        public RemoteEntry? Selected => _rows.Selected;
        public new void Focus() { try { _rows.Focus(); } catch { } }

        /// <summary>Column x-positions for a given content width — shared by the header + rows so they line up.</summary>
        internal static void Cols(int w, out int nameX, out int nameW, out int sizeX, out int sizeW, out int modX, out int modW, out int typeX, out int typeW)
        {
            nameX = 30; typeW = 52; modW = 120; sizeW = 74; int pad = 8;
            typeX = w - typeW - pad;
            modX = typeX - modW;
            sizeX = modX - sizeW;
            nameW = Math.Max(40, sizeX - nameX - 8);
        }

        private void HeaderPaint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            bool dark = ThemeHelper.IsDark;
            Color bg = dark ? Color.FromArgb(40, 40, 46) : Color.FromArgb(232, 232, 238);
            Color fg = dark ? Color.FromArgb(170, 170, 176) : Color.FromArgb(90, 90, 98);
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, _header.ClientRectangle);   // whole header dark (incl. the gutter strip → no white)

            int w = _header.Width - Gutter;   // content area aligns with the rows (which lose Gutter to the scrollbar)
            int nameX, nameW, sizeX, sizeW, modX, modW, typeX, typeW;
            Cols(w, out nameX, out nameW, out sizeX, out sizeW, out modX, out modW, out typeX, out typeW);
            using (var f = FontHelper.Ui(8.75f, FontStyle.Bold))
            {
                TextRenderer.DrawText(g, "Name", f, new Rectangle(nameX, 0, nameW, HeaderH), fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, "Size", f, new Rectangle(sizeX, 0, sizeW, HeaderH), fg, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, "Modified", f, new Rectangle(modX, 0, modW, HeaderH), fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, "Type", f, new Rectangle(typeX, 0, typeW, HeaderH), fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ThemeHelper.ThemeChanged -= OnTheme;
            base.Dispose(disposing);
        }

        // ── the scrolled rows surface (full-height; ThemedScrollPanel scrolls it + owns the scrollbar) ──
        private sealed class RowsSurface : Control
        {
            private readonly ThemedFileList _owner;
            private readonly List<RemoteEntry> _items = new List<RemoteEntry>();
            private int _sel = -1, _hover = -1;

            public event Action SelectionChanged;
            public event Action<RemoteEntry> ItemActivated;
            public event Action<Point> ContextRequested;
            public event Action ReloadRequested;
            public event Action<int, int> EnsureVisible;   // (y, h) in content coords

            public RowsSurface(ThemedFileList owner)
            {
                _owner = owner;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
                TabStop = true;
            }

            public RemoteEntry? Selected => (_sel >= 0 && _sel < _items.Count) ? _items[_sel] : (RemoteEntry?)null;

            public void SetRows(IReadOnlyList<RemoteEntry> items)
            {
                _items.Clear(); _items.AddRange(items);
                _sel = -1; _hover = -1;
                Invalidate();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                Focus();
                int i = e.Y / RowH;
                if (i >= 0 && i < _items.Count) { if (i != _sel) { _sel = i; SelectionChanged?.Invoke(); Invalidate(); } }
                else if (_sel != -1) { _sel = -1; SelectionChanged?.Invoke(); Invalidate(); }
                if (e.Button == MouseButtons.Right) ContextRequested?.Invoke(PointToScreen(e.Location));
            }

            protected override void OnMouseDoubleClick(MouseEventArgs e)
            {
                base.OnMouseDoubleClick(e);
                int i = e.Y / RowH;
                if (i >= 0 && i < _items.Count) ItemActivated?.Invoke(_items[i]);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                int i = (e.Y >= 0 && e.Y < _items.Count * RowH) ? e.Y / RowH : -1;
                if (i != _hover) { _hover = i; Invalidate(); }
            }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hover != -1) { _hover = -1; Invalidate(); } }

            protected override bool IsInputKey(Keys k)
            {
                switch (k & Keys.KeyCode) { case Keys.Up: case Keys.Down: case Keys.PageUp: case Keys.PageDown: case Keys.Home: case Keys.End: case Keys.Enter: return true; }
                return base.IsInputKey(k);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (_items.Count == 0) return;
                int n = _items.Count, sel = _sel;
                switch (e.KeyCode)
                {
                    case Keys.Up: sel = Math.Max(0, (_sel < 0 ? 0 : _sel) - 1); break;
                    case Keys.Down: sel = Math.Min(n - 1, _sel + 1); break;
                    case Keys.Home: sel = 0; break;
                    case Keys.End: sel = n - 1; break;
                    case Keys.PageUp: sel = Math.Max(0, (_sel < 0 ? 0 : _sel) - Math.Max(1, (Parent?.Height ?? RowH * 8) / RowH)); break;
                    case Keys.PageDown: sel = Math.Min(n - 1, _sel + Math.Max(1, (Parent?.Height ?? RowH * 8) / RowH)); break;
                    case Keys.Enter: if (_sel >= 0) ItemActivated?.Invoke(_items[_sel]); return;
                    case Keys.F5: ReloadRequested?.Invoke(); return;
                    default: return;
                }
                if (sel != _sel) { _sel = sel; SelectionChanged?.Invoke(); EnsureVisible?.Invoke(_sel * RowH, RowH); Invalidate(); }
                e.Handled = true;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                bool dark = ThemeHelper.IsDark;
                Color accent = ThemeHelper.GetWindowsAccentColor();
                Color bg = dark ? Color.FromArgb(30, 30, 34) : Color.FromArgb(250, 250, 252);
                Color name = dark ? Color.FromArgb(234, 234, 238) : Color.FromArgb(24, 24, 28);
                Color sub = dark ? Color.FromArgb(160, 160, 168) : Color.FromArgb(110, 110, 118);
                Color folderCol = dark ? Color.FromArgb(228, 190, 110) : Color.FromArgb(196, 150, 40);   // amber folders
                using (var bb = new SolidBrush(bg)) g.FillRectangle(bb, e.ClipRectangle);

                int w = Width;
                int nameX, nameW, sizeX, sizeW, modX, modW, typeX, typeW;
                Cols(w, out nameX, out nameW, out sizeX, out sizeW, out modX, out modW, out typeX, out typeW);

                int first = Math.Max(0, e.ClipRectangle.Top / RowH);
                int last = Math.Min(_items.Count - 1, e.ClipRectangle.Bottom / RowH);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var fName = FontHelper.Ui(9.5f))
                using (var fNameB = FontHelper.Ui(9.5f, FontStyle.Bold))
                {
                    for (int i = first; i <= last; i++)
                    {
                        var it = _items[i];
                        int y = i * RowH;
                        bool seld = i == _sel;
                        if (seld) using (var sb = new SolidBrush(accent)) g.FillRectangle(sb, 0, y, w, RowH);
                        else if (i == _hover) using (var hb = new SolidBrush(dark ? Color.FromArgb(44, 44, 50) : Color.FromArgb(236, 236, 240))) g.FillRectangle(hb, 0, y, w, RowH);

                        Color gl = seld ? Color.White : (it.IsDirectory ? folderCol : sub);
                        if (it.IsDirectory) DrawFolder(g, 8, y + RowH / 2, gl);
                        else DrawFile(g, 8, y + RowH / 2, gl);

                        Color nc = seld ? Color.White : name;
                        Color sc = seld ? Color.White : sub;
                        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
                        TextRenderer.DrawText(g, it.Name, it.IsDirectory ? fNameB : fName, new Rectangle(nameX, y, nameW, RowH), nc, flags | TextFormatFlags.Left);
                        if (!it.IsDirectory) TextRenderer.DrawText(g, Human(it.Size), fName, new Rectangle(sizeX, y, sizeW, RowH), sc, flags | TextFormatFlags.Right);
                        if (it.Modified > DateTime.MinValue) TextRenderer.DrawText(g, it.Modified.ToString("yyyy-MM-dd HH:mm"), fName, new Rectangle(modX, y, modW, RowH), sc, flags | TextFormatFlags.Left);
                        TextRenderer.DrawText(g, it.IsDirectory ? "Folder" : (it.IsSymlink ? "Link" : "File"), fName, new Rectangle(typeX, y, typeW, RowH), sc, flags | TextFormatFlags.Left);
                    }
                }
            }

            // ── owner-drawn glyphs (theme colours, on-brand line-art like the tab glyphs) ──
            private static void DrawFolder(Graphics g, int x, int cy, Color c)
            {
                using (var p = new Pen(c, 1.4f) { LineJoin = LineJoin.Round })
                using (var b = new SolidBrush(Color.FromArgb(46, c)))
                {
                    var body = new Rectangle(x, cy - 3, 16, 10);
                    g.FillRectangle(b, body);
                    g.DrawLines(p, new[] { new Point(x, cy - 3), new Point(x, cy - 6), new Point(x + 6, cy - 6), new Point(x + 8, cy - 3) });   // tab
                    g.DrawRectangle(p, body);
                }
            }

            private static void DrawFile(Graphics g, int x, int cy, Color c)
            {
                using (var p = new Pen(c, 1.3f) { LineJoin = LineJoin.Round })
                {
                    int lx = x + 2, top = cy - 7, w = 12, h = 15, fold = 4;
                    g.DrawLines(p, new[] { new Point(lx, top), new Point(lx + w - fold, top), new Point(lx + w, top + fold), new Point(lx + w, top + h), new Point(lx, top + h), new Point(lx, top) });
                    g.DrawLines(p, new[] { new Point(lx + w - fold, top), new Point(lx + w - fold, top + fold), new Point(lx + w, top + fold) });   // folded corner
                }
            }

            private static string Human(long n)
            {
                if (n < 1024) return n + " B";
                double v = n; string[] u = { "KB", "MB", "GB", "TB" }; int i = -1;
                do { v /= 1024; i++; } while (v >= 1024 && i < u.Length - 1);
                return v.ToString(v < 10 ? "0.0" : "0") + " " + u[i];
            }
        }
    }
}
