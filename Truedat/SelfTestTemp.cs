using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Truedat
{
    /// <summary>
    /// The folder the self-tests make their fixtures in (<see cref="Root"/>), and what keeps it from filling up.
    ///
    /// Every test deletes its own fixture when it ends, but some are kept on purpose (a failed CLI test keeps its library so
    /// it can be looked at) and a run that crashes or is killed deletes nothing. So the NEXT run removes whatever earlier runs
    /// left, when it starts. Several sessions can run the self-test on one machine at once and they share this folder, so a
    /// run removes nothing while any other truedat process is running: every self-test is one, whichever build it is, and a
    /// scan that happens to be running only puts the cleanup off to the next run. Two runs starting together each see the
    /// other, so neither removes anything.
    /// </summary>
    internal static class SelfTestTemp
    {
        internal static string Root => Path.Combine(Path.GetTempPath(), ".truedat-selftest");

        internal static RunFolder Begin() => Begin(Root, OtherTruedatRunning);

        internal static RunFolder Begin(string root, Func<bool> otherTruedatRunning)
        {
            Directory.CreateDirectory(root);
            var run = new RunFolder(root);
            run.RemoveLeftovers(otherTruedatRunning());
            return run;
        }

        /// <summary>Any truedat process other than this one — a self-test of any build, or anything else truedat is doing.</summary>
        internal static bool OtherTruedatRunning()
        {
            using var self = Process.GetCurrentProcess();
            bool other = false;
            foreach (var p in Process.GetProcessesByName(self.ProcessName))
                using (p) if (p.Id != self.Id) other = true;
            return other;
        }

        internal sealed class RunFolder : IDisposable
        {
            internal RunFolder(string root) { Root = root; }

            internal string Root { get; }
            /// <summary>Another truedat process was running when this run started, so nothing was removed.</summary>
            internal bool OtherRunning { get; private set; }
            /// <summary>Folders and files earlier runs left that this run removed.</summary>
            internal int Removed { get; private set; }
            /// <summary>Leftovers that could not be removed — something still holds a file in them. The next run tries again.</summary>
            internal List<string> StillThere { get; } = new List<string>();

            internal void RemoveLeftovers(bool otherRunning)
            {
                OtherRunning = otherRunning;
                if (otherRunning)
                {
                    Console.WriteLine($"  Self-test temp: another truedat is running, so what earlier runs left in {Root} stays for the next run.");
                    return;
                }
                foreach (var entry in Directory.GetFileSystemEntries(Root))
                {
                    try
                    {
                        if (Directory.Exists(entry)) Directory.Delete(entry, true); else File.Delete(entry);
                        Removed++;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { StillThere.Add(entry); }
                }
                if (Removed > 0) Console.WriteLine($"  Self-test temp: removed {Removed} left in {Root} by earlier runs.");
                foreach (var s in StillThere) Console.WriteLine($"  Self-test temp: could not remove {s} (something still holds a file in it); the next run tries again.");
            }

            /// <summary>Removes the folder if nothing is left in it (a non-recursive delete: what is still there is another
            /// run's, or a leftover the next run removes).</summary>
            public void Dispose()
            {
                try { Directory.Delete(Root); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }
    }
}
