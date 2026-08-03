using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Finestra.Core;

namespace Finestra.Helpers
{
    /// <summary>
    /// Minimal diagnostic sink: tees Debug/Trace output to ONE text file (Documents\Finestra\Finestra.log)
    /// so RT ARM32 — where DebugView has no build — is diagnosable by reading a file. A single
    /// <see cref="TextWriterTraceListener"/> on <see cref="Trace.Listeners"/> captures every Debug.WriteLine /
    /// Trace.WriteLine with no call-site changes (Debug and Trace share the one listener collection).
    ///
    /// NOTE: the [PATHS]/[ACCENT]/[DPI]/[SETTINGS] diagnostics are Debug.WriteLine — compiled ONLY in DEBUG
    /// builds — so build DEBUG to capture them on-device (that is the config we deploy to RT for accent
    /// verification). Trace.WriteLine lines (crashes, launch arg lists) are captured in Release too.
    /// Fully guarded and best-effort: logging can never take the app down, and the app runs even if the file
    /// can't be opened. UTF-8 WITH BOM so RT 8.1's built-in Notepad renders →/— cleanly.
    /// </summary>
    public static class FileLog
    {
        private static bool _init;

        /// <summary>Attaches the file listener. Call once, first thing in Main — before any [PATHS]/[DPI] trace.</summary>
        public static void Init()
        {
            if (_init) return;
            _init = true;
            try
            {
                // Resolving LogFile creates/validates Documents\Finestra\ (may emit its own [PATHS] before we attach).
                string path = StoragePaths.LogFile;
                var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                var w = new StreamWriter(fs, new UTF8Encoding(true)) { AutoFlush = true };
                Trace.Listeners.Add(new TextWriterTraceListener(w));
                Trace.AutoFlush = true;

                string arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "?";
                Trace.WriteLine("========== Finestra log " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    + "  (" + (IntPtr.Size == 8 ? "64-bit" : "32-bit") + " / " + arch + ") ==========");
                Trace.WriteLine("[PATHS] log file = " + path);
                // The legacy-data migration (1.0 rename) runs on the FIRST StoragePaths touch — above, before the
                // listener attached — so its note is buffered and written here (once, only if a migration happened).
                if (StoragePaths.MigrationNote != null) Trace.WriteLine(StoragePaths.MigrationNote);
            }
            catch { /* logging is best-effort — the app runs regardless */ }
        }

        /// <summary>Writes an always-on line (survives Release builds, unlike Debug.WriteLine). Non-throwing.</summary>
        public static void Line(string s) { try { Trace.WriteLine(s); } catch { } }
    }
}
