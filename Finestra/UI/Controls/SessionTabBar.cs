using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Finestra.Helpers;

namespace Finestra.UI.Controls
{
    /// <summary>The kind of remote a tab hosts — drives the tab glyph + which bar readout/controls show
    /// (FRDP-SSH-BUILD-2 / FRDP-FTP-BUILD-2). RDP = embedded desktop (stats + pause); SSH = terminal (status + ping);
    /// FTP = file browser (status + remote path / transfer progress). Only RDP shows pause.</summary>
    public enum SessionKind { Rdp, Ssh, Ftp }

    /// <summary>What a session host drives, whichever presentation it is wearing: the auto-hide hover
    /// <see cref="UI.OverlayBar"/> (fullscreen) or the persistent <see cref="SessionTabBar"/> (windowed).</summary>
    public interface ISessionBar
    {
        void SetTabs(List<string> names, IList<SessionKind> kinds, int active);
        void SetActive(int i);
        void SetStats(string s);
        void SetPaused(bool paused);
        /// <summary>Show or hide the pause/resume glyph — hidden for SSH tabs (no suppress-output equivalent).</summary>
        void SetPauseVisible(bool visible);
        void SetMaximized(bool maximized);
        void SetFullscreen(bool fullscreen);
        /// <summary>FRDP-RECONNECT — show/hide the Reconnect button for the active tab: visible when the session
        /// is dropped or an attempt is running; busy = "Reconnecting…" (disabled). RDP tabs are included since
        /// FIN-RDP-RECONNECT-2 (they show it when Disconnected); the original "never for RDP" rule assumed
        /// FreeRDP's +auto-reconnect covered it, and that setting defaults to false.</summary>
        void SetReconnect(bool visible, bool busy);
        /// <summary>The Reconnect button was clicked (only fires when visible + not busy).</summary>
        event Action ReconnectClicked;
        /// <summary>SDL-ENGINE — show/hide the Touch/Mouse switch for the active tab, and which mode it is in.
        /// One call, so the bar never shows a half-updated state.</summary>
        void SetTouch(bool visible, bool touchOn);
        /// <summary>The Touch/Mouse switch was pressed (only fires when visible).</summary>
        event Action TouchToggled;
    }

    /// <summary>
    /// FRDP-UI-WINDOWED — THE tab model, owner-painted in the Windows accent. ONE control, TWO presentations:
    /// the fullscreen host hosts it inside the topmost auto-hide <see cref="UI.OverlayBar"/> form, the windowed
    /// host docks it to the top where it doubles as the TITLE BAR. Same tabs, same stats readout, same
    /// pause/resume glyph, same window buttons — no forked logic.
    ///
    /// Layout (left→right): tabs (each with a "×") · "+" add · stats readout · pause/resume · Min · Restore/Max ·
    /// Close. A press that lands on none of those raises <see cref="BackgroundMouseDown"/> — the windowed host
    /// turns that into a window drag (and a double-press into maximize/restore).
    /// </summary>
    public sealed class SessionTabBar : Control, ISessionBar
    {
        public const int BarHeight = 38;
        private const int BtnW = 46;
        private const int StatsW = 240;
        private const int TabW = 210;
        private const int AddW = 40;
        private const int ReconW = 140;   // FRDP-RECONNECT — the Reconnect button (SSH/FTP have no pause, so this slot is free).
                                          // Widened from 122: "⟳  Reconnect" did not fit its 116px inner rect
                                          // and was being ellipsized inside its own button.
        private const int StatsGap = 12;  // breathing room between the stats readout and the control to its right

