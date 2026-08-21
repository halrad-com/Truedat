using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Truedat
{
    /// <summary>
    /// Answers "which process is holding this file open?" via the Windows Restart
    /// Manager (rstrtmgr.dll, present since Vista). Pure P/Invoke — no new package
    /// reference, no network, nothing to ship alongside the exe.
    ///
    /// This exists because a staged copy in %TEMP% can briefly refuse to open with
    /// ERROR_SHARING_VIOLATION, and the bare OS message ("used by another process")
    /// names a GUID temp path and no process, which tells an operator nothing about
    /// whether the cause is antivirus, a backup agent, a sync client, or truedat
    /// itself. Naming the holder turns an unactionable string into a decision.
    ///
    /// LIMITS, and they are real: Restart Manager reports what the caller is allowed
    /// to see. A holder running as SYSTEM (Defender's MsMpEng is the likely one here)
    /// is commonly NOT reported to an unelevated process, and holders in another
    /// terminal-server session may be missed. So an empty result means "nothing this
    /// process can see", NEVER "nothing held it" — <see cref="Describe"/> words it
    /// that way on purpose. Best-effort throughout: every failure path returns null
    /// rather than throwing, because this is a diagnostic and must never be able to
    /// break a scan it was added to explain.
    /// </summary>
    internal static class FileLockInfo
    {
        private const int RmRebootReasonNone = 0;
        private const int CchRmMaxAppName = 255;
        private const int CchRmMaxSvcName = 63;
        private const int ErrorMoreData = 234;
        private const int ErrorSuccess = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct RM_UNIQUE_PROCESS
        {
            public int dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)]
            public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)]
            public string strServiceShortName;
            public int ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint pSessionHandle);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint pSessionHandle,
            uint nFiles, string[] rgsFilenames,
            uint nApplications, [In] RM_UNIQUE_PROCESS[]? rgApplications,
            uint nServices, string[]? rgsServiceNames);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(uint dwSessionHandle,
            out uint pnProcInfoNeeded, ref uint pnProcInfo,
            [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);

        /// <summary>
        /// Processes currently holding <paramref name="path"/> open, as
        /// "name (PID n)" strings. Empty list when none are visible; null when the
        /// query itself could not run (API missing, session refused, marshalling
        /// failure). The two are different answers and callers must not merge them:
        /// empty = "we looked and saw nobody", null = "we could not look".
        /// </summary>
        internal static List<string>? TryGetHolders(string path)
        {
            uint session = 0;
            bool started = false;
            try
            {
                var key = new StringBuilder(Guid.NewGuid().ToString("N"));
                if (RmStartSession(out session, 0, key) != ErrorSuccess) return null;
                started = true;

                if (RmRegisterResources(session, 1, new[] { path }, 0, null, 0, null) != ErrorSuccess)
                    return null;

                uint needed = 0, count = 0, reasons = 0;
                // First call sizes the array: it returns ERROR_MORE_DATA with the
                // count in `needed`. A plain success with count 0 means nobody is
                // holding it — a real answer, not a failure.
                int rc = RmGetList(session, out needed, ref count, null, ref reasons);
                if (rc == ErrorSuccess) return new List<string>();
                if (rc != ErrorMoreData) return null;
                if (needed == 0) return new List<string>();

                var infos = new RM_PROCESS_INFO[needed];
                count = needed;
                if (RmGetList(session, out needed, ref count, infos, ref reasons) != ErrorSuccess)
                    return null;

                var holders = new List<string>((int)count);
                for (int i = 0; i < count; i++)
                {
                    var name = infos[i].strAppName;
                    if (string.IsNullOrWhiteSpace(name)) name = infos[i].strServiceShortName;
                    if (string.IsNullOrWhiteSpace(name)) name = "unknown";
                    holders.Add($"{name} (PID {infos[i].Process.dwProcessId})");
                }
                return holders;
            }
            catch
            {
                // EntryPointNotFoundException / DllNotFoundException on a stripped
                // Windows image, or any marshalling surprise. Diagnostic only.
                return null;
            }
            finally
            {
                if (started) { try { RmEndSession(session); } catch { } }
            }
        }

        /// <summary>
        /// One-line, log-ready rendering of <see cref="TryGetHolders"/>. Pure and
        /// separately testable — the wording is the part that has to stay honest,
        /// and it must never claim more than the API can support (see LIMITS above).
        /// </summary>
        internal static string Describe(List<string>? holders)
        {
            if (holders == null) return "lock holder: could not query";
            if (holders.Count == 0) return "lock holder: none visible to this process (a SYSTEM-level holder such as antivirus is not reported unelevated)";
            return "lock holder: " + string.Join(", ", holders);
        }
    }
}
