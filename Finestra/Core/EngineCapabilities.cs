using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Finestra.Helpers;

namespace Finestra.Core
{
    /// <summary>
    /// FIN-CAMERA-CAPABILITY — what does THIS wfreerdp binary actually support?
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
        private struct Entry { public long Length; public long WriteTicks; public bool Camera; }

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

        /// <summary>Does the binary import the Media Foundation DLLs the camera backend needs? Checks ASCII
        /// and UTF-16 at BOTH byte parities — a string at an odd offset is invisible to a UTF-16 scan that
        /// starts at byte 0, which has produced false negatives in this project before.</summary>
        private static bool Probe(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (Has(b, "mfplat.dll") || Has(b, "mfreadwrite.dll")) return true;
            return false;
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
