using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using Finestra.Helpers;

namespace Finestra.Core
{
    /// <summary>Result of a launch attempt: the process (or null) plus the exact arg string used (password-free
    /// when /from-stdin, redacted otherwise) so it can be logged and shown.</summary>
    public sealed class LaunchResult
    {
        public Process Process;
        public string ArgsForDisplay;   // safe to show/log (no password when UsedStdin)
        public bool UsedStdin;
        public string WfreerdpPath;
        public string Error;
        public bool Ok => Process != null && Error == null;
    }

    /// <summary>
    /// Assembles the wfreerdp.exe command line from a <see cref="ConnectionProfile"/> and launches it.
    ///
    /// TWO principles:
    ///  1. MINIMAL: a flag is emitted ONLY when the value differs from wfreerdp's own default (toggles) or is
    ///     an explicit non-Default choice (value-options). The one curated default is the display resolution
    ///     (detected primary). This keeps the command line short and never overrides wfreerdp needlessly.
    ///  2. SECURE CREDENTIALS: the password is fed via /from-stdin (piped to StandardInput) — it is NEVER on
    ///     the command line, so it can't be seen in Task Manager / the process list, and the arg string is
    ///     safe to log verbatim. /p (password on cmdline, visible) exists only as an explicit fallback.
    ///
    /// wfreerdp is launched either EMBEDDED (a WS_CHILD of a caller HWND via /parent-window — the chromeless
    /// SessionHost, fullscreen or windowed) or in its OWN window (shell-out), per the caller. The engine binary
    /// is selected DETERMINISTICALLY by detected CPU arch from the engine\&lt;arch&gt;\ bundle, PE-machine-verified.
    /// </summary>
    public static class RdpLauncher
    {
        /// <summary>
        /// The exact session size wfreerdp will be given (<c>/w</c> × <c>/h</c>), portrait swap applied.
        /// <paramref name="nativeW"/>/<paramref name="nativeH"/> supply <see cref="ResolutionMode.Native"/> — the
        /// caller decides what "native" means for its presentation (the monitor for a fullscreen host, the
        /// embed area for a windowed one). ONE source of truth: the session host sizes its window from this and
        /// <see cref="BuildTokens"/> emits from this, so the window and the session can never disagree.
        /// </summary>
        public static Size ResolveEmitSize(SettingsProfile s, int nativeW, int nativeH)
        {
            s = s ?? new SettingsProfile();
            if (s.ResolutionMode == ResolutionMode.Native || s.Width <= 0 || s.Height <= 0)
                return new Size(nativeW, nativeH);
            int w = s.Width, h = s.Height;
            if (s.Portrait) { int t = w; w = h; h = t; }
            return new Size(w, h);
        }

