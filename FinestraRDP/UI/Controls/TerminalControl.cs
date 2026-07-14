using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;
using Finestra.Core;
using Finestra.Helpers;
using Finestra.UI;
using VtNetCore.VirtualTerminal;
using VtNetCore.VirtualTerminal.Layout;
using VtNetCore.XTermParser;

namespace Finestra.UI.Controls
{
    /// <summary>
    /// FRDP-SSH-BUILD-1 — the owner-drawn terminal. VtNetCore parses the byte stream into a screen buffer
    /// (<see cref="DataConsumer.Push"/>); this control renders that buffer as a monospace cell grid
    /// (<see cref="VirtualTerminalController.GetPageSpans"/> → rows → spans with #RRGGBB fg/bg + bold/underline),
    /// draws the cursor, and turns keystrokes into the wire bytes the shell expects. It knows nothing about SSH —
    /// bytes arrive via <see cref="Feed"/> and leave via <see cref="Input"/>; the host wires those to a
    /// <see cref="Core.SshSession"/>. RDP is untouched.
    ///
    /// Complete: colour rendering, cursor, printable + special-key input (arrows/Ctrl/fn/…, app-cursor-mode aware),
    /// resize → grid recompute + server window-change, wheel scrollback over history, paste, and (FRDP-SSH-BUILD-2)
    /// mouse-drag selection with highlight → Ctrl+Shift+C copies the SELECTION (falls back to the whole screen).
    /// </summary>
    public sealed class TerminalControl : Control
    {
        private readonly VirtualTerminalController _term = new VirtualTerminalController();
        private readonly DataConsumer _consumer;
        private Font _font, _fontBold;
        private int _cellW = 8, _cellH = 16;
        private int _cols = 80, _rows = 24;
        private int _fontPt = 11;               // current point size (FRDP-POLISH-2 font-size pref)
        private bool _monochrome;               // client-side "colors off": ignore span fg/bg, paint theme default
        private int _scroll;                    // rows scrolled back into history (0 = live bottom)
        private bool _selecting;                // a left-drag selection is in progress
        private TextPosition _selAnchor;        // where the drag began (absolute buffer coords)
        private TextRange _selection;           // current selection (null = none) — highlighted + copied by Ctrl+Shift+C
        private readonly Dictionary<string, Color> _colorCache = new Dictionary<string, Color>();
        private readonly Color _defBg = Color.FromArgb(12, 12, 12);
        private readonly Color _defFg = Color.FromArgb(205, 205, 205);
        private bool _frozen;                 // FIN-KBD-FREEZE — grid pinned; content clips, no pty resize
        private Timer _resizeDebounce;        // FIN-KBD-FREEZE Part 3 — coalesce a live-resize's pty ChangeWindowSize
        private const int ResizeDebounceMs = 150;

        // FRDP-SSH-PERF — dirty-row invalidation: a per-row content fingerprint from the last frame, so Feed() only
        // invalidates the rows that actually changed instead of the whole control. _lastCursorRow is tracked
        // separately because the cursor can move without any row's text/color content changing at all.
        private string[] _lastRowSig;
        private int _lastCursorRow = -1;
        private readonly Dictionary<Color, SolidBrush> _brushCache = new Dictionary<Color, SolidBrush>();
        private readonly Dictionary<Color, Pen> _penCache = new Dictionary<Color, Pen>();

        // FIN-KBD-FREEZE-SCROLL — while frozen AND the grid no longer fits the (shrunk) control, content clips;
        // this is how far the top of the frozen grid is scrolled OUT of view, so the BOTTOM (cursor/prompt — what
        // you're actively typing) stays visible by default. 0 = fully bottom-anchored; maxPan = fully top-anchored.
        private int _clipPanPx;
        private bool _sbDragging, _sbHover;
        private int _sbDragGrabDy;

