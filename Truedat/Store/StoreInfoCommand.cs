using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Truedat.Store
{
    internal sealed class StoreInfo
    {
        public string Path = ""; public int SchemaVersion; public string SqliteVersion = ""; public string MachineId = ""; public string CreatedByHost = "";
        public List<(string Path, string Role)> Roots = new List<(string, string)>();
        public Dictionary<string, long> Counts = new Dictionary<string, long>(StringComparer.Ordinal);
        public long FileBytes, WalBytes, PageCount, PageSize, FreePages;
        public long Tracks;                       // descriptions: track_content rows, one per media (a mirror does not add any)
        public long FeatureRows;                  // copies: track_copy rows, one per location (a mirror does add these)
        public long SplitConflicts;               // content-disagree-at-split conflicts recorded by the v1→v2 migration
        public long ArtifactCount, ArtifactBytes;  // S2 C-A: aggregate artifacts (compressed extractor output) and their bytes on disk
        public Dictionary<string, long> ConflictsByKind = new Dictionary<string, long>(StringComparer.Ordinal);   // open conflicts per kind (review #6)
        public double BytesPerTrack;              // FileBytes / Tracks
        public double? MedianRtf;                 // analysisDuration / (durationMs/1000), median over rows with both
        public int RtfSamples;
        public Dictionary<string, long> FailedComponents = new Dictionary<string, long>(StringComparer.Ordinal);
        public Dictionary<string, long> Coverage = new Dictionary<string, long>(StringComparer.Ordinal);   // wave marker -> rows with it
        public long ContentKeysShared;            // content keys with > 1 location
        public long LocationsOnSharedKeys;
        public long SupersededHashes;             // non-primary content-hash facets (key moves)
        public long NoShaFeatures;                // feature rows keyed entity:<uuid>
        // S2 C4: the edges producer — media with an edge row, bytes of packed rows, ok/failed 'edges' runs, median fold+PCM ms.
        public long EdgeTracks, EdgeUnavailable, EdgeBytes, EdgeRunsOk, EdgeRunsFailed; public double? EdgeMedianMs; public string EdgesVersion = "";

        // store-v3 T12 — the state the operator and the hub's OOBE ask for: which generation the store is at, whether
        // the published files are at that generation, what the store has CAPTURED (arrays are opt-in and sticky), and
        // the one command that closes whatever gap there is. Every field is read from the store or from the files
        // beside it; none is inferred from the command line.
        public long Generation;                   // store_meta's generation: advances on every consumer-visible mutation
        public long EdgeArrayTracks;              // edge rows that hold the opt-in arrays (capture state, sticky per media)
        public string CatalogPath = "";           // the catalog beside the store — where a publication lands by default
        public long? PublishedGeneration;         // the generation the catalog on disk carries; null = legacy v1 header or no catalog
        public string FilesState = "";            // at-generation | behind | absent | legacy | off (config publish false)
        public string? FilesWhy;                  // when behind: the predicate's own reason
        public string NextCommand = "";           // the command that closes the gap this state names
    }

    /// <summary>--store-info: the measurements S1 owes, all derived from the store by query — bytes per
    /// track (spec §3 rule 6, decision 3), real-time factor from the imported
    /// analysisDuration, failure rate by component, coverage by capture wave, content shared by several
    /// locations (the per-location vs per-hash decision), key moves. dbstat is absent on Win10, so
    /// per-table bytes are not reported; totals are.</summary>
    internal static class StoreInfoCommand
    {
        internal const string ResultFileName = "store-info.json";

        static readonly string[] Tables =
        {
            "machine", "library_root", "file_entity", "identity_facet", "track_location", "track_content", "track_copy",
            "producer", "run", "work_item", "check_result", "conflict", "value", "segment", "artifact",
        };

        // v2: content columns count per media (track_content); copy facts count per location (track_copy).
        static readonly (string Key, string Table, string Column)[] CoverageColumns =
        {
            ("audioStreamSha256", "track_content", "audioStreamSha256"), ("fingerprint.v1", "track_copy", "fingerprint_v1_fileSize"),
            ("loudnessMomentary", "track_content", "loudnessMomentary"), ("averageLoudness", "track_content", "averageLoudness"), ("dynamicComplexity", "track_content", "dynamicComplexity"),
            ("hpcp12", "track_content", "hpcp12"), ("bitUsage", "track_content", "bitUsage_lowestNonZeroBit"), ("hfEnergyRatio", "track_content", "hfEnergyRatio"), ("smfmScores", "track_copy", "smfmScores"),
        };

        internal static int Run(string? storePath, bool audit, string? catalogPath = null)
        {
            try
            {
                StoreLog.SetVerbose(audit);
                var i = Collect(storePath, catalogPath);
                Console.WriteLine($"Store: {i.Path}");
                Console.WriteLine($"  schema v{i.SchemaVersion}, sqlite {i.SqliteVersion}, machine {i.MachineId} (created on {i.CreatedByHost})");
                Console.WriteLine($"  roots: {string.Join(", ", i.Roots.Select(r => $"{r.Path} ({r.Role})"))}");
                Console.WriteLine($"  size: {i.FileBytes:N0} bytes = {i.PageCount:N0} pages × {i.PageSize} ({i.FreePages:N0} free)" + (i.WalBytes > 0 ? $" + wal {i.WalBytes:N0}" : ""));
                Console.WriteLine($"  descriptions: {i.Tracks:N0} (one per media), copies: {i.FeatureRows:N0} (one per location)  →  {i.BytesPerTrack:N0} bytes/track (file size ÷ descriptions)");
                Console.WriteLine("  rows: " + string.Join(", ", Tables.Select(t => $"{t} {i.Counts[t]:N0}")));
                Console.WriteLine(i.MedianRtf.HasValue ? $"  analysis speed: median {i.MedianRtf:F1}× realtime over {i.RtfSamples:N0} tracks" : "  analysis speed: no analysisDuration data");
                Console.WriteLine("  coverage: " + string.Join(", ", CoverageColumns.Select(c => $"{c.Key} {i.Coverage[c.Key]:N0}")));
                Console.WriteLine(i.FailedComponents.Count > 0
                    ? "  failed components: " + string.Join(", ", i.FailedComponents.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value:N0}"))
                    : "  failed components: none recorded");
                Console.WriteLine($"  descriptions held by several copies: {i.ContentKeysShared:N0} over {i.LocationsOnSharedKeys:N0} locations; no-sha descriptions: {i.NoShaFeatures:N0}; superseded hashes (key moves): {i.SupersededHashes:N0}; split conflicts (v1→v2): {i.SplitConflicts:N0}");
                Console.WriteLine(i.ConflictsByKind.Count > 0
                    ? "  open conflicts: " + string.Join(", ", i.ConflictsByKind.Select(kv => $"{kv.Key} {kv.Value:N0}"))
                    : "  open conflicts: none");
                // Coverage per producer (C5: "done is a query"): eligible = every description, captured = those with an edge row.
                Console.WriteLine($"  edges ({i.EdgesVersion}): eligible {i.Tracks:N0}, captured {i.EdgeTracks:N0} ({i.EdgeUnavailable:N0} unavailable: under the floor), missing {Math.Max(0, i.Tracks - i.EdgeTracks):N0}" +
                    (i.EdgeRunsOk + i.EdgeRunsFailed > 0
                        ? $"; {i.EdgeBytes:N0} bytes of packed rows ({(i.EdgeTracks > 0 ? i.EdgeBytes / (double)i.EdgeTracks : 0):N0}/track); runs {i.EdgeRunsOk:N0} ok, {i.EdgeRunsFailed:N0} failed" + (i.EdgeMedianMs.HasValue ? $"; median fold+PCM {i.EdgeMedianMs:F0} ms" : "")
                        : ""));
                Console.WriteLine(i.ArtifactCount > 0
                    ? $"  aggregate artifacts: {i.ArtifactCount:N0} files, {i.ArtifactBytes:N0} bytes beside the store ({i.ArtifactBytes / (double)i.ArtifactCount:N0} bytes each; {(i.Tracks > 0 ? i.ArtifactCount * 100.0 / i.Tracks : 0):F1}% of descriptions)"
                    : "  aggregate artifacts: none (kept by scans run with --store)");
                // store-v3 T12: the state the OOBE and the operator read — generation, what the files show, what is captured.
                Console.WriteLine($"  generation: {i.Generation:N0}; files: {i.FilesState}"
                    + (i.PublishedGeneration.HasValue ? $" (catalog at generation {i.PublishedGeneration.Value:N0})" : "")
                    + (i.FilesWhy != null ? $" — {i.FilesWhy}" : "") + $"  [{i.CatalogPath}]");
                Console.WriteLine($"  capture: edge arrays on {i.EdgeArrayTracks:N0} of {i.EdgeTracks:N0} captured description(s)"
                    + (i.EdgeArrayTracks == 0 ? " (default row only — arrays are opt-in via --edge-arrays and sticky once captured)" : ""));
                Console.WriteLine($"  next: {i.NextCommand}");
                var outPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(i.Path) ?? ".", ResultFileName);
                File.WriteAllText(outPath, ToJson(i).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"  written: {outPath}");
                return 0;
            }
            catch (StoreException ex) { Console.Error.WriteLine("Error: " + ex.Message); return ex.ExitCode; }
            catch (Exception ex) { Console.Error.WriteLine("Error: store-info failed: " + ex.Message); return 1; }
        }

        /// <summary><paramref name="catalogPath"/> is the catalog the store was resolved against. Without it the files are looked for beside
        /// the database — wrong for --store-dir, an explicit --store and a network library's local store, where the state read "absent"
        /// and the next command exported a second catalog into the database's folder (codex review 2026-10-20 #8).</summary>
        internal static StoreInfo Collect(string? storePath, string? catalogPath = null)
        {
            var i = CollectOpen(storePath, catalogPath);
            // Rescan-2 R5: a scan is the remedy only when it would RUN. A catalog with no origin is accepted by the T18i precondition when
            // it agrees with the store and REFUSED when it does not — so store-info asks the precondition itself (the store is closed
            // again by now: one connection at a time, the network stores are opened exclusively) and names the reconciliation instead of
            // a scan that would refuse.
            if (i.FilesState == "legacy" && storePath != null)
            {
                var target = new System.Collections.Concurrent.ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
                StoreScan.LoadResult load;
                using (StoreLog.Quiet()) load = StoreScan.LoadAuthoritative(storePath, i.CatalogPath, target);
                if (load.Refused)
                {
                    i.FilesState = "legacy-disagree";
                    i.FilesWhy = (load.Refusal ?? "").Replace("Error: ", "").Replace(" Nothing was scanned.", "");
                    i.NextCommand = "none until reconciled — the scan refuses this catalog: " + i.FilesWhy;
                }
                else i.FilesWhy = "no store origin (written before the store was authoritative); it agrees with the store";
            }
            return i;
        }

        static StoreInfo CollectOpen(string? storePath, string? catalogPath)
        {
            var i = new StoreInfo();
            using var os = OpenStore.Open(storePath, create: false, VersionInfo.Display, DateTime.UtcNow.ToString("o"));
            var db = os.Db; var dal = os.Dal;
            i.Path = os.Path;
            i.SchemaVersion = SchemaMigrator.ReadVersion(db);
            i.SqliteVersion = os.Probe.VersionText ?? "?";
            i.MachineId = os.Machine.Id; i.CreatedByHost = os.Machine.CreatedByHost;
            foreach (var r in dal.ListRoots()) i.Roots.Add((r.Path, r.Role));
            foreach (var t in Tables) i.Counts[t] = dal.Count(t);

            i.FileBytes = new FileInfo(os.Path).Length;
            var wal = os.Path + "-wal";
            i.WalBytes = File.Exists(wal) ? new FileInfo(wal).Length : 0;
            i.PageCount = long.Parse(db.ExecScalar("PRAGMA page_count;") ?? "0");
            i.PageSize = long.Parse(db.ExecScalar("PRAGMA page_size;") ?? "0");
            i.FreePages = long.Parse(db.ExecScalar("PRAGMA freelist_count;") ?? "0");
            // v2: FeatureRows = copies (one per location); Tracks = descriptions (one per media). A mirror adds
            // copies, never descriptions — the operator's 1 : many : 1 made visible.
            i.FeatureRows = i.Counts["track_copy"];
            i.Tracks = i.Counts["track_content"];
            i.BytesPerTrack = i.Tracks > 0 ? (double)i.FileBytes / i.Tracks : 0;

            var rtfs = new List<double>();
            using (var st = db.Prepare("SELECT analysisDuration, fingerprint_v1_durationMs FROM track_copy WHERE analysisDuration IS NOT NULL AND fingerprint_v1_durationMs > 0;"))
                while (st.Step())
                {
                    // Nx realtime = audio seconds per wall second, truedat's throughput convention.
                    double wall = st.GetInt64(0) / (double)FieldPrecision.Scale("analysisDuration");   // v3: scaled INTEGER at the field's store precision (FieldPrecision.StoreDp)
                    if (wall > 0) rtfs.Add((st.GetInt64(1) / 1000.0) / wall);
                }
            i.RtfSamples = rtfs.Count;
            if (rtfs.Count > 0) { rtfs.Sort(); i.MedianRtf = rtfs.Count % 2 == 1 ? rtfs[rtfs.Count / 2] : (rtfs[rtfs.Count / 2 - 1] + rtfs[rtfs.Count / 2]) / 2; }

            using (var st = db.Prepare("SELECT failed_components FROM run WHERE outcome = 'failed' AND failed_components IS NOT NULL;"))
                while (st.Step())
                    foreach (var raw in (st.GetString(0) ?? "").Split(';'))
                    {
                        var tok = raw.Trim();
                        if (tok.Length == 0) continue;
                        if (tok.StartsWith("decode-", StringComparison.OrdinalIgnoreCase)) tok = "decode";
                        i.FailedComponents[tok] = i.FailedComponents.TryGetValue(tok, out var n) ? n + 1 : 1;
                    }

            foreach (var c in CoverageColumns)
                i.Coverage[c.Key] = long.Parse(db.ExecScalar($"SELECT count(*) FROM {c.Table} WHERE \"{c.Column}\" IS NOT NULL;") ?? "0");

            // v2: "shared" = descriptions held by more than one copy; the locations figure counts those copies.
            i.ContentKeysShared = long.Parse(db.ExecScalar("SELECT count(*) FROM (SELECT l.entity_id FROM track_copy p JOIN track_location l ON l.id = p.location_id GROUP BY l.entity_id HAVING count(*) > 1);") ?? "0");
            i.LocationsOnSharedKeys = long.Parse(db.ExecScalar("SELECT coalesce(sum(c), 0) FROM (SELECT count(*) AS c FROM track_copy p JOIN track_location l ON l.id = p.location_id GROUP BY l.entity_id HAVING count(*) > 1);") ?? "0");
            i.SupersededHashes = long.Parse(db.ExecScalar("SELECT count(*) FROM identity_facet WHERE is_primary = 0 AND substr(kind, 1, 13) = 'content_hash:';") ?? "0");
            i.NoShaFeatures = long.Parse(db.ExecScalar("SELECT count(*) FROM track_content WHERE content_key_kind = 'entity';") ?? "0");
            i.SplitConflicts = long.Parse(db.ExecScalar($"SELECT count(*) FROM conflict WHERE kind = '{SchemaV2Migration.ConflictKind}';") ?? "0");
            i.ArtifactCount = i.Counts["artifact"];
            i.ArtifactBytes = long.Parse(db.ExecScalar("SELECT coalesce(sum(bytes), 0) FROM artifact;") ?? "0");
            i.ConflictsByKind = dal.OpenConflictsByKind();
            // S2 C4/C5: edges coverage and cost, from the value and run tables — the SAME gap --work drains (review I2): the current
            // producer version only, and 'unavailable' (a track under the floor) counts as captured because the drain treats it as done.
            var edgesVersion = new global::Truedat.Edges.EdgeSettings().ProducerVersion;
            i.EdgesVersion = edgesVersion;
            using (var st = db.Prepare("SELECT count(DISTINCT content_hash) FROM value WHERE producer_key = 'edges' AND producer_version = ? AND segment_id = 0 AND field = 'edges.status' AND txt IN ('ok', 'unavailable');"))
            { st.Bind(1, edgesVersion); st.Step(); i.EdgeTracks = st.GetInt64(0); }
            using (var st = db.Prepare("SELECT count(DISTINCT content_hash) FROM value WHERE producer_key = 'edges' AND producer_version = ? AND segment_id = 0 AND field = 'edges.status' AND txt = 'unavailable';"))
            { st.Bind(1, edgesVersion); st.Step(); i.EdgeUnavailable = st.GetInt64(0); }
            // v4 (T05): the typed edge rows' blobs (HPCP + opt-in arrays) plus the whole-track vector rows.
            i.EdgeBytes = long.Parse(db.ExecScalar("SELECT coalesce(sum(length(vec)), 0) FROM value WHERE producer_key = 'edges';") ?? "0")
                        + long.Parse(db.ExecScalar("SELECT coalesce(sum(coalesce(length(hpcp), 0) + coalesce(length(arrays), 0)), 0) FROM edge;") ?? "0");
            i.EdgeRunsOk = long.Parse(db.ExecScalar("SELECT count(*) FROM run WHERE producer_key = 'edges' AND outcome = 'ok';") ?? "0");
            i.EdgeRunsFailed = long.Parse(db.ExecScalar("SELECT count(*) FROM run WHERE producer_key = 'edges' AND outcome = 'failed';") ?? "0");
            var edgeMs = new List<long>();
            using (var st = db.Prepare("SELECT duration_ms FROM run WHERE producer_key = 'edges' AND outcome = 'ok' AND duration_ms IS NOT NULL;"))
                while (st.Step()) edgeMs.Add(st.GetInt64(0));
            if (edgeMs.Count > 0) { edgeMs.Sort(); i.EdgeMedianMs = edgeMs.Count % 2 == 1 ? edgeMs[edgeMs.Count / 2] : (edgeMs[edgeMs.Count / 2 - 1] + edgeMs[edgeMs.Count / 2]) / 2.0; }

            // store-v3 T12: the state block.
            i.Generation = dal.Generation();
            i.EdgeArrayTracks = long.Parse(db.ExecScalar("SELECT count(DISTINCT content_hash) FROM edge WHERE arrays IS NOT NULL;") ?? "0");
            i.CatalogPath = catalogPath != null
                ? System.IO.Path.GetFullPath(catalogPath)
                : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(os.Path) ?? ".", Program.MoodsFileName);
            var catalogDir = System.IO.Path.GetDirectoryName(i.CatalogPath) ?? ".";
            var sidecarPath = System.IO.Path.ChangeExtension(i.CatalogPath, ".mbxs");
            var companionPath = System.IO.Path.Combine(catalogDir, global::Truedat.Edges.EdgeCompanion.FileName);
            bool expectCompanion = i.EdgeTracks > 0;
            if (StoreScan.PublishOff)
            {
                // Config publish false: no file is meant to exist, so none is "behind" and there is nothing to run.
                i.FilesState = "off";
            }
            else if (!File.Exists(i.CatalogPath))
            {
                i.FilesState = "absent";
                i.NextCommand = StoreScan.ScanPublishesCommand;
            }
            else if (!CatalogOrigin.TryRead(i.CatalogPath, out var origin))
            {
                // A v1 header: written before the store existed, or by a build that did not publish. The generation
                // cannot be compared at all, so this is its own state rather than a guess at "behind".
                i.FilesState = "legacy";
                i.NextCommand = StoreScan.ScanPublishesCommand;
            }
            else if (origin.Generation > i.Generation)
            {
                // Rescan-2 R5: FilesBehind reads any generation mismatch as "behind", but a catalog AHEAD of the store came from another
                // store state and the scan refuses it (StoreScan "catalog-ahead") — so a scan is not the remedy here.
                i.PublishedGeneration = origin.Generation;
                i.FilesState = "catalog-ahead";
                i.FilesWhy = $"catalog at generation {origin.Generation}, store at {i.Generation}: the catalog came from another store state; a scan refuses it";
                i.NextCommand = "move the catalog aside and scan (the store publishes the whole library), or seed this store from it: "
                              + "truedat --store-import --moods \"" + i.CatalogPath + "\" --store \"" + os.Path + "\"   then scan (the store then supersedes the catalog and republishes it)";
            }
            else
            {
                i.PublishedGeneration = origin.Generation;
                i.FilesState = Publication.FilesBehind(i.CatalogPath, sidecarPath, companionPath, expectCompanion, i.Generation, out var why)
                    ? "behind" : "at-generation";
                if (i.FilesState == "behind") { i.FilesWhy = why; i.NextCommand = StoreScan.ScanPublishesCommand; }
            }
            // With the files whole, the gap that remains is work: edges not yet folded, then nothing.
            if (i.NextCommand.Length == 0)
            {
                long missingEdges = Math.Max(0, i.Tracks - i.EdgeTracks);
                i.NextCommand = missingEdges > 0
                    ? "truedat --store --edges --refresh   (" + missingEdges.ToString("N0") + " description(s) with no edge row at " + i.EdgesVersion + ")"
                    : "nothing — the store is current and the files are published at its generation";
            }
            return i;
        }

        static JsonObject ToJson(StoreInfo i)
        {
            var counts = new JsonObject(); foreach (var kv in i.Counts) counts[kv.Key] = kv.Value;
            var failed = new JsonObject(); foreach (var kv in i.FailedComponents) failed[kv.Key] = kv.Value;
            var cov = new JsonObject(); foreach (var kv in i.Coverage) cov[kv.Key] = kv.Value;
            return new JsonObject
            {
                ["kind"] = "store-info", ["schemaVersion"] = 1, ["generatedAt"] = DateTime.UtcNow.ToString("o"),
                ["store"] = i.Path, ["storeSchema"] = i.SchemaVersion, ["sqlite"] = i.SqliteVersion, ["machine"] = i.MachineId, ["createdByHost"] = i.CreatedByHost,
                ["roots"] = new JsonArray(i.Roots.Select(r => (JsonNode?)new JsonObject { ["path"] = r.Path, ["role"] = r.Role }).ToArray()),
                ["fileBytes"] = i.FileBytes, ["walBytes"] = i.WalBytes, ["pageCount"] = i.PageCount, ["pageSize"] = i.PageSize, ["freePages"] = i.FreePages,
                ["tracks"] = i.Tracks, ["featureRows"] = i.FeatureRows, ["bytesPerTrack"] = Math.Round(i.BytesPerTrack, 1),
                ["medianRtf"] = i.MedianRtf.HasValue ? Math.Round(i.MedianRtf.Value, 2) : (double?)null, ["rtfSamples"] = i.RtfSamples,
                ["counts"] = counts, ["failedComponents"] = failed, ["coverage"] = cov,
                ["contentKeysShared"] = i.ContentKeysShared, ["locationsOnSharedKeys"] = i.LocationsOnSharedKeys, ["splitConflicts"] = i.SplitConflicts,
                ["artifactCount"] = i.ArtifactCount, ["artifactBytes"] = i.ArtifactBytes,
                ["openConflicts"] = new JsonObject(i.ConflictsByKind.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, JsonValue.Create(kv.Value)))),
                ["supersededHashes"] = i.SupersededHashes, ["noShaFeatures"] = i.NoShaFeatures,
                ["edges"] = new JsonObject
                {
                    ["producerVersion"] = i.EdgesVersion, ["eligible"] = i.Tracks, ["captured"] = i.EdgeTracks, ["unavailable"] = i.EdgeUnavailable, ["missing"] = Math.Max(0, i.Tracks - i.EdgeTracks),
                    ["bytes"] = i.EdgeBytes, ["runsOk"] = i.EdgeRunsOk, ["runsFailed"] = i.EdgeRunsFailed, ["medianMs"] = i.EdgeMedianMs,
                    ["arrayTracks"] = i.EdgeArrayTracks,
                },
                // store-v3 T12: machine-readable state for the hub's detect-and-guide half.
                ["state"] = new JsonObject
                {
                    ["generation"] = i.Generation,
                    ["catalog"] = i.CatalogPath,
                    ["publishedGeneration"] = i.PublishedGeneration,
                    ["files"] = i.FilesState,
                    ["filesWhy"] = i.FilesWhy,
                    ["edgeArrayTracks"] = i.EdgeArrayTracks,
                    ["nextCommand"] = i.NextCommand,
                },
            };
        }
    }
}