        /// <summary>Builds the ordered arg tokens (no password). <paramref name="useStdin"/> adds /from-stdin;
        /// otherwise the caller appends /p separately (fallback).</summary>
        public static List<string> BuildTokens(ConnectionProfile cp, int primaryW, int primaryH, bool useStdin, IntPtr parentWindow = default(IntPtr))
        {
            var t = new List<string>();
            var s = cp.Settings ?? new SettingsProfile();
            bool embed = parentWindow != IntPtr.Zero;

            // ── target ──
            t.Add("/v:" + cp.Host);
            if (cp.Port > 0 && cp.Port != 3389) t.Add("/port:" + cp.Port);
            if (!string.IsNullOrWhiteSpace(cp.Username)) t.Add("/u:" + cp.Username);
            if (!string.IsNullOrWhiteSpace(cp.Domain)) t.Add("/d:" + cp.Domain);

            // ── display ── When embedding, WE own the window (chromeless child), so wfreerdp's own +f is NEVER
            // emitted: s.Fullscreen instead selects which HOST the session lands in — the borderless
            // screen-filling one or the resizable windowed one (SessionHost). +f only means anything for the
            // shell-out path, where wfreerdp owns its window. Either way the session is sized by /w /h.
            if (s.Fullscreen && !embed)
            {
                t.Add("+f");
            }
            else
            {
                var sz = ResolveEmitSize(s, primaryW, primaryH);
                if (sz.Width > 0 && sz.Height > 0) { t.Add("/w:" + sz.Width); t.Add("/h:" + sz.Height); }
            }
            if (s.MultiMon) t.Add("/multimon");
            if (s.Span) t.Add("+span");

            // ── oversize behaviour: what happens when the host window is LARGER than the session ──
            // Exactly ONE flag — wfreerdp rejects the two together ("Smart sizing and dynamic resolution are
            // mutually exclusive", client/common/cmdline.c:2915). Letterbox emits NOTHING, which is the whole
            // point: the resolution the user chose is never silently overridden.
            switch (s.OversizeMode)
            {
                case OversizeMode.SmartSizing: t.Add("/smart-sizing"); break;
                case OversizeMode.Dynamic: t.Add("+dynamic-resolution"); break;
            }
            string bpp = BppValue(s.ColorDepth);
            if (bpp != null) t.Add("/bpp:" + bpp);

            // ── experience (emit only when differing from wfreerdp default) ──
            if (!s.Wallpaper) t.Add("-wallpaper");
            if (!s.Themes) t.Add("-themes");
            if (!s.FontSmoothing) t.Add("-fonts");
            if (s.MenuAnimations) t.Add("+menu-anims");
            if (s.WindowDrag) t.Add("+window-drag");
            if (s.Aero) t.Add("+aero");
            if (!s.Compression) t.Add("-compression");
            string clvl = CompressionLevelValue(s.CompressionLevel);
            if (clvl != null) t.Add("/compression-level:" + clvl);
            string net = NetworkValue(s.Network);
            if (net != null) t.Add("/network:" + net);
            string gfx = GfxValue(s.Gfx);
            if (gfx != null) t.Add("/gfx:" + gfx);

            // ── connection ──
            if (s.TimeoutMs > 0) t.Add("/timeout:" + s.TimeoutMs);
            if (s.AutoReconnect) t.Add("+auto-reconnect");
            if (s.TrustCertificate) t.Add("/cert:ignore");
            if (!string.IsNullOrWhiteSpace(s.GatewayHost)) t.Add("/gateway:g:" + s.GatewayHost.Trim());

            // ── local resources ──
            string audio = AudioValue(s.Audio);
            if (audio != null) t.Add("/audio-mode:" + audio);
            if (!s.Clipboard) t.Add("-clipboard");
            if (s.Drives) t.Add("+drives");
            if (s.Printer) t.Add("/printer");

            // ── security ──
            string sec = SecurityValue(s.Security);
            if (sec != null) t.Add(sec);

            // ── embed: /parent-window is HONORED by the Windows client (wf_client.c:1492 → EmbeddedWindow →
            // WS_CHILD parented to this HWND). The value is the parent HWND as a uint64. ──
            if (embed) t.Add("/parent-window:" + ((ulong)parentWindow.ToInt64()));

            // ── credentials ──
            if (useStdin) t.Add("/from-stdin");

            return t;
        }

        /// <summary>Launches wfreerdp for the connection. With <paramref name="useStdin"/> the password is piped
        /// to StandardInput (secure); otherwise it is appended as /p (visible fallback). stderr/stdout are teed
        /// to the Finestra log for diagnosis. Never throws — failures come back in <see cref="LaunchResult.Error"/>.</summary>
        public static LaunchResult Launch(ConnectionProfile cp, int primaryW, int primaryH, bool useStdin, IntPtr parentWindow = default(IntPtr))
        {
            var r = new LaunchResult { UsedStdin = useStdin };
            string exe = ResolveWfreerdpPath(out string note);
            r.WfreerdpPath = exe;
            if (!string.IsNullOrEmpty(note)) FileLog.Line("[ENGINE] " + note);
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                r.Error = string.IsNullOrEmpty(note) ? "No RDP engine found for this CPU." : note;
                return r;
            }

            var tokens = BuildTokens(cp, primaryW, primaryH, useStdin, parentWindow);
            string args = JoinArgs(tokens);

            string password = cp.GetPassword();
            if (!useStdin && !string.IsNullOrEmpty(password))
                args += " " + JoinArgs(new List<string> { "/p:" + password });   // fallback: on the command line (visible)

