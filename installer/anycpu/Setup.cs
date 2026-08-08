// =============================================================================
//  Finestra AnyCPU installer  (Setup.cs)
//
//  A .NET Framework 4.7 AnyCPU/MSIL installer, so it RUNS ON WINDOWS RT 8.1 (ARM32)
//  and Windows 10 ARM32 — unlike an Inno Setup installer, whose loader (SetupLdr) is
//  native x86 and physically cannot launch on RT (no x86 emulation). Ships next to
//  "payload.zip" (the app in its FLAT deploy layout: managed DLLs + .config +
//  engine\<arch>\wfreerdp.exe beside the exe - x64 and arm are bundled; engine\x86\
//  ships with only a README, and a user-supplied engine there is preserved across
//  upgrades by Installer.Install).
//
//  Pattern MIRRORED from CS-Ray's installer/anycpu/Setup.cs (the proven RT-capable
//  ship vehicle, same author): per-user %LocalAppData%\Programs\<App>, IShellLink COM
//  shortcuts, HKCU Uninstall entry, uninstall.exe self-copy, self-deleting uninstall.
//
//  Modes:
//    (no args)              -> install wizard UI
//    --silent [dir]         -> install to dir (default per-user) with no UI (scriptable)
//    --uninstall <dir>      -> remove install dir + shortcuts + registry (preserve user data)
//    (run as uninstall.exe) -> confirm, then uninstall its own folder
//
//  Installs per-user to %LocalAppData%\Programs\Finestra by default (NO elevation,
//  asInvoker). The app write-probes its data to Documents\Finestra (never beside the
//  exe), so neither install nor launch needs admin — exactly what the RT device needs.
//  UNINSTALL NEVER TOUCHES Documents\Finestra (connections, host keys, certs, settings).
// =============================================================================
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
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
        // A PRIVATE build is a SEPARATE PRODUCT, not a variant of the public one. This installer WIPES
        // its target directory (Installer.TryDeleteDir) and DELETES its uninstall key, so if the two
        // shared an identity a private install would silently replace the public app, and uninstalling
        // either would take the other with it. Diverging the identity here makes that impossible by
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
        internal const string AppVersion = "1.0.3";
        internal const string Publisher = "Hamed Ghorbani";
        internal const string ExeName = "Finestra.exe";
        internal const string PayloadName = "payload.zip";

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length >= 1 && Eq(args[0], "--uninstall"))
                {
                    string dir = args.Length >= 2 ? args[1] : Path.GetDirectoryName(Application.ExecutablePath);
                    return Uninstaller.Run(dir) ? 0 : 1;
                }
                if (args.Length >= 1 && Eq(args[0], "--silent"))
                {
                    string dir = args.Length >= 2 ? args[1] : DefaultDir();
                    Installer.Install(dir, true, null);
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // Double-clicking the dropped uninstall.exe (no args) -> confirm + uninstall its own folder.
                if (Eq(Path.GetFileNameWithoutExtension(Application.ExecutablePath), "uninstall"))
                {
                    if (MessageBox.Show("Remove " + AppName + " from this computer?\n\n(Your connections, host keys and settings in Documents\\Finestra are kept.)",
                        "Uninstall " + AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        Uninstaller.Run(Path.GetDirectoryName(Application.ExecutablePath));
                    return 0;
                }

                Application.Run(new InstallForm());
                return 0;
            }
            catch (Exception ex)
            {
                try { MessageBox.Show(ex.ToString(), AppName + " Setup - error", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
                return 1;
            }
        }

        internal static bool Eq(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        internal static string DefaultDir()
        {
            string p = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(p, "Programs", InstallFolderName);   // folder-safe name, not the display name
        }
    }

    internal static class Installer
    {
        /// <summary>The one install-dir file that may be user-supplied: since FIN-X86-UNBUNDLE the bundle
        /// ships engine\x86\ with only a README, so a 32-bit user adds their own engine here. Preserved
        /// across the install-dir wipe. Must stay in sync with RdpLauncher.EngineCandidates.</summary>
        private const string X86EngineRelPath = @"engine\x86\wfreerdp.exe";

        internal static void Install(string targetDir, bool desktopShortcut, Action<string> progress)
        {
            Report(progress, "Preparing...");
            string payload = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), Program.PayloadName);
            if (!File.Exists(payload))
                throw new FileNotFoundException("Installer payload not found next to Setup.exe:\n" + payload
                    + "\n\nKeep Setup.exe and payload.zip together.");

            // engine\x86\wfreerdp.exe is the ONE user-supplied file that can live in the install dir: since
            // FIN-X86-UNBUNDLE the bundle ships only a README in that folder, and a user on 32-bit Windows
            // is invited to drop their own 32-bit engine there. The wipe below would silently delete it on
            // every upgrade. Carry it across, and restore it AFTER extraction only if the payload did not
            // supply one (a bundled engine always wins).
            // BEST-EFFORT BY DESIGN: every step is swallowed. Losing a re-droppable 6 MB file must never
            // fail an install - and the shipped engine\x86\README.txt tells the user to keep their own copy
            // outside the install folder, which is what covers the case where this preserve fails.
            string preservedX86 = null;
            try
            {
                string existingX86 = Path.Combine(targetDir, X86EngineRelPath);
                if (File.Exists(existingX86))
                {
                    preservedX86 = Path.Combine(Path.GetTempPath(), "finestra_x86_" + Guid.NewGuid().ToString("N") + ".bin");
                    File.Copy(existingX86, preservedX86, true);
                    Report(progress, "Preserving your x86 engine...");
                }
            }
            catch { preservedX86 = null; }   // could not copy it out - proceed with the install regardless

            // Fresh install of the BINARIES only. User data lives in Documents\Finestra and is never in the
            // install dir, so wiping the target dir is safe (and it's how a re-install upgrades in place).
            // The single exception is the user-supplied x86 engine handled immediately above/below.
            if (Directory.Exists(targetDir)) { Report(progress, "Removing previous version..."); TryDeleteDir(targetDir); }
            Directory.CreateDirectory(targetDir);

            Report(progress, "Extracting files...");
            using (var fs = new FileStream(payload, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries)
                {
                    string dest = Path.Combine(targetDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(dest); continue; }  // dir entry
                    string d = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
                    using (var es = entry.Open())
                    using (var os = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
                        es.CopyTo(os);
                }
            }

            // Restore the user's x86 engine - ONLY if the payload did not ship one, so a bundled engine
            // always wins over a carried-over copy. Swallowed end to end; the temp copy is always cleaned up.
            if (preservedX86 != null)
            {
                try
                {
                    string dest = Path.Combine(targetDir, X86EngineRelPath);
                    if (!File.Exists(dest))
                    {
                        string destDir = Path.GetDirectoryName(dest);
                        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                        File.Copy(preservedX86, dest, false);
                        Report(progress, "Restored your x86 engine.");
                    }
                }
                catch { }
                finally { try { File.Delete(preservedX86); } catch { } }
            }

            string exe = Path.Combine(targetDir, Program.ExeName);
            string uninst = Path.Combine(targetDir, "uninstall.exe");

            Report(progress, "Creating shortcuts...");
            File.Copy(Application.ExecutablePath, uninst, true);   // uninstall.exe = this Setup (uninstall mode only)
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            Shortcut.Create(Path.Combine(programs, Program.AppName + ".lnk"), exe, null, targetDir, exe, Program.ShortcutDesc);
            if (desktopShortcut)
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                Shortcut.Create(Path.Combine(desktop, Program.AppName + ".lnk"), exe, null, targetDir, exe, Program.ShortcutDesc);
            }

            Report(progress, "Registering...");
            RegisterUninstall(targetDir, exe, uninst);
            Report(progress, "Done.");
        }

        private static void RegisterUninstall(string dir, string exe, string uninst)
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
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    try { k.SetValue("EstimatedSize", (int)DirSizeKB(dir), RegistryValueKind.DWord); } catch { }
                }
            }
            catch { /* uninstall entry is best-effort */ }
        }

        private static long DirSizeKB(string dir)
        {
            long total = 0;
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                try { total += new FileInfo(f).Length; } catch { }
            return total / 1024;
        }

        internal static void TryDeleteDir(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }
        private static void Report(Action<string> p, string s) { if (p != null) p(s); }
    }

    internal static class Uninstaller
    {
        internal static bool Run(string dir)
        {
            RemoveShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Program.AppName + ".lnk"));
            RemoveShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Program.AppName + ".lnk"));
            try { Registry.CurrentUser.DeleteSubKeyTree(Program.UninstallKey, false); } catch { }
            RemoveAppGeneratedState(dir);
            SelfDeleteDir(dir);   // remove the install dir last (uninstall.exe runs from inside it)
            return true;
        }

        // POLICY: PRESERVE the user's data — Documents\Finestra (connections.json, known_hosts.json,
        // known_certs.json, settings.json, logs) is NEVER touched by uninstall. Remove only clearly
        // app-generated, disposable machine state.
        private static void RemoveAppGeneratedState(string dir)
        {
            // Run-on-startup HKCU Run values — they point at the exe we're deleting, so leaving them is a
            // broken startup entry. (The app's own toggle created them; this is the matching cleanup.)
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    // NOTE: these VALUE NAMES are fixed literals, not derived from AppName - the app writes
                    // the same "Finestra" value whichever build created it. So when more than one install
                    // exists, only ONE of them owns the value, and deleting it blind would let uninstalling
                    // one build silently disable another build's run-at-startup. Delete only when the
                    // value's DATA points INSIDE the directory being removed.
                    if (k == null) return;
                    if (OwnedByThisInstall(k.GetValue("Finestra") as string, dir)) k.DeleteValue("Finestra", false);
                    // pre-1.0 name - same ownership test. This literal is a compatibility contract with
                    // pre-1.0 installs and must never be renamed.
                    if (OwnedByThisInstall(k.GetValue("FinestraRDP") as string, dir)) k.DeleteValue("FinestraRDP", false);
                }
            }
            catch { }
        }

        /// <summary>True when a Run value's command line points at an exe inside <paramref name="dir"/>.
        /// The stored value is usually quoted ("C:\...\Finestra.exe"), so compare on a trimmed prefix
        /// rather than parsing a command line. Unreadable or empty data counts as NOT ours: leaving a
        /// stale entry behind is recoverable, deleting another install's is not.</summary>
        private static bool OwnedByThisInstall(string runValue, string dir)
        {
            if (string.IsNullOrEmpty(runValue) || string.IsNullOrEmpty(dir)) return false;
            try
            {
                string cmd = runValue.Trim().Trim('"');
                string full = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
                return cmd.StartsWith(full, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void RemoveShortcut(string lnk) { try { if (File.Exists(lnk)) File.Delete(lnk); } catch { } }

        private static void SelfDeleteDir(string dir)
        {
            try
            {
                // uninstall.exe lives inside `dir` and is running -> it can't delete itself. Launch a detached cmd
                // that waits ~2s for this process to exit, then removes the whole dir (incl. uninstall.exe).
                var psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + dir + "\"");
                psi.CreateNoWindow = true; psi.UseShellExecute = false; psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
            }
            catch { }
        }
    }

    /// <summary>Creates a .lnk via the shell's IShellLink COM object (works on RT desktop).</summary>
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
        }

        private async void OnInstall(object sender, EventArgs e)
        {
            string dir = (_path.Text ?? "").Trim();
            if (string.IsNullOrEmpty(dir)) { MessageBox.Show(this, "Choose an install location."); return; }
            bool desktop = _desktop.Checked;
            SetBusy(true);
            try
            {
                await Task.Run(() => Installer.Install(dir, desktop, msg => { try { BeginInvoke((Action)(() => _status.Text = msg)); } catch { } }));
                _bar.Visible = false;
                if (MessageBox.Show(this, Program.AppName + " was installed.\n\nLaunch it now?", "Setup",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    try { Process.Start(Path.Combine(dir, Program.ExeName)); } catch { }
                Close();
            }
            catch (Exception ex)
            {
                _bar.Visible = false;
                MessageBox.Show(this, "Install failed:\n\n" + ex.Message, "Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetBusy(false);
            }
        }

        private void SetBusy(bool busy)
        {
            _install.Enabled = _cancel.Enabled = _browse.Enabled = _path.Enabled = _desktop.Enabled = !busy;
            _bar.Visible = busy;
        }
    }
}
