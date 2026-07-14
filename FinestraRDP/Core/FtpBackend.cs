using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Finestra.Helpers;
using FluentFTP;
using FluentFTP.Exceptions;

namespace Finestra.Core
{
    /// <summary>
    /// FRDP-FTP-BUILD-1 — the FTP + FTPS backend: a thin <see cref="IRemoteFileSystem"/> adapter over FluentFTP
    /// (MIT, net462, zero-dep, TLS via in-box SslStream). One backend covers plain FTP and FTPS (the TLS mode is a
    /// config switch). The server certificate is verified through the <see cref="KnownCerts"/> TOFU store + a themed
    /// prompt (NOT accept-any) — the SSH host-key lesson applied to TLS.
    /// </summary>
    public sealed class FtpBackend : IRemoteFileSystem
    {
        private readonly ConnectionProfile _cp;
        private readonly IRemotePrompts _prompts;
        private FtpClient _client;
        private bool _certRejected;
        private string _acceptedThumb;   // FRDP-FIXSWEEP B20 — cert accepted THIS connect (control→data channel), so the data channel doesn't re-prompt

        public FtpBackend(ConnectionProfile cp, IRemotePrompts prompts) { _cp = cp; _prompts = prompts; }

        private FtpProtocol Variant => (_cp.Ftp ?? new FtpSettings()).Protocol;
        public string Protocol => Variant == FtpProtocol.Ftps ? "FTPS" : "FTP";
        public bool CanRename => true;
        /// <summary>FRDP-FTP-RICH — neither FTP nor FTPS has a server-side copy verb (unlike SFTP's exec 'cp'
        /// escape hatch) — always false. The browser falls back to a labeled download→re-upload for these.</summary>
        public bool CanServerSideCopy => false;
        public void CopyServerSide(string fromPath, string toPath) => throw new NotSupportedException("FTP/FTPS has no server-side copy — this should never be called (CanServerSideCopy is always false).");
#pragma warning disable 0067   // FRDP-RECONNECT — FluentFTP has no disconnect event; FTP drops are detected op-driven by the browser
        public event Action Dropped;
#pragma warning restore 0067
        public string HomeDirectory { get { try { return _client != null ? _client.GetWorkingDirectory() : "/"; } catch { return "/"; } } }

        public void Connect()
        {
            var ftp = _cp.Ftp ?? new FtpSettings();
            int port = _cp.Port > 0 ? _cp.Port : ftp.DefaultPort();
            string user = string.IsNullOrWhiteSpace(_cp.Username) ? "anonymous" : _cp.Username.Trim();
            string pass = _cp.GetPassword();

            _client = new FtpClient(_cp.Host, user, pass ?? "");
            _client.Port = port;
            _client.Config.DataConnectionType = ftp.Passive ? FtpDataConnectionType.AutoPassive : FtpDataConnectionType.AutoActive;
            _client.Config.ValidateAnyCertificate = false;   // we TOFU the cert ourselves

            if (Variant == FtpProtocol.Ftps)
            {
                _client.Config.EncryptionMode =
                    ftp.TlsMode == FtpTlsMode.Implicit ? FtpEncryptionMode.Implicit :
                    ftp.TlsMode == FtpTlsMode.Auto ? FtpEncryptionMode.Auto : FtpEncryptionMode.Explicit;
                _client.Config.DataConnectionEncryption = true;
                _client.ValidateCertificate += (control, e) => HandleCert(e);   // lambda avoids naming FluentFTP's control type
            }
            else
            {
                _client.Config.EncryptionMode = FtpEncryptionMode.None;
            }

            try { _client.Connect(); }
            catch (Exception ex) when (_certRejected)
            { throw new RemoteFsException(RemoteFsError.CertRejected, "The server's TLS certificate was not trusted, so the connection was cancelled.", ex); }
            catch (FtpAuthenticationException ax) { throw new RemoteFsException(RemoteFsError.Auth, "The server rejected the username or password.", ax); }
            catch (System.Security.Authentication.AuthenticationException ax) { throw new RemoteFsException(RemoteFsError.Tls, "TLS negotiation with " + _cp.Host + " failed.", ax); }
            catch (SocketException sx) { throw new RemoteFsException(RemoteFsError.Unreachable, "Could not reach " + _cp.Host + ":" + port + ".", sx); }
            catch (FtpException fx) { throw new RemoteFsException(RemoteFsError.Protocol, fx.Message, fx); }
            catch (Exception ex) { throw new RemoteFsException(RemoteFsError.Unknown, "FTP connect failed: " + ex.Message, ex); }

            FileLog.Line("[FTP] connected " + Protocol + " " + user + "@" + _cp.Host + ":" + port + " encrypted=" + _client.IsEncrypted + " cwd=" + HomeDirectory);
        }

