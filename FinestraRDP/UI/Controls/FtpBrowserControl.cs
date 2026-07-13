using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Finestra.Core;
using Finestra.Helpers;
using Finestra.UI;

namespace Finestra.UI.Controls
{
    /// <summary>
    /// FRDP-FTP-BUILD-2 — the reusable dual-pane file-browser CONTENT (local ↔ remote + a transfer bar), extracted
    /// from BUILD-1's standalone form so it can be hosted BOTH ways: by <see cref="UI.FtpBrowserForm"/> (standalone
    /// window) and by <see cref="UI.FtpContent"/> (a tab in the SessionHost shell). It owns a
    /// <see cref="LocalFileSystem"/> (left) + a remote <see cref="IRemoteFileSystem"/> (right) and talks ONLY to the
    /// interface — no backend branches. It implements <see cref="IRemotePrompts"/>, marshalling host-key / passphrase
    /// / TLS-cert prompts to the themed dialogs over its host form. Connects + transfers off the UI thread; exposes a
    /// summary <see cref="StatusText"/> (● connected · path / transfer progress) for the shell's per-type bar.
    /// </summary>
    internal sealed class FtpBrowserControl : Panel, IRemotePrompts
    {
        private readonly ConnectionProfile _cp;
        private readonly SplitContainer _split;
        private readonly FileBrowserPane _local, _remote;
        private readonly Panel _statusBar;
        private readonly RoundedButton _upload, _download;
        private readonly ProgressBar _progress;
        private readonly Label _status;
        private IRemoteFileSystem _remoteFs;
        private bool _transferring, _splitInit;
        private volatile bool _dropped;        // FRDP-RECONNECT — was live, now dropped
        private volatile bool _reconnecting;   // FRDP-RECONNECT — an attempt is running
        private string _lastPath = "/";        // remote path to restore on reconnect
        private string _barStatus = "connecting…";

        /// <summary>Summary readout changed (for the shell's tab bar).</summary>
        public event Action<FtpBrowserControl> StatusChanged;
        /// <summary>Connect failed (error already shown) — host closes the tab / standalone form.</summary>
        public event Action<FtpBrowserControl> ConnectFailed;

        public string StatusText => _barStatus;
        /// <summary>FRDP-RECONNECT — the host shows the bar Reconnect button when true (dropped OR mid-attempt).</summary>
        public bool ShowReconnect => _dropped || _reconnecting;
        public bool Reconnecting => _reconnecting;

        public FtpBrowserControl(ConnectionProfile cp)
        {
            _cp = cp;
            DoubleBuffered = true;
            BackColor = ThemeHelper.IsDark ? Color.FromArgb(28, 28, 32) : Color.FromArgb(250, 250, 252);

            _statusBar = new Panel { Dock = DockStyle.Bottom, Height = 46 };
            _statusBar.Paint += (s, e) => { using (var b = new SolidBrush(BarBg())) e.Graphics.FillRectangle(b, _statusBar.ClientRectangle); };
            _upload = new RoundedButton { Text = "Upload →", Kind = RoundedButtonKind.Primary, Width = 116, Height = 32, Enabled = false, Font = FontHelper.Ui(9.5f, FontStyle.Bold) };
            _download = new RoundedButton { Text = "← Download", Kind = RoundedButtonKind.Primary, Width = 124, Height = 32, Enabled = false, Font = FontHelper.Ui(9.5f, FontStyle.Bold) };
            _upload.Click += (s, e) => DoUpload();
            _download.Click += (s, e) => DoDownload();
            _progress = new ProgressBar { Style = ProgressBarStyle.Continuous, Height = 14, Visible = false };
            _status = new Label { AutoSize = false, BackColor = Color.Transparent, Font = FontHelper.Ui(9f), TextAlign = ContentAlignment.MiddleLeft, Text = "" };
            _statusBar.Controls.Add(_upload); _statusBar.Controls.Add(_download); _statusBar.Controls.Add(_progress); _statusBar.Controls.Add(_status);

            _local = new FileBrowserPane { Dock = DockStyle.Fill }; _local.SetTitle("This PC");
            _remote = new FileBrowserPane { Dock = DockStyle.Fill }; _remote.SetTitle(cp.Host);
            _local.SelectionChanged += UpdateButtons;
            _remote.SelectionChanged += UpdateButtons;
            _remote.PathChanged += () => { _lastPath = _remote.CurrentPath; if (!_transferring && _remoteFs != null && !_dropped) SetBarStatus("● connected · " + _remote.CurrentPath); };
            _remote.RemoteError += OnRemoteError;   // FRDP-RECONNECT — a remote List failure may be a drop (op-driven, e.g. FTP)
            _split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 6 };
            _split.Panel1.Controls.Add(_local);
            _split.Panel2.Controls.Add(_remote);

