using System;
using System.Diagnostics;

namespace Truedat
{
    /// <summary>
    /// Waiting on a subprocess whose work has no natural upper bound — a conversion of a track that
    /// may be three minutes long or eight hours.
    ///
    /// A wall-clock cap on such a wait is a guess about how long the work should take, and it is
    /// wrong in the direction that hurts: it stops the long job that was working, not the stuck one.
    /// What separates the two is progress, so that is what this watches: processor time, or bytes
    /// read or written, since the last look.
    /// </summary>
    internal static class SubprocessWait
    {
        internal enum Outcome
        {
            /// <summary>The process ended by itself. Its exit code is the caller's to read.</summary>
            Exited,
            /// <summary>Nothing moved for the whole idle window. The process is still running: stopping it is the caller's
            /// act, because only the caller knows what to clean up and what to say.</summary>
            Stalled,
            /// <summary>The caller asked to stop waiting. The process is still running.</summary>
            Cancelled,
            /// <summary>Still working when the caller's ceiling passed. The process is still running.</summary>
            Overran,
        }

        /// <summary>Wait for <paramref name="proc"/> for as long as it makes progress. Looks every <paramref name="pollMs"/>;
        /// <paramref name="maxIdlePolls"/> looks in a row with no progress is a stall. Counters that cannot be read show no
        /// progress, so a process that cannot be observed is given the idle window and no more.
        /// <paramref name="maxMs"/> is the caller's ceiling (0 = none): progress alone cannot tell a long job from one
        /// that will never end, because a process spinning on bad input is busy too. The ceiling has to come from the
        /// size of the work, which only the caller knows.</summary>
        internal static Outcome WhileProgressing(Process proc, int pollMs, int maxIdlePolls, Func<bool>? cancelled = null, long maxMs = 0)
        {
            var lastCpu = TimeSpan.Zero;
            ulong lastIo = 0;
            int idle = 0;
            var clock = Stopwatch.StartNew();
            while (!proc.WaitForExit(pollMs))
            {
                if (cancelled != null && cancelled()) return Outcome.Cancelled;
                if (maxMs > 0 && clock.ElapsedMilliseconds >= maxMs) return Outcome.Overran;
                var cpu = lastCpu;
                ulong io = lastIo;
                try
                {
                    proc.Refresh();
                    cpu = proc.TotalProcessorTime;
                    io = Program.TryReadProcessIoBytes(proc);
                }
                catch { if (proc.HasExited) break; }   // gone between the wait and the read
                if (Program.EssentiaProgressed(cpu, lastCpu, io, lastIo))
                {
                    if (cpu > lastCpu) lastCpu = cpu;
                    if (io > lastIo) lastIo = io;
                    idle = 0;
                }
                else if (++idle >= maxIdlePolls) return Outcome.Stalled;
            }
            return Outcome.Exited;
        }
    }
}
