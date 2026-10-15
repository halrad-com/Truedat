using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Truedat.Store
{
    /// <summary>
    /// <c>store-result.json</c> — the machine record of a store verb's outcome (store-v3 T11), written beside
    /// <c>truedat.db</c> on EVERY exit path of a verb that touched the store (the <c>apply-result.json</c> rule: a
    /// caller that captures only a bounded stdout tail must never have to parse it). The hub's result-file reader
    /// (hub:H03d) consumes it after every launched scan/verb exit; the field names below are the hub's, verbatim
    /// (restfulbee:architect, 2026-10-15 18:30Z) — the hub renders <c>messages[].text</c> as written and never
    /// composes wording of its own, so a message's text IS the operator-facing sentence.
    /// </summary>
    internal sealed class StoreResultFile
    {
        internal const string KindValue = "store-result";
        internal const int SchemaVersionValue = 1;
        internal const string FileName = "store-result.json";

        /// <summary>scan | store-import | store-export | edges-export | fixup | remap | migrate | merge-moods | backfill | strip | prune | restore.</summary>
        public string Verb = "";
        public string StartedUtc = "";
        public string FinishedUtc = "";
        /// <summary>The process exit code the launcher also sees; the file is the tiebreak when the launcher saw none.</summary>
        public int ExitCode;
        /// <summary>completed | interrupted | failed.</summary>
        public string Outcome = StoreResult.OutcomeCompleted;
        /// <summary>A verbatim sentence the hub renders; null when completed.</summary>
        public string? Reason;
        public StoreResultStore Store = new StoreResultStore();
        public StoreResultExport Export = new StoreResultExport();
        public StoreResultCapture? Capture;
        public StoreResultCoverage? Coverage;
        public List<StoreResultMessage> Messages = new List<StoreResultMessage>();
        /// <summary>Verb-specific counters (an import's matched/created counts, a scan's entries/failures) — outside
        /// the hub contract, under one key the hub ignores; for the operator and the self-tests.</summary>
        public Dictionary<string, long>? Detail;
    }

    internal sealed class StoreResultStore
    {
        public string Path = "";
        public int SchemaVersion;
        /// <summary>The COMMITTED generation after this verb (0 is valid: nothing has advanced it yet).</summary>
        public long Generation;
        public string TruedatVersion = "";
    }

    internal sealed class StoreResultFileRef
    {
        public string Path = "";
        /// <summary>64 lower-case hex; null only for a companion that was not hashed.</summary>
        public string? Sha256;
        public long Bytes;
    }

    /// <summary><c>performed</c> false means nothing on disk changed — the hub then expects M8 "behind" until the next export.</summary>
    internal sealed class StoreResultExport
    {
        public bool Performed;
        public long? Generation;
        public StoreResultFileRef? Catalog;
        public StoreResultFileRef? Sidecar;
        public StoreResultFileRef? Companion;
    }

    internal sealed class StoreResultCapture { public string EdgeArrays = "off"; public int StickyRowsRefreshed; }

    internal sealed class StoreResultCoverage { public long Tracks, WithEdges, WithHpcp, SpeechLikely, Excluded; }

    internal sealed class StoreResultMessage
    {
        /// <summary>M1 | M6 | M11 | M12 | M13 (unknown ids render as Info and are logged, never dropped).</summary>
        public string Id = "";
        public string Text = "";
        /// <summary>The exact command to run, rendered as the actionable part; null when there is nothing to type.</summary>
        public string? Action;
    }

    internal static class StoreResult
    {
        internal const string OutcomeCompleted = "completed", OutcomeInterrupted = "interrupted", OutcomeFailed = "failed";

        /// <summary>
        /// The scan's record, from the end-of-run sink report. Written by <see cref="ScanSink.Flush"/> itself (so a flush is
        /// self-contained and names its record) and again by the scan after the companion step, with the companion named
        /// by hash — latest wins. <c>export.performed</c> is false until T18 moves the scan's save onto the publisher: today
        /// the scan writes a v1 catalog. Never throws into the scan.
        /// </summary>
        internal static string? WriteScan(string? storePath, ScanSinkReport r, bool edgesOn, string? companionPath, string? startedUtc = null)
        {
            if (storePath == null) return null;
            var rec = Begin("scan");
            if (startedUtc != null) rec.StartedUtc = startedUtc;
            string fullStore;
            try { fullStore = Path.GetFullPath(storePath); } catch { fullStore = storePath; }
            rec.Store = new StoreResultStore { Path = fullStore, TruedatVersion = VersionInfo.Display };
            try
            {
                if (File.Exists(fullStore))
                {
                    using (StoreLog.Quiet())
                    using (var os = OpenStore.Open(fullStore, create: false, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
                    {
                        rec.Store.SchemaVersion = SchemaMigrator.ReadVersion(os.Db);
                        rec.Store.Generation = os.Dal.Generation();
                    }
                }
            }
            catch (Exception ex) { StoreLog.Warn($"store-result: store not readable for the record ({ex.Message})"); }
            rec.Export = new StoreResultExport { Performed = false, Companion = companionPath != null ? Describe(companionPath) : null };
            rec.Capture = new StoreResultCapture { EdgeArrays = "off" };   // the array flag ships with T05; edges on/off is the companion's presence
            rec.Detail = new Dictionary<string, long>
            {
                ["entries"] = r.Entries, ["failures"] = r.Failures, ["unchanged"] = r.Unchanged, ["remeasured"] = r.Remeasured, ["drift"] = r.Drift,
                ["contentReplaced"] = r.ContentReplaced, ["entitiesCreated"] = r.EntitiesCreated, ["edges"] = r.Edges, ["edgesFailed"] = r.EdgesFailed,
                ["edgesOn"] = edgesOn ? 1 : 0, ["elapsedMs"] = r.ElapsedMs,
            };
            var path = r.Ok
                ? Write(fullStore, rec, 0, OutcomeCompleted)
                : Write(fullStore, rec, Environment.ExitCode, OutcomeFailed, "store write failed: " + (r.Error ?? "unknown"));
            r.ResultPath = path;
            return path;
        }

        /// <summary>Beside the store: <c>&lt;store directory&gt;/store-result.json</c>.</summary>
        internal static string PathFor(string storePath)
            => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(storePath)) ?? ".", StoreResultFile.FileName);

        internal static StoreResultFile Begin(string verb)
            => new StoreResultFile { Verb = verb, StartedUtc = DateTime.UtcNow.ToString("o") };

        /// <summary>A file's path, SHA-256 (64 lower-case hex) and length; null when it does not exist.</summary>
        internal static StoreResultFileRef? Describe(string? path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            return new StoreResultFileRef { Path = path!, Sha256 = Publication.Sha256Hex(path!), Bytes = new FileInfo(path!).Length };
        }

        /// <summary>
        /// Finish and write the record atomically (temp + rename, UTF-8 without BOM). Never throws into the verb — a
        /// record that cannot be written is logged, because the verb's own outcome must not become the record's.
        /// Returns the path written, or null.
        /// </summary>
        internal static string? Write(string? storePath, StoreResultFile r, int exitCode, string outcome, string? reason = null)
        {
            r.ExitCode = exitCode; r.Outcome = outcome; r.Reason = reason;
            r.FinishedUtc = DateTime.UtcNow.ToString("o");
            if (string.IsNullOrWhiteSpace(storePath)) { StoreLog.Warn("store-result.json not written: no store path"); return null; }
            string path;
            try { path = PathFor(storePath!); } catch (Exception ex) { StoreLog.Warn($"store-result.json not written: {ex.Message}"); return null; }
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { StoreLog.Warn($"store-result.json not written: {dir} does not exist"); return null; }
            var tmp = path + ".tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 14))
                using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
                {
                    w.WriteStartObject();
                    w.WriteString("kind", StoreResultFile.KindValue);
                    w.WriteNumber("schemaVersion", StoreResultFile.SchemaVersionValue);
                    w.WriteString("verb", r.Verb);
                    w.WriteString("startedUtc", r.StartedUtc);
                    w.WriteString("finishedUtc", r.FinishedUtc);
                    w.WriteNumber("exitCode", r.ExitCode);
                    w.WriteString("outcome", r.Outcome);
                    if (r.Reason == null) w.WriteNull("reason"); else w.WriteString("reason", r.Reason);
                    w.WriteStartObject("store");
                    w.WriteString("path", r.Store.Path);
                    w.WriteNumber("schemaVersion", r.Store.SchemaVersion);
                    w.WriteNumber("generation", r.Store.Generation);
                    w.WriteString("truedatVersion", r.Store.TruedatVersion);
                    w.WriteEndObject();
                    w.WriteStartObject("export");
                    w.WriteBoolean("performed", r.Export.Performed);
                    if (r.Export.Generation.HasValue) w.WriteNumber("generation", r.Export.Generation.Value); else w.WriteNull("generation");
                    WriteRef(w, "catalog", r.Export.Catalog);
                    WriteRef(w, "sidecar", r.Export.Sidecar);
                    WriteRef(w, "companion", r.Export.Companion);
                    w.WriteEndObject();
                    if (r.Capture != null)
                    {
                        w.WriteStartObject("capture");
                        w.WriteString("edgeArrays", r.Capture.EdgeArrays);
                        w.WriteNumber("stickyRowsRefreshed", r.Capture.StickyRowsRefreshed);
                        w.WriteEndObject();
                    }
                    if (r.Coverage != null)
                    {
                        w.WriteStartObject("coverage");
                        w.WriteNumber("tracks", r.Coverage.Tracks); w.WriteNumber("withEdges", r.Coverage.WithEdges); w.WriteNumber("withHpcp", r.Coverage.WithHpcp);
                        w.WriteNumber("speechLikely", r.Coverage.SpeechLikely); w.WriteNumber("excluded", r.Coverage.Excluded);
                        w.WriteEndObject();
                    }
                    w.WriteStartArray("messages");
                    foreach (var m in r.Messages)
                    {
                        w.WriteStartObject();
                        w.WriteString("id", m.Id); w.WriteString("text", m.Text);
                        if (m.Action == null) w.WriteNull("action"); else w.WriteString("action", m.Action);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    if (r.Detail != null)
                    {
                        w.WriteStartObject("detail");
                        foreach (var kv in r.Detail) w.WriteNumber(kv.Key, kv.Value);
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                    w.Flush();
                    fs.Flush(true);
                }
                if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
                return path;
            }
            catch (Exception ex)
            {
                try { File.Delete(tmp); } catch { }
                StoreLog.Warn($"store-result.json not written: {ex.Message}");
                return null;
            }
        }

        static void WriteRef(Utf8JsonWriter w, string name, StoreResultFileRef? f)
        {
            if (f == null) { w.WriteNull(name); return; }
            w.WriteStartObject(name);
            w.WriteString("path", f.Path);
            if (f.Sha256 == null) w.WriteNull("sha256"); else w.WriteString("sha256", f.Sha256);
            w.WriteNumber("bytes", f.Bytes);
            w.WriteEndObject();
        }

        /// <summary>Read a record back (the self-tests and <c>--store-info</c>). False for a missing, malformed, or
        /// other-kind file; never throws.</summary>
        internal static bool TryRead(string path, out StoreResultFile r)
        {
            r = new StoreResultFile();
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;
                if (!root.TryGetProperty("kind", out var k) || k.GetString() != StoreResultFile.KindValue) return false;
                if (!root.TryGetProperty("schemaVersion", out var sv) || sv.GetInt32() != StoreResultFile.SchemaVersionValue) return false;
                r.Verb = Str(root, "verb"); r.StartedUtc = Str(root, "startedUtc"); r.FinishedUtc = Str(root, "finishedUtc");
                r.ExitCode = root.TryGetProperty("exitCode", out var ec) ? ec.GetInt32() : 0;
                r.Outcome = Str(root, "outcome"); r.Reason = NullableStr(root, "reason");
                if (root.TryGetProperty("store", out var s) && s.ValueKind == JsonValueKind.Object)
                    r.Store = new StoreResultStore { Path = Str(s, "path"), SchemaVersion = s.TryGetProperty("schemaVersion", out var ssv) ? ssv.GetInt32() : 0, Generation = s.TryGetProperty("generation", out var g) ? g.GetInt64() : 0, TruedatVersion = Str(s, "truedatVersion") };
                if (root.TryGetProperty("export", out var e) && e.ValueKind == JsonValueKind.Object)
                {
                    r.Export = new StoreResultExport
                    {
                        Performed = e.TryGetProperty("performed", out var p) && p.ValueKind == JsonValueKind.True,
                        Generation = e.TryGetProperty("generation", out var eg) && eg.ValueKind == JsonValueKind.Number ? eg.GetInt64() : (long?)null,
                        Catalog = ReadRef(e, "catalog"), Sidecar = ReadRef(e, "sidecar"), Companion = ReadRef(e, "companion"),
                    };
                }
                if (root.TryGetProperty("capture", out var c) && c.ValueKind == JsonValueKind.Object)
                    r.Capture = new StoreResultCapture { EdgeArrays = Str(c, "edgeArrays"), StickyRowsRefreshed = c.TryGetProperty("stickyRowsRefreshed", out var sr) ? sr.GetInt32() : 0 };
                if (root.TryGetProperty("coverage", out var cv) && cv.ValueKind == JsonValueKind.Object)
                    r.Coverage = new StoreResultCoverage { Tracks = L(cv, "tracks"), WithEdges = L(cv, "withEdges"), WithHpcp = L(cv, "withHpcp"), SpeechLikely = L(cv, "speechLikely"), Excluded = L(cv, "excluded") };
                if (root.TryGetProperty("messages", out var ms) && ms.ValueKind == JsonValueKind.Array)
                    foreach (var m in ms.EnumerateArray())
                        r.Messages.Add(new StoreResultMessage { Id = Str(m, "id"), Text = Str(m, "text"), Action = NullableStr(m, "action") });
                if (root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.Object)
                {
                    r.Detail = new Dictionary<string, long>();
                    foreach (var kv in d.EnumerateObject()) if (kv.Value.ValueKind == JsonValueKind.Number) r.Detail[kv.Name] = kv.Value.GetInt64();
                }
                return true;
            }
            catch { return false; }
        }

        static string Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        static string? NullableStr(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static long L(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        static StoreResultFileRef? ReadRef(JsonElement o, string name)
        {
            if (!o.TryGetProperty(name, out var f) || f.ValueKind != JsonValueKind.Object) return null;
            return new StoreResultFileRef { Path = Str(f, "path"), Sha256 = NullableStr(f, "sha256"), Bytes = L(f, "bytes") };
        }
    }
}
