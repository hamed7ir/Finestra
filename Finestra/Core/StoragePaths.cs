using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Finestra.Core
{
    /// <summary>
    /// Resolves the app's persistent data directory. RT 8.1 INVARIANT: %APPDATA% is UNRELIABLE on
    /// jailbroken RT (confirmed in CS-Ray/TelegArm) — so we prefer <c>Documents\Finestra\</c> with a REAL
    /// write-test, and only fall back to %APPDATA% then beside-exe if Documents is unwritable.
    /// File-only: never reads Screen/UI (safe to call before SetProcessDpiAwareness).
    ///
    /// ONE-TIME MIGRATION (1.0 rename): pre-1.0 builds stored data in <c>FinestraRDP\</c>. If the NEW folder
    /// is fresh (absent or effectively empty) and the OLD folder exists at the SAME root, its contents are
    /// COPIED once — never overwriting anything already present — and the old folder is left intact as a
    /// natural backup for this release. A populated new folder is never touched by old data.
    /// </summary>
    public static class StoragePaths
    {
        private const string AppFolder = "Finestra";
        private const string LegacyAppFolder = "FinestraRDP";   // pre-1.0 folder name — migration SOURCE only

        private static string _dir;

        /// <summary>Set when the legacy-data migration copied something. The migration runs on the very first
        /// <see cref="AppDataDir"/> touch — BEFORE FileLog's listener attaches — so the note is carried here
        /// and FileLog.Init writes it once the log is live.</summary>
        public static string MigrationNote;

        /// <summary>The resolved, write-tested data directory (cached). Logs which root won via [PATHS].</summary>
        public static string AppDataDir
        {
            get
            {
                if (_dir != null) return _dir;
                foreach (var root in CandidateRoots())
                {
                    try
                    {
                        var dir = Path.Combine(root, AppFolder);
                        bool fresh = !Directory.Exists(dir) || IsEffectivelyEmpty(dir);   // BEFORE the probe creates it
                        Directory.CreateDirectory(dir);
                        var probe = Path.Combine(dir, ".writeprobe");
                        File.WriteAllText(probe, "ok");
                        File.Delete(probe);                     // real write-test, not just existence
                        _dir = dir;                              // set FIRST — anything the migration logs re-enters safely
                        if (fresh) TryMigrateLegacy(root, dir);
                        System.Diagnostics.Debug.WriteLine("[PATHS] data dir = " + dir);
                        return _dir;
                    }
                    catch { /* not writable — try the next candidate */ }
                }
                _dir = Path.Combine(Path.GetTempPath(), AppFolder);   // last resort (should never be needed)
                try { Directory.CreateDirectory(_dir); } catch { }
                System.Diagnostics.Debug.WriteLine("[PATHS] data dir (temp fallback) = " + _dir);
                return _dir;
            }
        }

        /// <summary>True when the folder holds nothing but (at most) a stale write probe — treated the same as
        /// absent, so a crashed first run can still migrate on the next launch.</summary>
        private static bool IsEffectivelyEmpty(string dir)
        {
            try
            {
                foreach (var e in Directory.GetFileSystemEntries(dir))
                    if (!string.Equals(Path.GetFileName(e), ".writeprobe", StringComparison.OrdinalIgnoreCase))
                        return false;
                return true;
            }
            catch { return false; }
        }

        /// <summary>Copies <c>root\FinestraRDP\*</c> → the fresh new folder (same root ONLY — historically both
        /// names resolve through the identical candidate ladder, so old data lives at the root the new dir just
        /// won). Copy, never move; never overwrite; the old folder is deliberately left intact.</summary>
        private static void TryMigrateLegacy(string root, string newDir)
        {
            try
            {
                var oldDir = Path.Combine(root, LegacyAppFolder);
                if (!Directory.Exists(oldDir)) return;
                int copied = CopyTree(oldDir, newDir);
                if (copied > 0)
                    MigrationNote = "[PATHS] migrated " + copied + " file(s) from legacy \"" + oldDir
                                  + "\" (old folder left intact as backup)";
            }
            catch { /* best-effort — a fresh start beats a crash at first touch */ }
        }

        private static int CopyTree(string src, string dst)
        {
            int n = 0;
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
            {
                var name = Path.GetFileName(f);
                if (string.Equals(name, ".writeprobe", StringComparison.OrdinalIgnoreCase)) continue;
                var to = Path.Combine(dst, name);
                if (File.Exists(to)) continue;                  // never overwrite the new folder's data
                try { File.Copy(f, to); n++; } catch { /* skip unreadable file, keep going */ }
            }
            foreach (var d in Directory.GetDirectories(src))
                try { n += CopyTree(d, Path.Combine(dst, Path.GetFileName(d))); } catch { }
            return n;
        }

        private static IEnumerable<string> CandidateRoots()
        {
            string docs = null, appdata = null, exe = null;
            try { docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); } catch { }
            if (!string.IsNullOrEmpty(docs)) yield return docs;                 // 1) Documents — RT-preferred
            try { appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } catch { }
            if (!string.IsNullOrEmpty(appdata)) yield return appdata;           // 2) %APPDATA% (unreliable on RT)
            try { exe = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); } catch { }
            if (!string.IsNullOrEmpty(exe)) yield return exe;                   // 3) beside the exe
        }

        public static string ConnectionsFile { get { return Path.Combine(AppDataDir, "connections.json"); } }
        public static string SettingsFile     { get { return Path.Combine(AppDataDir, "settings.json"); } }
        public static string LogFile          { get { return Path.Combine(AppDataDir, "Finestra.log"); } }
        public static string KnownHostsFile   { get { return Path.Combine(AppDataDir, "known_hosts.json"); } }
        public static string KnownCertsFile   { get { return Path.Combine(AppDataDir, "known_certs.json"); } }
    }
}
