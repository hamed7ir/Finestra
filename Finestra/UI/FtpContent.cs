using System;
using System.Windows.Forms;
using Finestra.Core;
using Finestra.UI.Controls;

namespace Finestra.UI
{
    /// <summary>
    /// FRDP-FTP-BUILD-2 — the FTP counterpart of <see cref="SshContent"/>: the third content type in a
    /// <see cref="SessionHost"/> tab. Like SSH it is an app-owned control (the <see cref="FtpBrowserControl"/>
    /// dual-pane browser) — no HWND embed, no PID — so attach / tear-off / fullscreen are ordinary WinForms
    /// re-parenting/dock. RDP and SSH paths in SessionHost stay byte-for-byte; FTP is a parallel branch guarded by
    /// <c>Session.IsFtp</c>. The browser control + its live <see cref="IRemoteFileSystem"/> session move together on
    /// tear-off, so the connection stays open.
    /// </summary>
    internal sealed class FtpContent : IDisposable
    {
        public readonly ConnectionProfile Profile;
        private readonly FtpBrowserControl _browser;
        private Panel _panel;

        /// <summary>Bar readout changed (● connected · path / transfer progress) — host pushes it if this tab is active.</summary>
        public event Action<FtpContent> StatusChanged;
        /// <summary>Connect failed (error already shown) — host closes this tab.</summary>
        public event Action<FtpContent> ConnectFailed;

        public FtpContent(ConnectionProfile cp)
        {
            Profile = cp;
            _browser = new FtpBrowserControl(cp) { Dock = DockStyle.Fill, Visible = false };
            _browser.StatusChanged += b => StatusChanged?.Invoke(this);
            _browser.ConnectFailed += b => ConnectFailed?.Invoke(this);
        }

        public string Title => string.IsNullOrEmpty(Profile.Name) ? Profile.Host : Profile.Name;
        public string StatusText => _browser.StatusText;

        // FRDP-RECONNECT — delegate the reconnect surface to the browser control.
        public bool ShowReconnect => _browser.ShowReconnect;
        public bool Reconnecting => _browser.Reconnecting;
        public void Reconnect() => _browser.Reconnect();

        /// <summary>Parent the browser into the host's embed panel + connect off the UI thread. Starts hidden; the
        /// host's SetActive reveals the active tab.</summary>
        public void Start(Panel embedHost)
        {
            _panel = embedHost;
            embedHost.Controls.Add(_browser);
            _browser.BringToFront();
            _browser.Start();
        }

        public void SetVisible(bool visible) { _browser.Visible = visible; if (visible) _browser.BringToFront(); }
        public void Focus() { try { _browser.FocusPane(); } catch { } }

        /// <summary>Tear-off / merge: move the browser control (with its live session) to another host's panel — a
        /// trivial managed re-parent; the IRemoteFileSystem connection is untouched (stays open).</summary>
        public void Reparent(Panel dest)
        {
            try
            {
                _panel?.Controls.Remove(_browser);
                dest.Controls.Add(_browser);
                _panel = dest;
                _browser.Dock = DockStyle.Fill;
                _browser.BringToFront();
            }
            catch { }
        }

        public void Dispose() { try { _browser.Dispose(); } catch { } }
    }
}
