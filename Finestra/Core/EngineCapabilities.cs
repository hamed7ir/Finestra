using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Text;
using Finestra.Helpers;

namespace Finestra.Core
{
    /// <summary>
    /// FIN-CAMERA-CAPABILITY — what does THIS engine binary actually support? (wfreerdp or, since the SDL-ENGINE
    /// switch, the SDL client — see <see cref="IsFinestraSdl"/>.)
    ///
    /// The problem this exists to stop: a switch the engine does not know is not ignored, it is FATAL.
    /// WinPR's command-line parser rejects the WHOLE line and the process exits before any network
    /// activity ("CommandLineParseArgumentsA: Failed at index N: Unexpected keyword"), so the session
    /// dies instantly with no useful message. A saved profile with Camera=true therefore could not
    /// connect at all on a public engine — the app emitted /camera:, and only the private spike engine
    /// implements it.
    ///
    /// So capability is decided from the ENGINE BINARY, not from a build flag, a version string, or a
    /// guess: the camera backend is Media Foundation, so an engine that has it imports mfplat and
    /// mfreadwrite. That is the same discriminator installer\anycpu\build.ps1 uses to keep spike
    /// engines out of releases — deliberately the same test, so the two can never disagree about what
    /// "has camera" means.
    ///
    /// Cached per (path, length, last-write) and computed once, so nothing is spawned or re-read on
    /// connect. Swapping a different engine into the same path re-detects, because length/mtime change.
    /// </summary>
    internal static class EngineCapabilities
    {
        private struct Entry
        {
            public long Length;
            public long WriteTicks;
            public bool Camera;
            public Dictionary<string, string> Build;   // /buildconfig, parsed once per binary
            public bool Sdl;                            // _sdlCache only: the Finestra SDL engine
        }

        private static readonly Dictionary<string, Entry> _cache =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new object();

