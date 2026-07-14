using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Finestra.Core;
using Finestra.Helpers;
using Finestra.UI.Controls;

namespace Finestra.UI
{
    /// <summary>
    /// The embed host: one top-level window that parents one or more <c>wfreerdp.exe</c> sessions as WS_CHILD
    /// windows via <c>/parent-window</c> (honored by the Windows client — wf_client.c:1492/:460). Each session is
    /// a tab. ONE session model, TWO presentations, chosen by the connection's <c>Fullscreen</c> toggle
    /// (FRDP-UI-WINDOWED — before this, embed was ALWAYS fullscreen and the toggle was ignored):
    ///
    ///  • <b>Fullscreen ON</b>  — borderless screen-filling window, TopMost, taskbar covered; the tab bar rides in
    ///    the auto-hide <see cref="OverlayBar"/> revealed by hovering the top edge.
    ///  • <b>Fullscreen OFF</b> — a normal RESIZABLE window whose PERSISTENT <see cref="SessionTabBar"/> is docked
    ///    at the top and doubles as the title bar (drag to move, double-press to maximize, Min/Restore/Close).
    ///
    /// Both embed into <see cref="_embedHost"/>, a plain child Panel that is the <c>/parent-window</c> target, so
    /// the child is sized by one code path (<see cref="FitChild"/>) in both modes. wfreerdp itself shows no chrome.
    /// The live stats pipe and pause/resume (FRDP-STATS-RECON) ride along unchanged in either bar.
    /// </summary>
    public sealed class SessionHost : Form
    {
        /// <summary>The resizable themed frame around a windowed session (also its resize grab area).</summary>
        private const int Frame = 6;

        // FRDP-POLISH-3 — a new connection JOINS the currently-active host (mstsc-style), windowed OR fullscreen,
        // tracked here via OnActivated. Only when there is NO live host does ConnectOrAdd open a fresh host (of the
        // connection's own Fullscreen presentation). Tear-off still spawns independent hosts directly (not via here).
        private static SessionHost _lastActiveHost;
        private static int _liveHosts;
        private static bool _appExiting;   // FRDP-FIXSWEEP B1 — set while closing all hosts on app exit (suppresses the manager re-show)
        // Every live host, so a tear-drag can hit-test its drop point against other windows' tab bars (merge).
        private static readonly List<SessionHost> _allHosts = new List<SessionHost>();
        private static DragGhost _ghost;

        /// <summary>Total live sessions across every open host (all windows, all tabs) — for the "close N active
        /// session(s)?" confirm before the manager exits.</summary>
        public static int ActiveSessionCount
        {
            get
            {
                int n = 0;
                foreach (var h in _allHosts)
                    if (h != null && !h.IsDisposed) n += h._sessions.Count;
                return n;
            }
        }

        /// <summary>FRDP-FIXSWEEP B1 — close every live host on app exit so their teardown runs (killing the wfreerdp
        /// children). The kill-on-close Job Object is the backstop; this is the graceful path.</summary>
        public static void CloseAllHosts()
        {
            _appExiting = true;
            foreach (var h in _allHosts.ToArray())   // copy — Close mutates _allHosts via OnFormClosed
                try { if (h != null && !h.IsDisposed) h.Close(); } catch { }
            try { _ghost?.Dispose(); _ghost = null; } catch { }   // FRDP-FIXSWEEP B23 — the static drag ghost, at app exit
        }

        /// <summary>FRDP-POLISH-3 — mstsc-style routing: a new connection JOINS the currently-active host if one is
        /// live (whether it is windowed OR fullscreen — the fix for SSH/FTP, which carry no Fullscreen flag, spawning
        /// a stray windowed host while an RDP host was fullscreen). Only with NO live host do we open a fresh host of
        /// the connection's own presentation. Explicit paths are preserved: tear-off spawns its own host; a
        /// connect with no active host opens one.</summary>
        public static void ConnectOrAdd(ConnectionProfile cp, Form manager)
        {
            SessionHost active = (_lastActiveHost != null && !_lastActiveHost.IsDisposed) ? _lastActiveHost : null;
            if (active != null)
            {
                active.AddSession(cp);             // makes the new tab active (RefreshTabs + SetActive)
                active.RevealForAddedSession();    // bring the host forward; flash the overlay if it's fullscreen
            }
            else
            {
                bool fullscreen = cp.Settings != null && cp.Settings.Fullscreen;
                var h = new SessionHost(manager, fullscreen, cp);
                _lastActiveHost = h;
                h.Show();
                h.AddSession(cp);
                if (h._sessions.Count == 0)   // FRDP-FIXSWEEP B6 — the launch failed → close the empty host, don't leave a zombie join target
                {
                    if (_lastActiveHost == h) _lastActiveHost = null;
                    try { h.Close(); } catch { }
                }
            }
        }

        /// <summary>Inferred RDP lifecycle phase shown in the bar (FRDP-POLISH-2). SSH has its own app-owned status.</summary>
        private enum RdpPhase { Connecting, Connected, Failed, Disconnected }

        /// <summary>No-embed backstop: if a launched wfreerdp neither embeds nor exits within this many 250 ms polls
        /// (~50 s), treat it as failed so the bar never spins on "connecting…" forever. The process exiting is the
        /// primary signal; this is the belt-and-suspenders for a hung connect.</summary>
        private const int NoEmbedTimeoutTicks = 200;

        private sealed class Session
        {
            public ConnectionProfile Profile;
            public Process Proc;
            public IntPtr Child;
            public StatsPipe Stats;
            public bool Paused;
            public string LastStats = "—";
            /// <summary>The host that currently owns this session. Re-pointed on a tear-off transplant so the
            /// StatsPipe's Updated event (a single, un-recreatable subscription — the pipe is one-shot per PID)
            /// routes to whichever host is showing the session NOW, not the one it was born in.</summary>
            public SessionHost Owner;
            /// <summary>The exact /w × /h wfreerdp was launched with — compared against the embed area to decide
            /// whether an OversizeMode.Dynamic session needs a one-shot renegotiation once it embeds.</summary>
            public Size Emitted;
            /// <summary>Consecutive child-polls on which the child has actually matched the embed area.</summary>
            public int FitStableTicks;
            /// <summary>Remaining Dynamic renegotiation posts, armed once the child is correctly sized.</summary>
            public int DynFitTicks;
            public bool DynFitArmed;

            /// <summary>FIN-KBD-FREEZE-SCROLL — RDP-only: the child's pan, in <see cref="ClipScrollbar"/>'s own
            /// "0 = bottom-anchored, maxPan = top-anchored" convention. Meaningful only once
            /// <see cref="PanEstablished"/> is true. Persists per-session across tab switches; unused for SSH/FTP.</summary>
            public int PanOffsetY;
            /// <summary>False until this freeze session's UNTOUCHED baseline is set (top-anchored — exactly where
            /// FitChild already left the child, so becoming clipped never moves it on its own) or the user drags —
            /// whichever comes first. Without this, "reset PanOffsetY to a fixed number" can't tell "untouched" from
            /// "a real pan" apart, because the meaning of any fixed number depends on maxPan, which isn't known
            /// until the embed area has actually shrunk (freezing and shrinking are two different moments).</summary>
            public bool PanEstablished;

            /// <summary>FRDP-POLISH-2 — inferred RDP phase for the bar. Connecting → Connected (first stats line);
            /// Failed (died before ever embedding / timed out); Disconnected (died after embedding).</summary>
            public RdpPhase Phase = RdpPhase.Connecting;
            public bool EverEmbedded;
            public int ConnectTicks;

            /// <summary>FRDP-RDP-LIVENESS Part 1 — DateTime.UtcNow.Ticks of the last STATS heartbeat (set the
            /// moment the pipe is started, so total silence from the start counts too, not just going-quiet-later).
            /// A raw long behind Interlocked, not a DateTime field: this is written from the StatsPipe reader
            /// thread and read from the UI-thread tick — a torn 8-byte read/write is a real risk on ARM32/RT.
            /// 0 = not yet started.</summary>
            private long _lastStatsTicksRaw;
            public void TouchStats() { System.Threading.Interlocked.Exchange(ref _lastStatsTicksRaw, DateTime.UtcNow.Ticks); }
            /// <summary>Seconds since the last heartbeat, or -1 if none has ever been recorded (never started —
            /// treated as "don't judge yet", not as infinitely stale, so a start-up race can't false-positive).</summary>
            public double SecondsSinceLastStats()
            {
                long t = System.Threading.Interlocked.Read(ref _lastStatsTicksRaw);
                if (t == 0) return -1;
                return Math.Max(0, (DateTime.UtcNow - new DateTime(t, DateTimeKind.Utc)).TotalSeconds);
            }

            /// <summary>FRDP-SSH-BUILD-2 — set for an SSH tab; null for an RDP tab. When set, this session is an
            /// app-owned terminal (no child HWND, no proc, no stats pipe) and the RDP machinery below is bypassed.
            /// RDP's proven path runs unchanged whenever <see cref="Ssh"/> is null.</summary>
            public SshContent Ssh;
            public bool IsSsh => Ssh != null;

            /// <summary>FRDP-FTP-BUILD-2 — set for an FTP tab; the third app-owned content type (dual-pane browser).
            /// null for RDP + SSH. When set, the RDP HWND machinery is bypassed (like SSH).</summary>
            public FtpContent Ftp;
            public bool IsFtp => Ftp != null;

            public SessionKind Kind => IsSsh ? SessionKind.Ssh : IsFtp ? SessionKind.Ftp : SessionKind.Rdp;

            public string Name => IsSsh ? Ssh.Title : IsFtp ? Ftp.Title : (string.IsNullOrEmpty(Profile.Name) ? Profile.Host : Profile.Name);
        }

        /// <summary>Child-polls (250ms each) the child must already be correctly sized before we ask the server to
        /// resize: wfreerdp must have finished its own window setup and opened the disp channel.</summary>
        private const int DynFitSettleTicks = 6;
        /// <summary>Then a short burst of posts; the engine dedupes once one lands.</summary>
        private const int DynFitRetries = 6;

        /// <summary>FRDP-RDP-LIVENESS Part 1 — how long the STATS heartbeat can go silent before a session is
        /// declared dead. Deliberately conservative: the engine streams ~1 line/sec while healthy, so this is a
        /// 12-15x margin over a single missed beat — a false "dead" verdict on a merely-busy/stressed device is
        /// worse than the frozen-tab bug this closes (see Part 2's under-load proof).</summary>
        private const int HeartbeatDeadSeconds = 15;

        private readonly Form _manager;
        private bool _fullscreen;                // MUTABLE now — a host flips presentation live (FRDP-FS-TOGGLE)
        private readonly List<Session> _sessions = new List<Session>();
        private int _active = -1;

