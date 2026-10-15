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
        public int Entries, Failures, Unchanged, Drift, ContentReplaced, ContentRekeyed, Remeasured, Stale, EntitiesCreated, Artifacts, ArtifactsShared;
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
        // Spilled to disk as one packed file each (review I3): ~11 KB of objects per track resident until the flush would have been
        // ~800 MB at 72k tracks; the dictionaries hold paths. Same pending directory as the main-JSON stash, extension .edges.
        static readonly ConcurrentDictionary<string, string> _stashedEdges = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly ConcurrentDictionary<string, string> _edges = new ConcurrentDictionary<string, string>(PathComparer.Instance);

        internal static int Pending => _entries.Count;
        internal static int PendingFailures => _failures.Count;
        internal static int PendingArtifacts => _artifacts.Count;
        internal static int PendingEdges => _edges.Count;

        internal static void Reset() { _entries.Clear(); _stashed.Clear(); _artifacts.Clear(); _stashedEdges.Clear(); _edges.Clear(); while (_failures.TryDequeue(out _)) { } }

        /// <summary>S2 C4: the extractor core hands over the edges result for the path it analysed; Record ties it to the entry.
        /// Written to disk at once (packed); a failure to spill is logged and the fold is dropped — never a failed track.</summary>
        internal static void StashEdges(string analysedPath, global::Truedat.Edges.EdgeFoldResult edges)
        {
            var dir = StoreDirectory;
            if (dir == null) return;
            try
            {
                var pending = ArtifactStore.PendingDir(dir);
                System.IO.Directory.CreateDirectory(pending);
                var path = System.IO.Path.Combine(pending, Guid.NewGuid().ToString("N") + ".edges");
                System.IO.File.WriteAllBytes(path, global::Truedat.Edges.EdgeStore.PackResult(edges));
                if (_stashedEdges.TryGetValue(analysedPath, out var previous)) TryDelete(previous);
                _stashedEdges[analysedPath] = path;
            }
            catch (Exception ex) { StoreLog.Warn($"could not stash the edge fold for {analysedPath}: {ex.Message}"); }
        }


        /// <summary>Reads a spilled fold and deletes its file; null (logged) when the file is gone or unreadable.</summary>
        internal static global::Truedat.Edges.EdgeFoldResult? ReadEdges(string file)
        {
            try
            {
                var r = global::Truedat.Edges.EdgeStore.UnpackResult(System.IO.File.ReadAllBytes(file));
                TryDelete(file);
                return r;
            }
            catch (Exception ex) { StoreLog.Warn($"spilled edge fold unreadable ({file}): {ex.Message}"); TryDelete(file); return null; }
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
            if (_stashedEdges.TryRemove(fromPath, out var edges)) { if (_stashedEdges.TryRemove(toPath, out var prevEdges)) TryDelete(prevEdges); _stashedEdges[toPath] = edges; }
            if (!_stashed.TryRemove(fromPath, out var pending)) return;
            if (_stashed.TryRemove(toPath, out var previous)) TryDelete(previous);
            _stashed[toPath] = pending;
        }

        /// <summary>The scan will never Record this analysed path (the health gate failed it, or a retry's result was not kept):
        /// delete its stash now (review #3).</summary>
        internal static void Discard(string analysedPath)
        {
            if (_stashedEdges.TryRemove(analysedPath, out var edges)) TryDelete(edges);
            if (_stashed.TryRemove(analysedPath, out var pending)) TryDelete(pending);
        }

        /// <summary>Every stash nothing tied to an entry: once a scan's entries are flushed (or a mode ends without recording
        /// anything) they are garbage. Returns how many were dropped.</summary>
        internal static int DropUntied()
        {
            int n = 0;
            foreach (var kv in _stashed) { if (_stashed.TryRemove(kv.Key, out var p)) { TryDelete(p); n++; } }
            foreach (var kv in _stashedEdges) { if (_stashedEdges.TryRemove(kv.Key, out var p)) { TryDelete(p); n++; } }
            return n;
        }

        static void TryDelete(string path) { try { System.IO.File.Delete(path); } catch { } }

        /// <summary><paramref name="analysedPath"/> is the path the extractor was given (staged copy or original); it ties the
        /// stashed main JSON to this catalog entry so the flush can file it under the entry's content key.</summary>
        internal static void Record(string path, TrackEntry entry, string? analysedPath = null)
        {
            // Under the tie lock: a checkpoint flush running beside the workers takes an entry together with its fold, never one without the other.
            lock (_tieLock)
            {
                _entries[path] = entry;
                if (analysedPath != null && _stashed.TryRemove(analysedPath, out var pending)) _artifacts[path] = pending;
                if (analysedPath != null && _stashedEdges.TryRemove(analysedPath, out var edges)) _edges[path] = edges;
            }
        }

        static readonly object _tieLock = new object();

        internal static void RecordFailure(string path, string components, string error, double durationSec)
            => _failures.Enqueue(new ScanFailure { Path = path, Components = components, Error = error, DurationSec = durationSec });

        internal const string ResultFileName = "store-result.json";

        /// <summary>Writes everything recorded since the last flush, then clears it. The catalog already holds the ENTRIES, so a
        /// failed store write loses no description — the next import recovers them; the edge folds are not in the catalog, so a
        /// failed flush drops them (counted and logged) and the next --refresh re-analyses those tracks. Never throws.
        /// <paramref name="final"/> false is the scan's periodic checkpoint (beside its catalog save, workers still running): it
        /// writes what is tied and leaves the untied stashes alone, because mid-scan those belong to tracks still in flight.
        /// The end-of-scan flush (final) also drops untied stashes and sweeps stale pending files — after the write, so a scan
        /// longer than the stale age never sweeps its own folds before they are filed.</summary>
        internal static ScanSinkReport Flush(string? storePath, string extractorBuild, IEnumerable<string>? rootHints = null, bool final = true)
        {
            var report = new ScanSinkReport { StorePath = storePath };
            var entries = new Dictionary<string, TrackEntry>(PathComparer.Instance);
            var failures = new List<ScanFailure>();
            var artifacts = new Dictionary<string, string>(PathComparer.Instance);
            var edges = new Dictionary<string, string>(PathComparer.Instance);
            lock (_tieLock)
            {
                foreach (var kv in _entries) entries[kv.Key] = kv.Value;
                foreach (var k in entries.Keys) { _entries.TryRemove(k, out _); if (_artifacts.TryRemove(k, out var p)) artifacts[k] = p; if (_edges.TryRemove(k, out var e)) edges[k] = e; }
            }
            while (_failures.TryDequeue(out var f)) failures.Add(f);
            if (entries.Count == 0 && failures.Count == 0) { if (final) SweepLeftovers(); report.Ok = true; return report; }
            try
            {
                // store-v3 T11: the scan's record (store-result.json, the hub:H03d shape) is written by the scan itself at the
                // end of the run (Program.WriteScanStoreResult), after the companion step — not the import-shaped file here.
                var r = ImportCommand.ExecuteEntries(storePath, entries, null, "primary", true, "essentia", extractorBuild,
                    $"scan:{Environment.MachineName}@{DateTime.UtcNow:o}", failures, null, rootHints, artifacts, edges);
                report.Ok = true; report.Entries = r.Entries; report.Failures = r.ErrorsRows; report.Unchanged = r.Unchanged; report.Drift = r.Drift;
                report.ContentReplaced = r.ContentReplaced; report.ContentRekeyed = r.ContentRekeyed; report.Remeasured = r.Remeasured; report.Stale = r.Stale;
                report.EntitiesCreated = r.EntitiesCreated; report.ElapsedMs = r.ElapsedMs; report.ResultPath = r.ResultPath;
                report.Artifacts = r.Artifacts; report.ArtifactsShared = r.ArtifactsShared; report.ArtifactBytes = r.ArtifactBytes;
                report.Edges = r.Edges; report.EdgesFailed = r.EdgesFailed;
                report.StorePath = storePath;
            }
            catch (Exception ex)
            {
                report.Ok = false; report.Error = ex.Message;
                foreach (var p in artifacts.Values) { try { System.IO.File.Delete(p); } catch { } }
                foreach (var p in edges.Values) TryDelete(p);
                StoreLog.Error($"store write failed: {ex.Message} ({entries.Count} entries, {failures.Count} failures not written; the catalog has them" +
                               (edges.Count > 0 ? $"; {edges.Count} edge fold(s) dropped — the next --refresh re-analyses those tracks" : "") + ")");
            }
            if (final) SweepLeftovers();
            // store-v3 T11: the end-of-run record, on both outcomes (the scan re-writes it after the companion step).
            if (final) StoreResult.WriteScan(storePath, report, edges.Count > 0 || report.Edges > 0, null);
            return report;
        }

        /// <summary>End of scan only (review #3): nothing will tie a stash once the scan is over, and a killed earlier scan may have
        /// left pending files behind — both are swept, so artifacts\pending cannot grow without bound.</summary>
        static void SweepLeftovers()
        {
            int untied = DropUntied();
            int stale = StoreDirectory != null ? ArtifactStore.SweepPending(StoreDirectory, TimeSpan.FromDays(1)) : 0;
            if (untied > 0 || stale > 0) StoreLog.Info($"artifact stashes dropped: {untied} untied (never recorded), {stale} stale (older than a day)");
        }
    }
}
