using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Newtonsoft.Json;

namespace Finestra.Core
{
    /// <summary>How a presented FTPS server certificate compares to what we already trust for a host:port.</summary>
    public enum CertStatus { Unknown, Match, Changed }

    /// <summary>What the user chose at a certificate-trust prompt.</summary>
    public enum CertDecision { Reject, AcceptOnce, AcceptAndStore }

    /// <summary>Everything the themed cert-trust dialog needs about one presented FTPS certificate.</summary>
    public sealed class CertPrompt
    {
        public string Host;
        public int Port;
        public string Subject;
        public string Issuer;
        public string Sha256;        // "SHA256:<hex>"
        public DateTime NotBefore, NotAfter;
        public string PolicyErrors;  // e.g. "RemoteCertificateChainErrors" — why the OS didn't auto-trust it
        public CertStatus Status;
        public string OldSha256;     // Changed only
    }

    /// <summary>One trusted FTPS certificate persisted to known_certs.json.</summary>
    public sealed class KnownCertEntry
    {
        public string Host { get; set; } = "";
        public int Port { get; set; } = 21;
        public string Subject { get; set; } = "";
        public string Sha256 { get; set; } = "";     // "SHA256:<hex>" thumbprint of the cert's raw data
        public DateTime FirstSeen { get; set; }

        [JsonIgnore] public string Key => Id(Host, Port);
        public static string Id(string host, int port) => (host ?? "").Trim().ToLowerInvariant() + ":" + port;
    }

    /// <summary>
    /// FRDP-FTP-BUILD-1 — the FTPS certificate TOFU store (Documents\Finestra\known_certs.json), the direct
    /// parallel to <see cref="KnownHosts"/> for SSH: trust-on-first-use of the server cert's SHA256 thumbprint,
    /// warn on a later change. We do NOT blindly accept-any (the SSH TOFU lesson): FluentFTP's cert-validation
    /// callback routes through <see cref="Check"/> + a themed prompt. FTPS often uses self-signed certs, so this
    /// thumbprint-pinning model (not CA-chain validation) is the honest fit — same shape as SSH host keys.
    /// </summary>
    public sealed class KnownCerts
    {
        public List<KnownCertEntry> Items { get; private set; } = new List<KnownCertEntry>();

        private static KnownCerts _instance;
        public static KnownCerts Instance => _instance ?? (_instance = Load());
        public static void Reload() { _instance = Load(); }

        /// <summary>SHA256 thumbprint of a certificate's raw DER bytes, formatted "SHA256:&lt;hex&gt;".</summary>
        public static string Thumbprint(X509Certificate cert)
        {
            try
            {
                using (var sha = SHA256.Create())
                    return "SHA256:" + BitConverter.ToString(sha.ComputeHash(cert.GetRawCertData())).Replace("-", "").ToLowerInvariant();
            }
            catch { return "SHA256:?"; }
        }

        public KnownCertEntry Lookup(string host, int port)
            => Items.FirstOrDefault(e => e.Key == KnownCertEntry.Id(host, port));

        public CertStatus Check(string host, int port, string sha256, out KnownCertEntry existing)
        {
            existing = Lookup(host, port);
            if (existing == null) return CertStatus.Unknown;
            return string.Equals(existing.Sha256, sha256, StringComparison.Ordinal) ? CertStatus.Match : CertStatus.Changed;
        }

        public void Store(string host, int port, string subject, string sha256)
        {
            var e = Lookup(host, port);
            if (e == null)
                Items.Add(new KnownCertEntry { Host = host, Port = port, Subject = subject, Sha256 = sha256, FirstSeen = DateTime.Now });
            else { e.Subject = subject; e.Sha256 = sha256; e.FirstSeen = DateTime.Now; }
            Save();
        }

        public void Remove(string host, int port)
        {
            Items.RemoveAll(e => e.Key == KnownCertEntry.Id(host, port));
            Save();
        }

        private static KnownCerts Load()
        {
            var store = new KnownCerts();
            try
            {
                string path = StoragePaths.KnownCertsFile;
                if (File.Exists(path))
                {
                    var items = JsonConvert.DeserializeObject<List<KnownCertEntry>>(File.ReadAllText(path));
                    if (items != null) store.Items = items;
                }
            }
            catch (Exception ex) { Debug.WriteLine("[KNOWNCERTS] load failed: " + ex.Message); }
            return store;
        }

        public void Save()
        {
            try
            {
                string path = StoragePaths.KnownCertsFile;
                string json = JsonConvert.SerializeObject(Items, Formatting.Indented);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (Exception ex) { Debug.WriteLine("[KNOWNCERTS] save failed: " + ex.Message); }
        }
    }
}