        private readonly Panel _embedHost;      // the /parent-window target in BOTH presentations
        private readonly ClipPanOverlay _rdpPanOverlay;   // FIN-KBD-FREEZE-SCROLL — RDP's pan scrollbar (SSH draws its own; FTP never clips)
        private readonly OverlayBar _bar;       // the auto-hide hover bar (shown only while fullscreen)
        private readonly SessionTabBar _tabBar; // the persistent docked bar (visible only while windowed)
        private readonly Timer _hoverTimer;     // drives the overlay reveal (runs only while fullscreen)
        private readonly Timer _childTimer;
        private Timer _flashTimer;              // FRDP-POLISH-3 — one-shot: briefly reveal the overlay when a tab is added in fullscreen
        private bool _flashing;

        /// <summary>The bar the user currently sees — the seam that lets one host wear either presentation.</summary>
        private ISessionBar Ui => _fullscreen ? (ISessionBar)_bar : _tabBar;

        private Rectangle _mon;
        private bool _barShown;
        private bool _wasMinimized;
        private bool _switching;                  // guards re-entrant fullscreen flips
        private Rectangle _savedWindowedBounds;   // to return to on exit-fullscreen
        private FormWindowState _savedWindowedState = FormWindowState.Normal;

        private string Mode => _fullscreen ? "fullscreen" : "windowed";

        private SessionHost(Form manager, bool fullscreen, ConnectionProfile first)
        {
            _manager = manager;
            _fullscreen = fullscreen;
            try { _mon = Screen.FromPoint(Cursor.Position).Bounds; } catch { _mon = Screen.PrimaryScreen.Bounds; }

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true;                 // the SESSION owns a taskbar button — that is what Minimize drops to
            DoubleBuffered = true;
            ThemedChrome.SetAppIcon(this);
            Text = "Finestra — " + (first != null && !string.IsNullOrEmpty(first.Name) ? first.Name : (first != null ? first.Host : "session"));

            _embedHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black, TabStop = false };
            _childTimer = new Timer { Interval = 250 };
            _childTimer.Tick += ChildTick;

            // BOTH bars exist for the life of the host, so it can flip presentation live without re-creating them.
            // The docked persistent bar is Visible only while windowed; the hover overlay is Shown only while
            // fullscreen. Both raise the same events → shared handlers wired once.
            _tabBar = new SessionTabBar { Dock = DockStyle.Top };
            _tabBar.TabClicked += SetActive;
            _tabBar.TabCloseClicked += CloseSession;
            _tabBar.AddClicked += OnAdd;
            _tabBar.MinimizeClicked += OnMinimize;
            _tabBar.RestoreClicked += OnRestoreOrMaximize;
            _tabBar.CloseClicked += OnCloseHost;
            _tabBar.PauseToggled += OnPauseToggled;
            _tabBar.FullscreenToggle += OnFullscreenToggle;
            _tabBar.BackgroundMouseDown += OnBarBackgroundMouseDown;
            _tabBar.TabTearRequested += OnTabTear;
            _tabBar.TabDragMove += OnTabDragMove;
            _tabBar.TabDragDrop += OnTabDragDrop;
            _tabBar.TabRightClicked += OnTabRightClick;
            _tabBar.ReconnectClicked += OnReconnectClicked;   // FRDP-RECONNECT

            _bar = new OverlayBar { Owner = this };
            _bar.TabClicked += SetActive;
            _bar.TabCloseClicked += CloseSession;
            _bar.AddClicked += OnAdd;
            _bar.MinimizeClicked += OnMinimize;
            _bar.RestoreClicked += OnRestoreOrMaximize;
            _bar.CloseClicked += OnCloseHost;
            _bar.PauseToggled += OnPauseToggled;
            _bar.FullscreenToggle += OnFullscreenToggle;
            _bar.TabRightClicked += OnTabRightClick;
            _bar.ReconnectClicked += OnReconnectClicked;   // FRDP-RECONNECT

            _hoverTimer = new Timer { Interval = 60 };
            _hoverTimer.Tick += HoverTick;

            Controls.Add(_embedHost);   // Fill added first → docks under the Top bar
            Controls.Add(_tabBar);      // Top; Visible is toggled by the presentation

            // FIN-KBD-FREEZE-SCROLL — a narrow strip, positioned/shown explicitly by RefreshRdpPanOverlay (not
            // docked — it must sit at a specific right-edge inset, and stay hidden unless the active RDP session
            // is actually frozen+clipped). A WinForms child of _embedHost, same as the RDP child's native parent.
            _rdpPanOverlay = new ClipPanOverlay { Visible = false };
            _embedHost.Controls.Add(_rdpPanOverlay);
            _rdpPanOverlay.PanChanged += OnRdpPanChanged;

            _embedHost.SizeChanged += (s, e) => { FitAllChildren(); RefreshRdpPanOverlay(); };

            // FIN-KEYBOARD — inset the CONTENT (not the window) while the touch keyboard covers us: the
            // embed area shrinks exactly like a user resize, so the existing fit + Dynamic-renegotiation
            // machinery does the rest. One debounced step, never a tween (Tegra 3 + live RDP child).
            KeyboardInset.KeyboardRectChanged += OnKeyboardRect;
            KeyboardInset.Register();
            Disposed += (s, e) => { KeyboardInset.KeyboardRectChanged -= OnKeyboardRect; KeyboardInset.Unregister(); };

            if (_fullscreen)
            {
                _tabBar.Visible = false;          // Dock=Top + invisible → _embedHost (Fill) takes the whole client
                BackColor = Color.Black;
                Bounds = _mon;                    // full monitor incl. taskbar → true fullscreen (activated in OnShown)
                TopMost = true;
                if (_manager != null) { try { _manager.Hide(); } catch { } }
            }
            else
            {
                Rectangle work;
                try { work = Screen.FromPoint(Cursor.Position).WorkingArea; } catch { work = Screen.PrimaryScreen.WorkingArea; }
                BackColor = ThemeHelper.GetWindowsAccentColor();   // the 6px frame reads as one chrome with the bar
                Padding = new Padding(Frame);
                MinimumSize = new Size(760, 480);

                Size embed = InitialEmbedSize(first, work);
                ClientSize = new Size(embed.Width + 2 * Frame, embed.Height + SessionTabBar.BarHeight + 2 * Frame);
                Location = new Point(work.X + Math.Max(0, (work.Width - Width) / 2), work.Y + Math.Max(0, (work.Height - Height) / 2));
            }
            _tabBar.SetFullscreen(_fullscreen);

            var forceForm = Handle;             // realize the HWND
            var forceEmbed = _embedHost.Handle; // realize the /parent-window target BEFORE any launch
            _liveHosts++;
            _allHosts.Add(this);
            FileLog.Line("[HOST] ctor mode=" + Mode + " mon=" + _mon + " embedHwnd=0x" + forceEmbed.ToInt64().ToString("X")
                + " liveHosts=" + _liveHosts + " managerVisibleAfterHide=" + (_manager != null ? _manager.Visible.ToString() : "null"));
        }

        // ── FIN-KEYBOARD — touch-keyboard inset (bottom padding composed WITH the windowed frame) ──
        private int _kbInset;             // current keyboard inset (0 = none)
        private bool _kbDroppedTopMost;   // trap 3: fullscreen TopMost dropped while the keyboard is up

        // ── FIN-KBD-FREEZE — session-size freeze while a keyboard is up ──────────────────────────────────
        // The actual fix (independent of WHY the window/embed area got smaller): while frozen, the RDP child
        // is never SetWindowPos'd, the Dynamic watchdog never renegotiates, and each SSH TerminalControl's
        // grid is pinned — the content simply clips at the bottom (where the keyboard is anyway), instead of
        // resizing/rescaling/renegotiating. This neutralizes BOTH our own padding-driven inset AND Windows'
        // own native keyboard-avoidance window move (a REAL WM_ENTER/EXITSIZEMOVE the OS drives on RT 8.1,
        // independent of anything Finestra does) — whichever one shrinks the window, frozen content ignores it.
        private bool _sizeFrozen;

        /// <summary>Freeze/unfreeze every session's size reaction. Unfreezing re-syncs ONCE, immediately, to
        /// whatever the CURRENT embed size actually is — if that's back to the pre-freeze size (the keyboard
        /// came and went, window unchanged), FitChild/TerminalControl find nothing changed and send nothing:
        /// the session comes back exactly as it was, as a natural consequence, not a special case.</summary>
        private void SetFrozen(bool frozen)
        {
            if (_sizeFrozen == frozen) return;
            _sizeFrozen = frozen;
            foreach (var s in _sessions) if (s.IsSsh) s.Ssh.SetFrozen(frozen);   // FTP needs no per-session call — see below
            // FIN-KBD-FREEZE-SCROLL — every RDP session starts each freeze with NO established pan: becoming
            // clipped must never move the child on its own (RefreshRdpPanOverlay's own untouched-baseline snap —
            // see PanEstablished's doc comment — is what keeps that true; a fixed PanOffsetY reset here can't,
            // since its meaning depends on maxPan, unknown until the embed area has actually shrunk).
            foreach (var s in _sessions) { s.PanOffsetY = 0; s.PanEstablished = false; }
            if (!frozen)
            {
                // FRDP-POLISH-4 — belt-and-suspenders: OnResize's own catch-all already re-clamps a drifted
                // maximized window the instant it happens, but re-checking here too costs nothing (idempotent)
                // and covers the keyboard-HIDE/UNFREEZE moment explicitly, matching how this bug was reported.
                if (WindowState == FormWindowState.Maximized) ReassertMaximizedClamp();
                FitAllChildren();   // RDP: resync once (no-ops if the embed size is unchanged — B22 tolerance)
                ArmDynamicRefit();  // Dynamic sessions: one settle-gated renegotiation attempt if actually needed
                FileLog.Line("[KBD] UNFROZEN → resync embed=" + _embedHost.ClientSize);
            }
            else FileLog.Line("[KBD] FROZEN embed=" + _embedHost.ClientSize);
            RefreshRdpPanOverlay();
        }

        /// <summary>FIN-KBD-FREEZE-SCROLL — the active session's frozen content height vs. the current embed area,
        /// if it's an RDP session with a live child. FTP/SSH sessions (no <see cref="Session.Child"/>) never clip
        /// this way — SSH has its own pan built into <see cref="TerminalControl"/>; FTP just reflows normally.</summary>
        private bool RdpClipInfo(Session s, out int contentH, out int maxPan)
        {
            contentH = 0; maxPan = 0;
            if (s == null || s.Child == IntPtr.Zero) return false;
            RECT r;
            if (!GetWindowRect(s.Child, out r)) return false;
            contentH = r.Bottom - r.Top;
            maxPan = Math.Max(0, contentH - _embedHost.ClientSize.Height);
            return true;
        }