            // display/log string: with stdin the real args carry NO password, so log verbatim; with /p, redact.
            r.ArgsForDisplay = useStdin ? args : RedactPassword(args, password);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardInput = useStdin,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = false,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory
                };
                var proc = new Process { StartInfo = psi };   // FRDP-FIXSWEEP B17 — dropped dead EnableRaisingEvents (no Exited handler; lifecycle is HasExited-polled in ChildTick)
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) FileLog.Line("[wfreerdp] " + e.Data); };
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) FileLog.Line("[wfreerdp] " + e.Data); };

                FileLog.Line("[LAUNCH] " + exe + " " + r.ArgsForDisplay + (useStdin ? "   (password via stdin)" : "   (password via /p)"));
                proc.Start();
                JobGuard.Assign(proc);   // FRDP-FIXSWEEP B1 — child dies with the app (kill-on-job-close backstop)
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                if (useStdin)
                {
                    try
                    {
                        // FreeRDP /from-stdin prompts for the MISSING credentials in order: username, domain,
                        // password. We put /u and /d on the command line when set, so feed only what's missing,
                        // then the password — in that exact order. Write RAW UTF-8 bytes with '\n' line endings
                        // straight to the pipe: this guarantees NO BOM (a StreamWriter preamble would corrupt the
                        // first field) and no '\r' (which would corrupt the password on some servers).
                        var lines = new List<string>();
                        if (string.IsNullOrWhiteSpace(cp.Username)) lines.Add(cp.Username ?? "");
                        if (string.IsNullOrWhiteSpace(cp.Domain)) lines.Add(cp.Domain ?? "");
                        lines.Add(password ?? "");
                        var sb = new StringBuilder();
                        foreach (var l in lines) sb.Append(l).Append('\n');
                        byte[] bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
                        var pipe = proc.StandardInput.BaseStream;
                        pipe.Write(bytes, 0, bytes.Length);
                        pipe.Flush();
                        proc.StandardInput.Close();
                    }
                    catch (Exception ex) { FileLog.Line("[LAUNCH] stdin write failed: " + ex.Message); }
                }

                r.Process = proc;
                return r;
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
                FileLog.Line("[LAUNCH] failed: " + ex);
                return r;
            }
        }

        /// <summary>Resolves the wfreerdp engine path (no diagnostic note).</summary>
        public static string ResolveWfreerdpPath() => ResolveWfreerdpPath(out _);

        /// <summary>
        /// Deterministic engine resolution for the 3-arch bundle. Order:
        ///  1. the user-configured path (honored as-is), else
        ///  2. pick by the DETECTED OS architecture → engine\&lt;arch&gt;\wfreerdp.exe, with the chosen exe's PE
        ///     machine type verified as a GUARD (never launch an arch-mismatched binary).
        /// ARM64 selects the ARM32 engine (runs emulated) with a logged note. No match → null + a themed
        /// "no engine for this CPU" reason in <paramref name="note"/> (surfaced to the user, never a silent fail).
        /// </summary>
        public static string ResolveWfreerdpPath(out string note)
        {
            note = "";
            try
            {
                string configured = AppSettings.Instance.WfreerdpPath;
                if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
            }
            catch { }
            return SelectEngine(DetectArch(), out note);
        }

        /// <summary>The detected OS architecture as an engine-folder key: "x64" | "x86" | "arm64" | "arm" |
        /// "unknown". Env-var based (PROCESSOR_ARCHITEW6432 ?? PROCESSOR_ARCHITECTURE) — no .NET 4.7.1
        /// RuntimeInformation dependency, so it works on Win8.1 / RT. ARCHITEW6432 is set in a WoW64 process
        /// (our 32-bit app on an x64/arm64 OS) and reports the OS arch; otherwise PROCESSOR_ARCHITECTURE.</summary>
        public static string DetectArch()
        {
            string e = (Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432")
                        ?? Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "").ToUpperInvariant();
            switch (e)
            {
                case "AMD64": return "x64";
                case "X86": return "x86";
                case "ARM64": return "arm64";
                case "ARM": return "arm";
                default: return "unknown";
            }
        }

        /// <summary>Deterministic pick for a given arch key: engine\&lt;sub&gt;\wfreerdp.exe (bundle) + dev-tree
        /// fallbacks, with the returned exe's PE machine type verified to match. Public + string-keyed so the
        /// selector is testable with forced arch values. Null + a reason in <paramref name="note"/> when no match.</summary>
        public static string SelectEngine(string arch, out string note)
        {
            note = "";
            string sub;
            ushort[] expect;
            switch (arch)
            {
                case "x64": sub = "x64"; expect = new ushort[] { 0x8664 }; break;
                case "x86": sub = "x86"; expect = new ushort[] { 0x14c }; break;
                case "arm": sub = "arm"; expect = new ushort[] { 0x1c0, 0x1c4 }; break;
                case "arm64": sub = "arm"; expect = new ushort[] { 0x1c0, 0x1c4 };
                    note = "ARM64 CPU → using the ARM32 engine (emulated); a native arm64 engine is future work. "; break;
                default: note = "No RDP engine for this CPU (unknown architecture '" + arch + "')."; return null;
            }

            string baseDir = "";
            try { baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); } catch { }
            if (string.IsNullOrEmpty(baseDir)) baseDir = AppContext.BaseDirectory ?? "";

            foreach (var rel in EngineCandidates(sub))
            {
                try
                {
                    string p = Path.GetFullPath(Path.Combine(baseDir, rel));
                    if (!File.Exists(p)) continue;
                    ushort m = PeMachine(p);
                    if (Array.IndexOf(expect, m) >= 0) return p;                       // PE-machine guard
                    FileLog.Line("[ENGINE] arch-mismatch, skipping " + p + " (machine=0x" + m.ToString("X") + ")");
                }
                catch { }
            }
            note += "No RDP engine for this CPU — expected engine\\" + sub + "\\wfreerdp.exe beside the app.";
            return null;
        }

        /// <summary>engine\&lt;sub&gt;\wfreerdp.exe is the deploy bundle layout; the ..\Freerdp\ entries are dev-tree
        /// fallbacks so a DEBUG run from bin\Debug\ finds the repo build outputs.</summary>
        private static IEnumerable<string> EngineCandidates(string sub)
        {
            yield return @"engine\" + sub + @"\wfreerdp.exe";
            if (sub == "x64") yield return @"..\..\..\..\Freerdp\build-x64\client\Windows\cli\wfreerdp.exe";
            if (sub == "x86") yield return @"..\..\..\..\Freerdp\build-x86\client\Windows\cli\wfreerdp.exe";
            if (sub == "arm") yield return @"..\..\..\..\Freerdp\deploy\wfreerdp.exe";
        }

        /// <summary>Reads the PE machine type (COFF header) of a binary; 0 on any failure.</summary>
        private static ushort PeMachine(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                using (var br = new BinaryReader(fs))
                {
                    fs.Seek(0x3C, SeekOrigin.Begin);
                    int peOff = br.ReadInt32();
                    fs.Seek(peOff + 4, SeekOrigin.Begin);
                    return br.ReadUInt16();
                }
            }
            catch { return 0; }
        }

        // ── value maps (null = emit nothing = wfreerdp default) ──
        private static string BppValue(ColorDepthOpt d)
        {
            switch (d) { case ColorDepthOpt.Bpp32: return "32"; case ColorDepthOpt.Bpp24: return "24";
                case ColorDepthOpt.Bpp16: return "16"; case ColorDepthOpt.Bpp15: return "15"; case ColorDepthOpt.Bpp8: return "8"; default: return null; }
        }
        private static string CompressionLevelValue(CompressionLevelOpt c)
        {
            switch (c) { case CompressionLevelOpt.Level0: return "0"; case CompressionLevelOpt.Level1: return "1"; case CompressionLevelOpt.Level2: return "2"; default: return null; }
        }
        private static string NetworkValue(NetworkOpt n)
        {
            switch (n) { case NetworkOpt.Modem: return "modem"; case NetworkOpt.BroadbandLow: return "broadband-low";
                case NetworkOpt.Broadband: return "broadband"; case NetworkOpt.BroadbandHigh: return "broadband-high";
                case NetworkOpt.Wan: return "wan"; case NetworkOpt.Lan: return "lan"; case NetworkOpt.Auto: return "auto"; default: return null; }
        }
        private static string GfxValue(GfxOpt g)
        {
            switch (g) { case GfxOpt.Avc444: return "AVC444:on"; case GfxOpt.Avc420: return "AVC420:on";
                case GfxOpt.Rfx: return "RFX:on"; case GfxOpt.Progressive: return "progressive:on"; default: return null; }
        }
        private static string AudioValue(AudioOpt a)
        {
            switch (a) { case AudioOpt.PlayLocal: return "0"; case AudioOpt.PlayRemote: return "1"; case AudioOpt.None: return "2"; default: return null; }
        }
        private static string SecurityValue(SecurityOpt s)
        {
            switch (s) { case SecurityOpt.ForceNla: return "/sec:nla"; case SecurityOpt.NlaOff: return "/sec:nla:off";
                case SecurityOpt.TlsOnly: return "/sec:tls"; case SecurityOpt.RdpOnly: return "/sec:rdp"; default: return null; }
        }

        // ── Windows argument joining/quoting (CommandLineToArgvW rules) ──
        public static string JoinArgs(List<string> tokens)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < tokens.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(QuoteArg(tokens[i]));
            }
            return sb.ToString();
        }

        private static string QuoteArg(string arg)
        {
            if (!string.IsNullOrEmpty(arg) && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
            var sb = new StringBuilder();
            sb.Append('"');
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); backslashes = 0; continue; }
                sb.Append('\\', backslashes); backslashes = 0; sb.Append(c);
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        private static string RedactPassword(string args, string password)
        {
            if (string.IsNullOrEmpty(password)) return args;
            return args.Replace(password, "********");
        }
    }
}
