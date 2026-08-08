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
        /// <summary>Written INTO the legacy folder once its contents have been migrated. It lives there,
        /// not in the new folder, because deleting the new folder is precisely how a user asks for a
        /// fresh start — a marker kept there would be erased by the very action it has to survive.</summary>
        private const string MigratedMarker = ".migrated";

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
                // PORTABLE wins outright: this copy's data lives with it, and no legacy migration runs
                // (TryMigrateLegacy is not even reached, so a portable copy can never pull in Documents data).
                var portable = PortableDir();
                if (portable != null)
                {
                    _dir = portable;
                    System.Diagnostics.Debug.WriteLine("[PATHS] data dir = " + _dir + "  (PORTABLE - sentinel beside the exe)");
                    return _dir;
                }
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

                // ONE-TIME, and the record lives in the OLD folder on purpose.
                //
                // It cannot live in the new folder: deleting Documents\Finestra is exactly the action a
                // user takes to start clean, and that would erase the record and re-arm the migration.
                // That is the bug this fixes — the caller recomputes "is the new folder fresh?" on EVERY
                // launch, so before this marker existed, every delete silently restored the legacy data
                // and a fresh start was impossible while the old folder remained.
                var marker = Path.Combine(oldDir, MigratedMarker);
                if (File.Exists(marker)) return;

                // NEVER OVERWRITE. Migration may only POPULATE AN EMPTY store. CopyTree already skips
                // files that exist, but after a delete there is nothing left to skip - which is how a
                // re-migration once replaced a NEWER connections.json with an older legacy one. Re-check
                // here so the rule holds regardless of how the caller decided we were "fresh".
                if (!IsEffectivelyEmpty(newDir)) return;

                int failed = 0;
                int copied = CopyTree(oldDir, newDir, ref failed);

                // ⚠ ONLY on a CLEAN run. The marker permanently disarms the migration, so writing it after a
                // run that could not read some of the source would strand exactly the files that failed —
                // silently, and for good, with the store already looking populated enough that the caller's
                // "fresh" test never fires again. A locked or unreadable file (an antivirus scan, a copy still
                // open in another process) is transient; leaving the marker unwritten simply retries next
                // launch, and CopyTree skips what it already placed, so a retry is cheap and never overwrites.
                // Zero copies with zero failures is still a clean run - an empty legacy folder is migrated.
                if (failed > 0)
                {
                    MigrationNote = "[PATHS] migrated " + copied + " file(s) from legacy \"" + oldDir
                                  + "\" but " + failed + " could not be read - NOT marking it done, so this retries next launch";
                    return;
                }

                try
                {
                    File.WriteAllText(marker,
                        "Finestra migrated this folder's contents to the current data folder." + Environment.NewLine +
                        "Its presence stops that migration ever running again, so deleting the new" + Environment.NewLine +
                        "data folder gives a genuinely fresh start. Delete this file to allow one" + Environment.NewLine +
                        "more migration. This folder itself is left intact as your backup." + Environment.NewLine);
                }
                catch { }

                if (copied > 0)
                    MigrationNote = "[PATHS] migrated " + copied + " file(s) from legacy \"" + oldDir
                                  + "\" (old folder left intact as backup; will not migrate again)";
            }
            catch { /* best-effort — a fresh start beats a crash at first touch */ }
        }

        /// <summary>Copies what is missing, never what already exists. Returns the number of files copied and
        /// counts, via <paramref name="failed"/>, everything it could not read — the caller needs that to decide
        /// whether the migration may be recorded as done. Keeps going on a failure rather than aborting: a
        /// partial copy plus a retry next launch beats stopping at the first locked file.</summary>
        private static int CopyTree(string src, string dst, ref int failed)
        {
            int n = 0;
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
            {
                var name = Path.GetFileName(f);
                if (string.Equals(name, ".writeprobe", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, MigratedMarker, StringComparison.OrdinalIgnoreCase)) continue;   // our own bookkeeping
                var to = Path.Combine(dst, name);
                if (File.Exists(to)) continue;                  // never overwrite the new folder's data
                try { File.Copy(f, to); n++; } catch { failed++; /* skip unreadable file, keep going */ }
            }
            foreach (var d in Directory.GetDirectories(src))
                try { n += CopyTree(d, Path.Combine(dst, Path.GetFileName(d)), ref failed); } catch { failed++; }
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

        /// <summary>PORTABLE MODE. A file with this name beside the exe makes this copy keep ALL of its
        /// data with it — connections, settings, known hosts, certificates and the log — instead of in
        /// the shared per-user data directory. It touches nothing in Documents.
        ///
        /// It applies to EVERY data file, not just the log. An earlier version redirected only the log,
        /// which satisfied nobody: an extracted zip still opened somebody else's connections, and was
        /// never a fresh start. "Portable" means the folder is the whole of it.
        ///
        /// ⚠ CONSEQUENCE, and it is intended: a portable copy starts with ZERO connections. It does not
        /// inherit the installed copy's profiles, and profiles made in it stay in it.
        ///
        /// INSTALLED copies deliberately do NOT carry this file, so they keep sharing Documents\Finestra
        /// with each other — that is how a public and a private install see the same connections.
        ///
        /// Without the sentinel nothing changes. Any user can create one; it needs no content.</summary>
        public const string PortableSentinel = "Finestra.portable";

        /// <summary>The exe's own directory when this is a portable copy AND that directory is really
        /// writable, otherwise null. The write probe matters — a bundle opened read-only (a network
        /// share, a locked-down stick) must fall back to the shared dir rather than lose its data.</summary>
        private static string PortableDir()
        {
            try
            {
                string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(exeDir)) return null;
                if (!File.Exists(Path.Combine(exeDir, PortableSentinel))) return null;
                var probe = Path.Combine(exeDir, ".writeprobe");
                try { File.WriteAllText(probe, "ok"); File.Delete(probe); return exeDir; }
                catch { return null; }   // not writable — fall through to the normal candidates
            }
            catch { return null; }
        }

        public static string ConnectionsFile { get { return Path.Combine(AppDataDir, "connections.json"); } }
        public static string SettingsFile     { get { return Path.Combine(AppDataDir, "settings.json"); } }
        public static string LogFile          { get { return Path.Combine(AppDataDir, "Finestra.log"); } }
        public static string KnownHostsFile   { get { return Path.Combine(AppDataDir, "known_hosts.json"); } }
        public static string KnownCertsFile   { get { return Path.Combine(AppDataDir, "known_certs.json"); } }
    }
}
