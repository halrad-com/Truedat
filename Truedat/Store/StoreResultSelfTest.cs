using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Truedat.Store
{
    /// <summary>Self-tests for <c>store-result.json</c> (store-v3 T11): the hub:H03d contract round-trips, every verb exit path
    /// writes it, the export's record names the published files by hash, and — the keeper's three compiled reproductions of
    /// 9706c5b — the scan's record carries the verb's FINAL outcome (exit 3 is never "completed / 0"), an export failure after
    /// publication keeps the publication facts, and an edges export records the generation its FILE was stamped with.</summary>
    internal static class StoreResultSelfTest
    {
        internal static void Run(Action<bool, string> assert)
        {
            Console.WriteLine("Store-result self-test (store-v3 T11: the hub:H03d record round-trips, temp + rename, no BOM)");
            try { TestRoundTrip(assert); }
            catch (Exception ex) { assert(false, $"StoreResult.RoundTrip: threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine("Store-result self-test (store-v3 T11: --store-export writes the record on success AND on failure; facts survive a late failure)");
            try { TestExportWritesIt(assert); }
            catch (Exception ex) { assert(false, $"StoreResult.Export: threw {ex.GetType().Name}: {ex.Message}"); }
            finally { ExportCommand.AfterPublished = null; }
            Console.WriteLine("Store-result self-test (store-v3 T11: the scan's record is provisional until the process boundary; outcome follows the verb)");
            try { TestScanRecord(assert); }
            catch (Exception ex) { assert(false, $"StoreResult.Scan: threw {ex.GetType().Name}: {ex.Message}"); }
            finally { StoreResult.ResetScanForTest(); }
            Console.WriteLine("Store-result self-test (store-v3 T11: a multi-flush scan — the record carries the tally; a failed checkpoint is re-queued, a failed final flush fails the record)");
            try { TestMultiFlushTally(assert); }
            catch (Exception ex) { assert(false, $"StoreResult.MultiFlush: threw {ex.GetType().Name}: {ex.Message}"); }
            finally { ScanSink.Reset(); StoreResult.ResetScanForTest(); }
            Console.WriteLine("Store-result self-test (store-v3 T11: a failed checkpoint keeps every tied payload — entry, artifact, edge fold — for the retry)");
            try { TestCheckpointPayloadRetry(assert); }
            catch (Exception ex) { assert(false, $"StoreResult.CheckpointPayload: threw {ex.GetType().Name}: {ex.Message}"); }
            finally { ScanSink.Reset(); StoreResult.ResetScanForTest(); }
            Console.WriteLine("Store-result self-test (store-v3 T11: a rollback after the writes leaves every pending file; a missing spill is a failed run, not a completed record)");
            try { TestPayloadRollbackAndLostSpill(assert); }
            catch (Exception ex) { assert(false, $"StoreResult.PayloadRollback: threw {ex.GetType().Name}: {ex.Message}"); }
            finally { ImportCommand.FailAfterEntriesForTest = 0; ScanSink.Reset(); StoreResult.ResetScanForTest(); }
            Console.WriteLine("Store-result self-test (store-v3 T11: a scan refusal writes its own failed record with the sentence and a non-zero exit)");
            try { TestRefuseScan(assert); }
            catch (Exception ex) { assert(false, $"StoreResult.RefuseScan: threw {ex.GetType().Name}: {ex.Message}"); }
            finally { StoreResult.ResetScanForTest(); }
            Console.WriteLine("Store-result self-test (store-v3 T11: the scan's failure rows commit with the entries — a failure there loses no payload)");
            try { TestFailureRowsSameTransaction(assert); }
            catch (Exception ex) { assert(false, $"StoreResult.FailureRowsTx: threw {ex.GetType().Name}: {ex.Message}"); }
            finally { ImportCommand.FailBeforeCommitForTest = false; ScanSink.Reset(); StoreResult.ResetScanForTest(); }
            Console.WriteLine("Store-result self-test (store-v3 T11: a failures-only flush commits its failed run in its own transaction)");
            try { TestFailuresOnlyFlush(assert); }
            catch (Exception ex) { assert(false, $"StoreResult.FailuresOnly: threw {ex.GetType().Name}: {ex.Message}"); }
            finally { ScanSink.Reset(); StoreResult.ResetScanForTest(); }
            Console.WriteLine("Store-result self-test (store-v3 T11: --edges-export records the generation the companion was stamped with)");
            try { TestEdgesExportGeneration(assert); }
            catch (Exception ex) { assert(false, $"StoreResult.EdgesExport: threw {ex.GetType().Name}: {ex.Message}"); }
            finally { Program.AfterEdgesCompanionWritten = null; }
            Console.WriteLine("Store-result self-test (keeper 099: the scan record's capture block is the scan's real array flag and its real sticky refreshes)");
            bool savedCapture = global::Truedat.Edges.EdgeStore.CaptureArraysDefault; var savedDir = ScanSink.StoreDirectory;
            try { TestScanCaptureFacts(assert); }
            catch (Exception ex) { assert(false, $"StoreResult.Capture: threw {ex.GetType().Name}: {ex.Message}"); }
            finally { global::Truedat.Edges.EdgeStore.CaptureArraysDefault = savedCapture; ScanSink.StoreDirectory = savedDir; ScanSink.Reset(); StoreResult.ResetScanForTest(); }
            Console.WriteLine("Store-result self-test (keeper 109: stickyRowsRefreshed counts rows a write actually refreshed — never an unavailable or failed fold's untouched rows; once per committed refresh)");
            try { TestStickyCountExact(assert); }
            catch (Exception ex) { assert(false, $"StoreResult.StickyExact: threw {ex.GetType().Name}: {ex.Message}"); }
            finally { ImportCommand.FailAfterEntriesForTest = 0; global::Truedat.Edges.EdgeStore.CaptureArraysDefault = savedCapture; ScanSink.StoreDirectory = savedDir; ScanSink.Reset(); StoreResult.ResetScanForTest(); }
            Console.WriteLine("Store-result self-test (TDA 092: the scan's final store step rewrites the companion when the RUN added edges — checkpoints included — and prints scan-wide totals)");
            var savedOut = Console.Out;
            try { TestFinishScanStoreCompanion(assert); }
            catch (Exception ex) { Console.SetOut(savedOut); assert(false, $"StoreResult.FinishCompanion: threw {ex.GetType().Name}: {ex.Message}"); }
            finally { Console.SetOut(savedOut); global::Truedat.Edges.EdgeStore.CaptureArraysDefault = savedCapture; ScanSink.StoreDirectory = savedDir; ScanSink.Reset(); StoreResult.ResetScanForTest(); }
            Console.WriteLine("Store-result self-test (keeper 113: the keeper's exact trigger — BPM 111 published, a checkpoint commits 222, an empty final flush — plus no-new-edges and a failed final flush)");
            try { TestCompanionKeeperTrigger(assert); }
            catch (Exception ex) { Console.SetOut(savedOut); assert(false, $"StoreResult.KeeperTrigger: threw {ex.GetType().Name}: {ex.Message}"); }
            finally { Console.SetOut(savedOut); ImportCommand.FailAfterEntriesForTest = 0; global::Truedat.Edges.EdgeStore.CaptureArraysDefault = savedCapture; ScanSink.StoreDirectory = savedDir; ScanSink.Reset(); StoreResult.ResetScanForTest(); }
            Console.WriteLine("Store-result self-test (store-v3 T11: --store-import writes the common record on success AND on failure)");
            try { TestImportWritesIt(assert); }
            catch (Exception ex) { assert(false, $"StoreResult.Import: threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine("Store-result self-test (store-v3 T06 Step 0: the nine catalog-changing verbs refuse with a store in force — failed record, store never created; no store = no refusal)");
            int savedExit = Environment.ExitCode;
            try { TestRefuseCatalogVerb(assert); }
            catch (Exception ex) { assert(false, $"StoreResult.RefuseCatalogVerb: threw {ex.GetType().Name}: {ex.Message}"); }
            finally { Environment.ExitCode = savedExit; }
        }

        /// <summary>T06 Step 0 (TDA 103 / keeper 118). The predicate is the run's resolved store path, so the helper is driven with
        /// a path and with null; the truth table over flags/config/--no-store and the nine real entry points is the CLI receipt
        /// (the verbs dispatch inside MainCore). What this pins: the record (verb spelling the hub reads, failed, exit 1, the
        /// sentence naming the store), that the refusal never opens or creates the store, and the --restore wording — a
        /// catalog import is not a restore and never removes entries.</summary>
        static void TestRefuseCatalogVerb(Action<bool, string> assert)
        {
            var nine = new[]
            {
                ("--fixup", "fixup"), ("--fixup --remap", "remap"), ("--verify --backfill", "backfill"), ("--merge-moods", "merge-moods"),
                ("--migrate", "migrate"), ("--strip-smfm", "strip"), ("--prune-entry", "prune"), ("--prune-excluded", "prune"), ("--restore", "restore"),
            };
            using (var d = new Dir())
            {
                var store = d.P("truedat.db");   // never created: a config store on an instance that has not scanned yet
                var resultPath = StoreResult.PathFor(store);
                foreach (var (flag, verb) in nine)
                {
                    Environment.ExitCode = 0;
                    var err = new StringWriter(); var savedErr = Console.Error; Console.SetError(err);
                    bool refused;
                    try { refused = Program.RefuseCatalogVerbWithStore(store, flag, verb); } finally { Console.SetError(savedErr); }
                    var rec = new StoreResultFile();
                    bool read = StoreResult.TryRead(resultPath, out rec);
                    var said = err.ToString();
                    assert(refused && Environment.ExitCode == 1 && read && rec.Verb == verb && rec.Outcome == "failed" && rec.ExitCode == 1
                        && rec.Store.Path == Path.GetFullPath(store) && rec.Store.SchemaVersion == null && rec.Store.Generation == null && !rec.Export.Performed,
                        $"{flag} with a store in force: refused, exit 1, record {verb}/failed/1, store facts unknown (never opened) ({refused}/{Environment.ExitCode}/{rec.Verb}/{rec.Outcome}/{rec.ExitCode}/{rec.Store.Path})");
                    assert(said.StartsWith("Error: " + flag + " changes mbxmoods.json only", StringComparison.Ordinal) && said.Contains(Path.GetFullPath(store)) && said.Contains("--no-store")
                        && rec.Reason != null && said.TrimEnd().EndsWith(rec.Reason, StringComparison.Ordinal),
                        $"{flag}: the sentence names the verb, the store path and what to do, and is the record's reason ({said.Trim()})");
                    assert(!File.Exists(store) && !File.Exists(store + "-wal") && Directory.GetFiles(d.Root).Length == 1,
                        $"{flag}: the refusal never opened or created the store; the record is the only file written ({string.Join(", ", Directory.GetFiles(d.Root).Select(Path.GetFileName))})");
                    File.Delete(resultPath);
                }
                // --restore: --store-import is named as seeding/extending, never as restoring the store.
                {
                    var err = new StringWriter(); var savedErr = Console.Error; Console.SetError(err);
                    try { Program.RefuseCatalogVerbWithStore(store, "--restore", "restore"); } finally { Console.SetError(savedErr); }
                    var said = err.ToString();
                    assert(said.Contains("seeds or extends the store") && said.Contains("it does not restore the store") && said.Contains("never removes store entries")
                        && !said.Contains("--store-import restores") && !said.Contains("restore the store with"),
                        $"--restore wording: an import seeds or extends, it is not a restore and never deletes ({said.Trim()})");
                    // T09: the refusal is now about THIS archive, not about the verb — it names the member a store snapshot
                    // carries and points at the snapshot that would have worked, so the operator is not left thinking a store
                    // can never be restored.
                    assert(said.Contains(StoreTransfer.StoreEntryName) && said.Contains("carries no store") && said.Contains("--snapshot"),
                        $"--restore wording: the refusal names the missing store member and the snapshot route ({said.Trim()})");
                    File.Delete(resultPath);
                }
                // No store in force (store off, or --no-store): no refusal, nothing written, exit code untouched.
                Environment.ExitCode = 0;
                foreach (var (flag, verb) in nine)
                    assert(!Program.RefuseCatalogVerbWithStore(null, flag, verb) && Environment.ExitCode == 0 && Directory.GetFiles(d.Root).Length == 0,
                        $"{flag} with no store in force: not refused, nothing written, exit code untouched");
            }
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
                assert(back.Verb == "store-export" && back.ExitCode == 0 && back.Outcome == "completed" && back.Reason == null && back.StartedUtc.EndsWith("Z") && back.FinishedUtc != null && back.FinishedUtc.EndsWith("Z") && back.WrittenUtc == back.FinishedUtc,
                    "round-trip: verb, exitCode, outcome, null reason, UTC stamps; a finished record's finishedUtc == writtenUtc");
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

        /// <summary>A store at a non-zero generation, so "generation 0" can never pass as "the generation was read".</summary>
        static long AdvanceTo(string store, long generation)
        {
            using var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o"));
            while (os.Dal.Generation() < generation) os.Dal.AdvanceGeneration();
            return os.Dal.Generation();
        }

        static int SchemaOf(string store)
        {
            using var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o"));
            return SchemaMigrator.ReadVersion(os.Db);
        }

        static void TestExportWritesIt(Action<bool, string> assert)
        {
            using (var d = new Dir())
            {
                var store = d.P("truedat.db");
                ImportCommand.ExecuteEntries(store, Fixture(@"D:\music\r.flac", 120), @"D:\music", "primary", true, "import", "1", "fixture", null, null);
                long gen = AdvanceTo(store, 7); int schema = SchemaOf(store);
                var outDir = d.P("out"); Directory.CreateDirectory(outDir);
                var outPath = Path.Combine(outDir, "mbxmoods.json");
                var resultPath = StoreResult.PathFor(store);

                int code = ExportCommand.Run(new ExportOptions { StorePath = store, OutPath = outPath });
                assert(code == 0 && File.Exists(resultPath), "export success: exit 0 and store-result.json beside the store");
                assert(StoreResult.TryRead(resultPath, out var ok) && ok.Verb == "store-export" && ok.Outcome == "completed" && ok.ExitCode == 0 && ok.Reason == null, "export success: verb/outcome/exitCode");
                assert(ok.Store.Path == Path.GetFullPath(store) && ok.Store.SchemaVersion == schema && ok.Store.Generation == gen && ok.Store.TruedatVersion == VersionInfo.Display, $"export success: store block (schema {ok.Store.SchemaVersion}, generation {ok.Store.Generation})");
                assert(ok.Export.Performed && ok.Export.Generation == gen && ok.Export.Catalog != null && ok.Export.Catalog.Path == outPath && ok.Export.Catalog.Sha256 == Publication.Sha256Hex(outPath),
                    "export success: export.performed, catalog ref hashes the published catalog");
                var sidecar = Path.ChangeExtension(outPath, ".mbxs");
                assert(ok.Export.Sidecar != null && ok.Export.Sidecar.Sha256 == Publication.Sha256Hex(sidecar) && CatalogOrigin.TryRead(outPath, out var o) && o.SidecarSha256 == ok.Export.Sidecar.Sha256,
                    "export success: sidecar ref == the catalog header's binding hash");
                assert(ok.Export.Companion == null, "export success: no edge rows -> companion null");
                assert(ok.Detail != null && ok.Detail["entries"] == 1, "export success: detail.entries");

                // Keeper repro (P2, first half): an unknown export root is refused by PickRoot AFTER the store opened — the record still
                // lands, and it keeps the schema and generation the store answered with (9706c5b wrote 0 / 0).
                int code2 = ExportCommand.Run(new ExportOptions { StorePath = store, OutPath = outPath, ExportRoot = @"Z:\no-such-root" });
                bool readBad = StoreResult.TryRead(resultPath, out var bad);
                assert(code2 != 0 && readBad && bad.Verb == "store-export" && bad.Outcome == "failed" && bad.ExitCode == code2 && !string.IsNullOrEmpty(bad.Reason) && !bad.Export.Performed,
                    $"export failure before publication: non-zero exit, record says failed with the reason, export.performed false ({bad.Reason})");
                assert(bad.Store.Path == Path.GetFullPath(store) && bad.Store.SchemaVersion == schema && bad.Store.Generation == gen,
                    $"export failure before publication: the store block keeps the schema ({bad.Store.SchemaVersion}) and generation ({bad.Store.Generation}) the store answered with — never 0 / 0");

                // Keeper repro (P2, second half): a failure AFTER the triple is published (their case: a directory sitting where key-moves.json
                // goes) must not erase the publication — the files are on disk at generation N and the record says so.
                ExportCommand.AfterPublished = () => throw new IOException("key-moves.json is a directory");
                int code3;
                try { code3 = ExportCommand.Run(new ExportOptions { StorePath = store, OutPath = outPath }); }
                finally { ExportCommand.AfterPublished = null; }
                bool readLate = StoreResult.TryRead(resultPath, out var late);
                assert(code3 == 1 && readLate && late.Outcome == "failed" && late.ExitCode == 1 && late.Reason != null && late.Reason.Contains("key-moves.json is a directory"),
                    $"export failure after publication: exit 1, failed, the reason names the late failure ({late.Reason})");
                assert(late.Export.Performed && late.Export.Generation == gen && late.Store.SchemaVersion == schema && late.Store.Generation == gen,
                    $"export failure after publication: export.performed TRUE at generation {late.Export.Generation} (schema {late.Store.SchemaVersion}) — the publication is a fact the failure does not erase");
                assert(late.Export.Catalog != null && late.Export.Catalog.Sha256 == Publication.Sha256Hex(outPath) && late.Export.Sidecar != null && late.Export.Sidecar.Sha256 == Publication.Sha256Hex(sidecar)
                       && CatalogOrigin.TryRead(outPath, out var o3) && o3.Generation == gen && o3.SidecarSha256 == late.Export.Sidecar.Sha256,
                    "export failure after publication: catalog + sidecar refs hash the files actually on disk, and the catalog header binds that sidecar at that generation");

                // No store path at all: the verb refuses and there is nowhere to put a record — no throw, non-zero exit.
                int code4 = ExportCommand.Run(new ExportOptions { StorePath = null, OutPath = outPath });
                assert(code4 != 0, "export with no store: refused without throwing");
            }
        }

        static ScanSinkReport OkReport(string store) => new ScanSinkReport { Ok = true, Entries = 3, Failures = 1, Edges = 2, EdgesFailed = 0, Remeasured = 1, EntitiesCreated = 2, ElapsedMs = 42, StorePath = store };

        static void TestScanRecord(Action<bool, string> assert)
        {
            const string started = "2026-10-15T00:00:00.0000000Z";
            StoreResult.ResetScanForTest();
            assert(StoreResult.FinishScan(0) == null, "FinishScan with nothing pending: null, writes nothing");
            assert(StoreResult.WriteScan(null, OkReport(""), false, null, started) == null, "WriteScan with no store: null, no record");

            using (var d = new Dir())
            {
                var store = d.P("truedat.db");
                ImportCommand.ExecuteEntries(store, Fixture(@"D:\music\s.flac", 100), @"D:\music", "primary", true, "import", "1", "fixture", null, null);
                var resultPath = StoreResult.PathFor(store);

                // 1. The scan begins: a provisional record replaces whatever verb wrote last — a refusal before the first flush still
                //    leaves a record, and a process killed here reads as interrupted (true).
                File.WriteAllText(resultPath, "{\"kind\":\"store-result\",\"schemaVersion\":1,\"verb\":\"store-export\",\"outcome\":\"completed\"}");
                StoreResult.BeginScan(store, started);
                assert(StoreResult.TryRead(resultPath, out var begun) && begun.Verb == "scan" && begun.Outcome == "interrupted" && begun.ExitCode == null && begun.Reason == StoreResult.ProvisionalReason && begun.StartedUtc == started && begun.Store.Path == Path.GetFullPath(store),
                    $"BeginScan: provisional record (verb scan, interrupted, the provisional reason, NO exit code) replaces the previous verb's ({begun.Verb}/{begun.Outcome}/{begun.ExitCode}/{begun.Reason})");
                assert(File.ReadAllText(resultPath).Contains("\"exitCode\": null"), "BeginScan: the bytes say exitCode null (the agreed pending-result rule), never 0");
                assert(begun.FinishedUtc == null && File.ReadAllText(resultPath).Contains("\"finishedUtc\": null") && begun.WrittenUtc.EndsWith("Z"),
                    $"BeginScan: finishedUtc null while provisional (a finish that has not happened is not observed), writtenUtc present ({begun.FinishedUtc}/{begun.WrittenUtc})");
                // A store that does not exist yet (a scan refused before its first flush): schema/generation are NOT READ -> null, not 0.
                using (var d0 = new Dir())
                {
                    StoreResult.BeginScan(d0.P("truedat.db"), started);
                    assert(StoreResult.TryRead(StoreResult.PathFor(d0.P("truedat.db")), out var none) && none.Store.SchemaVersion == null && none.Store.Generation == null && none.ExitCode == null,
                        $"BeginScan over a store that does not exist: schema/generation null (not read), exit code null ({none.Store.SchemaVersion}/{none.Store.Generation}/{none.ExitCode})");
                    var none2 = new StoreResultFile();
                    var fin0 = StoreResult.FinishScan(2);
                    assert(fin0 != null && StoreResult.TryRead(fin0!, out none2) && none2.ExitCode == 2 && none2.Outcome == "failed" && none2.Store.SchemaVersion == null,
                        $"a refusal before any flush: finished with the real exit code, store still not read ({none2.ExitCode}/{none2.Outcome}/{none2.Store.SchemaVersion})");
                }
                StoreResult.BeginScan(store, started);

                // 2. The flush writes its facts — still provisional. The keeper's P1 reproduction: 9706c5b wrote completed / 0 here.
                File.WriteAllText(d.P("mbxedges.v1.json"), "{\"contract\":\"mbxedges.v1\",\"tracks\":{}}");
                var path = StoreResult.WriteScan(store, OkReport(store), edgesOn: true, companionPath: d.P("mbxedges.v1.json"), startedUtc: started);
                StoreResultFile r = new StoreResultFile();
                bool readOk = path != null && path == resultPath && StoreResult.TryRead(path!, out r);
                assert(readOk, $"WriteScan: a non-null record path, THIS store's ({path})");
                assert(r.Verb == "scan" && r.Outcome == "interrupted" && r.ExitCode == null && r.Reason == StoreResult.ProvisionalReason && r.StartedUtc == started,
                    $"WriteScan: persistence success is NOT operation success — the record stays provisional until the boundary ({r.Outcome}/{r.ExitCode}/{r.Reason})");
                assert(r.Store.Path == Path.GetFullPath(store) && r.Store.SchemaVersion >= 3 && r.Store.TruedatVersion == VersionInfo.Display, "WriteScan: store block read from the store");
                assert(!r.Export.Performed && r.Export.Catalog == null && r.Export.Companion != null && r.Export.Companion.Sha256 == Publication.Sha256Hex(d.P("mbxedges.v1.json")),
                    "WriteScan: export.performed false (the scan path publishes with T18); the companion this run wrote is named by hash");
                assert(r.Capture != null && r.Capture.EdgeArrays == "off", "WriteScan: capture block present (arrays off: the flag ships with T05)");
                assert(r.Detail != null && r.Detail["entries"] == 3 && r.Detail["failures"] == 1 && r.Detail["edges"] == 2 && r.Detail["remeasured"] == 1 && r.Detail["checkpointFailures"] == 0, "WriteScan: detail counters");
                // "Killed after the first flush" (fixture-only, per the keeper): no FinishScan ever runs — the file on disk must still
                // say interrupted with NO exit code, never completed / 0.
                var killed = new StoreResultFile();
                assert(StoreResult.TryRead(resultPath, out killed) && killed.Outcome == "interrupted" && killed.ExitCode == null && File.ReadAllText(resultPath).Contains("\"exitCode\": null"),
                    $"process dies after a successful flush (no FinishScan): the record stays interrupted with exitCode null ({killed.Outcome}/{killed.ExitCode})");

                // 3. The boundary, exit 0, nothing noted: completed, null reason, every fact from the flush preserved. Then EXACTLY ONCE.
                var done = new StoreResultFile();
                var fin = StoreResult.FinishScan(0);
                assert(fin == resultPath && StoreResult.TryRead(resultPath, out done) && done.Outcome == "completed" && done.ExitCode == 0 && done.Reason == null
                       && done.Detail != null && done.Detail["entries"] == 3 && done.Export.Companion != null && done.StartedUtc == started && done.FinishedUtc != null && done.FinishedUtc == done.WrittenUtc,
                    $"FinishScan(0): completed, null reason, the flush's facts preserved ({done.Outcome}/{done.ExitCode}/{done.Reason})");
                var textAfterFinish = File.ReadAllText(resultPath);
                assert(StoreResult.FinishScan(3) == null && File.ReadAllText(resultPath) == textAfterFinish, "FinishScan twice: the second call is a no-op — the record is sealed, the file unchanged");

                // 4. The keeper's P1 reproduction, exact shape: the flush reported Ok, the scan then found its source gone (process exit 3).
                StoreResult.WriteScan(store, OkReport(store), edgesOn: false, companionPath: null, startedUtc: started);
                StoreResult.NoteScanOutcome(StoreResult.OutcomeInterrupted, "the source became unreachable — remaining paths were not examined: D:\\ offline");
                StoreResult.FinishScan(3);
                assert(StoreResult.TryRead(resultPath, out var lost) && lost.Outcome == "interrupted" && lost.ExitCode == 3 && lost.Reason != null && lost.Reason.StartsWith("the source became unreachable"),
                    $"source lost after a successful flush: interrupted / exit 3 / the sentence — never completed / 0 ({lost.Outcome}/{lost.ExitCode})");

                // 5. Analysis failures (exit 1); Ctrl+C (exit 0 but interrupted — the note outranks the exit code); exit non-zero with no note.
                StoreResult.WriteScan(store, OkReport(store), false, null, started);
                StoreResult.NoteScanOutcome(StoreResult.OutcomeFailed, "2 file(s) failed analysis — see mbxmoods-errors.csv");
                StoreResult.FinishScan(1);
                assert(StoreResult.TryRead(resultPath, out var fl) && fl.Outcome == "failed" && fl.ExitCode == 1 && fl.Reason == "2 file(s) failed analysis — see mbxmoods-errors.csv", $"analysis failures: failed / exit 1 / the count ({fl.Outcome}/{fl.ExitCode})");
                StoreResult.WriteScan(store, OkReport(store), false, null, started);
                StoreResult.NoteScanOutcome(StoreResult.OutcomeInterrupted, "interrupted (Ctrl+C) — progress up to that point was saved");
                StoreResult.FinishScan(0);
                assert(StoreResult.TryRead(resultPath, out var cc) && cc.Outcome == "interrupted" && cc.ExitCode == 0 && cc.Reason != null && cc.Reason.StartsWith("interrupted (Ctrl+C)"), $"Ctrl+C: interrupted even at exit 0 ({cc.Outcome}/{cc.ExitCode})");
                StoreResult.WriteScan(store, OkReport(store), false, null, started);
                StoreResult.FinishScan(3);
                assert(StoreResult.TryRead(resultPath, out var un) && un.Outcome == "failed" && un.ExitCode == 3 && un.Reason == "truedat exited 3; see the console output", $"non-zero exit with nothing noted: failed with a plain sentence ({un.Outcome}/{un.ExitCode}/{un.Reason})");

                // 6. A store write failure is final on its own and outranks a later note or exit 0: the catalog is complete, the STORE verb is not.
                var failed = new ScanSinkReport { Ok = false, Error = "unable to open database file", StorePath = store };
                var path2 = StoreResult.WriteScan(store, failed, edgesOn: false, companionPath: null, startedUtc: started);
                assert(path2 == resultPath && StoreResult.TryRead(path2!, out var f) && f.Outcome == "failed" && f.Reason == "store write failed: unable to open database file" && f.Export.Companion == null && f.ExitCode == null,
                    "store write failed: failed + the reason sentence at once, no companion, exit code still unknown");
                StoreResult.NoteScanOutcome(StoreResult.OutcomeInterrupted, "interrupted (Ctrl+C)");
                StoreResult.FinishScan(0);
                assert(StoreResult.TryRead(resultPath, out var f2) && f2.Outcome == "failed" && f2.Reason == "store write failed: unable to open database file" && f2.ExitCode == 0, $"store write failed outranks the note and exit 0 ({f2.Outcome}/{f2.Reason})");

                // 7. Crash finalization, once (packet item 4): Main's catch notes the error and finishes with the CLR code; its finally
                //    then runs FinishScan(Environment.ExitCode == 0) — which must NOT overwrite the crash code.
                StoreResult.WriteScan(store, OkReport(store), false, null, started);
                StoreResult.NoteScanOutcome(StoreResult.OutcomeFailed, "truedat stopped on an unexpected error: boom");
                var cr = new StoreResultFile();
                var crashPath = StoreResult.FinishScan(unchecked((int)0xE0434352));
                var afterCrash = StoreResult.FinishScan(0);
                assert(crashPath == resultPath && afterCrash == null && StoreResult.TryRead(resultPath, out cr) && cr.Outcome == "failed" && cr.ExitCode == unchecked((int)0xE0434352) && cr.Reason == "truedat stopped on an unexpected error: boom",
                    $"crash: the CLR exit code survives Main's finally ({cr.Outcome}/{cr.ExitCode})");

                // 8. Lifecycle isolation (the keeper's six scan-result failures): a record left pending for THIS store, whose directory is
                //    then deleted, must never receive the NEXT store's flush — the next flush gets its own record at its own path.
                StoreResult.WriteScan(store, OkReport(store), false, null, started);   // pending, never finished
                using (var d2 = new Dir())
                {
                    var store2 = d2.P("truedat.db");
                    ImportCommand.ExecuteEntries(store2, Fixture(@"D:\music\t.flac", 90), @"D:\music", "primary", true, "import", "1", "fixture", null, null);
                    d.Dispose();   // the first fixture is gone, its record still pending
                    var p3 = StoreResult.WriteScan(store2, OkReport(store2), false, null, started);
                    assert(p3 != null && p3 == StoreResult.PathFor(store2) && File.Exists(p3) && StoreResult.TryRead(p3!, out var iso) && iso.Store.Path == Path.GetFullPath(store2),
                        $"isolation: the next store's flush writes a non-null record at ITS path, not the deleted fixture's ({p3})");
                    var p4 = StoreResult.FinishScan(0);
                    assert(p4 == p3 && StoreResult.TryRead(p3!, out var iso2) && iso2.Outcome == "completed" && iso2.Store.Path == Path.GetFullPath(store2), "isolation: and finishes at that path");
                }
            }
        }

        static void TestMultiFlushTally(Action<bool, string> assert)
        {
            const string started = "2026-10-15T00:00:00.0000000Z";
            using (var d = new Dir())
            {
                var store = d.P("truedat.db");
                ScanSink.Reset(); StoreResult.ResetScanForTest();
                var prevDir = ScanSink.StoreDirectory; ScanSink.StoreDirectory = d.Root;
                var badStore = d.P("bad"); Directory.CreateDirectory(badStore);   // a DIRECTORY where the store should be: SQLite cannot open it (a missing directory would just be created)
                try
                {
                    TrackEntry E(string sha, double bpm) => new TrackEntry { Features = new TrackFeatures { Artist = "A", Title = "T", Bpm = bpm, Mfcc = new[] { 1.0, 2.0 } }, LastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), AudioStreamSha256 = sha };
                    var keys = new[] { @"D:\music\m1.flac", @"D:\music\m2.flac", @"D:\music\m3.flac" };
                    // checkpoint 1: two entries; checkpoint 2 FAILS (a path whose directory does not exist: the batch is re-queued, not dropped); final: the
                    // re-queued entry + one more. The tally is 4 entries over the scan, one checkpoint failure, Ok.
                    ScanSink.Record(keys[0], E(new string('1', 64), 100)); ScanSink.Record(keys[1], E(new string('2', 64), 101));
                    var c1 = ScanSink.Flush(store, "output-x64.5", keys, final: false);
                    ScanSink.Record(keys[2], E(new string('3', 64), 102));
                    var c2 = ScanSink.Flush(badStore, "output-x64.5", keys, final: false);
                    assert(c1.Ok && c1.Entries == 2 && !c2.Ok && ScanSink.Pending == 1 && ScanSink.Total.CheckpointFailures == 1 && ScanSink.Total.Ok,
                        $"a failed checkpoint re-queues its batch (pending {ScanSink.Pending}) and is counted ({ScanSink.Total.CheckpointFailures}); the tally is still ok ({ScanSink.Total.Ok}; c1 {c1.Ok}/{c1.Entries}, c2 {c2.Ok})");
                    ScanSink.Record(@"D:\music\m4.flac", E(new string('4', 64), 103));
                    var fin = ScanSink.Flush(store, "output-x64.5", keys, final: true);
                    assert(fin.Ok && fin.Entries == 2 && ScanSink.Total.Entries == 4 && ScanSink.Total.Ok && ScanSink.Total.CheckpointFailures == 1,
                        $"the final flush writes the re-queued entry too (this flush {fin.Entries}); the TALLY is 4 over the scan ({ScanSink.Total.Entries}), ok, 1 checkpoint failure");
                    var rec = new StoreResultFile();
                    var path = StoreResult.WriteScan(store, ScanSink.Total, false, null, started);
                    assert(path != null && StoreResult.TryRead(path!, out rec) && rec.Detail != null && rec.Detail["entries"] == 4 && rec.Detail["checkpointFailures"] == 1 && rec.Outcome == "interrupted" && rec.ExitCode == null,
                        $"the record carries the tally, not the last flush: entries {rec.Detail?["entries"]} (4), checkpointFailures {rec.Detail?["checkpointFailures"]} (1)");
                    var done = StoreResult.FinishScan(0);
                    assert(done != null && StoreResult.TryRead(done!, out var ok) && ok.Outcome == "completed" && ok.ExitCode == 0 && ok.Detail != null && ok.Detail["entries"] == 4, "finished: completed with the tally");
                    long rows; using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o"))) rows = os.Dal.Count("track_content");
                    assert(rows == 4, $"every entry of the scan is in the store, including the re-queued one ({rows} content rows)");

                    // A failed FINAL flush: the tally fails, the record says failed with the store reason, and FinishScan(0) keeps it failed.
                    ScanSink.Reset(); StoreResult.ResetScanForTest();
                    ScanSink.Record(@"D:\music\m5.flac", E(new string('5', 64), 104));
                    var bad = ScanSink.Flush(badStore, "output-x64.5", keys, final: true);
                    assert(!bad.Ok && !ScanSink.Total.Ok && ScanSink.Total.Error != null, $"a failed final flush fails the tally ({ScanSink.Total.Error})");
                    var failed = new StoreResultFile();
                    var p2 = StoreResult.WriteScan(store, ScanSink.Total, false, null, started);
                    StoreResult.FinishScan(0);
                    assert(p2 != null && StoreResult.TryRead(p2!, out failed) && failed.Outcome == "failed" && failed.ExitCode == 0 && failed.Reason != null && failed.Reason.StartsWith("store write failed:"),
                        $"the record says failed with the store reason even at exit 0 ({failed.Outcome}/{failed.ExitCode}/{failed.Reason})");
                }
                finally { ScanSink.StoreDirectory = prevDir; ScanSink.Reset(); StoreResult.ResetScanForTest(); }
            }
        }

        /// <summary>Keeper finding on b4622f7 (store-t11-b4622f7/checkpoint-payload-probe): a checkpoint that fails AFTER the edge spill was
        /// consumed lost the fold — the spill file was deleted at read, before the transaction committed — so the retry wrote the entry and
        /// the artifact but no edge rows, and the record said completed. Every tied payload must stay recoverable until its write commits.</summary>
        static void TestCheckpointPayloadRetry(Action<bool, string> assert)
        {
            const string started = "2026-10-15T00:00:00.0000000Z";
            using (var d = new Dir())
            {
                var store = d.P("truedat.db");
                ScanSink.Reset(); StoreResult.ResetScanForTest();
                var prevDir = ScanSink.StoreDirectory; ScanSink.StoreDirectory = d.Root;
                try
                {
                    var sha = new string('7', 64);
                    const string key = @"D:\music\payload.flac";
                    var entry = new TrackEntry { Features = new TrackFeatures { Artist = "A", Title = "T", Bpm = 100, Mfcc = new[] { 1.0, 2.0 } }, LastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), AudioStreamSha256 = sha };
                    var settings = new global::Truedat.Edges.EdgeSettings();
                    var fold = new global::Truedat.Edges.EdgeFoldResult { DurationMs = 240000, ProducerVersion = settings.ProducerVersion, Extractor = "output-x64.5" };
                    fold.Head.Status = "ok"; fold.Head.StartMs = 0; fold.Head.EndMs = 30000; fold.Tail.Status = "ok"; fold.Tail.StartMs = 210000; fold.Tail.EndMs = 240000;
                    fold.Head.Bpm = new global::Truedat.Edges.EdgeBpm { Value = 123.5, Beats = 60, Regularity = 0.9 };
                    // The extractor core's order: main JSON stashed, the fold spilled, then Record ties both to the entry.
                    ScanSink.StashMainJson(key, "{\"lowlevel\":{\"spectral_centroid\":{\"mean\":1.0}}}");
                    ScanSink.StashEdges(key, fold);
                    ScanSink.Record(key, entry, key);
                    assert(ScanSink.Pending == 1 && ScanSink.PendingArtifacts == 1 && ScanSink.PendingEdges == 1, $"tied: 1 entry, 1 artifact, 1 edge spill pending ({ScanSink.Pending}/{ScanSink.PendingArtifacts}/{ScanSink.PendingEdges})");
                    // The keeper's trigger: a CHECKPOINT whose artifact commit refuses the producer build AFTER the entry and the fold were consumed.
                    var c = ScanSink.Flush(store, "bad/build", new[] { key }, final: false);
                    assert(!c.Ok && ScanSink.Pending == 1 && ScanSink.PendingArtifacts == 1 && ScanSink.PendingEdges == 1 && ScanSink.Total.CheckpointFailures == 1,
                        $"the failed checkpoint re-queued the entry WITH its artifact and its edge spill ({c.Ok}; {ScanSink.Pending}/{ScanSink.PendingArtifacts}/{ScanSink.PendingEdges}; {c.Error})");
                    // The valid retry must commit ALL of it — entry, artifact AND the edge rows — or report a real failure; never complete having dropped the edges.
                    var fin = ScanSink.Flush(store, "output-x64.5", new[] { key }, final: true);
                    long content, artifacts, segments, values; global::Truedat.Edges.EdgeFoldResult? back;
                    using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
                    {
                        content = os.Dal.Count("track_content"); artifacts = os.Dal.Count("artifact");
                        segments = os.Dal.CountSegments(sha, settings.ProducerVersion); values = os.Dal.ScalarLong("SELECT count(*) FROM edge WHERE content_hash = ?;", sha);
                        back = global::Truedat.Edges.EdgeStore.Read(os.Dal, sha, settings.ProducerVersion);
                    }
                    assert(fin.Ok && fin.Entries == 1 && fin.Edges == 1 && fin.EdgesFailed == 0, $"retry: ok, 1 entry, 1 edge result written ({fin.Ok}; entries {fin.Entries}, edges {fin.Edges}, failed {fin.EdgesFailed}; {fin.Error})");
                    assert(content == 1 && artifacts == 1 && segments == 2 && values == 2, $"retry committed the whole payload: content {content} (1), artifact {artifacts} (1), edge segments {segments} (2), typed edge rows {values} (2)");
                    assert(back != null && back.Status == "ok" && back.DurationMs == 240000 && back.Extractor == "output-x64.5" && back.Head.Bpm != null && Math.Abs(back.Head.Bpm.Value - 123.5) < 1e-4 && back.Head.Bpm.Beats == 60 && back.Tail.StartMs == 210000,
                        $"the edge rows read back as the fold that was spilled (status {back?.Status}, duration {back?.DurationMs}, extractor {back?.Extractor}, head bpm {back?.Head.Bpm?.Value}, tail start {back?.Tail.StartMs})");
                    var rec = new StoreResultFile();
                    var path = StoreResult.WriteScan(store, ScanSink.Total, true, null, started);
                    var fin2 = StoreResult.FinishScan(0);
                    assert(path != null && fin2 == path && StoreResult.TryRead(path!, out rec) && rec.Outcome == "completed" && rec.Detail != null && rec.Detail["entries"] == 1 && rec.Detail["edges"] == 1 && rec.Detail["checkpointFailures"] == 1,
                        $"the record: completed only because ALL payload committed — entries {rec.Detail?["entries"]} (1), edges {rec.Detail?["edges"]} (1), checkpointFailures {rec.Detail?["checkpointFailures"]} (1)");
                }
                finally { ScanSink.StoreDirectory = prevDir; ScanSink.Reset(); StoreResult.ResetScanForTest(); }
            }
        }

        /// <summary>TDA regressions (2) and (3) on keeper 006: a rollback AFTER the artifact rows were written leaves every pending file where it
        /// was and no row-less committed file; a spill that is missing at write time is a FAILED edges run and the record is not completed.</summary>
        static void TestPayloadRollbackAndLostSpill(Action<bool, string> assert)
        {
            const string started = "2026-10-15T00:00:00.0000000Z";
            using (var d = new Dir())
            {
                var store = d.P("truedat.db");
                ScanSink.Reset(); StoreResult.ResetScanForTest();
                var prevDir = ScanSink.StoreDirectory; ScanSink.StoreDirectory = d.Root;
                try
                {
                    TrackEntry E(string sha) => new TrackEntry { Features = new TrackFeatures { Artist = "A", Title = "T", Bpm = 100, Mfcc = new[] { 1.0, 2.0 } }, LastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), AudioStreamSha256 = sha };
                    var settings = new global::Truedat.Edges.EdgeSettings();
                    global::Truedat.Edges.EdgeFoldResult Fold(double bpm)
                    {
                        var f = new global::Truedat.Edges.EdgeFoldResult { DurationMs = 240000, ProducerVersion = settings.ProducerVersion, Extractor = "output-x64.5" };
                        f.Head.Status = "ok"; f.Head.EndMs = 30000; f.Tail.Status = "ok"; f.Tail.StartMs = 210000; f.Tail.EndMs = 240000;
                        f.Head.Bpm = new global::Truedat.Edges.EdgeBpm { Value = bpm, Beats = 60, Regularity = 0.9 };
                        return f;
                    }
                    string k1 = @"D:\music\rb1.flac", k2 = @"D:\music\rb2.flac"; string sha1 = new string('8', 64), sha2 = new string('9', 64);
                    foreach (var (k, sha, bpm, json) in new[] { (k1, sha1, 111.0, "{\"a\":1}"), (k2, sha2, 112.0, "{\"a\":2}") })
                    { ScanSink.StashMainJson(k, json); ScanSink.StashEdges(k, Fold(bpm)); ScanSink.Record(k, E(sha), k); }
                    var pend1 = ScanSink.PendingArtifactFile(k1)!; var pend2 = ScanSink.PendingArtifactFile(k2)!; var spill1 = ScanSink.PendingEdgeFile(k1)!; var spill2 = ScanSink.PendingEdgeFile(k2)!;
                    string PendSha(string f) { using var h = System.Security.Cryptography.SHA256.Create(); using var fs = File.OpenRead(f); return BitConverter.ToString(h.ComputeHash(fs)).Replace("-", "").ToLowerInvariant(); }
                    var pendSha1 = PendSha(pend1);
                    int CommittedFiles() => Directory.Exists(d.P("artifacts")) ? Directory.GetFiles(d.P("artifacts"), "*", SearchOption.AllDirectories).Count(f => !f.Contains(Path.DirectorySeparatorChar + "pending" + Path.DirectorySeparatorChar)) : 0;

                    // (2) the transaction fails AFTER both entries (artifact rows + planned moves, edge rows) were written: rollback must leave the
                    //     pending files intact and NOTHING committed on disk — no row-less artifact file.
                    ImportCommand.FailAfterEntriesForTest = 2;
                    var c = ScanSink.Flush(store, "output-x64.5", new[] { k1, k2 }, final: false);
                    ImportCommand.FailAfterEntriesForTest = 0;
                    assert(!c.Ok && ScanSink.Pending == 2 && ScanSink.PendingArtifacts == 2 && ScanSink.PendingEdges == 2, $"rollback after the writes: all re-queued ({c.Ok}; {ScanSink.Pending}/{ScanSink.PendingArtifacts}/{ScanSink.PendingEdges}; {c.Error})");
                    assert(File.Exists(pend1) && File.Exists(pend2) && File.Exists(spill1) && File.Exists(spill2) && CommittedFiles() == 0,
                        $"rollback after the writes: every pending file is still where the sink put it and no artifact file exists without its row (pending {File.Exists(pend1)}/{File.Exists(pend2)}, spills {File.Exists(spill1)}/{File.Exists(spill2)}, committed files {CommittedFiles()})");
                    long rowsAfterRollback; using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o"))) rowsAfterRollback = os.Dal.Count("artifact") + os.Dal.Count("track_content");
                    assert(rowsAfterRollback == 0, $"rollback after the writes: no rows ({rowsAfterRollback})");
                    // The retry commits all of it, and the committed artifact files ARE the pending ones (sha256 identity); the spills are gone after the commit.
                    var fin = ScanSink.Flush(store, "output-x64.5", new[] { k1, k2 }, final: true);
                    long arts, segs1, segs2; string? relSha1;
                    using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
                    { arts = os.Dal.Count("artifact"); segs1 = os.Dal.CountSegments(sha1, settings.ProducerVersion); segs2 = os.Dal.CountSegments(sha2, settings.ProducerVersion); relSha1 = os.Db.ExecScalar("SELECT sha256 FROM artifact WHERE content_hash = '" + sha1 + "';"); }
                    var committed1 = Path.Combine(d.Root, ArtifactStore.RelativePathFor(sha1, "output-x64.5"));
                    assert(fin.Ok && fin.Entries == 2 && fin.Edges == 2 && arts == 2 && segs1 == 2 && segs2 == 2 && CommittedFiles() == 2 && File.Exists(committed1) && PendSha(committed1) == pendSha1 && relSha1 == pendSha1 && !File.Exists(pend1) && !File.Exists(spill1) && !File.Exists(spill2),
                        $"retry: 2 entries, 2 edge results, 2 artifact rows whose files are the pending ones (sha identity {relSha1 == pendSha1}), spills deleted after the commit (ok {fin.Ok}, edges {fin.Edges}, artifacts {arts}, segs {segs1}/{segs2}, files {CommittedFiles()})");

                    // (3) a spill that is MISSING at write time: a failed edges run naming the reason, SpillsLost counted, the record failed — never "no fold" + completed.
                    ScanSink.Reset(); StoreResult.ResetScanForTest();
                    const string k3 = @"D:\music\rb3.flac"; var sha3 = new string('a', 64);
                    ScanSink.StashEdges(k3, Fold(113)); ScanSink.Record(k3, E(sha3), k3);
                    var spill3 = ScanSink.PendingEdgeFile(k3)!; File.Delete(spill3);
                    var lost = ScanSink.Flush(store, "output-x64.5", new[] { k3 }, final: true);
                    long failedRuns; bool stillLacking; string? component;
                    using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
                    {
                        failedRuns = long.Parse(os.Db.ExecScalar("SELECT count(*) FROM run WHERE producer_key = 'edges' AND outcome = 'failed' AND error_text LIKE '%spilled edge fold%';") ?? "0");
                        component = os.Db.ExecScalar("SELECT failed_components FROM run WHERE producer_key = 'edges' AND outcome = 'failed' AND error_text LIKE '%spilled edge fold%';");
                        stillLacking = os.Dal.ContentKeysLackingEdges(global::Truedat.Edges.EdgeStore.ProducerKey, settings.ProducerVersion, includeFailed: false).Contains(sha3);
                    }
                    assert(lost.Ok && lost.Entries == 1 && lost.Edges == 0 && lost.EdgesFailed == 1 && lost.SpillsLost == 1 && failedRuns == 1,
                        $"a missing spill is a FAILED edges run naming the reason, counted as lost — never 'no fold' (edges {lost.Edges}, failed {lost.EdgesFailed}, lost {lost.SpillsLost}, failed runs naming it {failedRuns})");
                    // TDA N1: a lost spill is a STORE-side loss, so it must not park the track — the failed run is marked 'spill-lost' and a plain
                    // --refresh (no --retry-errors) still lists the track as lacking edges, exactly as the record's sentence promises.
                    assert(component == "spill-lost" && stillLacking, $"a lost spill does not park the track: run component '{component}' (spill-lost), still lacking edges for a plain --refresh = {stillLacking}");
                    var rec = new StoreResultFile();
                    var path = StoreResult.WriteScan(store, ScanSink.Total, true, null, started); StoreResult.FinishScan(0);
                    assert(path != null && StoreResult.TryRead(path!, out rec) && rec.Outcome == "failed" && rec.ExitCode == 0 && rec.Reason != null && rec.Reason.StartsWith("store write lost 1 edge fold") && rec.Detail != null && rec.Detail["spillsLost"] == 1,
                        $"the record is NOT completed when a fold was lost: {rec.Outcome} / {rec.Reason}");

                    // (4) a fold that could not be SPILLED at all (disk full, the pending directory unwritable): the same rule as (3) — a failed
                    //     edges run naming the cause, counted as lost, the track still lacking edges. Before: StashEdges warned and dropped the
                    //     fold, so the entry arrived with no fold, no edges run was written, the summary said "0 folds failed", and the track had
                    //     no edges with nothing in the store saying why.
                    ScanSink.Reset(); StoreResult.ResetScanForTest();
                    const string k4 = @"D:\music\rb4.flac"; var sha4 = new string('b', 64);
                    ScanSink.FailEdgeStashForTest = "There is not enough space on the disk.";
                    try { ScanSink.StashEdges(k4, Fold(114)); } finally { ScanSink.FailEdgeStashForTest = null; }
                    ScanSink.Record(k4, E(sha4), k4);
                    var unspilled = ScanSink.Flush(store, "output-x64.5", new[] { k4 }, final: true);
                    long failedRuns4; bool lacking4; string? component4;
                    using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
                    {
                        failedRuns4 = long.Parse(os.Db.ExecScalar("SELECT count(*) FROM run WHERE producer_key = 'edges' AND outcome = 'failed' AND content_hash = '" + sha4 + "' AND error_text LIKE '%not enough space%';") ?? "0");
                        component4 = os.Db.ExecScalar("SELECT failed_components FROM run WHERE producer_key = 'edges' AND outcome = 'failed' AND content_hash = '" + sha4 + "';");
                        lacking4 = os.Dal.ContentKeysLackingEdges(global::Truedat.Edges.EdgeStore.ProducerKey, settings.ProducerVersion, includeFailed: false).Contains(sha4);
                    }
                    assert(unspilled.Ok && unspilled.Entries == 1 && unspilled.Edges == 0 && unspilled.EdgesFailed == 1 && unspilled.SpillsLost == 1 && failedRuns4 == 1 && component4 == "spill-lost" && lacking4,
                        $"a fold that could not be spilled is a FAILED edges run naming the cause, never silently dropped (edges {unspilled.Edges}, failed {unspilled.EdgesFailed}, lost {unspilled.SpillsLost}, failed runs naming it {failedRuns4}, component '{component4}', still lacking {lacking4})");
                }
                finally { ImportCommand.FailAfterEntriesForTest = 0; ScanSink.FailEdgeStashForTest = null; ScanSink.StoreDirectory = prevDir; ScanSink.Reset(); StoreResult.ResetScanForTest(); }
            }
        }

        /// <summary>Keeper P2-c/P2-d (TDA on keeper 007): every scan refusal after the store is resolved writes ITS OWN scan/failed record
        /// with the exact sentence and a non-zero exit code — the previous verb's record is never left standing, and a refused scan is
        /// never "completed".</summary>
        static void TestRefuseScan(Action<bool, string> assert)
        {
            using (var d = new Dir())
            {
                var store = d.P("truedat.db");
                StoreResult.ResetScanForTest();
                var resultPath = StoreResult.PathFor(store);
                // The previous verb's record (a completed import) sits beside the store.
                var prev = StoreResult.Begin("store-import"); prev.Store = new StoreResultStore { Path = store, SchemaVersion = 3, Generation = 0, TruedatVersion = "v" };
                StoreResult.Write(store, prev, 0, StoreResult.OutcomeCompleted);
                foreach (var (sentence, code, expectReason) in new[]
                {
                    ("Error: Folder not found: Q:\\absent", 1, "Folder not found: Q:\\absent"),
                    ("Error: --folder is mutually exclusive with --file-list and --analyze-file.", 1, "--folder is mutually exclusive with --file-list and --analyze-file."),
                    ("Error: iTunes library not found: Q:\\nope\\iTunes Music Library.xml (run --paths to see what resolved)", 2, "iTunes library not found: Q:\\nope\\iTunes Music Library.xml (run --paths to see what resolved)"),
                })
                {
                    StoreResult.ResetScanForTest();
                    int exit = Program.RefuseScan(store, sentence, code);
                    var fin = StoreResult.FinishScan(exit);
                    var rec = new StoreResultFile();
                    assert(exit == code && fin == resultPath && StoreResult.TryRead(resultPath, out rec) && rec.Verb == "scan" && rec.Outcome == "failed" && rec.ExitCode == code && rec.Reason == expectReason,
                        $"refusal '{sentence}': scan / failed / exit {code} / the sentence as the reason ({rec.Verb}/{rec.Outcome}/{rec.ExitCode}/{rec.Reason})");
                }
                // No store in force: nothing to write, no throw, the exit code still comes back.
                StoreResult.ResetScanForTest();
                assert(Program.RefuseScan(null, "Error: Folder not found: Q:\\x", 1) == 1 && StoreResult.FinishScan(1) == null, "refusal with no store: exit code returned, no record");
            }
        }

        /// <summary>TDA N2 on 3244dbe: the scan's failure rows were written in a SECOND transaction after the entries had committed, Finish had
        /// moved the artifacts and the spills were deleted; a failure there re-queued the whole flush — committed entries with no spills — and the
        /// retry recorded spurious lost folds. They must commit together.</summary>
        static void TestFailureRowsSameTransaction(Action<bool, string> assert)
        {
            const string started = "2026-10-15T00:00:00.0000000Z";
            using (var d = new Dir())
            {
                var store = d.P("truedat.db");
                ScanSink.Reset(); StoreResult.ResetScanForTest();
                var prevDir = ScanSink.StoreDirectory; ScanSink.StoreDirectory = d.Root;
                try
                {
                    TrackEntry E(string sha) => new TrackEntry { Features = new TrackFeatures { Artist = "A", Title = "T", Bpm = 100, Mfcc = new[] { 1.0, 2.0 } }, LastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), AudioStreamSha256 = sha };
                    var settings = new global::Truedat.Edges.EdgeSettings();
                    global::Truedat.Edges.EdgeFoldResult Fold()
                    {
                        var f = new global::Truedat.Edges.EdgeFoldResult { DurationMs = 240000, ProducerVersion = settings.ProducerVersion, Extractor = "output-x64.5" };
                        f.Head.Status = "ok"; f.Head.EndMs = 30000; f.Tail.Status = "ok"; f.Tail.StartMs = 210000; f.Tail.EndMs = 240000; return f;
                    }
                    string k1 = @"D:\music\ft1.flac", k2 = @"D:\music\ft2.flac";
                    foreach (var (k, sha, json) in new[] { (k1, new string('c', 64), "{\"a\":1}"), (k2, new string('d', 64), "{\"a\":2}") })
                    { ScanSink.StashMainJson(k, json); ScanSink.StashEdges(k, Fold()); ScanSink.Record(k, E(sha), k); }
                    ScanSink.RecordFailure(@"D:\music\bad.flac", "essentia", "essentia exit 1: decode error", 12.3);
                    var pend1 = ScanSink.PendingArtifactFile(k1)!; var spill1 = ScanSink.PendingEdgeFile(k1)!; var spill2 = ScanSink.PendingEdgeFile(k2)!;
                    int CommittedFiles() => Directory.Exists(d.P("artifacts")) ? Directory.GetFiles(d.P("artifacts"), "*", SearchOption.AllDirectories).Count(f => !f.Contains(Path.DirectorySeparatorChar + "pending" + Path.DirectorySeparatorChar)) : 0;

                    ImportCommand.FailBeforeCommitForTest = true;   // throws right after the failure rows were written, still inside the transaction
                    var c = ScanSink.Flush(store, "output-x64.5", new[] { k1, k2 }, final: false);
                    ImportCommand.FailBeforeCommitForTest = false;
                    long rows, failRows;
                    using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
                    { rows = os.Dal.Count("track_content"); failRows = long.Parse(os.Db.ExecScalar("SELECT count(*) FROM run WHERE outcome = 'failed';") ?? "0"); }
                    assert(!c.Ok && rows == 0 && failRows == 0 && File.Exists(pend1) && File.Exists(spill1) && File.Exists(spill2) && CommittedFiles() == 0 && ScanSink.Pending == 2 && ScanSink.PendingFailures == 1 && ScanSink.PendingEdges == 2,
                        $"a failure while writing the failure rows commits NOTHING (rows {rows}, failed runs {failRows}), every pending file is present (artifact {File.Exists(pend1)}, spills {File.Exists(spill1)}/{File.Exists(spill2)}, committed files {CommittedFiles()}), everything re-queued ({ScanSink.Pending}/{ScanSink.PendingFailures}/{ScanSink.PendingEdges})");
                    var fin = ScanSink.Flush(store, "output-x64.5", new[] { k1, k2 }, final: true);
                    using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
                    { rows = os.Dal.Count("track_content"); failRows = long.Parse(os.Db.ExecScalar("SELECT count(*) FROM run WHERE outcome = 'failed';") ?? "0"); }
                    var rec = new StoreResultFile();
                    var path = StoreResult.WriteScan(store, ScanSink.Total, true, null, started); StoreResult.FinishScan(0);
                    assert(fin.Ok && fin.Entries == 2 && fin.Edges == 2 && fin.SpillsLost == 0 && fin.Failures == 1 && rows == 2 && failRows == 1 && path != null && StoreResult.TryRead(path!, out rec) && rec.Outcome == "completed" && rec.Detail != null && rec.Detail["spillsLost"] == 0 && rec.Detail["failures"] == 1,
                        $"the clean retry commits entries, edges AND the failure row together, no lost fold: ok {fin.Ok}, entries {fin.Entries}, edges {fin.Edges}, lost {fin.SpillsLost}, failures {fin.Failures}, rows {rows}, failed runs {failRows}, record {rec.Outcome}");
                    // Operator 2026-10-22: the record's detail accounts for EVERY entry — the buckets it names sum to `entries` (MB3's EV3 record named 1,399 of 7,655).
                    long Det(string k) => rec.Detail != null && rec.Detail.TryGetValue(k, out var v) ? v : -1;
                    long bucketSum = 0; foreach (var k in StoreResult.EntryBuckets) bucketSum += Det(k);
                    assert(rec.Detail != null && bucketSum == Det("entries") && Det("entries") == 2,
                        $"store-result detail buckets sum to entries ({bucketSum} of {Det("entries")}: {string.Join(", ", System.Linq.Enumerable.Select(StoreResult.EntryBuckets, k => k + "=" + Det(k)))})");
                }
                finally { ImportCommand.FailBeforeCommitForTest = false; ScanSink.StoreDirectory = prevDir; ScanSink.Reset(); StoreResult.ResetScanForTest(); }
            }
        }

        /// <summary>TDA Z1 on 76cf2da: a flush with ZERO entries and some failures (an --analyze-file / --file-list call whose only file failed)
        /// takes ImportEntriesCore's own transaction for the failure rows — it must commit exactly one failed run (components + error) and the
        /// record must say failures 1.</summary>
        static void TestFailuresOnlyFlush(Action<bool, string> assert)
        {
            const string started = "2026-10-15T00:00:00.0000000Z";
            using (var d = new Dir())
            {
                var store = d.P("truedat.db");
                ScanSink.Reset(); StoreResult.ResetScanForTest();
                var prevDir = ScanSink.StoreDirectory; ScanSink.StoreDirectory = d.Root;
                try
                {
                    const string only = @"D:\music\only.flac";
                    ScanSink.RecordFailure(only, "essentia", "essentia exit 1: decode error", 12.3);
                    assert(ScanSink.Pending == 0 && ScanSink.PendingFailures == 1, "failures-only: nothing analysed, one failure pending");
                    var fin = ScanSink.Flush(store, "output-x64.5", new[] { only }, final: true);
                    long failedRuns, contentRows; string? components, error;
                    using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
                    {
                        failedRuns = long.Parse(os.Db.ExecScalar("SELECT count(*) FROM run WHERE producer_key = 'essentia' AND outcome = 'failed';") ?? "0");
                        components = os.Db.ExecScalar("SELECT failed_components FROM run WHERE producer_key = 'essentia' AND outcome = 'failed';");
                        error = os.Db.ExecScalar("SELECT error_text FROM run WHERE producer_key = 'essentia' AND outcome = 'failed';");
                        contentRows = os.Dal.Count("track_content");
                    }
                    assert(fin.Ok && fin.Entries == 0 && fin.Failures == 1 && failedRuns == 1 && components == "essentia" && error == "essentia exit 1: decode error" && contentRows == 0 && ScanSink.PendingFailures == 0,
                        $"a failures-only flush COMMITS exactly one failed run (ok {fin.Ok}, entries {fin.Entries}, failures {fin.Failures}; rows {failedRuns} '{components}' / '{error}'; content {contentRows}; pending failures {ScanSink.PendingFailures})");
                    var rec = new StoreResultFile();
                    var path = StoreResult.WriteScan(store, ScanSink.Total, false, null, started); StoreResult.FinishScan(1);
                    assert(path != null && StoreResult.TryRead(path!, out rec) && rec.Detail != null && rec.Detail["failures"] == 1 && rec.Detail["entries"] == 0 && rec.Outcome == "failed" && rec.ExitCode == 1,
                        $"the record says failures 1 / entries 0 ({rec.Detail?["failures"]}/{rec.Detail?["entries"]}, {rec.Outcome}/{rec.ExitCode})");
                }
                finally { ScanSink.StoreDirectory = prevDir; ScanSink.Reset(); StoreResult.ResetScanForTest(); }
            }
        }

        static long CompanionGeneration(string companionPath)
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(companionPath));
            return doc.RootElement.TryGetProperty("origin", out var o) && o.TryGetProperty("generation", out var g) ? g.GetInt64() : -1;
        }

        // Keeper 099: WriteScan wrote capture { edgeArrays: "off", stickyRowsRefreshed: 0 } whatever the scan did. The block must be
        // the scan's resolved array flag and the edge rows it wrote WITH arrays only because the media already held them while the
        // flag was off ("sticky per track"). Three scans through the real sink, each its own record.
        static void TestScanCaptureFacts(Action<bool, string> assert)
        {
            const string P = @"D:\music\verdict\a-lavc.mp3";
            StoreResultFile Scan(string store, string dir, bool flagOn)
            {
                ScanSink.Reset(); StoreResult.ResetScanForTest();
                global::Truedat.Edges.EdgeStore.CaptureArraysDefault = flagOn;
                ScanSink.StoreDirectory = dir;
                var analysed = Path.Combine(dir, "staged-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".mp3");
                ScanSink.StashEdges(analysed, global::Truedat.Edges.EdgeSelfTest.SyntheticFold(dir, 240, true));
                ScanSink.Record(P, VerdictSelfTest.Fixture()[P], analysed);
                ScanSink.Flush(store, "output-x64.5", new[] { P });
                var rec = new StoreResultFile();
                StoreResult.TryRead(StoreResult.PathFor(store), out rec);
                return rec;
            }
            using (var d = new Dir())
            {
                var on = Scan(d.P("on.db"), d.Root, flagOn: true);
                assert(on.Capture != null && on.Capture.EdgeArrays == "on" && on.Capture.StickyRowsRefreshed == 0,
                    $"a scan with the array flag ON records edgeArrays 'on' and no sticky refreshes (got '{on.Capture?.EdgeArrays}', {on.Capture?.StickyRowsRefreshed})");

                var sticky = d.P("sticky.db");
                Scan(sticky, d.Root, flagOn: true);                          // a first scan captured the arrays
                var off = Scan(sticky, d.Root, flagOn: false);               // a later scan, flag off, re-folds the same track
                assert(off.Capture != null && off.Capture.EdgeArrays == "off" && off.Capture.StickyRowsRefreshed == 2,
                    $"a flag-OFF scan re-folding a track that holds arrays records edgeArrays 'off' and both edge rows refreshed WITH arrays (got '{off.Capture?.EdgeArrays}', {off.Capture?.StickyRowsRefreshed})");

                var none = Scan(d.P("none.db"), d.Root, flagOn: false);
                assert(none.Capture != null && none.Capture.EdgeArrays == "off" && none.Capture.StickyRowsRefreshed == 0,
                    $"a flag-OFF scan of a track with no arrays records 'off' and no sticky refreshes (got '{none.Capture?.EdgeArrays}', {none.Capture?.StickyRowsRefreshed})");
            }
        }

        // Keeper 109 on a57e709: the count read every arrays-bearing row for the content/version AFTER the write, so an 'unavailable'
        // fold — which returns after the whole-track rows and never touches an edge row — reported the two rows an earlier scan captured.
        // The count must be what the write refreshed: 0 for unavailable and failed folds (rows, array bytes and run ids untouched), 2 for a
        // real sticky refresh, once per COMMITTED refresh across a rolled-back checkpoint, and the scan-wide sum across checkpoints.
        static void TestStickyCountExact(Action<bool, string> assert)
        {
            const string P = @"D:\music\verdict\a-lavc.mp3", Q = @"D:\music\verdict\b-lame.mp3";
            var shaP = VerdictSelfTest.Fixture()[P].AudioStreamSha256!;
            global::Truedat.Edges.EdgeFoldResult Fold(string dir, string status)
            {
                var f = global::Truedat.Edges.EdgeSelfTest.SyntheticFold(dir, 240, true);
                if (status == "unavailable") f.Head.Status = f.Tail.Status = "unavailable";
                if (status == "failed") { f.Head.Status = f.Tail.Status = "failed"; f.Head.Error = f.Tail.Error = "synthetic failure"; }
                return f;
            }
            void Record(string dir, string path, global::Truedat.Edges.EdgeFoldResult fold)
            {
                var analysed = Path.Combine(dir, "staged-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".mp3");
                ScanSink.StashEdges(analysed, fold);
                ScanSink.Record(path, VerdictSelfTest.Fixture()[path], analysed);
            }
            void NewScan(string dir, bool flagOn) { ScanSink.Reset(); StoreResult.ResetScanForTest(); global::Truedat.Edges.EdgeStore.CaptureArraysDefault = flagOn; ScanSink.StoreDirectory = dir; }
            int Sticky(string store) { var rec = new StoreResultFile(); StoreResult.TryRead(StoreResult.PathFor(store), out rec); return rec.Capture?.StickyRowsRefreshed ?? -1; }
            string Rows(string store)
            {
                using var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o"));
                using var st = os.Db.Prepare("SELECT kind, run_id, hex(arrays) FROM edge WHERE content_hash = ? ORDER BY kind;");
                st.Bind(1, shaP);
                var parts = new List<string>();
                while (st.Step()) parts.Add(st.GetString(0) + ":" + (st.IsNull(1) ? "null" : st.GetInt64(1).ToString()) + ":" + (st.GetString(2) ?? "").GetHashCode().ToString("x"));
                return string.Join(" | ", parts);
            }
            using (var d = new Dir())
            {
                // the keeper's trigger: a captured content/version, then an UNAVAILABLE fold with the flag off
                var store = d.P("unavailable.db");
                NewScan(d.Root, true); Record(d.Root, P, Fold(d.Root, "ok")); ScanSink.Flush(store, "output-x64.5", new[] { P });
                var before = Rows(store);
                NewScan(d.Root, false); Record(d.Root, P, Fold(d.Root, "unavailable")); ScanSink.Flush(store, "output-x64.5", new[] { P });
                var after = Rows(store);
                assert(Sticky(store) == 0, $"an UNAVAILABLE fold over captured rows refreshes nothing: stickyRowsRefreshed 0 (got {Sticky(store)})");
                assert(before.Length > 0 && after == before, $"…and both edge rows keep their array bytes and run ids (before [{before}] after [{after}])");

                // a FAILED fold over captured rows
                var store2 = d.P("failed.db");
                NewScan(d.Root, true); Record(d.Root, P, Fold(d.Root, "ok")); ScanSink.Flush(store2, "output-x64.5", new[] { P });
                var before2 = Rows(store2);
                NewScan(d.Root, false); Record(d.Root, P, Fold(d.Root, "failed")); ScanSink.Flush(store2, "output-x64.5", new[] { P });
                assert(Sticky(store2) == 0 && Rows(store2) == before2, $"a FAILED fold refreshes nothing: 0, rows unchanged (got {Sticky(store2)})");

                // a real sticky refresh: both rows rewritten with arrays, with the new fold's run
                var store3 = d.P("refresh.db");
                NewScan(d.Root, true); Record(d.Root, P, Fold(d.Root, "ok")); ScanSink.Flush(store3, "output-x64.5", new[] { P });
                var before3 = Rows(store3);
                NewScan(d.Root, false); Record(d.Root, P, Fold(d.Root, "ok")); ScanSink.Flush(store3, "output-x64.5", new[] { P });
                assert(Sticky(store3) == 2 && Rows(store3) != before3, $"a successful sticky refresh counts the two rows it rewrote (got {Sticky(store3)}; rows changed {Rows(store3) != before3})");

                // a checkpoint that rolls back, then the retry commits: the committed refresh is counted ONCE
                var store4 = d.P("rollback.db");
                NewScan(d.Root, true); Record(d.Root, P, Fold(d.Root, "ok")); ScanSink.Flush(store4, "output-x64.5", new[] { P });
                NewScan(d.Root, false); Record(d.Root, P, Fold(d.Root, "ok"));
                ImportCommand.FailAfterEntriesForTest = 1;
                var cp = ScanSink.Flush(store4, "output-x64.5", new[] { P }, final: false);
                ImportCommand.FailAfterEntriesForTest = 0;
                var fin = ScanSink.Flush(store4, "output-x64.5", new[] { P });
                assert(!cp.Ok && fin.Ok && Sticky(store4) == 2, $"a rolled-back checkpoint then a committed retry counts the refresh once (checkpoint ok {cp.Ok}, final ok {fin.Ok}; got {Sticky(store4)})");

                // two successful checkpoints and an empty final flush: the scan-wide sum
                var store5 = d.P("sum.db");
                NewScan(d.Root, true); Record(d.Root, P, Fold(d.Root, "ok")); Record(d.Root, Q, Fold(d.Root, "ok")); ScanSink.Flush(store5, "output-x64.5", new[] { P, Q });
                NewScan(d.Root, false);
                Record(d.Root, P, Fold(d.Root, "ok")); ScanSink.Flush(store5, "output-x64.5", new[] { P, Q }, final: false);
                Record(d.Root, Q, Fold(d.Root, "ok")); ScanSink.Flush(store5, "output-x64.5", new[] { P, Q }, final: false);
                ScanSink.Flush(store5, "output-x64.5", new[] { P, Q });
                // An EMPTY final flush returns before writing a record (ScanSink.Flush's early exit); the scan's own end-of-run write,
                // Program.FlushStoreSink, then always writes it from the scan-wide tally — done here exactly as it does.
                StoreResult.WriteScan(store5, ScanSink.Total, true, null);
                assert(Sticky(store5) == 4, $"two committed checkpoints (2 + 2) and an empty final flush record the scan-wide total 4 (got {Sticky(store5)})");
            }
        }

        // TDA 092 (my finding on dd27ebd): the final store step gated the companion rewrite on the FINAL flush's edge count, so a long
        // --store --edges scan whose checkpoints committed every edge row and whose final flush was empty left mbxedges.v1.json behind the
        // store. Two edge-bearing checkpoints and an empty final flush, through the real sink and Program.FinishScanStore.
        static void TestFinishScanStoreCompanion(Action<bool, string> assert)
        {
            const string P = @"D:\music\verdict\a-lavc.mp3", Q = @"D:\music\verdict\b-lame.mp3";
            using (var d = new Dir())
            {
                var store = d.P("truedat.db");
                ScanSink.Reset(); StoreResult.ResetScanForTest();
                global::Truedat.Edges.EdgeStore.CaptureArraysDefault = false; ScanSink.StoreDirectory = d.Root;
                void Record(string path)
                {
                    var analysed = Path.Combine(d.Root, "staged-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".mp3");
                    ScanSink.StashEdges(analysed, global::Truedat.Edges.EdgeSelfTest.SyntheticFold(d.Root, 240, true));
                    ScanSink.Record(path, VerdictSelfTest.Fixture()[path], analysed);
                }
                Record(P); var cp1 = ScanSink.Flush(store, "output-x64.5", new[] { P, Q }, final: false);
                Record(Q); var cp2 = ScanSink.Flush(store, "output-x64.5", new[] { P, Q }, final: false);
                var fin = ScanSink.Flush(store, "output-x64.5", new[] { P, Q });
                assert(cp1.Ok && cp2.Ok && cp1.Edges + cp2.Edges == 2 && fin.Ok && fin.Edges == 0,
                    $"setup: two checkpoints committed the run's edges and the final flush was empty (checkpoint edges {cp1.Edges} + {cp2.Edges}, final {fin.Edges})");

                var companion = d.P(global::Truedat.Edges.EdgeCompanion.FileName);
                var captured = new StringWriter(); var saved = Console.Out; Console.SetOut(captured);
                bool written;
                try { written = Program.FinishScanStore(store, fin, ScanSink.Total, true, companion, new global::Truedat.Edges.EdgeSettings()); }
                finally { Console.SetOut(saved); }
                var tracks = new List<string>();
                if (File.Exists(companion))
                    using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(companion)))
                        foreach (var t in doc.RootElement.GetProperty("tracks").EnumerateObject()) tracks.Add(t.Name);
                var sha = VerdictSelfTest.Fixture()[P].AudioStreamSha256!;
                assert(written && File.Exists(companion) && tracks.Contains(sha),
                    $"the companion IS rewritten after a run whose edges all landed at checkpoints, and holds the run's rows (written {written}; file {File.Exists(companion)}; tracks {tracks.Count})");
                var printed = captured.ToString();
                assert(printed.Contains("edges: 2 tracks folded"),
                    $"the summary prints the scan-wide edge total, not the final flush's (printed: {printed.Replace(Environment.NewLine, " | ").Trim()})");
            }
        }

        // Keeper 113 / TDA 095: the keeper's exact trigger, reproduced on dd27ebd (companion stayed BPM 111 / generation 2 while the store
        // held 222 / generation 3). Through the real sink and Program.FinishScanStore — which now also writes the scan's record, as the
        // scan does. Four runs on one store: (A) a prior run publishes BPM 111; (B) a checkpoint commits BPM 222, the final flush is empty;
        // (C) a run that adds no edges; (D) a run whose final flush FAILS.
        static void TestCompanionKeeperTrigger(Action<bool, string> assert)
        {
            const string P = @"D:\music\verdict\a-lavc.mp3";
            var sha = VerdictSelfTest.Fixture()[P].AudioStreamSha256!;
            using (var d = new Dir())
            {
                var store = d.P("truedat.db"); var companion = d.P(global::Truedat.Edges.EdgeCompanion.FileName);
                var settings = new global::Truedat.Edges.EdgeSettings();
                void NewScan() { ScanSink.Reset(); StoreResult.ResetScanForTest(); global::Truedat.Edges.EdgeStore.CaptureArraysDefault = false; ScanSink.StoreDirectory = d.Root; }
                void Record(double? bpm)
                {
                    if (bpm == null) { ScanSink.Record(P, VerdictSelfTest.Fixture()[P]); return; }
                    var fold = global::Truedat.Edges.EdgeSelfTest.SyntheticFold(d.Root, 240, true);
                    fold.Head.Bpm!.Value = bpm.Value;
                    var analysed = Path.Combine(d.Root, "staged-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".mp3");
                    ScanSink.StashEdges(analysed, fold);
                    ScanSink.Record(P, VerdictSelfTest.Fixture()[P], analysed);
                }
                bool Finish(ScanSinkReport fin)
                {
                    var saved = Console.Out; Console.SetOut(new StringWriter());
                    try { return Program.FinishScanStore(store, fin, ScanSink.Total, true, companion, settings, DateTime.UtcNow.ToString("o")); }
                    finally { Console.SetOut(saved); }
                }
                (double? HeadBpm, long? Gen, string Sha) Comp()
                {
                    if (!File.Exists(companion)) return (null, null, "");
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(companion));
                    var root = doc.RootElement;
                    double? bpm = root.GetProperty("tracks").TryGetProperty(sha, out var t) && t.GetProperty("head").GetProperty("bpm").ValueKind == System.Text.Json.JsonValueKind.Object
                        ? t.GetProperty("head").GetProperty("bpm").GetProperty("value").GetDouble() : (double?)null;
                    long? gen = root.TryGetProperty("origin", out var o) ? o.GetProperty("generation").GetInt64() : (long?)null;
                    return (bpm, gen, Publication.Sha256Hex(companion));
                }
                long StoreGen() { using var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o")); return os.Dal.Generation(); }
                StoreResultFile Rec() { var rec = new StoreResultFile(); StoreResult.TryRead(StoreResult.PathFor(store), out rec); return rec; }

                // (A) a prior run: BPM 111 published
                NewScan(); Record(111); var finA = ScanSink.Flush(store, "output-x64.5", new[] { P }); Finish(finA);
                var a = Comp();
                assert(a.HeadBpm == 111 && a.Gen == StoreGen(), $"setup (A): the prior run published BPM 111 at the store's generation (bpm {a.HeadBpm}, companion gen {a.Gen}, store {StoreGen()})");

                // (B) the keeper's trigger: a checkpoint commits BPM 222, the final flush is empty
                NewScan(); Record(222);
                var cp = ScanSink.Flush(store, "output-x64.5", new[] { P }, final: false);
                long afterCheckpoint = StoreGen();
                var finB = ScanSink.Flush(store, "output-x64.5", new[] { P });
                assert(cp.Ok && cp.Edges == 1 && finB.Ok && finB.Entries == 0 && afterCheckpoint > a.Gen,
                    $"setup (B): the checkpoint committed the changed fold (store {a.Gen} -> {afterCheckpoint}) and the final flush was empty");
                bool written = Finish(finB);
                var b = Comp(); var recB = Rec();
                assert(written && b.HeadBpm == 222, $"(B) the companion is rewritten and carries the COMMITTED fold, BPM 222 (written {written}; bpm {b.HeadBpm})");
                assert(b.Gen == StoreGen(), $"(B) the companion's origin.generation equals the store's generation after publication (companion {b.Gen}, store {StoreGen()})");
                assert(recB.Export?.Companion != null && recB.Export.Companion.Sha256 == b.Sha,
                    $"(B) store-result.json names the companion by its actual hash (record {recB.Export?.Companion?.Sha256 ?? "null"}, file {b.Sha})");

                // (C) a run that adds no edges: the companion is not rewritten
                NewScan(); Record(null); var finC = ScanSink.Flush(store, "output-x64.5", new[] { P });
                bool writtenC = Finish(finC);
                var c = Comp(); var recC = Rec();
                assert(!writtenC && c.Sha == b.Sha && recC.Export?.Companion == null,
                    $"(C) a run with no new edges does not rewrite the companion, and the record names none (written {writtenC}; file unchanged {c.Sha == b.Sha}; record companion {recC.Export?.Companion?.Sha256 ?? "null"})");

                // (D) a run whose final flush FAILS: nothing published, the record says failed
                NewScan(); Record(333);
                ImportCommand.FailAfterEntriesForTest = 1;
                ScanSinkReport finD;
                try { finD = ScanSink.Flush(store, "output-x64.5", new[] { P }); }
                finally { ImportCommand.FailAfterEntriesForTest = 0; }
                bool writtenD = Finish(finD);
                var dd = Comp(); var recD = Rec();
                assert(!finD.Ok && !writtenD && dd.Sha == b.Sha && dd.HeadBpm == 222 && recD.Outcome == "failed" && recD.Export?.Companion == null,
                    $"(D) a failed final flush publishes nothing: companion unchanged (BPM {dd.HeadBpm}), record failed with no companion (ok {finD.Ok}; written {writtenD}; outcome {recD.Outcome})");
            }
        }

        static void TestEdgesExportGeneration(Action<bool, string> assert)
        {
            using (var d = new Dir())
            {
                var store = d.P("truedat.db");
                ImportCommand.ExecuteEntries(store, Fixture(@"D:\music\e.flac", 110), @"D:\music", "primary", true, "import", "1", "fixture", null, null);
                long gen = AdvanceTo(store, 7); int schema = SchemaOf(store);
                var companion = d.P("mbxedges.v1.json");
                var resultPath = StoreResult.PathFor(store);
                var settings = new global::Truedat.Edges.EdgeSettings();

                // Plain: the record's generation is the file's.
                var ok = new StoreResultFile();
                int code = Program.RunEdgesExport(store, companion, settings);
                assert(code == 0 && File.Exists(companion) && StoreResult.TryRead(resultPath, out ok) && ok.Verb == "edges-export" && ok.Outcome == "completed" && ok.ExitCode == 0,
                    "edges-export: exit 0, companion written, record completed");
                assert(ok.Export.Performed && ok.Export.Generation == gen && ok.Store.Generation == gen && ok.Store.SchemaVersion == schema && CompanionGeneration(companion) == gen
                       && ok.Export.Companion != null && ok.Export.Companion.Sha256 == Publication.Sha256Hex(companion) && ok.Export.Catalog == null && ok.Export.Sidecar == null,
                    $"edges-export: record generation {ok.Export.Generation} == file origin.generation {CompanionGeneration(companion)} == store {gen}; companion named by hash; no catalog/sidecar");

                // Keeper repro (P2 edge): a commit by another connection lands between the companion write and the record — 9706c5b re-read
                // the store and recorded 8 against a file stamped 7. The record must carry the generation the FILE was stamped with.
                Program.AfterEdgesCompanionWritten = () => { using var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o")); os.Dal.AdvanceGeneration(); };
                int code2;
                try { code2 = Program.RunEdgesExport(store, companion, settings); }
                finally { Program.AfterEdgesCompanionWritten = null; }
                long storeNow;
                using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o"))) storeNow = os.Dal.Generation();
                assert(code2 == 0 && storeNow == gen + 1, $"interleaving: the concurrent commit really advanced the store to {storeNow} (expected {gen + 1})");
                assert(StoreResult.TryRead(resultPath, out var il) && il.Export.Generation == gen && il.Store.Generation == gen && CompanionGeneration(companion) == gen && il.Export.Companion != null && il.Export.Companion.Sha256 == Publication.Sha256Hex(companion),
                    $"interleaving: record says {il.Export.Generation}, file says {CompanionGeneration(companion)}, store moved on to {storeNow} — the record describes the file, not a later read");

                // Failure: no target → failed record with a reason, nothing performed.
                var bad = new StoreResultFile();
                int code3 = Program.RunEdgesExport(store, null, settings);
                assert(code3 == 1 && StoreResult.TryRead(resultPath, out bad) && bad.Outcome == "failed" && bad.ExitCode == 1 && !bad.Export.Performed && bad.Reason != null && bad.Reason.Contains("not written") && bad.Store.SchemaVersion == null && bad.Store.Generation == null,
                    $"edges-export with no target: exit 1, failed record, store not read -> schema/generation null ({bad.Reason}; {bad.Store.SchemaVersion}/{bad.Store.Generation})");
            }
        }

        static void TestImportWritesIt(Action<bool, string> assert)
        {
            using (var d = new Dir())
            {
                var store = d.P("truedat.db");
                var catalog = d.P("mbxmoods.json");
                var entries = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
                foreach (var kv in Fixture(@"D:\music\i.flac", 95)) entries[kv.Key] = kv.Value;
                Program.SaveResultsForStore(catalog, entries);
                var resultPath = StoreResult.PathFor(store);

                var ok = new StoreResultFile();
                int code = ImportCommand.Run(new ImportOptions { StorePath = store, CatalogPath = catalog, LibraryRoot = @"D:\music", RootRole = "primary" });
                assert(code == 0 && File.Exists(resultPath) && StoreResult.TryRead(resultPath, out ok) && ok.Verb == "store-import" && ok.Outcome == "completed" && ok.ExitCode == 0 && ok.Reason == null,
                    "import success: exit 0, the common record beside the store, completed");
                // T07: the import's one committed batch created an entry a reader can see, so the committed generation is 1.
                assert(ok.Store.Path == Path.GetFullPath(store) && ok.Store.SchemaVersion == SchemaOf(store) && ok.Store.Generation == 1 && !ok.Export.Performed && ok.Detail != null && ok.Detail["entries"] == 1 && ok.Detail["entitiesCreated"] == 1,
                    $"import success: store block from the opened store (schema {ok.Store.SchemaVersion}, committed generation {ok.Store.Generation}), nothing published, detail counters");
                assert(File.Exists(Path.Combine(d.Root, ImportCommand.ResultFileName)), "import success: the verb's own store-import-result.json is still written beside it");

                // Failure after the store opened (catalog missing): failed record, the store facts kept.
                var bad = new StoreResultFile();
                int code2 = ImportCommand.Run(new ImportOptions { StorePath = store, CatalogPath = d.P("no-such.json"), LibraryRoot = @"D:\music" });
                assert(code2 != 0 && StoreResult.TryRead(resultPath, out bad) && bad.Verb == "store-import" && bad.Outcome == "failed" && bad.ExitCode == code2 && bad.Reason != null && bad.Reason.Contains("not found") && bad.Store.Path == Path.GetFullPath(store)
                       && bad.Store.SchemaVersion == null && bad.Store.Generation == null && bad.Detail == null,
                    $"import failure before the store opened: failed record with the reason, the store named, schema/generation NOT READ -> null, no counts ({bad.Reason}; {bad.Store.SchemaVersion}/{bad.Store.Generation})");
                // TDA coverage ask: a failure AFTER the store opened, through Run — the record keeps the at-open schema/generation and carries no
                // counts from the rolled-back batch.
                long genNow = AdvanceTo(store, 5);
                var afterOpen = new StoreResultFile();
                ImportCommand.AfterStoreOpened = () => throw new IOException("disk full after open");
                int code3;
                try { code3 = ImportCommand.Run(new ImportOptions { StorePath = store, CatalogPath = catalog, LibraryRoot = @"D:\music", RootRole = "primary" }); }
                finally { ImportCommand.AfterStoreOpened = null; }
                assert(code3 == 1 && StoreResult.TryRead(resultPath, out afterOpen) && afterOpen.Outcome == "failed" && afterOpen.ExitCode == 1 && afterOpen.Reason != null && afterOpen.Reason.Contains("disk full after open")
                       && afterOpen.Store.SchemaVersion == SchemaOf(store) && afterOpen.Store.Generation == genNow && afterOpen.Detail == null && !afterOpen.Export.Performed,
                    $"import failure after open: failed/exit 1 with the reason, at-open schema {afterOpen.Store.SchemaVersion} and generation {afterOpen.Store.Generation} kept, no detail counts");
            }
        }
    }
}