        // ── FTPS cert TOFU (parallel to the SSH host-key dialog) ──
        private void HandleCert(FtpSslValidationEventArgs e)
        {
            int port = _cp.Port > 0 ? _cp.Port : (_cp.Ftp ?? new FtpSettings()).DefaultPort();
            string thumb = KnownCerts.Thumbprint(e.Certificate);

            // FRDP-FIXSWEEP B20 — explicit FTPS validates the cert on the control channel AND again on the data channel;
            // "Accept once" doesn't persist, so without this the same cert re-prompts mid-connect. Auto-accept the SAME
            // cert we already accepted this connect (a different cert still prompts).
            if (!string.IsNullOrEmpty(_acceptedThumb) && string.Equals(thumb, _acceptedThumb, StringComparison.OrdinalIgnoreCase))
            { e.Accept = true; return; }

            var kc = KnownCerts.Instance;
            KnownCertEntry existing;
            CertStatus status = kc.Check(_cp.Host, port, thumb, out existing);
            if (status == CertStatus.Match) { e.Accept = true; _acceptedThumb = thumb; FileLog.Line("[FTP] cert OK (known) " + _cp.Host + ":" + port); return; }

            var c2 = SafeCert2(e.Certificate);
            var prompt = new CertPrompt
            {
                Host = _cp.Host, Port = port, Sha256 = thumb, Status = status, OldSha256 = existing?.Sha256,
                Subject = c2?.Subject ?? e.Certificate.Subject,
                Issuer = c2?.Issuer ?? e.Certificate.Issuer,
                NotBefore = c2?.NotBefore ?? DateTime.MinValue,
                NotAfter = c2?.NotAfter ?? DateTime.MinValue,
                PolicyErrors = e.PolicyErrors.ToString()
            };
            CertDecision decision = _prompts != null ? _prompts.VerifyCert(prompt) : CertDecision.Reject;

            if (decision == CertDecision.Reject) { _certRejected = true; e.Accept = false; FileLog.Line("[FTP] cert REJECTED " + _cp.Host + ":" + port); return; }
            e.Accept = true;
            _acceptedThumb = thumb;   // B20 — remember for the data channel this connect
            if (decision == CertDecision.AcceptAndStore) { kc.Store(_cp.Host, port, prompt.Subject, thumb); FileLog.Line("[FTP] cert STORED " + _cp.Host + ":" + port + " " + thumb); }
            else FileLog.Line("[FTP] cert accepted once " + _cp.Host + ":" + port);
        }

        private static X509Certificate2 SafeCert2(X509Certificate c) { try { return new X509Certificate2(c); } catch { return null; } }

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

