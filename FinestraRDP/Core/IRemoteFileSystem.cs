using System;
using System.Collections.Generic;

namespace Finestra.Core
{
    /// <summary>One directory entry — protocol-neutral. The browser renders these; it never sees SSH.NET/FluentFTP types.</summary>
    public struct RemoteEntry
    {
        public string Name;
        public string FullPath;
        public bool IsDirectory;
        public bool IsSymlink;
        public long Size;
        public DateTime Modified;
    }

    /// <summary>Classified file-system failure → a plain-language themed message (never a raw stack).</summary>
    public enum RemoteFsError { Auth, HostKeyRejected, CertRejected, Tls, Unreachable, NotFound, Denied, Protocol, Unknown }

    public sealed class RemoteFsException : Exception
    {
        public RemoteFsError Error { get; }
        public RemoteFsException(RemoteFsError error, string message, Exception inner = null) : base(message, inner) { Error = error; }
    }

    /// <summary>The UI-side decisions a backend may need mid-connect (host-key / passphrase / server-cert). The
    /// browser form implements this and marshals each to a themed dialog — so the backends stay UI-agnostic and the
    /// browser stays backend-agnostic.</summary>
    public interface IRemotePrompts
    {
        HostKeyDecision VerifyHostKey(HostKeyPrompt p);            // SFTP
        string GetPassphrase(string host, string keyPath);        // SFTP (encrypted key, no stored passphrase)
        CertDecision VerifyCert(CertPrompt p);                     // FTPS
    }

    /// <summary>
    /// FRDP-FTP-BUILD-1 — the spine. ONE dual-pane browser talks ONLY to this; the two remote backends
    /// (<see cref="SftpBackend"/> over SSH.NET, <see cref="FtpBackend"/> over FluentFTP) and the
    /// <see cref="LocalFileSystem"/> (the left pane) all implement it, so the browser has zero backend-specific
    /// branches. Path math (<see cref="Parent"/>/<see cref="Combine"/>) is on the interface because '/' (remote)
    /// and '\' (local) differ — the pane must not hardcode a separator.
    /// </summary>
    public interface IRemoteFileSystem : IDisposable
    {
        string Protocol { get; }                 // "SFTP" | "FTPS" | "FTP" | "Local"
        bool CanRename { get; }

        void Connect();                          // may prompt via the injected IRemotePrompts; throws RemoteFsException
        string HomeDirectory { get; }

        string Parent(string path);              // "/a/b" → "/a"  |  "C:\a\b" → "C:\a"
        string Combine(string dir, string name); // dir + separator + name

        IReadOnlyList<RemoteEntry> List(string path);
        void Delete(string path, bool isDir);
        void Rename(string fromPath, string toPath);
        void Mkdir(string path);

        /// <summary>True if a file/dir already exists at <paramref name="path"/> (for the overwrite confirm before a
        /// transfer replaces it — FRDP-FIXSWEEP B2). Never throws: any failure ⇒ false.</summary>
        bool Exists(string path);

        /// <summary>FRDP-RECONNECT — the transport dropped (event-driven, e.g. SFTP ErrorOccurred). May never fire for
        /// backends without a disconnect event (plain FTP) — those are detected op-driven by the browser instead.</summary>
        event Action Dropped;

        /// <summary>Move bytes between this (remote) filesystem and the LOCAL disk. Progress is (bytesDone, bytesTotal).
        /// Only the remote backends implement these; <see cref="LocalFileSystem"/> stubs them (the browser always
        /// routes a transfer through the remote side).</summary>
        void Download(string remotePath, string localPath, Action<long, long> progress);
        void Upload(string localPath, string remotePath, Action<long, long> progress);
    }
}
