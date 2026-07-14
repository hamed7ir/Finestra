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

        /// <summary>FRDP-RDP-LIVENESS Part 0 — every [STATS] log line was missing the pid, which is exactly what
        /// made two same-named "us2" sessions indistinguishable in a real device log. Read-only after construction.</summary>
        public readonly int Pid;

        public StatsPipe(int wfreerdpPid)
        {
            Pid = wfreerdpPid;
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
                FileLog.Line("[STATS pid=" + pid + "] pipe connected to wfreerdp");
                string line;
                int rawLogged = 0;
                bool loggedNonzero = false;
                while (!_stop && (line = reader.ReadLine()) != null)
                {
                    // Diagnostics: the raw line carries trailing "<baseRtt> <resultCbN> <syncCbN>" so the
                    // proof log shows whether the autodetect callbacks fired and when the first real
                    // (non-zero) reading arrived. Parsing below only consumes the first two numbers.
                    if (rawLogged < 2) { FileLog.Line("[STATS pid=" + pid + "] raw: " + line); rawLogged++; }
                    var parts = line.Split(' ');
                    if (parts.Length >= 3 && parts[0] == "STATS"
                        && int.TryParse(parts[1], out int rtt) && int.TryParse(parts[2], out int bw))
                    {
                        if (!loggedNonzero && (rtt > 0 || bw > 0))
                        { FileLog.Line("[STATS pid=" + pid + "] first non-zero: " + line); loggedNonzero = true; }
                        if (_prevRtt >= 0) _jitter += (Math.Abs(rtt - _prevRtt) - _jitter) / 8.0;   // smoothed |Δrtt|
                        _prevRtt = rtt;
                        try { Updated?.Invoke(rtt, bw, (int)Math.Round(_jitter)); } catch { }
                    }
                }
                // FRDP-RDP-LIVENESS Part 0 — a clean EOF (server closed its end) fell through the while loop with
                // ZERO log output before this: the exact blind spot that made a dead channel indistinguishable from
                // "still fine, just quiet". _stop is set by OUR OWN Dispose(), so only log the OTHER-END-closed case.
                if (!_stop) FileLog.Line("[STATS pid=" + pid + "] read loop ended (EOF, remote closed)");
            }
            catch (Exception ex) { FileLog.Line("[STATS pid=" + pid + "] pipe ended: " + ex.Message); }
        }

        /// <summary>FRDP-RDP-LIVENESS Part 1 — corroborating signal only, NEVER sufficient alone: a hung engine's
        /// write can fail well before the heartbeat's own silence threshold elapses. The liveness check logs this
        /// alongside its verdict; it never independently triggers anything on its own.</summary>
        public volatile bool LastSendFailed;

        public void Pause() => Send("PAUSE");
        public void Resume() => Send("RESUME");
        private void Send(string cmd)
        {
            try { _writer?.WriteLine(cmd); LastSendFailed = false; }
            catch (Exception ex) { LastSendFailed = true; FileLog.Line("[STATS pid=" + Pid + "] send failed: " + ex.Message); }
        }

        public void Dispose()
        {
            _stop = true;
            try { _pipe?.Dispose(); } catch { }
        }
    }
}