        public event Action<int> TabClicked;
        public event Action<int> TabCloseClicked;
        public event Action AddClicked;
        public event Action MinimizeClicked;
        public event Action RestoreClicked;
        public event Action CloseClicked;
        public event Action PauseToggled;
        /// <summary>The ⛶ / ⧉ button — flip the running session between windowed and fullscreen (FRDP-FS-TOGGLE).</summary>
        public event Action FullscreenToggle;
        /// <summary>FRDP-RECONNECT — the Reconnect button was clicked (SSH/FTP dropped tab).</summary>
        public event Action ReconnectClicked;
        /// <summary>SDL-ENGINE — the Touch/Mouse switch was pressed.</summary>
        public event Action TouchToggled;
        /// <summary>Press on the bar background (hit nothing). e.Clicks==2 means a double-press.</summary>
        public event Action<MouseEventArgs> BackgroundMouseDown;
        /// <summary>Tear this tab out into its own window (FRDP-TEAROFF). Raised by a middle-click on the tab.</summary>
        public event Action<int> TabTearRequested;
        /// <summary>Right-click on a tab → (tabIndex, screen point). The host shows a per-type context menu (FRDP-POLISH-2).</summary>
        public event Action<int, Point> TabRightClicked;
        /// <summary>A left-press-and-drag past the threshold is in progress: (tabIndex, cursor screen point).</summary>
        public event Action<int, Point> TabDragMove;
        /// <summary>The drag ended: (tabIndex, drop screen point). Only fires if a real drag happened.</summary>
        public event Action<int, Point> TabDragDrop;

        private int _pressTab = -1;
        private Point _pressScreen;
        private bool _dragging;
        private static readonly int DragThreshold = Math.Max(12, SystemInformation.DragSize.Width * 2);

        private List<string> _tabs = new List<string>();
        private IList<SessionKind> _kinds = new List<SessionKind>();
        private int _active;
        private string _stats = "—";
        private bool _paused, _maximized, _fullscreen;
        private bool _pauseVisible = true;   // hidden while an SSH tab is active
        private bool _reconVisible, _reconBusy;   // FRDP-RECONNECT — the Reconnect button state (active dropped SSH/FTP tab)
        private bool _touchVisible, _touchOn;     // SDL-ENGINE — the Touch/Mouse switch (active RDP tab, SDL engine)

        private Rectangle[] _tabRects = new Rectangle[0];
        private Rectangle[] _tabCloseRects = new Rectangle[0];
        // FIN-SSH-TAB-MENU — SSH-only "⋮" (vertical ellipsis) so touch users (no right-click gesture) can discover
        // and reach the per-tab quick menu. Rectangle.Empty for non-SSH tabs — never drawn, never hit-tested.
        private Rectangle[] _tabMenuRects = new Rectangle[0];
        private Rectangle _addRect, _statsRect, _pauseRect, _fsRect, _minRect, _restoreRect, _closeRect, _reconnectRect, _touchRect;

        public SessionTabBar()
        {
            Height = BarHeight;
            // Selectable=false: a click on the bar must never pull focus off the embedded RDP child.
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
        }

        public void SetTabs(List<string> names, IList<SessionKind> kinds, int active) { _tabs = names ?? new List<string>(); _kinds = kinds ?? new List<SessionKind>(); _active = active; Recalc(); Invalidate(); }
        public void SetActive(int i) { _active = i; Invalidate(); }
        public void SetStats(string s) { _stats = s ?? "—"; Invalidate(); }
        public void SetPaused(bool paused) { _paused = paused; Invalidate(); }
        public void SetPauseVisible(bool visible) { if (_pauseVisible == visible) return; _pauseVisible = visible; Invalidate(); }
        private SessionKind KindAt(int i) => (i >= 0 && i < _kinds.Count) ? _kinds[i] : SessionKind.Rdp;
        public void SetMaximized(bool maximized) { if (_maximized == maximized) return; _maximized = maximized; Invalidate(); }
        public void SetFullscreen(bool fullscreen) { if (_fullscreen == fullscreen) return; _fullscreen = fullscreen; Invalidate(); }
        public void SetReconnect(bool visible, bool busy) { if (_reconVisible == visible && _reconBusy == busy) return; _reconVisible = visible; _reconBusy = busy; Recalc(); Invalidate(); }
        public void SetTouch(bool visible, bool touchOn) { if (_touchVisible == visible && _touchOn == touchOn) return; _touchVisible = visible; _touchOn = touchOn; Recalc(); Invalidate(); }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Recalc(); }

