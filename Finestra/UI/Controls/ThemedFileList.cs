using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Finestra.Core;
using Finestra.Helpers;

namespace Finestra.UI.Controls
{
    /// <summary>FRDP-POLISH-4 — which column a header click sorts by.</summary>
    internal enum SortColumn { Name, Size, Modified, Type }

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
        /// <summary>FRDP-POLISH-4 — F2 with exactly one item selected.</summary>
        public event Action RenameRequested;
        /// <summary>FRDP-POLISH-4 — Delete key; the caller reads <see cref="SelectedEntries"/> for what to remove.</summary>
        public event Action DeleteRequested;
        /// <summary>FRDP-POLISH-4 — Backspace, "go up a level".</summary>
        public event Action UpRequested;
        /// <summary>FRDP-FTP-RICH — Ctrl+C / Ctrl+X / Ctrl+V. The caller reads <see cref="SelectedEntries"/> for
        /// Copy/Cut; Paste needs no payload (the clipboard lives in the owning FtpBrowserControl).</summary>
        public event Action CopyRequested;
        public event Action CutRequested;
        public event Action PasteRequested;
        /// <summary>FRDP-POLISH-4 — a column header was clicked; toggles ascending/descending if it's the same
        /// column again. Dirs-first is the caller's job (this only carries WHICH field + direction).</summary>
        public event Action<SortColumn, bool> SortRequested;

        private SortColumn _sortCol = SortColumn.Name;
        private bool _sortAsc = true;

        public ThemedFileList()
        {
            DoubleBuffered = true;
            _header = new Panel { Dock = DockStyle.Top, Height = HeaderH };
            _header.Paint += HeaderPaint;
            _header.Resize += (s, e) => _header.Invalidate();   // repaint column labels on width change (Panel won't by default)
            _header.MouseClick += HeaderClick;
            _header.Cursor = Cursors.Hand;

            _scroll = new ThemedScrollPanel { Dock = DockStyle.Fill };
            _rows = new RowsSurface(this) { Dock = DockStyle.Top, Height = 0 };
            _scroll.Host.Controls.Add(_rows);

            _rows.SelectionChanged += () => SelectionChanged?.Invoke();
            _rows.ItemActivated += e => ItemActivated?.Invoke(e);
            _rows.ContextRequested += p => ContextRequested?.Invoke(p);
            _rows.ReloadRequested += () => ReloadRequested?.Invoke();
            _rows.RenameRequested += () => RenameRequested?.Invoke();
            _rows.DeleteRequested += () => DeleteRequested?.Invoke();
            _rows.UpRequested += () => UpRequested?.Invoke();
            _rows.CopyRequested += () => CopyRequested?.Invoke();
            _rows.CutRequested += () => CutRequested?.Invoke();
            _rows.PasteRequested += () => PasteRequested?.Invoke();
            _rows.EnsureVisible += (y, h) => _scroll.EnsureVisible(y, h);

            Controls.Add(_scroll);    // Fill (added first → under the Top header)
            Controls.Add(_header);
            ThemeHelper.ThemeChanged += OnTheme;
        }

