using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Finestra.Core;
using Finestra.Helpers;
using Finestra.UI.Controls;

namespace Finestra.UI
{
    /// <summary>
    /// The app shell: a borderless window with an owner-painted Windows-accent title bar (hamburger + title +
    /// close), a hamburger flyout (New Connection / Settings / Theme / About / Exit) and a themed content area.
    /// Accent + dark/light come from <see cref="ThemeHelper"/> — OS-BRANCHED: DWM\AccentColor on 10/11,
    /// Explorer\Accent\AccentColor on 8.1/RT — and recolor LIVE with no restart, driven by BOTH the reliable
    /// WM_DWMCOLORIZATIONCOLORCHANGED signal (fires on 8.1 AND 10/11) and SystemEvents. The chrome is hand-rolled
    /// GDI+ (no third-party UI library) so every line is RT ARM32-safe. Part A shell — New/Settings/About are
    /// stubs until Parts B/C land.
    /// </summary>
    public sealed class MainForm : Form
    {
        private const int BarH = 46;

        private Panel _header, _content;
        private TableLayoutPanel _outer, _stack;
        private GlyphButton _menuBtn, _closeBtn;
        private Label _emptyTitle, _emptyHint;
        private RoundedButton _newBtn, _newBtn2;
        private Panel _listHost, _topBar;
        private Label _listTitle;
        private ThemedScrollPanel _listScroll;
        private ThemedContextMenuStrip _flyout;
        private ToolStripMenuItem _miSystem, _miLight, _miDark;

        private bool _drag;
        private Point _dragStart;

        // FRDP-POLISH — tray + close behavior
        private NotifyIcon _tray;
        private readonly bool _startHidden;   // launched with /tray → start hidden to the tray
        private bool _allowVisible;           // gate for SetVisibleCore when starting hidden
        private bool _forceExit;              // an explicit Exit (hamburger / tray / dialog) → bypass CloseAction

        public MainForm() : this(false) { }

        public MainForm(bool startHidden)
        {
            _startHidden = startHidden;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(900, 580);
            MinimumSize = new Size(600, 420);
            AutoScaleMode = AutoScaleMode.Font;   // RT DPI model: system-aware font scaling (not per-monitor)
            Font = FontHelper.Ui(9.75f);
            Text = "Finestra";
            DoubleBuffered = true;
            ThemedChrome.SetAppIcon(this);        // app icon on taskbar / Alt-Tab

            BuildChrome();
            BuildContent();
            BuildFlyout();
            BuildTray();

            ApplyTheme();
            RefreshConnections();
            ThemeHelper.ThemeChanged += ApplyTheme;
            ThemeHelper.StartListening();
        }

        /// <summary>Start hidden when launched with /tray: suppress the very first Show so no window flashes.</summary>
        protected override void SetVisibleCore(bool value)
        {
            if (_startHidden && !_allowVisible) { base.SetVisibleCore(false); return; }
            base.SetVisibleCore(value);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // body width is known now — re-stretch rows and refresh the scroll range
            foreach (Control r in _listScroll.Host.Controls) r.Width = _listScroll.Host.ClientSize.Width;
            _listScroll.RelayoutContent();
        }

