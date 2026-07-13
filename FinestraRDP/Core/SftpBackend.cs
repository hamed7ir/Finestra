using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
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

        public SftpBackend(ConnectionProfile cp, IRemotePrompts prompts) { _cp = cp; _auth = new SshAuth(cp, prompts); }

        public string Protocol => "SFTP";
        public bool CanRename => true;
        public string HomeDirectory => _client != null ? _client.WorkingDirectory : "/";
        public event Action Dropped;   // FRDP-RECONNECT — raised on SftpClient.ErrorOccurred

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

        // FRDP-FIXSWEEP B2 — write to a temp then move into place, so a failed transfer never clobbers the good file.
        public void Download(string remotePath, string localPath, Action<long, long> progress)
        {
            long total = 0; try { total = _client.GetAttributes(remotePath).Size; } catch { }
            string tmp = localPath + ".frdp-part";
            try { File.Delete(tmp); } catch { }   // clear any stale part from a prior crash
            try
            {
                using (var fs = File.Create(tmp))
                    _client.DownloadFile(remotePath, fs, b => { try { progress?.Invoke((long)b, total); } catch { } });
                if (File.Exists(localPath)) File.Replace(tmp, localPath, null);
                else File.Move(tmp, localPath);
            }
            catch { try { File.Delete(tmp); } catch { } throw; }   // no litter; original untouched
        }

        public void Upload(string localPath, string remotePath, Action<long, long> progress)
        {
            long total = 0; try { total = new FileInfo(localPath).Length; } catch { }
            string tmp = remotePath + ".frdp-part";
            try { if (_client.Exists(tmp)) _client.DeleteFile(tmp); } catch { }
            try
            {
                using (var fs = File.OpenRead(localPath))
                    _client.UploadFile(fs, tmp, b => { try { progress?.Invoke((long)b, total); } catch { } });
                // rename temp → final. SFTP rename won't overwrite an existing target, so on failure delete-then-rename
                // (a tiny non-atomic window — acceptable; the overwrite was already confirmed by the UI).
                try { _client.RenameFile(tmp, remotePath); }
                catch
                {
                    try { if (_client.Exists(remotePath)) _client.DeleteFile(remotePath); } catch { }
                    _client.RenameFile(tmp, remotePath);
                }
            }
            catch { try { if (_client.Exists(tmp)) _client.DeleteFile(tmp); } catch { } throw; }
        }

        public void Dispose()
        {
            try { if (_client != null) { if (_client.IsConnected) _client.Disconnect(); _client.Dispose(); } } catch { }
            _client = null;
        }
    }
}