        // FRDP-FIXSWEEP B9 — FluentFTP's FtpClient is ONE control connection, NOT thread-safe. Every op runs off the UI
        // thread (Task.Run), so serialize all client touches behind one semaphore: a navigate issued during a transfer
        // (or a rapid double-click) waits on its own worker thread instead of interleaving commands and desyncing the
        // protocol. The UI thread never calls these directly, so it never blocks here.
        private readonly System.Threading.SemaphoreSlim _sem = new System.Threading.SemaphoreSlim(1, 1);
        private T Locked<T>(Func<T> f) { _sem.Wait(); try { return f(); } finally { _sem.Release(); } }
        private void Locked(Action a) { _sem.Wait(); try { a(); } finally { _sem.Release(); } }

        public IReadOnlyList<RemoteEntry> List(string path) => Locked(() =>
        {
            var outp = new List<RemoteEntry>();
            foreach (var it in _client.GetListing(string.IsNullOrEmpty(path) ? "/" : path))
            {
                if (it.Name == "." || it.Name == "..") continue;
                outp.Add(new RemoteEntry
                {
                    Name = it.Name,
                    FullPath = it.FullName,
                    IsDirectory = it.Type == FtpObjectType.Directory,
                    IsSymlink = it.Type == FtpObjectType.Link,
                    Size = it.Size < 0 ? 0 : it.Size,
                    Modified = it.Modified
                });
            }
            return (IReadOnlyList<RemoteEntry>)outp;
        });

        public void Delete(string path, bool isDir) => Locked(() => { if (isDir) _client.DeleteDirectory(path); else _client.DeleteFile(path); });
        public void Rename(string fromPath, string toPath) => Locked(() => _client.Rename(fromPath, toPath));
        public void Mkdir(string path) => Locked(() => _client.CreateDirectory(path));

        public bool Exists(string path) { try { return Locked(() => _client != null && _client.FileExists(path)); } catch { return false; } }

        // FRDP-FIXSWEEP B2 — temp-then-move both directions, so a failed transfer never clobbers the good file.
        // (B9 — the whole transfer holds the semaphore, so a concurrent navigate waits instead of racing the client.)
        // FRDP-FTP-RICH — FluentFTP has NO CancellationToken parameter on DownloadFile/UploadFile in this version
        // (confirmed via reflection — no async/CT overload exists at all). The progress callback is the only hook
        // INSIDE a transfer, so cancellation is checked there by throwing — BUT empirically (Part 6, against a
        // real FTP server) FluentFTP does NOT propagate that exception as-is: it catches whatever the callback
        // throws and re-wraps it in a generic FtpException ("...see InnerException for more info"), so a plain
        // "catch (OperationCanceledException)" here never fires. Unwrap it back via IsCancellation below so the
        // CALLER's cancel/pause logic (which specifically keys off that exception type) still works — this was a
        // real bug the verification harness caught, not a hypothetical. Resume uses FluentFTP's OWN native
        // FtpLocalExists.Resume/FtpRemoteExists.Resume (REST) — it reads the existing partial temp file's on-disk
        // size itself, so resumeOffset isn't threaded through explicitly here (it agrees with the temp file's real
        // size by construction — that's exactly what "resuming a paused transfer" means).
        public void Download(string remotePath, string localPath, Action<long, long> progress, CancellationToken ct, long resumeOffset) => Locked(() =>
        {
            long total = 0; try { total = _client.GetFileSize(remotePath); } catch { }
            string tmp = localPath + ".frdp-part";
            if (resumeOffset <= 0) { try { System.IO.File.Delete(tmp); } catch { } }
            var existsMode = resumeOffset > 0 ? FtpLocalExists.Resume : FtpLocalExists.Overwrite;
            try
            {
                var st = _client.DownloadFile(tmp, remotePath, existsMode, FtpVerify.None,
                    p => { if (ct.IsCancellationRequested) throw new OperationCanceledException(ct); try { progress?.Invoke(p.TransferredBytes, total); } catch { } });
                if (st == FtpStatus.Failed) throw new System.IO.IOException("Download failed.");
                if (System.IO.File.Exists(localPath)) System.IO.File.Replace(tmp, localPath, null);
                else System.IO.File.Move(tmp, localPath);
            }
            catch (OperationCanceledException) { ResyncAfterCancel(); throw; }   // temp left in place — caller's call (pause vs cancel)
            catch (Exception ex) when (IsCancellation(ex, ct)) { ResyncAfterCancel(); throw new OperationCanceledException(ct); }
            catch { try { System.IO.File.Delete(tmp); } catch { } throw; }
        });

