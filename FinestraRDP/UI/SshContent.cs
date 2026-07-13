using System;
using System.Drawing;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows.Forms;
using Finestra.Core;
using Finestra.UI.Controls;

namespace Finestra.UI
{
    /// <summary>
    /// FRDP-SSH-BUILD-2 — the SSH counterpart to an embedded wfreerdp child inside a <see cref="SessionHost"/> tab.
    /// Where an RDP session is a foreign child HWND the host sizes with SetParent/SetWindowPos, an SSH session is a
    /// <see cref="TerminalControl"/> the app OWNS: attaching, tearing off, and fullscreen are ordinary WinForms
    /// re-parenting/dock — no HWND embed, no PID, no stats pipe. This class holds that control + its
    /// <see cref="SshSession"/> and exposes the small surface SessionHost drives (Start / SetVisible / Focus /
    /// Reparent / Tick / Dispose), so the RDP path in SessionHost stays byte-for-byte unchanged and SSH is a
    /// parallel branch. Host-key TOFU + passphrase prompts + friendly errors are reused from FRDP-SSH-AUTH.
    /// </summary>
    internal sealed class SshContent : IDisposable
    {
        public readonly ConnectionProfile Profile;
        private readonly TerminalControl _term;
        private SshSession _session;
        private Panel _panel;
        private volatile bool _connected;
        private volatile bool _dropped;        // FRDP-RECONNECT — was live, now dropped (Reconnect button shows)
        private volatile bool _reconnecting;   // FRDP-RECONNECT — a reconnect attempt is running ("Reconnecting…")
        private string _status = "connecting…";
        private int _tick;
        private volatile bool _pinging;   // FRDP-FIXSWEEP B25 — toggled on the UI thread + the ThreadPool ping callback

        /// <summary>The status/ping readout changed — host pushes it to the bar if this tab is active.</summary>
        public event Action<SshContent> StatusChanged;
        /// <summary>The connect attempt failed (error already shown) — host closes this tab.</summary>
        public event Action<SshContent> ConnectFailed;

        public SshContent(ConnectionProfile cp)
        {
            Profile = cp;
            _term = new TerminalControl { Dock = DockStyle.Fill, Visible = false };
            // apply this connection's terminal prefs (FRDP-POLISH-2) — colors / font / scrollback
            var prefs = cp.Ssh?.Terminal ?? new TerminalPrefs();
            _term.Monochrome = !prefs.Colors;
            _term.SetFontSize(prefs.ClampedFont);
            _term.Scrollback = prefs.ClampedScrollback;
        }

        public string Title => string.IsNullOrEmpty(Profile.Name) ? Profile.Host : Profile.Name;
        public string StatusText => _status;

        /// <summary>FRDP-RECONNECT — the host shows the bar Reconnect button when true (dropped OR mid-attempt).</summary>
        public bool ShowReconnect => _dropped || _reconnecting;
        /// <summary>FRDP-RECONNECT — an attempt is in flight → the button reads "Reconnecting…" (disabled).</summary>
        public bool Reconnecting => _reconnecting;

        // ── lifecycle ────────────────────────────────────────────────────────────
        /// <summary>Parent the terminal into the host's embed panel and connect off the UI thread. The terminal
        /// starts hidden; the host's SetActive reveals the active tab.</summary>
        public void Start(Panel embedHost)
        {
            _panel = embedHost;
            embedHost.Controls.Add(_term);
            _term.BringToFront();
            // FRDP-RECONNECT — wire terminal input/resize ONCE; they forward to the CURRENT session and go inert while
            // dropped, so a reconnect just swaps _session (no re-wiring, no accumulating handlers).
            _term.Input += OnTermInput;
            _term.Resized += OnTermResized;
            RunConnect(false);
        }

        /// <summary>Build a fresh SshSession + shell off the UI thread and hand it to GoLive. Used for the first
        /// connect AND for reconnect — same profile, DPAPI creds, and the full fail-closed host-key TOFU each time.</summary>
        private void RunConnect(bool isReconnect)
        {
            Form owner = _panel != null ? _panel.FindForm() : null;   // resolve live (B10 — the panel may have moved on tear-off)
            int cols = _term.Cols, rows = _term.Rows;
            int w = Math.Max(100, _panel != null ? _panel.ClientSize.Width : _term.ClientSize.Width);
            int h = Math.Max(60, _panel != null ? _panel.ClientSize.Height : _term.ClientSize.Height);
            Task.Run(() =>
            {
                var s = new SshSession(Profile);
                s.HostKeyVerifier = prompt =>
                {
                    try { return (owner == null || owner.IsDisposed) ? HostKeyDecision.Reject : (HostKeyDecision)owner.Invoke(new Func<HostKeyDecision>(() => HostKeyDialog.Ask(owner, prompt))); }
                    catch { return HostKeyDecision.Reject; }
                };
                s.PassphraseProvider = () =>
                {
                    try { return (owner == null || owner.IsDisposed) ? null : (string)owner.Invoke(new Func<string>(() => PassphrasePrompt.Ask(owner, Profile.Host, Profile.Ssh?.PrivateKeyPath))); }
                    catch { return null; }
                };
                try { s.Connect(cols, rows, w, h); Post(owner, () => GoLive(s)); }
                catch (Exception ex) { try { s.Dispose(); } catch { } Post(owner, () => { if (isReconnect) ReconnectFailed(ex); else Fail(owner, ex); }); }
            });
        }

        private void OnTermInput(byte[] bytes) { var s = _session; if (_connected && s != null) s.Send(bytes); }
        private void OnTermResized(int c, int r, int pw, int ph) { var s = _session; if (_connected && s != null) s.Resize(c, r, pw, ph); }