        /// <summary>Repositions (never resizes — SWP_NOSIZE) the frozen RDP child to match its session's current
        /// PanOffsetY. Moving a child via SetWindowPos generates WM_MOVE, not WM_SIZE — NudgeDynamicResize's own
        /// analysis of wf_send_resize's four call sites confirms a plain move can't trigger a renegotiation, so
        /// this is safe to do even while frozen.</summary>
        private void ApplyChildPan(Session s)
        {
            if (s == null || s.Child == IntPtr.Zero) return;
            int contentH, maxPan;
            if (!RdpClipInfo(s, out contentH, out maxPan)) return;
            int pan = Math.Max(0, Math.Min(maxPan, s.PanOffsetY));
            s.PanOffsetY = pan;
            int y = pan - maxPan;   // pan=0 → y=-maxPan (bottom-anchored); pan=maxPan → y=0 (top-anchored)
            try { SetWindowPos(s.Child, IntPtr.Zero, 0, y, 0, 0, SWP_NOZORDER | SWP_NOSIZE); } catch { }
        }

        private void OnRdpPanChanged(int newPan)
        {
            if (_active < 0 || _active >= _sessions.Count) return;
            _sessions[_active].PanOffsetY = newPan;
            ApplyChildPan(_sessions[_active]);
        }

        /// <summary>FIN-KBD-FREEZE-SCROLL — shows/hides/repositions the RDP pan scrollbar for whichever session is
        /// currently active, and (since the native RDP child paints above WinForms siblings by default — see
        /// FitAllChildren's z-order note) explicitly forces the overlay above it whenever it should be visible.
        /// Called on freeze/unfreeze, tab switch, and embed-area resize — every place the active session or its
        /// clipped-ness could have changed.</summary>
        private void RefreshRdpPanOverlay()
        {
            Session act = (_active >= 0 && _active < _sessions.Count) ? _sessions[_active] : null;
            int contentH = 0, maxPan = 0;
            bool show = _sizeFrozen && act != null && RdpClipInfo(act, out contentH, out maxPan) && maxPan > 0;
            if (!show)
            {
                if (_rdpPanOverlay.Visible) _rdpPanOverlay.Visible = false;
                return;
            }

            // First time THIS FREEZE SESSION that this session is actually clipped: snap to the untouched baseline
            // (pan=maxPan → ApplyChildPan's y=0 — exactly where FitChild already left it) so becoming clipped is
            // silent. Only a real drag (OnRdpPanChanged) moves it after that.
            if (!act.PanEstablished) { act.PanOffsetY = maxPan; act.PanEstablished = true; }

            var embedSize = _embedHost.ClientSize;
            _rdpPanOverlay.Bounds = new Rectangle(embedSize.Width - ClipScrollbar.Gutter, 0, ClipScrollbar.Gutter, embedSize.Height);
            _rdpPanOverlay.ContentHeight = contentH;
            _rdpPanOverlay.MaxPan = maxPan;
            _rdpPanOverlay.Pan = Math.Max(0, Math.Min(maxPan, act.PanOffsetY));
            act.PanOffsetY = _rdpPanOverlay.Pan;
            _rdpPanOverlay.Visible = true;
            ApplyChildPan(act);   // re-clamp+reposition the child too, in case maxPan just shrank
            try { BringWindowToTop(_rdpPanOverlay.Handle); } catch { }   // force above the native child in z-order
            _rdpPanOverlay.Invalidate();
        }

        /// <summary>The presentation's base padding (fullscreen 0 / windowed Frame) composed with the
        /// keyboard inset. The transforms call this instead of assigning Padding raw, so a fullscreen
        /// toggle mid-keyboard keeps the inset.</summary>
        private void ApplyFramePadding()
        {
            int f = _fullscreen ? 0 : Frame;
            Padding = new Padding(f, f, f, f + _kbInset);
        }

        private void OnKeyboardRect(System.Drawing.Rectangle kb)
        {
            try
            {
                if (IsDisposed) return;
                // FIN-KBD-FREEZE — set BEFORE the padding/layout change below, not after: ApplyFramePadding can
                // shrink _embedHost right here, which fires SizeChanged synchronously — if _sizeFrozen were still
                // false at that instant, FitAllChildren's guard wouldn't catch it and the child would get resized
                // to the padded area a moment before freezing "officially" took effect (a real, if narrow, race —
                // found via a RefreshRdpPanOverlay diagnostic during FIN-KBD-FREEZE-SCROLL verification). Freeze
                // the instant ANY keyboard is up, not just when OUR OWN computed inset is nonzero: Windows' own
                // native keyboard-avoidance can move/resize this window independent of the padding math below, and
                // the freeze must neutralize that too. Unfreeze the instant the keyboard is fully gone —
                // SetFrozen(false) does the one-time resync (itself now also guaranteed to run before any padding
                // change reverts, for the same reason).
                SetFrozen(!kb.IsEmpty);
                int inset = KeyboardInset.ComputeBottomInset(this, kb);
                if (inset != _kbInset)
                {
                    _kbInset = inset;
                    SuspendLayout();
                    ApplyFramePadding();     // _embedHost (Fill) shrinks/grows with it — sessions may clip while frozen
                    ResumeLayout(true);
                    FileLog.Line("[KBD] host inset=" + inset + " embed=" + _embedHost.ClientSize);
                }
                // Trap: a fullscreen host is TopMost — the touch keyboard could render BEHIND it,
                // making SSH-on-tablet unusable. Drop TopMost while the keyboard is up; restore on hide.
                if (inset > 0 && _fullscreen && TopMost)
                {
                    TopMost = false; _kbDroppedTopMost = true;
                    FileLog.Line("[KBD] fullscreen TopMost dropped (keyboard up)");
                }
                else if (inset == 0 && _kbDroppedTopMost)
                {
                    _kbDroppedTopMost = false;
                    if (_fullscreen) { TopMost = true; KeepBarOnTop(); }
                }
            }
            catch { /* best-effort — never break a live session over the keyboard */ }
        }

        /// <summary>Client size of the embed area for the FIRST windowed session: the profile's own resolution when
        /// it has one, else the curated windowed "Native" default. Clamped to the monitor work area.</summary>
        private static Size InitialEmbedSize(ConnectionProfile first, Rectangle work)
        {
            int maxW = Math.Max(320, work.Width - 2 * Frame);
            int maxH = Math.Max(240, work.Height - SessionTabBar.BarHeight - 2 * Frame);
            var nat = WindowedNative(maxW, maxH);
            var sess = RdpLauncher.ResolveEmitSize(first != null ? first.Settings : null, nat.Width, nat.Height);
            return new Size(Math.Min(sess.Width, maxW), Math.Min(sess.Height, maxH));
        }

        /// <summary>The one curated windowed default: a windowed session obviously cannot be the whole display, so
        /// ResolutionMode.Native in a window means 80% of the usable area. Width is rounded DOWN to a multiple of 4
        /// because wfreerdp rounds it UP (wf_client.c) — matching here keeps the child exactly the panel's width.</summary>
        private static Size WindowedNative(int maxW, int maxH)
        {
            int w = Round4(Math.Min(maxW, Math.Max(800, (int)(maxW * 0.80))));
            int h = Math.Min(maxH, Math.Max(600, (int)(maxH * 0.80)));
            return new Size(w, h);
        }

