using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
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
        private readonly RoundedButton _upload, _download, _cancelBtn, _pauseBtn;
        private readonly ThemedProgressBar _progress;
        private readonly Label _status;
        private IRemoteFileSystem _remoteFs;
        private bool _transferring, _splitInit;
        private volatile bool _dropped;        // FRDP-RECONNECT — was live, now dropped
        private volatile bool _reconnecting;   // FRDP-RECONNECT — an attempt is running
        private string _lastPath = "/";        // remote path to restore on reconnect
        private string _barStatus = "connecting…";

        // FRDP-FTP-RICH — the Copy/Cut/Paste clipboard. SourceFs identifies which PANE it came from (there are only
        // ever two fixed panes, so "same filesystem" IS "same pane instance" — no separate identity check needed).
        private sealed class ClipboardState { public IRemoteFileSystem SourceFs; public List<RemoteEntry> Items; public bool IsCut; }
        private ClipboardState _clipboard;

        // Cancel/Pause state for the CURRENTLY RUNNING single-leg transfer (RunBatchTransfer). The fallback
        // download→re-upload copy (RunFallbackCopy, FTP/FTPS same-pane Copy) is Cancel-only by design — see its
        // own comment for why pause isn't worth the added complexity there.
        private CancellationTokenSource _xferCts;
        private bool _pauseRequested;             // Pause was clicked — the next OperationCanceledException means "pause", not "cancel"
        private bool _paused;
        private List<RemoteEntry> _pausedItems;   // remaining batch (item 0 = the one to resume), null when nothing is paused
        private bool _pausedToRemote, _pausedMoveAfter;
        private long _resumeOffset;
        private long _lastReportedDone;           // snapshot from Report() — becomes _resumeOffset on pause

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

            // FRDP-POLISH-4 — BackColor must be set EXPLICITLY, not just painted via the Paint handler below: the
            // child RoundedButtons blend their own rounded corners to Parent.BackColor (see RoundedButton.OnPaint),
            // and an unset BackColor ambiently inherits the OUTER panel's darker color instead of this strip's own
            // BarBg() — the exact "black band behind the buttons" bug already root-caused once this session in
            // ConnectionRow. Same fix, same family.
            _statusBar = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = BarBg() };
            _statusBar.Paint += (s, e) => { using (var b = new SolidBrush(BarBg())) e.Graphics.FillRectangle(b, _statusBar.ClientRectangle); };
            _upload = new RoundedButton { Text = "Upload →", Kind = RoundedButtonKind.Primary, Width = 116, Height = 32, Enabled = false, Font = FontHelper.Ui(9.5f, FontStyle.Bold) };
            _download = new RoundedButton { Text = "← Download", Kind = RoundedButtonKind.Primary, Width = 124, Height = 32, Enabled = false, Font = FontHelper.Ui(9.5f, FontStyle.Bold) };
            _upload.Click += (s, e) => DoUpload();
            _download.Click += (s, e) => DoDownload();
            // FRDP-FTP-RICH — Cancel/Pause, visible only while a transfer is running (or paused). Pause toggles
            // its own glyph (❙❙ running / ▶ paused), matching the existing RDP pause-button convention elsewhere
            // in the app rather than inventing a new visual language for "toggle" buttons.
            _cancelBtn = new RoundedButton { Text = "✕", Kind = RoundedButtonKind.Danger, Width = 32, Height = 32, Visible = false, Font = FontHelper.Ui(11f, FontStyle.Bold) };
            _pauseBtn = new RoundedButton { Text = "❙❙", Kind = RoundedButtonKind.Neutral, Width = 32, Height = 32, Visible = false, Font = FontHelper.Ui(11f, FontStyle.Bold) };
            _cancelBtn.Click += (s, e) => CancelClicked();
            _pauseBtn.Click += (s, e) => PauseOrResumeClicked();
            _progress = new ThemedProgressBar { Height = 14, Visible = false };
            _status = new Label { AutoSize = false, BackColor = Color.Transparent, Font = FontHelper.Ui(9f), TextAlign = ContentAlignment.MiddleLeft, Text = "" };
            _statusBar.Controls.Add(_upload); _statusBar.Controls.Add(_download); _statusBar.Controls.Add(_progress); _statusBar.Controls.Add(_status);
            _statusBar.Controls.Add(_cancelBtn); _statusBar.Controls.Add(_pauseBtn);

            _local = new FileBrowserPane { Dock = DockStyle.Fill }; _local.SetTitle("This PC");
            _remote = new FileBrowserPane { Dock = DockStyle.Fill }; _remote.SetTitle(cp.Host);
            _local.SelectionChanged += UpdateButtons;
            _remote.SelectionChanged += UpdateButtons;
            // FRDP-FTP-RICH — Copy/Cut/Paste replace POLISH-4's ad-hoc cross-pane Upload/Download/Move items (the
            // brief's explicit ask — the old items are gone, not kept alongside). Same extension-point pattern:
            // the OWNER appends what needs clipboard state / the sibling pane, right before the menu shows.
            _local.ExtraContextItems += menu => AddClipboardItems(menu, _local);
            _remote.ExtraContextItems += menu => AddClipboardItems(menu, _remote);
            _local.CopyRequested += () => SetClipboard(_local.Fs, _local.SelectedEntries, false);
            _local.CutRequested += () => SetClipboard(_local.Fs, _local.SelectedEntries, true);
            _local.PasteRequested += () => DoPaste(_local, _local.Fs);
            _remote.CopyRequested += () => { if (_remoteFs != null) SetClipboard(_remoteFs, _remote.SelectedEntries, false); };
            _remote.CutRequested += () => { if (_remoteFs != null) SetClipboard(_remoteFs, _remote.SelectedEntries, true); };
            _remote.PasteRequested += () => { if (_remoteFs != null) DoPaste(_remote, _remoteFs); };
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
            if (_statusBar != null) { _statusBar.BackColor = BarBg(); _statusBar.Invalidate(); }
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
            RunBatchTransfer(new List<RemoteEntry> { sel.Value }, toRemote: true, moveAfter: false);
        }

        private void DoDownload()
        {
            var sel = _remote.Selected; if (sel == null || sel.Value.IsDirectory || _remoteFs == null) return;
            // FRDP-FIXSWEEP B5 — at the drive-list root the local path is "" → Combine gives a RELATIVE name that lands
            // in the exe's CWD. Block it with a themed prompt rather than a hidden default destination.
            if (string.IsNullOrEmpty(_local.CurrentPath))
            {
                ConfirmDialog.Info(FindForm() ?? (IWin32Window)this, "Open a drive or folder in the left pane first, so there's a destination to download into.", "Finestra — Files");
                return;
            }
            RunBatchTransfer(new List<RemoteEntry> { sel.Value }, toRemote: false, moveAfter: false);
        }

        // ── FRDP-FTP-RICH — the Copy/Cut/Paste clipboard ──────────────────────────
        private void SetClipboard(IRemoteFileSystem fs, IReadOnlyList<RemoteEntry> selected, bool isCut)
        {
            if (fs == null) return;
            var files = new List<RemoteEntry>();
            foreach (var it in selected) if (!it.IsDirectory) files.Add(it);   // dirs unsupported this batch, matches the existing Delete/transfer limit
            if (files.Count == 0) return;
            _clipboard = new ClipboardState { SourceFs = fs, Items = files, IsCut = isCut };
        }

        /// <summary>Builds Copy/Cut (if this pane has a selection) and Paste (if the clipboard holds anything) —
        /// replaces POLISH-4's ad-hoc cross-pane Upload/Download/Move items entirely, per this batch's brief.
        /// Paste's label states plainly what will happen: a same-filesystem paste is a SERVER-SIDE op (instant) if
        /// the backend supports it, a labeled slow fallback if not (FTP/FTPS same-pane Copy — no silent surprise),
        /// or a normal transfer when crossing panes.</summary>
        private void AddClipboardItems(ThemedContextMenuStrip menu, FileBrowserPane pane)
        {
            if (_dropped || _reconnecting) return;
            var fs = pane == _local ? _local.Fs : _remoteFs;
            if (fs == null) return;

            var selected = new List<RemoteEntry>();
            foreach (var it in pane.SelectedEntries) if (!it.IsDirectory) selected.Add(it);
            if (selected.Count > 0)
            {
                string suffix = selected.Count == 1 ? "" : " (" + selected.Count + ")";
                menu.Items.Add(new ToolStripMenuItem("Copy" + suffix, null, (s, e) => SetClipboard(fs, selected, false)) { ShortcutKeyDisplayString = "Ctrl+C" });
                menu.Items.Add(new ToolStripMenuItem("Cut" + suffix, null, (s, e) => SetClipboard(fs, selected, true)) { ShortcutKeyDisplayString = "Ctrl+X" });
            }

            if (_clipboard != null)
            {
                bool sameFs = _clipboard.SourceFs == fs;
                string label = "Paste" + (_clipboard.Items.Count > 1 ? " (" + _clipboard.Items.Count + ")" : "");
                if (sameFs && !_clipboard.IsCut && !fs.CanServerSideCopy) label += "  (slow — via this PC)";   // Part 3's explicit "no silent surprise" ask
                var item = new ToolStripMenuItem(label, null, (s, e) => DoPaste(pane, fs)) { ShortcutKeyDisplayString = "Ctrl+V" };
                menu.Items.Add(item);
            }
        }

        private void DoPaste(FileBrowserPane destPane, IRemoteFileSystem destFs)
        {
            var clip = _clipboard;
            if (clip == null || _transferring || destFs == null) return;
            bool sameFs = clip.SourceFs == destFs;   // there are only ever 2 fixed panes, so same-filesystem == same-pane-instance
            if (sameFs)
            {
                if (clip.IsCut) RunServerSideMove(destFs, destPane, clip.Items);
                else if (destFs.CanServerSideCopy) RunServerSideCopy(destFs, destPane, clip.Items);
                else RunFallbackCopy(destPane, clip);
            }
            else
            {
                bool toRemote = destPane == _remote;
                RunBatchTransfer(clip.Items, toRemote, moveAfter: clip.IsCut);
            }
            if (clip.IsCut) _clipboard = null;   // a Cut is consumed by one paste; a Copy can be pasted repeatedly
        }

        /// <summary>FRDP-FTP-RICH Part 2 — server-side MOVE is just a Rename to a new parent. Native on SFTP, FTP,
        /// FTPS, and Local alike — no protocol distinction needed here at all. Multi-select aware: one confirm for
        /// the batch, stops on the first failure (matches Delete's existing semantics from POLISH-4).</summary>
        private void RunServerSideMove(IRemoteFileSystem fs, FileBrowserPane destPane, List<RemoteEntry> items)
        {
            string what = items.Count == 1 ? "'" + items[0].Name + "'" : items.Count + " items";
            if (!ConfirmDialog.Ask(FindForm() ?? (IWin32Window)this, "Move " + what + " here?", "Finestra — Files")) return;
            string destDir = destPane.CurrentPath;
            Task.Run(() =>
            {
                int done = 0; var failures = new List<string>();
                foreach (var it in items)
                {
                    try { fs.Rename(it.FullPath, fs.Combine(destDir, it.Name)); done++; }
                    catch (Exception ex) { failures.Add(it.Name + ": " + ex.Message); break; }
                }
                Post(() =>
                {
                    if (failures.Count > 0) MessageBox.Show(FindForm() ?? (IWin32Window)this, "Move failed:\n\n" + string.Join("\n", failures.ToArray()), "Finestra — Files", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    destPane.Reload();
                });
            });
        }

        /// <summary>FRDP-FTP-RICH Part 3 — server-side COPY via the backend's exec escape hatch (SFTP only —
        /// CanServerSideCopy is already confirmed true by the caller). No bytes cross the network.</summary>
        private void RunServerSideCopy(IRemoteFileSystem fs, FileBrowserPane destPane, List<RemoteEntry> items)
        {
            string destDir = destPane.CurrentPath;
            Task.Run(() =>
            {
                int done = 0; var failures = new List<string>();
                foreach (var it in items)
                {
                    try { fs.CopyServerSide(it.FullPath, fs.Combine(destDir, it.Name)); done++; }
                    catch (Exception ex) { failures.Add(it.Name + ": " + ex.Message); break; }
                }
                Post(() =>
                {
                    if (failures.Count > 0) MessageBox.Show(FindForm() ?? (IWin32Window)this, "Copy failed:\n\n" + string.Join("\n", failures.ToArray()), "Finestra — Files", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    destPane.Reload();
                });
            });
        }

        /// <summary>FRDP-FTP-RICH Part 3 — FTP/FTPS have NO server-side copy verb at all (unlike SFTP's exec
        /// escape hatch): the only way to duplicate a file on the SAME server is download it to this PC then
        /// upload it back. Labeled in the menu ("slow — via this PC") before the user ever clicks it — no silent
        /// surprise. Cancel-only by design (no Pause) — pausing a TWO-LEG composite op would need to track which
        /// leg + its own offset on top of the per-item batch state RunBatchTransfer already carries, which is
        /// real added complexity for what's already the slow, less-common path; the brief's own escape valve
        /// ("if it balloons, ship Cancel and defer Pause") applies narrowly here without affecting normal
        /// single-leg transfers, which DO get full pause/resume.</summary>
        private void RunFallbackCopy(FileBrowserPane destPane, ClipboardState clip)
        {
            string what = clip.Items.Count == 1 ? "'" + clip.Items[0].Name + "'" : clip.Items.Count + " items";
            string msg = "Copy " + what + " on the server?\n\nThis protocol has no server-side copy, so each file is downloaded to this PC and re-uploaded — it may be slow for large files.";
            if (!ConfirmDialog.Ask(FindForm() ?? (IWin32Window)this, msg, "Finestra — Files")) return;

            string destDir = destPane.CurrentPath;
            var fs = clip.SourceFs;
            _transferring = true; _xferStart = DateTime.Now; _lastPct = -1;
            _xferCts = new CancellationTokenSource(); _pauseRequested = false;
            _progress.Value = 0; _progress.Visible = true;
            _cancelBtn.Visible = true; _pauseBtn.Visible = false;   // no pause for this composite op
            UpdateButtons();
            var token = _xferCts.Token;
            Task.Run(() =>
            {
                int done = 0; var failures = new List<string>(); bool cancelled = false;
                foreach (var it in clip.Items)
                {
                    string stage = Path.Combine(Path.GetTempPath(), "frdp-copy-" + Guid.NewGuid().ToString("N") + "-" + it.Name);
                    string remoteDest = fs.Combine(destDir, it.Name);
                    try
                    {
                        _xferLabel = "⇅ " + it.Name; _lastPct = -1;
                        fs.Download(it.FullPath, stage, Report, token, 0);
                        fs.Upload(stage, remoteDest, Report, token, 0);
                        done++;
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancel-only path (no pause here) — clean up BOTH legs' own internal ".frdp-part" temps,
                        // whichever leg was actually in flight when the token fired.
                        cancelled = true;
                        try { File.Delete(stage + ".frdp-part"); } catch { }
                        try { if (fs.Exists(remoteDest + ".frdp-part")) fs.Delete(remoteDest + ".frdp-part", false); } catch { }
                    }
                    catch (Exception ex) { failures.Add(it.Name + ": " + ex.Message); }
                    finally { try { File.Delete(stage); } catch { } }   // the fully-downloaded staging copy, if the download leg finished
                    if (cancelled) break;
                }
                Post(() =>
                {
                    _transferring = false; _xferCts = null; _progress.Visible = false; _cancelBtn.Visible = false;
                    _status.Text = done + " of " + clip.Items.Count + " copied" + (failures.Count > 0 ? ", " + failures.Count + " failed" : "");
                    if (failures.Count > 0) MessageBox.Show(FindForm() ?? (IWin32Window)this, "Some items failed:\n\n" + string.Join("\n", failures.ToArray()), "Finestra — Files", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    destPane.Reload();
                    if (_remoteFs != null) SetBarStatus("● connected · " + _remote.CurrentPath);
                    UpdateButtons();
                });
            });
        }

        /// <summary>FRDP-FTP-RICH Part 4/5 — the single-leg transfer runner: cross-pane Copy/Move (from Paste) AND
        /// the toolbar Upload/Download buttons all funnel through here now (a single-item list is just N=1) —
        /// ONE place gets Cancel/Pause/Resume instead of two. A resumed call passes startOffset > 0, continuing
        /// items[0] from that byte and the rest of the batch normally afterward.</summary>
        private void RunBatchTransfer(List<RemoteEntry> items, bool toRemote, bool moveAfter, long startOffset = 0)
        {
            if ((_transferring && !_paused) || items.Count == 0 || _remoteFs == null) return;
            if (moveAfter && startOffset == 0)   // don't re-ask on a resume continuation
            {
                string what = items.Count == 1 ? "'" + items[0].Name + "'" : items.Count + " items";
                string msg = "Move " + what + (toRemote ? " to " + _cp.Host : " to This PC") + "?\n\nThe source is deleted after each file transfers successfully.";
                if (!ConfirmDialog.Ask(FindForm() ?? (IWin32Window)this, msg, "Finestra — Files")) return;
            }
            _transferring = true; _paused = false; _xferStart = DateTime.Now; _lastPct = -1;
            _xferCts = new CancellationTokenSource(); _pauseRequested = false;
            _progress.Value = 0; _progress.Visible = true;
            _cancelBtn.Visible = true; _pauseBtn.Visible = true; _pauseBtn.Text = "❙❙";
            UpdateButtons();
            var token = _xferCts.Token;
            Task.Run(() =>
            {
                int done = 0;
                var failures = new List<string>();
                bool pausedMidBatch = false;
                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    string localPath = toRemote ? it.FullPath : _local.Fs.Combine(_local.CurrentPath, it.Name);
                    string remotePath = toRemote ? _remoteFs.Combine(_remote.CurrentPath, it.Name) : it.FullPath;
                    _xferLabel = (toRemote ? "↑ " : "↓ ") + it.Name + "  (" + (i + 1) + "/" + items.Count + ")";
                    _lastPct = -1;
                    long thisOffset = i == 0 ? startOffset : 0;
                    _lastReportedDone = thisOffset;
                    try
                    {
                        if (thisOffset == 0)   // the overwrite question already got a Yes before the FIRST attempt
                        {
                            bool proceed = toRemote ? ConfirmOverwrite(_remoteFs, remotePath, it.Name) : ConfirmOverwrite(_local.Fs, localPath, it.Name);
                            if (!proceed) continue;
                        }
                        if (toRemote) _remoteFs.Upload(localPath, remotePath, Report, token, thisOffset);
                        else _remoteFs.Download(remotePath, localPath, Report, token, thisOffset);
                        if (moveAfter)
                        {
                            try { if (toRemote) _local.Fs.Delete(localPath, false); else _remoteFs.Delete(remotePath, false); }
                            catch (Exception ex) { failures.Add(it.Name + " (transferred, but couldn't remove the source: " + ex.Message + ")"); }
                        }
                        done++;
                    }
                    catch (OperationCanceledException)
                    {
                        if (_pauseRequested)
                        {
                            pausedMidBatch = true;
                            _pausedItems = items.GetRange(i, items.Count - i);
                            _pausedToRemote = toRemote; _pausedMoveAfter = moveAfter;
                            _resumeOffset = _lastReportedDone;
                        }
                        else
                        {
                            // a real cancel — Download/Upload deliberately left their own ".frdp-part" temp in
                            // place (so a PAUSE could reuse it); with no resume coming, clean it up ourselves.
                            try
                            {
                                if (toRemote) { string tmp = remotePath + ".frdp-part"; if (_remoteFs.Exists(tmp)) _remoteFs.Delete(tmp, false); }
                                else { string tmp = localPath + ".frdp-part"; if (_local.Fs.Exists(tmp)) _local.Fs.Delete(tmp, false); }
                            }
                            catch { }
                        }
                        break;   // either paused (state remembered above) or a real cancel (temp cleaned up above)
                    }
                    catch (Exception ex) { failures.Add(it.Name + ": " + ex.Message); }
                }
                Post(() =>
                {
                    _xferCts = null;
                    if (pausedMidBatch)
                    {
                        _transferring = false; _paused = true;
                        _status.Text = _xferLabel + "   ⏸ paused";
                        SetBarStatus(_xferLabel + " · paused");
                        _cancelBtn.Visible = true; _pauseBtn.Text = "▶"; _pauseBtn.Visible = true;
                    }
                    else
                    {
                        _transferring = false; _paused = false; _pausedItems = null;
                        _progress.Visible = false; _cancelBtn.Visible = false; _pauseBtn.Visible = false;
                        _status.Text = done + " of " + items.Count + " done" + (failures.Count > 0 ? ", " + failures.Count + " failed" : "");
                        if (failures.Count > 0)
                            MessageBox.Show(FindForm() ?? (IWin32Window)this, "Some items failed:\n\n" + string.Join("\n", failures.ToArray()), "Finestra — Files", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        _local.Reload(); _remote.Reload();
                        if (_remoteFs != null) SetBarStatus("● connected · " + _remote.CurrentPath);
                    }
                    UpdateButtons();
                });
            });
        }

        /// <summary>FRDP-FTP-RICH Part 4 — Cancel while a transfer is running. _pauseRequested stays false, so the
        /// batch loop's catch treats the resulting OperationCanceledException as a real cancel: pausedMidBatch
        /// stays false, _pausedItems is never set, and the temp file — still on disk, since Download/Upload leaves
        /// it in place on ANY OperationCanceledException — is simply abandoned rather than resumed from. Cancelling
        /// something ALREADY paused is a different path (CancelPaused, below) since there's no running token left
        /// to cancel at that point — it deletes the temp explicitly instead.</summary>
        private void CancelClicked()
        {
            if (_paused) { CancelPaused(); return; }
            if (!_transferring) return;
            _pauseRequested = false;
            try { _xferCts?.Cancel(); } catch { }
        }

        /// <summary>Discards a paused transfer's partial temp file and resets state — this is the "throw away the
        /// resume point" path, distinct from just cancelling a RUNNING transfer.</summary>
        private void CancelPaused()
        {
            if (_pausedItems == null || _pausedItems.Count == 0) { _paused = false; UpdateButtons(); return; }
            var it = _pausedItems[0];
            try
            {
                if (_pausedToRemote)
                {
                    string tmp = _remoteFs.Combine(_remote.CurrentPath, it.Name) + ".frdp-part";
                    try { if (_remoteFs.Exists(tmp)) _remoteFs.Delete(tmp, false); } catch { }
                }
                else
                {
                    string tmp = _local.Fs.Combine(_local.CurrentPath, it.Name) + ".frdp-part";
                    try { if (_local.Fs.Exists(tmp)) _local.Fs.Delete(tmp, false); } catch { }
                }
            }
            catch { }
            _paused = false; _pausedItems = null; _resumeOffset = 0;
            _progress.Visible = false; _cancelBtn.Visible = false; _pauseBtn.Visible = false;
            _status.Text = ""; if (_remoteFs != null) SetBarStatus("● connected · " + _remote.CurrentPath);
            UpdateButtons();
        }

        /// <summary>FRDP-FTP-RICH Part 5 — Pause signals the SAME cancellation token Cancel would, but flags
        /// _pauseRequested first so the batch loop's catch knows to remember the resume point instead of
        /// discarding it. Resume re-invokes RunBatchTransfer with the remembered offset.</summary>
        private void PauseOrResumeClicked()
        {
            if (_paused) { ResumePaused(); return; }
            if (!_transferring) return;
            _pauseRequested = true;
            _pauseBtn.Enabled = false;   // avoid a double-click racing the single in-flight cancellation
            try { _xferCts?.Cancel(); } catch { }
        }

        private void ResumePaused()
        {
            if (_pausedItems == null || _pausedItems.Count == 0) { _paused = false; UpdateButtons(); return; }
            var items = _pausedItems; bool toRemote = _pausedToRemote; bool moveAfter = _pausedMoveAfter; long offset = _resumeOffset;
            _pausedItems = null; _paused = false; _pauseBtn.Enabled = true;
            RunBatchTransfer(items, toRemote, moveAfter, offset);
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
            _lastReportedDone = done;
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
            // FRDP-FTP-RICH — Cancel/Pause sit just left of the progress bar, only visible during a transfer.
            _cancelBtn.SetBounds(_statusBar.Width - 260 - 32 - 6 - 32 - 6, y, 32, 32);
            _pauseBtn.SetBounds(_statusBar.Width - 260 - 32 - 6, y, 32, 32);
            _progress.SetBounds(_statusBar.Width - 260, (46 - 14) / 2, 250, 14);
            _status.SetBounds(left, 0, Math.Max(40, _statusBar.Width - 260 - 32 - 6 - 32 - 6 - left - 10), 46);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { ThemeHelper.ThemeChanged -= ApplyTheme; try { _remoteFs?.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
    }
}
