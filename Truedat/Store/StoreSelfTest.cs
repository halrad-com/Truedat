using System;
using System.Collections.Generic;
using System.IO;

namespace Truedat.Store
{
    /// <summary>Self-tests for the v2 store, driven from <c>--self-test</c> through the same
    /// Assert(bool, string) the rest of the suite uses. Each block is isolated: an exception inside
    /// one block is reported as a FAIL naming the block and the next block still runs.</summary>
    internal static class StoreSelfTest
    {
        internal static void Run(Action<bool, string> assert)
        {
            Console.WriteLine("Store self-test (C1: sqlite layer + open guard)");
            Block(assert, "StoreLog", () => TestStoreLog(assert));
            Block(assert, "SqliteProbe seam", () => TestProbeSeam(assert));
            Block(assert, "SqliteProbe real", () => TestProbeReal(assert));
            Block(assert, "SqliteDb", () => TestDb(assert));
            Console.WriteLine("Store self-test (C2: schema v1 + drift)");
            Block(assert, "Schema", () => TestSchema(assert));
            Console.WriteLine("Store self-test (C3a: uuid v7 + canonical paths)");
            Block(assert, "Uuid7", () => TestUuid7(assert));
            Block(assert, "CanonicalPath", () => TestCanonicalPath(assert));
            Console.WriteLine("Store self-test (C3b: machine identity + entity resolution)");
            Block(assert, "MachineIdentity", () => TestMachineIdentity(assert));
            Block(assert, "EntityResolver", () => TestEntityResolver(assert));
            Console.WriteLine("Store self-test (C4: TrackEntry mapper + feature rows)");
            Block(assert, "TrackEntryMapper", () => TestMapper(assert));
            Block(assert, "FeatureRows", () => TestFeatureRows(assert));
            Console.WriteLine("Store self-test (C5: import)");
            Block(assert, "LedgerImport", () => TestLedgerReaders(assert));
            Block(assert, "Import", () => TestImport(assert));
            Console.WriteLine("Store self-test (C6: export + round-trip gate)");
            Block(assert, "Export", () => TestExport(assert));
        }

        // ---------------------------------------------------------------- export
        static void TestExport(Action<bool, string> assert)
        {
            var dir = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "export-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var catDir = Path.Combine(dir, "lib"); var outDir = Path.Combine(dir, "out");
            Directory.CreateDirectory(catDir); Directory.CreateDirectory(outDir);
            var store = Path.Combine(dir, "truedat.db");
            try
            {
                var entries = FixtureEntries(@"D:\Music");
                var catalog = Path.Combine(catDir, "mbxmoods.json");
                WriteCatalog(catalog, entries);
                ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = catalog, LibraryRoot = @"D:\Music" });

                bool refused = false;
                try { ExportCommand.Execute(new ExportOptions { StorePath = store, OutPath = null }); } catch (StoreException) { refused = true; }
                assert(refused, "no output path -> refusal");

                var outPath = Path.Combine(outDir, "mbxmoods.json");
                var r = ExportCommand.Execute(new ExportOptions { StorePath = store, OutPath = outPath });
                assert(r.Entries == 6 && r.Root == @"D:\Music", $"export picks the single primary root and writes 6 entries ({r.Entries}, {r.Root})");
                assert(File.Exists(outPath), "the export file exists");
                assert(r.KeyMoves == 0 && r.KeyMovesPath == null, "no superseded hashes -> no key-moves.json");

                // The gate, writer-vs-writer: the exported catalog loads to the same key set and every entry writes the same bytes.
                var back = new System.Collections.Concurrent.ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
                int n = Program.LoadExistingMoodsForStore(outPath, back);
                int same = 0;
                foreach (var kv in entries) if (back.TryGetValue(kv.Key, out var b) && EntryJson(kv.Key, kv.Value) == EntryJson(kv.Key, b)) same++;
                assert(n == 6 && same == 6, $"export reloads to 6 entries, 6/6 byte-identical through the writer ({n}, {same})");
                var text = File.ReadAllText(outPath);
                assert(text.Contains("\"trackCount\":6"), "the export carries the writer's header and trackCount");

                var rt = ExportCommand.Roundtrip(store, catalog, null);
                assert(rt.Equal && rt.CatalogEntries == 6 && rt.StoreEntries == 6 && rt.Same == 6, $"Roundtrip(store, catalog) reports equal: {rt.Same}/{rt.CatalogEntries}, only-in-catalog {rt.OnlyInCatalog}, only-in-store {rt.OnlyInStore}, different {rt.Different}");