        /// <summary>FIN-KBD-FREEZE — while true, <see cref="Recompute"/> is a no-op: cols/rows stay pinned, so no
        /// local reflow and no remote pty resize happen no matter how the control's pixel size changes (via its
        /// container shrinking/growing) — content simply clips. Flipping back to false re-syncs ONCE, immediately
        /// (bypassing the debounce), to whatever size the control is NOW; if that's unchanged from before the
        /// freeze, nothing is sent to the server at all — the session comes back exactly as it was, as a natural
        /// consequence of the cols/rows comparison, not as a special case.</summary>
        public bool Frozen
        {
            get { return _frozen; }
            set
            {
                if (_frozen == value) return;
                _frozen = value;
                // A resize just before freezing can already have armed the debounce timer; left running, it
                // fires its deferred pty resize WHILE frozen — the exact leak this must close (x64-proven: a
                // synthetic-rect test caught this racing exactly this way before the guard existed).
                if (_frozen) { _resizeDebounce?.Stop(); _clipPanPx = 0; }   // fresh freeze starts bottom-anchored (0 = maxPan at this instant)
                else { _clipPanPx = 0; _sbDragging = false; RecomputeNow(); }   // unfreeze re-fits the grid — panning is moot
                Invalidate();
            }
        }

        /// <summary>FIN-KBD-FREEZE-SCROLL — true only once frozen AND the pinned grid no longer fits the (shrunk)
        /// control: the state that needs the pan/scrollbar to reach the clipped-off rows at all.</summary>
        private bool Clipped => _frozen && _rows * _cellH > ClientSize.Height;
        private int MaxClipPan => Math.Max(0, _rows * _cellH - ClientSize.Height);

        /// <summary>Bytes the user produced (keystrokes / paste) — wire straight to the ShellStream.</summary>
        public event Action<byte[]> Input;
        /// <summary>The grid was re-measured: (columns, rows, pixelWidth, pixelHeight) — send a window-change.</summary>
        public event Action<int, int, int, int> Resized;

        public TerminalControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            BackColor = _defBg;
            _consumer = new DataConsumer(_term);
            _term.MaximumHistoryLines = 5000;
            SetFontSize(_fontPt);
        }

        // ── FRDP-POLISH-2: live terminal prefs (colors / font size / scrollback) — all non-destructive ─────────────
        /// <summary>Client-side monochrome: when true, span fg/bg are ignored and everything paints in the theme's
        /// default fg/bg (bold/underline still apply). The server still gets <c>xterm-256color</c> — this is a render
        /// choice, live-togglable, and it doesn't change remote app behaviour.</summary>
        public bool Monochrome { get { return _monochrome; } set { if (_monochrome == value) return; _monochrome = value; Invalidate(); } }

        public int FontSize => _fontPt;

        /// <summary>Change the font size live. Re-measures the cell + recomputes the grid (cols/rows) → raises
        /// <see cref="Resized"/> so the host resizes the remote pty. The controller (and its scrollback buffer) is
        /// NOT recreated, so history survives a font change.</summary>
        public void SetFontSize(int pt)
        {
            _fontPt = Math.Max(TerminalPrefs.MinFont, Math.Min(TerminalPrefs.MaxFont, pt));
            SetFont(FontHelper.Mono(_fontPt));
        }
        public void AdjustFontSize(int delta) => SetFontSize(_fontPt + delta);

        /// <summary>Scrollback depth (history lines kept for wheel-scroll). Live.</summary>
        public int Scrollback { set { try { _term.MaximumHistoryLines = Math.Max(200, value); } catch { } } }

        public void SetFont(Font f)
        {
            _font?.Dispose(); _fontBold?.Dispose();
            _font = f;
            _fontBold = new Font(f, f.Style | FontStyle.Bold);
            // Measure on a throwaway Bitmap Graphics so this works from the ctor (before the control has a handle).
            using (var bmp = new System.Drawing.Bitmap(1, 1))
            using (var g = Graphics.FromImage(bmp))
            {
                // exact monospace advance: measure a 10-char run with NoPadding, divide (kills per-call padding).
                var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
                int w10 = TextRenderer.MeasureText(g, "0000000000", _font, new Size(10000, 100), flags).Width;
                _cellW = Math.Max(1, (int)Math.Round(w10 / 10.0));
                _cellH = Math.Max(1, TextRenderer.MeasureText(g, "0", _font, new Size(100, 100), flags).Height);
            }
            // FIN-KBD-FREEZE — a font-size change is a deliberate action (the tab menu / terminal prefs), never
            // a reactive "the container resized because of a keyboard" event: it must not be silently swallowed
            // by the freeze, so this bypasses it via RecomputeNow() (also correct pre-freeze/pre-handle, in the
            // ctor's own SetFontSize call — RecomputeNow() has no _frozen check to trip either way).
            RecomputeNow();
        }

