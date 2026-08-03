using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Finestra.Helpers;

namespace Finestra.Core
{
    /// <summary>
    /// FRDP-FIXSWEEP B1 — a Win32 Job Object with KILL_ON_JOB_CLOSE that every launched wfreerdp child is assigned to.
    /// When the app process ends (normal exit OR a hard taskkill), the OS closes the job handle and terminates every
    /// process in it, so no orphaned wfreerdp survives the app. Windows 8.1+ supports nested jobs, so this is safe even
    /// if the app is itself already in a job (debugger / Explorer). kernel32 only — no new dependency. Failures are
    /// logged (never a dialog); the graceful host-close path is the other layer.
    /// </summary>
    internal static class JobGuard
    {
        private static IntPtr _job = IntPtr.Zero;   // held for the whole process lifetime; NEVER closed early
        private static readonly object _lock = new object();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr sec, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, int len);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        /// <summary>Create the kill-on-close job once, at startup. Idempotent.</summary>
        public static void Init()
        {
            lock (_lock)
            {
                if (_job != IntPtr.Zero) return;
                try
                {
                    IntPtr job = CreateJobObject(IntPtr.Zero, null);
                    if (job == IntPtr.Zero) { FileLog.Line("[JOB] CreateJobObject failed err=" + Marshal.GetLastWin32Error()); return; }
                    var ext = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    ext.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                    int len = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                    IntPtr buf = Marshal.AllocHGlobal(len);
                    try
                    {
                        Marshal.StructureToPtr(ext, buf, false);
                        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buf, len))
                        { FileLog.Line("[JOB] SetInformationJobObject failed err=" + Marshal.GetLastWin32Error()); return; }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                    _job = job;   // keep the handle open for the process lifetime — closing it is what kills the children
                    FileLog.Line("[JOB] kill-on-close job ready");
                }
                catch (Exception ex) { FileLog.Line("[JOB] init failed: " + ex.Message); }
            }
        }

        /// <summary>Assign a just-started child to the job so it dies with the app. Failure is logged, not fatal.</summary>
        public static void Assign(Process proc)
        {
            if (proc == null) return;
            if (_job == IntPtr.Zero) Init();   // Init is itself locked + idempotent
            if (_job == IntPtr.Zero) return;
            try
            {
                if (!AssignProcessToJobObject(_job, proc.Handle))
                    FileLog.Line("[JOB] assign pid=" + SafePid(proc) + " failed err=" + Marshal.GetLastWin32Error());
                else
                    FileLog.Line("[JOB] assigned pid=" + SafePid(proc));
            }
            catch (Exception ex) { FileLog.Line("[JOB] assign threw: " + ex.Message); }
        }

        private static string SafePid(Process p) { try { return p.Id.ToString(); } catch { return "?"; } }
    }
}
