using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace Finestra.Core
{
    /// <summary>How a presented host key compares to what we already trust for a host:port.</summary>
    public enum HostKeyStatus { Unknown, Match, Changed }

    /// <summary>What the user chose at a host-key prompt. Reject aborts the connection (CanTrust=false).</summary>
    public enum HostKeyDecision { Reject, AcceptOnce, AcceptAndStore }

    /// <summary>
    /// Everything the UI needs to prompt about one presented host key — filled by <see cref="SshSession"/> in the
    /// <c>HostKeyReceived</c> callback and handed to the themed dialog. Immutable data, no UI here.
    /// </summary>
    public sealed class HostKeyPrompt
    {
        public string Host;
        public int Port;
        public string KeyType;      // e.g. ssh-ed25519
        public string Sha256;       // OpenSSH form: "SHA256:<base64-no-pad>"
        public string Md5;          // legacy "MD5:aa:bb:…" (shown as a secondary aid)
        public int KeyBits;
        public HostKeyStatus Status;
        public string OldKeyType;   // Changed only — what was trusted before
        public string OldSha256;    // Changed only
    }

    /// <summary>One trusted host identity persisted to known_hosts.json.</summary>
    public sealed class KnownHostEntry
    {
        public string Host { get; set; } = "";
        public int Port { get; set; } = 22;
        public string KeyType { get; set; } = "";        // ssh-ed25519 / ssh-rsa / ecdsa-…
        public string Sha256 { get; set; } = "";         // "SHA256:<base64-no-pad>" (OpenSSH form)
        public DateTime FirstSeen { get; set; }

        [JsonIgnore] public string Key => Id(Host, Port);
        public static string Id(string host, int port) => (host ?? "").Trim().ToLowerInvariant() + ":" + port;
    }

    /// <summary>
    /// FRDP-SSH-AUTH — the TOFU (trust-on-first-use) known-hosts store: Documents\Finestra\known_hosts.json,
    /// via <see cref="StoragePaths"/> (Documents-first, RT-safe) with the same atomic-save + guarded-load pattern
    /// as <see cref="ConnectionStore"/>. It maps host:port → the key type + SHA256 fingerprint first accepted for
    /// it, so a later <see cref="HostKeyStatus.Changed"/> key (the MITM signal) is detectable rather than silently
    /// trusted. Fingerprints are computed exactly like OpenSSH — <c>"SHA256:" + base64(sha256(hostkey))</c> with
    /// the padding stripped — so they can be eyeball-compared against <c>ssh</c> / <c>ssh-keygen -lf</c>.
    /// </summary>
    public sealed class KnownHosts
    {
        // FRDP-FIXSWEEP B4 — the store is a process-wide singleton hit from every SSH.NET connect thread (SSH tabs via
        // SshSession, SFTP tabs via SshAuth) AND read on the UI thread (the manage view). ONE static lock guards every
        // read, mutation, and save so concurrent first-time connects can't corrupt the list / known_hosts.json.
        private static readonly object _lock = new object();
        private readonly List<KnownHostEntry> _items = new List<KnownHostEntry>();

        /// <summary>A thread-safe SNAPSHOT of the trusted entries (the manage view enumerates this).</summary>
        public List<KnownHostEntry> Items { get { lock (_lock) { return new List<KnownHostEntry>(_items); } } }

        private static KnownHosts _instance;
        public static KnownHosts Instance { get { lock (_lock) { return _instance ?? (_instance = Load()); } } }

        /// <summary>Force a re-read from disk (used by the settings "manage" view and by tests that edit the file).</summary>
        public static void Reload() { lock (_lock) { _instance = Load(); } }

        /// <summary>OpenSSH-style SHA256 fingerprint of a raw host-key blob (the same bytes OpenSSH hashes).</summary>
        public static string Fingerprint(byte[] hostKey)
        {
            using (var sha = SHA256.Create())
                return "SHA256:" + Convert.ToBase64String(sha.ComputeHash(hostKey ?? new byte[0])).TrimEnd('=');
        }

        public KnownHostEntry Lookup(string host, int port)
        {
            lock (_lock) { return _items.FirstOrDefault(e => e.Key == KnownHostEntry.Id(host, port)); }
        }

        /// <summary>Classify a presented key against the store — pure, no mutation. The SHA256 fingerprint (a hash of
        /// the key blob) is the cryptographic identity, so the match is decided on it ALONE. <paramref name="keyType"/>
        /// is only a display label: SSH.NET reports an RSA key's *signature* algorithm (rsa-sha2-256/512), which can
        /// differ between sessions for the very same key — comparing it would raise false "changed" alarms.</summary>
        public HostKeyStatus Check(string host, int port, string keyType, string sha256, out KnownHostEntry existing)
        {
            lock (_lock)
            {
                existing = _items.FirstOrDefault(e => e.Key == KnownHostEntry.Id(host, port));
                if (existing == null) return HostKeyStatus.Unknown;
                return string.Equals(existing.Sha256, sha256, StringComparison.Ordinal) ? HostKeyStatus.Match : HostKeyStatus.Changed;
            }
        }

        /// <summary>Insert or replace the trusted key for host:port, then persist. (Replace = a deliberately-accepted
        /// key change.)</summary>
        public void Store(string host, int port, string keyType, string sha256)
        {
            lock (_lock)
            {
                var e = _items.FirstOrDefault(x => x.Key == KnownHostEntry.Id(host, port));
                if (e == null)
                    _items.Add(new KnownHostEntry { Host = host, Port = port, KeyType = keyType, Sha256 = sha256, FirstSeen = DateTime.Now });
                else { e.KeyType = keyType; e.Sha256 = sha256; e.FirstSeen = DateTime.Now; }
                SaveLocked();
            }
        }

        public void Remove(string host, int port)
        {
            lock (_lock) { _items.RemoveAll(e => e.Key == KnownHostEntry.Id(host, port)); SaveLocked(); }
        }

        private static KnownHosts Load()
        {
            var store = new KnownHosts();
            try
            {
                string path = StoragePaths.KnownHostsFile;
                if (File.Exists(path))
                {
                    var items = JsonConvert.DeserializeObject<List<KnownHostEntry>>(File.ReadAllText(path));
                    if (items != null) store._items.AddRange(items);
                }
            }
            catch (Exception ex) { Debug.WriteLine("[KNOWNHOSTS] load failed: " + ex.Message); }
            return store;
        }

        public void Save() { lock (_lock) { SaveLocked(); } }

        // Caller holds _lock. Serializes the live list + atomic temp-then-replace (a crash mid-write can't corrupt it).
        private void SaveLocked()
        {
            try
            {
                string path = StoragePaths.KnownHostsFile;
                string json = JsonConvert.SerializeObject(_items, Formatting.Indented);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (Exception ex) { Debug.WriteLine("[KNOWNHOSTS] save failed: " + ex.Message); }
        }
    }
}