        /// <summary>Push received bytes into the parser and repaint. UI thread only (the host marshals).</summary>
        public void Feed(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            try { _consumer.Push(data); } catch { }
            // Resetting EITHER offset re-maps every row index to a DIFFERENT on-screen Y — a row whose text content
            // is unchanged can still need repainting because a DIFFERENT row's content now occupies its old pixel
            // position. Dirty-row diffing only compares CONTENT per row index, so it can't catch that on its own —
            // a full repaint is required whenever either offset actually moves, not just a missed optimization.
            bool wasScrolledOrPanned = _scroll != 0 || _clipPanPx != 0;
            _scroll = 0;                        // snap to the live bottom on new output (standard terminal behaviour)
            _clipPanPx = 0;                      // same snap, for the frozen-clip pan (see FIN-KBD-FREEZE-SCROLL)
            if (wasScrolledOrPanned) Invalidate();
            else InvalidateDirty();
        }

        /// <summary>FRDP-SSH-PERF — invalidate only the rows whose rendered content actually changed since the last
        /// frame (a per-row content fingerprint), plus the cursor's old/new row if it moved without any row's text
        /// changing at all. Falls back to a full <see cref="Invalidate()"/> whenever a selection is visible (rare,
        /// and simpler/safer than teaching the fingerprint about highlight state) or the grid size just changed.</summary>
        private void InvalidateDirty()
        {
            if (_selection != null || _cols <= 0 || _rows <= 0 || _cellH <= 0 || ClientSize.Width <= 0) { Invalidate(); return; }
            int start = _term.BottomRow - _rows + 1;   // _scroll is always 0 right here (Feed just reset it)
            List<LayoutRow> rows;
            try { rows = _term.GetPageSpans(start, _rows, _cols, null); } catch { Invalidate(); return; }

            int w = ClientSize.Width;
            if (_lastRowSig == null || _lastRowSig.Length != _rows)
            {
                _lastRowSig = new string[_rows];
                Invalidate();   // grid size just changed — one full repaint, then back to per-row diffing
            }
            else
            {
                for (int ri = 0; ri < _rows; ri++)
                {
                    string sig = ri < rows.Count ? RowSignature(rows[ri]) : "";
                    if (sig != _lastRowSig[ri]) Invalidate(new Rectangle(0, RowY(ri), w, _cellH));
                }
            }
            for (int ri = 0; ri < _rows; ri++) _lastRowSig[ri] = ri < rows.Count ? RowSignature(rows[ri]) : "";

            var cur = _term.CursorState;
            int curRow = (cur != null && cur.ShowCursor) ? cur.CurrentRow : -1;
            if (curRow != _lastCursorRow)
            {
                if (_lastCursorRow >= 0 && _lastCursorRow < _rows) Invalidate(new Rectangle(0, RowY(_lastCursorRow), w, _cellH));
                if (curRow >= 0 && curRow < _rows) Invalidate(new Rectangle(0, RowY(curRow), w, _cellH));
                _lastCursorRow = curRow;
            }
        }

        /// <summary>Row index → its current on-screen Y, honoring the frozen-clip pan offset (see
        /// <see cref="Clipped"/>) so dirty-rect invalidation always targets where the row is ACTUALLY drawn.</summary>
        private int RowY(int rowIndex) => rowIndex * _cellH - PanTopOffset();

        private static string RowSignature(LayoutRow row)
        {
            if (row?.Spans == null) return "";
            const char Sep = (char)1, End = (char)2;
            var sb = new StringBuilder();
            foreach (var s in row.Spans)
                sb.Append(s.Text).Append(Sep).Append(s.ForgroundColor).Append(Sep).Append(s.BackgroundColor)
                  .Append(s.Bold ? '1' : '0').Append(s.Underline ? '1' : '0').Append(s.Hidden ? '1' : '0').Append(End);
            return sb.ToString();
        }

        public int Cols => _cols;
        public int Rows => _rows;