                // A changed catalog: one entry re-keyed (legacy.flac gains a frame-anchored sha) and one new entry.
                var changed = FixtureEntries(@"D:\Music");
                changed[@"D:\Music\old\legacy.flac"].AudioStreamSha256 = "2222222222222222222222222222222222222222222222222222222222222222";
                changed[@"D:\Music\old\legacy.flac"].AudioStreamSha256Source = "flac-frames";
                changed[@"D:\Music\new\n.mp3"] = Entry("9999999999999999999999999999999999999999999999999999999999999999", null, "mp3", 60000);
                var catalog2 = Path.Combine(catDir, "mbxmoods.json");   // same path: the catalog changed in place
                WriteCatalog(catalog2, changed);
                var ri = ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = catalog2, LibraryRoot = @"D:\Music" });
                // MatchedPath is 2: legacy.flac (re-keyed) AND nosha.mp3, which has no hash and can only ever match by path.
                assert(ri.EntitiesCreated == 1 && ri.MatchedPath == 2 && ri.LocationsNew == 1, $"changed catalog: 1 new entity, 2 matched by path (the re-keyed FLAC and the no-sha entry), 1 new location ({ri.EntitiesCreated}/{ri.MatchedPath}/{ri.LocationsNew})");

                var rt2 = ExportCommand.Roundtrip(store, catalog2, null);
                assert(rt2.Equal && rt2.CatalogEntries == 7, $"after re-import the store matches the changed catalog, 7/7 ({rt2.Same}/{rt2.CatalogEntries}, different {rt2.Different})");
                var rtOld = ExportCommand.Roundtrip(store, outPath, null);
                assert(!rtOld.Equal && rtOld.Different == 1 && rtOld.OnlyInStore == 1 && rtOld.FirstDifferentPath != null && rtOld.FirstDifferentPath.EndsWith("legacy.flac", StringComparison.OrdinalIgnoreCase),
                    $"against the OLD export the gate reports 1 different (legacy.flac) and 1 only-in-store (n.mp3) ({rtOld.Different}/{rtOld.OnlyInStore}, first {rtOld.FirstDifferentPath})");

                var out2 = Path.Combine(outDir, "second", "mbxmoods.json");
                Directory.CreateDirectory(Path.GetDirectoryName(out2)!);
                var r2 = ExportCommand.Execute(new ExportOptions { StorePath = store, OutPath = out2 });
                assert(r2.Entries == 7 && r2.KeyMoves == 1 && r2.KeyMovesPath != null && File.Exists(r2.KeyMovesPath), $"export after a key move writes key-moves.json beside it with 1 move ({r2.KeyMoves})");
                var km = File.ReadAllText(r2.KeyMovesPath ?? "");
                assert(km.Contains("1111111111111111111111111111111111111111111111111111111111111111") && km.Contains("2222222222222222222222222222222222222222222222222222222222222222")
                    && km.Contains(FacetKinds.ShaTaglibRegion) && km.Contains(FacetKinds.ShaFrames), "key-moves.json names old and new hash with their kinds");

                // Two primary roots and no --export-root -> refuse, naming them.
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                    os.Dal.EnsureRoot(os.Machine.Id, @"E:\Other", "primary", DateTime.UtcNow.ToString("o"));
                string? msg = null;
                try { ExportCommand.Execute(new ExportOptions { StorePath = store, OutPath = Path.Combine(outDir, "x.json") }); } catch (StoreException ex) { msg = ex.Message; }
                assert(msg != null && msg.Contains(@"D:\Music") && msg.Contains(@"E:\Other"), $"two primary roots without --export-root refuse and name both ({msg})");
                var r3 = ExportCommand.Execute(new ExportOptions { StorePath = store, OutPath = Path.Combine(outDir, "x.json"), ExportRoot = @"d:\music\" });
                assert(r3.Entries == 7 && r3.Root == @"D:\Music", "--export-root selects the root case-insensitively, tolerating a trailing separator");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ---------------------------------------------------------------- ledger readers
        static void TestLedgerReaders(Action<bool, string> assert)
        {
            var dir = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "ledgers-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                var errors = Path.Combine(dir, "mbxmoods-errors.csv");
                File.WriteAllLines(errors, new[]
                {
                    "Error,Artist,Title,FilePath,SizeMB,Duration,FailedComponents",
                    "\"TagLib: bad header, really\",Tool,\"The Grudge\",D:\\Music\\bad\\x.flac,42.1,500.0,sha;fingerprint",
                    "essentia exit 1,,,D:\\Music\\bad\\y.mp3,3.0,0.0,essentia",
                });
                var old = Path.Combine(dir, "old-errors.csv");
                File.WriteAllLines(old, new[] { "Error,Artist,Title,FilePath,SizeMB,Duration", "boom,A,T,D:\\Music\\bad\\z.mp3,1.0,2.0" });
                var skipped = Path.Combine(dir, "mbxmoods-skipped.csv");
                File.WriteAllLines(skipped, new[] { "path,extension,reason,timestamp", "D:\\Music\\skip\\s.dsf,.dsf,unsupported codec: DSD,2026-10-01T00:00:00Z" });
                var verify = Path.Combine(dir, "mbxmoods-verify.csv");
                File.WriteAllLines(verify, new[] { "status\tpath\tdetail\tbackfilledFields", "OK\tD:\\Music\\Tool\\a.flac\t\t", "DRIFT\tD:\\Music\\Tool\\b.mp3\tsha changed\t" });

                var e = LedgerImport.ReadErrors(errors);
                assert(e.Count == 2 && e[0].Path == @"D:\Music\bad\x.flac" && e[0].Error == "TagLib: bad header, really" && e[0].FailedComponents == "sha;fingerprint",
                    "errors.csv: quoted fields with commas, FailedComponents column");
                var eo = LedgerImport.ReadErrors(old);
                assert(eo.Count == 1 && eo[0].Path == @"D:\Music\bad\z.mp3" && eo[0].FailedComponents == null, "errors.csv without the FailedComponents column still reads (old files)");
                var s = LedgerImport.ReadSkipped(skipped);
                assert(s.Count == 1 && s[0].Path == @"D:\Music\skip\s.dsf" && s[0].Reason == "unsupported codec: DSD" && s[0].Timestamp == "2026-10-01T00:00:00Z", "skipped.csv rows");
                var v = LedgerImport.ReadVerify(verify);
                assert(v.Count == 2 && v[1].Status == "DRIFT" && v[1].Path == @"D:\Music\Tool\b.mp3" && v[1].Detail == "sha changed" && v[0].Detail == null, "verify.csv (tab-separated) rows, empty detail reads as null");
                assert(LedgerImport.ReadErrors(Path.Combine(dir, "absent.csv")).Count == 0, "a missing ledger reads as empty, not an error");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ---------------------------------------------------------------- import fixture
        static int _fixtureReviewAttempts;

        static TrackEntry Entry(string? sha, string? shaSource, string codec, int durationMs, bool full = false)
        {
            var e = full ? FullyPopulatedEntry() : new TrackEntry { Features = new TrackFeatures { Artist = "A", Title = "T", Bpm = 120, Mfcc = new[] { 1.5, -2.25 } }, LastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc) };
            e.AudioStreamSha256 = sha; e.AudioStreamSha256Source = shaSource;
            e.FingerprintV1 = new Program.FingerprintV1 { FileSize = 1234, PathTail = "x", DurationMs = durationMs, SampleRate = 44100, Channels = 2, Codec = codec, AudioHead64kMd5 = "m" };
            return e;
        }

        static void WriteCatalog(string path, IEnumerable<KeyValuePair<string, TrackEntry>> entries)
        {
            using var fs = File.Create(path);
            using var jw = new System.Text.Json.Utf8JsonWriter(fs);
            jw.WriteStartObject();
            jw.WritePropertyName("tracks"); jw.WriteStartObject();
            foreach (var kv in entries) Program.WriteTrackEntryForStore(jw, kv.Key, kv.Value);
            jw.WriteEndObject(); jw.WriteEndObject();
        }

        static Dictionary<string, TrackEntry> FixtureEntries(string root)
        {
            const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            const string L = "1111111111111111111111111111111111111111111111111111111111111111";
            const string S = "5555555555555555555555555555555555555555555555555555555555555555";
            return new Dictionary<string, TrackEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [root + @"\Tool\a.flac"] = Entry(A, "flac-frames", "flac", 300000, full: true),
                [root + @"\Tool\b.mp3"] = Entry(B, null, "mp3", 180000),
                [root + @"\odd\nosha.mp3"] = Entry(null, null, "mp3", 100000),
                [root + @"\old\legacy.flac"] = Entry(L, null, "flac", 250000),
                [root + @"\dup\d1.flac"] = Entry(S, "flac-frames", "flac", 100000),
                [root + @"\dup\d2.flac"] = Entry(S, "flac-frames", "flac", 200000),   // same sha, duration differs by 100 s -> conflict
            };
        }

        static void WriteLedgers(string dir, string root)
        {
            File.WriteAllLines(Path.Combine(dir, "mbxmoods-errors.csv"), new[]
            {
                "Error,Artist,Title,FilePath,SizeMB,Duration,FailedComponents",
                $"\"TagLib: bad header\",Tool,X,{root}\\bad\\x.flac,42.1,500.0,sha;fingerprint",
            });
            File.WriteAllLines(Path.Combine(dir, "mbxmoods-skipped.csv"), new[]
            {
                "path,extension,reason,timestamp", $"{root}\\skip\\s.dsf,.dsf,unsupported codec: DSD,2026-10-01T00:00:00Z",
            });
            File.WriteAllLines(Path.Combine(dir, "mbxmoods-verify.csv"), new[]
            {
                "status\tpath\tdetail\tbackfilledFields", $"OK\t{root}\\Tool\\a.flac\t\t", $"DRIFT\t{root}\\Tool\\b.mp3\tsha changed\t",
            });
            File.WriteAllText(Path.Combine(dir, "mbxmoods-verify.receipt.json"),
                "{\"schemaVersion\":1,\"kind\":\"verify-coverage-receipt\",\"catalog\":{\"path\":\"x\",\"trackCount\":6,\"digest\":\"00\"},\"shard\":{\"index\":1,\"total\":1,\"host\":\"ATOM\"},\"coverage\":{\"entries\":6,\"digest\":\"00\"}}");
            var ledger = new ReviewLedger();
            var rec = ledger.Upsert(new ReviewRecord { Path = root + @"\bad\x.flac", Reason = "TagLib: bad header", State = ReviewState.Auto, StateReason = "auto:failed-component:sha", Attempts = 2 },
                new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), isRetry: false);
            _fixtureReviewAttempts = rec.Attempts;   // Upsert owns the attempt count; the import must carry what the ledger says
            File.WriteAllText(Path.Combine(dir, ReviewLedger.FileName), ledger.ToJson("selftest", new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc)).ToJsonString());
        }

        // ---------------------------------------------------------------- import
        static void TestImport(Action<bool, string> assert)
        {
            var dir = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "import-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var catDir = Path.Combine(dir, "lib"); var mirrorDir = Path.Combine(dir, "mirror");
            Directory.CreateDirectory(catDir); Directory.CreateDirectory(mirrorDir);
            var store = Path.Combine(dir, "truedat.db");
            try
            {
                var entries = FixtureEntries(@"D:\Music");
                var catalog = Path.Combine(catDir, "mbxmoods.json");
                WriteCatalog(catalog, entries);
                WriteLedgers(catDir, @"D:\Music");

                bool refused = false;
                try { ImportCommand.Execute(new ImportOptions { StorePath = null, CatalogPath = catalog }); } catch (StoreException) { refused = true; }
                assert(refused, "no --store -> refusal (the store path is never guessed)");

                var o = new ImportOptions { StorePath = store, CatalogPath = catalog, LibraryRoot = @"D:\Music", RootRole = "primary", BatchSize = 2 };
                var r = ImportCommand.Execute(o);
                assert(r.Entries == 6, $"6 entries read ({r.Entries})");
                assert(r.Roots.Count == 1 && r.Roots[0] == @"D:\Music", "the given root is used");
                assert(r.EntitiesCreated == 4 && r.MatchedHash == 1 && r.CreatedNoSha == 1, $"entities: 4 created with a sha, 1 matched by sha (d2), 1 created without ({r.EntitiesCreated}/{r.MatchedHash}/{r.CreatedNoSha})");
                assert(r.Locations == 6 && r.Conflicts == 1, $"6 locations, 1 sha collision conflict ({r.Locations}/{r.Conflicts})");
                assert(r.LegacySha == 1 && r.LegacyShaUnrecoverable == 3, $"1 legacy-sha FLAC (taglib-region kind), 3 flac-frames FLACs whose old sha is unrecoverable ({r.LegacySha}/{r.LegacyShaUnrecoverable})");
                assert(r.ErrorsRows == 1 && r.SkippedRows == 1 && r.ReviewRows == 1 && r.VerifyRows == 2 && r.ReceiptRows == 1, $"ledgers: errors {r.ErrorsRows}, skipped {r.SkippedRows}, review {r.ReviewRows}, verify {r.VerifyRows}, receipts {r.ReceiptRows}");
                assert(File.Exists(r.ResultPath) && File.ReadAllText(r.ResultPath).Contains("\"entries\": 6"), "store-import-result.json written beside the store with the counts");
                assert(r.StoreBytesAfter > r.StoreBytesBefore && r.StoreBytesAfter > 100_000, $"store size is measured after a WAL checkpoint, so it reflects the rows ({r.StoreBytesBefore} -> {r.StoreBytesAfter})");
                assert(!File.Exists(store + "-wal") || new FileInfo(store + "-wal").Length == 0, "the WAL is folded into the main file at the end of an import");

                using (var os = OpenStore.Open(store, create: false, "selftest", DateTime.UtcNow.ToString("o")))
                {
                    assert(os.Dal.Count("file_entity") == 7, $"7 entities: 5 from the catalog + the errors path + the skipped path (review shares the errors path) ({os.Dal.Count("file_entity")})");
                    assert(os.Dal.Count("track_feature") == 6 && os.Dal.Count("track_location") == 8,
                        $"6 feature rows; 8 locations = 6 catalog entries + the errors path + the skipped path, which are real files too ({os.Dal.Count("track_feature")}/{os.Dal.Count("track_location")})");
                    assert(os.Dal.Count("run") == 4, $"runs: import + errors + skipped + receipt = 4 ({os.Dal.Count("run")})");
                    assert(os.Dal.Count("check_result") == 2 && os.Dal.Count("work_item") == 1 && os.Dal.Count("conflict") == 1, "2 verify checks, 1 review work item, 1 conflict");
                    assert(os.Db.ExecScalar("SELECT review_state FROM work_item;") == "auto" && os.Db.ExecScalar("SELECT attempts FROM work_item;") == _fixtureReviewAttempts.ToString(),
                        $"review state and attempts carried onto the work item (state {os.Db.ExecScalar("SELECT review_state FROM work_item;")}, attempts {os.Db.ExecScalar("SELECT attempts FROM work_item;")} vs ledger {_fixtureReviewAttempts})");
                    assert(os.Db.ExecScalar("SELECT outcome FROM run WHERE producer_key = 'import';") == "ok", "the import's own run is recorded ok");
                    assert(os.Db.ExecScalar("SELECT failed_components FROM run WHERE outcome = 'failed';") == "sha;fingerprint", "errors.csv row became a failed essentia run with its components");
                    assert(os.Db.ExecScalar("SELECT count(*) FROM identity_facet WHERE kind = '" + FacetKinds.ShaTaglibRegion + "';") == "1", "legacy FLAC sha recorded under the taglib-region kind");
                    assert(os.Db.ExecScalar("SELECT count(*) FROM track_feature WHERE content_key_kind = 'entity';") == "1", "the no-sha entry's feature row is keyed entity:<uuid> with kind 'entity'");

                    // Per-entry byte equality — the import side of the round-trip gate.
                    long root = os.Dal.EnsureRoot(os.Machine.Id, @"D:\Music", "primary", DateTime.UtcNow.ToString("o"));
                    int same = 0, total = 0;
                    foreach (var (display, back) in os.Dal.ReadEntries(root))
                    {
                        total++;
                        if (entries.TryGetValue(display, out var orig) && EntryJson(display, orig) == EntryJson(display, back)) same++;
                    }
                    assert(total == 6 && same == 6, $"every imported entry writes identical bytes to its original ({same}/{total})");
                }

                var r2 = ImportCommand.Execute(o);
                assert(r2.EntitiesCreated == 0 && r2.CreatedNoSha == 0 && r2.Locations == 6 && r2.Conflicts == 0, $"re-import of the same catalog creates nothing ({r2.EntitiesCreated}/{r2.CreatedNoSha}/{r2.Locations}/{r2.Conflicts})");
                using (var os = OpenStore.Open(store, create: false, "selftest", DateTime.UtcNow.ToString("o")))
                {
                    assert(os.Dal.Count("file_entity") == 7 && os.Dal.Count("track_feature") == 6 && os.Dal.Count("check_result") == 2 && os.Dal.Count("work_item") == 1,
                        "re-import leaves entity/feature/check/work counts unchanged");
                    assert(os.Dal.Count("run") == 5, $"re-import adds exactly one run (its own); ledger runs are not duplicated ({os.Dal.Count("run")})");
                }

                // Mirror: the same library under X:\Library, imported as a mirror root -> locations, not entities.
                var mirror = Path.Combine(mirrorDir, "mbxmoods.json");
                WriteCatalog(mirror, FixtureEntries(@"X:\Library"));
                var r3 = ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = mirror, LibraryRoot = @"X:\Library", RootRole = "mirror" });
                assert(r3.EntitiesCreated == 0 && r3.CreatedNoSha == 0 && r3.Locations == 6 && r3.MatchedHash == 5 && r3.MatchedPath == 1,
                    $"mirror import: 0 entities created, 6 new locations, 5 matched by sha, the no-sha one matched by canonical path across roots ({r3.EntitiesCreated}/{r3.CreatedNoSha}/{r3.Locations}/{r3.MatchedHash}/{r3.MatchedPath})");
                using (var os = OpenStore.Open(store, create: false, "selftest", DateTime.UtcNow.ToString("o")))
                    assert(os.Dal.Count("file_entity") == 7 && os.Dal.Count("track_location") == 14 && os.Dal.Count("library_root") == 2,
                        $"after the mirror: 7 entities, 14 locations (8 + 6 mirrored), 2 roots ({os.Dal.Count("file_entity")}/{os.Dal.Count("track_location")}/{os.Dal.Count("library_root")})");

                // Batch atomicity: fail after 3 entries with batches of 2 -> exactly the first batch is in the store.
                var store2 = Path.Combine(dir, "partial.db");
                bool threw = false;
                try { ImportCommand.Execute(new ImportOptions { StorePath = store2, CatalogPath = catalog, LibraryRoot = @"D:\Music", BatchSize = 2, FailAfterEntries = 3 }); } catch (Exception) { threw = true; }
                assert(threw, "the fail-after seam throws");
                using (var os = OpenStore.Open(store2, create: false, "selftest", DateTime.UtcNow.ToString("o")))
                {
                    assert(os.Dal.Count("track_feature") == 2, $"after a mid-import failure the store holds whole batches only ({os.Dal.Count("track_feature")} rows, batch size 2)");
                    assert(os.Db.ExecScalar("SELECT outcome FROM run WHERE producer_key = 'import';") == "failed", "the interrupted import's run row says failed, not ok");
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>The catalog writer's bytes for one entry, as a string — the unit of the round-trip gate.</summary>
        internal static string EntryJson(string path, TrackEntry entry) => ExportCommand.EntryJson(path, entry);

        // ---------------------------------------------------------------- mapper
        static void TestMapper(Action<bool, string> assert)
        {
            // Column set == DDL minus the meta columns, both ways (the mapper and v1.sql cannot drift apart).
            var db = FreshStore(out var dir);
            try
            {
                var ddl = new HashSet<string>(StringComparer.Ordinal);
                using (var st = db.Prepare("PRAGMA table_info(track_feature);"))
                    while (st.Step()) ddl.Add(st.GetString(1)!);
                foreach (var m in StoreSchema.TrackFeatureMetaColumns) ddl.Remove(m);
                var mapper = new HashSet<string>(TrackEntryMapper.ColumnNames, StringComparer.Ordinal);
                var onlyDdl = new List<string>(); foreach (var c in ddl) if (!mapper.Contains(c)) onlyDdl.Add(c);
                var onlyMap = new List<string>(); foreach (var c in mapper) if (!ddl.Contains(c)) onlyMap.Add(c);
                assert(onlyDdl.Count == 0 && onlyMap.Count == 0,
                    "mapper columns == DDL columns" + (onlyDdl.Count > 0 ? " — DDL only: " + string.Join(",", onlyDdl) : "") + (onlyMap.Count > 0 ? " — mapper only: " + string.Join(",", onlyMap) : ""));
                assert(TrackEntryMapper.ColumnNames.Count == ddl.Count && ddl.Count > 100, $"{ddl.Count} mapped columns");
            }
            finally { db.Dispose(); try { Directory.Delete(dir, true); } catch { } }

            const string path = @"D:\Music\x.flac";
            var full = FullyPopulatedEntry();
            var before = EntryJson(path, full);
            var back = TrackEntryMapper.FromValues(TrackEntryMapper.ToRow(full), full.LastModified);
            var after = EntryJson(path, back);
            assert(before == after, "fully populated entry: writer bytes identical after ToRow → FromValues" + (before == after ? "" : FirstDiff(before, after)));

            var minimal = new TrackEntry { Features = new TrackFeatures(), LastModified = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc) };
            var minBefore = EntryJson(path, minimal);
            var minBack = TrackEntryMapper.FromValues(TrackEntryMapper.ToRow(minimal), minimal.LastModified);
            assert(minBefore == EntryJson(path, minBack), "minimal entry (every nullable absent): writer bytes identical" + (minBefore == EntryJson(path, minBack) ? "" : FirstDiff(minBefore, EntryJson(path, minBack))));
            assert(minBack.FingerprintV1 == null && minBack.Features.KeyVoteEdma == null && minBack.Features.BitUsage == null
                && minBack.Features.HfSpectralStructure == null && minBack.Features.Mfcc == null && minBack.Features.SmfmScores == null,
                "absent blocks come back null, not as empty objects");

            // Full double precision survives: mfcc is written by WriteShortestNumber, not rounded.
            var prec = new TrackEntry { Features = new TrackFeatures { Mfcc = new[] { -821.3456789012345, 1e-9, 0.1, 12345678.9 } }, LastModified = DateTime.UtcNow };
            var precBack = TrackEntryMapper.FromValues(TrackEntryMapper.ToRow(prec), prec.LastModified);
            assert(precBack.Features.Mfcc != null && precBack.Features.Mfcc.Length == 4
                && precBack.Features.Mfcc[0] == -821.3456789012345 && precBack.Features.Mfcc[1] == 1e-9 && precBack.Features.Mfcc[3] == 12345678.9,
                "vectors round-trip bit-exact (float64 blobs)");
            var smfm = new TrackEntry { Features = new TrackFeatures { SmfmScores = new[] { 0, 255, 17 } }, LastModified = DateTime.UtcNow };
            var smfmBack = TrackEntryMapper.FromValues(TrackEntryMapper.ToRow(smfm), smfm.LastModified);
            assert(smfmBack.Features.SmfmScores != null && smfmBack.Features.SmfmScores.Length == 3 && smfmBack.Features.SmfmScores[1] == 255, "int vectors round-trip (int32 blobs)");
        }

        static string FirstDiff(string a, string b)
        {
            int i = 0; while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
            int from = Math.Max(0, i - 40);
            return $" — first difference at {i}: before='{a.Substring(from, Math.Min(90, a.Length - from))}' after='{b.Substring(from, Math.Min(90, b.Length - from))}'";
        }

        // ---------------------------------------------------------------- feature rows in the store
        static void TestFeatureRows(Action<bool, string> assert)
        {
            var db = FreshStore(out var dir);
            try
            {
                using var dal = new StoreDal(db);
                var now = "2026-10-11T10:00:00.0000000Z";
                var m = MachineIdentity.EnsureMinted(dal, "ATOM", now);
                long root = dal.EnsureRoot(m.Id, @"D:\Music", "primary", now);
                var full = FullyPopulatedEntry();
                const string display = @"D:\Music\Tool\a.flac";
                var r = EntityResolver.Resolve(dal, full.AudioStreamSha256, FacetKinds.ShaFrames, root, "tool/a.flac", "import:test", now);
                long loc = dal.UpsertLocation(r.EntityId, root, "Tool/a.flac", "tool/a.flac", display, full.LastModified.ToString("o"), now);

                using (var tx = db.BeginTransaction())
                {
                    dal.UpsertFeature(loc, full.AudioStreamSha256!, FacetKinds.ShaFrames, null, full, now);
                    dal.UpsertFeature(loc, full.AudioStreamSha256!, FacetKinds.ShaFrames, null, full, now);
                    tx.Complete();
                }
                assert(dal.Count("track_feature") == 1, "UpsertFeature twice on one location -> one row");
                assert(db.ExecScalar($"SELECT content_key_kind FROM track_feature WHERE location_id = {loc};") == FacetKinds.ShaFrames,
                    "the feature row carries the scheme of its content key (restfulbee ask: version every hash)");

                var read = new List<(string, TrackEntry)>(dal.ReadEntries(root));
                assert(read.Count == 1 && read[0].Item1 == display, "ReadEntries yields the location's display path");
                var before = EntryJson(display, full);
                var after = read.Count == 1 ? EntryJson(display, read[0].Item2) : "";
                assert(before == after, "entry read back from the store writes identical bytes" + (before == after ? "" : FirstDiff(before, after)));
                assert(read.Count == 1 && read[0].Item2.LastModified == full.LastModified && read[0].Item2.LastModified.Kind == full.LastModified.Kind,
                    "lastModified comes back from track_location, exact including Kind");
            }
            finally { db.Dispose(); try { Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>A fresh schema-v1 store in a temp dir; caller disposes and deletes.</summary>
        static SqliteDb FreshStore(out string dir)
        {
            dir = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "store-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var db = SqliteDb.Open(Path.Combine(dir, "store.db"), create: true);
            SchemaMigrator.Apply(db, "selftest");
            return db;
        }

        // ---------------------------------------------------------------- machine identity
        static void TestMachineIdentity(Action<bool, string> assert)
        {
            var db = FreshStore(out var dir);
            var lines = new List<string>(); var oldSink = StoreLog.Sink; var oldThr = StoreLog.Threshold;
            try
            {
                StoreLog.Sink = lines.Add; StoreLog.SetVerbose(false);
                var dal = new StoreDal(db);
                var now = "2026-10-11T10:00:00.0000000Z";
                var m1 = MachineIdentity.EnsureMinted(dal, "ATOM", now);
                assert(m1.Id.Length == 36 && m1.Id[14] == '7', "first open mints a UUIDv7 machine id");
                assert(m1.HostMatches && m1.CreatedByHost == "ATOM", "created-by host recorded, matches");
                assert(dal.Count("machine") == 1 && dal.MetaGet("machine_id") == m1.Id, "one machine row, id in store_meta");

                var m2 = MachineIdentity.EnsureMinted(dal, "atom", now);
                assert(m2.Id == m1.Id && dal.Count("machine") == 1, "second open reads the same id (case-insensitive host), mints nothing");
                m2.RequireWritable(adopt: false);   // must not throw
                var warns = lines.FindAll(l => l.Contains(" WARN "));
                assert(warns.Count == 0, "matching host logs no warning (INFO lines are fine)");

                var m3 = MachineIdentity.EnsureMinted(dal, "MAX1", now);
                assert(!m3.HostMatches && m3.Id == m1.Id, "a copied store opened on another host keeps the id but HostMatches is false");
                warns = lines.FindAll(l => l.Contains(" WARN "));
                assert(warns.Count == 1 && warns[0].Contains("ATOM") && warns[0].Contains("MAX1"),
                    $"hostname mismatch warns once, naming both hosts ({(warns.Count > 0 ? warns[0] : "<none>")})");
                bool refused = false;
                try { m3.RequireWritable(adopt: false); } catch (StoreException) { refused = true; }
                assert(refused, "writes as this machine are refused on a host mismatch without --machine-adopt");
                m3.RequireWritable(adopt: true);
                assert(true, "--machine-adopt lets the write proceed");

                long r1 = dal.EnsureRoot(m1.Id, @"D:\Music", "primary", now);
                long r2 = dal.EnsureRoot(m1.Id, @"D:\Music", "primary", now);
                long r3 = dal.EnsureRoot(m1.Id, @"X:\Library", "mirror", now);
                assert(r1 == r2 && r3 != r1 && dal.Count("library_root") == 2, "EnsureRoot is idempotent per (machine, path) and distinct per path");
            }
            finally { StoreLog.Sink = oldSink; StoreLog.Threshold = oldThr; db.Dispose(); try { Directory.Delete(dir, true); } catch { } }
        }

        // ---------------------------------------------------------------- entity resolution
        static void TestEntityResolver(Action<bool, string> assert)
        {
            var db = FreshStore(out var dir);
            try
            {
                var dal = new StoreDal(db);
                var now = "2026-10-11T10:00:00.0000000Z";
                var m = MachineIdentity.EnsureMinted(dal, "ATOM", now);
                long root = dal.EnsureRoot(m.Id, @"D:\Music", "primary", now);
                long mirror = dal.EnsureRoot(m.Id, @"X:\Library", "mirror", now);
                const string shaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
                const string shaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

                assert(FacetKinds.ForShaSource("flac-frames", true) == FacetKinds.ShaFrames
                    && FacetKinds.ForShaSource(null, false) == FacetKinds.ShaFrames
                    && FacetKinds.ForShaSource(null, true) == FacetKinds.ShaTaglibRegion
                    && FacetKinds.ForShaSource("whole-file", false) == FacetKinds.ShaWholeFile,
                    "sha source -> facet kind: flac-frames/invariant -> primary kind, FLAC with no source -> legacy taglib-region, whole-file -> whole-file");

                var r1 = EntityResolver.Resolve(dal, shaA, FacetKinds.ShaFrames, root, "tool/a.flac", "import:test", now);
                assert(r1.How == ResolveHow.Created && r1.EntityId.Length == 36, "new sha + new path -> Created");
                assert(dal.HasFacet(r1.EntityId, FacetKinds.ShaFrames, shaA, out var prim) && prim && dal.CountFacets(r1.EntityId) == 1,
                    "created entity has exactly one facet: the sha, primary");
                dal.UpsertLocation(r1.EntityId, root, "Tool/a.flac", "tool/a.flac", @"D:\Music\Tool\a.flac", now, now);

                var r2 = EntityResolver.Resolve(dal, shaA, FacetKinds.ShaFrames, root, "tool/a.flac", "import:test", now);
                assert(r2.How == ResolveHow.MatchedHash && r2.EntityId == r1.EntityId && dal.Count("file_entity") == 1,
                    "same sha again -> MatchedHash, same id, nothing minted (idempotent on content hash)");

                var r3 = EntityResolver.Resolve(dal, shaA, FacetKinds.ShaFrames, mirror, "tool/a.flac", "import:mirror", now);
                assert(r3.How == ResolveHow.MatchedHash && r3.EntityId == r1.EntityId, "same sha under a MIRROR root -> same entity (the X:/P: case)");
                dal.UpsertLocation(r1.EntityId, mirror, "Tool/a.flac", "tool/a.flac", @"X:\Library\Tool\a.flac", now, now);
                assert(dal.Count("track_location") == 2 && dal.Count("file_entity") == 1, "two locations, one entity");

                var r4 = EntityResolver.Resolve(dal, null, FacetKinds.ShaFrames, root, "odd/nosha.mp3", "import:test", now);
                assert(r4.How == ResolveHow.CreatedNoSha && dal.HasFacet(r4.EntityId, FacetKinds.PathCanonical, "odd/nosha.mp3", out _),
                    "no sha, new path -> CreatedNoSha with a path:canonical facet only");
                dal.UpsertLocation(r4.EntityId, root, "odd/nosha.mp3", "odd/nosha.mp3", @"D:\Music\odd\nosha.mp3", now, now);
                var r5 = EntityResolver.Resolve(dal, shaB, FacetKinds.ShaFrames, root, "odd/nosha.mp3", "import:test", now);
                assert(r5.How == ResolveHow.MatchedPath && r5.EntityId == r4.EntityId, "a sha arriving later for a known path -> MatchedPath, same entity");
                assert(dal.HasFacet(r4.EntityId, FacetKinds.ShaFrames, shaB, out var p5) && p5 && dal.HasFacet(r4.EntityId, FacetKinds.PathCanonical, "odd/nosha.mp3", out _),
                    "the new sha is added as primary; the path facet is kept (facets are never deleted)");

                // Legacy FLAC: first seen with the pre-2026-07-21 sha, later the frame-anchored one at the same path.
                const string legacy = "1111111111111111111111111111111111111111111111111111111111111111";
                const string frames = "2222222222222222222222222222222222222222222222222222222222222222";
                var l1 = EntityResolver.Resolve(dal, legacy, FacetKinds.ShaTaglibRegion, root, "old/f.flac", "import:2026-06", now);
                dal.UpsertLocation(l1.EntityId, root, "old/f.flac", "old/f.flac", @"D:\Music\old\f.flac", now, now);
                var l2 = EntityResolver.Resolve(dal, frames, FacetKinds.ShaFrames, root, "old/f.flac", "import:2026-10", now);
                assert(l2.How == ResolveHow.MatchedPath && l2.EntityId == l1.EntityId, "re-keyed FLAC at the same path -> same entity");
                assert(dal.HasFacet(l1.EntityId, FacetKinds.ShaTaglibRegion, legacy, out var pl) && !pl
                    && dal.HasFacet(l1.EntityId, FacetKinds.ShaFrames, frames, out var pf) && pf,
                    "old sha kept as a NON-primary facet, new sha primary - the key move is recoverable from the store");
                assert(EntityResolver.Resolve(dal, legacy, FacetKinds.ShaTaglibRegion, mirror, "old/f.flac", "import:mirror-old", now).EntityId == l1.EntityId,
                    "a mirror still carrying the OLD sha resolves to the same entity through the kept facet");

                long loc1 = dal.UpsertLocation(r1.EntityId, root, "Tool/a.flac", "tool/a.flac", @"D:\Music\Tool\a.flac", "2026-01-01T00:00:00.0000000Z", now);
                long loc2 = dal.UpsertLocation(r1.EntityId, root, "Tool/a.flac", "tool/a.flac", @"D:\Music\Tool\a.flac", "2026-02-02T00:00:00.0000000Z", now);
                assert(loc1 == loc2 && dal.Count("track_location") == 4, "UpsertLocation on the same (root, canonical) updates in place, same id");
                assert(db.ExecScalar($"SELECT last_modified_utc FROM track_location WHERE id = {loc1};") == "2026-02-02T00:00:00.0000000Z", "upsert refreshed last_modified_utc");
                assert(dal.FindLocation(root, "tool/a.flac", out var eid) == loc1 && eid == r1.EntityId, "FindLocation returns id and entity");
                assert(dal.FindLocation(root, "nope/x.flac", out _) == null, "FindLocation misses cleanly");
            }
            finally { db.Dispose(); try { Directory.Delete(dir, true); } catch { } }
        }

        // ---------------------------------------------------------------- uuid v7
        static void TestUuid7(Action<bool, string> assert)
        {
            var a = Uuid7.New();
            assert(a.Length == 36 && a[8] == '-' && a[13] == '-' && a[18] == '-' && a[23] == '-', $"canonical 8-4-4-4-12 text ({a})");
            assert(a == a.ToLowerInvariant(), "lowercase hex");
            assert(a[14] == '7', $"version nibble is 7 (got '{a[14]}')");
            assert("89ab".IndexOf(a[19]) >= 0, $"variant nibble is 8/9/a/b (got '{a[19]}')");
            bool ok = true; string prev = a; int n = 2000;
            for (int i = 0; i < n && ok; i++) { var next = Uuid7.New(); ok = string.CompareOrdinal(next, prev) > 0; prev = next; }
            assert(ok, $"{n} ids minted back-to-back are strictly increasing as text (time-ordered within a millisecond)");
            var distinct = new HashSet<string>();
            for (int i = 0; i < 5000; i++) distinct.Add(Uuid7.New());
            assert(distinct.Count == 5000, "5000 ids are distinct");
            // Time field: the first 48 bits are unix milliseconds — must decode to roughly now.
            long ms = Convert.ToInt64(a.Substring(0, 8) + a.Substring(9, 4), 16);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            assert(Math.Abs(now - ms) < 60_000, $"leading 48 bits are unix ms (decoded {ms}, now {now})");
        }

        // ---------------------------------------------------------------- canonical paths
        static void TestCanonicalPath(Action<bool, string> assert)
        {
            CanonicalPath.Split(@"D:\Music", @"D:\Music\Tool\Lateralus\01 - The Grudge.flac", out var rel, out var canon);
            assert(rel == "Tool/Lateralus/01 - The Grudge.flac", $"rel keeps case, '/' separators ({rel})");
            assert(canon == "tool/lateralus/01 - the grudge.flac", $"canonical is case-folded ({canon})");

            CanonicalPath.Split(@"d:\music\", @"D:\Music\A\b.mp3", out rel, out canon);
            assert(rel == "A/b.mp3" && canon == "a/b.mp3", "root match is case-insensitive and tolerates a trailing separator");

            CanonicalPath.Split(@"\\nas\music", @"\\nas\music\X\y.flac", out rel, out canon);
            assert(rel == "X/y.flac" && canon == "x/y.flac", "UNC root");

            // NFD "é" (e + combining acute) becomes NFC "é" in both forms.
            CanonicalPath.Split(@"D:\M", "D:\\M\\Caf\u0065\u0301\\t.flac", out rel, out canon);
            assert(rel == "Caf\u00e9/t.flac" && canon == "caf\u00e9/t.flac", "NFC normalisation (decomposed é folds to composed)");

            bool threw = false;
            try { CanonicalPath.Split(@"D:\Music", @"E:\Other\x.flac", out _, out _); } catch (ArgumentException) { threw = true; }
            assert(threw, "a path outside the root throws ArgumentException");

            assert(CanonicalPath.VolumeOf(@"d:\Music\x.flac") == "D:" && CanonicalPath.VolumeOf(@"\\nas\music\x.flac") == @"\\nas\music",
                "VolumeOf: drive letter upper-cased; UNC = \\\\server\\share");

            var roots = CanonicalPath.InferRoots(new[]
            {
                @"D:\Music\Tool\a.flac", @"D:\Music\Tool\b.flac", @"D:\Music\Zeppelin\c.flac",
                @"\\nas\music\Live\d.flac", @"\\nas\music\Live\e.flac",
            });
            roots.Sort(StringComparer.OrdinalIgnoreCase);
            assert(roots.Count == 2 && roots[0] == @"D:\Music" && roots[1] == @"\\nas\music\Live",
                $"InferRoots: longest common directory per volume ({string.Join(" | ", roots)})");
            var one = CanonicalPath.InferRoots(new[] { @"D:\Music\Tool\a.flac" });
            assert(one.Count == 1 && one[0] == @"D:\Music\Tool", "InferRoots: a single path yields its directory");
        }

        // ---------------------------------------------------------------- schema v1
        static readonly string[] ExpectedTables =
        {
            "schema_version", "store_meta", "machine", "library_root", "file_entity", "identity_facet",
            "track_location", "producer", "run", "work_item", "check_result", "conflict",
            "value", "segment", "artifact", "track_feature",
        };

        static void TestSchema(Action<bool, string> assert)
        {
            assert(StoreSchema.FlattenKey(null, "bpm") == "bpm", "FlattenKey: top-level key unchanged");
            assert(StoreSchema.FlattenKey("keyVotes", "krumhansl") == "keyVotes_krumhansl", "FlattenKey: nested joins with '_'");
            assert(StoreSchema.FlattenKey(null, "fingerprint.v1") == "fingerprint_v1", "FlattenKey: '.' becomes '_'");
            assert(StoreSchema.FlattenKey("fingerprint_v1", "mp3LameTag") == "fingerprint_v1_mp3LameTag", "FlattenKey: two levels");

            var dir = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "schema-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                using (var db = SqliteDb.Open(Path.Combine(dir, "store.db"), create: true))
                {
                    SchemaMigrator.Apply(db, "selftest");
                    assert(SchemaMigrator.ReadVersion(db) == StoreSchema.CurrentVersion, $"schema_version == {StoreSchema.CurrentVersion} after Apply on an empty store");
                    foreach (var t in ExpectedTables)
                        assert(db.ExecScalar($"SELECT count(*) FROM sqlite_master WHERE type='table' AND name='{t}';") == "1", $"table {t} exists");
                    assert(db.ExecScalar("SELECT count(*) FROM producer;") == "3", "producer seeded with essentia/import/verify");
                    assert(db.ExecScalar("SELECT truedat_version FROM schema_version WHERE version = 1;") == "selftest", "schema_version records the truedat version that applied it");

                    SchemaMigrator.Apply(db, "selftest-again");
                    assert(SchemaMigrator.ReadVersion(db) == StoreSchema.CurrentVersion && db.ExecScalar("SELECT count(*) FROM schema_version;") == "1",
                        "Apply is idempotent on a current store (no second row, no error)");

                    // The drift test: every key the catalog writer emits has a column, and every column is an emitted key.
                    var emitted = EmittedCatalogKeys();
                    assert(emitted.Count > 100, $"writer emits a populated entry with >100 flattened keys (got {emitted.Count})");
                    var columns = new HashSet<string>(StringComparer.Ordinal);
                    using (var st = db.Prepare("PRAGMA table_info(track_feature);"))
                        while (st.Step()) columns.Add(st.GetString(1)!);
                    foreach (var meta in StoreSchema.TrackFeatureMetaColumns) columns.Remove(meta);
                    columns.Add("lastModified");   // lives on track_location.last_modified_utc by design
                    var missing = new List<string>();
                    foreach (var k in emitted) if (!columns.Contains(k)) missing.Add(k);
                    assert(missing.Count == 0, "DRIFT: every emitted catalog key has a track_feature column" + (missing.Count == 0 ? "" : " — missing: " + string.Join(", ", missing)));
                    var dead = new List<string>();
                    // A column the writer does not emit is dead UNLESS the emission registry (FieldPolicy) is what
                    // cuts it: trackId and bpmHistogram are TrackFeatures fields the store still captures.
                    foreach (var c in columns) if (!emitted.Contains(c) && !FieldPolicy.IsExcluded(c) && c != "lastModified") dead.Add(c);
                    assert(dead.Count == 0, "DRIFT: every track_feature column is an emitted catalog key" + (dead.Count == 0 ? "" : " — dead: " + string.Join(", ", dead)));

                    // Refuses a store newer than the exe.
                    db.Exec("INSERT INTO schema_version(version, applied_utc, truedat_version) VALUES (999, '2099-01-01T00:00:00Z', 'future');");
                    bool refused = false; string? msg = null;
                    try { SchemaMigrator.Apply(db, "selftest"); } catch (StoreException ex) { refused = true; msg = ex.Message; }
                    assert(refused && msg != null && msg.Contains("999") && msg.IndexOf("newer", StringComparison.OrdinalIgnoreCase) >= 0,
                        $"a store at schema 999 is refused with a sentence naming the versions ({msg})");
                }
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch { } }
        }

        /// <summary>Writes a fully populated TrackEntry through the real catalog writer and returns the
        /// flattened key set it emitted (the <c>truedat</c> verdict block excluded — it is write-time
        /// derived, never stored). Generated, never hand-listed.</summary>
        internal static HashSet<string> EmittedCatalogKeys()
        {
            var entry = FullyPopulatedEntry();
            using var ms = new MemoryStream();
            using (var jw = new System.Text.Json.Utf8JsonWriter(ms))
            {
                jw.WriteStartObject();
                Program.WriteTrackEntryForStore(jw, @"C:\m\x.flac", entry);
                jw.WriteEndObject();
            }
            ms.Position = 0;
            using var doc = System.Text.Json.JsonDocument.Parse(ms);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var top in doc.RootElement.EnumerateObject())
                Walk(top.Value, null, keys);
            return keys;

            static void Walk(System.Text.Json.JsonElement obj, string? prefix, HashSet<string> into)
            {
                foreach (var p in obj.EnumerateObject())
                {
                    if (prefix == null && p.Name == "truedat") continue;
                    var flat = StoreSchema.FlattenKey(prefix, p.Name);
                    if (p.Value.ValueKind == System.Text.Json.JsonValueKind.Object) Walk(p.Value, flat, into);
                    else into.Add(flat);
                }
            }
        }

        /// <summary>Every public field/property of TrackEntry, TrackFeatures and their nested blocks set
        /// to a non-default value, by reflection, so the writer emits every key it knows.</summary>
        internal static TrackEntry FullyPopulatedEntry()
        {
            var e = new TrackEntry { Features = new TrackFeatures(), LastModified = new DateTime(2026, 10, 11, 1, 2, 3, DateTimeKind.Utc) };
            Populate(e.Features);
            e.AnalysisDurationSecs = 12.5;
            e.FileMd5 = "0123456789abcdef0123456789abcdef";
            e.AudioStreamSha256 = "ab".PadRight(64, 'c');
            e.AudioStreamSha256Source = "flac-frames";
            var fp = new Program.FingerprintV1();
            Populate(fp);
            fp.AudioHead64kMd5Source = "flac-frames";   // "invariant" would be omitted by the writer
            e.FingerprintV1 = fp;
            return e;
        }

        static void Populate(object o)
        {
            var t = o.GetType();
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                f.SetValue(o, SampleFor(f.FieldType, f.Name));
            foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                if (p.CanWrite) p.SetValue(o, SampleFor(p.PropertyType, p.Name));
        }

        static object? SampleFor(Type type, string name)
        {
            var u = Nullable.GetUnderlyingType(type) ?? type;
            if (u == typeof(double)) return 1.5;
            if (u == typeof(int)) return 7;
            if (u == typeof(long)) return 9L;
            if (u == typeof(byte)) return (byte)3;
            if (u == typeof(bool)) return true;
            if (u == typeof(string)) return "x" + name;
            if (u == typeof(DateTime)) return new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            if (u == typeof(double[])) return new[] { 1.0, 2.0, 3.0 };
            if (u == typeof(int[])) return new[] { 1, 2, 3 };
            if (u.IsClass && u.GetConstructor(Type.EmptyTypes) != null)
            {
                var child = Activator.CreateInstance(u)!;
                Populate(child);
                return child;
            }
            return null;
        }

        static void Block(Action<bool, string> assert, string name, Action body)
        {
            try { body(); }
            catch (Exception ex) { assert(false, $"{name}: threw {ex.GetType().Name}: {ex.Message}"); }
        }

        // ---------------------------------------------------------------- StoreLog
        static void TestStoreLog(Action<bool, string> assert)
        {
            var lines = new List<string>();
            var oldSink = StoreLog.Sink; var oldThreshold = StoreLog.Threshold;
            try
            {
                StoreLog.Sink = lines.Add;
                StoreLog.SetVerbose(false);
                StoreLog.Debug("hidden"); StoreLog.Trace("hidden");
                StoreLog.Info("shown-info"); StoreLog.Warn("shown-warn"); StoreLog.Error("shown-error");
                assert(lines.Count == 3, $"non-verbose: Debug/Trace suppressed, Info/Warn/Error pass (got {lines.Count})");
                assert(lines.Count == 3 && lines[0].StartsWith("[store] INFO ") && lines[0].EndsWith("shown-info"),
                    "line format is '[store] LEVEL message'");
                lines.Clear();
                StoreLog.SetVerbose(true);
                StoreLog.Debug("d"); StoreLog.Trace("t");
                assert(lines.Count == 2, "--audit (verbose) lets Debug and Trace through");
            }
            finally { StoreLog.Sink = oldSink; StoreLog.Threshold = oldThreshold; }
        }

        // ---------------------------------------------------------------- probe, through the seam
        static void TestProbeSeam(Action<bool, string> assert)
        {
            Func<string> ver = () => "x";
            Func<string, bool> opt = _ => false;

            var r = SqliteProbe.Probe(() => throw new DllNotFoundException("winsqlite3.dll"), ver, opt);
            assert(!r.Ok && r.Failure == SqliteProbeFailure.DllNotFound, "DllNotFoundException -> DllNotFound");
            assert(r.Message != null && r.Message.Contains("winsqlite3.dll") && !r.Message.Contains("Exception"),
                "DllNotFound message names the DLL and is a sentence, not an exception dump");

            r = SqliteProbe.Probe(() => throw new BadImageFormatException("x86"), ver, opt);
            assert(!r.Ok && r.Failure == SqliteProbeFailure.BadImage, "BadImageFormatException -> BadImage");

            r = SqliteProbe.Probe(() => throw new EntryPointNotFoundException("sqlite3_x"), ver, opt);
            assert(!r.Ok && r.Failure == SqliteProbeFailure.EntryPointMissing, "EntryPointNotFoundException -> EntryPointMissing");

            r = SqliteProbe.Probe(() => SqliteProbe.MinimumVersionNumber - 1, () => "3.39.9", opt);
            assert(!r.Ok && r.Failure == SqliteProbeFailure.VersionBelowMinimum, "version 3039999 -> VersionBelowMinimum");
            assert(r.Message != null && r.Message.Contains("3.39.9") && r.Message.Contains(SqliteProbe.MinimumVersionText),
                "VersionBelowMinimum message states found and required versions");

            r = SqliteProbe.Probe(() => SqliteProbe.MinimumVersionNumber, () => "3.40.0", o => o == "ENABLE_RTREE");
            assert(r.Ok && r.VersionNumber == SqliteProbe.MinimumVersionNumber && r.VersionText == "3.40.0",
                "exact minimum version is accepted");
            assert(r.HasRtree && !r.HasFts5, "compile options are probed per name (RTREE yes, FTS5 no)");
        }

        // ---------------------------------------------------------------- probe, for real
        static void TestProbeReal(Action<bool, string> assert)
        {
            var r = SqliteProbe.Probe();
            assert(r.Ok, $"real winsqlite3.dll probe is Ok ({r.Failure}: {r.Message})");
            assert(r.VersionNumber >= SqliteProbe.MinimumVersionNumber, $"real version {r.VersionText} >= {SqliteProbe.MinimumVersionText}");
            assert(r.HasRtree, "RTREE compiled in (true on every Windows build seen: Win10 + Win11)");
        }

        // ---------------------------------------------------------------- db
        static void TestDb(Action<bool, string> assert)
        {
            // Non-ASCII on purpose — the path footgun that gave truedat its staging subsystem.
            var dir = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "späike-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "store.db");
            try
            {
                bool threw = false;
                try { SqliteDb.Open(path, create: false).Dispose(); } catch (SqliteException) { threw = true; }
                assert(threw, "Open(create:false) on a missing file throws SqliteException");

                using (var db = SqliteDb.Open(path, create: true))
                {
                    assert(string.Equals(db.ExecScalar("PRAGMA journal_mode;"), "wal", StringComparison.OrdinalIgnoreCase), "WAL is on after Open");
                    assert(db.ExecScalar("PRAGMA foreign_keys;") == "1", "foreign_keys is ON after Open (recovery review: do not repeat FK-off)");

                    db.Exec("CREATE TABLE t (id INTEGER PRIMARY KEY, i INTEGER, d REAL, s TEXT, b BLOB, n TEXT);");

                    var blob = new byte[] { 1, 2, 3, 0, 255 };
                    using (var ins = db.Prepare("INSERT INTO t(i, d, s, b, n) VALUES (?, ?, ?, ?, ?);"))
                    {
                        ins.Bind(1, 42L).Bind(2, 2.5).Bind(3, "héllo").Bind(4, blob).BindNull(5);
                        assert(!ins.Step(), "INSERT Step() returns false (DONE)");
                    }
                    assert(db.Changes == 1, "Changes == 1 after one insert");
                    assert(db.LastInsertRowId == 1, "LastInsertRowId == 1");

                    using (var sel = db.Prepare("SELECT i, d, s, b, n FROM t WHERE id = ?;"))
                    {
                        sel.Bind(1, 1L);
                        assert(sel.Step(), "SELECT Step() returns true on a row");
                        assert(sel.GetInt64(0) == 42 && sel.GetDouble(1) == 2.5 && sel.GetString(2) == "héllo", "int64/double/UTF-8 text round-trip");
                        var back = sel.GetBlob(3);
                        assert(back != null && back.Length == 5 && back[4] == 255 && back[3] == 0, "blob round-trips including a 0 byte");
                        assert(sel.IsNull(4) && sel.GetString(4) == null, "NULL reads as IsNull and null string");
                        assert(!sel.Step(), "no second row");
                        sel.Reset(); sel.Bind(1, 1L);
                        assert(sel.Step() && sel.GetInt64(0) == 42, "Reset() lets a statement run again");
                    }

                    using (var tx = db.BeginTransaction())
                    {
                        db.Exec("INSERT INTO t(i) VALUES (7);");
                        // no Complete(): dispose must roll back
                    }
                    assert(db.ExecScalar("SELECT count(*) FROM t;") == "1", "transaction disposed without Complete() rolls back");
                    using (var tx = db.BeginTransaction())
                    {
                        db.Exec("INSERT INTO t(i) VALUES (8);");
                        tx.Complete();
                    }
                    assert(db.ExecScalar("SELECT count(*) FROM t;") == "2", "transaction with Complete() commits");

                    bool bad = false; int rc = 0; string? msg = null;
                    try { db.Exec("SELEKT nonsense;"); } catch (SqliteException ex) { bad = true; rc = ex.ResultCode; msg = ex.Message; }
                    assert(bad && rc != 0 && msg != null && msg.IndexOf("syntax", StringComparison.OrdinalIgnoreCase) >= 0,
                        $"bad SQL throws SqliteException with rc != 0 and sqlite's own message (rc={rc}: {msg})");
                }
                assert(File.Exists(path), "database file exists at the non-ASCII path after close");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }
}