            Controls.Add(_split);       // Fill (added first)
            Controls.Add(_statusBar);   // Bottom

            ApplyTheme();
            ThemeHelper.ThemeChanged += ApplyTheme;   // FRDP-POLISH-3 — recolor bg/transfer-bar/status/splitter on a live theme flip
        }

        private static Color BarBg() => ThemeHelper.IsDark ? Color.FromArgb(38, 38, 43) : Color.FromArgb(236, 236, 240);

        /// <summary>FRDP-POLISH-3 — re-read this control's OWN themed surfaces (the panes/list/buttons re-theme
        /// themselves). Runs in the ctor and on every <see cref="ThemeHelper.ThemeChanged"/> so a System/Light/Dark
        /// flip recolors the FTP browser immediately, with no stale elements.</summary>
        private void ApplyTheme()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke((Action)ApplyTheme); } catch { } return; }
            bool dark = ThemeHelper.IsDark;
            BackColor = dark ? Color.FromArgb(28, 28, 32) : Color.FromArgb(250, 250, 252);
            Color edge = dark ? Color.FromArgb(20, 20, 24) : Color.FromArgb(222, 222, 228);   // the 6px splitter seam
            if (_split != null) { _split.BackColor = edge; _split.Panel1.BackColor = edge; _split.Panel2.BackColor = edge; }
            if (_status != null) _status.ForeColor = dark ? Color.FromArgb(200, 200, 206) : Color.FromArgb(60, 60, 66);
            if (_statusBar != null) _statusBar.Invalidate();
            Invalidate();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (!_splitInit && _split != null && Width > 200) { try { _split.SplitterDistance = Width / 2; _splitInit = true; } catch { } }
        }

        // ── lifecycle ──
        /// <summary>Bind the local pane, connect the remote off the UI thread. Call after the control is in a form.</summary>
        public void Start()
        {
            var local = new LocalFileSystem();
            var remote = CreateBackend(_cp, this);
            Task.Run(() =>
            {
                // FRDP-FIXSWEEP B19 — resolve the remote home dir HERE (worker thread); for FTP it's a live PWD round-trip
                // and GoLive runs on the UI thread — reading it there (twice) stalled the UI on a high-latency link.
                try { remote.Connect(); string home = SafeHome(remote); Post(() => GoLive(local, remote, home)); }
                catch (Exception ex) { try { remote.Dispose(); } catch { } Post(() => Fail(ex)); }
            });
        }

        private static string SafeHome(IRemoteFileSystem fs) { try { return fs.HomeDirectory; } catch { return "/"; } }

        private void Post(Action a) { try { if (!IsDisposed) BeginInvoke(a); } catch { } }

        private static IRemoteFileSystem CreateBackend(ConnectionProfile cp, IRemotePrompts prompts)
        {
            var ftp = cp.Ftp ?? new FtpSettings();
            return ftp.Protocol == FtpProtocol.Sftp ? (IRemoteFileSystem)new SftpBackend(cp, prompts) : new FtpBackend(cp, prompts);
        }

        private void GoLive(LocalFileSystem local, IRemoteFileSystem remote, string remoteHome)
        {
            if (IsDisposed) { try { remote.Dispose(); } catch { } return; }
            _remoteFs = remote;
            remote.Dropped += OnRemoteDroppedRaw;   // FRDP-RECONNECT — SFTP idle-drop signal
            _lastPath = remoteHome;
            _remote.SetTitle(remote.Protocol + "  " + _cp.Host);
            _local.Bind(local); _local.NavigateAsync(local.HomeDirectory);   // local home is a fast local call
            _remote.Bind(remote); _remote.NavigateAsync(remoteHome);          // remoteHome captured off the UI thread (B19)
            SetBarStatus("● connected · " + remoteHome);
            UpdateButtons();
        }

        // ── FRDP-RECONNECT ───────────────────────────────────────────────────────
        private void OnRemoteDroppedRaw() { Post(() => MarkDropped()); }        // backend event (SSH.NET thread) → UI
        private void OnRemoteError(Exception ex) { if (IsDrop(ex)) MarkDropped(); }   // op-driven (already on the UI thread)

        private void MarkDropped()
        {
            if (IsDisposed || _dropped || _reconnecting) return;   // debounce — once
            _dropped = true;
            try { _remote.SetLive(false); } catch { }              // remote pane ops off; local stays usable
            UpdateButtons();
            SetBarStatus("● disconnected · " + _lastPath);
        }

        /// <summary>Is this exception a transport drop (vs a normal op error like permission-denied)?</summary>
        private static bool IsDrop(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is System.Net.Sockets.SocketException || e is ObjectDisposedException || e is System.IO.IOException) return true;
                string t = e.GetType().Name;
                if (t.IndexOf("SshConnection", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("SshException", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                var rfe = e as RemoteFsException;
                if (rfe != null && (rfe.Error == RemoteFsError.Unreachable || rfe.Error == RemoteFsError.Protocol || rfe.Error == RemoteFsError.Tls)) return true;
            }
            return false;
        }

        /// <summary>FRDP-RECONNECT — rebuild the backend (fresh client + fresh B9 semaphore, NOT the dead one), reconnect,
        /// refresh the remote pane to the last path (home if it's gone). Manual only; guards double-click / re-entry.</summary>
        public void Reconnect()
        {
            if (!_dropped || _reconnecting) return;
            _reconnecting = true;
            SetBarStatus("● reconnecting…");
            UpdateButtons();
            string path = _lastPath;
            var old = _remoteFs;
            try { if (old != null) old.Dropped -= OnRemoteDroppedRaw; } catch { }
            try { old?.Dispose(); } catch { }
            _remoteFs = null;
            Task.Run(() =>
            {
                var remote = CreateBackend(_cp, this);
                try
                {
                    remote.Connect();
                    bool pathOk = false; try { pathOk = remote.Exists(path); } catch { }
                    string home = SafeHome(remote);
                    Post(() => ReconnectLive(remote, pathOk ? path : home));
                }
                catch (Exception ex) { try { remote.Dispose(); } catch { } Post(() => ReconnectFailed(ex)); }
            });
        }

        private void ReconnectLive(IRemoteFileSystem remote, string path)
        {
            if (IsDisposed) { try { remote.Dispose(); } catch { } return; }   // tab closed mid-attempt → bail, no leak
            _remoteFs = remote;
            remote.Dropped += OnRemoteDroppedRaw;
            _dropped = false; _reconnecting = false;
            _remote.SetLive(true);
            _remote.SetTitle(remote.Protocol + "  " + _cp.Host);
            _remote.Bind(remote);
            _remote.NavigateAsync(path);
            SetBarStatus("● connected · " + path);
            UpdateButtons();
        }

        private void ReconnectFailed(Exception ex)
        {
            _reconnecting = false; _dropped = true;
            SetBarStatus("● disconnected · " + _lastPath);   // → host re-enables the Reconnect button
            UpdateButtons();
            string msg = ex is RemoteFsException fe ? FriendlyFs(fe) : "Could not reconnect to " + _cp.Host + ":\n\n" + ex.Message;
            try { ConfirmDialog.Info(FindForm() ?? (IWin32Window)this, msg, "Finestra — Files"); } catch { }
        }

        private void Fail(Exception ex)
        {
            if (IsDisposed) return;
            string msg = ex is RemoteFsException fe ? FriendlyFs(fe) : "Could not connect to " + _cp.Host + ":\n\n" + ex.Message;
            SetBarStatus("○ " + (ex is RemoteFsException f2 ? f2.Error.ToString().ToLowerInvariant() : "failed"));
            try { MessageBox.Show(FindForm() ?? (IWin32Window)this, msg, "Finestra — Files", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }   // B10 — resolve owner live
            ConnectFailed?.Invoke(this);
        }

        private static string FriendlyFs(RemoteFsException fe)
        {
            switch (fe.Error)
            {
                case RemoteFsError.HostKeyRejected: return "Host key verification failed.\n\nThe server's key was not trusted, so the connection was cancelled.";
                case RemoteFsError.CertRejected: return "The server's TLS certificate was not trusted, so the connection was cancelled.";
                case RemoteFsError.Tls: return fe.Message + "\n\nFor explicit TLS use port 21; implicit TLS is port 990.";
                default: return fe.Message;
            }
        }

        public void FocusPane() { try { _remote.FocusList(); } catch { } }
        public void Dispose_() => Dispose();

        // ── transfers ──
        private void UpdateButtons()
        {
            if (_transferring || _dropped || _reconnecting) { _upload.Enabled = _download.Enabled = false; return; }   // FRDP-RECONNECT
            var l = _local.Selected; var r = _remote.Selected;
            _upload.Enabled = _remoteFs != null && l != null && !l.Value.IsDirectory;
            _download.Enabled = _remoteFs != null && r != null && !r.Value.IsDirectory;
        }

        private void DoUpload()
        {
            var sel = _local.Selected; if (sel == null || sel.Value.IsDirectory || _remoteFs == null) return;
            string local = sel.Value.FullPath, name = sel.Value.Name, remote = _remoteFs.Combine(_remote.CurrentPath, name);
            RunTransfer("↑ " + name, () => ConfirmOverwrite(_remoteFs, remote, name), () => _remoteFs.Upload(local, remote, Report), () => _remote.Reload());
        }

        private void DoDownload()
        {
            var sel = _remote.Selected; if (sel == null || sel.Value.IsDirectory || _remoteFs == null) return;
            string name = sel.Value.Name;
            // FRDP-FIXSWEEP B5 — at the drive-list root the local path is "" → Combine gives a RELATIVE name that lands
            // in the exe's CWD. Block it with a themed prompt rather than a hidden default destination.
            if (string.IsNullOrEmpty(_local.CurrentPath))
            {
                ConfirmDialog.Info(FindForm() ?? (IWin32Window)this, "Open a drive or folder in the left pane first, so there's a destination to download into.", "Finestra — Files");
                return;
            }
            string remote = sel.Value.FullPath, local = _local.Fs.Combine(_local.CurrentPath, name);
            RunTransfer("↓ " + name, () => ConfirmOverwrite(_local.Fs, local, name), () => _remoteFs.Download(remote, local, Report), () => _local.Reload());
        }

        /// <summary>FRDP-FIXSWEEP B2 — runs on the transfer worker; if the destination already exists, ask (themed,
        /// marshaled to the UI) before replacing it. Returns true to proceed, false to skip.</summary>
        private bool ConfirmOverwrite(IRemoteFileSystem destFs, string destPath, string name)
        {
            bool exists;
            try { exists = destFs != null && destFs.Exists(destPath); } catch { exists = false; }
            if (!exists) return true;
            try
            {
                if (IsDisposed || !IsHandleCreated) return false;
                return (bool)Invoke(new Func<bool>(() =>
                    ConfirmDialog.Ask(FindForm() ?? (IWin32Window)this,
                        "Replace “" + name + "”?\n\nA file with this name already exists at the destination.",
                        "Finestra — Files")));
            }
            catch { return false; }
        }

        private int _lastPct = -1;
        private string _xferLabel = "";
        private DateTime _xferStart;
        private void Report(long done, long total)
        {
            int pct = total > 0 ? (int)(done * 100 / total) : -1;
            if (pct >= 0 && pct == _lastPct) return;   // FRDP-FIXSWEEP B21 — unknown size (pct<0): keep updating the bytes readout
            _lastPct = pct;
            double secs = Math.Max(0.25, (DateTime.Now - _xferStart).TotalSeconds);
            string speed = HumanRate(done / secs);
            try
            {
                if (IsDisposed) return;
                BeginInvoke((Action)(() =>
                {
                    if (pct >= 0) { _progress.Value = Math.Max(0, Math.Min(100, pct)); _status.Text = _xferLabel + "   " + pct + "%   (" + Human(done) + " / " + Human(total) + ")   " + speed; SetBarStatus(_xferLabel + " · " + pct + "% · " + speed); }
                    else { _status.Text = _xferLabel + "   " + Human(done); SetBarStatus(_xferLabel + " · " + Human(done)); }
                }));
            }
            catch { }
        }

        private void RunTransfer(string label, Func<bool> proceed, Action transfer, Action onDone)
        {
            if (_transferring) return;
            _transferring = true; _xferLabel = label; _lastPct = -1; _xferStart = DateTime.Now;
            _progress.Value = 0; _progress.Visible = true; _status.Text = label + "   …";
            SetBarStatus(label + " · …");
            UpdateButtons();
            Task.Run(() =>
            {
                Exception err = null; bool ran = false;
                try { if (proceed == null || proceed()) { transfer(); ran = true; } } catch (Exception ex) { err = ex; }
                try
                {
                    if (!IsDisposed) BeginInvoke((Action)(() =>
                    {
                        _transferring = false; _progress.Visible = false;
                        if (err != null) { _status.Text = "Failed: " + err.Message; MessageBox.Show(FindForm() ?? (IWin32Window)this, "Transfer failed:\n\n" + err.Message, "Finestra — Files", MessageBoxButtons.OK, MessageBoxIcon.Warning); }   // B10
                        else if (ran) _status.Text = label + "   ✓ done";
                        else _status.Text = "";   // user declined the overwrite
                        onDone?.Invoke();
                        if (_remoteFs != null) SetBarStatus("● connected · " + _remote.CurrentPath);
                        UpdateButtons();
                    }));
                }
                catch { }
            });
        }

        private void SetBarStatus(string s) { _barStatus = s; StatusChanged?.Invoke(this); }

        private static string Human(long n)
        {
            if (n < 1024) return n + " B";
            double v = n; string[] u = { "KB", "MB", "GB", "TB" }; int i = -1;
            do { v /= 1024; i++; } while (v >= 1024 && i < u.Length - 1);
            return v.ToString(v < 10 ? "0.0" : "0") + " " + u[i];
        }
        private static string HumanRate(double bytesPerSec) => Human((long)bytesPerSec) + "/s";

        // ── IRemotePrompts — marshal each to its themed dialog over the host form ──
        // FRDP-FIXSWEEP B10 — marshal via THIS control (never stale, follows the control across a tear-off Reparent)
        // and resolve the CURRENT host form inside the Invoke, instead of a cached _owner that goes stale on tear-off.
        public HostKeyDecision VerifyHostKey(HostKeyPrompt p)
        {
            try { return (IsDisposed || !IsHandleCreated) ? HostKeyDecision.Reject : (HostKeyDecision)Invoke(new Func<HostKeyDecision>(() => HostKeyDialog.Ask(FindForm() ?? (IWin32Window)this, p))); }
            catch { return HostKeyDecision.Reject; }
        }
        public string GetPassphrase(string host, string keyPath)
        {
            try { return (IsDisposed || !IsHandleCreated) ? null : (string)Invoke(new Func<string>(() => PassphrasePrompt.Ask(FindForm() ?? (IWin32Window)this, host, keyPath))); }
            catch { return null; }
        }
        public CertDecision VerifyCert(CertPrompt p)
        {
            try { return (IsDisposed || !IsHandleCreated) ? CertDecision.Reject : (CertDecision)Invoke(new Func<CertDecision>(() => CertTrustDialog.Ask(FindForm() ?? (IWin32Window)this, p))); }
            catch { return CertDecision.Reject; }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_upload == null) return;
            int y = (46 - 32) / 2;
            _upload.SetBounds(10, y, _upload.Width, 32);
            _download.SetBounds(10 + _upload.Width + 8, y, _download.Width, 32);
            int left = 10 + _upload.Width + _download.Width + 24;
            _progress.SetBounds(_statusBar.Width - 260, (46 - 14) / 2, 250, 14);
            _status.SetBounds(left, 0, Math.Max(40, _statusBar.Width - 260 - left - 10), 46);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { ThemeHelper.ThemeChanged -= ApplyTheme; try { _remoteFs?.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
    }
}
