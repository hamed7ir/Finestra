using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using Finestra.Helpers;

namespace Finestra.Core
{
    /// <summary>
    /// FRDP-STATS-RECON, Form B (IPC-patched wfreerdp): the C# side of the local named pipe. Connects to the
    /// patched wfreerdp's per-PID pipe (<c>\\.\pipe\finestrardp.&lt;pid&gt;</c>), reads live "STATS &lt;rtt&gt;
    /// &lt;bw&gt;" lines — RTT (ms) + bandwidth (kbps) straight off FreeRDP's autodetect — derives jitter, and
    /// sends PAUSE / RESUME (→ gdi_send_suppress_output on the wfreerdp main thread). Rendering stays NATIVE in
    /// wfreerdp's window; only ~tens of bytes/sec cross the pipe. Spike client: one reader thread, best-effort.
    /// </summary>
    public sealed class StatsPipe : IDisposable
    {
        /// <summary>rttMs, bandwidthKbps, jitterMs — raised on the reader thread (marshal to the UI yourself).</summary>
        public event Action<int, int, int> Updated;

        private NamedPipeClientStream _pipe;
        private StreamWriter _writer;
        private readonly Thread _thread;
        private volatile bool _stop;
        private int _prevRtt = -1;
        private double _jitter;

        public StatsPipe(int wfreerdpPid)
        {
            _thread = new Thread(() => Run(wfreerdpPid)) { IsBackground = true, Name = "StatsPipe" };
            _thread.Start();
        }

        private void Run(int pid)
        {
            try
            {
                // "finestrardp" is COMPILED INTO the native engine (the patched wfreerdp names its pipe
                // \\.\pipe\finestrardp.<pid>). It matches the engine build — rename ONLY together with an
                // engine rebuild, never as part of an app-side rename.
                _pipe = new NamedPipeClientStream(".", "finestrardp." + pid, PipeDirection.InOut, PipeOptions.None);
                _pipe.Connect(10000);   // wfreerdp opens the pipe in post_connect (after the session is up)
                _writer = new StreamWriter(_pipe) { AutoFlush = true, NewLine = "\n" };
                var reader = new StreamReader(_pipe);
                FileLog.Line("[STATS] pipe connected to wfreerdp pid=" + pid);
                string line;
                int rawLogged = 0;
                bool loggedNonzero = false;
                while (!_stop && (line = reader.ReadLine()) != null)
                {
                    // Diagnostics: the raw line carries trailing "<baseRtt> <resultCbN> <syncCbN>" so the
                    // proof log shows whether the autodetect callbacks fired and when the first real
                    // (non-zero) reading arrived. Parsing below only consumes the first two numbers.
                    if (rawLogged < 2) { FileLog.Line("[STATS] raw: " + line); rawLogged++; }
                    var parts = line.Split(' ');
                    if (parts.Length >= 3 && parts[0] == "STATS"
                        && int.TryParse(parts[1], out int rtt) && int.TryParse(parts[2], out int bw))
                    {
                        if (!loggedNonzero && (rtt > 0 || bw > 0))
                        { FileLog.Line("[STATS] first non-zero: " + line); loggedNonzero = true; }
                        if (_prevRtt >= 0) _jitter += (Math.Abs(rtt - _prevRtt) - _jitter) / 8.0;   // smoothed |Δrtt|
                        _prevRtt = rtt;
                        try { Updated?.Invoke(rtt, bw, (int)Math.Round(_jitter)); } catch { }
                    }
                }
            }
            catch (Exception ex) { FileLog.Line("[STATS] pipe ended: " + ex.Message); }
        }

        public void Pause() => Send("PAUSE");
        public void Resume() => Send("RESUME");
        private void Send(string cmd) { try { _writer?.WriteLine(cmd); } catch (Exception ex) { FileLog.Line("[STATS] send failed: " + ex.Message); } }

        public void Dispose()
        {
            _stop = true;
            try { _pipe?.Dispose(); } catch { }
        }
    }
}
