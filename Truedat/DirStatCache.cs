using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Truedat
{
    /// <summary>The three facts every scan site reads about a file, snapshotted together.</summary>
    internal struct FileStat
    {
        public bool Exists;
        public long Length;
        public DateTime LastWriteTimeUtc;
    }

    /// <summary>
    /// One directory enumeration serves every sibling's existence / size / mtime.
    ///
    /// Every scan mode stats each work-list file once (<c>new FileInfo(path)</c>). On local
    /// NTFS that is free out of the OS metadata cache; over SMB it is one network round trip
    /// per ALREADY-CACHED track, which is the dominant cost of an incremental scan against a
    /// NAS (MoodsMode's own comment at the stat site says so). A single
    /// <c>QUERY_DIRECTORY</c> returns name + size + mtime for every file in a folder in a
    /// handful of responses, so an album's worth of stats collapses to ~one round trip.
    ///
    /// Lazy per directory, NOT one upfront pass: the first call for a directory enumerates
    /// it, later siblings hit the map. Albums are contiguous in the XML, so the savings are
    /// the same, but the mtime staleness window shrinks from "the whole scan" to "this
    /// album", and there is no front-loaded latency before the first track starts.
    ///
    /// FAIL-SAFE IS THE LOAD-BEARING PROPERTY. If a directory cannot be enumerated (gone,
    /// access denied, share dropped mid-scan) it is remembered as failed and every file in it
    /// goes to a direct <c>FileInfo</c>, exactly as before this class existed. So the
    /// reachability path is unchanged — a dead share still reads as missing, the caller still
    /// re-probes the root, the scan still STOPS — and a failed directory costs the same round
    /// trips it always cost, never more. <see cref="Disabled"/> (<c>--no-dir-batch</c>) routes
    /// every call direct, for the same reason <c>--no-stage</c> exists: the operator can
    /// revert the behaviour without a rebuild.
    ///
    /// The counters are the proof surface. Round-trip COUNT is deterministic and needs no
    /// slow network to demonstrate, so the end-of-scan line reports them.
    /// </summary>
    internal sealed class DirStatCache
    {
        /// <summary>--no-dir-batch: every call becomes a direct per-file stat.</summary>
        public static bool Disabled;

        // Directory -> lazily built map of filename -> stat. A NULL map means the enumeration
        // FAILED; that is remembered so every later sibling goes direct without retrying the
        // enumeration. Lazy with ExecutionAndPublication so parallel workers on one album
        // share one enumeration instead of racing N of them. Keyed by PathComparer so the
        // XML's spelling of a directory and the walk's spelling land on the same entry.
        readonly ConcurrentDictionary<string, Lazy<Dictionary<string, FileStat>?>> _dirs =
            new ConcurrentDictionary<string, Lazy<Dictionary<string, FileStat>?>>(PathComparer.Instance);

        long _enumerations, _served, _direct, _dirFailures;
        /// <summary>Directories this cache enumerated itself (seeding is not counted).</summary>
        public long Enumerations => Volatile.Read(ref _enumerations);
        /// <summary>Stats answered from a map — enumerated or seeded — with no per-file syscall.</summary>
        public long Served => Volatile.Read(ref _served);
        /// <summary>Stats that fell back to a direct <c>FileInfo</c>: disabled, unparseable path, or a failed directory.</summary>
        public long Direct => Volatile.Read(ref _direct);
        /// <summary>Directories whose enumeration threw; each is counted once and then served direct.</summary>
        public long DirFailures => Volatile.Read(ref _dirFailures);

        public FileStat Stat(string path)
        {
            if (Disabled) return DirectStat(path);

            string? dir;
            string name;
            try
            {
                dir = Path.GetDirectoryName(path);
                name = Path.GetFileName(path);
            }
            catch { return DirectStat(path); }
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return DirectStat(path);

            Dictionary<string, FileStat>? map;
            try
            {
                map = _dirs.GetOrAdd(dir!, d => new Lazy<Dictionary<string, FileStat>?>(
                    () => Enumerate(d), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
            }
            catch { return DirectStat(path); }

            if (map == null) return DirectStat(path);
            Interlocked.Increment(ref _served);
            // Absent from a successfully enumerated directory IS the answer: the whole folder
            // was listed, so a missing name is a missing file and costs no further round trip.
            return map.TryGetValue(name, out var s) ? s : default;
        }

        /// <summary>
        /// Record a <see cref="FileInfo"/> that a directory walk already produced, so the
        /// scan's later <see cref="Stat"/> for it costs nothing. The <c>--folder</c> walk
        /// yields these for free (they are populated from the same find data a direct stat
        /// would fetch). Called from the single-threaded walk BEFORE the parallel loop
        /// touches the directory; the map is complete by the time any reader sees it.
        /// </summary>
        public void Seed(FileInfo fi)
        {
            string? dir;
            string name;
            FileStat s;
            try
            {
                dir = fi.DirectoryName;
                name = fi.Name;
                s = new FileStat { Exists = true, Length = fi.Length, LastWriteTimeUtc = fi.LastWriteTimeUtc };
            }
            catch { return; }
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return;

            var map = _dirs.GetOrAdd(dir!, _ => new Lazy<Dictionary<string, FileStat>?>(
                () => new Dictionary<string, FileStat>(StringComparer.OrdinalIgnoreCase),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
            if (map == null) return;   // this directory already failed to enumerate; leave it direct
            map[name] = s;
        }

        Dictionary<string, FileStat>? Enumerate(string dir)
        {
            try
            {
                var map = new Dictionary<string, FileStat>(StringComparer.OrdinalIgnoreCase);
                // The FileInfo the enumerator yields is initialised from WIN32_FIND_DATA, so
                // reading Length / LastWriteTimeUtc here is a field read, not a second
                // syscall per file — that is the entire point. Top level only: the work list
                // names files, and recursing would prefetch folders nothing will ask about.
                foreach (var fi in new DirectoryInfo(dir).EnumerateFiles())
                    map[fi.Name] = new FileStat { Exists = true, Length = fi.Length, LastWriteTimeUtc = fi.LastWriteTimeUtc };
                Interlocked.Increment(ref _enumerations);
                return map;
            }
            catch
            {
                Interlocked.Increment(ref _dirFailures);
                return null;
            }
        }

        // The pre-cache behaviour, verbatim in effect: the FileInfo ctor throws on invalid
        // path characters where File.Exists swallowed to false, and every site wrapped it in
        // try/catch and treated null as missing. Same answer here, as a struct.
        FileStat DirectStat(string path)
        {
            Interlocked.Increment(ref _direct);
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return default;
                return new FileStat { Exists = true, Length = fi.Length, LastWriteTimeUtc = fi.LastWriteTimeUtc };
            }
            catch { return default; }
        }
    }
}
