using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Finestra.Helpers;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Finestra.Core
{
    /// <summary>
    /// FRDP-FTP-BUILD-1 — the SSH auth + host-key TOFU builder shared by the SFTP backend. It mirrors
    /// <see cref="SshSession"/>'s auth-method building + <c>HostKeyReceived</c> handling and REUSES the existing
    /// <see cref="KnownHosts"/> store + <see cref="HostKeyPrompt"/>/<see cref="HostKeyDecision"/> + the
    /// <see cref="SshAuthException"/> classification — but lives here so <see cref="SshSession"/> stays byte-for-byte
    /// (SSH untouched). SFTP reads its auth from <see cref="ConnectionProfile.Ftp"/> (the SFTP variant). Prompts are
    /// delegated to <see cref="IRemotePrompts"/> so this stays UI-agnostic. (A future cleanup could unify this with
    /// SshSession; kept separate now to avoid any SSH regression.)
    /// </summary>
    internal sealed class SshAuth
    {
        private readonly ConnectionProfile _cp;
        private readonly IRemotePrompts _prompts;
        public bool HostKeyRejected { get; private set; }

        public SshAuth(ConnectionProfile cp, IRemotePrompts prompts) { _cp = cp; _prompts = prompts; }

        public string User => string.IsNullOrWhiteSpace(_cp.Username) ? "root" : _cp.Username.Trim();
        public int Port => _cp.Port > 0 ? _cp.Port : 22;

        public ConnectionInfo BuildConnectionInfo()
        {
            string secret = _cp.GetPassword();          // password (password auth) OR key passphrase (key auth)
            var ftp = _cp.Ftp ?? new FtpSettings();

            var methods = new List<AuthenticationMethod>();
            if (ftp.AuthKind == SshAuthKind.PrivateKey && !string.IsNullOrWhiteSpace(ftp.PrivateKeyPath))
            {
                var keyFile = LoadKey(ftp.PrivateKeyPath, secret);
                methods.Add(new PrivateKeyAuthenticationMethod(User, keyFile));
            }
            else
            {
                methods.Add(new PasswordAuthenticationMethod(User, secret ?? ""));
                var ki = new KeyboardInteractiveAuthenticationMethod(User);
                ki.AuthenticationPrompt += (s, e) => { foreach (AuthenticationPrompt p in e.Prompts) p.Response = secret ?? ""; };
                methods.Add(ki);
            }
            return new ConnectionInfo(_cp.Host, Port, User, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(20) };
        }

        public void AttachHostKey(BaseClient client) { client.HostKeyReceived += OnHostKeyReceived; }

        private void OnHostKeyReceived(object sender, HostKeyEventArgs e)
        {
            // FRDP-FIXSWEEP B12 — DEFAULT-DENY (SFTP path; same as SshSession). CanTrust defaults to TRUE in SSH.NET,
            // so deny first; only a fully successful verify re-enables trust; any throw ⇒ stays denied + surfaced.
            e.CanTrust = false;
            try
            {
                string type = e.HostKeyName;
                string fp = KnownHosts.Fingerprint(e.HostKey);
                var kh = KnownHosts.Instance;

                KnownHostEntry existing;
                HostKeyStatus status = kh.Check(_cp.Host, Port, type, fp, out existing);
                if (status == HostKeyStatus.Match) { e.CanTrust = true; FileLog.Line("[SFTP] host key OK (known) " + _cp.Host + ":" + Port + " " + fp); return; }

                var prompt = new HostKeyPrompt
                {
                    Host = _cp.Host, Port = Port, KeyType = type, Sha256 = fp,
                    Md5 = SafeMd5(e), KeyBits = e.KeyLength, Status = status,
                    OldKeyType = existing?.KeyType, OldSha256 = existing?.Sha256
                };
                HostKeyDecision decision = _prompts != null ? _prompts.VerifyHostKey(prompt) : HostKeyDecision.Reject;

                if (decision == HostKeyDecision.Reject) { HostKeyRejected = true; FileLog.Line("[SFTP] host key REJECTED " + _cp.Host + ":" + Port); return; }
                e.CanTrust = true;
                if (decision == HostKeyDecision.AcceptAndStore) { kh.Store(_cp.Host, Port, type, fp); FileLog.Line("[SFTP] host key STORED " + _cp.Host + ":" + Port + " " + fp); }
                else FileLog.Line("[SFTP] host key accepted once " + _cp.Host + ":" + Port);
            }
            catch (Exception ex)
            {
                e.CanTrust = false;   // fail closed
                HostKeyRejected = true;
                FileLog.Line("[SFTP] host-key verification errored → connection refused: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string SafeMd5(HostKeyEventArgs e) { try { return e.FingerPrintMD5; } catch { return null; } }

        private PrivateKeyFile LoadKey(string path, string storedPassphrase)
        {
            if (!File.Exists(path)) throw new SshAuthException(SshAuthError.UnreadableKey, "The private key file was not found:\n" + path);
            if (IsPuttyPpk(path))
                throw new SshAuthException(SshAuthError.PuttyPpk,
                    "This looks like a PuTTY .ppk key, which this client can't read directly.\n\nConvert it to OpenSSH format with PuTTYgen and select that file instead.");

            bool hadStored = !string.IsNullOrEmpty(storedPassphrase);
            try { return hadStored ? new PrivateKeyFile(path, storedPassphrase) : new PrivateKeyFile(path); }
            catch (SshPassPhraseNullOrEmptyException)
            {
                string entered = _prompts != null ? _prompts.GetPassphrase(_cp.Host, path) : null;
                if (string.IsNullOrEmpty(entered)) throw new SshAuthException(SshAuthError.PassphraseRequired, "This key is passphrase-protected and no passphrase was provided.");
                try { return new PrivateKeyFile(path, entered); }
                catch (Exception ex) { throw new SshAuthException(SshAuthError.BadPassphrase, "The passphrase did not unlock the key.", ex); }
            }
            catch (SshAuthException) { throw; }
            catch (Exception ex)
            {
                if (hadStored) throw new SshAuthException(SshAuthError.BadPassphrase, "The saved passphrase did not unlock the key (or the key is unreadable).", ex);
                throw new SshAuthException(SshAuthError.UnreadableKey, "The private key could not be read (unsupported or corrupt format).", ex);
            }
        }

        private static bool IsPuttyPpk(string path)
        {
            try
            {
                using (var r = new StreamReader(path, Encoding.ASCII, false))
                {
                    char[] head = new char[20]; int n = r.Read(head, 0, head.Length);
                    return n >= 20 && new string(head, 0, n).StartsWith("PuTTY-User-Key-File-", StringComparison.Ordinal);
                }
            }
            catch { return false; }
        }
    }
}
