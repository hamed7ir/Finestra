using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using Finestra.Helpers;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Finestra.Core
{
    /// <summary>
    /// FRDP-FTP-BUILD-1 — the SFTP backend: a thin <see cref="IRemoteFileSystem"/> adapter over SSH.NET
    /// <see cref="SftpClient"/>. Auth + host-key TOFU come from <see cref="SshAuth"/> (the existing SSH path,
    /// reused — same <see cref="KnownHosts"/> store, same fingerprint), so an SFTP connection needs zero new
    /// credentials machinery. No new dependency (SftpClient is in the already-shipped Renci.SshNet.dll).
    /// </summary>
    public sealed class SftpBackend : IRemoteFileSystem
    {
        private readonly ConnectionProfile _cp;
        private readonly SshAuth _auth;
        private SftpClient _client;

        // FRDP-FTP-RICH — a SEPARATE SshClient purely for exec ('cp' server-side copy). SftpClient's own connection
        // is an SFTP-channel-only session; SSH.NET's object model has no "open another channel type on an existing
        // SftpClient" API, so this is a second TCP+SSH connection using the SAME ConnectionInfo (freshly rebuilt —
        // SshAuth.BuildConnectionInfo() reads the ALREADY-decrypted stored secret, no re-prompt) and the SAME
        // host-key store (AttachHostKey auto-matches silently since this host:port is already trusted from the
        // first connect — confirmed via SshAuth.OnHostKeyReceived's HostKeyStatus.Match path, no second TOFU
        // prompt). Lazily created on first CanServerSideCopy/CopyServerSide use, not at Connect() — most sessions
        // never touch Copy, and eagerly paying for a second handshake on every SFTP connect isn't worth it.
        private SshClient _execClient;
        private bool? _execCapable;

        public SftpBackend(ConnectionProfile cp, IRemotePrompts prompts) { _cp = cp; _auth = new SshAuth(cp, prompts); }

        public string Protocol => "SFTP";
        public bool CanRename => true;
        public string HomeDirectory => _client != null ? _client.WorkingDirectory : "/";
        public event Action Dropped;   // FRDP-RECONNECT — raised on SftpClient.ErrorOccurred

        /// <summary>FRDP-FTP-RICH — probes (once, cached) whether this server allows exec at all: some SFTP-only
        /// servers deny shell access entirely (chrooted/restricted accounts), and this must fail closed to the
        /// labeled download→re-upload fallback rather than throw mid-copy. First read after Connect() pays for the
        /// second connection + a round trip; every read after that is free.</summary>
        public bool CanServerSideCopy
        {
            get
            {
                if (_execCapable.HasValue) return _execCapable.Value;
                _execCapable = ProbeExecCapability();
                return _execCapable.Value;
            }
        }

        private bool ProbeExecCapability()
        {
            try
            {
                EnsureExecClient();
                var cmd = _execClient.RunCommand("echo frdp_exec_ok");
                bool ok = cmd.ExitStatus == 0 && (cmd.Result ?? "").Trim() == "frdp_exec_ok";
                FileLog.Line("[SFTP] exec capability probe: " + (ok ? "available" : "denied (exit=" + cmd.ExitStatus + ")"));
                return ok;
            }
            catch (Exception ex) { FileLog.Line("[SFTP] exec capability probe failed: " + ex.Message); return false; }
        }

        private void EnsureExecClient()
        {
            if (_execClient != null && _execClient.IsConnected) return;
            ConnectionInfo ci;
            try { ci = _auth.BuildConnectionInfo(); }
            catch (SshAuthException ax) { throw ToFsEx(ax); }
            var c = new SshClient(ci);
            _auth.AttachHostKey(c);
            c.Connect();
            _execClient = c;
        }

        /// <summary>FRDP-FTP-RICH — 'cp -r' over the exec channel: true server-side, no bytes cross the network.
        /// Paths are shell-quoted (POSIX single-quote escaping — safe against spaces/$/backticks/semicolons/
        /// anything else a filename could legally contain); this is a shell command line, so quoting is
        /// injection-sensitive and is NOT optional.</summary>
        public void CopyServerSide(string fromPath, string toPath)
        {
            if (!CanServerSideCopy) throw new NotSupportedException("Server-side copy isn't available on this server (shell access denied).");
            string cmd = "cp -r " + ShellQuote(fromPath) + " " + ShellQuote(toPath);
            SshCommand result;
            try { result = _execClient.RunCommand(cmd); }
            catch (Exception ex) { throw new RemoteFsException(RemoteFsError.Protocol, "Server-side copy failed: " + ex.Message, ex); }
            if (result.ExitStatus != 0)
            {
                string msg = !string.IsNullOrEmpty(result.Error) ? result.Error.Trim() : "exit code " + result.ExitStatus;
                throw new RemoteFsException(RemoteFsError.Protocol, "Server-side copy failed: " + msg);
            }
        }

        /// <summary>POSIX single-quote escaping: wrap in single quotes, and every embedded single quote becomes
        /// '\'' (close the quote, an escaped literal quote, reopen the quote). Safe for ANY byte a path can
        /// legally contain except NUL, which can't appear in a POSIX path anyway.</summary>
        private static string ShellQuote(string s) => "'" + (s ?? "").Replace("'", "'\\''") + "'";

        public void Connect()
        {
            ConnectionInfo ci;
            try { ci = _auth.BuildConnectionInfo(); }
            catch (SshAuthException ax) { throw ToFsEx(ax); }

            _client = new SftpClient(ci);
            _client.KeepAliveInterval = TimeSpan.FromSeconds(30);   // FRDP-FIXSWEEP C1 — detect idle drops
            _client.ErrorOccurred += (s, e) => { try { Dropped?.Invoke(); } catch { } };   // FRDP-RECONNECT — idle-drop signal
            _auth.AttachHostKey(_client);
            try { _client.Connect(); }
            catch (SshConnectionException) when (_auth.HostKeyRejected)
            { throw new RemoteFsException(RemoteFsError.HostKeyRejected, "Host key verification failed — the connection was not trusted."); }
            catch (SshAuthException ax) { throw ToFsEx(ax); }
            catch (SshAuthenticationException ax) { throw new RemoteFsException(RemoteFsError.Auth, "The server rejected the credentials.\n\nCheck the username/password (or that the key is in authorized_keys).", ax); }
            catch (SocketException sx) { throw new RemoteFsException(RemoteFsError.Unreachable, "Could not reach " + _cp.Host + ".", sx); }
            catch (SshOperationTimeoutException tx) { throw new RemoteFsException(RemoteFsError.Unreachable, "The connection to " + _cp.Host + " timed out.", tx); }
            catch (Exception ex) { throw new RemoteFsException(RemoteFsError.Unknown, "SFTP connect failed: " + ex.Message, ex); }

            FileLog.Line("[SFTP] connected " + _auth.User + "@" + _cp.Host + ":" + _auth.Port + " cwd=" + _client.WorkingDirectory);
        }

        private static RemoteFsException ToFsEx(SshAuthException ax)
        {
            var kind = ax.Error == SshAuthError.HostKeyRejected ? RemoteFsError.HostKeyRejected : RemoteFsError.Auth;
            return new RemoteFsException(kind, ax.Message, ax);
        }

        public string Parent(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/") return "/";
            path = path.TrimEnd('/');
            int i = path.LastIndexOf('/');
            return i <= 0 ? "/" : path.Substring(0, i);
        }

        public string Combine(string dir, string name)
        {
            if (string.IsNullOrEmpty(dir) || dir == "/") return "/" + name;
            return dir.TrimEnd('/') + "/" + name;
        }

        public IReadOnlyList<RemoteEntry> List(string path)
        {
            var outp = new List<RemoteEntry>();
            foreach (var f in _client.ListDirectory(string.IsNullOrEmpty(path) ? "." : path))
            {
                if (f.Name == "." || f.Name == "..") continue;
                outp.Add(new RemoteEntry
                {
                    Name = f.Name,
                    FullPath = f.FullName,
                    IsDirectory = f.IsDirectory,
                    IsSymlink = f.IsSymbolicLink,
                    Size = f.Length,
                    Modified = f.LastWriteTime
                });
            }
            return outp;
        }

        public void Delete(string path, bool isDir) { if (isDir) _client.DeleteDirectory(path); else _client.DeleteFile(path); }
        public void Rename(string fromPath, string toPath) => _client.RenameFile(fromPath, toPath);
        public void Mkdir(string path) => _client.CreateDirectory(path);

        public bool Exists(string path) { try { return _client != null && _client.Exists(path); } catch { return false; } }

        private const int BufSize = 64 * 1024;

        // FRDP-FIXSWEEP B2 — write to a temp then move into place, so a failed transfer never clobbers the good
        // file. FRDP-FTP-RICH — a manual byte-pump loop (not the DownloadFile convenience method) so both
        // cancellation (checked every buffer) and resume (seek both streams to resumeOffset first) are possible;
        // SftpFileStream (from OpenRead) supports Seek. On OperationCanceledException the temp file is left as-is
        // (the caller decides: pause keeps it for a later resume, a real cancel deletes it itself) — any OTHER
        // exception still cleans up the temp, matching the original behavior exactly.
        public void Download(string remotePath, string localPath, Action<long, long> progress, CancellationToken ct, long resumeOffset)
        {
            long total = 0; try { total = _client.GetAttributes(remotePath).Size; } catch { }
            string tmp = localPath + ".frdp-part";
            if (resumeOffset <= 0) { try { File.Delete(tmp); } catch { } }   // fresh start — clear any stale part
            try
            {
                using (var remote = _client.OpenRead(remotePath))
                using (var local = new FileStream(tmp, resumeOffset > 0 ? FileMode.OpenOrCreate : FileMode.Create, FileAccess.Write))
                {
                    if (resumeOffset > 0) { remote.Seek(resumeOffset, SeekOrigin.Begin); local.Seek(resumeOffset, SeekOrigin.Begin); }
                    byte[] buf = new byte[BufSize];
                    long done = resumeOffset;
                    int n;
                    while ((n = remote.Read(buf, 0, buf.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        local.Write(buf, 0, n);
                        done += n;
                        try { progress?.Invoke(done, total); } catch { }
                    }
                }
                if (File.Exists(localPath)) File.Replace(tmp, localPath, null);
                else File.Move(tmp, localPath);
            }
            catch (OperationCanceledException) { throw; }   // temp left in place — caller's call (pause vs cancel)
            catch { try { File.Delete(tmp); } catch { } throw; }   // no litter; original untouched
        }

        public void Upload(string localPath, string remotePath, Action<long, long> progress, CancellationToken ct, long resumeOffset)
        {
            long total = 0; try { total = new FileInfo(localPath).Length; } catch { }
            string tmp = remotePath + ".frdp-part";
            if (resumeOffset <= 0) { try { if (_client.Exists(tmp)) _client.DeleteFile(tmp); } catch { } }
            try
            {
                using (var local = File.OpenRead(localPath))
                using (var remote = _client.Open(tmp, resumeOffset > 0 ? FileMode.OpenOrCreate : FileMode.Create, FileAccess.Write))
                {
                    if (resumeOffset > 0) { local.Seek(resumeOffset, SeekOrigin.Begin); remote.Seek(resumeOffset, SeekOrigin.Begin); }
                    byte[] buf = new byte[BufSize];
                    long done = resumeOffset;
                    int n;
                    while ((n = local.Read(buf, 0, buf.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        remote.Write(buf, 0, n);
                        done += n;
                        try { progress?.Invoke(done, total); } catch { }
                    }
                }
                // rename temp → final. SFTP rename won't overwrite an existing target, so on failure delete-then-rename
                // (a tiny non-atomic window — acceptable; the overwrite was already confirmed by the UI).
                try { _client.RenameFile(tmp, remotePath); }
                catch
                {
                    try { if (_client.Exists(remotePath)) _client.DeleteFile(remotePath); } catch { }
                    _client.RenameFile(tmp, remotePath);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { try { if (_client.Exists(tmp)) _client.DeleteFile(tmp); } catch { } throw; }
        }

        public void Dispose()
        {
            try { if (_client != null) { if (_client.IsConnected) _client.Disconnect(); _client.Dispose(); } } catch { }
            try { if (_execClient != null) { if (_execClient.IsConnected) _execClient.Disconnect(); _execClient.Dispose(); } } catch { }
            _client = null; _execClient = null;
        }
    }
}
