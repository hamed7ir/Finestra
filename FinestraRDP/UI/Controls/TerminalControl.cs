using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using Finestra.Core;
using Finestra.Helpers;
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
            Recompute();
            Invalidate();
        }

        /// <summary>Push received bytes into the parser and repaint. UI thread only (the host marshals).</summary>
        public void Feed(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            try { _consumer.Push(data); } catch { }
            _scroll = 0;                        // snap to the live bottom on new output (standard terminal behaviour)
            Invalidate();
        }

        public int Cols => _cols;
        public int Rows => _rows;

        // ── grid metrics ──────────────────────────────────────────────────────────
        private void Recompute()
        {
            int c = Math.Max(8, ClientSize.Width / Math.Max(1, _cellW));
            int r = Math.Max(2, ClientSize.Height / Math.Max(1, _cellH));
            if (c == _cols && r == _rows) return;
            _cols = c; _rows = r;
            try { _term.ResizeView(_cols, _rows); } catch { }
            Resized?.Invoke(_cols, _rows, ClientSize.Width, ClientSize.Height);
        }

        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Recompute(); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Right) { Paste(); return; }   // right-click = paste (Ctrl+C is SIGINT, so it can't copy)
            if (e.Button == MouseButtons.Left)
            {
                _selecting = true;
                _selAnchor = PosAt(e.Location);
                _selection = null;          // a fresh press clears the previous highlight (a plain click deselects)
                Capture = true;
                Invalidate();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_selecting || (e.Button & MouseButtons.Left) == 0) return;
            _selection = OrderedRange(_selAnchor, PosAt(e.Location));
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left) { _selecting = false; Capture = false; }
        }

        /// <summary>Screen point → absolute buffer position (column, line) — the same coordinate space
        /// <see cref="VirtualTerminalController.GetPageSpans"/> renders and <see cref="VirtualTerminalController.GetText"/>
        /// extracts, so the highlight and the copied text agree.</summary>
        private TextPosition PosAt(Point p)
        {
            int col = Math.Max(0, Math.Min(_cols - 1, p.X / Math.Max(1, _cellW)));
            int rel = Math.Max(0, Math.Min(_rows - 1, p.Y / Math.Max(1, _cellH)));
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
            int max = Math.Max(0, Math.Min(_term.BottomRow - _rows + 1, _term.MaximumHistoryLines));
            int step = e.Delta > 0 ? 3 : -3;
            int ns = Math.Max(0, Math.Min(max, _scroll + step));
            if (ns != _scroll) { _scroll = ns; Invalidate(); }
        }

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
            for (int ri = 0; ri < rows.Count && ri < _rows; ri++)
            {
                var row = rows[ri];
                if (row?.Spans == null) continue;
                int col = 0, py = ri * _cellH;
                foreach (var span in row.Spans)
                {
                    string text = span.Text ?? "";
                    if (text.Length == 0) continue;
                    int px = col * _cellW, wpx = text.Length * _cellW;
                    Color bg = _monochrome ? _defBg : ParseColor(span.BackgroundColor, _defBg);
                    Color fg = span.Hidden ? bg : (_monochrome ? _defFg : ParseColor(span.ForgroundColor, _defFg));
                    if (bg != _defBg) using (var b = new SolidBrush(bg)) g.FillRectangle(b, px, py, wpx, _cellH);
                    TextRenderer.DrawText(g, text, span.Bold ? _fontBold : _font, new Rectangle(px, py, wpx + _cellW, _cellH), fg, flags);
                    if (span.Underline) using (var p = new Pen(fg)) g.DrawLine(p, px, py + _cellH - 1, px + wpx, py + _cellH - 1);
                    col += text.Length;
                }
            }

            // cursor — live bottom only, block, when focused
            var cur = _term.CursorState;
            if (_scroll == 0 && cur != null && cur.ShowCursor && cur.CurrentRow >= 0 && cur.CurrentRow < _rows)
            {
                var r = new Rectangle(cur.CurrentColumn * _cellW, cur.CurrentRow * _cellH, _cellW, _cellH);
                using (var b = new SolidBrush(Color.FromArgb(Focused ? 200 : 90, _defFg))) g.FillRectangle(b, r);
            }
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

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _font?.Dispose(); _fontBold?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
