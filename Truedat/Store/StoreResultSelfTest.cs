using System;
using System.Collections.Generic;
using System.IO;

namespace Truedat.Store
{
    /// <summary>Self-tests for <c>store-result.json</c> (store-v3 T11): the hub:H03d contract round-trips, every verb
    /// exit path writes it, and the export's record names the published files by hash.</summary>
    internal static class StoreResultSelfTest
    {
        internal static void Run(Action<bool, string> assert)
        {
            Console.WriteLine("Store-result self-test (store-v3 T11: the hub:H03d record round-trips, temp + rename, no BOM)");
            try { TestRoundTrip(assert); }
            catch (Exception ex) { assert(false, $"StoreResult.RoundTrip: threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine("Store-result self-test (store-v3 T11: --store-export writes the record on success AND on failure)");
            try { TestExportWritesIt(assert); }
            catch (Exception ex) { assert(false, $"StoreResult.Export: threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine("Store-result self-test (store-v3 T11: the scan's end-of-run record, ok and failed)");
            try { TestScanRecord(assert); }
            catch (Exception ex) { assert(false, $"StoreResult.Scan: threw {ex.GetType().Name}: {ex.Message}"); }
        }

        sealed class Dir : IDisposable
        {
            internal readonly string Root;
            internal Dir() { Root = Path.Combine(Path.GetTempPath(), "truedat-result-selftest-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); }
            internal string P(string name) => Path.Combine(Root, name);
            public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
        }

        static void TestRoundTrip(Action<bool, string> assert)
        {
            using (var d = new Dir())
            {
                var store = d.P("truedat.db");
                File.WriteAllText(d.P("a.bin"), "abc");
                var r = StoreResult.Begin("store-export");
                r.Store = new StoreResultStore { Path = store, SchemaVersion = 3, Generation = 7, TruedatVersion = "v-test" };
                r.Export = new StoreResultExport { Performed = true, Generation = 7, Catalog = StoreResult.Describe(d.P("a.bin")), Sidecar = null, Companion = new StoreResultFileRef { Path = "x", Sha256 = null, Bytes = 1 } };
                r.Capture = new StoreResultCapture { EdgeArrays = "on", StickyRowsRefreshed = 2 };
                r.Coverage = new StoreResultCoverage { Tracks = 10, WithEdges = 4, WithHpcp = 9, SpeechLikely = 1, Excluded = 0 };
                r.Messages.Add(new StoreResultMessage { Id = "M11", Text = "3 tracks hold edge arrays; sticky.", Action = "truedat --edges-discard --dry-run" });
                r.Messages.Add(new StoreResultMessage { Id = "M1", Text = "conditional", Action = null });
                r.Detail = new Dictionary<string, long> { ["entries"] = 5 };
                File.WriteAllText(StoreResult.PathFor(store), "{\"kind\":\"store-import-result\"}");   // the old shape is replaced
                var path = StoreResult.Write(store, r, 0, StoreResult.OutcomeCompleted);
                assert(path == StoreResult.PathFor(store) && File.Exists(path) && !File.Exists(path + ".tmp"), "Write: beside the store, temp + rename, no temp left");
                var bytes = File.ReadAllBytes(path!);
                assert(bytes.Length > 2 && bytes[0] == (byte)'{', "Write: UTF-8 without BOM, one JSON object");
                assert(StoreResult.TryRead(path!, out var back), "TryRead: the record reads back");
                assert(back.Verb == "store-export" && back.ExitCode == 0 && back.Outcome == "completed" && back.Reason == null && back.StartedUtc.EndsWith("Z") && back.FinishedUtc.EndsWith("Z"),
                    "round-trip: verb, exitCode, outcome, null reason, UTC stamps");
                assert(back.Store.Path == store && back.Store.SchemaVersion == 3 && back.Store.Generation == 7 && back.Store.TruedatVersion == "v-test", "round-trip: store block");
                assert(back.Export.Performed && back.Export.Generation == 7 && back.Export.Catalog != null && back.Export.Catalog.Sha256 == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad" && back.Export.Catalog.Bytes == 3
                       && back.Export.Sidecar == null && back.Export.Companion != null && back.Export.Companion.Sha256 == null,
                    "round-trip: export block incl. a hashed ref, a null ref and a ref with null sha");
                assert(back.Capture != null && back.Capture.EdgeArrays == "on" && back.Capture.StickyRowsRefreshed == 2, "round-trip: capture block");
                assert(back.Coverage != null && back.Coverage.Tracks == 10 && back.Coverage.WithEdges == 4 && back.Coverage.WithHpcp == 9 && back.Coverage.SpeechLikely == 1, "round-trip: coverage block");
                assert(back.Messages.Count == 2 && back.Messages[0].Id == "M11" && back.Messages[0].Action == "truedat --edges-discard --dry-run" && back.Messages[1].Action == null, "round-trip: messages verbatim, action nullable");
                assert(back.Detail != null && back.Detail["entries"] == 5, "round-trip: detail counters");
                // Failure + reason; the old file is replaced in place.
                var r2 = StoreResult.Begin("scan");
                var p2 = StoreResult.Write(store, r2, 1, StoreResult.OutcomeFailed, "store write failed: disk full");
                assert(p2 == path && StoreResult.TryRead(path!, out var b2) && b2.Verb == "scan" && b2.Outcome == "failed" && b2.ExitCode == 1 && b2.Reason == "store write failed: disk full", "Write: a later verb replaces the record; failed + reason round-trip");
                // Other kinds / malformed never throw.
                File.WriteAllText(d.P("other.json"), "{\"kind\":\"store-import-result\",\"schemaVersion\":1}");
                assert(!StoreResult.TryRead(d.P("other.json"), out _) && !StoreResult.TryRead(d.P("none.json"), out _), "TryRead: other kind / missing -> false");
                File.WriteAllText(d.P("bad.json"), "{nope");
                assert(!StoreResult.TryRead(d.P("bad.json"), out _), "TryRead: malformed -> false, no throw");
                // No store path / no directory: logged, never thrown.
                assert(StoreResult.Write(null, StoreResult.Begin("x"), 0, StoreResult.OutcomeCompleted) == null, "Write: no store path -> null, no throw");
                assert(StoreResult.Write(d.P("no-such-dir") + "\\truedat.db", StoreResult.Begin("x"), 0, StoreResult.OutcomeCompleted) == null, "Write: missing directory -> null, no throw");
            }
        }

        static Dictionary<string, TrackEntry> Fixture(string path, double bpm)
            => new Dictionary<string, TrackEntry>(PathComparer.Instance)
            {
                [path] = new TrackEntry { Features = new TrackFeatures { Artist = "A", Title = "T", Bpm = bpm, Mfcc = new[] { 1.5, -2.25 } }, LastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), AudioStreamSha256 = new string('b', 64) },
            };

        static void TestExportWritesIt(Action<bool, string> assert)
        {
            using (var d = new Dir())
            {
                var store = d.P("truedat.db");
                ImportCommand.ExecuteEntries(store, Fixture(@"D:\music\r.flac", 120), @"D:\music", "primary", true, "import", "1", "fixture", null, null);
                var outDir = d.P("out"); Directory.CreateDirectory(outDir);
                var outPath = Path.Combine(outDir, "mbxmoods.json");
                var resultPath = StoreResult.PathFor(store);

                int code = ExportCommand.Run(new ExportOptions { StorePath = store, OutPath = outPath });
                assert(code == 0 && File.Exists(resultPath), "export success: exit 0 and store-result.json beside the store");
                assert(StoreResult.TryRead(resultPath, out var ok) && ok.Verb == "store-export" && ok.Outcome == "completed" && ok.ExitCode == 0 && ok.Reason == null, "export success: verb/outcome/exitCode");
                assert(ok.Store.Path == Path.GetFullPath(store) && ok.Store.SchemaVersion >= 3 && ok.Store.Generation == 0 && ok.Store.TruedatVersion == VersionInfo.Display, $"export success: store block (schema {ok.Store.SchemaVersion}, generation {ok.Store.Generation})");
                assert(ok.Export.Performed && ok.Export.Generation == 0 && ok.Export.Catalog != null && ok.Export.Catalog.Path == outPath && ok.Export.Catalog.Sha256 == Publication.Sha256Hex(outPath),
                    "export success: export.performed, catalog ref hashes the published catalog");
                var sidecar = Path.ChangeExtension(outPath, ".mbxs");
                assert(ok.Export.Sidecar != null && ok.Export.Sidecar.Sha256 == Publication.Sha256Hex(sidecar) && CatalogOrigin.TryRead(outPath, out var o) && o.SidecarSha256 == ok.Export.Sidecar.Sha256,
                    "export success: sidecar ref == the catalog header's binding hash");
                assert(ok.Export.Companion == null, "export success: no edge rows -> companion null");
                assert(ok.Detail != null && ok.Detail["entries"] == 1, "export success: detail.entries");

                // Failure path: an unknown export root is refused by PickRoot (StoreException) — the record still lands.
                int code2 = ExportCommand.Run(new ExportOptions { StorePath = store, OutPath = outPath, ExportRoot = @"Z:\no-such-root" });
                bool readBad = StoreResult.TryRead(resultPath, out var bad);
                assert(code2 != 0 && readBad && bad.Verb == "store-export" && bad.Outcome == "failed" && bad.ExitCode == code2 && !string.IsNullOrEmpty(bad.Reason) && !bad.Export.Performed,
                    $"export failure: non-zero exit, record says failed with the reason, export.performed false ({bad.Reason})");
                assert(bad.Store.Path == Path.GetFullPath(store), "export failure: the store block still names the store");
            }
        }

        static void TestScanRecord(Action<bool, string> assert)
        {
            using (var d = new Dir())
            {
                var store = d.P("truedat.db");
                ImportCommand.ExecuteEntries(store, Fixture(@"D:\music\s.flac", 100), @"D:\music", "primary", true, "import", "1", "fixture", null, null);
                var rep = new ScanSinkReport { Ok = true, Entries = 3, Failures = 1, Edges = 2, EdgesFailed = 0, Remeasured = 1, EntitiesCreated = 2, ElapsedMs = 42, StorePath = store };
                File.WriteAllText(d.P("mbxedges.v1.json"), "{\"contract\":\"mbxedges.v1\",\"tracks\":{}}");
                var path = StoreResult.WriteScan(store, rep, edgesOn: true, companionPath: d.P("mbxedges.v1.json"), startedUtc: "2026-10-15T00:00:00.0000000Z");
                StoreResultFile r = new StoreResultFile();
                bool readOk = path != null && StoreResult.TryRead(path!, out r);
                assert(readOk && r.Verb == "scan" && r.Outcome == "completed" && r.ExitCode == 0 && r.StartedUtc == "2026-10-15T00:00:00.0000000Z",
                    "scan ok: verb scan, completed, the scan's start stamp");
                assert(r.Store.Path == Path.GetFullPath(store) && r.Store.SchemaVersion >= 3 && r.Store.TruedatVersion == VersionInfo.Display, "scan ok: store block read from the store");
                assert(!r.Export.Performed && r.Export.Catalog == null && r.Export.Companion != null && r.Export.Companion.Sha256 == Publication.Sha256Hex(d.P("mbxedges.v1.json")),
                    "scan ok: export.performed false (the scan path publishes with T18); the companion this run wrote is named by hash");
                assert(r.Capture != null && r.Capture.EdgeArrays == "off", "scan ok: capture block present (arrays off: the flag ships with T05)");
                assert(r.Detail != null && r.Detail["entries"] == 3 && r.Detail["failures"] == 1 && r.Detail["edges"] == 2 && r.Detail["remeasured"] == 1, "scan ok: detail counters");
                assert(rep.ResultPath == path, "scan ok: the sink report carries the record's path");

                var failed = new ScanSinkReport { Ok = false, Error = "unable to open database file", StorePath = store };
                var path2 = StoreResult.WriteScan(store, failed, edgesOn: false, companionPath: null, startedUtc: "2026-10-15T00:00:00.0000000Z");
                assert(path2 != null && StoreResult.TryRead(path2!, out var f) && f.Outcome == "failed" && f.Reason == "store write failed: unable to open database file" && f.Export.Companion == null,
                    "scan failed: failed + the reason sentence, no companion");
            }
        }
    }
}