        // ── chrome ─────────────────────────────────────────────────────────────
        private void BuildChrome()
        {
            _header = new Panel { Dock = DockStyle.Top, Height = BarH };
            _header.Paint += HeaderPaint;
            _header.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left && WindowState == FormWindowState.Normal) { _drag = true; _dragStart = e.Location; } };
            _header.MouseMove += (s, e) => { if (_drag) Location = new Point(Location.X + e.X - _dragStart.X, Location.Y + e.Y - _dragStart.Y); };
            _header.MouseUp += (s, e) => _drag = false;
            _header.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleMaximize(); };

            _menuBtn = new GlyphButton(GlyphButton.Glyph.Menu) { Dock = DockStyle.Left, Width = 52, TabStop = false };
            _menuBtn.Click += (s, e) => _flyout.Show(_menuBtn, new Point(0, _menuBtn.Height));

            _closeBtn = new GlyphButton(GlyphButton.Glyph.Close) { Dock = DockStyle.Right, Width = 52, TabStop = false };
            _closeBtn.Click += (s, e) => Close();

            _header.Controls.Add(_menuBtn);    // docks Left
            _header.Controls.Add(_closeBtn);   // docks Right

            _content = new Panel { Dock = DockStyle.Fill };

            Controls.Add(_content);            // Fill added first → docks around the Top header
            Controls.Add(_header);
        }

        private void HeaderPaint(object sender, PaintEventArgs e)
        {
            // Title painted in the clear band BETWEEN the hamburger (left) and close (right) glyph buttons.
            int left = _menuBtn.Width + 6;
            int right = _closeBtn.Width + 6;
            var rect = new Rectangle(left, 0, Math.Max(1, _header.Width - left - right), BarH);
            using (var f = FontHelper.Ui(13f, FontStyle.Bold))
                TextRenderer.DrawText(e.Graphics, "Finestra", f, rect, Color.White,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        // ── content (empty state for A1) ────────────────────────────────────────
        private void BuildContent()
        {
            _outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            _outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            _outer.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            _outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _outer.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));

            _stack = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 3, Anchor = AnchorStyles.None };
            _stack.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _emptyTitle = new Label { AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(0, 0, 0, 6), Text = "No connections yet", Font = FontHelper.Ui(15f, FontStyle.Bold) };
            _emptyHint = new Label { AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(0, 0, 0, 18), Text = "Create a connection to get started.", Font = FontHelper.Ui(10f) };
            _newBtn = new RoundedButton { Kind = RoundedButtonKind.Primary, Text = "New Connection", Anchor = AnchorStyles.None, Width = 200, Height = 42, Margin = new Padding(0), Font = FontHelper.Ui(10.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter };
            _newBtn.Click += (s, e) => NewConnection();

            _stack.Controls.Add(_emptyTitle, 0, 0);
            _stack.Controls.Add(_emptyHint, 0, 1);
            _stack.Controls.Add(_newBtn, 0, 2);
            _outer.Controls.Add(_stack, 0, 1);

            // ── list view (shown when there are connections) ──
            _listHost = new Panel { Dock = DockStyle.Fill, Visible = false };
            _topBar = new Panel { Dock = DockStyle.Top, Height = 56 };
            _listTitle = new Label { AutoSize = false, Dock = DockStyle.Fill, Text = "Connections", Font = FontHelper.Ui(13.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(18, 0, 0, 0) };
            _newBtn2 = new RoundedButton { Kind = RoundedButtonKind.Primary, Text = "New", Width = 96, Height = 36, Dock = DockStyle.Right, Margin = new Padding(0), Font = FontHelper.Ui(10f, FontStyle.Bold) };
            _newBtn2.Click += (s, e) => NewConnection();
            var newWrap = new Panel { Dock = DockStyle.Right, Width = 116, Padding = new Padding(8, 10, 16, 10) };
            newWrap.Controls.Add(_newBtn2);
            _topBar.Controls.Add(_listTitle);
            _topBar.Controls.Add(newWrap);
            _listScroll = new ThemedScrollPanel { Dock = DockStyle.Fill };
            _listHost.Controls.Add(_listScroll);
            _listHost.Controls.Add(_topBar);

            _content.Controls.Add(_outer);      // empty state (Dock=Fill)
            _content.Controls.Add(_listHost);   // list view (Dock=Fill) — visibility toggled by RefreshConnections
        }

        // ── connection list ─────────────────────────────────────────────────────
        private void RefreshConnections()
        {
            var items = ConnectionStore.Instance.Items;
            bool any = items.Count > 0;
            _outer.Visible = !any;
            _listHost.Visible = any;
            if (!any) return;

            _listScroll.Host.Controls.Clear();
            int y = 8;
            int w = Math.Max(10, _listScroll.Host.ClientSize.Width);
            foreach (var cp in items)
            {
                var row = new ConnectionRow(cp)
                {
                    Location = new Point(0, y),
                    Width = w,
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };
                row.ConnectClicked += Connect;
                row.EditClicked += EditConnection;
                row.DeleteClicked += DeleteConnection;
                _listScroll.Host.Controls.Add(row);
                y += row.Height + 8;
            }
            _listScroll.Host.Height = y + 8;
            _listScroll.RelayoutContent();
        }

        private void NewConnection()
        {
            using (var f = new ConnectionEditorForm(null))
                if (f.ShowDialog(this) == DialogResult.OK && f.Result != null)
                {
                    ConnectionStore.Instance.AddOrUpdate(f.Result);
                    RefreshConnections();
                }
        }

        private void EditConnection(ConnectionProfile cp)
        {
            using (var f = new ConnectionEditorForm(cp))
                if (f.ShowDialog(this) == DialogResult.OK && f.Result != null)
                {
                    ConnectionStore.Instance.AddOrUpdate(f.Result);
                    RefreshConnections();
                }
        }

        private void DeleteConnection(ConnectionProfile cp)
        {
            if (ConfirmDialog.Ask(this, "Delete \"" + (string.IsNullOrEmpty(cp.Name) ? cp.Host : cp.Name) + "\"?", "Finestra"))
            {
                ConnectionStore.Instance.Remove(cp.Id);
                RefreshConnections();
            }
        }

        private void Connect(ConnectionProfile cp)
        {
            // FRDP-SSH-BUILD-2 — SSH now joins the tabbed SessionHost shell BESIDE RDP tabs (routed to the windowed
            // host, since an SSH profile carries no Fullscreen flag). The standalone SshTerminalForm stays compiled
            // and reachable, but the tabbed shell is the product path.
            if (cp.Type == ConnectionType.Ssh)
            {
                SessionHost.ConnectOrAdd(cp, this);
                return;
            }

            // FRDP-FTP-BUILD-2 — FTP joins the tabbed SessionHost shell BESIDE RDP + SSH (windowed host). The
            // standalone FtpBrowserForm stays compiled/reachable, but the tabbed shell is the product path.
            if (cp.Type == ConnectionType.Ftp)
            {
                SessionHost.ConnectOrAdd(cp, this);
                return;
            }

            // Embed (default) = Finestra hosts the chromeless wfreerdp child in a tabbed SessionHost; the
            // connection's own Fullscreen toggle then picks the presentation (borderless fullscreen + hover
            // overlay, or a resizable window with a persistent tab/title bar). Window = the proven UI-1B
            // shell-out (wfreerdp in its own window). Preserved as a setting so the UI-1B behavior never regresses.
            if (string.Equals(AppSettings.Instance.ConnectMode, "Window", StringComparison.OrdinalIgnoreCase))
            {
                var pb = Screen.PrimaryScreen.Bounds;
                var res = RdpLauncher.Launch(cp, pb.Width, pb.Height, useStdin: true);   // own window (no /parent-window)
                if (!res.Ok)
                    MessageBox.Show(this, res.Error ?? "Failed to launch wfreerdp.", "Finestra", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            SessionHost.ConnectOrAdd(cp, this);
        }

        // ── hamburger flyout ────────────────────────────────────────────────────
        private void BuildFlyout()
        {
            _flyout = new ThemedContextMenuStrip { Font = FontHelper.Ui(9.75f) };

            var miNew = new ToolStripMenuItem("New Connection…");
            miNew.Click += (s, e) => NewConnection();
            var miSettings = new ToolStripMenuItem("Settings…");
            miSettings.Click += (s, e) => OpenSettings();

            var miTheme = new ToolStripMenuItem("Theme");
            _miSystem = new ToolStripMenuItem("System") { Tag = ThemeMode.System };
            _miLight = new ToolStripMenuItem("Light") { Tag = ThemeMode.Light };
            _miDark = new ToolStripMenuItem("Dark") { Tag = ThemeMode.Dark };
            EventHandler pick = (s, e) => SetTheme((ThemeMode)((ToolStripMenuItem)s).Tag);
            _miSystem.Click += pick; _miLight.Click += pick; _miDark.Click += pick;
            miTheme.DropDownItems.AddRange(new ToolStripItem[] { _miSystem, _miLight, _miDark });
            miTheme.DropDownOpening += (s, e) => SyncThemeChecks();

            var miAbout = new ToolStripMenuItem("About Finestra");
            miAbout.Click += (s, e) => ShowAbout();
            var miExit = new ToolStripMenuItem("Exit");
            miExit.Click += (s, e) => RequestExit();   // explicit Exit → real quit (bypasses close-to-tray)

            _flyout.Items.AddRange(new ToolStripItem[]
            {
                miNew, miSettings, new ToolStripSeparator(),
                miTheme, new ToolStripSeparator(),
                miAbout, miExit
            });
        }

        private void SyncThemeChecks()
        {
            var m = ThemeHelper.Mode;
            if (_miSystem != null) _miSystem.Checked = m == ThemeMode.System;
            if (_miLight != null) _miLight.Checked = m == ThemeMode.Light;
            if (_miDark != null) _miDark.Checked = m == ThemeMode.Dark;
        }

        private void SetTheme(ThemeMode mode)
        {
            ThemeHelper.SetMode(mode);   // raises ThemeChanged → ApplyTheme (live recolor)
            try { var s = AppSettings.Instance; s.ThemeMode = mode.ToString(); s.Save(); } catch { }
            SyncThemeChecks();
        }

        // ── theming ─────────────────────────────────────────────────────────────
        private void ApplyTheme()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke((Action)ApplyTheme); } catch { } return; }

            bool dark = ThemeHelper.IsDark;
            Color accent = ThemeHelper.GetWindowsAccentColor();
            Color bg = dark ? Color.FromArgb(32, 32, 36) : Color.FromArgb(245, 245, 248);
            Color fg = dark ? Color.FromArgb(236, 236, 240) : Color.FromArgb(28, 28, 32);
            Color hint = dark ? Color.FromArgb(150, 150, 156) : Color.FromArgb(110, 110, 118);

            BackColor = bg;
            _header.BackColor = accent;
            _menuBtn.Accent = accent; _menuBtn.BackColor = accent;
            _closeBtn.Accent = accent; _closeBtn.BackColor = accent;
            _content.BackColor = bg;
            _outer.BackColor = bg;
            _stack.BackColor = bg;
            _emptyTitle.ForeColor = fg;
            _emptyHint.ForeColor = hint;

            _listHost.BackColor = bg;
            _topBar.BackColor = bg;
            _listTitle.ForeColor = fg;

            _header.Invalidate(true);
            _menuBtn.Invalidate(); _closeBtn.Invalidate();
            _content.Invalidate(true);
        }

        private void OpenSettings()
        {
            using (var f = new AppSettingsForm())
            {
                f.ShowDialog(this);
                if (f.WfreerdpChanged) RefreshConnections();   // path change may affect launch availability
            }
        }

        private void ShowAbout()
        {
            using (var f = new AboutForm()) f.ShowDialog(this);
        }

        private void ToggleMaximize()
            => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;

        // ── system tray (FRDP-POLISH) ─────────────────────────────────────────────
        private void BuildTray()
        {
            _tray = new NotifyIcon { Icon = ThemedChrome.AppIcon, Text = "Finestra", Visible = true };
            var menu = new ThemedContextMenuStrip { Font = FontHelper.Ui(9.75f) };
            var miOpen = new ToolStripMenuItem("Open Finestra");
            miOpen.Click += (s, e) => RestoreFromTray();
            var miExit = new ToolStripMenuItem("Exit");
            miExit.Click += (s, e) => RequestExit();
            menu.Items.AddRange(new ToolStripItem[] { miOpen, new ToolStripSeparator(), miExit });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => RestoreFromTray();
        }

        private void HideToTray()
        {
            Hide();                 // drops the taskbar button; the tray icon (Visible=true) stays
            ShowInTaskbar = false;
            FileLog.Line("[TRAY] hidden to tray (sessions alive=" + SessionHost.ActiveSessionCount + ")");
        }

        private void RestoreFromTray()
        {
            _allowVisible = true;
            ShowInTaskbar = true;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            FileLog.Line("[TRAY] restored from tray");
        }

        /// <summary>Explicit Exit (hamburger / tray / the Ask dialog): quit for real, past any close-to-tray.</summary>
        private void RequestExit()
        {
            _forceExit = true;
            Close();
        }

        /// <summary>FIN-SINGLETON — a second launch signaled us: surface the manager. Shows it if hidden
        /// (including hidden-to-tray), un-minimizes via SW_RESTORE (returns to the PRE-minimize state —
        /// a Maximized window stays Maximized, unlike RestoreFromTray's forced Normal), then takes
        /// foreground (the exiting second instance granted us AllowSetForegroundWindow). Session hosts
        /// are untouched — except a fullscreen host's TopMost is dropped (one-shot, re-asserted on its
        /// next activation) so the manager is GENUINELY visible, not buried behind the topmost session.</summary>
        public void ActivateFromSecondInstance()
        {
            try
            {
                _allowVisible = true;
                if (!Visible) { ShowInTaskbar = true; Show(); }
                if (WindowState == FormWindowState.Minimized) ShowWindowNative(Handle, SW_RESTORE);
                SessionHost.DropFullscreenTopMostForManager();
                Activate();
                BringToFront();
                SetForegroundWindow(Handle);
                FileLog.Line("[SINGLETON] manager activated by a second launch");
            }
            catch { /* activation is best-effort — never take the app down for it */ }
        }

        // ── close behavior: Ask / MinimizeToTray / Exit, with a live-session guard (FRDP-POLISH) ──
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Only govern a USER close (✕ / Alt-F4 / our RequestExit). Windows shutdown / Application.Exit pass through.
            if (e.CloseReason != CloseReason.UserClosing) { base.OnFormClosing(e); return; }

            if (!_forceExit)
            {
                var action = Startup.ParseCloseAction(AppSettings.Instance.CloseAction);
                if (action == CloseAction.Ask)
                {
                    using (var dlg = new CloseChoiceDialog())
                    {
                        var r = dlg.ShowDialog(this);
                        if (r != DialogResult.OK) { e.Cancel = true; return; }   // dialog ✕/Esc → cancel the close
                        if (dlg.Remember)
                        {
                            AppSettings.Instance.CloseAction = dlg.Choice.ToString();
                            AppSettings.Instance.Save();
                        }
                        if (dlg.Choice == CloseAction.MinimizeToTray) { e.Cancel = true; HideToTray(); return; }
                        // dlg.Choice == Exit → fall through to the exit path
                    }
                }
                else if (action == CloseAction.MinimizeToTray) { e.Cancel = true; HideToTray(); return; }
                // action == Exit → fall through to the exit path
            }

            // EXIT PATH — killing live RDP silently is worse than one prompt.
            int n = SessionHost.ActiveSessionCount;
            if (n > 0)
            {
                if (!ConfirmDialog.Ask(this, "Close " + n + " active session" + (n == 1 ? "" : "s") + "?", "Finestra"))
                { e.Cancel = true; _forceExit = false; return; }
            }
            SessionHost.CloseAllHosts();   // FRDP-FIXSWEEP B1 — graceful: close hosts so wfreerdp children are killed (job = backstop)
            try { _tray.Visible = false; _tray.Dispose(); } catch { }   // remove the icon NOW (no tray ghost)
            FileLog.Line("[EXIT] manager closing, killed sessions=" + n);
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { _tray?.Dispose(); } catch { }
            ThemeHelper.ThemeChanged -= ApplyTheme;
            ThemeHelper.StopListening();
            base.OnFormClosed(e);
        }

        // ── native: live accent signal + borderless-maximize clamp ──────────────
        protected override void WndProc(ref Message m)
        {
            const int WM_GETMINMAXINFO = 0x0024;
            const int WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
            switch (m.Msg)
            {
                case WM_DWMCOLORIZATIONCOLORCHANGED:
                    ThemeHelper.NotifyAccentChanged();   // reliable accent signal on 8.1 AND 10/11
                    base.WndProc(ref m);
                    return;
                case WM_GETMINMAXINFO:
                    ConstrainMaximize(m.LParam);          // borderless maximize must not cover the taskbar
                    base.WndProc(ref m);
                    return;
                default:
                    base.WndProc(ref m);
                    return;
            }
        }

        private void ConstrainMaximize(IntPtr lParam)
        {
            try
            {
                var mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));
                IntPtr mon = MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST);
                if (mon != IntPtr.Zero)
                {
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                    if (GetMonitorInfo(mon, ref mi))
                    {
                        RECT work = mi.rcWork, full = mi.rcMonitor;
                        mmi.ptMaxPosition.X = work.Left - full.Left;
                        mmi.ptMaxPosition.Y = work.Top - full.Top;
                        mmi.ptMaxSize.X = work.Right - work.Left;
                        mmi.ptMaxSize.Y = work.Bottom - work.Top;
                        mmi.ptMinTrackSize.X = MinimumSize.Width;
                        mmi.ptMinTrackSize.Y = MinimumSize.Height;
                        Marshal.StructureToPtr(mmi, lParam, true);
                    }
                }
            }
            catch { /* leave default maximize bounds on any failure */ }
        }

        private const int MONITOR_DEFAULTTONEAREST = 2;
        private const int SW_RESTORE = 9;   // FIN-SINGLETON — restores to the pre-minimize state (Normal OR Maximized)
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        [DllImport("user32.dll", EntryPoint = "ShowWindow")] private static extern bool ShowWindowNative(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
        [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
    }
}
