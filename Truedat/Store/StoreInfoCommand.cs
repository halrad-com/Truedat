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
        public long Tracks;                       // distinct entities holding a feature row (a mirror does not double it)
        public long FeatureRows;                  // track_feature rows (per location — a mirror does double this)
        public double BytesPerTrack;              // FileBytes / Tracks
        public long ProjectedBytesAt156k;         // BytesPerTrack * 156000
        public double? MedianRtf;                 // analysisDuration / (durationMs/1000), median over rows with both
        public int RtfSamples;
        public Dictionary<string, long> FailedComponents = new Dictionary<string, long>(StringComparer.Ordinal);
        public Dictionary<string, long> Coverage = new Dictionary<string, long>(StringComparer.Ordinal);   // wave marker -> rows with it
        public long ContentKeysShared;            // content keys with > 1 location
        public long LocationsOnSharedKeys;
        public long SupersededHashes;             // non-primary content-hash facets (key moves)
        public long NoShaFeatures;                // feature rows keyed entity:<uuid>
    }

    /// <summary>--store-info: the measurements S1 owes, all derived from the store by query — bytes per
    /// track and the 156k projection (spec §3 rule 6, decision 3), real-time factor from the imported
    /// analysisDuration, failure rate by component, coverage by capture wave, content shared by several
    /// locations (the per-location vs per-hash decision), key moves. dbstat is absent on Win10, so
    /// per-table bytes are not reported; totals are.</summary>
    internal static class StoreInfoCommand
    {
        internal const string ResultFileName = "store-info.json";

        static readonly string[] Tables =
        {
            "machine", "library_root", "file_entity", "identity_facet", "track_location", "track_feature",
            "producer", "run", "work_item", "check_result", "conflict", "value", "segment", "artifact",
        };

        static readonly (string Key, string Column)[] CoverageColumns =
        {
            ("audioStreamSha256", "audioStreamSha256"), ("fingerprint.v1", "fingerprint_v1_fileSize"),
            ("loudnessMomentary", "loudnessMomentary"), ("averageLoudness", "averageLoudness"), ("dynamicComplexity", "dynamicComplexity"),
            ("hpcp12", "hpcp12"), ("bitUsage", "bitUsage_lowestNonZeroBit"), ("hfEnergyRatio", "hfEnergyRatio"), ("smfmScores", "smfmScores"),
        };

        internal static int Run(string? storePath, bool audit)
        {
            try
            {
                StoreLog.SetVerbose(audit);
                var i = Collect(storePath);
                Console.WriteLine($"Store: {i.Path}");
                Console.WriteLine($"  schema v{i.SchemaVersion}, sqlite {i.SqliteVersion}, machine {i.MachineId} (created on {i.CreatedByHost})");
                Console.WriteLine($"  roots: {string.Join(", ", i.Roots.Select(r => $"{r.Path} ({r.Role})"))}");
                Console.WriteLine($"  size: {i.FileBytes:N0} bytes = {i.PageCount:N0} pages × {i.PageSize} ({i.FreePages:N0} free)" + (i.WalBytes > 0 ? $" + wal {i.WalBytes:N0}" : ""));
                Console.WriteLine($"  tracks: {i.Tracks:N0} (distinct), feature rows: {i.FeatureRows:N0} (per location)  →  {i.BytesPerTrack:N0} bytes/track; projected at 156k tracks: {i.ProjectedBytesAt156k / (1024.0 * 1024):N0} MB");
                Console.WriteLine("  rows: " + string.Join(", ", Tables.Select(t => $"{t} {i.Counts[t]:N0}")));
                Console.WriteLine(i.MedianRtf.HasValue ? $"  analysis speed: median {i.MedianRtf:F1}× realtime over {i.RtfSamples:N0} tracks" : "  analysis speed: no analysisDuration data");
                Console.WriteLine("  coverage: " + string.Join(", ", CoverageColumns.Select(c => $"{c.Key} {i.Coverage[c.Key]:N0}")));
                Console.WriteLine(i.FailedComponents.Count > 0
                    ? "  failed components: " + string.Join(", ", i.FailedComponents.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value:N0}"))
                    : "  failed components: none recorded");
                Console.WriteLine($"  content shared by several locations: {i.ContentKeysShared:N0} keys over {i.LocationsOnSharedKeys:N0} locations; no-sha feature rows: {i.NoShaFeatures:N0}; superseded hashes (key moves): {i.SupersededHashes:N0}");
                var outPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(i.Path) ?? ".", ResultFileName);
                File.WriteAllText(outPath, ToJson(i).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"  written: {outPath}");
                return 0;
            }
            catch (StoreException ex) { Console.Error.WriteLine("Error: " + ex.Message); return ex.ExitCode; }
            catch (Exception ex) { Console.Error.WriteLine("Error: store-info failed: " + ex.Message); return 1; }
        }

        internal static StoreInfo Collect(string? storePath)
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
            i.FeatureRows = i.Counts["track_feature"];
            // Tracks = distinct entities holding a feature row: features are stored per location, so a mirror
            // doubles FeatureRows but not Tracks (review #3).
            i.Tracks = long.Parse(db.ExecScalar("SELECT count(DISTINCT l.entity_id) FROM track_feature f JOIN track_location l ON l.id = f.location_id;") ?? "0");
            i.BytesPerTrack = i.Tracks > 0 ? (double)i.FileBytes / i.Tracks : 0;
            i.ProjectedBytesAt156k = (long)(i.BytesPerTrack * 156_000);

            var rtfs = new List<double>();
            using (var st = db.Prepare("SELECT analysisDuration, fingerprint_v1_durationMs FROM track_feature WHERE analysisDuration IS NOT NULL AND fingerprint_v1_durationMs > 0;"))
                while (st.Step())
                {
                    // Nx realtime = audio seconds per wall second, truedat's throughput convention.
                    double wall = st.GetDouble(0);
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
                i.Coverage[c.Key] = long.Parse(db.ExecScalar($"SELECT count(*) FROM track_feature WHERE \"{c.Column}\" IS NOT NULL;") ?? "0");

            i.ContentKeysShared = long.Parse(db.ExecScalar("SELECT count(*) FROM (SELECT content_key FROM track_feature WHERE content_key_kind != 'entity' GROUP BY content_key HAVING count(*) > 1);") ?? "0");
            i.LocationsOnSharedKeys = long.Parse(db.ExecScalar("SELECT coalesce(sum(c), 0) FROM (SELECT count(*) AS c FROM track_feature WHERE content_key_kind != 'entity' GROUP BY content_key HAVING count(*) > 1);") ?? "0");
            i.SupersededHashes = long.Parse(db.ExecScalar("SELECT count(*) FROM identity_facet WHERE is_primary = 0 AND substr(kind, 1, 13) = 'content_hash:';") ?? "0");
            i.NoShaFeatures = long.Parse(db.ExecScalar("SELECT count(*) FROM track_feature WHERE content_key_kind = 'entity';") ?? "0");
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
                ["tracks"] = i.Tracks, ["featureRows"] = i.FeatureRows, ["bytesPerTrack"] = Math.Round(i.BytesPerTrack, 1), ["projectedBytesAt156k"] = i.ProjectedBytesAt156k,
                ["medianRtf"] = i.MedianRtf.HasValue ? Math.Round(i.MedianRtf.Value, 2) : (double?)null, ["rtfSamples"] = i.RtfSamples,
                ["counts"] = counts, ["failedComponents"] = failed, ["coverage"] = cov,
                ["contentKeysShared"] = i.ContentKeysShared, ["locationsOnSharedKeys"] = i.LocationsOnSharedKeys,
                ["supersededHashes"] = i.SupersededHashes, ["noShaFeatures"] = i.NoShaFeatures,
            };
        }
    }
}
