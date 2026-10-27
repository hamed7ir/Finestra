// =============================================================================
//  Finestra AnyCPU installer  (Setup.cs)
//
//  A .NET Framework AnyCPU/MSIL installer, so it RUNS ON WINDOWS RT 8.1 (ARM32)
//  and Windows 10 ARM32 — unlike an Inno Setup installer, whose loader (SetupLdr) is
//  native x86 and physically cannot launch on RT (no x86 emulation).
//
//  ONE FILE. build.ps1 compiles this source twice, and both carry the embedded resource
//  "payload.list" (sha256 TAB size TAB relative name) - the ONE description of what Setup owns:
//    uninstall.exe               no payload; what an install leaves in the program folder
//    Finestra-Setup-<ver>.exe    the download: also embeds "payload.zip" (the app in its FLAT
//                                deploy layout: managed DLLs + .config + engine\<arch>\sdl-freerdp.exe
//                                beside the exe - x64 and arm are bundled; engine\x86\ ships with only
//                                a README, so a user-supplied engine there is never ours to touch)
//                                and "uninstall.exe" (the exe above, written into the install folder)
//  Every payload.zip entry must match payload.list before anything is written, and uninstall.exe
//  deletes exactly those names. Nothing is read from beside the exe: it can be run from anywhere.
//
//  Pattern MIRRORED from CS-Ray's installer/Setup.cs (same author), including the fixes
//  from its two installer reviews (INSTALLER-REVIEW-HANDOFF-CSRAY.md, 2026-10-13):
//    - never write into or delete a folder that is not new, empty, or an installed copy
//    - delete only the files Setup put there; never a recursive delete, never rmdir /s
//    - verify the whole payload BEFORE touching the old install, then swap files in
//    - one normalised folder path for the files, shortcuts and registry
//    - never kill the user's app: ask them to close it, with Retry
//    - unattended modes never show a dialog and never guess a folder from a switch
//    - shortcuts, the Uninstall key and the Run values go only if they point into the folder
//
//  Modes:
//    (no args)                         -> install wizard UI
//    --silent [dir] [--no-desktop]     -> install with no UI (default dir: per-user)
//    --uninstall [dir]                 -> uninstall with UI (Retry while the app runs)
//    --uninstall [dir] --silent        -> uninstall with no UI
//    (run as uninstall.exe)            -> confirm, then uninstall its own folder (also: any copy
//                                         without an embedded payload, i.e. a renamed uninstall.exe)
//  Exit codes: 0 ok, 1 error, 2 unknown switch, 4 the app is running from there, 5 folder refused.
//
//  Installs per-user to %LocalAppData%\Programs\Finestra by default (NO elevation,
//  asInvoker). The app write-probes its data to Documents\Finestra (never beside the
//  exe), so neither install nor launch needs admin — exactly what the RT device needs.
//  UNINSTALL NEVER TOUCHES Documents\Finestra (connections, host keys, certs, settings).
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FinestraSetup
{
    internal static class Program
    {
        // PRODUCT IDENTITY. Everything user-visible, and everything that decides WHERE this installer
        // writes, derives from the four consts below: the install directory (DefaultDir), the Start-menu
        // and desktop shortcut filenames, the HKCU uninstall key, the wizard title and its messages.
        //
        // A PRIVATE build is a SEPARATE PRODUCT, not a variant of the public one. Both packages carry the
        // same file names, so if the two shared an identity a private install would overwrite the public
        // app's files in place, take over its shortcut and its Add/Remove entry, and uninstalling either
        // would remove the other's program files. Diverging the identity here makes that impossible by
        // construction: different folder, different shortcut, different Add/Remove entry. Nothing else
        // in this file needs to know which variant it is.
        //
        // DELIBERATELY NOT gated: the pre-1.0 "FinestraRDP" Run-value cleanup further down. That literal
        // is a compatibility contract with existing installs and must never be renamed or duplicated.
#if PRIVATE_BUILD
        internal const string AppName = "Finestra (Private)";
        internal const string InstallFolderName = "Finestra-Private";
        internal const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Finestra-Private";
        internal const string ShortcutDesc = "Finestra (Private) - Remote Connection Manager";
#else
        internal const string AppName = "Finestra";
        internal const string InstallFolderName = "Finestra";
        internal const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Finestra";
        internal const string ShortcutDesc = "Finestra - Remote Connection Manager";
#endif
        internal const string AppVersion = "2.0.2";
        internal const string Publisher = "Hamed Ghorbani";
        internal const string ExeName = "Finestra.exe";
        internal const string ProcessName = "Finestra";
        // SDL-ENGINE — the RDP engine is FreeRDP's SDL client (sdl-freerdp.exe) since the SDL switch; wfreerdp.exe
        // is what older releases shipped and what a private build and a user's own x86 build still are.
        // A running copy of EITHER from the folder blocks an install or uninstall.
        internal static readonly string[] EngineExeNames = { "sdl-freerdp.exe", "wfreerdp.exe" };
        internal const string PayloadName = "payload.zip";        // manifest resources build.ps1 embeds
        internal const string UninstallerName = "uninstall.exe";
        internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        internal const string DataFolderNote = @"Documents\Finestra";

        [STAThread]
        private static int Main(string[] args)
        {
            bool quiet = Array.Exists(args, a => Eq(a, "--silent"));
            try
            {
                string a0 = args.Length >= 1 ? args[0] : "";
                if (Eq(a0, "--uninstall"))
                {
                    // Entries written by any build quote the folder; tolerate a stray quote or trailing backslash
                    // ("D:\Apps\X\" reaches us as D:\Apps\X" - the command-line parser reads \" as a literal quote).
                    string raw = args.Length >= 2 && !args[1].StartsWith("--") ? args[1].Trim().Trim('"') : Path.GetDirectoryName(Application.ExecutablePath);
                    string dir = Norm(raw);
                    if (quiet)
                    {
                        if (dir == null) return 5;
                        if (InUse.Check(dir) != null) return 4;
                        if (!SafeTarget.IsInstalledCopy(dir)) return 5;   // never touch a folder that is not an installed Finestra
                        return Uninstaller.Run(dir) ? 0 : 1;
                    }
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    return UninstallWithUi(dir ?? raw, false);   // Apps & features has already asked "are you sure"
                }
                if (Eq(a0, "--silent"))
                {
                    string dir = DefaultDir();
                    bool desktop = true;
                    for (int i = 1; i < args.Length; i++)
                    {
                        if (Eq(args[i], "--no-desktop")) desktop = false;
                        else if (args[i].StartsWith("-") || args[i].StartsWith("/")) return 2;   // e.g. /S: a switch, not a folder
                        else dir = args[i];
                    }
                    if (SafeTarget.Problem(dir) != null) return 5;
                    if (InUse.Check(dir) != null) return 4;
                    Installer.Install(dir, desktop, null);
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // Double-clicking the dropped uninstall.exe (no args) -> confirm + uninstall its own folder. A copy with
                // no payload inside can only be an uninstaller, whatever it has been renamed to: it has nothing to install.
                if (Eq(Path.GetFileNameWithoutExtension(Application.ExecutablePath), "uninstall") || !Payload.HasPackage())
                    return UninstallWithUi(Path.GetDirectoryName(Application.ExecutablePath), true);

                Application.Run(new InstallForm());
                return 0;
            }
            catch (Exception ex)
            {
                if (quiet) { try { Console.Error.WriteLine(ex.ToString()); } catch { } return 1; }   // unattended: never block on a dialog
                try { MessageBox.Show(ex.ToString(), AppName + " Setup - error", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
                return 1;
            }
        }

        /// <summary>Message boxes only - no form, so nothing here can keep the process alive past the
        /// self-delete's wait. Deliberately NO "removed" box after Uninstaller.Run: the detached cmd is
        /// already waiting for uninstall.exe to exit, and a box left open would make it miss.</summary>
        private static int UninstallWithUi(string dir, bool confirm)
        {
            if (confirm && MessageBox.Show("Remove " + AppName + " from this computer?\n\n(Your connections, host keys and settings in " + DataFolderNote + " are kept.)",
                    "Uninstall " + AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 0;
            string busy;
            while ((busy = InUse.Check(dir)) != null)
                if (MessageBox.Show(busy + "\n\nClose " + AppName + " and its sessions, then press Retry.", "Uninstall " + AppName,
                    MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) != DialogResult.Retry) return 4;
            if (!SafeTarget.IsInstalledCopy(dir))
            {
                MessageBox.Show(dir + "\n\ndoesn't look like an installed " + AppName + " (no " + ExeName + " and uninstall.exe in it), so nothing was removed.",
                    "Uninstall " + AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 5;
            }
            Uninstaller.Run(dir);
            return 0;
        }

        internal static bool Eq(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        /// <summary>The one spelling of a folder used for files, shortcuts and the registry: full, no
        /// trailing backslash. Null when it is not a usable path.</summary>
        internal static string Norm(string dir)
        {
            try
            {
                string s = (dir ?? "").Trim();
                return s.Length == 0 ? null : Path.GetFullPath(s).TrimEnd('\\');
            }
            catch { return null; }
        }

        internal static string DefaultDir()
        {
            string p = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(Path.Combine(p, "Programs"), InstallFolderName);   // folder-safe name, not the display name
        }
    }

    // ── payload: payload.zip and uninstall.exe embedded in this exe, described by the embedded payload.list ──────
    internal sealed class PayloadFile
    {
        public string Name, Sha256;
        public long Size;
    }

    internal static class Payload
    {
        /// <summary>The embedded list (sha256 TAB size TAB relative name) build.ps1 wrote from the staged bundle.
        /// uninstall.exe is compiled with the same list (and no payload), so it knows what it owns.</summary>
        internal static List<PayloadFile> List()
        {
            var list = new List<PayloadFile>();
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.list"))
            {
                if (s == null) throw new InvalidDataException("This Setup has no payload.list - it was not built by installer\\anycpu\\build.ps1.");
                using (var r = new StreamReader(s, Encoding.UTF8))
                {
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        var f = line.Split('\t');
                        if (f.Length != 3) continue;
                        string name = f[2];
                        // The names are ours (build.ps1 wrote them), but they become paths under the install folder:
                        // refuse anything that could step outside it.
                        if (name.Length == 0 || Path.IsPathRooted(name) || name.Contains("..") || name.IndexOf('/') >= 0)
                            throw new InvalidDataException("payload.list has an unusable name: " + name);
                        list.Add(new PayloadFile { Sha256 = f[0], Size = long.Parse(f[1]), Name = name });
                    }
                }
            }
            return list;
        }

        /// <summary>True when this exe carries a package to install (Setup), false for the payload-free uninstall.exe.</summary>
        internal static bool HasPackage()
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceInfo(Program.PayloadName) != null;
        }

        /// <summary>Every listed file, read from the embedded payload.zip and checked against its size and SHA-256 -
        /// all of it, in memory, BEFORE the install folder is touched. A damaged download throws here and leaves a
        /// working install exactly as it was. Each file is read straight into an array of its listed size: about
        /// 40 MB at peak, against twice that when every file went through a growing MemoryStream (fine on RT).</summary>
        internal static Dictionary<string, byte[]> ReadVerified(List<PayloadFile> files)
        {
            var want = new Dictionary<string, PayloadFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files) want[f.Name] = f;
            var data = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            using (var rs = Assembly.GetExecutingAssembly().GetManifestResourceStream(Program.PayloadName))
            {
                // The payload-free uninstall.exe lands here when asked to install: it has nothing to install from.
                if (rs == null) throw new InvalidDataException("This program carries no " + Program.AppName + " package to install - "
                    + "it is the uninstaller. Run Finestra-Setup to install.");
                using (var zip = new ZipArchive(rs, ZipArchiveMode.Read))
                using (var sha = SHA256.Create())
                {
                    foreach (var e in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(e.Name)) continue;   // a directory entry
                        string name = e.FullName.Replace('/', '\\');
                        PayloadFile f;
                        if (!want.TryGetValue(name, out f) || data.ContainsKey(name))
                            throw Damaged("it holds " + name + ", which its own file list does not name");
                        if (e.Length != f.Size) throw Damaged(name + " is " + e.Length + " bytes, not " + f.Size);
                        byte[] bytes = new byte[f.Size];
                        using (var s = e.Open())
                        {
                            int off = 0, n;
                            while (off < bytes.Length && (n = s.Read(bytes, off, bytes.Length - off)) > 0) off += n;
                            if (off != bytes.Length || s.ReadByte() != -1) throw Damaged(name + " does not unpack to its listed size");
                        }
                        string got = Hex(sha.ComputeHash(bytes));
                        if (!string.Equals(got, f.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw Damaged(name + " has SHA-256 " + got + ", expected " + f.Sha256);
                        data[name] = bytes;
                    }
                }
            }
            foreach (var f in files)
                if (!data.ContainsKey(f.Name)) throw Damaged("it is missing " + f.Name);
            return data;
        }

        /// <summary>The embedded uninstall.exe, read before the install folder is touched: a folder left without one
        /// would not count as an installed copy, and no later Setup could upgrade or remove it.</summary>
        internal static byte[] Uninstaller()
        {
            using (var rs = Assembly.GetExecutingAssembly().GetManifestResourceStream(Program.UninstallerName))
            {
                if (rs == null) throw Damaged("it carries no " + Program.UninstallerName);
                var bytes = new byte[rs.Length];
                int off = 0, n;
                while (off < bytes.Length && (n = rs.Read(bytes, off, bytes.Length - off)) > 0) off += n;
                if (off != bytes.Length || bytes.Length == 0) throw Damaged(Program.UninstallerName + " is truncated");
                return bytes;
            }
        }

        private static InvalidDataException Damaged(string what)
        {
            return new InvalidDataException("This Setup is damaged (" + what + "). Download it again.");
        }

        internal static void Copy(Stream from, Stream to)
        {
            var buf = new byte[81920];
            int n;
            while ((n = from.Read(buf, 0, buf.Length)) > 0) to.Write(buf, 0, n);
        }

        internal static string Hex(byte[] h)
        {
            var sb = new StringBuilder(h.Length * 2);
            foreach (var b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    /// <summary>Is the app running from a folder? Finestra is never killed - closing it (and the RDP sessions it
    /// started) is the user's call. The engine counts too: an orphaned wfreerdp.exe keeps its image locked, and an
    /// install over it would leave a folder with one old engine in it.</summary>
    internal static class InUse
    {
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        private static string ImagePath(Process p)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                if (h == IntPtr.Zero) return null;
                var sb = new StringBuilder(1024); int n = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null;
            }
            catch { return null; }
            finally { if (h != IntPtr.Zero) CloseHandle(h); }
        }

        /// <summary>A running image cannot be opened for writing. This also catches a copy started through a
        /// junction, subst or mapped drive, whose reported image path doesn't start with the folder.</summary>
        private static bool Locked(string exe)
        {
            if (!File.Exists(exe)) return false;
            try { using (new FileStream(exe, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) { } return false; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (IOException) { return true; }        // sharing violation: something has it mapped
            catch { return false; }                      // access denied etc.: not evidence of a running copy
        }

        /// <summary>Null when nothing of ours runs from <paramref name="dir"/>, else a sentence saying what does. A
        /// process whose path cannot be read (an elevated copy) counts as possibly running from there.</summary>
        internal static string Check(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            string full;
            try { full = Path.GetFullPath(dir).TrimEnd('\\') + "\\"; } catch { return null; }

            if (Locked(Path.Combine(full, Program.ExeName))) return Program.AppName + " is running from " + dir + ".";
            try
            {
                string eng = Path.Combine(full, "engine");
                if (Directory.Exists(eng))
                    foreach (var d in Directory.GetDirectories(eng))
                        foreach (var exe in Program.EngineExeNames)
                            if (Locked(Path.Combine(d, exe)))
                                return "An RDP session (" + exe + ") is still running from " + dir + ".";
            }
            catch { }

            bool unknown = false;
            int self = Process.GetCurrentProcess().Id;
            var names = new List<string> { Program.ProcessName };
            foreach (var exe in Program.EngineExeNames) names.Add(Path.GetFileNameWithoutExtension(exe));
            foreach (var name in names)
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (p.Id == self) continue;
                        string img = ImagePath(p);
                        if (img == null) unknown = true;
                        else if (img.StartsWith(full, StringComparison.OrdinalIgnoreCase))
                            return (Program.Eq(name, Program.ProcessName) ? Program.AppName + " is" : "An RDP session (" + name + ".exe) is still")
                                + " running from " + dir + ".";
                    }
                    finally { p.Dispose(); }
                }
            return unknown ? Program.AppName + " is running (possibly from " + dir + ")." : null;
        }
    }

    /// <summary>
    /// Where Setup may write and what it may delete. A folder of the user's - typed into the path box, or given on
    /// the command line - must never be one: drive roots and the well-known shell folders are refused outright, and
    /// an existing non-empty folder is only written into when it is an installed copy (uninstall.exe + Finestra.exe).
    /// </summary>
    internal static class SafeTarget
    {
        internal static string Problem(string dir)
        {
            string full;
            try { full = Path.GetFullPath((dir ?? "").Trim()).TrimEnd('\\'); }
            catch (Exception ex) { return "Not a usable folder: " + ex.Message; }
            if (full.Length == 0) return "Choose an install folder.";
            if (full.IndexOfAny(new[] { '%', '^' }) >= 0) return "The folder name can't contain % or ^.";   // uninstall's cmd would expand them
            string root = (Path.GetPathRoot(full) ?? "").TrimEnd('\\');
            if (root.Length == 0 || root.Equals(full, StringComparison.OrdinalIgnoreCase)) return "Choose a folder for " + Program.AppName + ", not a whole drive.";
            foreach (var f in Protected())
                if (f.Equals(full, StringComparison.OrdinalIgnoreCase))
                    return full + " is a system or personal folder. Choose a folder of its own, e.g. " + Path.Combine(full, Program.InstallFolderName) + ".";
            if (!Directory.Exists(full) || IsEmpty(full) || IsInstalledCopy(full)) return null;
            return full + " already holds other files. Choose an empty folder, or the folder " + Program.AppName + " is installed in.";
        }

        /// <summary>The folder holds an installed Finestra: the program and the uninstall.exe Setup drops next to it.</summary>
        internal static bool IsInstalledCopy(string dir)
        {
            try { return File.Exists(Path.Combine(dir, Program.UninstallerName)) && File.Exists(Path.Combine(dir, Program.ExeName)); }
            catch { return false; }
        }

        private static bool IsEmpty(string dir)
        {
            try { using (var e = Directory.EnumerateFileSystemEntries(dir).GetEnumerator()) return !e.MoveNext(); }
            catch { return false; }
        }

        private static IEnumerable<string> Protected()
        {
            var sf = new[]
            {
                Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.DesktopDirectory,
                Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyPictures,
                Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.Programs, Environment.SpecialFolder.StartMenu,
                Environment.SpecialFolder.Startup, Environment.SpecialFolder.Favorites,
            };
            var list = new List<string>();
            foreach (var f in sf)
            {
                try { string p = Environment.GetFolderPath(f); if (!string.IsNullOrEmpty(p)) list.Add(Path.GetFullPath(p).TrimEnd('\\')); } catch { }
            }
            try
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(profile)) list.Add(Path.Combine(profile, "Downloads"));   // no SpecialFolder for it on .NET 4.x
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(local)) list.Add(Path.Combine(local, "Programs"));
            }
            catch { }
            return list;
        }
    }

    internal static class Installer
    {
        internal static void Install(string targetDir, bool desktopShortcut, Action<string> progress)
        {
            Report(progress, "Preparing...");
            string refused = SafeTarget.Problem(targetDir);
            if (refused != null) throw new InvalidOperationException(refused);
            targetDir = Path.GetFullPath(targetDir.Trim()).TrimEnd('\\');   // one spelling for the files, shortcuts and uninstall entry
            var files = Payload.List();
            if (!files.Exists(f => Program.Eq(f.Name, Program.ExeName))) throw new InvalidDataException("This Setup carries no " + Program.ExeName + ".");

            // Check the whole package BEFORE touching the folder: a damaged download must not take a working install down.
            Report(progress, "Checking the package...");
            var data = Payload.ReadVerified(files);
            byte[] uninstBytes = Payload.Uninstaller();

            // The folder is new, empty or an installed Finestra (SafeTarget). Each file is written beside the old one and
            // then swapped in, so an interrupted install leaves whole files and a working uninstall.exe. Nothing else in
            // the folder is touched - in particular a user-supplied engine\x86\wfreerdp.exe (FIN-X86-UNBUNDLE: the bundle
            // ships only a README there) simply survives, with no copy-out/copy-back step.
            Directory.CreateDirectory(targetDir);
            int i = 0;
            foreach (var f in files)
            {
                Report(progress, "Installing " + (++i) + " of " + files.Count + ": " + f.Name);
                string dest = Path.Combine(targetDir, f.Name);
                string d = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
                Swap(dest, data[f.Name]);
            }
            Owned.RemoveRetired(targetDir, files);   // files only older versions shipped

            string exe = Path.Combine(targetDir, Program.ExeName);
            string uninst = Path.Combine(targetDir, Program.UninstallerName);

            Report(progress, "Creating shortcuts...");
            Swap(uninst, uninstBytes);   // the payload-free uninstaller (until 2.0.0 this was a full copy of Setup)
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            Shortcut.Create(Path.Combine(programs, Program.AppName + ".lnk"), exe, null, targetDir, exe, Program.ShortcutDesc);
            if (desktopShortcut)
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                Shortcut.Create(Path.Combine(desktop, Program.AppName + ".lnk"), exe, null, targetDir, exe, Program.ShortcutDesc);
            }

            Report(progress, "Registering...");
            long bytes = 0;
            foreach (var f in files) bytes += f.Size;
            try { bytes += new FileInfo(uninst).Length; } catch { }
            RegisterUninstall(targetDir, exe, uninst, bytes / 1024);
            Report(progress, "Done.");
        }

        private static void RegisterUninstall(string dir, string exe, string uninst, long sizeKB)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Program.UninstallKey))
                {
                    if (k == null) return;
                    k.SetValue("DisplayName", Program.AppName);
                    k.SetValue("DisplayVersion", Program.AppVersion);
                    k.SetValue("Publisher", Program.Publisher);
                    k.SetValue("DisplayIcon", exe);
                    k.SetValue("InstallLocation", dir);
                    k.SetValue("UninstallString", "\"" + uninst + "\" --uninstall \"" + dir + "\"");
                    k.SetValue("QuietUninstallString", "\"" + uninst + "\" --uninstall \"" + dir + "\" --silent");
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    k.SetValue("EstimatedSize", (int)sizeKB, RegistryValueKind.DWord);   // what Setup put there, not the user's files
                }
            }
            catch { /* uninstall entry is best-effort */ }
        }

        /// <summary>Writes beside the old file, then swaps it in, so an interrupted install leaves whole files.</summary>
        private static void Swap(string dest, byte[] bytes)
        {
            string tmp = dest + ".new";
            try
            {
                File.WriteAllBytes(tmp, bytes);
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }   // don't leave a half step behind
                throw;
            }
        }

        private static void Report(Action<string> p, string s) { if (p != null) p(s); }
    }

    /// <summary>The files Setup owns in an install folder: this package's (payload.list, which uninstall.exe carries
    /// too), uninstall.exe, and what earlier releases shipped. Upgrade and uninstall remove only these, so anything
    /// else in the folder - files the user keeps there, a Finestra.portable they dropped in - stays.</summary>
    internal static class Owned
    {
        // Every name a release has put in the install folder: 1.0.2 and 1.0.3 shipped exactly this set (checked
        // against both shipped zips, 2026-10-21), and the release after them added Microsoft.Bcl.Cryptography.dll
        // (an SSH.NET 2026.0.0 dependency). An upgrade removes the ones this package no longer carries, so a
        // dependency dropped in a later release does not linger; uninstall removes all of them. Add new names here
        // when they first ship.
        // ⚠ engine\x86\wfreerdp.exe is deliberately NOT here. 1.0.2 shipped it, but since FIN-X86-UNBUNDLE that
        // slot is where a 32-bit user drops their OWN engine, and nothing tells the two copies apart. Leaving it
        // is the safe direction: at worst a 1.0.2 engine stays behind, and the folder with it.
        private static readonly string[] Shipped =
        {
            "Finestra.exe", "Finestra.exe.config", "LICENSE", "THIRD-PARTY-NOTICES.txt", "FREERDP-MODIFICATIONS.txt",
            "README.txt", "BUILD-INFO.txt",
            "BouncyCastle.Cryptography.dll", "FluentFTP.dll", "Microsoft.Bcl.AsyncInterfaces.dll", "Microsoft.Bcl.Cryptography.dll",
            "Microsoft.Extensions.DependencyInjection.Abstractions.dll", "Microsoft.Extensions.Logging.Abstractions.dll",
            "Newtonsoft.Json.dll", "Renci.SshNet.dll", "System.Buffers.dll", "System.Diagnostics.DiagnosticSource.dll",
            "System.Formats.Asn1.dll", "System.Memory.dll", "System.Numerics.Vectors.dll",
            "System.Runtime.CompilerServices.Unsafe.dll", "System.Threading.Tasks.Extensions.dll", "System.ValueTuple.dll",
            "VtNetCore.dll",
            @"engine\x64\wfreerdp.exe", @"engine\arm\wfreerdp.exe", @"engine\x86\README.txt",
            // SDL-ENGINE — the release after 1.0.3 replaced wfreerdp.exe with the SDL client. The two wfreerdp
            // names above STAY: they are how an upgrade removes the engine 1.0.2/1.0.3 left in x64 and arm.
            // Never an x86 engine name here, under either name: that slot is the user's (see the note above).
            @"engine\x64\sdl-freerdp.exe", @"engine\arm\sdl-freerdp.exe",
        };

        internal static void RemoveRetired(string dir, List<PayloadFile> files)
        {
            foreach (var name in Shipped)
                if (!files.Exists(f => Program.Eq(f.Name, name))) Del(Path.Combine(dir, name));
        }

        internal static void RemoveAll(string dir, List<PayloadFile> files)
        {
            var names = new List<string>();
            foreach (var f in files) names.Add(f.Name);
            names.AddRange(Shipped);
            foreach (var n in names) { Del(Path.Combine(dir, n)); Del(Path.Combine(dir, n + ".new")); }   // .new: an install cut off mid-swap
            foreach (var sub in DirsOf(names)) { try { Directory.Delete(Path.Combine(dir, sub), false); } catch { } }   // only if empty now
        }

        /// <summary>The folders the given names live in, deepest first (engine\x64 before engine).</summary>
        private static List<string> DirsOf(List<string> names)
        {
            var seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            foreach (var n in names)
                for (string d = Path.GetDirectoryName(n); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
                    if (!seen.ContainsKey(d)) { seen[d] = true; list.Add(d); }
            list.Sort((a, b) => b.Split('\\').Length.CompareTo(a.Split('\\').Length));
            return list;
        }

        private static void Del(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
    }

    internal static class Uninstaller
    {
        internal static bool Run(string dir)
        {
            if (!SafeTarget.IsInstalledCopy(dir)) return false;   // never touch a folder that is not an installed Finestra
            // OWNERSHIP: the shortcut names, the uninstall key and the Run values are fixed, so another install of the
            // app (a copy in another folder) may own them. Remove only what points INTO the folder being removed.
            RemoveShortcutIfOurs(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Program.AppName + ".lnk"), dir);
            RemoveShortcutIfOurs(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Program.AppName + ".lnk"), dir);
            RemoveUninstallKeyIfOurs(dir);
            RemoveRunValuesIfOurs(dir);
            Owned.RemoveAll(dir, Payload.List());
            SelfDeleteDir(dir);   // uninstall.exe and the then-empty folder last (uninstall.exe may be running from it)
            return true;
        }

        // POLICY: PRESERVE the user's data — Documents\Finestra (connections.json, known_hosts.json,
        // known_certs.json, settings.json, logs) is NEVER touched by uninstall.

        /// <summary>True when <paramref name="path"/> is <paramref name="dir"/> or inside it - compared
        /// separator-terminated, so ...\Finestra-Private is NOT inside ...\Finestra. Unreadable or empty counts as
        /// NOT ours: leaving a stale entry is recoverable, deleting another install's is not.</summary>
        internal static bool Inside(string path, string dir)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(dir)) return false;
            try
            {
                string d = Path.GetFullPath(dir.Trim().Trim('"')).TrimEnd('\\');
                string p = Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\');
                return p.Equals(d, StringComparison.OrdinalIgnoreCase) || p.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>The program path of a command line: a quoted first token, or everything before " -" / " /".</summary>
        internal static string ExeOf(string command)
        {
            command = (command ?? "").Trim();
            if (command.StartsWith("\""))
            {
                int q = command.IndexOf('"', 1);
                return q > 0 ? command.Substring(1, q - 1) : command.Trim('"');
            }
            int cut = command.IndexOf(" -", StringComparison.Ordinal);
            if (cut < 0) cut = command.IndexOf(" /", StringComparison.Ordinal);
            return cut > 0 ? command.Substring(0, cut) : command;
        }

        /// <summary>A shortcut whose target can't be read, or that can't be deleted, is tried again for about 2 s:
        /// the installer test once saw ours survive an uninstall (not reproduced in isolation), and a .lnk the shell
        /// has just written may be held open for a moment. A readable target outside the folder ends it at once.</summary>
        private static void RemoveShortcutIfOurs(string lnk, string dir)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (!File.Exists(lnk)) return;
                    string target = Shortcut.TargetOf(lnk);
                    if (target != null)
                    {
                        if (!Inside(target, dir)) return;   // another copy's shortcut: keep it
                        File.Delete(lnk);
                        return;
                    }
                }
                catch { }
                Thread.Sleep(200);
            }
        }

        private static void RemoveUninstallKeyIfOurs(string dir)
        {
            try
            {
                string loc;
                using (var k = Registry.CurrentUser.OpenSubKey(Program.UninstallKey))
                    loc = k != null ? k.GetValue("InstallLocation") as string : null;
                if (Inside(loc, dir) && Inside(dir, loc)) Registry.CurrentUser.DeleteSubKeyTree(Program.UninstallKey);
            }
            catch { }
        }

        // The app's own "Run at Windows startup" toggle writes HKCU Run\Finestra = "<exe>" /tray; left behind it
        // would point at the exe being deleted. The value NAME is the same whichever build or copy wrote it, so only
        // ONE install owns it - delete it only when its exe is inside this folder.
        private static void RemoveRunValuesIfOurs(string dir)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Program.RunKey, true))
                {
                    if (k == null) return;
                    // "FinestraRDP" is the pre-1.0 name. This literal is a compatibility contract with pre-1.0
                    // installs and must never be renamed.
                    foreach (var name in new[] { "Finestra", "FinestraRDP" })
                    {
                        string cmd = k.GetValue(name) as string;
                        if (cmd != null && Inside(ExeOf(cmd), dir)) k.DeleteValue(name, false);
                    }
                }
            }
            catch { }
        }

        private static void SelfDeleteDir(string dir)
        {
            try
            {
                // uninstall.exe may be running from inside `dir`, so a detached cmd waits for it to exit (with a second try
                // for a slow device), deletes it, and removes the folder only if it is empty now: rmdir without /s never
                // deletes what isn't ours. Its working folder is System32, never `dir` (a process's current folder can't
                // be removed).
                string u = Path.Combine(dir, Program.UninstallerName);
                var psi = new ProcessStartInfo("cmd.exe",
                    "/c ping 127.0.0.1 -n 4 >nul & del /f /q \"" + u + "\" & ping 127.0.0.1 -n 4 >nul & del /f /q \"" + u + "\" & rmdir \"" + dir + "\"");
                psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
                psi.CreateNoWindow = true; psi.UseShellExecute = false; psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
            }
            catch { }
        }
    }

    /// <summary>Creates and reads .lnk files via the shell's IShellLink COM object (works on RT desktop).</summary>
    internal static class Shortcut
    {
        internal static void Create(string lnkPath, string target, string args, string workDir, string iconPath, string desc)
        {
            try
            {
                var link = (IShellLinkW)new CShellLink();
                link.SetPath(target);
                if (!string.IsNullOrEmpty(args)) link.SetArguments(args);
                if (!string.IsNullOrEmpty(workDir)) link.SetWorkingDirectory(workDir);
                if (!string.IsNullOrEmpty(iconPath)) link.SetIconLocation(iconPath, 0);
                if (!string.IsNullOrEmpty(desc)) link.SetDescription(desc);
                ((IPersistFile)link).Save(lnkPath, true);
            }
            catch { /* shortcut is best-effort */ }
        }

        /// <summary>The target path a .lnk points at, or null when it cannot be read.</summary>
        internal static string TargetOf(string lnkPath)
        {
            try
            {
                var link = (IShellLinkW)new CShellLink();
                ((IPersistFile)link).Load(lnkPath, 0);
                var sb = new StringBuilder(1024);
                link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                return sb.Length > 0 ? sb.ToString() : null;
            }
            catch { return null; }
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class CShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }
    }

    internal sealed class InstallForm : Form
    {
        private readonly TextBox _path;
        private readonly CheckBox _desktop;
        private readonly Button _install, _cancel, _browse;
        private readonly Label _status;
        private readonly ProgressBar _bar;
        private bool _busy;
        private static readonly Color Accent = Color.FromArgb(178, 40, 160); // Finestra magenta

        public InstallForm()
        {
            Text = Program.AppName + " " + Program.AppVersion + " Setup";
            try { using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("FinestraSetup.icon.ico")) if (s != null) Icon = new Icon(s); } catch { }
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(480, 300);
            BackColor = Color.White;
            Font = new Font("Segoe UI", 9f);

            var header = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Accent };
            var title = new Label { Dock = DockStyle.Fill, ForeColor = Color.White, Font = new Font("Segoe UI", 14f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(18, 0, 0, 0), Text = "Install " + Program.AppName + " " + Program.AppVersion };
            header.Controls.Add(title);

            var lbl = new Label { Left = 18, Top = 88, Width = 444, Height = 18, Text = "Install location (per-user, no admin needed):" };
            _path = new TextBox { Left = 18, Top = 110, Width = 350, Text = Program.DefaultDir() };
            _browse = new Button { Left = 378, Top = 108, Width = 84, Height = 26, Text = "Browse..." };
            _browse.Click += (s, e) => { using (var d = new FolderBrowserDialog()) { try { d.SelectedPath = _path.Text; } catch { } if (d.ShowDialog(this) == DialogResult.OK) _path.Text = Path.Combine(d.SelectedPath, Program.InstallFolderName); } };
            _desktop = new CheckBox { Left = 18, Top = 148, Width = 300, Text = "Create a desktop shortcut", Checked = true };
            _status = new Label { Left = 18, Top = 194, Width = 444, Height = 18, ForeColor = Color.Gray, Text = "" };
            _bar = new ProgressBar { Left = 18, Top = 216, Width = 444, Height = 14, Style = ProgressBarStyle.Marquee, Visible = false, MarqueeAnimationSpeed = 30 };
            _install = new Button { Left = 300, Top = 256, Width = 84, Height = 28, Text = "Install" };
            _cancel = new Button { Left = 388, Top = 256, Width = 74, Height = 28, Text = "Cancel" };
            _install.Click += OnInstall;
            _cancel.Click += (s, e) => Close();

            Controls.Add(header);
            Controls.Add(lbl); Controls.Add(_path); Controls.Add(_browse); Controls.Add(_desktop);
            Controls.Add(_status); Controls.Add(_bar); Controls.Add(_install); Controls.Add(_cancel);
            AcceptButton = _install; CancelButton = _cancel;

            // The title-bar X must not end the process mid-install (the worker thread is a background thread).
            // Finished() clears _busy FIRST, so the Close() it calls - and every X after it - goes through.
            FormClosing += (s, e) => { if (_busy && e.CloseReason == CloseReason.UserClosing) e.Cancel = true; };
        }

        private void OnInstall(object sender, EventArgs e)
        {
            string dir = (_path.Text ?? "").Trim();
            if (string.IsNullOrEmpty(dir)) { MessageBox.Show(this, "Choose an install location."); return; }
            string refused = SafeTarget.Problem(dir);
            if (refused != null) { MessageBox.Show(this, refused, "Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            string busy;
            while ((busy = InUse.Check(dir)) != null)
                if (MessageBox.Show(this, busy + "\n\nClose " + Program.AppName + " and its sessions, then press Retry.", "Setup",
                    MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) != DialogResult.Retry) return;

            bool desktop = _desktop.Checked;
            SetBusy(true);
            var t = new Thread(() =>
            {
                Exception err = null;
                try { Installer.Install(dir, desktop, msg => { try { BeginInvoke((Action)(() => _status.Text = msg)); } catch { } }); }
                catch (Exception ex) { err = ex; }
                try { BeginInvoke((Action)(() => Finished(dir, err))); } catch { }
            }) { IsBackground = true };
            t.Start();
        }

        private void Finished(string dir, Exception err)
        {
            // First thing: the install is over, so the close guard must stop blocking. (CS-Ray shipped this guard with
            // the flag still set here, which cancelled the Close() below and every click on the X after it.)
            SetBusy(false);
            if (err != null)
            {
                MessageBox.Show(this, "Install failed:\n\n" + err.Message, "Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (MessageBox.Show(this, Program.AppName + " was installed.\n\nLaunch it now?", "Setup",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                try { Process.Start(Path.Combine(Program.Norm(dir) ?? dir, Program.ExeName)); } catch { }
            Close();
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _install.Enabled = _cancel.Enabled = _browse.Enabled = _path.Enabled = _desktop.Enabled = !busy;
            _bar.Visible = busy;
        }
    }
}