        private void Recalc()
        {
            _closeRect = new Rectangle(Width - BtnW, 0, BtnW, BarHeight);
            _restoreRect = new Rectangle(Width - 2 * BtnW, 0, BtnW, BarHeight);
            _minRect = new Rectangle(Width - 3 * BtnW, 0, BtnW, BarHeight);
            _fsRect = new Rectangle(Width - 4 * BtnW, 0, BtnW, BarHeight);
            _pauseRect = new Rectangle(Width - 5 * BtnW, 0, BtnW, BarHeight);
            // SDL-ENGINE — the Touch/Mouse switch sits left of Pause, a cell of the same size as its peers. It is only
            // shown for a LIVE session (never while Reconnect is up), and Reconnect spans the pause cell, so the two
            // never overlap: the host shows one or the other.
            _touchRect = new Rectangle(Width - 6 * BtnW, 0, BtnW, BarHeight);
            _reconnectRect = new Rectangle(_fsRect.Left - ReconW, 5, ReconW - 6, BarHeight - 10);   // FRDP-RECONNECT — left of FS (the free SSH/FTP pause slot)
            // The readout must not run into whatever sits to its right. Its right edge was previously the
            // NEXT CONTROL'S LEFT EDGE EXACTLY - zero gap - so a full-width status string ended flush
            // against the Reconnect button and the two read as one run of text. Right-aligned text also
            // has no trailing bearing to soften that. Keep a real gap.
            //
            // And SCALE THE BUDGET WITH DPI: StatsW is a PIXEL constant while the font is in POINTS, so at
            // 144 DPI the same text is half again as wide with no extra room. Measured at 144: the ordinary
            // live readout "rtt 388 ms · 100.0 Mbps · jit 12" needs 277px and the longest disconnect string
            // 294px, both against a flat 240px - so the EVERYDAY case truncated, not just an edge case.
            int statsW = StatsW, gap = StatsGap;
            try { double s = DeviceDpi / 96.0; statsW = (int)Math.Round(StatsW * s); gap = (int)Math.Round(StatsGap * s); }
            catch { /* pre-4.7 or no handle yet - the unscaled constants still render, just tighter */ }
            int statsRight = (_reconVisible ? _reconnectRect.Left : _touchVisible ? _touchRect.Left : _pauseRect.Left) - gap;
            _statsRect = new Rectangle(statsRight - statsW, 0, statsW, BarHeight);

            var tabs = new List<Rectangle>();
            var closes = new List<Rectangle>();
            var menus = new List<Rectangle>();
            int x = 6;
            int maxTabsRight = Math.Max(120, _statsRect.Left - AddW - 12);
            for (int i = 0; i < _tabs.Count; i++)
            {
                int w = Math.Min(TabW, Math.Max(90, (maxTabsRight - 6) / Math.Max(1, _tabs.Count)));
                var r = new Rectangle(x, 4, w, BarHeight - 8);
                tabs.Add(r);
                var close = new Rectangle(r.Right - 22, r.Top + (r.Height - 16) / 2, 16, 16);
                closes.Add(close);
                menus.Add(KindAt(i) == SessionKind.Ssh ? new Rectangle(close.Left - 20, close.Top, 16, 16) : Rectangle.Empty);
                x += w + 4;
            }
            _tabRects = tabs.ToArray();
            _tabCloseRects = closes.ToArray();
            _tabMenuRects = menus.ToArray();
            _addRect = new Rectangle(Math.Min(x, maxTabsRight), 4, AddW, BarHeight - 8);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color accent = ThemeHelper.GetWindowsAccentColor();
            using (var b = new SolidBrush(accent)) g.FillRectangle(b, ClientRectangle);

            for (int i = 0; i < _tabRects.Length; i++)
            {
                var r = _tabRects[i];
                bool act = i == _active;
                using (var tb = new SolidBrush(act ? Blend(accent, Color.White, 0.26f) : Blend(accent, Color.Black, 0.14f)))
                using (var path = DrawHelper.RoundedRect(r, 6)) g.FillPath(tb, path);
                // per-type glyph tile — cached bitmap (never rebuilt per paint), size derives from the tab height
                int tile = Math.Max(14, r.Height - 8);
                g.DrawImageUnscaled(TypeGlyph.Get(KindAt(i), tile), r.X + 6, r.Y + (r.Height - tile) / 2);
                bool hasMenu = !_tabMenuRects[i].IsEmpty;
                var textR = new Rectangle(r.X + tile + 12, r.Y, r.Width - tile - (hasMenu ? 56 : 36), r.Height);
                using (var f = FontHelper.Ui(9.5f, act ? FontStyle.Bold : FontStyle.Regular))
                    TextRenderer.DrawText(g, _tabs[i], f, textR, Color.White,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                if (hasMenu) DrawTabMenuGlyph(g, _tabMenuRects[i]);
                var c = _tabCloseRects[i];
                using (var p = new Pen(Color.FromArgb(220, 255, 255, 255), 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(p, c.Left + 4, c.Top + 4, c.Right - 4, c.Bottom - 4);
                    g.DrawLine(p, c.Right - 4, c.Top + 4, c.Left + 4, c.Bottom - 4);
                }
            }

            using (var ab = new SolidBrush(Blend(accent, Color.Black, 0.14f)))
            using (var path = DrawHelper.RoundedRect(_addRect, 6)) g.FillPath(ab, path);
            using (var p = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                int cx = _addRect.Left + _addRect.Width / 2, cy = _addRect.Top + _addRect.Height / 2;
                g.DrawLine(p, cx - 7, cy, cx + 7, cy);
                g.DrawLine(p, cx, cy - 7, cx, cy + 7);
            }

            // live stats readout (rtt · bandwidth · jitter, from the wfreerdp autodetect pipe)
            //
            // ⚠ EndEllipsis is LOAD-BEARING, not cosmetic. This rect is a fixed StatsW px wide while the
            // font is in POINTS, so it scales with DPI and the rect does not: a string that fits at 96 DPI
            // can be half as wide again at 144. Right-aligned text that overruns its rect is clipped at the
            // LEFT, which silently eats the leading status dot and the first word - the reader loses
            // "● disconnected" and keeps the tail. With EndEllipsis it degrades at the right instead and
            // the dot always survives. Do not remove this flag; widen StatsW if more text is needed.
            //
            // (The Reconnect button does NOT overlap this rect - Recalc anchors _statsRect's right edge to
            // _reconnectRect.Left when the button is visible, so the two are disjoint by construction. The
            // cost of the button is that the tabs get less room, not that the readout does.)
            using (var f = FontHelper.Ui(9.5f, FontStyle.Bold))
                TextRenderer.DrawText(g, _stats, f, _statsRect, Color.White,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix
                    | TextFormatFlags.EndEllipsis);

            if (_reconVisible) DrawReconnect(g, accent);   // FRDP-RECONNECT — the active dropped SSH/FTP tab
            if (_touchVisible && !_reconVisible) DrawTouch(g, _touchRect, _touchOn, accent);   // SDL-ENGINE
            if (_pauseVisible) DrawPause(g, _pauseRect, _paused);   // hidden for SSH tabs (no suppress-output)
            DrawFsGlyph(g, _fsRect, _fullscreen);
            DrawGlyph(g, _minRect, 'm');
            DrawGlyph(g, _restoreRect, _maximized ? 'R' : 'r');
            DrawGlyph(g, _closeRect, 'x');
        }

        /// <summary>Fullscreen toggle: OUTWARD corner brackets (expand) when windowed, INWARD (contract) when
        /// fullscreen.</summary>
        private static void DrawFsGlyph(Graphics g, Rectangle r, bool fullscreen)
        {
            int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
            int a = 7, arm = 4;
            using (var p = new Pen(Color.White, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                if (!fullscreen)   // expand — corner Ls opening outward
                {
                    g.DrawLines(p, new[] { new Point(cx - a + arm, cy - a), new Point(cx - a, cy - a), new Point(cx - a, cy - a + arm) });
                    g.DrawLines(p, new[] { new Point(cx + a - arm, cy - a), new Point(cx + a, cy - a), new Point(cx + a, cy - a + arm) });
                    g.DrawLines(p, new[] { new Point(cx - a + arm, cy + a), new Point(cx - a, cy + a), new Point(cx - a, cy + a - arm) });
                    g.DrawLines(p, new[] { new Point(cx + a - arm, cy + a), new Point(cx + a, cy + a), new Point(cx + a, cy + a - arm) });
                }
                else               // contract — corner Ls pointing inward
                {
                    int b = a - 1;
                    g.DrawLines(p, new[] { new Point(cx - b - arm, cy - b), new Point(cx - b, cy - b), new Point(cx - b, cy - b - arm) });
                    g.DrawLines(p, new[] { new Point(cx + b + arm, cy - b), new Point(cx + b, cy - b), new Point(cx + b, cy - b - arm) });
                    g.DrawLines(p, new[] { new Point(cx - b - arm, cy + b), new Point(cx - b, cy + b), new Point(cx - b, cy + b + arm) });
                    g.DrawLines(p, new[] { new Point(cx + b + arm, cy + b), new Point(cx + b, cy + b), new Point(cx + b, cy + b + arm) });
                }
            }
        }

        /// <summary>FIN-SSH-TAB-MENU — vertical ellipsis "⋮": three small filled dots, same white-on-accent
        /// stroke weight as the window-control glyphs above (visually consistent, not a new style).</summary>
        private static void DrawTabMenuGlyph(Graphics g, Rectangle r)
        {
            int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
            using (var b = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
            {
                const int dot = 3, gap = 5;
                for (int i = -1; i <= 1; i++) g.FillEllipse(b, cx - dot / 2, cy + i * gap - dot / 2, dot, dot);
            }
        }

        private static void DrawGlyph(Graphics g, Rectangle r, char kind)
        {
            int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
            using (var p = new Pen(Color.White, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                if (kind == 'm') g.DrawLine(p, cx - 7, cy + 5, cx + 7, cy + 5);
                else if (kind == 'r') g.DrawRectangle(p, cx - 6, cy - 6, 12, 12);
                else if (kind == 'R')   // restore-down: two offset squares
                {
                    g.DrawRectangle(p, cx - 7, cy - 3, 10, 10);
                    g.DrawLine(p, cx - 3, cy - 7, cx + 7, cy - 7);
                    g.DrawLine(p, cx + 7, cy - 7, cx + 7, cy + 3);
                }
                else { g.DrawLine(p, cx - 6, cy - 6, cx + 6, cy + 6); g.DrawLine(p, cx + 6, cy - 6, cx - 6, cy + 6); }
            }
        }

        private void DrawReconnect(Graphics g, Color accent)
        {
            var r = _reconnectRect;
            Color bg = _reconBusy ? Blend(accent, Color.Black, 0.22f) : Blend(accent, Color.White, 0.32f);
            using (var b = new SolidBrush(bg))
            using (var path = DrawHelper.RoundedRect(r, 6)) g.FillPath(b, path);
            if (!_reconBusy)
                using (var p = new Pen(Color.FromArgb(235, 255, 255, 255), 1.6f))
                using (var path2 = DrawHelper.RoundedRect(r, 6)) g.DrawPath(p, path2);
            using (var f = FontHelper.Ui(9f, FontStyle.Bold))
                TextRenderer.DrawText(g, _reconBusy ? "Reconnecting…" : "⟳  Reconnect", f, r,
                    _reconBusy ? Color.FromArgb(205, 255, 255, 255) : Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        /// <summary>SDL-ENGINE — the Touch/Mouse switch shows the CURRENT mode: a pointing hand on a lit backplate in
        /// touch mode, a pointer arrow on the plain bar in mouse mode ("lit = touch"). Outlined GDI+ paths only, no
        /// font glyphs. (The first touch glyph, a dot with two ripples above it, read as a Wi-Fi symbol.)</summary>
        private static void DrawTouch(Graphics g, Rectangle r, bool touchOn, Color accent)
        {
            int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
            if (touchOn)
            {
                var plate = new Rectangle(cx - 16, cy - 14, 32, 28);
                using (var b = new SolidBrush(Blend(accent, Color.White, 0.26f)))
                using (var path = DrawHelper.RoundedRect(plate, 6)) g.FillPath(b, path);
                using (var hand = HandPath(cx - 8f, cy - 11f))
                using (var p = new Pen(Color.White, 1.6f) { LineJoin = LineJoin.Round })
                    g.DrawPath(p, hand);
            }
            else
            {
                // a classic pointer arrow, tip at the top left
                var pts = new[]
                {
                    new Point(cx - 5, cy - 9), new Point(cx - 5, cy + 7), new Point(cx - 1, cy + 3),
                    new Point(cx + 2, cy + 9), new Point(cx + 4, cy + 8), new Point(cx + 1, cy + 2), new Point(cx + 6, cy + 2),
                };
                using (var p = new Pen(Color.White, 1.6f) { LineJoin = LineJoin.Round })
                    g.DrawPolygon(p, pts);
            }
        }

        /// <summary>A pointing hand in a 16 x 22 box with its top-left at (x, y): the index finger raised, three
        /// folded fingers, the thumb at the left - the shape of the Windows link-select cursor.</summary>
        private static GraphicsPath HandPath(float x, float y)
        {
            var p = new GraphicsPath();
            p.AddArc(x + 4f, y + 0f, 4f, 4f, 180, 180);           // index fingertip
            p.AddLine(x + 8f, y + 2f, x + 8f, y + 10f);           // index finger, right side
            p.AddArc(x + 8f, y + 8.5f, 3f, 3f, 180, 180);         // middle knuckle
            p.AddArc(x + 11f, y + 9.2f, 2.8f, 3f, 180, 180);      // ring knuckle
            p.AddArc(x + 13.8f, y + 10f, 2.2f, 3f, 180, 180);     // little knuckle
            p.AddLine(x + 16f, y + 11.5f, x + 16f, y + 15.5f);    // palm, right side
            p.AddArc(x + 10f, y + 11f, 6f, 10f, 0, 90);           // palm, bottom right
            p.AddLine(x + 13f, y + 21f, x + 7.5f, y + 21f);       // wrist
            p.AddLine(x + 7.5f, y + 21f, x + 4.5f, y + 18f);      // palm, bottom left
            p.AddLine(x + 4.5f, y + 18f, x + 1.2f, y + 14.6f);    // thumb, underside
            p.AddArc(x + 0f, y + 12.2f, 3f, 3f, 135, 180);        // thumb tip
            p.AddLine(x + 3.1f, y + 13.3f, x + 4f, y + 14f);      // thumb, top
            p.AddLine(x + 4f, y + 14f, x + 4f, y + 2f);           // index finger, left side
            p.CloseFigure();
            return p;
        }

        private static void DrawPause(Graphics g, Rectangle r, bool paused)
        {
            int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
            using (var w = new SolidBrush(Color.White))
            {
                if (paused)
                    g.FillPolygon(w, new[] { new Point(cx - 5, cy - 7), new Point(cx - 5, cy + 7), new Point(cx + 7, cy) });   // ▶ resume
                else
                {
                    g.FillRectangle(w, cx - 6, cy - 7, 4, 14);   // ▮▮ pause
                    g.FillRectangle(w, cx + 2, cy - 7, 4, 14);
                }
            }
        }

        /// <summary>The tab under a point, or -1. Public so the host can hit-test a drag against this bar.</summary>
        public int TabIndexAt(Point clientPt)
        {
            for (int i = 0; i < _tabRects.Length; i++)
                if (_tabRects[i].Contains(clientPt)) return i;
            return -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            // Middle-click a tab = tear it into its own window (a discrete, non-drag trigger).
            if (e.Button == MouseButtons.Middle)
            {
                int t = TabIndexAt(e.Location);
                if (t >= 0) TabTearRequested?.Invoke(t);
                return;
            }
            // Right-click a tab = the per-type quick menu (FRDP-POLISH-2). The host decides the items.
            if (e.Button == MouseButtons.Right)
            {
                int t = TabIndexAt(e.Location);
                if (t >= 0) TabRightClicked?.Invoke(t, PointToScreen(e.Location));
                return;
            }
            if (e.Button != MouseButtons.Left) return;   // window controls are left-only

            if (_reconVisible && !_reconBusy && _reconnectRect.Contains(e.Location)) { ReconnectClicked?.Invoke(); return; }   // FRDP-RECONNECT
            if (_closeRect.Contains(e.Location)) { CloseClicked?.Invoke(); return; }
            if (_restoreRect.Contains(e.Location)) { RestoreClicked?.Invoke(); return; }
            if (_minRect.Contains(e.Location)) { MinimizeClicked?.Invoke(); return; }
            if (_fsRect.Contains(e.Location)) { FullscreenToggle?.Invoke(); return; }
            if (_pauseVisible && _pauseRect.Contains(e.Location)) { PauseToggled?.Invoke(); return; }
            if (_touchVisible && !_reconVisible && _touchRect.Contains(e.Location)) { TouchToggled?.Invoke(); return; }   // SDL-ENGINE
            if (_addRect.Contains(e.Location)) { AddClicked?.Invoke(); return; }
            for (int i = 0; i < _tabRects.Length; i++)
            {
                // FIN-SSH-TAB-MENU — a left-click on the "⋮" opens the SAME per-type quick menu right-click does
                // (touch has no right-click gesture); reuses TabRightClicked so the host needs no new plumbing.
                if (i < _tabMenuRects.Length && !_tabMenuRects[i].IsEmpty && _tabMenuRects[i].Contains(e.Location))
                { TabRightClicked?.Invoke(i, PointToScreen(e.Location)); return; }
                if (i < _tabCloseRects.Length && _tabCloseRects[i].Contains(e.Location)) { TabCloseClicked?.Invoke(i); return; }
                if (_tabRects[i].Contains(e.Location))
                {
                    // Defer the switch: this press might be a click (switch tab) or a drag (tear out). Decide on
                    // move/up. WinForms captures the mouse on a control press, so we get every move until release.
                    _pressTab = i; _pressScreen = PointToScreen(e.Location); _dragging = false; Capture = true;
                    return;
                }
            }
            BackgroundMouseDown?.Invoke(e);   // bar background = the window's title region (windowed host)
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_pressTab < 0 || (e.Button & MouseButtons.Left) == 0) return;
            Point scr = PointToScreen(e.Location);
            if (!_dragging)
            {
                int dx = scr.X - _pressScreen.X, dy = scr.Y - _pressScreen.Y;
                if (dx * dx + dy * dy >= DragThreshold * DragThreshold) _dragging = true;
            }
            if (_dragging) TabDragMove?.Invoke(_pressTab, scr);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_pressTab < 0) return;
            int tab = _pressTab; bool dragged = _dragging;
            Point scr = PointToScreen(e.Location);
            _pressTab = -1; _dragging = false; Capture = false;
            if (dragged) TabDragDrop?.Invoke(tab, scr);   // a real drag → tear/merge/cancel is the host's call
            else TabClicked?.Invoke(tab);                 // no drag → it was a click → switch to that tab
        }

        private static Color Blend(Color a, Color b, float t)
            => Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }
}