        private static void Post(Form owner, Action a)
        {
            try { if (owner != null && !owner.IsDisposed) owner.BeginInvoke(a); } catch { }
        }

        private void GoLive(SshSession s)
        {
            if (_term.IsDisposed) { try { s.Dispose(); } catch { } return; }   // tab closed mid-attempt → bail, no leak
            _session = s;
            _connected = true; _dropped = false; _reconnecting = false;
            s.Received += bytes => { try { if (!_term.IsDisposed) _term.BeginInvoke((Action)(() => _term.Feed(bytes))); } catch { } };
            s.Closed += reason => { try { if (!_term.IsDisposed) _term.BeginInvoke((Action)(() => OnClosed(reason))); } catch { } };
            s.Resize(_term.Cols, _term.Rows, _term.ClientSize.Width, _term.ClientSize.Height);   // push current size now that it's live
            SetStatus("● connected · ping …");
            if (_term.Visible) _term.Focus();
        }

        private void Fail(Form owner, Exception ex)
        {
            try { MessageBox.Show(owner, SshErrors.Explain(ex, Profile.Host), "Finestra — SSH", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
            ConnectFailed?.Invoke(this);
        }

        // FRDP-RECONNECT — the session dropped (pump end / error / failed write): tell the truth. A terminal line, inert
        // input (OnTermInput gates on _connected), ● disconnected, and the host reveals the Reconnect button.
        private void OnClosed(string reason)
        {
            if (_reconnecting) return;   // a stale drop from a torn-down session during an attempt — ignore
            _connected = false; _dropped = true;
            try { _term.Feed(System.Text.Encoding.UTF8.GetBytes("\r\n*** connection lost — " + DateTime.Now.ToString("HH:mm:ss") + " ***\r\n")); } catch { }
            SetStatus("● disconnected");
        }

        /// <summary>FRDP-RECONNECT — rebuild the transport IN PLACE into the SAME terminal (scrollback preserved).
        /// Manual only, no auto-retry; guards double-click / re-entry.</summary>
        public void Reconnect()
        {
            if (!_dropped || _reconnecting) return;
            _reconnecting = true;
            try { _session?.Dispose(); } catch { }
            _session = null;
            SetStatus("● reconnecting…");   // → StatusChanged → host shows the busy button
            RunConnect(true);
        }

        private void ReconnectFailed(Exception ex)
        {
            _reconnecting = false; _dropped = true;
            try { _term.Feed(System.Text.Encoding.UTF8.GetBytes("\r\n*** reconnect failed ***\r\n")); } catch { }
            SetStatus("● disconnected");   // → host re-enables the Reconnect button
            try { ConfirmDialog.Info(_panel != null ? _panel.FindForm() : null, SshErrors.Explain(ex, Profile.Host), "Finestra — SSH"); } catch { }
        }

        // ── host-driven surface ──────────────────────────────────────────────────
        public void SetVisible(bool visible) { _term.Visible = visible; if (visible) { _term.BringToFront(); } }
        public void Focus() { try { if (!_term.IsDisposed) _term.Focus(); } catch { } }

        // FRDP-POLISH-2 — live prefs from the tab right-click menu. SESSION-ONLY (not written back to the saved
        // profile; the editor/Settings is the persistent home). Font change → grid reflow → remote pty resize.
        public bool ColorsOn => !_term.Monochrome;
        public int FontSize => _term.FontSize;
        public void SetColors(bool on) { _term.Monochrome = !on; }
        public void AdjustFontSize(int delta) { _term.AdjustFontSize(delta); }

        /// <summary>Tear-off / merge: move the terminal control between host embed panels — a trivial managed
        /// re-parent (unlike RDP's cross-process SetParent). The SshSession is untouched → the shell stays
        /// connected, no re-login.</summary>
        public void Reparent(Panel dest)
        {
            try
            {
                _panel?.Controls.Remove(_term);
                dest.Controls.Add(_term);
                _panel = dest;
                _term.Dock = DockStyle.Fill;
                _term.BringToFront();
            }
            catch { }
        }

        /// <summary>250 ms housekeeping: an app-measured ICMP ping to the host every ~3 s while connected — labelled
        /// distinctly from RDP's protocol stats so the two can't be confused.</summary>
        public void Tick()
        {
            if (!_connected) return;
            if (++_tick % 12 != 0) return;   // ~3 s at the host's 250 ms cadence
            Ping();
        }

        private void Ping()
        {
            if (_pinging) return;
            _pinging = true;
            Ping p = null;
            try
            {
                p = new Ping();
                p.PingCompleted += (s, e) =>
                {
                    _pinging = false;
                    try
                    {
                        if (!_connected) return;
                        if (e.Reply != null && e.Reply.Status == IPStatus.Success)
                            SetStatus("● connected · ping " + e.Reply.RoundtripTime + " ms");
                        else
                            SetStatus("● connected · ping n/a");
                    }
                    catch { }
                    finally { try { ((Ping)s).Dispose(); } catch { } }
                };
                p.SendAsync(Profile.Host, 1000, new byte[16], null);
            }
            catch { _pinging = false; try { p?.Dispose(); } catch { } try { SetStatus("● connected · ping n/a"); } catch { } }   // B25 — dispose the Ping on a synchronous throw
        }

        private void SetStatus(string s) { _status = s; StatusChanged?.Invoke(this); }

        public void Dispose()
        {
            _connected = false;
            try { _session?.Dispose(); } catch { }
            try { _term.Dispose(); } catch { }
        }
    }
}