        private void HeaderClick(object sender, MouseEventArgs e)
        {
            int x = e.X;
            int w = _header.Width - Gutter;
            int nameX, nameW, sizeX, sizeW, modX, modW, typeX, typeW;
            Cols(w, out nameX, out nameW, out sizeX, out sizeW, out modX, out modW, out typeX, out typeW);
            SortColumn col;
            if (x >= typeX) col = SortColumn.Type;
            else if (x >= modX) col = SortColumn.Modified;
            else if (x >= sizeX) col = SortColumn.Size;
            else col = SortColumn.Name;
            _sortAsc = (col == _sortCol) ? !_sortAsc : true;
            _sortCol = col;
            _header.Invalidate();
            SortRequested?.Invoke(_sortCol, _sortAsc);
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
        /// <summary>FRDP-POLISH-4 — every currently-selected entry, in row order (may be more than one after a
        /// Ctrl/Shift-click or Ctrl+A). Empty when nothing is selected.</summary>
        public IReadOnlyList<RemoteEntry> SelectedEntries => _rows.SelectedEntries;
        public void SelectAll() => _rows.SelectAll();
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
            Color accent = ThemeHelper.GetWindowsAccentColor();
            string arrow = _sortAsc ? " ▲" : " ▼";   // ▲ / ▼ — FRDP-POLISH-4 sort indicator
            using (var f = FontHelper.Ui(8.75f, FontStyle.Bold))
            {
                TextRenderer.DrawText(g, "Name" + (_sortCol == SortColumn.Name ? arrow : ""), f, new Rectangle(nameX, 0, nameW, HeaderH), _sortCol == SortColumn.Name ? accent : fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, "Size" + (_sortCol == SortColumn.Size ? arrow : ""), f, new Rectangle(sizeX, 0, sizeW, HeaderH), _sortCol == SortColumn.Size ? accent : fg, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, "Modified" + (_sortCol == SortColumn.Modified ? arrow : ""), f, new Rectangle(modX, 0, modW, HeaderH), _sortCol == SortColumn.Modified ? accent : fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, "Type" + (_sortCol == SortColumn.Type ? arrow : ""), f, new Rectangle(typeX, 0, typeW, HeaderH), _sortCol == SortColumn.Type ? accent : fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
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
            // FRDP-POLISH-4 — multi-select. _sel stays the FOCUS row (keyboard nav anchor, and the single-item
            // Selected property every existing caller already uses); _selected is the actual multi-selection set;
            // _anchor is the Shift-click range base (set on every plain/Ctrl click, held across Shift-clicks).
            private int _sel = -1, _hover = -1, _anchor = -1;
            private readonly HashSet<int> _selected = new HashSet<int>();

            public event Action SelectionChanged;
            public event Action<RemoteEntry> ItemActivated;
            public event Action<Point> ContextRequested;
            public event Action ReloadRequested;
            public event Action RenameRequested;
            public event Action DeleteRequested;
            public event Action UpRequested;
            public event Action CopyRequested;
            public event Action CutRequested;
            public event Action PasteRequested;
            public event Action<int, int> EnsureVisible;   // (y, h) in content coords

            public RowsSurface(ThemedFileList owner)
            {
                _owner = owner;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
                TabStop = true;
            }

            public RemoteEntry? Selected => (_sel >= 0 && _sel < _items.Count) ? _items[_sel] : (RemoteEntry?)null;

            public IReadOnlyList<RemoteEntry> SelectedEntries
            {
                get
                {
                    var list = new List<RemoteEntry>(_selected.Count);
                    var idx = new List<int>(_selected); idx.Sort();
                    foreach (int i in idx) if (i >= 0 && i < _items.Count) list.Add(_items[i]);
                    return list;
                }
            }

            public void SelectAll()
            {
                if (_items.Count == 0) return;
                _selected.Clear();
                for (int i = 0; i < _items.Count; i++) _selected.Add(i);
                _sel = _items.Count - 1; _anchor = 0;
                SelectionChanged?.Invoke();
                Invalidate();
            }

            public void SetRows(IReadOnlyList<RemoteEntry> items)
            {
                _items.Clear(); _items.AddRange(items);
                _sel = -1; _hover = -1; _anchor = -1; _selected.Clear();
                Invalidate();
            }

            /// <summary>Replace the selection with exactly one row (plain click, arrow-key nav, activation).</summary>
            private void SelectSingle(int i)
            {
                _selected.Clear();
                if (i >= 0 && i < _items.Count) _selected.Add(i);
                _sel = i; _anchor = i;
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                Focus();
                int i = e.Y / RowH;
                bool valid = i >= 0 && i < _items.Count;

                if (e.Button == MouseButtons.Right)
                {
                    // Right-click on a row already in the selection keeps the WHOLE selection (so a context-menu
                    // action applies to everything picked) — matches Explorer. Right-click elsewhere replaces it.
                    if (valid && !_selected.Contains(i)) { SelectSingle(i); SelectionChanged?.Invoke(); Invalidate(); }
                    else if (!valid && _selected.Count > 0) { _selected.Clear(); _sel = -1; _anchor = -1; SelectionChanged?.Invoke(); Invalidate(); }
                    ContextRequested?.Invoke(PointToScreen(e.Location));
                    return;
                }

                if (!valid) { if (_selected.Count > 0) { _selected.Clear(); _sel = -1; _anchor = -1; SelectionChanged?.Invoke(); Invalidate(); } return; }

                if ((Control.ModifierKeys & Keys.Shift) == Keys.Shift && _anchor >= 0)
                {
                    int lo = Math.Min(_anchor, i), hi = Math.Max(_anchor, i);
                    _selected.Clear();
                    for (int k = lo; k <= hi; k++) _selected.Add(k);
                    _sel = i;   // anchor unchanged — a further Shift-click re-ranges from the SAME anchor
                }
                else if ((Control.ModifierKeys & Keys.Control) == Keys.Control)
                {
                    if (_selected.Contains(i)) _selected.Remove(i); else _selected.Add(i);
                    _sel = i; _anchor = i;
                }
                else SelectSingle(i);

                SelectionChanged?.Invoke();
                Invalidate();
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
                switch (k & Keys.KeyCode) { case Keys.Up: case Keys.Down: case Keys.PageUp: case Keys.PageDown: case Keys.Home: case Keys.End: case Keys.Enter: case Keys.Back: return true; }
                return base.IsInputKey(k);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (e.Control && e.KeyCode == Keys.A) { SelectAll(); e.Handled = true; return; }
                if (e.Control && e.KeyCode == Keys.C) { if (_selected.Count > 0) CopyRequested?.Invoke(); e.Handled = true; return; }
                if (e.Control && e.KeyCode == Keys.X) { if (_selected.Count > 0) CutRequested?.Invoke(); e.Handled = true; return; }
                if (e.Control && e.KeyCode == Keys.V) { PasteRequested?.Invoke(); e.Handled = true; return; }
                if (e.KeyCode == Keys.Back) { UpRequested?.Invoke(); e.Handled = true; return; }
                if (e.KeyCode == Keys.F2) { if (_selected.Count == 1) RenameRequested?.Invoke(); e.Handled = true; return; }
                if (e.KeyCode == Keys.Delete) { if (_selected.Count > 0) DeleteRequested?.Invoke(); e.Handled = true; return; }
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
                // Arrow-key nav (no Shift extend, per this batch's scope) always collapses to a single selection.
                if (sel != _sel || _selected.Count > 1) { SelectSingle(sel); SelectionChanged?.Invoke(); EnsureVisible?.Invoke(_sel * RowH, RowH); Invalidate(); }
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
                        bool seld = _selected.Contains(i);
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
