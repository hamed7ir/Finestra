using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Finestra.Core;

namespace Finestra.Helpers
{
    /// <summary>
    /// Minimal diagnostic sink: tees Debug/Trace output to ONE text file (Finestra.log, beside the exe for a
    /// portable copy, otherwise Documents\Finestra) so RT ARM32 — where DebugView has no build — is
    /// diagnosable by reading a file. A single <see cref="TextWriterTraceListener"/> on
    /// <see cref="Trace.Listeners"/> captures every Debug.WriteLine / Trace.WriteLine with no call-site
    /// changes (Debug and Trace share the one listener collection).
    ///
    /// ★ OFF BY DEFAULT IN RELEASE. A shipped app must not quietly accumulate a diagnostic file nobody
    /// asked for — one real install reached 731 KB. The user turns it on in Settings when something needs
    /// diagnosing, and off again afterwards. With it off NO listener is attached and NO log file is
    /// created at all, so `Line` costs a bool test.
    ///
    /// DEBUG builds always log, regardless of the setting: development and the on-device diagnostic builds
    /// depend on it, and that is the config deployed to RT for accent/keyboard verification.
    ///
    /// NOTE: the [PATHS]/[ACCENT]/[DPI]/[SETTINGS] diagnostics are Debug.WriteLine — compiled ONLY in DEBUG
    /// builds. Trace.WriteLine lines (crashes, launch arg lists) are captured in Release too, when enabled.
    /// Fully guarded and best-effort: logging can never take the app down, and the app runs even if the file
    /// can't be opened. UTF-8 WITH BOM so RT 8.1's built-in Notepad renders →/— cleanly.
    /// </summary>
    public static class FileLog
    {
        private static TextWriterTraceListener _listener;
        private static bool _bannerWritten;

        /// <summary>True when a listener is currently attached. DEBUG builds force it on.</summary>
        public static bool IsEnabled { get { return _listener != null; } }

        /// <summary>Should we be logging right now? DEBUG: always. RELEASE: only if the user asked.
        /// Reading the setting is guarded — a corrupt or unreadable settings file must not stop startup,
        /// and "off" is the safe answer.</summary>
        private static bool Wanted()
        {
#if DEBUG
            return true;
#else
            try { return AppSettings.Instance.EnableLogging; } catch { return false; }
#endif
        }

        /// <summary>Call once, first thing in Main — before any [PATHS]/[DPI] trace.</summary>
        public static void Init() { Apply(); }

        /// <summary>Attach or detach to match the current setting. Safe to call repeatedly; the Settings
        /// dialog calls it on save so the toggle takes effect without a restart.</summary>
        public static void Refresh() { Apply(); }

        private static void Apply()
        {
            try
            {
                bool want = Wanted();
                if (want == (_listener != null)) return;   // already in the right state

                if (!want)
                {
                    Trace.WriteLine("[LOG] logging disabled in Settings — closing this log.");
                    try { Trace.Listeners.Remove(_listener); } catch { }
                    try { _listener.Flush(); _listener.Close(); _listener.Dispose(); } catch { }
                    _listener = null;
                    return;
                }

                // Resolving LogFile creates/validates the data dir (may emit its own [PATHS] before we attach).
                string path = StoragePaths.LogFile;
                var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                var w = new StreamWriter(fs, new UTF8Encoding(true)) { AutoFlush = true };
                _listener = new TextWriterTraceListener(w);
                Trace.Listeners.Add(_listener);
                Trace.AutoFlush = true;

                string arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "?";
                Trace.WriteLine("========== Finestra log " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    + "  (" + (IntPtr.Size == 8 ? "64-bit" : "32-bit") + " / " + arch + ") ==========");
                Trace.WriteLine("[PATHS] log file = " + path);
                // The legacy-data migration runs on the FIRST StoragePaths touch — above, before the listener
                // attached — so its note is buffered and written here (once, only if a migration happened).
                if (!_bannerWritten && StoragePaths.MigrationNote != null) Trace.WriteLine(StoragePaths.MigrationNote);
                _bannerWritten = true;
            }
            catch { /* logging is best-effort — the app runs regardless */ }
        }

        /// <summary>Writes a line when logging is on. Non-throwing, and a no-op with no listener attached.</summary>
        public static void Line(string s) { try { if (_listener != null) Trace.WriteLine(s); } catch { } }
    }
}