        /// <summary>Walks the InnerException chain looking for the OperationCanceledException FluentFTP's own
        /// wrapping hid — only true when the token is ALSO actually signaled, so an unrelated failure that happens
        /// to occur near a cancel request is never misclassified as "the cancel worked".</summary>
        private static bool IsCancellation(Exception ex, CancellationToken ct)
        {
            if (!ct.IsCancellationRequested) return false;
            for (var e = ex; e != null; e = e.InnerException) if (e is OperationCanceledException) return true;
            return false;
        }

        /// <summary>FRDP-FTP-RICH Part 6 — throwing out of the progress callback aborts OUR read/write loop, but on
        /// a small/fast transfer the server can already have queued its "226 Transfer complete" reply before the
        /// abort lands; that stray reply then desyncs the NEXT command's response parsing (confirmed live: a
        /// List() right after a cancelled download intermittently failed with "Failed to get the EPSV port from:
        /// Transfer complete."). A plain reconnect on the same FtpClient (config/cert handler stay attached) is
        /// the cheapest reliable resync — best-effort, since a failure here just surfaces on the next real op the
        /// same way any other dropped FTP connection already does.</summary>
        private void ResyncAfterCancel()
        {
            try { _client.Disconnect(); } catch { }
            try { _client.Connect(); } catch { }
        }

        public void Upload(string localPath, string remotePath, Action<long, long> progress, CancellationToken ct, long resumeOffset) => Locked(() =>
        {
            long total = 0; try { total = new System.IO.FileInfo(localPath).Length; } catch { }
            string tmp = remotePath + ".frdp-part";
            if (resumeOffset <= 0) { try { if (_client.FileExists(tmp)) _client.DeleteFile(tmp); } catch { } }
            var existsMode = resumeOffset > 0 ? FtpRemoteExists.Resume : FtpRemoteExists.Overwrite;
            try
            {
                var st = _client.UploadFile(localPath, tmp, existsMode, true, FtpVerify.None,
                    p => { if (ct.IsCancellationRequested) throw new OperationCanceledException(ct); try { progress?.Invoke(p.TransferredBytes, total); } catch { } });
                if (st == FtpStatus.Failed) throw new System.IO.IOException("Upload failed.");
                // rename temp → final; RNTO onto an existing name may be refused → delete-then-rename; if the server
                // refuses rename entirely, direct overwrite upload (the overwrite was already confirmed by the UI).
                try { _client.Rename(tmp, remotePath); }
                catch
                {
                    try { if (_client.FileExists(remotePath)) _client.DeleteFile(remotePath); } catch { }
                    try { _client.Rename(tmp, remotePath); }
                    catch
                    {
                        try { if (_client.FileExists(tmp)) _client.DeleteFile(tmp); } catch { }
                        _client.UploadFile(localPath, remotePath, FtpRemoteExists.Overwrite, true, FtpVerify.None,
                            p => { try { progress?.Invoke(p.TransferredBytes, total); } catch { } });
                    }
                }
            }
            catch (OperationCanceledException) { ResyncAfterCancel(); throw; }
            catch (Exception ex) when (IsCancellation(ex, ct)) { ResyncAfterCancel(); throw new OperationCanceledException(ct); }
            catch { try { if (_client.FileExists(tmp)) _client.DeleteFile(tmp); } catch { } throw; }
        });

        public void Dispose()
        {
            try { if (_client != null) { if (_client.IsConnected) _client.Disconnect(); _client.Dispose(); } } catch { }
            _client = null;
        }
    }
}