        // ── grid metrics ──────────────────────────────────────────────────────────
        private void Recompute()
        {
            if (_frozen) return;   // FIN-KBD-FREEZE — grid pinned entirely: no cols/rows change, no pty resize
            int c = Math.Max(8, ClientSize.Width / Math.Max(1, _cellW));
            int r = Math.Max(2, ClientSize.Height / Math.Max(1, _cellH));
            if (c == _cols && r == _rows) return;
            _cols = c; _rows = r;
            try { _term.ResizeView(_cols, _rows); } catch { }   // local reflow — cheap, stays instant/responsive
            ArmResizeDebounce();                                 // the EXPENSIVE part (remote pty resize) is deferred
        }

        /// <summary>FIN-KBD-FREEZE Part 3 — a live drag/animated resize sends many WM_SIZE frames; Recompute()
        /// runs per frame (cheap local reflow only), but the remote pty resize (a network round trip — SSH
        /// ChangeWindowSize) is coalesced here so one settle = ONE resize, not one per frame. Independent of the
        /// freeze itself: this also helps a plain user drag-resize with no keyboard involved at all.</summary>
        private void ArmResizeDebounce()
        {
            if (_resizeDebounce == null) { _resizeDebounce = new Timer { Interval = ResizeDebounceMs }; _resizeDebounce.Tick += OnResizeDebounceTick; }
            _resizeDebounce.Stop();
            _resizeDebounce.Start();
        }

        private void OnResizeDebounceTick(object sender, EventArgs e)
        {
            _resizeDebounce.Stop();
            // A WM_TIMER already pulled off the queue by a DoEvents()/PeekMessage pass still dispatches even if
            // Stop() (KillTimer) ran moments earlier in that same pass — Stop() only blocks FUTURE ticks. Re-check
            // here as the backstop so a stale tick can never leak a resize while frozen.
            if (_frozen) return;
            Resized?.Invoke(_cols, _rows, ClientSize.Width, ClientSize.Height);
        }

        /// <summary>FIN-KBD-FREEZE — unfreeze's immediate, undebounced catch-up ("re-fit ONCE"). Any pending
        /// debounced fire is cancelled — this call wins. If the control's size is back to exactly what it was
        /// before freezing, cols/rows are unchanged and NOTHING is sent to the server.</summary>
        private void RecomputeNow()
        {
            _resizeDebounce?.Stop();
            int c = Math.Max(8, ClientSize.Width / Math.Max(1, _cellW));
            int r = Math.Max(2, ClientSize.Height / Math.Max(1, _cellH));
            if (c == _cols && r == _rows) { Invalidate(); return; }   // EXACT restore — nothing changed, nothing sent
            _cols = c; _rows = r;
            try { _term.ResizeView(_cols, _rows); } catch { }
            Resized?.Invoke(_cols, _rows, ClientSize.Width, ClientSize.Height);
            Invalidate();
        }

        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Recompute(); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            // FRDP-POLISH-4 — right-click now opens the themed Select All/Copy/Paste/Clear menu instead of
            // instant-pasting; Paste is still the FIRST item, so it's one extra click for the old muscle memory.
            if (e.Button == MouseButtons.Right) { ShowContextMenu(PointToScreen(e.Location)); return; }
            if (e.Button != MouseButtons.Left) return;

            // FIN-KBD-FREEZE-SCROLL — the scrollbar only exists (and only intercepts clicks) while frozen+clipped;
            // everywhere else, a left-press is exactly the pre-existing text-selection gesture, unchanged.
            if (Clipped)
            {
                var thumb = ScrollbarThumbRect();
                if (thumb.Contains(e.Location)) { _sbDragging = true; _sbDragGrabDy = e.Y - thumb.Y; Capture = true; return; }
                if (ScrollbarTrackRect().Contains(e.Location))
                {
                    AdjustClipPan(e.Y < thumb.Y ? ClientSize.Height : -ClientSize.Height);   // page toward the click
                    return;
                }
            }

