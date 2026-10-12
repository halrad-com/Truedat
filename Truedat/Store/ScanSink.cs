using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Truedat.Store
{
    internal sealed class ScanFailure
    {
        public string Path = ""; public string Components = ""; public string Error = ""; public double DurationSec;
    }

    internal sealed class ScanSinkReport
    {
        public bool Ok; public string? Error;
        public int Entries, Failures, Unchanged, Drift, ContentReplaced, ContentRekeyed, Stale, EntitiesCreated;
        public long ElapsedMs; public string? ResultPath; public string? StorePath;
    }

    /// <summary>S2 C3 — the scan writes the store. The scan paths call <see cref="Record"/> for every entry they
    /// analysed this run (never for a cache hit) and the errors-ledger writer calls <see cref="RecordFailure"/>;
    /// after the catalog is saved exactly as today, <see cref="Flush"/> writes the collected entries through the
    /// import's in-memory entry as producer essentia@&lt;build&gt;, one run row per entry, plus a failed run per
    /// failure. A store failure is counted, logged and reported — it never costs a catalog entry.</summary>
    internal static class ScanSink
    {
        static readonly ConcurrentDictionary<string, TrackEntry> _entries = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
        static readonly ConcurrentQueue<ScanFailure> _failures = new ConcurrentQueue<ScanFailure>();

        /// <summary>The extractor build the scan ran with (producer version of every run row); set at scan start.</summary>
        internal static string ExtractorBuild = "unknown";

        internal static int Pending => _entries.Count;
        internal static int PendingFailures => _failures.Count;

        internal static void Reset() { _entries.Clear(); while (_failures.TryDequeue(out _)) { } }

        internal static void Record(string path, TrackEntry entry) => _entries[path] = entry;

        internal static void RecordFailure(string path, string components, string error, double durationSec)
            => _failures.Enqueue(new ScanFailure { Path = path, Components = components, Error = error, DurationSec = durationSec });

        internal const string ResultFileName = "store-result.json";

        /// <summary>Writes everything recorded since the last flush, then clears it (the catalog already holds these
        /// entries, so nothing is lost if the store write fails — the next import recovers them). Never throws.</summary>
        internal static ScanSinkReport Flush(string? storePath, string extractorBuild, IEnumerable<string>? rootHints = null)
        {
            var report = new ScanSinkReport { StorePath = storePath };
            var entries = new Dictionary<string, TrackEntry>(_entries, PathComparer.Instance);
            var failures = new List<ScanFailure>();
            while (_failures.TryDequeue(out var f)) failures.Add(f);
            foreach (var k in entries.Keys) _entries.TryRemove(k, out _);
            if (entries.Count == 0 && failures.Count == 0) { report.Ok = true; return report; }
            try
            {
                var resultPath = storePath == null ? null : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(storePath)) ?? ".", ResultFileName);
                var r = ImportCommand.ExecuteEntries(storePath, entries, null, "primary", true, "essentia", extractorBuild,
                    $"scan:{Environment.MachineName}@{DateTime.UtcNow:o}", failures, resultPath, rootHints);
                report.Ok = true; report.Entries = r.Entries; report.Failures = r.ErrorsRows; report.Unchanged = r.Unchanged; report.Drift = r.Drift;
                report.ContentReplaced = r.ContentReplaced; report.ContentRekeyed = r.ContentRekeyed; report.Stale = r.Stale;
                report.EntitiesCreated = r.EntitiesCreated; report.ElapsedMs = r.ElapsedMs; report.ResultPath = r.ResultPath;
                report.StorePath = storePath;
            }
            catch (Exception ex)
            {
                report.Ok = false; report.Error = ex.Message;
                StoreLog.Error($"store write failed: {ex.Message} ({entries.Count} entries, {failures.Count} failures not written; the catalog has them)");
            }
            return report;
        }
    }
}
