using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Finestra.Helpers;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Finestra.Core
{
    /// <summary>Why an SSH connection failed in a way we can explain in plain language (no raw exception dump).</summary>
    public enum SshAuthError { PuttyPpk, PassphraseRequired, BadPassphrase, UnreadableKey, KeyRejected, PasswordRejected, HostKeyRejected }

    /// <summary>A classified, user-presentable auth failure. <see cref="SshTerminalForm"/> maps <see cref="Error"/>
    /// to a friendly themed message; the raw cause is preserved for the log.</summary>
    public sealed class SshAuthException : Exception
    {
        public SshAuthError Error { get; }
        public SshAuthException(SshAuthError error, string message, Exception inner = null) : base(message, inner) { Error = error; }
    }

    /// <summary>
    /// FRDP-SSH-BUILD-1 / FRDP-SSH-AUTH — the SSH.NET 2025.1.0 side of a terminal session. Connects (password,
    /// keyboard-interactive, or private key with optional passphrase), verifies the server host key against the
    /// TOFU <see cref="KnownHosts"/> store (via <see cref="HostKeyVerifier"/>), opens a <c>xterm-256color</c>
    /// <see cref="ShellStream"/>, pumps received bytes off a background thread (<see cref="Received"/>), sends
    /// keystrokes (<see cref="Send"/>), and propagates the terminal size (<see cref="Resize"/>). No UI here — the
    /// host-key prompt and the passphrase prompt are delegated to callbacks the UI supplies.
    /// </summary>
    public sealed class SshSession : IDisposable
    {
        private readonly ConnectionProfile _cp;
        private SshClient _client;
        private ShellStream _shell;
        private Thread _pump;
        private volatile bool _stop;             // intentional close (Dispose) — never counts as a drop
        private int _closedRaised;               // FRDP-RECONNECT — debounce: the drop fires Closed exactly once

        // FRDP-SSH-PERF — Send/Resize used to write+flush the ShellStream synchronously on the CALLER's thread,
        // which for every keystroke is the UI thread: a slow/laggy link made typing itself stall. Both now enqueue
        // a closure here instead; one dedicated background thread drains it in order, so keystrokes/resizes stay
        // in the order they were issued but never block the caller.
        private readonly BlockingCollection<Action> _txQueue = new BlockingCollection<Action>();
        private Thread _writer;

        /// <summary>Bytes from the server — raised on the RX thread; marshal to the UI before touching the terminal.</summary>
        public event Action<byte[]> Received;
        /// <summary>The session ended (server closed / error) — raised once, on the RX thread.</summary>
        public event Action<string> Closed;

        /// <summary>Decides what to do with an unknown/changed host key. Called (blocking) on SSH.NET's connect
        /// thread; the UI implementation marshals to the UI thread and shows the themed dialog. Null ⇒ reject
        /// (secure default) — never silently trust.</summary>
        public Func<HostKeyPrompt, HostKeyDecision> HostKeyVerifier { get; set; }

        /// <summary>Supplies a passphrase for an encrypted key when none is stored. Returns null/empty ⇒ cancel.</summary>
        public Func<string> PassphraseProvider { get; set; }

        /// <summary>Set when the connection was aborted specifically because the user rejected the host key, so the
        /// failure can be explained precisely rather than as a generic handshake error.</summary>
        public bool HostKeyRejected { get; private set; }

        public bool IsConnected => _client != null && _client.IsConnected;
        public string ServerVersion => _client?.ConnectionInfo?.ServerVersion;

        public SshSession(ConnectionProfile cp) { _cp = cp; }

        public void Connect(int cols, int rows, int pxW, int pxH)
        {
            string user = string.IsNullOrWhiteSpace(_cp.Username) ? "root" : _cp.Username.Trim();
            string secret = _cp.GetPassword();      // password (password auth) OR key passphrase (key auth)
            int port = _cp.Port > 0 ? _cp.Port : 22;
            var ssh = _cp.Ssh ?? new SshSettings();

            var methods = new List<AuthenticationMethod>();
            if (ssh.AuthKind == SshAuthKind.PrivateKey && !string.IsNullOrWhiteSpace(ssh.PrivateKeyPath))
            {
                var keyFile = LoadKey(ssh.PrivateKeyPath, secret);   // handles .ppk detection + passphrase prompt/retry
                methods.Add(new PrivateKeyAuthenticationMethod(user, keyFile));
            }
            else
            {
                // password AND keyboard-interactive (many sshd offer only the latter for passwords) — same secret.
                methods.Add(new PasswordAuthenticationMethod(user, secret ?? ""));
                var ki = new KeyboardInteractiveAuthenticationMethod(user);
                ki.AuthenticationPrompt += (s, e) => { foreach (AuthenticationPrompt p in e.Prompts) p.Response = secret ?? ""; };
                methods.Add(ki);
            }

            var ci = new ConnectionInfo(_cp.Host, port, user, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(20) };
            _client = new SshClient(ci);
            _client.KeepAliveInterval = TimeSpan.FromSeconds(30);   // FRDP-FIXSWEEP C1 — detect idle drops (else the tab lies "connected" until OS TCP timeout)
            _client.HostKeyReceived += OnHostKeyReceived;   // TOFU verification (replaces accept-any)
            _client.ErrorOccurred += (s, e) => RaiseClosed("connection error");   // FRDP-RECONNECT — belt-and-suspenders drop signal

            try { _client.Connect(); }
            catch (SshConnectionException) when (HostKeyRejected)
            {
                throw new SshAuthException(SshAuthError.HostKeyRejected,
                    "Host key verification failed — the connection was not trusted.");
            }
            catch (SshAuthenticationException ex)
            {
                // wrong key/password/passphrase-at-server — a friendly, non-raw message. FRDP-FIXSWEEP B14 — key vs
                // password rejection are now DISTINCT (were both KeyRejected), so the message can be specific.
                var err = ssh.AuthKind == SshAuthKind.PrivateKey ? SshAuthError.KeyRejected : SshAuthError.PasswordRejected;
                throw new SshAuthException(err, ex.Message, ex);
            }

            FileLog.Line("[SSH] connected " + user + "@" + _cp.Host + ":" + port
                + " auth=" + (ssh.AuthKind == SshAuthKind.PrivateKey ? "key" : "password")
                + " kex=" + _client.ConnectionInfo.CurrentKeyExchangeAlgorithm
                + " cipher=" + _client.ConnectionInfo.CurrentClientEncryption
                + " hostkey=" + _client.ConnectionInfo.CurrentHostKeyAlgorithm);

            _shell = _client.CreateShellStream("xterm-256color", (uint)Math.Max(1, cols), (uint)Math.Max(1, rows),
                                               (uint)Math.Max(1, pxW), (uint)Math.Max(1, pxH), 32 * 1024);
            _pump = new Thread(Pump) { IsBackground = true, Name = "SshRx" };
            _pump.Start();
            _writer = new Thread(WriteLoop) { IsBackground = true, Name = "SshTx" };
            _writer.Start();
        }

        /// <summary>FRDP-SSH-PERF — drains <see cref="_txQueue"/> in order, one item at a time, off the UI thread.
        /// Each queued closure carries its own try/catch (see <see cref="Send"/>/<see cref="Resize"/>) so a failed
        /// write and a failed resize keep their existing, distinct error handling — this loop only sequences them.</summary>
        private void WriteLoop()
        {
            try { foreach (var action in _txQueue.GetConsumingEnumerable()) { if (_stop) break; action(); } }
            catch (ObjectDisposedException) { }     // CompleteAdding raced a final Add — harmless, session's closing
            catch { }
        }

        // ── TOFU host-key verification ──────────────────────────────────────────
        private void OnHostKeyReceived(object sender, HostKeyEventArgs e)
        {
            // FRDP-FIXSWEEP B12 — DEFAULT-DENY. SSH.NET's HostKeyEventArgs.CanTrust defaults to TRUE, so any throw
            // before an explicit decision below would silently trust an unverified key. Deny first; only a fully
            // successful verify path re-enables trust; any exception ⇒ stays denied (fail closed) + surfaced.
            e.CanTrust = false;
            try
            {
                int port = _cp.Port > 0 ? _cp.Port : 22;
                string type = e.HostKeyName;
                string fp = KnownHosts.Fingerprint(e.HostKey);      // OpenSSH-style, version-independent (computed here)
                var kh = KnownHosts.Instance;

                KnownHostEntry existing;
                HostKeyStatus status = kh.Check(_cp.Host, port, type, fp, out existing);

                if (status == HostKeyStatus.Match)
                {
                    e.CanTrust = true;
                    FileLog.Line("[SSH] host key OK (known) " + _cp.Host + ":" + port + " " + type + " " + fp);
                    return;
                }

                var prompt = new HostKeyPrompt
                {
                    Host = _cp.Host, Port = port, KeyType = type, Sha256 = fp,
                    Md5 = SafeMd5(e), KeyBits = e.KeyLength, Status = status,
                    OldKeyType = existing?.KeyType, OldSha256 = existing?.Sha256
                };

                HostKeyDecision decision = HostKeyVerifier != null ? HostKeyVerifier(prompt) : HostKeyDecision.Reject;

                if (decision == HostKeyDecision.Reject)
                {
                    HostKeyRejected = true;
                    FileLog.Line("[SSH] host key REJECTED (" + status + ") " + _cp.Host + ":" + port + " " + type + " " + fp);
                    return;   // e.CanTrust stays false
                }

                e.CanTrust = true;
                if (decision == HostKeyDecision.AcceptAndStore)
                {
                    kh.Store(_cp.Host, port, type, fp);
                    FileLog.Line("[SSH] host key STORED (" + status + ") " + _cp.Host + ":" + port + " " + type + " " + fp);
                }
                else
                    FileLog.Line("[SSH] host key accepted once (" + status + ") " + _cp.Host + ":" + port + " " + type + " " + fp);
            }
            catch (Exception ex)
            {
                e.CanTrust = false;   // fail closed
                HostKeyRejected = true;
                FileLog.Line("[SSH] host-key verification errored → connection refused: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string SafeMd5(HostKeyEventArgs e)
        {
            try { return e.FingerPrintMD5; } catch { return null; }
        }

        // ── private-key loading (format detect + passphrase prompt/retry) ───────
        private PrivateKeyFile LoadKey(string path, string storedPassphrase)
        {
            if (!File.Exists(path))
                throw new SshAuthException(SshAuthError.UnreadableKey, "The private key file was not found:\n" + path);

            if (IsPuttyPpk(path))
                throw new SshAuthException(SshAuthError.PuttyPpk,
                    "This looks like a PuTTY .ppk key, which this client can't read directly.\n\n" +
                    "Convert it to OpenSSH format with PuTTYgen (Conversions → Export OpenSSH key) and select that file instead.");

            bool hadStored = !string.IsNullOrEmpty(storedPassphrase);
            try
            {
                return hadStored ? new PrivateKeyFile(path, storedPassphrase) : new PrivateKeyFile(path);
            }
            catch (SshPassPhraseNullOrEmptyException)
            {
                // encrypted key, and no (usable) stored passphrase → prompt once
                string entered = PassphraseProvider?.Invoke();
                if (string.IsNullOrEmpty(entered))
                    throw new SshAuthException(SshAuthError.PassphraseRequired, "This key is passphrase-protected and no passphrase was provided.");
                try { return new PrivateKeyFile(path, entered); }
                catch (Exception ex) { throw new SshAuthException(SshAuthError.BadPassphrase, "The passphrase did not unlock the key.", ex); }
            }
            catch (SshAuthException) { throw; }
            catch (Exception ex)
            {
                // a stored passphrase that's wrong throws here (not SshPassPhraseNullOrEmpty); so does an unsupported/corrupt file
                if (hadStored)
                    throw new SshAuthException(SshAuthError.BadPassphrase, "The saved passphrase did not unlock the key (or the key is unreadable).", ex);
                throw new SshAuthException(SshAuthError.UnreadableKey, "The private key could not be read (unsupported or corrupt format).", ex);
            }
        }

        /// <summary>PuTTY .ppk files begin with the literal header "PuTTY-User-Key-File-" — a definitive detect that
        /// doesn't depend on the file extension.</summary>
        private static bool IsPuttyPpk(string path)
        {
            try
            {
                using (var r = new StreamReader(path, Encoding.ASCII, false))
                {
                    char[] head = new char[20];
                    int n = r.Read(head, 0, head.Length);
                    return n >= 20 && new string(head, 0, n).StartsWith("PuTTY-User-Key-File-", StringComparison.Ordinal);
                }
            }
            catch { return false; }
        }

        private void Pump()
        {
            var buf = new byte[32 * 1024];
            string reason = "connection closed";
            try
            {
                while (!_stop)
                {
                    int n = _shell.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    var slice = new byte[n];
                    Array.Copy(buf, slice, n);
                    try { Received?.Invoke(slice); } catch { }
                }
            }
            catch (Exception ex) { reason = ex.Message; if (!_stop) FileLog.Line("[SSH] rx ended: " + ex.Message); }
            RaiseClosed(reason);
        }

        /// <summary>FRDP-RECONNECT — raise the drop signal exactly once (never for an intentional close). Fired from
        /// the pump ending, ErrorOccurred, and a failed Send — all debounced into one Connected→Disconnected event.</summary>
        private void RaiseClosed(string reason)
        {
            if (_stop) return;
            if (Interlocked.Exchange(ref _closedRaised, 1) != 0) return;
            FileLog.Line("[SSH] dropped: " + reason);
            try { Closed?.Invoke(reason); } catch { }
        }

        /// <summary>Queues the bytes for the background writer — returns immediately, never blocks the caller
        /// (the UI thread, for every keystroke). Order is preserved: this is FIFO against every other queued
        /// Send/Resize.</summary>
        public void Send(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            try
            {
                _txQueue.Add(() =>
                {
                    // FRDP-RECONNECT — a failed write means the session is down; trigger the (debounced) drop ONCE
                    // instead of swallowing it per keystroke. No keystroke bytes in the log.
                    try { if (_shell != null) { _shell.Write(data, 0, data.Length); _shell.Flush(); } }
                    catch { RaiseClosed("write failed"); }
                });
            }
            catch (InvalidOperationException) { }   // CompleteAdding already ran (session closing) — nothing to do
        }

        /// <summary>Queued alongside Send() (same FIFO) so a resize can never overtake keystrokes issued before it.</summary>
        public void Resize(int cols, int rows, int pxW, int pxH)
        {
            try
            {
                _txQueue.Add(() =>
                {
                    try
                    {
                        if (_shell == null) return;
                        _shell.ChangeWindowSize((uint)Math.Max(1, cols), (uint)Math.Max(1, rows), (uint)Math.Max(1, pxW), (uint)Math.Max(1, pxH));
                        FileLog.Line("[SSH] resize " + cols + "x" + rows + " (remote pty)");   // grid reflow → server window-change (only fires on an actual grid change)
                    }
                    catch (Exception ex) { FileLog.Line("[SSH] resize failed: " + ex.Message); }
                });
            }
            catch (InvalidOperationException) { }
        }

        public void Dispose()
        {
            _stop = true;
            try { _txQueue.CompleteAdding(); } catch { }   // lets WriteLoop drain and exit instead of leaking the thread
            try { _shell?.Dispose(); } catch { }
            try { if (_client != null) { if (_client.IsConnected) _client.Disconnect(); _client.Dispose(); } } catch { }
            _shell = null; _client = null;
        }
    }
}