        private static int Round4(int v) => Math.Max(4, (v / 4) * 4);

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_fullscreen)
            {
                if (_manager != null) { try { _manager.Hide(); } catch { } }   // belt: ensure the manager is gone
                TopMost = true;
                ActivateOverlay();   // show+park the hover bar, start hover polling, ForceForeground (hide taskbar)
            }
            _childTimer.Start();
            FileLog.Line("[HOST] shown mode=" + Mode + " bounds=" + Bounds + " embed=" + _embedHost.ClientSize
                + " ws=" + WindowState + " managerVisible=" + (_manager != null ? _manager.Visible.ToString() : "null"));
        }

        /// <summary>FRDP-POLISH-3 — track the most-recently-focused host so ConnectOrAdd JOINs it (mstsc-style).</summary>
        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            _lastActiveHost = this;
            // FIN-SINGLETON trap 2 (one-shot): our TopMost was dropped so an activated manager could
            // genuinely surface over this fullscreen host — re-assert it now that the user returned here.
            // ONE-SHOT by flag so the fullscreen modal-confirm dance (which also drops TopMost) is untouched.
            if (_reassertTopMost)
            {
                _reassertTopMost = false;
                if (_fullscreen && !IsDisposed) { try { TopMost = true; KeepBarOnTop(); } catch { } }
            }
        }

        // FIN-SINGLETON — set when the manager activation dropped this fullscreen host's TopMost.
        private bool _reassertTopMost;

        /// <summary>FIN-SINGLETON trap 2: a fullscreen host is TopMost, so an activated manager would surface
        /// BEHIND it and the icon click would look dead. Drop TopMost on every fullscreen host (sessions,
        /// tabs, z-order among hosts untouched); each re-asserts one-shot on its next activation.</summary>
        public static void DropFullscreenTopMostForManager()
        {
            foreach (var h in _allHosts.ToArray())
                try { if (!h.IsDisposed && h._fullscreen && h.TopMost) { h.TopMost = false; h._reassertTopMost = true; } } catch { }
        }

        /// <summary>FIN-SINGLETON — activation fallback target if the manager is somehow gone: the most
        /// recently active live host, else any live host, else null.</summary>
        public static Form LastActiveOrAny()
        {
            if (_lastActiveHost != null && !_lastActiveHost.IsDisposed) return _lastActiveHost;
            for (int i = _allHosts.Count - 1; i >= 0; i--)
                if (!_allHosts[i].IsDisposed) return _allHosts[i];
            return null;
        }

        // ── sessions ────────────────────────────────────────────────────────────
        public void AddSession(ConnectionProfile cp)
        {
            if (cp.Type == ConnectionType.Ssh) { AddSshSession(cp); return; }   // FRDP-SSH-BUILD-2 — parallel content branch
            if (cp.Type == ConnectionType.Ftp) { AddFtpSession(cp); return; }   // FRDP-FTP-BUILD-2 — third content branch

            // "Native" means the monitor for a fullscreen host and the embed area for a windowed one.
            Size native = _fullscreen ? _mon.Size : _embedHost.ClientSize;
            if (native.Width <= 0 || native.Height <= 0) native = _mon.Size;

            var res = RdpLauncher.Launch(cp, native.Width, native.Height, true, _embedHost.Handle);
            if (!res.Ok)
            {
                MessageBox.Show(this, res.Error ?? "Failed to launch wfreerdp.", "Finestra", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            var sess = RdpLauncher.ResolveEmitSize(cp.Settings, native.Width, native.Height);
            FileLog.Line("[HOST] addsession mode=" + Mode + " pid=" + (res.Process != null ? res.Process.Id.ToString() : "?")
                + " session=" + sess.Width + "x" + sess.Height + " embed=" + _embedHost.ClientSize + " args=" + res.ArgsForDisplay);
            _sessions.Add(new Session { Profile = cp, Proc = res.Process, Emitted = sess, Owner = this });
            RefreshTabs();
            SetActive(_sessions.Count - 1);
        }

        /// <summary>FRDP-SSH-BUILD-2 — add an SSH tab: an app-owned <see cref="SshContent"/> (terminal + session)
        /// parented into the same embed panel, shown/hidden by the tab like an RDP child. No launch, no PID.</summary>
        private void AddSshSession(ConnectionProfile cp)
        {
            var content = new SshContent(cp);
            content.StatusChanged += OnSshStatus;
            content.ConnectFailed += OnSshConnectFailed;
            _sessions.Add(new Session { Profile = cp, Ssh = content, Owner = this });
            content.Start(_embedHost);   // parents the terminal into _embedHost + connects off the UI thread
            FileLog.Line("[HOST] add SSH session " + cp.Host + " mode=" + Mode + " tabs=" + _sessions.Count);
            RefreshTabs();
            SetActive(_sessions.Count - 1);
        }

        private void OnSshStatus(SshContent c)
        {
            if (IsDisposed) return;
            try { BeginInvoke((Action)(() => { if (!IsDisposed && _active >= 0 && _active < _sessions.Count && _sessions[_active].Ssh == c) { Ui.SetStats(c.StatusText); PushReconnect(_sessions[_active]); } })); } catch { }
        }

        private void OnSshConnectFailed(SshContent c)
        {
            if (IsDisposed) return;
            try { BeginInvoke((Action)(() => { int i = _sessions.FindIndex(s => s.Ssh == c); if (i >= 0) CloseSession(i); })); } catch { }
        }

        /// <summary>FRDP-FTP-BUILD-2 — add an FTP tab: an app-owned <see cref="FtpContent"/> (dual-pane browser)
        /// parented into the same embed panel, shown/hidden by the tab. No launch, no PID (like SSH).</summary>
        private void AddFtpSession(ConnectionProfile cp)
        {
            var content = new FtpContent(cp);
            content.StatusChanged += OnFtpStatus;
            content.ConnectFailed += OnFtpConnectFailed;
            _sessions.Add(new Session { Profile = cp, Ftp = content, Owner = this });
            content.Start(_embedHost);
            FileLog.Line("[HOST] add FTP session " + cp.Host + " mode=" + Mode + " tabs=" + _sessions.Count);
            RefreshTabs();
            SetActive(_sessions.Count - 1);
        }

        private void OnFtpStatus(FtpContent c)
        {
            if (IsDisposed) return;
            try { BeginInvoke((Action)(() => { if (!IsDisposed && _active >= 0 && _active < _sessions.Count && _sessions[_active].Ftp == c) { Ui.SetStats(c.StatusText); PushReconnect(_sessions[_active]); } })); } catch { }
        }

        /// <summary>FRDP-RECONNECT — Reconnect button clicked in the active tab's bar → rebuild that session's transport.</summary>
        private void OnReconnectClicked()
        {
            if (_active < 0 || _active >= _sessions.Count) return;
            var s = _sessions[_active];
            if (s.IsSsh) s.Ssh.Reconnect();
            else if (s.IsFtp) s.Ftp.Reconnect();
        }

        /// <summary>Show/hide the bar Reconnect button for a session (dropped SSH/FTP → visible; RDP → always hidden).</summary>
        private void PushReconnect(Session s)
        {
            bool show = false, busy = false;
            if (s.IsSsh) { show = s.Ssh.ShowReconnect; busy = s.Ssh.Reconnecting; }
            else if (s.IsFtp) { show = s.Ftp.ShowReconnect; busy = s.Ftp.Reconnecting; }
            Ui.SetReconnect(show, busy);
        }

        private void OnFtpConnectFailed(FtpContent c)
        {
            if (IsDisposed) return;
            try { BeginInvoke((Action)(() => { int i = _sessions.FindIndex(s => s.Ftp == c); if (i >= 0) CloseSession(i); })); } catch { }
        }

        // ── FRDP-POLISH-2: per-tab right-click quick menu ────────────────────────────────────────────────────────
        // SSH tabs get LIVE (session-only) colour + font toggles and a link to the full editor. RDP tabs get only a
        // minimal menu (tear-off / close) — no invented RDP prefs. Quick toggles do NOT rewrite the saved profile;
        // the editor + Settings are the persistent home (so a flick doesn't silently mutate a stored connection).
        private void OnTabRightClick(int tab, Point screen)
        {
            if (tab < 0 || tab >= _sessions.Count) return;
            var s = _sessions[tab];
            var menu = new ThemedContextMenuStrip { Font = FontHelper.Ui(9.75f) };

            if (s.IsSsh)
            {
                var colors = new ToolStripMenuItem("Terminal colours") { Checked = s.Ssh.ColorsOn };
                colors.Click += (a, b) => s.Ssh.SetColors(!s.Ssh.ColorsOn);
                menu.Items.Add(colors);
                menu.Items.Add(MenuItem("Font larger", () => s.Ssh.AdjustFontSize(+1)));
                menu.Items.Add(MenuItem("Font smaller", () => s.Ssh.AdjustFontSize(-1)));
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(MenuItem("Edit connection…", () => EditConnection(s.Profile)));
                menu.Items.Add(new ToolStripSeparator());
            }
            menu.Items.Add(MenuItem("Tear off", () => OnTabTear(tab)));
            menu.Items.Add(MenuItem("Close tab", () => CloseSession(tab)));

            try { menu.Show(screen); } catch { }
        }

        private static ToolStripMenuItem MenuItem(string text, Action onClick)
        {
            var it = new ToolStripMenuItem(text);
            it.Click += (a, b) => { try { onClick(); } catch { } };
            return it;
        }

        /// <summary>Open the full editor for a connection (from the tab menu). Writes the SAVED profile (for the next
        /// connect); the running session is unaffected — quick prefs stay live-only.</summary>
        private void EditConnection(ConnectionProfile cp)
        {
            try
            {
                bool wasTop = TopMost; TopMost = false;
                using (var f = new ConnectionEditorForm(cp))
                    if (f.ShowDialog(this) == DialogResult.OK && f.Result != null)
                        ConnectionStore.Instance.AddOrUpdate(f.Result);
                if (wasTop) { TopMost = true; KeepBarOnTop(); }
            }
            catch (Exception ex) { FileLog.Line("[HOST] edit connection failed: " + ex.Message); }
        }

        private void ChildTick(object sender, EventArgs e)
        {
            // FRDP-FIXSWEEP B16 — guard each session's tick so one throw (a native call, a torn session) can't pop the
            // error dialog every 250 ms. Logged quietly — no dialog, no secret.
            foreach (var s in _sessions)
                try { ChildTickOne(s); }
                catch (Exception ex) { FileLog.Line("[HOST] child-tick error (" + (s != null ? s.Name : "?") + "): " + ex.Message); }
        }

        private void ChildTickOne(Session s)
        {
            if (s.IsSsh) { s.Ssh.Tick(); return; }   // SSH: app-measured ping housekeeping; no HWND watchdog
            if (s.IsFtp) return;                      // FTP: status is event-driven (path / transfer), no polling

            if (s.Child == IntPtr.Zero && s.Proc != null && !s.Proc.HasExited)
            {
                IntPtr child = FindChildByPid(_embedHost.Handle, s.Proc.Id);
                if (child != IntPtr.Zero)
                {
                    s.Child = child;
                    s.EverEmbedded = true;                    // phase inference: embedded ⇒ not a "failed" connect
                    FileLog.Line("[HOST] child EMBEDDED hwnd=0x" + child.ToInt64().ToString("X") + " pid=" + s.Proc.Id + " mode=" + Mode);
                    StartStats(s);
                    FitChild(child);
                    ShowWindow(child, IndexOf(s) == _active ? SW_SHOW : SW_HIDE);
                    if (IndexOf(s) == _active) FocusChild(child);
                    KeepBarOnTop();
                    if (IndexOf(s) == _active) RefreshRdpPanOverlay();   // FIN-KBD-FREEZE-SCROLL — connecting while already frozen is a real (if narrow) case
                }
            }

            if (s.Child == IntPtr.Zero)
            {
                // not embedded yet → still "connecting…", UNLESS the process died first or the no-embed backstop fired.
                if (s.Phase == RdpPhase.Connecting)
                {
                    if (s.Proc == null || s.Proc.HasExited) SetRdpPhase(s, RdpPhase.Failed);
                    else if (++s.ConnectTicks > NoEmbedTimeoutTicks)
                    {
                        try { s.Proc.Kill(); } catch { }   // FRDP-FIXSWEEP B7 — hung-but-alive engine: kill it, don't leave it lingering behind a "failed" tab
                        SetRdpPhase(s, RdpPhase.Failed);
                    }
                }
                return;
            }
            if (s.Proc != null && s.Proc.HasExited) { SetRdpPhase(s, RdpPhase.Disconnected); return; }   // dropped after embed

            // FRDP-RDP-LIVENESS Part 1 — Proc.HasExited only catches a fully-exited process. A HUNG engine (the
            // process is still alive, but its channel died — confirmed the mechanism for this by Part 0's harness
            // ruling out a stale/disposed-sibling pipe reference) needs a second signal: the engine streams a
            // STATS line ~1/sec while healthy, so sustained silence means the channel is dead even though the OS
            // process isn't. Deliberately runs BEFORE the minimized/frozen early-returns below — those guard the
            // resize watchdog, not liveness, and a hung session must be caught even while backgrounded or frozen.
            // Debounced + conservative: the ONLY trigger is sustained heartbeat silence; a Send() failure is logged
            // as corroborating context but never independently trips this (see StatsPipe.LastSendFailed).
            if (s.Stats != null)
            {
                double idle = s.SecondsSinceLastStats();
                if (idle >= HeartbeatDeadSeconds)
                {
                    FileLog.Line("[HOST] rdp heartbeat DEAD pid=" + s.Stats.Pid + " (no STATS for " + idle.ToString("0.0") + "s"
                        + (s.Stats.LastSendFailed ? ", + a failed write" : "") + ") — " + s.Name);
                    try { s.Stats?.Dispose(); } catch { }
                    try { if (s.Proc != null && !s.Proc.HasExited) s.Proc.Kill(); } catch { }   // FRDP-FIXSWEEP B7 — reap the hung engine, don't leave a zombie
                    SetRdpPhase(s, RdpPhase.Disconnected);
                    return;
                }
            }

            if (WindowState == FormWindowState.Minimized) return;
            if (_sizeFrozen) return;   // FIN-KBD-FREEZE — the watchdog re-fit/renegotiate is exactly what freezing must suppress

            // WATCHDOG. wfreerdp sizes its OWN window in wf_resize_window() right after creating it (wf_client.c:593),
            // which can land AFTER our first FitChild and shrink the child back to the session size (a 1280x720 session
            // in a 1920x1080 host = desktop in the top-left of a black panel, indistinguishable from letterbox). Keep
            // re-fitting until the child really matches, then count how long it has held.
            if (!ChildFitsEmbed(s.Child)) { FitChild(s.Child); s.FitStableTicks = 0; }
            else s.FitStableTicks++;

            // A Dynamic session whose negotiated size differs from the area it landed in renegotiates ONCE — the only
            // way Dynamic can act in the FULLSCREEN host, which never resizes. Armed only after the child is correctly
            // sized and settled, so wf_send_resize reads the right client_width and the disp channel has opened (a send
            // before that fails, and the engine records its dedupe key even on failure — wf_event.c:364 — wedging it).
            if (!s.DynFitArmed && IsDynamic(s) && s.Emitted != _embedHost.ClientSize
                && s.FitStableTicks >= DynFitSettleTicks)
            {
                s.DynFitArmed = true;
                s.DynFitTicks = DynFitRetries;
                FileLog.Line("[HOST] dynamic fit: session " + s.Emitted.Width + "x" + s.Emitted.Height
                    + " != embed " + _embedHost.ClientSize.Width + "x" + _embedHost.ClientSize.Height + " → renegotiating");
            }

            if (s.DynFitTicks > 0)
            {
                s.DynFitTicks--;
                try { PostMessage(s.Child, WM_EXITSIZEMOVE, IntPtr.Zero, IntPtr.Zero); } catch { }
            }
        }

        /// <summary>Is the embedded child actually the size of the embed panel?</summary>
        private bool ChildFitsEmbed(IntPtr child)
        {
            RECT r;
            if (!GetWindowRect(child, out r)) return true;   // can't tell → don't thrash
            var sz = _embedHost.ClientSize;
            // FRDP-FIXSWEEP B22 — wfreerdp rounds the width UP to a multiple of 4 (wf_client.c), so an exact == never
            // converges when the embed width is odd → perpetual re-fit + Dynamic never renegotiates. A few px of
            // tolerance absorbs the round-up (a genuinely wrong size differs by far more) so FitStableTicks can grow.
            return Math.Abs((r.Right - r.Left) - sz.Width) <= 4 && Math.Abs((r.Bottom - r.Top) - sz.Height) <= 4;
        }

        private static bool IsDynamic(Session s)
        {
            var st = s.Profile != null ? s.Profile.Settings : null;
            return st != null && st.OversizeMode == OversizeMode.Dynamic;
        }

        private int IndexOf(Session s) => _sessions.IndexOf(s);

        // ── live stats (Form B: wfreerdp autodetect RTT/bandwidth streamed over the per-PID named pipe) ──
        private void StartStats(Session s)
        {
            try
            {
                s.Stats = new StatsPipe(s.Proc.Id);
                s.TouchStats();   // FRDP-RDP-LIVENESS Part 1 — baseline NOW, so total silence from the start counts too
                // Route to whichever host owns the session NOW (it may have been torn into another window). The
                // StatsPipe can't be recreated — wfreerdp's pipe is one client, one shot — so this single
                // subscription must follow the session, via s.Owner, for its whole life.
                s.Stats.Updated += (rtt, bw, jit) =>
                {
                    var o = s.Owner;
                    if (o != null && !o.IsDisposed) o.OnStats(s, rtt, bw, jit);
                };
            }
            catch (Exception ex) { FileLog.Line("[STATS] start failed: " + ex.Message); }
        }

        private void OnStats(Session s, int rtt, int bw, int jit)
        {
            s.TouchStats();   // FRDP-RDP-LIVENESS Part 1 — runs on the StatsPipe reader thread, same as every other write here
            string bwStr = bw >= 1000 ? (bw / 1000.0).ToString("0.0") + " Mbps" : bw + " kbps";
            s.LastStats = "rtt " + rtt + " ms · " + bwStr + " · jit " + jit;
            if (IsDisposed) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (IsDisposed) return;   // FRDP-FIXSWEEP B11 — guard the queued delegate too (parity with OnSshStatus/OnFtpStatus)
                    if (s.Phase == RdpPhase.Connecting) s.Phase = RdpPhase.Connected;   // first stats line = connected; stats ARE the indicator
                    if (IndexOf(s) == _active) Ui.SetStats(RdpBarText(s));
                }));
            }
            catch { }
        }

        /// <summary>The bar text for an RDP tab: a phase dot while connecting/failed/disconnected, and ONLY the stats
        /// readout once connected (no redundant "● connected" beside live numbers). FRDP-POLISH-2.</summary>
        private static string RdpBarText(Session s)
        {
            switch (s.Phase)
            {
                case RdpPhase.Failed: return "● failed";
                case RdpPhase.Disconnected: return "● disconnected";
                case RdpPhase.Connected: return s.LastStats;
                default: return "● connecting…";
            }
        }

        private void SetRdpPhase(Session s, RdpPhase phase)
        {
            if (s.Phase == phase) return;
            s.Phase = phase;
            FileLog.Line("[HOST] rdp " + phase.ToString().ToLowerInvariant() + " — " + s.Name);
            if (IndexOf(s) == _active) { try { Ui.SetStats(RdpBarText(s)); } catch { } }
        }

        private void OnPauseToggled()
        {
            if (_active < 0 || _active >= _sessions.Count) return;
            var s = _sessions[_active];
            if (s.Stats == null) return;
            s.Paused = !s.Paused;
            if (s.Paused) s.Stats.Pause(); else s.Stats.Resume();
            Ui.SetPaused(s.Paused);
            // FRDP-RDP-LIVENESS Part 0 — the session NAME alone is ambiguous (two tabs to the same profile are
            // both "us2"); the pid is what actually identifies which live session this Pause/Resume acted on.
            FileLog.Line("[STATS pid=" + s.Stats.Pid + "] " + (s.Paused ? "PAUSE (suppress output)" : "RESUME (allow + refresh)") + " — " + s.Name);
        }

        private void SetActive(int i)
        {
            if (i < 0 || i >= _sessions.Count) return;
            _active = i;
            for (int k = 0; k < _sessions.Count; k++)
            {
                var s = _sessions[k];
                if (s.IsSsh) s.Ssh.SetVisible(k == i);
                else if (s.IsFtp) s.Ftp.SetVisible(k == i);
                else if (s.Child != IntPtr.Zero) ShowWindow(s.Child, k == i ? SW_SHOW : SW_HIDE);
            }
            var act = _sessions[i];
            if (act.IsSsh) act.Ssh.Focus();
            else if (act.IsFtp) act.Ftp.Focus();
            // FIN-KBD-FREEZE — a tab switch used to call FitChild unconditionally, which forces the child back to
            // _embedHost.ClientSize: switching TO an RDP tab while frozen+clipped would silently un-freeze that
            // tab's size the moment it became active. RefreshRdpPanOverlay (below) re-applies this tab's own pan
            // instead, which repositions (never resizes) the child — the freeze contract holds across tab switches.
            else if (act.Child != IntPtr.Zero) { if (!_sizeFrozen) FitChild(act.Child); FocusChild(act.Child); }
            Ui.SetActive(i);
            Ui.SetStats(act.IsSsh ? act.Ssh.StatusText : act.IsFtp ? act.Ftp.StatusText : RdpBarText(act));   // RDP: connecting… / stats / failed
            Ui.SetPaused(act.Paused);
            Ui.SetPauseVisible(!act.IsSsh && !act.IsFtp);   // only RDP has pause/resume
            PushReconnect(act);   // FRDP-RECONNECT — show/hide the Reconnect button for the newly-active tab
            KeepBarOnTop();
            RefreshRdpPanOverlay();
            FileLog.Line("[HOST] active tab=" + i + " (" + act.Name + ") of " + _sessions.Count + " mode=" + Mode);
        }

        private void RefreshTabs()
        {
            var names = new List<string>();
            var kinds = new List<SessionKind>();
            foreach (var s in _sessions) { names.Add(s.Name); kinds.Add(s.Kind); }
            Ui.SetTabs(names, kinds, _active);
        }

        private void CloseSession(int i)
        {
            if (i < 0 || i >= _sessions.Count) return;
            var s = _sessions[i];
            if (s.IsSsh) { try { s.Ssh.Dispose(); } catch { } }
            else if (s.IsFtp) { try { s.Ftp.Dispose(); } catch { } }
            else
            {
                // FRDP-RDP-LIVENESS Part 0 — when a same-named sibling tab's stats pipe gets torn down matters:
                // correlate this timestamp against any later "[STATS pid=…] send failed" to tell a live session's
                // own dead channel apart from a write that (were it ever possible) reached a disposed sibling's.
                if (s.Stats != null) FileLog.Line("[STATS pid=" + s.Stats.Pid + "] disposed (tab closed) — " + s.Name);
                try { s.Stats?.Dispose(); } catch { }
                try { if (s.Proc != null && !s.Proc.HasExited) s.Proc.Kill(); } catch { }
                try { s.Proc?.Dispose(); } catch { }   // FRDP-FIXSWEEP B17 — release the Process handle/plumbing
            }
            _sessions.RemoveAt(i);
            if (_sessions.Count == 0) { Close(); return; }
            if (i < _active) _active--;              // FRDP-FIXSWEEP B3 — a tab LEFT of the active one shifts its index down
            if (_active >= _sessions.Count) _active = _sessions.Count - 1;
            RefreshTabs();
            SetActive(_active);
        }

        // ── tear-off: move a LIVE embedded session into its own window (FRDP-TEAROFF) ──────────────────────────
        //
        // THE PRIMITIVE this feature rests on: a live wfreerdp child is a WS_CHILD render surface parented to a
        // host's embed panel. The RDP/TCP session, the autodetect stats pipe (a named pipe keyed to the wfreerdp
        // PID), and the decode/render loop ALL live in the wfreerdp *process* — none of them know or care which
        // window is their parent. So SetParent(child, otherPanel) moves the picture to another window WITHOUT a
        // reconnect: no re-auth, no new PID, the pipe never drops. The session object (Proc/Child/Stats/Paused)
        // is handed over as-is; only its Owner (stats routing) and its fit bookkeeping are re-pointed.

        private void OnTabTear(int i)
        {
            if (i < 0 || i >= _sessions.Count) return;
            if (_sessions.Count == 1) { FileLog.Line("[TEAR] ignored — already the only tab in its window"); return; }
            TearToNewWindow(i, new Point(Location.X + 48, Location.Y + 48));   // cascade off this window
        }

        // ── drag tear-off (left-press-drag a tab out) ──
        private void OnTabDragMove(int tab, Point screen)
        {
            if (tab < 0 || tab >= _sessions.Count) return;
            var target = FindWindowedHostBarAt(screen);
            bool merge = target != null && target != this;
            if (_ghost == null || _ghost.IsDisposed) _ghost = new DragGhost();
            _ghost.Track(_sessions[tab].Name, screen, merge);
        }

        private void OnTabDragDrop(int tab, Point screen)
        {
            try { _ghost?.Stop(); } catch { }
            if (tab < 0 || tab >= _sessions.Count) return;
            var target = FindWindowedHostBarAt(screen);
            if (target == this) { SetActive(tab); return; }                 // dropped back on our own bar → just select
            if (target != null && !target.IsDisposed)                        // dropped on another host's bar → MERGE
            {
                FileLog.Line("[TEAR] drag-merge '" + _sessions[tab].Name + "' → host '" + target.Text + "'");
                TransplantSession(_sessions[tab], target);
                return;
            }
            if (_sessions.Count == 1) { SetActive(tab); return; }            // only tab, nowhere to go → already its own window
            FileLog.Line("[TEAR] drag-out '" + _sessions[tab].Name + "' → new window at " + screen);
            TearToNewWindow(tab, new Point(screen.X - 60, screen.Y - 10));   // out → new window near the drop
        }

        /// <summary>The windowed host whose tab bar is under <paramref name="screen"/> (may be this host), or null.</summary>
        private static SessionHost FindWindowedHostBarAt(Point screen)
        {
            foreach (var h in _allHosts)
            {
                if (h == null || h.IsDisposed || h._fullscreen) continue;
                if (!h.Visible || h.WindowState == FormWindowState.Minimized) continue;
                var bar = h._tabBar;
                if (bar == null || !bar.IsHandleCreated) continue;
                try
                {
                    var tl = bar.PointToScreen(Point.Empty);
                    if (new Rectangle(tl, bar.Size).Contains(screen)) return h;
                }
                catch { }
            }
            return null;
        }

        /// <summary>Spawn a fresh windowed host at <paramref name="screenPt"/> and transplant session
        /// <paramref name="i"/> into it. Source keeps its remaining tabs, or closes if it is emptied.</summary>
        public SessionHost TearToNewWindow(int i, Point screenPt)
        {
            if (i < 0 || i >= _sessions.Count) return null;
            var s = _sessions[i];
            var dest = new SessionHost(_manager, false, s.Profile);   // empty windowed host, sized for this profile
            dest.Show();                                              // realizes dest._embedHost as the new parent
            dest.Location = ClampToWorkArea(screenPt, dest.Size);
            TransplantSession(s, dest);
            dest.Activate();
            return dest;
        }

        /// <summary>Move a live session from this host into <paramref name="dest"/> by re-parenting its child.</summary>
        private void TransplantSession(Session s, SessionHost dest)
        {
            int idx = _sessions.IndexOf(s);
            if (idx < 0 || dest == null || dest.IsDisposed) return;
            string pid = s.Proc != null ? s.Proc.Id.ToString() : "?";

            _sessions.RemoveAt(idx);   // detach from this host's model first (so our teardown never kills it)

            if (s.IsSsh)
            {
                s.Ssh.StatusChanged -= OnSshStatus; s.Ssh.ConnectFailed -= OnSshConnectFailed;      // stop routing to this host
                s.Ssh.Reparent(dest._embedHost);                                                     // managed control move — session untouched
                s.Ssh.StatusChanged += dest.OnSshStatus; s.Ssh.ConnectFailed += dest.OnSshConnectFailed;
                FileLog.Line("[TEAR] transplant SSH '" + s.Name + "' → host '" + dest.Text + "' (stays connected)");
            }
            else if (s.IsFtp)
            {
                s.Ftp.StatusChanged -= OnFtpStatus; s.Ftp.ConnectFailed -= OnFtpConnectFailed;
                s.Ftp.Reparent(dest._embedHost);                                                     // managed control move — session untouched
                s.Ftp.StatusChanged += dest.OnFtpStatus; s.Ftp.ConnectFailed += dest.OnFtpConnectFailed;
                FileLog.Line("[TEAR] transplant FTP '" + s.Name + "' → host '" + dest.Text + "' (stays connected)");
            }
            else if (s.Child != IntPtr.Zero)
            {
                IntPtr prev = SetParent(s.Child, dest._embedHost.Handle);   // ← the live re-parent
                FileLog.Line("[TEAR] transplant '" + s.Name + "' pid=" + pid + " child=0x" + s.Child.ToInt64().ToString("X")
                    + " oldParent=0x" + prev.ToInt64().ToString("X") + " newParent=0x" + dest._embedHost.Handle.ToInt64().ToString("X"));
            }

            dest.AdoptSession(s);

            if (_sessions.Count == 0) { FileLog.Line("[TEAR] source host emptied → closing"); Close(); }
            else
            {
                if (idx < _active) _active--;            // FRDP-FIXSWEEP B3 — tearing a tab LEFT of the active one shifts its index down
                if (_active >= _sessions.Count) _active = _sessions.Count - 1;
                RefreshTabs();
                SetActive(_active);
            }
        }

        /// <summary>Take ownership of a session whose child is ALREADY re-parented into our embed panel.</summary>
        private void AdoptSession(Session s)
        {
            s.Owner = this;
            s.FitStableTicks = 0; s.DynFitArmed = false; s.DynFitTicks = 0;   // re-evaluate fit/Dynamic in this host
            _sessions.Add(s);
            if (s.Child != IntPtr.Zero) { FitChild(s.Child); ShowWindow(s.Child, SW_SHOW); }
            RefreshTabs();
            SetActive(_sessions.Count - 1);
            if (s.Child != IntPtr.Zero) FocusChild(s.Child);
            FileLog.Line("[TEAR] adopted '" + s.Name + "' into " + Mode + " host; tabs now=" + _sessions.Count);
        }

        private static Point ClampToWorkArea(Point pt, Size sz)
        {
            try
            {
                var wa = Screen.FromPoint(pt).WorkingArea;
                int x = Math.Max(wa.Left, Math.Min(pt.X, wa.Right - sz.Width));
                int y = Math.Max(wa.Top, Math.Min(pt.Y, wa.Bottom - sz.Height));
                return new Point(x, y);
            }
            catch { return pt; }
        }

        // ── hover reveal, fullscreen only (cursor poll — reliable regardless of who has mouse capture) ──
        private void HoverTick(object sender, EventArgs e)
        {
            // Guard on window state: without this the bar re-reveals itself over the LOCAL desktop while the host
            // is minimized (a topmost ghost bar that also eats clicks) — the FRDP-UI-WINDOWED minimize bug.
            if (WindowState != FormWindowState.Normal || !Visible) return;
            if (_flashing) { KeepBarOnTop(); return; }   // FRDP-POLISH-3 — holding a just-added-tab reveal; _flashTimer hides it
            Point p = Cursor.Position;
            if (!_mon.Contains(p)) return;
            if (!_barShown && p.Y <= _mon.Top + 2)
            {
                _bar.RevealTo(_mon.Left, _mon.Top, _mon.Width);
                KeepBarOnTop();
                _barShown = true;
            }
            else if (_barShown && p.Y > _mon.Top + OverlayBar.BarHeight + 10)
            {
                _bar.HideAbove(_mon.Left, _mon.Top, _mon.Width);
                _barShown = false;
            }
        }

        /// <summary>Stop the hover machinery and stow the overlay — before minimizing, so Windows does not bring
        /// the bar back with the owner and so no ghost bar can appear over the local desktop.</summary>
        private void ParkBar()
        {
            if (!_fullscreen) return;
            _hoverTimer.Stop();
            _barShown = false;
            try { _bar.HideAbove(_mon.Left, _mon.Top, _mon.Width); _bar.Hide(); } catch { }
        }

        private void RestoreFullscreen()
        {
            if (!_fullscreen) return;
            TopMost = true;
            Bounds = _mon;
            try { _bar.Show(); _bar.HideAbove(_mon.Left, _mon.Top, _mon.Width); } catch { }
            _barShown = false;
            _hoverTimer.Start();
            ForceForeground();
            if (_active >= 0 && _active < _sessions.Count && _sessions[_active].Child != IntPtr.Zero)
                FocusChild(_sessions[_active].Child);
            FileLog.Line("[HOST] restored from taskbar (fullscreen), sessions=" + _sessions.Count);
        }

        // ── live windowed⇄fullscreen (FRDP-FS-TOGGLE) ───────────────────────────────────────────────────────────
        //
        // TRANSFORM-IN-PLACE: the SAME host restyles between presentations; the wfreerdp child is NEVER re-parented
        // (unlike tear-off). The session/pipe/render are untouched — only this window's chrome, bounds, TopMost and
        // which bar is shown change. Fullscreen targets the monitor the WINDOW IS ON (Screen.FromControl), so a
        // window dragged to monitor 2 fullscreens monitor 2.

        /// <summary>Show + park the hover overlay and become the foreground fullscreen window (taskbar hides).</summary>
        private void ActivateOverlay()
        {
            _barShown = false;
            _bar.Show();
            _bar.HideAbove(_mon.Left, _mon.Top, _mon.Width);
            _hoverTimer.Start();
            ForceForeground();
        }

        /// <summary>Stop hover polling and stow the overlay off-screen.</summary>
        private void DeactivateOverlay()
        {
            _hoverTimer.Stop();
            _barShown = false;
            try { _bar.HideAbove(_mon.Left, _mon.Top, _mon.Width); _bar.Hide(); } catch { }
        }

        /// <summary>FRDP-POLISH-3 — a session was just added to THIS host (already the active tab). Bring the host
        /// forward; if fullscreen, briefly reveal the hover overlay so the user SEES the new connection open (a
        /// fullscreen add is otherwise silent — the tab bar is hidden).</summary>
        private void RevealForAddedSession()
        {
            Resume();                           // un-minimize + foreground (fullscreen: TopMost, hide manager, bar on top)
            if (_fullscreen) FlashOverlay();
            else { try { Activate(); } catch { } }
        }

        /// <summary>Reveal the fullscreen overlay bar and hold it visible for a beat, then let hover take over.</summary>
        private void FlashOverlay()
        {
            if (!_fullscreen) return;
            _hoverTimer.Start();                // OnAdd's ParkBar may have stopped hover polling — ensure it's live
            try { _bar.Show(); _bar.RevealTo(_mon.Left, _mon.Top, _mon.Width); } catch { }
            KeepBarOnTop();
            _barShown = true;
            _flashing = true;
            if (_flashTimer == null)
            {
                _flashTimer = new Timer { Interval = 1800 };
                _flashTimer.Tick += (s, e) =>
                {
                    _flashTimer.Stop();
                    _flashing = false;
                    try   // hide now, unless the cursor is holding the bar at the very top edge — then let hover own it
                    {
                        Point p = Cursor.Position;
                        if (!(_mon.Contains(p) && p.Y <= _mon.Top + 2)) { _bar.HideAbove(_mon.Left, _mon.Top, _mon.Width); _barShown = false; }
                    }
                    catch { _barShown = false; }
                };
            }
            _flashTimer.Stop(); _flashTimer.Start();
            FileLog.Line("[HOST] flash overlay (tab added in fullscreen), tabs=" + _sessions.Count);
        }

        private void OnFullscreenToggle()
        {
            if (_switching) return;
            if (_fullscreen) ExitFullscreenLive(); else EnterFullscreenLive();
        }

        private void EnterFullscreenLive()
        {
            // FIN-KBD-FREEZE — a deliberate presentation change always wins over an in-flight keyboard freeze:
            // cancel it FIRST so this method's own FitAllChildren()/ArmDynamicRefit() below actually run.
            if (_sizeFrozen) SetFrozen(false);
            _switching = true;
            try
            {
                _savedWindowedState = WindowState;
                _savedWindowedBounds = (WindowState == FormWindowState.Normal) ? Bounds : RestoreBounds;
                if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;

                _mon = Screen.FromControl(this).Bounds;   // fullscreen the monitor THIS window is on
                _fullscreen = true;

                SuspendLayout();
                _tabBar.Visible = false;                   // Dock=Top invisible → _embedHost (Fill) fills the monitor
                ApplyFramePadding();                       // 0 frame + any live keyboard inset (FIN-KEYBOARD)
                BackColor = Color.Black;
                ResumeLayout(true);

                Bounds = _mon;
                TopMost = true;
                ActivateOverlay();

                RefreshTabs(); Ui.SetActive(_active); PushActiveStatsToBar(); Ui.SetFullscreen(true);
                FitAllChildren(); ArmDynamicRefit();
                FocusActiveChild();
                FileLog.Line("[FS] → FULLSCREEN on " + _mon + " embed=" + _embedHost.ClientSize + " sessions=" + _sessions.Count);
            }
            finally { _switching = false; }
        }

        private void ExitFullscreenLive()
        {
            // FIN-KBD-FREEZE — same reasoning as EnterFullscreenLive: a deliberate action cancels the freeze.
            if (_sizeFrozen) SetFrozen(false);
            _switching = true;
            try
            {
                _fullscreen = false;
                DeactivateOverlay();
                TopMost = false;

                SuspendLayout();
                BackColor = ThemeHelper.GetWindowsAccentColor();
                ApplyFramePadding();                       // Frame + any live keyboard inset (FIN-KEYBOARD)
                _tabBar.Visible = true;
                ResumeLayout(true);

                if (_savedWindowedBounds.Width > 0 && _savedWindowedBounds.Height > 0)
                    Bounds = _savedWindowedBounds;
                if (_savedWindowedState == FormWindowState.Maximized) WindowState = FormWindowState.Maximized;

                RefreshTabs(); Ui.SetActive(_active); PushActiveStatsToBar(); Ui.SetFullscreen(false);
                Ui.SetMaximized(WindowState == FormWindowState.Maximized);
                FitAllChildren(); ArmDynamicRefit();
                FocusActiveChild();
                Activate(); try { SetForegroundWindow(Handle); } catch { }
                FileLog.Line("[FS] → WINDOWED bounds=" + Bounds + " embed=" + _embedHost.ClientSize + " sessions=" + _sessions.Count);
            }
            finally { _switching = false; }
        }

        private void PushActiveStatsToBar()
        {
            if (_active < 0 || _active >= _sessions.Count) return;
            var a = _sessions[_active];
            Ui.SetStats(a.IsSsh ? a.Ssh.StatusText : a.IsFtp ? a.Ftp.StatusText : RdpBarText(a));
            Ui.SetPaused(a.Paused);
            Ui.SetPauseVisible(!a.IsSsh && !a.IsFtp);
        }

        private void FocusActiveChild()
        {
            if (_active < 0 || _active >= _sessions.Count) return;
            var a = _sessions[_active];
            if (a.IsSsh) a.Ssh.Focus();
            else if (a.IsFtp) a.Ftp.Focus();
            else if (a.Child != IntPtr.Zero) FocusChild(a.Child);
        }

        /// <summary>After a presentation flip the embed area changed size; a Dynamic session must renegotiate to it.
        /// The child is already fitted (FitAllChildren updates client_width), the disp channel is long open, so a
        /// short burst of WM_EXITSIZEMOVE posts (drained by ChildTick) makes the engine reflow — the same
        /// mechanism as FRDP-FILL, and the engine dedupes to the new size. Letterbox/Smart need no renegotiation.</summary>
        private void ArmDynamicRefit()
        {
            foreach (var s in _sessions)
                if (s.Child != IntPtr.Zero && IsDynamic(s)) s.DynFitTicks = DynFitRetries;
        }

        // ── window buttons ──────────────────────────────────────────────────────
        /// <summary>Minimize the SESSION HOST to the taskbar (never the hidden manager). Sessions stay alive and
        /// the taskbar button brings them back.</summary>
        private void OnMinimize()
        {
            if (_fullscreen) { ParkBar(); TopMost = false; }
            WindowState = FormWindowState.Minimized;
            FileLog.Line("[HOST] MINIMIZE mode=" + Mode + " → host iconified, sessions alive=" + _sessions.Count);
        }

        /// <summary>Fullscreen: leave the session and come back to the connection manager. Windowed: the normal
        /// maximize / restore-down toggle.</summary>
        private void OnRestoreOrMaximize()
        {
            if (_fullscreen)
            {
                ParkBar();
                TopMost = false;
                WindowState = FormWindowState.Minimized;   // drop out of fullscreen; sessions stay alive
                ShowManager();
                FileLog.Line("[HOST] RESTORE (fullscreen) → manager shown, sessions alive=" + _sessions.Count);
            }
            else ToggleMaximize();
        }

        private void ToggleMaximize()
        {
            // FIN-KBD-FREEZE — same reasoning: a deliberate maximize/restore click cancels the freeze first.
            if (_sizeFrozen) SetFrozen(false);
            WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
            Ui.SetMaximized(WindowState == FormWindowState.Maximized);
            NudgeDynamicResize();   // WindowState changes never raise OnResizeEnd
            FileLog.Line("[HOST] " + (WindowState == FormWindowState.Maximized ? "MAXIMIZE" : "RESTORE-DOWN") + " embed=" + _embedHost.ClientSize);
        }

        /// <summary>The bar's ✕ is a WINDOW control: it closes the host (and every session in it). An individual
        /// session is closed by its own tab "×".</summary>
        private void OnCloseHost()
        {
            if (_sessions.Count > 1)
            {
                bool wasTop = TopMost;
                TopMost = false;   // a fullscreen host is TopMost; drop it so the modal confirm isn't hidden behind
                bool ok = ConfirmDialog.Ask(this, "Close all " + _sessions.Count + " sessions in this window?", "Finestra");
                if (!ok) { TopMost = wasTop; return; }
            }
            Close();
        }

        private void OnAdd()
        {
            if (_fullscreen) { ParkBar(); TopMost = false; }
            ShowManager();   // pick another connection → routed back here (or to the other host) by ConnectOrAdd
        }

        /// <summary>Bar background = the windowed host's title region: press-drag moves it, double-press maximizes.</summary>
        private void OnBarBackgroundMouseDown(MouseEventArgs e)
        {
            if (_fullscreen || e.Button != MouseButtons.Left) return;
            if (e.Clicks >= 2) { ToggleMaximize(); return; }
            if (WindowState == FormWindowState.Maximized) return;
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);   // native move loop
        }

        public void Resume()
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;   // → OnResize restores
            if (_fullscreen)
            {
                TopMost = true;
                if (_manager != null) { try { _manager.Hide(); } catch { } }
                KeepBarOnTop();
            }
            Activate();
            try { SetForegroundWindow(Handle); } catch { }
        }

        private void ShowManager()
        {
            if (_manager == null) return;
            try { _manager.Show(); _manager.WindowState = FormWindowState.Normal; _manager.Activate(); } catch { }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // WinForms raises this from setters used DURING construction (Padding/MinimumSize/ClientSize), long
            // before the bars are wired — bail out until they exist. Also skip while a fullscreen flip is mid-flight.
            if (_tabBar == null || _embedHost == null || _switching) return;
            bool minimized = WindowState == FormWindowState.Minimized;
            if (_fullscreen)
            {
                if (minimized) ParkBar();
                else if (_wasMinimized) RestoreFullscreen();
            }
            else if (!minimized)
            {
                Ui.SetMaximized(WindowState == FormWindowState.Maximized);
                // FRDP-POLISH-4 — a borderless MAXIMIZED window's bounds can drift off the work area (covering the
                // taskbar) when Windows nudges it for its own touch-keyboard avoidance — this can happen with our
                // own freeze mechanism bypassed entirely (hardware-keyboard suppression means no SetFrozen(false)
                // ever runs). Catch it AT THE SOURCE: any resize while maximized re-checks the true work area and
                // snaps back if it drifted. Idempotent (Bounds already correct → no-op, no re-entrant loop).
                if (WindowState == FormWindowState.Maximized) ReassertMaximizedClamp();
                FitAllChildren();   // the child tracks the host: refill the area below the bar
            }
            _wasMinimized = minimized;
        }

        /// <summary>FRDP-POLISH-4 — re-derive the CURRENT monitor's work area (same source <see
        /// cref="ConstrainMaximize"/> uses for the initial maximize) and snap <see cref="Form.Bounds"/> back onto
        /// it if it drifted. A borderless maximized window owns its own geometry — nothing else re-asserts this
        /// after the initial WM_GETMINMAXINFO-driven maximize, so a later OS-driven move (the touch keyboard
        /// changing the work area, then reverting) can leave it covering the taskbar with no self-correction.</summary>
        private void ReassertMaximizedClamp()
        {
            try
            {
                IntPtr mon = MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST);
                if (mon == IntPtr.Zero) return;
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (!GetMonitorInfo(mon, ref mi)) return;
                RECT work = mi.rcWork;
                var correct = new Rectangle(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
                if (Bounds != correct)
                {
                    FileLog.Line("[HOST] maximized bounds drifted from work area (" + Bounds + " -> " + correct + ") — re-clamping");
                    Bounds = correct;
                }
            }
            catch { /* best-effort — never break a resize over this */ }
        }

        /// <summary>Fires once the modal move/resize loop ends — the honest place to record the new geometry.</summary>
        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            if (_fullscreen || _embedHost == null) return;
            if (_sizeFrozen) { FileLog.Line("[KBD] resize-end IGNORED (frozen) bounds=" + Bounds); return; }
            FitAllChildren();
            NudgeDynamicResize();
            FileLog.Line("[HOST] resize-end bounds=" + Bounds + " embed=" + _embedHost.ClientSize);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _hoverTimer?.Stop(); _hoverTimer?.Dispose();     // FRDP-FIXSWEEP B23 — dispose the timers, not just Stop
            _childTimer.Stop(); _childTimer.Dispose();
            foreach (var s in _sessions)
            {
                if (s.IsSsh) { try { s.Ssh.Dispose(); } catch { } }
                else if (s.IsFtp) { try { s.Ftp.Dispose(); } catch { } }
                else
                {
                    try { s.Stats?.Dispose(); } catch { }
                    try { if (s.Proc != null && !s.Proc.HasExited) s.Proc.Kill(); } catch { }
                    try { s.Proc?.Dispose(); } catch { }   // FRDP-FIXSWEEP B17 — release the Process handle/plumbing
                }
            }
            try { _bar?.Close(); } catch { }
            try { _flashTimer?.Stop(); _flashTimer?.Dispose(); } catch { }
            _allHosts.Remove(this);
            // FRDP-POLISH-3 — if THIS was the join target, re-point to another live host (or null → next connect opens one).
            if (_lastActiveHost == this) _lastActiveHost = _allHosts.Count > 0 ? _allHosts[_allHosts.Count - 1] : null;
            if (--_liveHosts <= 0) { _liveHosts = 0; if (!_appExiting) ShowManager(); }   // last host gone → manager (unless app-exiting, B1)
            base.OnFormClosed(e);
        }

        private void KeepBarOnTop()
        {
            try { if (_bar != null && _bar.IsHandleCreated) SetWindowPos(_bar.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); } catch { }
        }

        /// <summary>Force this window to the foreground (defeating the foreground-lock) so it counts as the
        /// fullscreen foreground window and Windows hides the taskbar.</summary>
        private void ForceForeground()
        {
            try
            {
                Activate();
                IntPtr fg = GetForegroundWindow();
                uint fgThread = GetWindowThreadProcessId(fg, out uint _);
                uint myThread = GetCurrentThreadId();
                if (fgThread != 0 && fgThread != myThread) AttachThreadInput(myThread, fgThread, true);
                BringWindowToTop(Handle);
                SetForegroundWindow(Handle);
                SetWindowPos(Handle, HWND_TOPMOST, _mon.X, _mon.Y, _mon.Width, _mon.Height, SWP_SHOWWINDOW);
                if (fgThread != 0 && fgThread != myThread) AttachThreadInput(myThread, fgThread, false);
            }
            catch { }
        }

        private void FocusChild(IntPtr child)
        {
            try
            {
                SetForegroundWindow(Handle);
                uint me = GetCurrentThreadId();
                uint them = GetWindowThreadProcessId(child, out uint _);
                if (them != me) AttachThreadInput(me, them, true);
                SetFocus(child);
                if (them != me) AttachThreadInput(me, them, false);
            }
            catch { }
        }

        private void FitAllChildren()
        {
            if (_sizeFrozen) return;   // FIN-KBD-FREEZE — sessions pinned; content clips instead of resizing
            foreach (var s in _sessions)
                if (s.Child != IntPtr.Zero) FitChild(s.Child);
            // FitChild shows each child (SWP_SHOWWINDOW), so a hidden RDP tab can re-surface and bleed OVER an active
            // SSH/FTP tab's WinForms content (a foreign child HWND sits above WinForms siblings in z-order). Keep only
            // the ACTIVE RDP child visible — behaviour-preserving (the visible state is unchanged: active shown,
            // others hidden), fixes the coexistence bleed (FRDP-FTP-BUILD-2).
            for (int k = 0; k < _sessions.Count; k++)
                if (!_sessions[k].IsSsh && !_sessions[k].IsFtp && _sessions[k].Child != IntPtr.Zero && k != _active)
                    ShowWindow(_sessions[k].Child, SW_HIDE);
        }

        /// <summary>
        /// Make an EMBEDDED session honour <see cref="OversizeMode.Dynamic"/> (FRDP-FILL).
        ///
        /// FACT (FreeRDP 3.28 source): <c>wf_send_resize()</c> — the only caller of <c>disp->SendMonitorLayout</c>
        /// — is reachable from just four places in <c>wf_event.c</c>: WM_SIZE while <c>wfc->fullscreen</c> (:462),
        /// WM_SIZE with SIZE_MAXIMIZED (:471), WM_SIZE with SIZE_RESTORED after a maximize (:479), and
        /// WM_EXITSIZEMOVE (:492). An embedded session is a WS_CHILD we resize with SetWindowPos: it is not
        /// fullscreen, never maximized, and a child window NEVER receives WM_EXITSIZEMOVE — that goes to the
        /// top-level window running the modal resize loop, i.e. us. So <c>+dynamic-resolution</c> ALONE is inert
        /// when embedded and the session letterboxes (measured: black band = 100%).
        ///
        /// Fix, with the engine untouched: post the child the very message its own window proc already handles.
        /// WM_SIZE has kept <c>client_width/height</c> current, so the engine sends the monitor layout for the new
        /// size. It self-throttles (RESIZE_MIN_DELAY) and no-ops when the size is unchanged, so a spare post is
        /// free. Only sessions that explicitly chose Dynamic are poked — Letterbox stays untouched, by design.
        /// </summary>
        private void NudgeDynamicResize()
        {
            if (_sizeFrozen) return;   // FIN-KBD-FREEZE — no renegotiation while frozen
            foreach (var s in _sessions)
            {
                if (s.Child == IntPtr.Zero || !IsDynamic(s)) continue;
                try { PostMessage(s.Child, WM_EXITSIZEMOVE, IntPtr.Zero, IntPtr.Zero); } catch { }
            }
        }

        /// <summary>Size the embedded child to fill the embed panel. This resizes the child WINDOW only — what the
        /// SESSION does about the mismatch is the user's <see cref="OversizeMode"/> choice (letterbox / scale /
        /// renegotiate); see <see cref="NudgeDynamicResize"/> for the last one.</summary>
        private void FitChild(IntPtr child)
        {
            try
            {
                var sz = _embedHost.ClientSize;
                int style = GetWindowLong(child, GWL_STYLE);
                SetWindowLong(child, GWL_STYLE, style & ~WS_BORDER);   // strip the thin border → edge-to-edge
                SetWindowPos(child, IntPtr.Zero, 0, 0, sz.Width, sz.Height, SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
                // wfreerdp's window class does not redraw on resize (no CS_HREDRAW/CS_VREDRAW) and it swallows
                // WM_ERASEBKGND, so a SetWindowPos'd child just keeps its stale pixels: no WM_PAINT means
                // wf_scale_blt never re-blits and /smart-sizing looks exactly like letterbox (measured). Ask for a
                // full repaint. Letterbox is unaffected (its BitBlt has nothing to draw past the framebuffer).
                InvalidateRect(child, IntPtr.Zero, true);
            }
            catch { }
        }

        private static IntPtr FindChildByPid(IntPtr parent, int pid)
        {
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(parent, (h, l) =>
            {
                GetWindowThreadProcessId(h, out uint wpid);
                if (wpid == (uint)pid && GetParent(h) == parent) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        // ── windowed frame: borderless but resizable (WM_NCHITTEST) + maximize clamped to the work area ──
        protected override void WndProc(ref Message m)
        {
            if (!_fullscreen)
            {
                if (m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal)
                {
                    int lp = unchecked((int)(long)m.LParam);
                    var p = PointToClient(new Point(unchecked((short)(lp & 0xFFFF)), unchecked((short)((lp >> 16) & 0xFFFF))));
                    int ht = HitTestFrame(p);
                    if (ht != 0) { m.Result = (IntPtr)ht; return; }
                }
                else if (m.Msg == WM_GETMINMAXINFO)
                {
                    ConstrainMaximize(m.LParam);
                    base.WndProc(ref m);
                    return;
                }
            }
            // FIN-KEYBOARD — same WA_ACTIVE auto-refocus guard as ThemedDialog (see there for the full
            // rationale): the FTP pane's path textboxes are the only TextBoxBase controls a SessionHost
            // can have focused, so this only ever fires for them — RDP (native child) and SSH (owner-drawn
            // TerminalControl, not a TextBoxBase) are untouched. Checked unconditionally (fullscreen or not).
            if (m.Msg == WM_ACTIVATE && (m.WParam.ToInt64() & 0xFFFF) == WA_ACTIVE)
                try { BeginInvoke((Action)ClearAutoRefocusedTextField); } catch { }
            base.WndProc(ref m);
        }

        private const int WM_ACTIVATE = 0x0006;
        private const long WA_ACTIVE = 1;

        private void ClearAutoRefocusedTextField()
        {
            try { if (!IsDisposed && ActiveControl is TextBoxBase) ActiveControl = null; }
            catch { }
        }

        private int HitTestFrame(Point p)
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            bool l = p.X <= Frame, r = p.X >= w - Frame, t = p.Y <= Frame, b = p.Y >= h - Frame;
            if (t && l) return HTTOPLEFT;
            if (t && r) return HTTOPRIGHT;
            if (b && l) return HTBOTTOMLEFT;
            if (b && r) return HTBOTTOMRIGHT;
            if (l) return HTLEFT;
            if (r) return HTRIGHT;
            if (t) return HTTOP;
            if (b) return HTBOTTOM;
            return 0;
        }

        /// <summary>A borderless maximize would cover the taskbar — clamp it to the monitor work area.</summary>
        private void ConstrainMaximize(IntPtr lParam)
        {
            try
            {
                var mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));
                IntPtr mon = MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST);
                if (mon == IntPtr.Zero) return;
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (!GetMonitorInfo(mon, ref mi)) return;
                RECT work = mi.rcWork, full = mi.rcMonitor;
                mmi.ptMaxPosition.X = work.Left - full.Left;
                mmi.ptMaxPosition.Y = work.Top - full.Top;
                mmi.ptMaxSize.X = work.Right - work.Left;
                mmi.ptMaxSize.Y = work.Bottom - work.Top;
                mmi.ptMinTrackSize.X = MinimumSize.Width;
                mmi.ptMinTrackSize.Y = MinimumSize.Height;
                Marshal.StructureToPtr(mmi, lParam, true);
            }
            catch { /* leave default maximize bounds on any failure */ }
        }

        // ── native ──
        private const int GWL_STYLE = -16;
        private const int WS_BORDER = 0x00800000;
        private const int SW_HIDE = 0, SW_SHOW = 5;
        private const int WM_NCHITTEST = 0x0084, WM_GETMINMAXINFO = 0x0024, WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_EXITSIZEMOVE = 0x0232;   // posted to the embedded child to drive OversizeMode.Dynamic
        private const int HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
                          HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        private const int MONITOR_DEFAULTTONEAREST = 2;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040, SWP_FRAMECHANGED = 0x0020;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int idx);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int idx, int val);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, int flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
        [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
    }
}