        /// <summary>True when this engine implements /camera: (Media Foundation rdpecam backend present).
        /// Unknown or unreadable engine → FALSE: omitting a switch degrades a feature, emitting one the
        /// engine cannot parse kills the whole connection, so the safe answer is always "no".</summary>
        public static bool SupportsCamera(string enginePath)
        {
            if (string.IsNullOrEmpty(enginePath)) return false;
            try
            {
                var fi = new FileInfo(enginePath);
                if (!fi.Exists) return false;
                long len = fi.Length, ticks = fi.LastWriteTimeUtc.Ticks;

                lock (_lock)
                {
                    Entry e;
                    if (_cache.TryGetValue(enginePath, out e) && e.Length == len && e.WriteTicks == ticks)
                        return e.Camera;   // already resolved for this exact binary — no re-read, no re-log

                    bool cam = Probe(enginePath);
                    _cache[enginePath] = new Entry { Length = len, WriteTicks = ticks, Camera = cam };

                    // Logged ONCE PER RESOLUTION, with the path and the size/mtime the answer was keyed on.
                    // A wrong cache has to be diagnosable from Finestra.log rather than by reasoning about
                    // timestamps after the fact.
                    FileLog.Line("[ENGCAP] " + enginePath + " len=" + len
                        + " mtimeUtc=" + new DateTime(ticks, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss")
                        + " → camera=" + (cam ? "CAPABLE" : "not capable"));
                    return cam;
                }
            }
            catch (Exception ex)
            {
                FileLog.Line("[ENGCAP] probe failed for " + enginePath + ": " + ex.Message + " → camera=not capable");
                return false;
            }
        }

        private static readonly Dictionary<string, Entry> _sdlCache =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>SDL-ENGINE — is this engine the SDL client with Finestra's embedding module
        /// (client/SDL/SDL3/sdl_finestra.cpp on the fork), rather than the Windows client (wfreerdp)?
        ///
        /// The two need different handling: the SDL engine reads ONE stdin line (the password) where wfreerdp
        /// reads the missing username/domain first, takes keyboard focus on its own child window where
        /// wfreerdp's hook wants the host panel focused, renegotiates its size from the window by itself, and
        /// has a touch mode. Decided from the BINARY, like <see cref="SupportsCamera"/>: the module's window
        /// class name "FinestraSessionWindow" is a UTF-16 literal compiled into it (checked at both byte
        /// parities by <see cref="Has"/>), and no wfreerdp contains it. A stock upstream sdl-freerdp does not
        /// either — it cannot embed — so it is treated as NOT this engine. Cached per (path, length, mtime).</summary>
        public static bool IsFinestraSdl(string enginePath)
        {
            if (string.IsNullOrEmpty(enginePath)) return false;
            try
            {
                var fi = new FileInfo(enginePath);
                if (!fi.Exists) return false;
                long len = fi.Length, ticks = fi.LastWriteTimeUtc.Ticks;
                lock (_lock)
                {
                    Entry e;
                    if (_sdlCache.TryGetValue(enginePath, out e) && e.Length == len && e.WriteTicks == ticks)
                        return e.Sdl;
                    bool sdl = Has(File.ReadAllBytes(enginePath), "FinestraSessionWindow");
                    _sdlCache[enginePath] = new Entry { Length = len, WriteTicks = ticks, Sdl = sdl };
                    FileLog.Line("[ENGCAP] " + enginePath + " len=" + len + " → kind=" + (sdl ? "SDL (Finestra module)" : "wfreerdp/other"));
                    return sdl;
                }
            }
            catch (Exception ex)
            {
                FileLog.Line("[ENGCAP] kind probe failed for " + enginePath + ": " + ex.Message + " → kind=wfreerdp/other");
                return false;
            }
        }

        /// <summary>Does the binary import the Media Foundation DLLs the camera backend needs? Checks ASCII
        /// and UTF-16 at BOTH byte parities — a string at an odd offset is invisible to a UTF-16 scan that
        /// starts at byte 0, which has produced false negatives in this project before.</summary>
        private static bool Probe(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (Has(b, "mfplat.dll") || Has(b, "mfreadwrite.dll")) return true;
            return false;
        }

        /// <summary>The engine's OWN build configuration, from <c>wfreerdp /buildconfig</c>.
        ///
        /// ⚠ WHY THIS EXISTS. Several things this app's settings form offers are decided at ENGINE COMPILE
        /// TIME, not at runtime: "Software (openh264)" only works if WITH_OPENH264 was on, and the graphics
        /// AVC path only exists if WITH_GFX_H264 was on. Without this the form offers choices the engine
        /// cannot honour and silently does something else — which is exactly how swapping in a rebuilt
        /// engine changed behaviour underneath a form that claimed to control it.
        ///
        /// Asking the binary beats sniffing it: /buildconfig is the engine's own report, so the form and
        /// the engine cannot disagree. Cached per (path, length, mtime) exactly like SupportsCamera, so the
        /// process is spawned at most once per distinct binary and never on a connect path.</summary>
        public static Dictionary<string, string> BuildConfig(string enginePath)
        {
            var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(enginePath)) return empty;
            try
            {
                var fi = new FileInfo(enginePath);
                if (!fi.Exists) return empty;
                long len = fi.Length, ticks = fi.LastWriteTimeUtc.Ticks;

                lock (_lock)
                {
                    Entry e;
                    bool fresh = _cache.TryGetValue(enginePath, out e) && e.Length == len && e.WriteTicks == ticks;
                    if (fresh && e.Build != null) return e.Build;

                    var parsed = ProbeBuildConfig(enginePath);
                    bool cam = fresh ? e.Camera : Probe(enginePath);
                    _cache[enginePath] = new Entry { Length = len, WriteTicks = ticks, Camera = cam, Build = parsed };

                    FileLog.Line("[ENGCAP] buildconfig " + enginePath + " -> " + parsed.Count + " flag(s);"
                        + " OPENH264=" + Flag(parsed, "WITH_OPENH264")
                        + " GFX_H264=" + Flag(parsed, "WITH_GFX_H264")
                        + " MEDIA_FOUNDATION=" + Flag(parsed, "WITH_MEDIA_FOUNDATION")
                        + " SWSCALE=" + Flag(parsed, "WITH_SWSCALE")
                        + " VERBOSE_WINPR_ASSERT=" + Flag(parsed, "WITH_VERBOSE_WINPR_ASSERT"));
                    return parsed;
                }
            }
            catch (Exception ex)
            {
                FileLog.Line("[ENGCAP] buildconfig failed for " + enginePath + ": " + ex.Message);
                return empty;
            }
        }

