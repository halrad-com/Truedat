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
        public int Entries, Failures, Unchanged, Drift, ContentReplaced, ContentRekeyed, Stale, EntitiesCreated, Artifacts, ArtifactsShared;
        public long ArtifactBytes;
        /// <summary>S2 C4: edge rows written / edge folds that failed (a failed 'edges' run row, no partial row).</summary>
        public int Edges, EdgesFailed;
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

        /// <summary>The directory of the store in force (set when the store path is resolved); null = no store, keep nothing.
        /// The extractor core stashes its main JSON here (C-A) before the content key is known.</summary>
        internal static string? StoreDirectory;

        // analysed-path (what the extractor was given: the staged copy or the original) -> pending artifact file
        static readonly ConcurrentDictionary<string, string> _stashed = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // catalog key -> pending artifact file, once Record() has tied the two together
        static readonly ConcurrentDictionary<string, string> _artifacts = new ConcurrentDictionary<string, string>(PathComparer.Instance);
        // S2 C4: the edges producer's result per analysed path (stashed by the extractor core), then per catalog key (tied by Record).
        static readonly ConcurrentDictionary<string, global::Truedat.Edges.EdgeFoldResult> _stashedEdges = new ConcurrentDictionary<string, global::Truedat.Edges.EdgeFoldResult>(StringComparer.OrdinalIgnoreCase);
        static readonly ConcurrentDictionary<string, global::Truedat.Edges.EdgeFoldResult> _edges = new ConcurrentDictionary<string, global::Truedat.Edges.EdgeFoldResult>(PathComparer.Instance);

        internal static int Pending => _entries.Count;
        internal static int PendingFailures => _failures.Count;
        internal static int PendingArtifacts => _artifacts.Count;
        internal static int PendingEdges => _edges.Count;

        internal static void Reset() { _entries.Clear(); _stashed.Clear(); _artifacts.Clear(); _stashedEdges.Clear(); _edges.Clear(); while (_failures.TryDequeue(out _)) { } }

        /// <summary>S2 C4: the extractor core hands over the edges result for the path it analysed; Record ties it to the entry.</summary>
        internal static void StashEdges(string analysedPath, global::Truedat.Edges.EdgeFoldResult edges) => _stashedEdges[analysedPath] = edges;

        /// <summary>S2 C5: the --work drain takes the fold the core stashed for the path it analysed (and drops the main-JSON stash,
        /// which the drain does not file: the description already exists). Null when the core produced none.</summary>
        internal static global::Truedat.Edges.EdgeFoldResult? TakeEdges(string analysedPath)
        {
            if (_stashed.TryRemove(analysedPath, out var pending)) TryDelete(pending);
            return _stashedEdges.TryRemove(analysedPath, out var edges) ? edges : null;
        }

        /// <summary>Called by the extractor core right after a successful parse, with the JSON text it is about to delete.</summary>
        internal static void StashMainJson(string analysedPath, string json)
        {
            var dir = StoreDirectory;
            if (dir == null) return;
            try { _stashed[analysedPath] = ArtifactStore.Stash(dir, json); }
            catch (Exception ex) { StoreLog.Warn($"could not stash the extractor output for {analysedPath}: {ex.Message}"); }
        }

        /// <summary>A retry handed the core a different path (a downmixed or transcoded WAV): move its stash to the path the
        /// scan will Record with (review #2). A stash already under <paramref name="toPath"/> (the first attempt's) is
        /// replaced by the retry's. No-op when nothing is stashed under <paramref name="fromPath"/>.</summary>
        internal static void Rekey(string fromPath, string toPath)
        {
            if (_stashedEdges.TryRemove(fromPath, out var edges)) _stashedEdges[toPath] = edges;
            if (!_stashed.TryRemove(fromPath, out var pending)) return;
            if (_stashed.TryRemove(toPath, out var previous)) TryDelete(previous);
            _stashed[toPath] = pending;
        }

        /// <summary>The scan will never Record this analysed path (the health gate failed it, or a retry's result was not kept):
        /// delete its stash now (review #3).</summary>
        internal static void Discard(string analysedPath)
        {
            _stashedEdges.TryRemove(analysedPath, out _);
            if (_stashed.TryRemove(analysedPath, out var pending)) TryDelete(pending);
        }

        /// <summary>Every stash nothing tied to an entry: once a scan's entries are flushed (or a mode ends without recording
        /// anything) they are garbage. Returns how many were dropped.</summary>
        internal static int DropUntied()
        {
            int n = 0;
            foreach (var kv in _stashed) { if (_stashed.TryRemove(kv.Key, out var p)) { TryDelete(p); n++; } }
            _stashedEdges.Clear();
            return n;
        }

        static void TryDelete(string path) { try { System.IO.File.Delete(path); } catch { } }

        /// <summary><paramref name="analysedPath"/> is the path the extractor was given (staged copy or original); it ties the
        /// stashed main JSON to this catalog entry so the flush can file it under the entry's content key.</summary>
        internal static void Record(string path, TrackEntry entry, string? analysedPath = null)
        {
            _entries[path] = entry;
            if (analysedPath != null && _stashed.TryRemove(analysedPath, out var pending)) _artifacts[path] = pending;
            if (analysedPath != null && _stashedEdges.TryRemove(analysedPath, out var edges)) _edges[path] = edges;
        }

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
            var artifacts = new Dictionary<string, string>(PathComparer.Instance);
            var edges = new Dictionary<string, global::Truedat.Edges.EdgeFoldResult>(PathComparer.Instance);
            foreach (var k in entries.Keys) { _entries.TryRemove(k, out _); if (_artifacts.TryRemove(k, out var p)) artifacts[k] = p; if (_edges.TryRemove(k, out var e)) edges[k] = e; }
            // Review #3: nothing will tie a stash after the entries are flushed; and a killed earlier scan may have left
            // pending files behind — both are swept here, so artifacts\pending cannot grow without bound.
            int untied = DropUntied();
            int stale = StoreDirectory != null ? ArtifactStore.SweepPending(StoreDirectory, TimeSpan.FromDays(1)) : 0;
            if (untied > 0 || stale > 0) StoreLog.Info($"artifact stashes dropped: {untied} untied (never recorded), {stale} stale (older than a day)");
            if (entries.Count == 0 && failures.Count == 0) { report.Ok = true; return report; }
            try
            {
                var resultPath = storePath == null ? null : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(storePath)) ?? ".", ResultFileName);
                var r = ImportCommand.ExecuteEntries(storePath, entries, null, "primary", true, "essentia", extractorBuild,
                    $"scan:{Environment.MachineName}@{DateTime.UtcNow:o}", failures, resultPath, rootHints, artifacts, edges);
                report.Ok = true; report.Entries = r.Entries; report.Failures = r.ErrorsRows; report.Unchanged = r.Unchanged; report.Drift = r.Drift;
                report.ContentReplaced = r.ContentReplaced; report.ContentRekeyed = r.ContentRekeyed; report.Stale = r.Stale;
                report.EntitiesCreated = r.EntitiesCreated; report.ElapsedMs = r.ElapsedMs; report.ResultPath = r.ResultPath;
                report.Artifacts = r.Artifacts; report.ArtifactsShared = r.ArtifactsShared; report.ArtifactBytes = r.ArtifactBytes;
                report.Edges = r.Edges; report.EdgesFailed = r.EdgesFailed;
                report.StorePath = storePath;
            }
            catch (Exception ex)
            {
                report.Ok = false; report.Error = ex.Message;
                foreach (var p in artifacts.Values) { try { System.IO.File.Delete(p); } catch { } }
                StoreLog.Error($"store write failed: {ex.Message} ({entries.Count} entries, {failures.Count} failures not written; the catalog has them)");
            }
            return report;
        }
    }
}
