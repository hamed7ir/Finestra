using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Finestra.UI.Controls;

namespace Finestra.UI
{
    /// <summary>
    /// The FULLSCREEN presentation of the shared tab model: a borderless, TOPMOST, no-activation form that
    /// slides in at the top edge on hover and parks off-screen otherwise. It is a separate top-level window
    /// (not a child of wfreerdp) so it is GUARANTEED to paint above the heavy embedded child — the native
    /// floatbar, a WS_CHILD of wfreerdp, did not reliably show.
    ///
    /// It owns no tab logic: it hosts a <see cref="SessionTabBar"/> and forwards its events. The windowed host
    /// docks that same control instead of hovering it (FRDP-UI-WINDOWED) — one tab model, two presentations.
    /// </summary>
    public sealed class OverlayBar : Form, ISessionBar
    {
        public const int BarHeight = SessionTabBar.BarHeight;

        private readonly SessionTabBar _tab;

        public event Action<int> TabClicked;
        public event Action<int> TabCloseClicked;
        public event Action AddClicked;
        public event Action MinimizeClicked;
        public event Action RestoreClicked;
        public event Action CloseClicked;
        public event Action PauseToggled;
        public event Action FullscreenToggle;
        public event Action<int, System.Drawing.Point> TabRightClicked;
        public event Action ReconnectClicked;   // FRDP-RECONNECT

        protected override bool ShowWithoutActivation => true;   // never steal focus from the RDP session
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW (no taskbar/Alt-Tab entry)
                return cp;
            }
        }

        public OverlayBar()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            Height = BarHeight;

            _tab = new SessionTabBar { Dock = DockStyle.Fill };
            _tab.TabClicked += i => TabClicked?.Invoke(i);
            _tab.TabCloseClicked += i => TabCloseClicked?.Invoke(i);
            _tab.AddClicked += () => AddClicked?.Invoke();
            _tab.MinimizeClicked += () => MinimizeClicked?.Invoke();
            _tab.RestoreClicked += () => RestoreClicked?.Invoke();
            _tab.CloseClicked += () => CloseClicked?.Invoke();
            _tab.PauseToggled += () => PauseToggled?.Invoke();
            _tab.FullscreenToggle += () => FullscreenToggle?.Invoke();
            _tab.TabRightClicked += (t, p) => TabRightClicked?.Invoke(t, p);
            _tab.ReconnectClicked += () => ReconnectClicked?.Invoke();   // FRDP-RECONNECT
            Controls.Add(_tab);
        }

        public void SetTabs(List<string> names, IList<SessionKind> kinds, int active) => _tab.SetTabs(names, kinds, active);
        public void SetActive(int i) => _tab.SetActive(i);
        public void SetStats(string s) => _tab.SetStats(s);
        public void SetPaused(bool paused) => _tab.SetPaused(paused);
        public void SetPauseVisible(bool visible) => _tab.SetPauseVisible(visible);
        public void SetMaximized(bool maximized) { /* the fullscreen host has no maximize state */ }
        public void SetFullscreen(bool fullscreen) => _tab.SetFullscreen(fullscreen);
        public void SetReconnect(bool visible, bool busy) => _tab.SetReconnect(visible, busy);   // FRDP-RECONNECT

        /// <summary>Slide the bar down to reveal it at the given top-left, full monitor width.</summary>
        public void RevealTo(int x, int y, int width)
        {
            SetBounds(x, y, width, BarHeight);
            if (!Visible) Show();
        }

        /// <summary>Slide the bar up off the top edge (hidden but alive).</summary>
        public void HideAbove(int x, int y, int width) => SetBounds(x, y - BarHeight, width, BarHeight);
    }
}
