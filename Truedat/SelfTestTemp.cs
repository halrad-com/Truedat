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
    /// left, when it starts. Several sessions can run the self-test on one machine at once, so each run makes its fixtures in
    /// a folder of its own (run-&lt;pid&gt;-&lt;id&gt;) and holds an owner file in it open for as long as it runs; another run's
    /// cleanup removes that folder only once nothing holds the owner file - a crash or a kill releases it. Loose files that
    /// builds from before this left directly in the shared folder are removed only while no other truedat is running.
    /// </summary>
    internal static class SelfTestTemp
    {
        static string SharedRoot => Path.Combine(Path.GetTempPath(), ".truedat-selftest");
        static string? _runRoot;
        /// <summary>Where this run's fixtures go: its own folder once the self-test has begun, the shared folder before.</summary>
        internal static string Root => _runRoot ?? SharedRoot;

        internal static RunFolder Begin()
        {
            var run = Begin(SharedRoot, OtherTruedatRunning, privateRun: true);
            _runRoot = run.Root;
            return run;
        }

        /// <summary>Folders a run makes for itself are named with this prefix and hold <see cref="OwnerFile"/> open for the run's
        /// life (the hub-side EV6.2 review: a check of "is another truedat running" made once could not protect a run that started
        /// after it).</summary>
        internal const string RunPrefix = "run-", OwnerFile = ".owner";

        internal static RunFolder Begin(string root, Func<bool> otherTruedatRunning, bool privateRun = false)
        {
            Directory.CreateDirectory(root);
            RunFolder run;
            if (privateRun)
            {
                var own = Path.Combine(root, RunPrefix + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(own);
                var owner = new FileStream(Path.Combine(own, OwnerFile), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                run = new RunFolder(own, root, owner);
            }
            else run = new RunFolder(root, root, null);
            run.RemoveLeftovers(otherTruedatRunning());
            return run;
        }

        /// <summary>A run folder whose owner file can be opened alone belongs to a run that has ended.</summary>
        static bool RunHasEnded(string runFolder)
        {
            var owner = Path.Combine(runFolder, OwnerFile);
            if (!File.Exists(owner)) return true;
            try { using (new FileStream(owner, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return true; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
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
            readonly string _shared;
            FileStream? _owner;
            internal RunFolder(string root, string shared, FileStream? owner) { Root = root; _shared = shared; _owner = owner; }

            /// <summary>This run's own folder (or the shared one, for a run that did not ask for its own).</summary>
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
                foreach (var entry in Directory.GetFileSystemEntries(_shared))
                {
                    if (string.Equals(entry, Root, StringComparison.OrdinalIgnoreCase)) continue;   // this run's own folder
                    // A run's own folder goes only when that run has ended: its owner file is the proof, not a guess about what is
                    // running. Anything else is what an older build left loose, removed only when no other truedat is running.
                    bool runFolder = Path.GetFileName(entry).StartsWith(RunPrefix, StringComparison.Ordinal) && Directory.Exists(entry);
                    if (runFolder ? !RunHasEnded(entry) : otherRunning) continue;
                    try
                    {
                        if (Directory.Exists(entry)) Directory.Delete(entry, true); else File.Delete(entry);
                        Removed++;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { StillThere.Add(entry); }
                }
                if (otherRunning) Console.WriteLine($"  Self-test temp: another truedat is running, so loose files earlier builds left in {_shared} stay for the next run.");
                if (Removed > 0) Console.WriteLine($"  Self-test temp: removed {Removed} left in {_shared} by earlier runs.");
                foreach (var s in StillThere) Console.WriteLine($"  Self-test temp: could not remove {s} (something still holds a file in it); the next run tries again.");
            }

            /// <summary>Removes the folder if nothing is left in it (a non-recursive delete: what is still there is another
            /// run's, or a leftover the next run removes).</summary>
            public void Dispose()
            {
                if (_owner != null)
                {
                    _owner.Dispose(); _owner = null;
                    try { File.Delete(Path.Combine(Root, OwnerFile)); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
                try { Directory.Delete(Root); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                if (!string.Equals(Root, _shared, StringComparison.OrdinalIgnoreCase))
                    try { Directory.Delete(_shared); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }
    }
}