            _selecting = true;
            _selAnchor = PosAt(e.Location);
            _selection = null;          // a fresh press clears the previous highlight (a plain click deselects)
            Capture = true;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_sbDragging)
            {
                _clipPanPx = ClipScrollbar.PanFromDrag(ClientSize, ContentHeight, MaxClipPan, e.Y, _sbDragGrabDy);
                Invalidate();
                return;
            }
            if (Clipped)
            {
                bool hover = ScrollbarThumbRect().Contains(e.Location);
                if (hover != _sbHover) { _sbHover = hover; Invalidate(); }
            }
            if (!_selecting || (e.Button & MouseButtons.Left) == 0) return;
            _selection = OrderedRange(_selAnchor, PosAt(e.Location));
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left) { _selecting = false; _sbDragging = false; Capture = false; }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_sbHover) { _sbHover = false; Invalidate(); }
        }

        /// <summary>Screen point → absolute buffer position (column, line) — the same coordinate space
        /// <see cref="VirtualTerminalController.GetPageSpans"/> renders and <see cref="VirtualTerminalController.GetText"/>
        /// extracts, so the highlight and the copied text agree. Accounts for the frozen-clip pan offset so a click/
        /// drag still lands on the row actually under the pointer.</summary>
        private TextPosition PosAt(Point p)
        {
            int col = Math.Max(0, Math.Min(_cols - 1, p.X / Math.Max(1, _cellW)));
            int panRow = (p.Y + PanTopOffset()) / Math.Max(1, _cellH);
            int rel = Math.Max(0, Math.Min(_rows - 1, panRow));
            int start = _term.BottomRow - _rows + 1 - _scroll;
            return new TextPosition(col, start + rel);
        }

        private static TextRange OrderedRange(TextPosition a, TextPosition b)
        {
            bool aFirst = a.Row < b.Row || (a.Row == b.Row && a.Column <= b.Column);
            return new TextRange { Start = aFirst ? a : b, End = aFirst ? b : a };
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (Clipped) { AdjustClipPan((e.Delta > 0 ? 1 : -1) * _cellH * 3); return; }
            int max = Math.Max(0, Math.Min(_term.BottomRow - _rows + 1, _term.MaximumHistoryLines));
            int step = e.Delta > 0 ? 3 : -3;
            int ns = Math.Max(0, Math.Min(max, _scroll + step));
            if (ns != _scroll) { _scroll = ns; Invalidate(); }
        }

        // ── frozen-clip pan + scrollbar (FIN-KBD-FREEZE-SCROLL) ──────────────────────
        private int PanTopOffset()
        {
            int maxPan = _frozen ? MaxClipPan : 0;
            return maxPan - Math.Max(0, Math.Min(maxPan, _clipPanPx));
        }

        private void AdjustClipPan(int deltaPx)
        {
            int maxPan = MaxClipPan;
            int newPan = Math.Max(0, Math.Min(maxPan, _clipPanPx + deltaPx));
            if (newPan != _clipPanPx) { _clipPanPx = newPan; Invalidate(); }
        }

        private int ContentHeight => Math.Max(1, _rows * _cellH);
        private Rectangle ScrollbarTrackRect() => ClipScrollbar.TrackRect(ClientSize);
        private Rectangle ScrollbarThumbRect() => ClipScrollbar.ThumbRect(ClientSize, ContentHeight, _clipPanPx, MaxClipPan);

        // ── paint ──────────────────────────────────────────────────────────────────
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(_defBg);
            if (_cols <= 0 || _rows <= 0 || _cellW <= 0 || _cellH <= 0) return;

            int start = _term.BottomRow - _rows + 1 - _scroll;
            List<LayoutRow> rows;
            try { rows = _term.GetPageSpans(start, _rows, _cols, _selection); }   // _selection (or null) inverts the highlighted range
            catch { return; }

            var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            var clip = e.ClipRectangle;
            for (int ri = 0; ri < rows.Count && ri < _rows; ri++)
            {
                var row = rows[ri];
                if (row?.Spans == null) continue;
                int py = RowY(ri);
                if (py + _cellH <= clip.Top || py >= clip.Bottom) continue;   // dirty-row invalidation already narrows this — skip the rest cheaply
                int col = 0;
                foreach (var span in row.Spans)
                {
                    string text = span.Text ?? "";
                    if (text.Length == 0) continue;
                    int px = col * _cellW, wpx = text.Length * _cellW;
                    Color bg = _monochrome ? _defBg : ParseColor(span.BackgroundColor, _defBg);
                    Color fg = span.Hidden ? bg : (_monochrome ? _defFg : ParseColor(span.ForgroundColor, _defFg));
                    if (bg != _defBg) g.FillRectangle(CachedBrush(bg), px, py, wpx, _cellH);
                    TextRenderer.DrawText(g, text, span.Bold ? _fontBold : _font, new Rectangle(px, py, wpx + _cellW, _cellH), fg, flags);
                    if (span.Underline) g.DrawLine(CachedPen(fg), px, py + _cellH - 1, px + wpx, py + _cellH - 1);
                    col += text.Length;
                }
            }

            // cursor — live bottom only, block, when focused
            var cur = _term.CursorState;
            if (_scroll == 0 && cur != null && cur.ShowCursor && cur.CurrentRow >= 0 && cur.CurrentRow < _rows)
            {
                var r = new Rectangle(cur.CurrentColumn * _cellW, RowY(cur.CurrentRow), _cellW, _cellH);
                g.FillRectangle(CachedBrush(Color.FromArgb(Focused ? 200 : 90, _defFg)), r);
            }

            // FIN-KBD-FREEZE-SCROLL — a floating overlay, not a reserved gutter: reflowing _cols to make room would
            // violate the freeze (cols/rows must not change while frozen), so this draws on top of the last column.
            if (Clipped) ClipScrollbar.Draw(g, ClientSize, ContentHeight, _clipPanPx, MaxClipPan, _sbHover || _sbDragging);
        }

        private SolidBrush CachedBrush(Color c)
        {
            SolidBrush b;
            if (!_brushCache.TryGetValue(c, out b)) { b = new SolidBrush(c); _brushCache[c] = b; }
            return b;
        }

        private Pen CachedPen(Color c)
        {
            Pen p;
            if (!_penCache.TryGetValue(c, out p)) { p = new Pen(c); _penCache[c] = p; }
            return p;
        }

        private Color ParseColor(string s, Color fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            Color c;
            if (_colorCache.TryGetValue(s, out c)) return c;
            try { c = ColorTranslator.FromHtml(s); } catch { c = fallback; }
            _colorCache[s] = c;
            return c;
        }

        // ── input ────────────────────────────────────────────────────────────────
        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Up: case Keys.Down: case Keys.Left: case Keys.Right:
                case Keys.Tab: case Keys.Home: case Keys.End: case Keys.PageUp: case Keys.PageDown:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Alt) return;   // leave Alt combos (Alt+F4 etc.) to the system

            // copy/paste — Ctrl+Shift+C/V (plain Ctrl+C must stay SIGINT)
            if (e.Control && e.Shift && e.KeyCode == Keys.C) { CopySelectionOrScreen(); Handled(e); return; }
            if (e.Control && e.Shift && e.KeyCode == Keys.V) { Paste(); Handled(e); return; }
            if (e.Shift && e.KeyCode == Keys.Insert) { Paste(); Handled(e); return; }

            byte[] seq = null;
            switch (e.KeyCode)
            {
                case Keys.Enter: seq = new byte[] { 0x0D }; break;          // CR (VtNetCore returns LF; shells want CR)
                case Keys.Back: seq = new byte[] { 0x7F }; break;           // DEL (the Unix backspace)
                case Keys.Escape: seq = new byte[] { 0x1B }; break;         // single ESC
                case Keys.Up: seq = Key("Up", e); break;
                case Keys.Down: seq = Key("Down", e); break;
                case Keys.Left: seq = Key("Left", e); break;
                case Keys.Right: seq = Key("Right", e); break;
                case Keys.Home: seq = Key("Home", e); break;
                case Keys.End: seq = Key("End", e); break;
                case Keys.PageUp: seq = Key("PageUp", e); break;
                case Keys.PageDown: seq = Key("PageDown", e); break;
                case Keys.Insert: seq = Key("Insert", e); break;
                case Keys.Delete: seq = Key("Delete", e); break;
                case Keys.Tab: seq = new byte[] { 0x09 }; break;
                default:
                    if (e.KeyCode >= Keys.F1 && e.KeyCode <= Keys.F12)
                        seq = Key("F" + (e.KeyCode - Keys.F1 + 1), e);
                    else if (e.Control && !e.Shift && e.KeyCode >= Keys.A && e.KeyCode <= Keys.Z)
                        seq = new byte[] { (byte)(e.KeyCode - Keys.A + 1) };   // Ctrl+A..Z → 0x01..0x1A
                    break;
            }
            if (seq != null && seq.Length > 0) { Input?.Invoke(seq); Handled(e); }
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            char c = e.KeyChar;
            if (c == '\r' || c == '\n' || c == '\b' || c == 0x1B || c == '\t') { e.Handled = true; return; }  // handled in KeyDown
            if (c < 0x20 && c != 0x00) { e.Handled = true; return; }   // control chars handled in KeyDown
            Input?.Invoke(Encoding.UTF8.GetBytes(c.ToString()));
            e.Handled = true;
        }

        private byte[] Key(string name, KeyEventArgs e)
        {
            try { var b = _term.GetKeySequence(name, e.Control, e.Shift); if (b != null && b.Length > 0) return b; } catch { }
            return null;
        }
        private static void Handled(KeyEventArgs e) { e.Handled = true; e.SuppressKeyPress = true; }

        // ── clipboard ──────────────────────────────────────────────────────────────
        /// <summary>Ctrl+Shift+C copies the drag-selection if there is one (FRDP-SSH-BUILD-2), else the whole
        /// visible screen (the BUILD-1 fallback).</summary>
        private void CopySelectionOrScreen()
        {
            try
            {
                string s = _selection != null ? _term.GetText(_selection) : _term.GetScreenText();
                if (!string.IsNullOrEmpty(s)) Clipboard.SetText(s);
            }
            catch { }
        }
        private void Paste()
        {
            try
            {
                if (!Clipboard.ContainsText()) return;
                string s = Clipboard.GetText().Replace("\r\n", "\r").Replace("\n", "\r");
                if (s.Length > 0) Input?.Invoke(Encoding.UTF8.GetBytes(s));
            }
            catch { }
        }

        /// <summary>FRDP-POLISH-4 — selects the ENTIRE accessible buffer (visible screen + scrollback, same depth
        /// the mouse-wheel scroll-clamp already uses) so a following Copy grabs everything, not just what fits
        /// on screen right now.</summary>
        private void SelectAllForCopy()
        {
            if (_cols <= 0 || _rows <= 0) return;
            int viewTop = _term.BottomRow - _rows + 1;
            int maxScroll = Math.Max(0, Math.Min(viewTop, _term.MaximumHistoryLines));
            int topRow = Math.Max(0, viewTop - maxScroll);
            // End column is INCLUSIVE (0.._cols-1), matching PosAt's own clamp for mouse-drag selection — _cols
            // itself is one past the last valid column and made GetText silently return nothing (caught by
            // CopySelectionOrScreen's own try/catch) instead of throwing loudly. Caught by the harness, not a guess.
            _selection = new TextRange { Start = new TextPosition(0, topRow), End = new TextPosition(_cols - 1, _term.BottomRow) };
            Invalidate();
        }

        /// <summary>FRDP-POLISH-4 — client-side only: wipes the local screen + scrollback buffer, same as a
        /// terminal's own "Clear"/"Reset" command. Never sends anything to the remote — the user's own shell
        /// session/history on the server side is untouched.</summary>
        private void ClearTerminal()
        {
            try { _term.FullReset(); } catch { }
            _selection = null; _scroll = 0; _clipPanPx = 0;
            Invalidate();
        }

        // ── context menu ───────────────────────────────────────────────────────────
        private void ShowContextMenu(Point screen)
        {
            var menu = new ThemedContextMenuStrip { Font = FontHelper.Ui(9.5f) };
            menu.Items.Add(new ToolStripMenuItem("Paste", null, (s, e) => Paste()) { ShortcutKeyDisplayString = "Ctrl+Shift+V" });
            menu.Items.Add(new ToolStripMenuItem("Copy", null, (s, e) => CopySelectionOrScreen()) { ShortcutKeyDisplayString = "Ctrl+Shift+C", Enabled = true });
            menu.Items.Add(new ToolStripMenuItem("Select All", null, (s, e) => SelectAllForCopy()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Clear", null, (s, e) => ClearTerminal()));
            menu.Show(screen);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _font?.Dispose(); _fontBold?.Dispose(); _resizeDebounce?.Stop(); _resizeDebounce?.Dispose();
                foreach (var b in _brushCache.Values) b.Dispose();
                foreach (var p in _penCache.Values) p.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