        /// <summary>One flag's value, or "?" when the engine did not report it.</summary>
        public static string Flag(Dictionary<string, string> cfg, string name)
        {
            string v;
            return (cfg != null && cfg.TryGetValue(name, out v)) ? v : "?";
        }

        /// <summary>True when the named flag is affirmatively on. FreeRDP prints ON, TRUE or 1 depending on how
        /// the option was declared, so accept all three. Anything else — including an absent flag — is false:
        /// "could not confirm" must never enable a control.</summary>
        public static bool IsOn(string enginePath, string name)
        {
            string v = Flag(BuildConfig(enginePath), name);
            return v.Equals("ON", StringComparison.OrdinalIgnoreCase)
                || v.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
                || v == "1";
        }

        /// <summary>Short capability line for the settings form, so what the engine can actually do is visible
        /// rather than assumed.</summary>
        public static string Summary(string enginePath)
        {
            var cfg = BuildConfig(enginePath);
            if (cfg.Count == 0) return "(engine did not report a build configuration)";
            return "openh264 " + Flag(cfg, "WITH_OPENH264")
                 + " · display H.264 " + Flag(cfg, "WITH_GFX_H264")
                 + " · swscale " + Flag(cfg, "WITH_SWSCALE")
                 + " · audio(winmm) " + Flag(cfg, "WITH_WINMM")
                 + (IsOn(enginePath, "WITH_VERBOSE_WINPR_ASSERT")
                        ? "   ⚠ VERBOSE_WINPR_ASSERT=ON — runtime checks, slower" : "");
        }

        /// <summary>Runs the engine with /buildconfig and parses the NAME=VALUE tokens it prints. No window,
        /// output redirected, hard 5 s timeout — a hung or wrong binary must degrade to "unknown", never
        /// block the settings dialog.</summary>
        private static Dictionary<string, string> ProbeBuildConfig(string path)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var psi = new ProcessStartInfo(path, "/buildconfig")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var proc = Process.Start(psi))
            {
                if (proc == null) return map;
                // Both streams are read concurrently and the wait comes FIRST: reading them to the end before
                // waiting (as this did) blocks until the process exits, so the 5 s limit could never fire and an
                // engine that does not exit hung the Settings dialog.
                var so = proc.StandardOutput.ReadToEndAsync();
                var se = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(5000)) { try { proc.Kill(); } catch { } }
                string outp = (so.Wait(1000) ? so.Result : "") + " " + (se.Wait(1000) ? se.Result : "");

                char[] sep = { ' ', '\t', '\r', '\n' };
                foreach (string tok in outp.Split(sep, StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = tok.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = tok.Substring(0, eq);
                    if (!k.StartsWith("WITH", StringComparison.OrdinalIgnoreCase) &&
                        !k.StartsWith("BUILD", StringComparison.OrdinalIgnoreCase)) continue;
                    map[k] = tok.Substring(eq + 1);
                }
            }
            return map;
        }

        private static bool Has(byte[] bytes, string needle)
        {
            string ascii = Encoding.ASCII.GetString(bytes);
            if (ascii.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            for (int off = 0; off <= 1; off++)
            {
                if (bytes.Length <= off) continue;
                string wide = Encoding.Unicode.GetString(bytes, off, bytes.Length - off);
                if (wide.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }
    }
}
