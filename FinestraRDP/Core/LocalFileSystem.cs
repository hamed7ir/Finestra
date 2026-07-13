using System;
using System.Collections.Generic;
using System.IO;

namespace Finestra.Core
{
    /// <summary>
    /// FRDP-FTP-BUILD-1 — the LOCAL side of the dual-pane browser as an <see cref="IRemoteFileSystem"/>, so the pane
    /// UI is identical for both sides. Path "" is the drive list (the local "root"). Byte transfers live on the
    /// REMOTE backend (Download/Upload go through it), so they are no-ops here.
    /// </summary>
    public sealed class LocalFileSystem : IRemoteFileSystem
    {
        public string Protocol => "Local";
        public bool CanRename => true;
#pragma warning disable 0067   // FRDP-RECONNECT — the local filesystem never drops
        public event Action Dropped;
#pragma warning restore 0067
        public void Connect() { }
        public void Dispose() { }

        public string HomeDirectory
        {
            get { try { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } catch { return "C:\\"; } }
        }

        public string Parent(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try
            {
                var di = new DirectoryInfo(path);
                if (di.Parent == null) return "";     // drive root → the drive list
                return di.Parent.FullName;
            }
            catch { return ""; }
        }

        public string Combine(string dir, string name)
        {
            try { return Path.Combine(dir ?? "", name ?? ""); } catch { return name ?? ""; }
        }

        public IReadOnlyList<RemoteEntry> List(string path)
        {
            var outp = new List<RemoteEntry>();
            if (string.IsNullOrEmpty(path))
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    try { outp.Add(new RemoteEntry { Name = d.Name, FullPath = d.Name, IsDirectory = true, Size = 0, Modified = DateTime.MinValue }); }
                    catch { }
                }
                return outp;
            }
            var dir = new DirectoryInfo(path);
            foreach (var sub in dir.GetDirectories())
            {
                try { outp.Add(new RemoteEntry { Name = sub.Name, FullPath = sub.FullName, IsDirectory = true, IsSymlink = (sub.Attributes & FileAttributes.ReparsePoint) != 0, Size = 0, Modified = sub.LastWriteTime }); }
                catch { }
            }
            foreach (var f in dir.GetFiles())
            {
                try { outp.Add(new RemoteEntry { Name = f.Name, FullPath = f.FullName, IsDirectory = false, Size = f.Length, Modified = f.LastWriteTime }); }
                catch { }
            }
            return outp;
        }

        public void Delete(string path, bool isDir)
        {
            if (isDir) Directory.Delete(path, true); else File.Delete(path);
        }

        public void Rename(string fromPath, string toPath)
        {
            if (Directory.Exists(fromPath)) Directory.Move(fromPath, toPath);
            else File.Move(fromPath, toPath);
        }

        public void Mkdir(string path) => Directory.CreateDirectory(path);

        public bool Exists(string path) { try { return !string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)); } catch { return false; } }

        // transfers are performed by the remote backend (it moves bytes to/from local disk)
        public void Download(string remotePath, string localPath, Action<long, long> progress) => throw new NotSupportedException();
        public void Upload(string localPath, string remotePath, Action<long, long> progress) => throw new NotSupportedException();
    }
}
