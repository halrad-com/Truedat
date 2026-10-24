using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
            Block(assert, "NestedRoots", () => TestNestedRoots(assert));
            Block(assert, "FlatnessDefinition", () => TestFlatnessDefinition(assert));
            Console.WriteLine("Store self-test (C7: verbs answering from the store)");
            Block(assert, "StoreDictionary", () => TestStoreDictionary(assert));
            Console.WriteLine("Store self-test (C8: --store-info measurements)");
            Block(assert, "StoreInfo", () => TestStoreInfo(assert));
            Console.WriteLine("Store self-test (page size: 16 KB pages by default, --store-vacuum repacks)");
            Block(assert, "Vacuum", () => TestVacuum(assert));
            Console.WriteLine("Store self-test (precision: the field's declared dp on the store path, no six-figure rewrite, the catalog writer untouched)");
            Block(assert, "StorePrecision", () => TestStorePrecision(assert));
            Console.WriteLine("Store self-test (T03: one FieldPrecision registry; every numeric column declared; the parser rounds from it)");
            Block(assert, "FieldPrecision", () => TestFieldPrecision(assert));
            Block(assert, "FieldPrecisionWiring", () => TestFieldPrecisionWiring(assert));
            Console.WriteLine("Store self-test (T04: schema v3 — typed columns, exact values, sparse histogram, v2 -> v3 migration with backup)");
            Block(assert, "SchemaV3", () => TestSchemaV3(assert));
            Console.WriteLine("Store self-test (S2 C0: default store location is local; network opens exclusive)");
            Block(assert, "StoreLocation", () => TestStoreLocation(assert));
            Console.WriteLine("Store self-test (S2 C2: a differing value is a conflict, never an overwrite; equal re-imports write nothing)");
            Block(assert, "Drift", () => TestDrift(assert));
            Console.WriteLine("Store self-test (keeper 067: a copy fact the arrival did not observe, or carries only at its written precision, keeps the stored value)");
            Block(assert, "CopyMerge", () => TestCopyMerge(assert));
            Block(assert, "PerFileIdentity", () => TestPerFileIdentity(assert));
            Block(assert, "RoundtripCopyFacts", () => TestRoundtripCopyFacts(assert));
            Block(assert, "RoundtripPrecision", () => TestRoundtripPrecision(assert));
            Console.WriteLine("Store self-test (keeper 083/084 N1: --refresh before an import says why no track is selected for edges, and names the import for this catalog/store)");
            Block(assert, "EdgesRefreshNotice", () => TestEdgesRefreshNotice(assert));
            Console.WriteLine("Store self-test (S2 C3: the scan writes the store through the import's in-memory entry)");
            Block(assert, "ScanSink", () => TestScanSink(assert));
            Console.WriteLine("Store self-test (S2 C-A: the extractor's aggregate output is kept, compressed, once per content)");
            Block(assert, "Artifacts", () => TestArtifacts(assert));
        }

        // ---------------------------------------------------------------- S2 C-A: the aggregate artifact
        static void TestArtifacts(Action<bool, string> assert)
        {
            var dir = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "art-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var store = Path.Combine(dir, "truedat.db");
            try
            {
                // The file store on its own: stash -> commit -> read back; same content key -> one file.
                const string json = "{\"lowlevel\":{\"spectral_centroid\":{\"mean\":1793.406,\"var\":12.5,\"median\":1790.1}},\"tonal\":{\"hpcp\":{\"mean\":[0.1,0.2,0.3]}}}";
                var pending = ArtifactStore.Stash(dir, json);
                assert(File.Exists(pending) && pending.StartsWith(ArtifactStore.PendingDir(dir), StringComparison.OrdinalIgnoreCase), "Stash writes a compressed pending file under artifacts\\pending");
                var key = "ab".PadRight(64, 'c');
                var c1 = ArtifactStore.Commit(dir, pending, key, "output-x64.4");
                assert(c1.Created && File.Exists(Path.Combine(dir, c1.RelPath)) && c1.RelPath == Path.Combine("artifacts", "output-x64.4", "ab", key + ArtifactStore.Extension) && c1.Bytes > 0 && c1.Sha256.Length == 64,
                    $"Commit files it under artifacts\\<build>\\<2>\\<key>.main.json.z with bytes and sha ({c1.RelPath}, {c1.Bytes} B)");
                assert(ArtifactStore.ReadMainJson(dir, c1.RelPath) == json, "the committed artifact inflates back to the exact extractor JSON (the re-fold entry point)");
                assert(c1.Bytes < json.Length, $"it is smaller than the JSON it holds ({c1.Bytes} < {json.Length})");
                var pending2 = ArtifactStore.Stash(dir, json);
                var c2 = ArtifactStore.Commit(dir, pending2, key, "output-x64.4");
                assert(!c2.Created && !File.Exists(pending2) && c2.Sha256 == c1.Sha256, "a second copy of the same content under the same build is discarded: one artifact per (content key, build)");
                // Review #1: a different extractor build is a different artifact — its own file, never the old file's bytes under a new label.
                var pending5 = ArtifactStore.Stash(dir, json.Replace("1793.406", "1793.5"));
                var c5 = ArtifactStore.Commit(dir, pending5, key, "output-x64.5");
                assert(c5.Created && c5.RelPath != c1.RelPath && c5.Sha256 != c1.Sha256 && File.Exists(Path.Combine(dir, c1.RelPath)) && File.Exists(Path.Combine(dir, c5.RelPath)),
                    $"the same content under a second build gets its own file with its own sha; the first stays ({c5.RelPath})");
                assert(ArtifactStore.RelativePathFor("entity:01a12a6b-5d68-7002-829d-3df22ece2565", "output-x64.4") == Path.Combine("artifacts", "output-x64.4", "en", "entity_01a12a6b-5d68-7002-829d-3df22ece2565" + ArtifactStore.Extension),
                    "a no-sha media's artifact is keyed by its entity id, ':' made path-safe");
                // The key and the build are path components built from store data: anything that is not a sha, an entity id or a plain build token is refused.
                bool refused = false;
                try { ArtifactStore.RelativePathFor(@"..\..\evil", "output-x64.4"); } catch (StoreException) { refused = true; }
                assert(refused, "a content key that is not a sha or entity id is refused (cannot escape artifacts\\)");
                refused = false;
                try { ArtifactStore.RelativePathFor(key, @"..\x"); } catch (StoreException) { refused = true; }
                assert(refused, "a build token with path characters is refused");

                // Through the scan sink: the core stashes by the analysed (staged) path, Record ties it to the catalog key, the flush files it.
                ScanSink.Reset();
                ScanSink.StoreDirectory = dir;
                var entries = FixtureEntries(@"D:\Music");
                ScanSink.StashMainJson(@"C:\stage\abc.flac", json);
                ScanSink.StashMainJson(@"C:\stage\def.mp3", json.Replace("1793.406", "900.0"));
                ScanSink.Record(@"D:\Music\Tool\a.flac", entries[@"D:\Music\Tool\a.flac"], @"C:\stage\abc.flac");
                ScanSink.Record(@"D:\Music\Tool\b.mp3", entries[@"D:\Music\Tool\b.mp3"], @"C:\stage\def.mp3");
                ScanSink.Record(@"D:\Music\odd\nosha.mp3", entries[@"D:\Music\odd\nosha.mp3"]);   // analysed without a stash (e.g. the stash failed): no artifact, still written
                assert(ScanSink.PendingArtifacts == 2, "two stashed outputs are tied to their catalog entries");
                var r = ScanSink.Flush(store, "output-x64.4", entries.Keys);
                assert(r.Ok && r.Artifacts == 2 && r.ArtifactsShared == 0 && r.ArtifactBytes > 0, $"flush files 2 artifacts ({r.Artifacts} new, {r.ArtifactsShared} shared, {r.ArtifactBytes} B, ok {r.Ok} {r.Error})");
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                {
                    assert(os.Dal.Count("artifact") == 2 && os.Db.ExecScalar("SELECT count(*) FROM artifact WHERE kind = 'main' AND format_version = 'output-x64.4' AND content_hash_kind = '" + FacetKinds.ShaFrames + "';") == "2",
                        $"artifact rows: kind main, format = the extractor build, keyed by the primary sha kind ({os.Dal.Count("artifact")})");
                    var sha = entries[@"D:\Music\Tool\a.flac"].AudioStreamSha256!;
                    var rel = os.Db.ExecScalar($"SELECT path FROM artifact WHERE content_hash = '{sha}';");
                    assert(rel != null && File.Exists(Path.Combine(dir, rel)) && ArtifactStore.ReadMainJson(dir, rel) == json, "the FLAC's artifact row points at a file that inflates to its extractor JSON");
                    assert(os.Db.ExecScalar("SELECT count(*) FROM artifact a JOIN run r ON r.id = a.run_id WHERE r.producer_key = 'essentia';") == "2", "each artifact points at the run that produced it");
                    assert(os.Dal.Count("track_content") == 3, "the entry without a stash is still written (3 descriptions)");
                    var info = StoreInfoCommand.Collect(store);
                    assert(info.ArtifactCount == 2 && info.ArtifactBytes == r.ArtifactBytes, $"--store-info reports artifact count and bytes ({info.ArtifactCount}, {info.ArtifactBytes})");
                }
                assert(Directory.GetFiles(ArtifactStore.PendingDir(dir)).Length == 0, "nothing is left in pending after a flush");

                // Same content again (a re-scan of the same file): shared, not duplicated.
                ScanSink.StashMainJson(@"C:\stage\abc2.flac", json);
                ScanSink.Record(@"D:\Music\Tool\a.flac", entries[@"D:\Music\Tool\a.flac"], @"C:\stage\abc2.flac");
                var r2 = ScanSink.Flush(store, "output-x64.4", entries.Keys);
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                    assert(r2.Ok && r2.Artifacts == 0 && r2.ArtifactsShared == 1 && os.Dal.Count("artifact") == 2, $"re-analysing the same audio under the same build adds no artifact ({r2.Artifacts} new, {r2.ArtifactsShared} shared, rows {os.Dal.Count("artifact")})");

                // Review #1 end to end: the same audio analysed by a newer build gets a second artifact file and row.
                ScanSink.StashMainJson(@"C:\stage\abc3.flac", json.Replace("1793.406", "1793.5"));
                ScanSink.Record(@"D:\Music\Tool\a.flac", entries[@"D:\Music\Tool\a.flac"], @"C:\stage\abc3.flac");
                var r5 = ScanSink.Flush(store, "output-x64.5", entries.Keys);
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                {
                    var sha = entries[@"D:\Music\Tool\a.flac"].AudioStreamSha256!;
                    assert(r5.Ok && r5.Artifacts == 1 && os.Dal.Count("artifact") == 3
                        && os.Db.ExecScalar($"SELECT count(DISTINCT path) FROM artifact WHERE content_hash = '{sha}';") == "2"
                        && os.Db.ExecScalar($"SELECT count(DISTINCT sha256) FROM artifact WHERE content_hash = '{sha}';") == "2",
                        $"a newer build files its own artifact: two paths, two shas for one content hash ({os.Dal.Count("artifact")} rows)");
                }

                // Review #2: a retry hands the core a different path (the downmixed WAV); the caller re-keys the stash to the source it will Record.
                ScanSink.StashMainJson(@"C:\stage\tmp-downmix.wav", json);
                ScanSink.Rekey(@"C:\stage\tmp-downmix.wav", @"C:\stage\multichannel.flac");
                ScanSink.Record(@"D:\Music\Tool\b.mp3", entries[@"D:\Music\Tool\b.mp3"], @"C:\stage\multichannel.flac");
                assert(ScanSink.PendingArtifacts == 1, "Rekey moves a stash from the retry's path to the source path Record will use");
                ScanSink.Rekey(@"C:\stage\never-stashed", @"C:\stage\x");   // a no-op, never a throw
                // Review #3: a stash the scan never ties (health-gate failure) is discarded explicitly, and anything still untied at the flush is dropped.
                ScanSink.StashMainJson(@"C:\stage\failed-gate.flac", json);
                var failedPending = Directory.GetFiles(ArtifactStore.PendingDir(dir)).Length;
                ScanSink.Discard(@"C:\stage\failed-gate.flac");
                assert(Directory.GetFiles(ArtifactStore.PendingDir(dir)).Length == failedPending - 1, "Discard deletes the stash of a track the health gate failed");
                ScanSink.StashMainJson(@"C:\stage\untied.flac", json);
                var stale = ArtifactStore.Stash(dir, "{}");
                File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-3));
                var r6 = ScanSink.Flush(store, "output-x64.4", entries.Keys);
                assert(r6.Ok && Directory.GetFiles(ArtifactStore.PendingDir(dir)).Length == 0 && !File.Exists(stale),
                    $"the flush drops every untied stash and sweeps stale pending files from a killed scan ({Directory.GetFiles(ArtifactStore.PendingDir(dir)).Length} left)");
                assert(ArtifactStore.SweepPending(dir, TimeSpan.FromDays(1)) == 0, "nothing stale remains to sweep");
                // Operator 2026-10-22: the pre-1c7b8dd spill place (<store>\artifacts\pending) is removed once it is empty — an empty
                // directory from 2026-10-18 sat in every upgraded instance; a legacy dir that still holds a fresh file stays.
                var prevWork = ArtifactStore.WorkRoot;
                ArtifactStore.WorkRoot = Path.Combine(dir, "stage");
                try
                {
                    var legacy = Path.Combine(dir, "artifacts", "pending");
                    Directory.CreateDirectory(legacy);
                    ArtifactStore.SweepPending(dir, TimeSpan.FromDays(1));
                    assert(!Directory.Exists(legacy), "the sweep removes the legacy spill directory once it is empty");
                    Directory.CreateDirectory(legacy);
                    File.WriteAllText(Path.Combine(legacy, "fresh.z"), "x");
                    ArtifactStore.SweepPending(dir, TimeSpan.FromDays(1));
                    assert(Directory.Exists(legacy) && File.Exists(Path.Combine(legacy, "fresh.z")), "a legacy spill directory holding a fresh file is left alone");
                }
                finally { ArtifactStore.WorkRoot = prevWork; }
            }
            finally { ScanSink.Reset(); ScanSink.StoreDirectory = null; try { Directory.Delete(dir, true); } catch { } }
        }

        // ---------------------------------------------------------------- S2 C3: the scan writes the store
        static void TestScanSink(Action<bool, string> assert)
        {
            var dir = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "sink-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var store = Path.Combine(dir, "truedat.db");
            try
            {
                // Parity: the in-memory entry and the file import produce the same store.
                var entries = FixtureEntries(@"D:\Music");
                var catalog = Path.Combine(dir, "mbxmoods.json");
                WriteCatalog(catalog, entries);
                var viaFile = Path.Combine(dir, "via-file.db");
                ImportCommand.Execute(new ImportOptions { StorePath = viaFile, CatalogPath = catalog, LibraryRoot = @"D:\Music" });
                var viaMem = Path.Combine(dir, "via-mem.db");
                var rm = ImportCommand.ExecuteEntries(viaMem, entries, @"D:\Music", "primary", false, "import", "1", "selftest:entries", null, null);
                assert(rm.Entries == 6 && rm.EntitiesCreated == 4 && rm.CreatedNoSha == 1 && rm.Locations == 6, $"ExecuteEntries over the fixture: 6 entries, 4 entities + 1 no-sha, 6 locations ({rm.Entries}/{rm.EntitiesCreated}+{rm.CreatedNoSha}/{rm.Locations})");
                using (var a = OpenStore.Open(viaFile, false, "selftest", DateTime.UtcNow.ToString("o")))
                using (var b = OpenStore.Open(viaMem, false, "selftest", DateTime.UtcNow.ToString("o")))
                {
                    long ra = a.Dal.EnsureRoot(a.Machine.Id, @"D:\Music", "primary", DateTime.UtcNow.ToString("o"));
                    long rb = b.Dal.EnsureRoot(b.Machine.Id, @"D:\Music", "primary", DateTime.UtcNow.ToString("o"));
                    var ea = new Dictionary<string, string>(PathComparer.Instance); foreach (var (d, e) in a.Dal.ReadEntries(ra)) ea[d] = EntryJson(d, e);
                    int same = 0; foreach (var (d, e) in b.Dal.ReadEntries(rb)) if (ea.TryGetValue(d, out var j) && j == EntryJson(d, e)) same++;
                    assert(same == 6 && ea.Count == 6, $"file import and in-memory import read back byte-identical entries ({same}/6)");
                    assert(a.Dal.Count("conflict") == b.Dal.Count("conflict") && a.Dal.Count("track_content") == b.Dal.Count("track_content"), "same conflicts and descriptions either way");
                }

                // The sink: two analysed entries and one failure from a scan, flushed as essentia@<build>.
                ScanSink.Reset();
                ScanSink.Record(@"D:\Music\Tool\a.flac", entries[@"D:\Music\Tool\a.flac"]);
                ScanSink.Record(@"D:\Music\Tool\b.mp3", entries[@"D:\Music\Tool\b.mp3"]);
                ScanSink.RecordFailure(@"D:\Music\bad\x.flac", "essentia", "essentia exit 1: decode error", 12.3);
                assert(ScanSink.Pending == 2 && ScanSink.PendingFailures == 1, "the sink collects analysed entries and failures");
                var r = ScanSink.Flush(store, "output-x64.4", entries.Keys);   // the whole catalog's keys are the root hint
                assert(r.Ok && r.Entries == 2 && r.Failures == 1 && r.Error == null, $"flush writes 2 entries and 1 failure ({r.Ok}, {r.Entries}/{r.Failures}, {r.Error})");
                assert(ScanSink.Pending == 0 && ScanSink.PendingFailures == 0, "flush consumes what it wrote");
                var srf = new StoreResultFile();
                bool srfRead = r.ResultPath != null && StoreResult.TryRead(r.ResultPath, out srf);
                long srfEntries = srf.Detail != null && srf.Detail.TryGetValue("entries", out var de) ? de : -1;
                long srfFailures = srf.Detail != null && srf.Detail.TryGetValue("failures", out var df) ? df : -1;
                assert(srfRead && srf.Verb == "scan" && srf.Store?.Path == Path.GetFullPath(store) && srfEntries == 2 && srfFailures == 1,
                    $"store-result.json is written beside the store as the scan's record with the flush's counts ({r.ResultPath}: read {srfRead}, verb {srf.Verb}, store {srf.Store?.Path}, entries {srfEntries}, failures {srfFailures})");
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                {
                    assert(os.Dal.Count("track_content") == 2 && os.Dal.Count("track_copy") == 2, $"2 descriptions, 2 copies ({os.Dal.Count("track_content")}/{os.Dal.Count("track_copy")})");
                    assert(os.Db.ExecScalar("SELECT count(*) FROM run WHERE producer_key = 'essentia' AND producer_version = 'output-x64.4' AND outcome = 'ok';") == "2", "one ok run per analysed entry, producer essentia@<build>");
                    assert(os.Db.ExecScalar("SELECT count(*) FROM run WHERE producer_key = 'essentia' AND outcome = 'failed' AND failed_components = 'essentia' AND error_text LIKE '%decode error%';") == "1", "the failure is a failed essentia run with its components and error");
                    assert(os.Db.ExecScalar("SELECT count(*) FROM track_content c JOIN run r ON r.id = c.run_id WHERE r.producer_key = 'essentia' AND r.location_id IS NOT NULL;") == "2"
                        && os.Db.ExecScalar("SELECT count(*) FROM track_content c JOIN run r ON r.id = c.run_id WHERE r.duration_ms IS NOT NULL;") == "1",
                        $"each description points at its own run on its location; the run carries the analysis duration when the entry has one (a.flac) ({os.Db.ExecScalar("SELECT count(*) FROM track_content c JOIN run r ON r.id = c.run_id WHERE r.duration_ms IS NOT NULL;")} with duration)");
                    assert(os.Db.ExecScalar("SELECT count(*) FROM track_location l WHERE l.display_path = 'D:\\Music\\bad\\x.flac';") == "1", "the failed file gets an entity and a location (it is a real file), no description");
                    assert(os.Db.ExecScalar("SELECT count(*) FROM library_root;") == "1" && os.Db.ExecScalar("SELECT path FROM library_root;") == @"D:\Music", "the root is inferred from the whole catalog's keys when none is given");
                }
                // A partial re-scan whose two files share a deeper folder must NOT mint a narrower root.
                ScanSink.Record(@"D:\Music\Tool\a.flac", entries[@"D:\Music\Tool\a.flac"]);
                ScanSink.Record(@"D:\Music\Tool\b.mp3", entries[@"D:\Music\Tool\b.mp3"]);
                var rp = ScanSink.Flush(store, "output-x64.4", null);   // no hints at all: the existing root must still cover them
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                    assert(rp.Ok && rp.Unchanged == 2 && os.Db.ExecScalar("SELECT count(*) FROM library_root;") == "1" && os.Dal.Count("track_location") == 3,
                        $"a partial re-scan under an existing root adds no root and no location ({os.Db.ExecScalar("SELECT count(*) FROM library_root;")} roots, {os.Dal.Count("track_location")} locations, {rp.Unchanged} unchanged)");

                // Same entries again (a re-scan that re-analysed the same two files): nothing written, drift rule applies.
                ScanSink.Record(@"D:\Music\Tool\a.flac", entries[@"D:\Music\Tool\a.flac"]);
                ScanSink.Record(@"D:\Music\Tool\b.mp3", entries[@"D:\Music\Tool\b.mp3"]);
                var r2 = ScanSink.Flush(store, "output-x64.4", entries.Keys);
                assert(r2.Ok && r2.Unchanged == 2 && r2.Drift == 0, $"re-flushing equal entries writes nothing ({r2.Unchanged} unchanged, {r2.Drift} drift, {r2.ContentReplaced} replaced, {r2.ContentRekeyed} rekeyed, {r2.Stale} stale, {r2.Entries} entries, {r2.EntitiesCreated} created, ok {r2.Ok} {r2.Error})");
                // A NEWER extractor build with a changed value replaces the description, with a resolved 'content-replaced' conflict as the trace (review #5).
                var newer = FixtureEntries(@"D:\Music");
                var bpm5 = newer[@"D:\Music\Tool\a.flac"].Features!.Bpm * 1.5;
                newer[@"D:\Music\Tool\a.flac"].Features!.Bpm = bpm5;
                ScanSink.Record(@"D:\Music\Tool\a.flac", newer[@"D:\Music\Tool\a.flac"]);
                var r3 = ScanSink.Flush(store, "output-x64.5", entries.Keys);
                assert(r3.Ok && r3.ContentReplaced == 1 && r3.Drift == 0, $"a newer extractor build replaces the description without a drift conflict ({r3.ContentReplaced} replaced, {r3.Drift} drift)");
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                    assert(os.Db.ExecScalar("SELECT count(*) FROM conflict WHERE kind = 'content-replaced' AND state = 'resolved' AND value_a = 'essentia@output-x64.4' AND value_b = 'essentia@output-x64.5' AND run_a IS NOT NULL AND run_b IS NOT NULL;") == "1",
                        "the replacement leaves a resolved content-replaced conflict naming both producers and both runs");
                // An OLDER build arriving over a newer description is drift, not a replacement: the newer description is kept.
                var older = FixtureEntries(@"D:\Music");
                older[@"D:\Music\Tool\a.flac"].Features!.Bpm = bpm5 * 2;
                ScanSink.Record(@"D:\Music\Tool\a.flac", older[@"D:\Music\Tool\a.flac"]);
                var r3b = ScanSink.Flush(store, "output-x64.3", entries.Keys);
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                    assert(r3b.Ok && r3b.ContentReplaced == 0 && r3b.Drift == 1
                        && StoredText(os, @"D:\Music\Tool\a.flac", "bpm") == Canon1(bpm5),
                        $"an older build (.3 over .5) is drift, never a replacement; the .5 description stays ({r3b.ContentReplaced} replaced, {r3b.Drift} drift; stored {StoredText(os, @"D:\Music\Tool\a.flac", "bpm")} vs {Canon1(bpm5)})");
                // An unknown build string cannot be ordered: it replaces, with the trace.
                var unk = FixtureEntries(@"D:\Music");
                unk[@"D:\Music\Tool\a.flac"].Features!.Bpm = 77;
                ScanSink.Record(@"D:\Music\Tool\a.flac", unk[@"D:\Music\Tool\a.flac"]);
                var r3c = ScanSink.Flush(store, "md5:deadbeef0123", entries.Keys);
                assert(r3c.Ok && r3c.ContentReplaced == 1, $"a build that cannot be ordered replaces (with its trace) rather than guessing ({r3c.ContentReplaced} replaced, {r3c.Drift} drift)");

                // T01 (F2, I01): a fresh measurement from the SAME producer/version REPLACES the description and stamps its own run.
                // The store never keeps the older value under the newer run; within-noise tolerance is import-only.
                var remeasured = FixtureEntries(@"D:\Music");
                var bpmSame = 77 * 1.25;
                remeasured[@"D:\Music\Tool\a.flac"].Features!.Bpm = bpmSame;           // beyond noise; same producer as the stored row (essentia@md5:deadbeef0123)
                ScanSink.Record(@"D:\Music\Tool\a.flac", remeasured[@"D:\Music\Tool\a.flac"]);
                var r6 = ScanSink.Flush(store, "md5:deadbeef0123", entries.Keys);
                string driftRowsAfterRemeasure;
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                {
                    driftRowsAfterRemeasure = os.Db.ExecScalar("SELECT count(*) FROM conflict WHERE kind = 'value-drift';") ?? "";
                    assert(r6.Ok && r6.Remeasured == 1 && r6.Drift == 0,
                        $"a same-producer fresh measurement beyond noise is a REMEASURE, never a kept-stored drift ({r6.Remeasured} remeasured, {r6.Drift} drift, {r6.ContentReplaced} replaced, {r6.Unchanged} unchanged)");
                    assert(StoredText(os, @"D:\Music\Tool\a.flac", "bpm") == Canon1(bpmSame),
                        $"the store holds the fresh value at bpm's declared precision ({StoredText(os, @"D:\Music\Tool\a.flac", "bpm")} vs {Canon1(bpmSame)} from {bpmSame})");
                    assert(os.Db.ExecScalar("SELECT count(*) FROM track_content c JOIN track_location l ON l.entity_id = c.entity_id WHERE l.display_path = 'D:\\Music\\Tool\\a.flac' AND c.run_id = (SELECT max(id) FROM run WHERE producer_key = 'essentia' AND producer_version = 'md5:deadbeef0123' AND outcome = 'ok');") == "1",
                        "the description's run is the measuring run (the newest ok run of that producer), not the older one");
                    assert(os.Db.ExecScalar("SELECT count(*) FROM conflict WHERE kind = 'value-drift' AND state = 'resolved' AND field = 'bpm' AND value_a = '77' AND value_b = '" + Canon1(bpmSame) + "';") == "1",
                        $"the old/new pair is kept as a RESOLVED value-drift record (history), not an open conflict ({os.Db.ExecScalar("SELECT count(*) FROM conflict WHERE kind = 'value-drift';")} value-drift rows: {os.Db.ExecScalar("SELECT group_concat(state || ':' || field || ':' || value_a || '>' || value_b, ' ') FROM conflict WHERE kind = 'value-drift';")})");
                }
                // Within run-to-run noise, same producer: STILL a remeasure — the exact fresh value and its run land; no conflict row (noise is not information).
                var noise = FixtureEntries(@"D:\Music");
                var bpmNoise = bpmSame * 1.001;
                noise[@"D:\Music\Tool\a.flac"].Features!.Bpm = bpmNoise;
                ScanSink.Record(@"D:\Music\Tool\a.flac", noise[@"D:\Music\Tool\a.flac"]);
                var r7 = ScanSink.Flush(store, "md5:deadbeef0123", entries.Keys);
                // "Exactly" = as the mapper stores it: the store path's precision rule (six significant figures until T04 removes it)
                // applies to every write, so the oracle is the mapper's own rendering of the fresh entry, never a literal.
                int bpmCol = TrackEntryMapper.ContentIndexOf("bpm");
                var bpmNoiseStored = TrackEntryMapper.FormatContent(bpmCol, TrackEntryMapper.ContentValue(TrackEntryMapper.ToRow(noise[@"D:\Music\Tool\a.flac"]), bpmCol));
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                    assert(r7.Ok && r7.Remeasured == 1 && r7.Unchanged == 0 && r7.Drift == 0
                        && StoredText(os, @"D:\Music\Tool\a.flac", "bpm") == bpmNoiseStored
                        && os.Db.ExecScalar("SELECT count(*) FROM conflict WHERE kind = 'value-drift';") == driftRowsAfterRemeasure,
                        $"a within-noise fresh measurement still replaces exactly (as the mapper stores it), with no new conflict row ({r7.Remeasured} remeasured, {r7.Unchanged} unchanged, stored {StoredText(os, @"D:\Music\Tool\a.flac", "bpm")} vs {bpmNoiseStored}, conflicts {os.Db.ExecScalar("SELECT count(*) FROM conflict WHERE kind = 'value-drift';")} vs {driftRowsAfterRemeasure})");

                // T01 review finding 1: a remeasure must not wipe stored values that the arrival did not observe (NULL columns —
                // ffmpeg absent, --no-bitusage). First store a bitUsage under this producer, then remeasure with it nulled and bpm changed.
                var withBits = FixtureEntries(@"D:\Music");
                withBits[@"D:\Music\Tool\a.flac"].Features!.Bpm = bpmNoise;
                withBits[@"D:\Music\Tool\a.flac"].Features!.BitUsage = new BitUsageSummary { LowestNonZeroBit = 9, BottomBitActivity = 0.25, EffectiveBits = 23, SamplesAnalyzed = 500, Method = "test" };
                ScanSink.Record(@"D:\Music\Tool\a.flac", withBits[@"D:\Music\Tool\a.flac"]);
                var r8 = ScanSink.Flush(store, "md5:deadbeef0123", entries.Keys);
                var noBits = FixtureEntries(@"D:\Music");
                noBits[@"D:\Music\Tool\a.flac"].Features!.Bpm = bpmNoise * 1.3;      // a real change, so the remeasure fires
                noBits[@"D:\Music\Tool\a.flac"].Features!.BitUsage = null;          // not observed this time
                ScanSink.Record(@"D:\Music\Tool\a.flac", noBits[@"D:\Music\Tool\a.flac"]);
                var r9 = ScanSink.Flush(store, "md5:deadbeef0123", entries.Keys);
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                    assert(r8.Remeasured == 1 && r9.Remeasured == 1
                        && os.Db.ExecScalar("SELECT c.bitUsage_lowestNonZeroBit FROM track_content c JOIN track_location l ON l.entity_id = c.entity_id WHERE l.display_path = 'D:\\Music\\Tool\\a.flac';") == "9",
                        $"a remeasure keeps a stored value the arrival did not observe (bitUsage null on arrival): lowestNonZeroBit {os.Db.ExecScalar("SELECT c.bitUsage_lowestNonZeroBit FROM track_content c JOIN track_location l ON l.entity_id = c.entity_id WHERE l.display_path = 'D:\\Music\\Tool\\a.flac';") ?? "null"} (r8 {r8.Remeasured} remeasured, r9 {r9.Remeasured} remeasured)");
                // Idempotency after a remeasure: the identical entry again writes nothing.
                ScanSink.Record(@"D:\Music\Tool\a.flac", noBits[@"D:\Music\Tool\a.flac"]);
                var r10 = ScanSink.Flush(store, "md5:deadbeef0123", entries.Keys);
                assert(r10.Ok && r10.Unchanged == 1 && r10.Remeasured == 0 && r10.Drift == 0,
                    $"re-flushing the remeasured entry unchanged writes nothing ({r10.Unchanged} unchanged, {r10.Remeasured} remeasured, {r10.Drift} drift)");
                // Operator 2026-10-22 ("can we fix the accounting"): every entry a flush receives lands in EXACTLY ONE bucket, and the buckets
                // sum to the entries — the MB3 EV3 record named 1,399 of 7,655. The earlier reports of this block are checked the same way.
                foreach (var (name, rep) in new[] { ("r6", r6), ("r7", r7), ("r8", r8), ("r9", r9), ("r10", r10) })
                    assert(rep.Accounted == rep.Entries, $"{name}: the buckets sum to the entries ({rep.Accounted} of {rep.Entries}: {rep.Buckets})");
                // A fresh measurement from a DIFFERENT producer that AGREES within noise: today it wrote nothing, so a description a catalog
                // import seeded stayed stamped import@1 after the scan had measured it (MB3: 6,147 of 7,538). Now it is CONFIRMED — the fresh
                // values and the measuring run take the row, exactly as a remeasure does; no conflict row (agreement is not information).
                string ProducerOf(OpenStore os) => os.Db.ExecScalar("SELECT r.producer_version FROM track_content c JOIN run r ON r.id = c.run_id JOIN track_location l ON l.entity_id = c.entity_id WHERE l.display_path = 'D:\\Music\\Tool\\a.flac';") ?? "";
                string conflictsBefore;
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o"))) conflictsBefore = os.Db.ExecScalar("SELECT count(*) FROM conflict;") ?? "";
                var agree = FixtureEntries(@"D:\Music");
                agree[@"D:\Music\Tool\a.flac"].Features!.Bpm = noBits[@"D:\Music\Tool\a.flac"].Features!.Bpm * 1.0005;   // within noise of the stored value
                agree[@"D:\Music\Tool\a.flac"].Features!.BitUsage = null;
                ScanSink.Record(@"D:\Music\Tool\a.flac", agree[@"D:\Music\Tool\a.flac"]);
                var c1 = ScanSink.Flush(store, "md5:cafe0000cafe", entries.Keys);   // a build the store cannot order against md5:deadbeef0123
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                    assert(c1.Ok && c1.Confirmed == 1 && c1.Unchanged == 0 && c1.ContentReplaced == 0 && c1.Drift == 0 && c1.Accounted == c1.Entries
                           && ProducerOf(os) == "md5:cafe0000cafe"
                           && StoredText(os, @"D:\Music\Tool\a.flac", "bpm") == TrackEntryMapper.FormatContent(bpmCol, TrackEntryMapper.ContentValue(TrackEntryMapper.ToRow(agree[@"D:\Music\Tool\a.flac"]), bpmCol))
                           && os.Db.ExecScalar("SELECT c.bitUsage_lowestNonZeroBit FROM track_content c JOIN track_location l ON l.entity_id = c.entity_id WHERE l.display_path = 'D:\\Music\\Tool\\a.flac';") == "9"
                           && os.Db.ExecScalar("SELECT count(*) FROM conflict;") == conflictsBefore,
                        $"an agreeing measurement from another producer CONFIRMS the description: counted once, re-stamped to the measuring run (now {ProducerOf(os)}), fresh values stored, unobserved columns kept, no conflict row ({c1.Buckets})");
                ScanSink.Record(@"D:\Music\Tool\a.flac", agree[@"D:\Music\Tool\a.flac"]);
                var c2 = ScanSink.Flush(store, "md5:cafe0000cafe", entries.Keys);
                assert(c2.Ok && c2.Unchanged == 1 && c2.Confirmed == 0 && c2.CopyOnly == 0 && c2.Accounted == c2.Entries, $"the same arrival again is Unchanged ({c2.Buckets})");
                // Copy facts only (a tag touch moved the mtime; the description agrees): its own bucket, never 'unchanged' and never unnamed.
                var touched = FixtureEntries(@"D:\Music");
                touched[@"D:\Music\Tool\a.flac"].Features!.Bpm = agree[@"D:\Music\Tool\a.flac"].Features!.Bpm;
                touched[@"D:\Music\Tool\a.flac"].Features!.BitUsage = null;
                touched[@"D:\Music\Tool\a.flac"].LastModified = agree[@"D:\Music\Tool\a.flac"].LastModified.AddHours(1);
                ScanSink.Record(@"D:\Music\Tool\a.flac", touched[@"D:\Music\Tool\a.flac"]);
                var c3 = ScanSink.Flush(store, "md5:cafe0000cafe", entries.Keys);
                assert(c3.Ok && c3.CopyOnly == 1 && c3.Unchanged == 0 && c3.Confirmed == 0 && c3.Accounted == c3.Entries, $"a copy-facts-only write is its own bucket ({c3.Buckets})");
                // A first description (new media) is 'described', not merely 'entities created' (an existing media can gain its first description too).
                var fresh = FixtureEntries(@"D:\Music");
                var newEntry = fresh[@"D:\Music\Tool\a.flac"]; newEntry.AudioStreamSha256 = new string('7', 64);
                ScanSink.Record(@"D:\Music\Tool\brand-new.flac", newEntry);
                var c4 = ScanSink.Flush(store, "md5:cafe0000cafe", entries.Keys);
                assert(c4.Ok && c4.Described == 1 && c4.EntitiesCreated == 1 && c4.Unchanged == 0 && c4.Accounted == c4.Entries, $"a first description is 'described' ({c4.Buckets})");
                // Review finding 5: once the description is replaced, an OPEN drift row for that field is stale — it is closed.
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                    assert(os.Db.ExecScalar("SELECT count(*) FROM conflict WHERE kind = 'value-drift' AND state = 'open' AND field = 'bpm';") == "0",
                        $"a remeasure closes the stale open drift rows for the field it replaced ({os.Db.ExecScalar("SELECT count(*) FROM conflict WHERE kind = 'value-drift' AND state = 'open' AND field = 'bpm';")} still open)");
                // Review 2026-10-22 F1: an arrival that DRIFTS beyond noise on one column and FILLS a null on another (an older build here) is
                // one entry: counted once, as drift. The fill still lands (null -> value loses nothing); the stored description is kept.
                const string dfPath = @"D:\Music\Tool\drift-fill.flac";
                var df1 = FixtureEntries(@"D:\Music")[@"D:\Music\Tool\a.flac"]; df1.AudioStreamSha256 = new string('8', 64); df1.Features!.BitUsage = null;
                ScanSink.Record(dfPath, df1);
                ScanSink.Flush(store, "output-x64.5", entries.Keys);
                var df2 = FixtureEntries(@"D:\Music")[@"D:\Music\Tool\a.flac"]; df2.AudioStreamSha256 = new string('8', 64); df2.Features!.Bpm = df1.Features.Bpm * 1.5;
                ScanSink.Record(dfPath, df2);
                var c5 = ScanSink.Flush(store, "output-x64.3", entries.Keys);
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                    assert(c5.Ok && c5.Drift == 1 && c5.Filled == 0 && c5.Accounted == c5.Entries
                           && os.Db.ExecScalar("SELECT c.bitUsage_lowestNonZeroBit FROM track_content c JOIN track_location l ON l.entity_id = c.entity_id WHERE l.display_path = 'D:\\Music\\Tool\\drift-fill.flac';") == TrackEntryMapper.FormatContent(TrackEntryMapper.ContentIndexOf("bitUsage_lowestNonZeroBit"), TrackEntryMapper.ContentValue(TrackEntryMapper.ToRow(df2), TrackEntryMapper.ContentIndexOf("bitUsage_lowestNonZeroBit")))
                           && StoredText(os, dfPath, "bpm") == TrackEntryMapper.FormatContent(bpmCol, TrackEntryMapper.ContentValue(TrackEntryMapper.ToRow(df1), bpmCol)),
                        $"drift + fill is ONE entry, counted as drift; the null is filled and the stored bpm kept ({c5.Accounted} of {c5.Entries}: {c5.Buckets}; bitUsage {os.Db.ExecScalar("SELECT c.bitUsage_lowestNonZeroBit FROM track_content c JOIN track_location l ON l.entity_id = c.entity_id WHERE l.display_path = 'D:\\Music\\Tool\\drift-fill.flac';") ?? "null"}; bpm {StoredText(os, dfPath, "bpm")} vs first {TrackEntryMapper.FormatContent(bpmCol, TrackEntryMapper.ContentValue(TrackEntryMapper.ToRow(df1), bpmCol))})");

                // A store that cannot be opened: reported, never thrown.
                ScanSink.Record(@"D:\Music\Tool\a.flac", entries[@"D:\Music\Tool\a.flac"]);
                // A directory is not a database. One INSIDE the test dir, so the failure record written beside it is deleted with the test dir
                // (passing the test dir itself put store-result.json in the shared parent, where it stayed).
                var notADb = Path.Combine(dir, "not-a-db"); Directory.CreateDirectory(notADb);
                var bad = ScanSink.Flush(notADb, "output-x64.4");
                assert(!bad.Ok && bad.Error != null, $"an unopenable store is a reported failure, not an exception ({bad.Error})");
                ScanSink.Reset();
            }
            finally { ScanSink.Reset(); try { Directory.Delete(dir, true); } catch { } }
        }

        // ---------------------------------------------------------------- S2 C2: observation integrity
        static void TestDrift(Action<bool, string> assert)
        {
            var dir = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "drift-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var catDir = Path.Combine(dir, "lib"); Directory.CreateDirectory(catDir);
            var store = Path.Combine(dir, "truedat.db");
            try
            {
                var entries = FixtureEntries(@"D:\Music");
                var catalog = Path.Combine(catDir, "mbxmoods.json");
                WriteCatalog(catalog, entries);
                var r1 = ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = catalog, LibraryRoot = @"D:\Music" });
                string stamps = "", copyStamps = ""; long conflictsBefore = 0;
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                {
                    stamps = os.Db.ExecScalar("SELECT group_concat(updated_utc) FROM (SELECT updated_utc FROM track_content ORDER BY entity_id);") ?? "";
                    copyStamps = os.Db.ExecScalar("SELECT group_concat(updated_utc) FROM (SELECT updated_utc FROM track_copy ORDER BY location_id);") ?? "";
                    conflictsBefore = os.Dal.Count("conflict");
                }
                System.Threading.Thread.Sleep(20);   // so an unwanted rewrite would show as a different updated_utc

                // 1. The same catalog again: nothing is written.
                var r2 = ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = catalog, LibraryRoot = @"D:\Music" });
                assert(r2.Unchanged == 6 && r2.Drift == 0 && r2.ContentReplaced == 0, $"identical re-import: 6 unchanged, 0 drift, 0 replaced ({r2.Unchanged}/{r2.Drift}/{r2.ContentReplaced})");
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                {
                    assert(os.Db.ExecScalar("SELECT group_concat(updated_utc) FROM (SELECT updated_utc FROM track_content ORDER BY entity_id);") == stamps, "identical re-import leaves every description's updated_utc alone (no row churn)");
                    assert(os.Db.ExecScalar("SELECT group_concat(updated_utc) FROM (SELECT updated_utc FROM track_copy ORDER BY location_id);") == copyStamps, "identical re-import leaves every copy row's updated_utc alone");
                    assert(os.Dal.Count("conflict") == conflictsBefore, "identical re-import adds no conflict");
                }

                // 2. A changed catalog, same lastModified: one real content change, one noise-level change, one copy-fact change.
                var changed = FixtureEntries(@"D:\Music");
                var origBpmA = changed[@"D:\Music\Tool\a.flac"].Features!.Bpm;
                changed[@"D:\Music\Tool\a.flac"].Features!.Bpm = origBpmA * 1.25;          // beyond noise -> drift conflict, description kept
                changed[@"D:\Music\Tool\b.mp3"].Features!.Bpm = 120.4;                    // 0.33 % -> noise, nothing written
                changed[@"D:\Music\old\legacy.flac"].Features!.Artist = "Renamed";         // a copy fact -> newer wins, no conflict
                WriteCatalog(catalog, changed);
                var r3 = ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = catalog, LibraryRoot = @"D:\Music" });
                assert(r3.Drift == 1 && r3.Unchanged == 4 && r3.ContentReplaced == 0, $"changed catalog: 1 drift (a.flac bpm), 4 unchanged (b noise, nosha, d1, d2), 0 replaced ({r3.Drift}/{r3.Unchanged}/{r3.ContentReplaced})");
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                {
                    assert(os.Dal.Count("conflict") == conflictsBefore + 1 && os.Db.ExecScalar("SELECT count(*) FROM conflict WHERE kind = 'value-drift';") == "1", $"exactly one value-drift conflict ({os.Dal.Count("conflict") - conflictsBefore} new)");
                    assert(os.Db.ExecScalar("SELECT field FROM conflict WHERE kind = 'value-drift';") == "bpm"
                        && os.Db.ExecScalar("SELECT value_a FROM conflict WHERE kind = 'value-drift';") == Canon1(origBpmA)
                        && os.Db.ExecScalar("SELECT value_b FROM conflict WHERE kind = 'value-drift';") == Canon1(origBpmA * 1.25)
                        && os.Db.ExecScalar("SELECT count(*) FROM conflict WHERE kind = 'value-drift' AND run_a IS NOT NULL AND run_b IS NOT NULL AND run_a <> run_b;") == "1",
                        $"the drift conflict names the field, both values and both runs ({os.Db.ExecScalar("SELECT field || ' ' || value_a || ' ' || value_b || ' ' || run_a || '/' || run_b FROM conflict WHERE kind = 'value-drift';")})");
                    assert(StoredText(os, @"D:\Music\Tool\a.flac", "bpm") == Canon1(origBpmA),
                        $"the stored description is NOT overwritten by the drifted value ({StoredText(os, @"D:\Music\Tool\a.flac", "bpm")} vs {Canon1(origBpmA)})");
                    assert(StoredText(os, @"D:\Music\Tool\b.mp3", "bpm") == "120", $"a noise-level difference writes nothing (b.mp3 bpm stays 120: {StoredText(os, @"D:\Music\Tool\b.mp3", "bpm")})");
                    assert(os.Db.ExecScalar("SELECT p.artist FROM track_copy p JOIN track_location l ON l.id = p.location_id WHERE l.display_path = 'D:\\Music\\old\\legacy.flac';") == "Renamed", "a copy fact (artist) is updated: newer wins, no conflict");
                }

                // 3. The same drifted catalog again: the drift is still a drift, but the conflict is not recorded twice.
                var r4 = ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = catalog, LibraryRoot = @"D:\Music" });
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                    assert(r4.Drift == 1 && os.Db.ExecScalar("SELECT count(*) FROM conflict WHERE kind = 'value-drift';") == "1", $"re-importing the same drifted catalog records no duplicate conflict (drift {r4.Drift}, rows {os.Db.ExecScalar("SELECT count(*) FROM conflict WHERE kind = 'value-drift';")})");

                // 3b. Review #4: a stored NULL gaining a value under the same producer is a FILL, not drift; a value going to NULL is "not observed".
                var filled = FixtureEntries(@"D:\Music");
                filled[@"D:\Music\Tool\a.flac"].Features!.Bpm = origBpmA * 1.25;   // keep the known drift in place
                filled[@"D:\Music\Tool\b.mp3"].Features!.BitUsage = new BitUsageSummary { LowestNonZeroBit = 8, BottomBitActivity = 0.5, EffectiveBits = 24, SamplesAnalyzed = 1000, Method = "test" };
                WriteCatalog(catalog, filled);
                string runBeforeFill;
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                    runBeforeFill = os.Db.ExecScalar("SELECT c.run_id FROM track_content c JOIN track_location l ON l.entity_id = c.entity_id WHERE l.display_path = 'D:\\Music\\Tool\\b.mp3';") ?? "";
                var rf = ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = catalog, LibraryRoot = @"D:\Music" });
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                {
                    assert(rf.Filled == 1 && os.Db.ExecScalar("SELECT count(*) FROM conflict WHERE kind = 'value-drift';") == "1",
                        $"a null bitUsage gaining values is filled (1 filled), no new conflict ({rf.Filled} filled, conflicts {os.Db.ExecScalar("SELECT count(*) FROM conflict WHERE kind = 'value-drift';")})");
                    assert(os.Db.ExecScalar("SELECT c.bitUsage_lowestNonZeroBit FROM track_content c JOIN track_location l ON l.entity_id = c.entity_id WHERE l.display_path = 'D:\\Music\\Tool\\b.mp3';") == "8", "the filled value is stored");
                    // T01 (F2, I01): a fill never re-stamps the row — the retained columns keep the run that measured them.
                    assert(os.Db.ExecScalar("SELECT c.run_id FROM track_content c JOIN track_location l ON l.entity_id = c.entity_id WHERE l.display_path = 'D:\\Music\\Tool\\b.mp3';") == runBeforeFill,
                        $"a fill keeps the description's run stamp (was {runBeforeFill}, now {os.Db.ExecScalar("SELECT c.run_id FROM track_content c JOIN track_location l ON l.entity_id = c.entity_id WHERE l.display_path = 'D:\\Music\\Tool\\b.mp3';")})");
                }
                WriteCatalog(catalog, changed);   // back to bitUsage null for b.mp3
                var rn = ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = catalog, LibraryRoot = @"D:\Music" });
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                    assert(rn.Filled == 0 && os.Db.ExecScalar("SELECT c.bitUsage_lowestNonZeroBit FROM track_content c JOIN track_location l ON l.entity_id = c.entity_id WHERE l.display_path = 'D:\\Music\\Tool\\b.mp3';") == "8"
                        && os.Db.ExecScalar("SELECT count(*) FROM conflict WHERE kind = 'value-drift';") == "1",
                        $"a value arriving as null is 'not observed': kept, no conflict ({rn.Filled} filled, {rn.Drift} drift)");

                // 4. A key move (the media gains a new content hash) re-keys and rewrites the description — not drift.
                var moved = FixtureEntries(@"D:\Music");
                moved[@"D:\Music\old\legacy.flac"].AudioStreamSha256 = "2222222222222222222222222222222222222222222222222222222222222222";
                moved[@"D:\Music\old\legacy.flac"].AudioStreamSha256Source = "flac-frames";
                WriteCatalog(catalog, moved);
                var r5 = ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = catalog, LibraryRoot = @"D:\Music" });
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                    assert(r5.ContentRekeyed == 1 && os.Db.ExecScalar("SELECT c.content_key FROM track_content c JOIN track_location l ON l.entity_id = c.entity_id WHERE l.display_path = 'D:\\Music\\old\\legacy.flac';") == "2222222222222222222222222222222222222222222222222222222222222222",
                        $"a key move re-keys the description to the new hash without a drift conflict (rekeyed {r5.ContentRekeyed}, drift {r5.Drift})");

                // 5. ...but the reverse is not a key move: an OLDER catalog carrying the pre-2026-07-21 TagLib-region hash for the same FLAC
                // is the stale observation of a media the store already describes under its frame hash. It must not re-key the description
                // back to the legacy hash or put the old values over the new (codex review 2026-10-20, #2).
                var older = FixtureEntries(@"D:\Music");
                older[@"D:\Music\old\legacy.flac"].Features!.Bpm = 77;   // the old catalog's (different) measurement
                WriteCatalog(catalog, older);
                var r6 = ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = catalog, LibraryRoot = @"D:\Music" });
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                {
                    var key6 = os.Db.ExecScalar("SELECT c.content_key FROM track_content c JOIN track_location l ON l.entity_id = c.entity_id WHERE l.display_path = 'D:\\Music\\old\\legacy.flac';");
                    var bpm6 = os.Db.ExecScalar("SELECT c.bpm FROM track_content c JOIN track_location l ON l.entity_id = c.entity_id WHERE l.display_path = 'D:\\Music\\old\\legacy.flac';");
                    assert(r6.ContentRekeyed == 0 && key6 == "2222222222222222222222222222222222222222222222222222222222222222" && bpm6 != "770",
                        $"an older catalog's legacy FLAC hash does not re-key or regress the frame-keyed description (rekeyed {r6.ContentRekeyed}, key {key6}, bpm {bpm6})");
                }

                // 5b. The same arrival against a description a SCAN wrote (producer essentia) — how it arises in the field. Step 5's
                // description was an import's, so both sides were import@1 and the drift rule kept it for the wrong reason; with the
                // producers differing nothing could order the two and the older catalog REPLACED the scan's values (EV3 pre-tag
                // review, H2). A legacy-hash arrival is a known-older observation: the description and its run stand, exactly.
                const string sqlBpm = "SELECT c.bpm FROM track_content c JOIN track_location l ON l.entity_id = c.entity_id WHERE l.display_path = 'D:\\Music\\old\\legacy.flac';";
                const string sqlRun = "SELECT c.run_id FROM track_content c JOIN track_location l ON l.entity_id = c.entity_id WHERE l.display_path = 'D:\\Music\\old\\legacy.flac';";
                var scanned = FixtureEntries(@"D:\Music")[@"D:\Music\old\legacy.flac"];
                scanned.AudioStreamSha256 = "2222222222222222222222222222222222222222222222222222222222222222";
                scanned.AudioStreamSha256Source = "flac-frames";
                scanned.Features!.Bpm = 150;   // beyond the 1 % noise band from the stored 120, so the scan's write (and its producer) actually lands
                scanned.Features.Title = "Senjara"; scanned.Features.Genre = null!;   // the scan's copy facts: a title, and a genre it did not observe
                ImportCommand.ExecuteEntries(store, new Dictionary<string, TrackEntry>(PathComparer.Instance) { [@"D:\Music\old\legacy.flac"] = scanned },
                    @"D:\Music", "primary", true, "essentia", "output-x64.5", "scan", null, null);
                // Execution review D4: the alias branch kept the DESCRIPTION but the copy merge still ran as "newer wins", so the older
                // catalog's title and head hash replaced the scan's. A known-older arrival may only FILL a copy fact the store lacks.
                older[@"D:\Music\old\legacy.flac"].Features!.Title = "Senjara (old catalog)";
                older[@"D:\Music\old\legacy.flac"].Features!.Genre = "Jazz";
                const string sqlCopy = "SELECT p.title || '|' || coalesce(p.genre, '<null>') FROM track_copy p JOIN track_location l ON l.id = p.location_id WHERE l.display_path = 'D:\\Music\\old\\legacy.flac';";
                string bpmScan, runScan;
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o"))) { bpmScan = os.Db.ExecScalar(sqlBpm) ?? ""; runScan = os.Db.ExecScalar(sqlRun) ?? ""; }
                assert(bpmScan == "1500", $"fixture: the scan's description is in place (bpm {bpmScan}, expected 1500)");
                WriteCatalog(catalog, older);   // the old catalog again: legacy hash, bpm 77
                var r7 = ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = catalog, LibraryRoot = @"D:\Music" });
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                {
                    var bpm7 = os.Db.ExecScalar(sqlBpm); var run7 = os.Db.ExecScalar(sqlRun);
                    var key7 = os.Db.ExecScalar("SELECT c.content_key FROM track_content c JOIN track_location l ON l.entity_id = c.entity_id WHERE l.display_path = 'D:\\Music\\old\\legacy.flac';");
                    assert(r7.ContentReplaced == 0 && r7.ContentRekeyed == 0 && key7 == "2222222222222222222222222222222222222222222222222222222222222222" && bpm7 == "1500" && run7 == runScan,
                        $"an older catalog's legacy FLAC hash leaves a scan's description and its run untouched (replaced {r7.ContentReplaced}, rekeyed {r7.ContentRekeyed}, key {key7}, bpm {bpm7} expected 1500, run {run7} expected {runScan})");
                    var copy7 = os.Db.ExecScalar(sqlCopy);
                    assert(copy7 == "Senjara|Jazz",
                        $"an older catalog's legacy FLAC hash leaves the scan's copy facts in place and only fills what the store lacks (title|genre = {copy7}, expected Senjara|Jazz)");
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ---------------------------------------------------------------- S2 C0: store location
        // ---------------------------------------------------------------- --refresh before an import (keeper 083/084 / TDA 062/063, N1)
        // With no store yet, `--store --edges --refresh` printed nothing about edges and wrote 0 edge rows, exit 0: the lacking-edges
        // list comes from the store. The scan now says why and names the import that repairs THIS catalog/store pair, with explicit
        // quoted paths (a bare --store-import can resolve a different catalog); exit code, record and track selection are unchanged.
        static void TestEdgesRefreshNotice(Action<bool, string> assert)
        {
            const string Db = @"D:\My Lib\truedat.db", Cat = @"D:\My Lib\mbxmoods.json";
            const string Cmd = "truedat --store-import --moods \"D:\\My Lib\\mbxmoods.json\" --store \"D:\\My Lib\\truedat.db\"";
            var absent = Program.AbsentStoreEdgesNotice(refresh: true, storePath: Db, edges: true, storeExists: false, catalogPath: Cat);
            assert(absent != null && absent.Contains("no store at \"D:\\My Lib\\truedat.db\" yet") && absent.Contains(Cmd)
                   && absent.Contains("a track analysed for another reason still gets them"),
                $"refresh + store path + edges + no store file: quoted store, the import for THIS catalog/store, no promise about other analysis ({absent})");
            assert(Program.AbsentStoreEdgesNotice(false, Db, true, false, Cat) == null, "no --refresh: silent (not asking for edges backfill)");
            assert(Program.AbsentStoreEdgesNotice(true, null, true, false, Cat) == null, "no store in force: silent (TDA 062's condition)");
            assert(Program.AbsentStoreEdgesNotice(true, Db, false, false, Cat) == null, "no edges: silent");
            assert(Program.AbsentStoreEdgesNotice(true, Db, true, true, Cat) == null, "the store exists: no absent-store sentence (the count line speaks)");

            var dir = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "edgesnotice " + Guid.NewGuid().ToString("N").Substring(0, 8));   // a space, on purpose
            var catDir = Path.Combine(dir, "lib"); Directory.CreateDirectory(catDir);
            var store = Path.Combine(dir, "truedat.db");
            var catalog = Path.Combine(catDir, "mbxmoods.json");
            try
            {
                var all = FixtureEntries(@"D:\Music");
                string a = @"D:\Music\Tool\a.flac", b = @"D:\Music\Tool\b.mp3", l = @"D:\Music\old\legacy.flac", n = @"D:\Music\odd\nosha.mp3";
                string d1 = @"D:\Music\dup\d1.flac", d2 = @"D:\Music\dup\d2.flac";   // two catalog entries sharing ONE content hash
                var scanned = new[] { all[a], all[b], all[l], all[n], all[d1], all[d2] };
                HashSet<string> Held() { using var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")); return os.Dal.ContentKeysHeld(); }
                string? Notice() => Program.NotInStoreEdgesNotice(scanned, Held(), catalog, store);
                void ImportOnly(params string[] keys)
                {
                    var sub = new Dictionary<string, TrackEntry>(StringComparer.OrdinalIgnoreCase);
                    foreach (var k in keys) sub[k] = all[k];
                    WriteCatalog(catalog, sub);
                    ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = catalog, LibraryRoot = @"D:\Music" });
                }
                var cmd = $"truedat --store-import --moods \"{Path.GetFullPath(catalog)}\" --store \"{Path.GetFullPath(store)}\"";

                // 1 held, 2 absent among the three single-hash entries; d1/d2 share an ABSENT hash and count as two ENTRIES; nosha never.
                ImportOnly(a);
                var p1 = Notice();
                assert(p1 != null && p1.Contains("4 catalog entries have a content hash the store does not hold") && p1.Contains(cmd),
                    $"a held; b, legacy, d1, d2 absent -> 4 entries (per entry, not the 3 unique hashes); nosha outside the count; the command names this catalog and store ({p1})");

                // The shared hash HELD (via d1 only): d2 shares it and does not count. a's content is held with NO edge rows (an import
                // writes none) — held-without-edges is the lacking-edges list's business, not this count's.
                ImportOnly(a, d1);
                var p2 = Notice();
                assert(p2 != null && p2.Contains("2 catalog entries have a content hash") ,
                    $"a + d1 held: b and legacy absent -> 2; d2 shares d1's held hash and does not inflate it; held content without edges is not counted ({p2})");

                // A failed edge fold recorded on held content changes nothing here either.
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                {
                    var ent = os.Db.ExecScalar($"SELECT entity_id FROM track_location WHERE display_path = '{a}';");
                    var edgesVersion = new Truedat.Edges.EdgeSettings().ProducerVersion;   // the CURRENT version: a failed fold at it is what parks a track
                    os.Dal.EnsureProducer(Truedat.Edges.EdgeStore.ProducerKey, edgesVersion, "analysis", needsAudio: true);   // as ImportCommand.cs:520 does for a failed fold
                    os.Dal.InsertRun(Truedat.Edges.EdgeStore.ProducerKey, edgesVersion, ent, null, null, null, DateTime.UtcNow.ToString("o"), null, null, null, "failed", "fold", null, "selftest", null);
                }
                var p3 = Notice();
                assert(p3 == p2, $"a failed fold on held content does not change the count ({p3})");

                // One absent: the singular wording.
                ImportOnly(a, d1, b);
                var one = Notice();
                assert(one != null && one.Contains("1 catalog entry has a content hash the store does not hold") && one.Contains(cmd), $"legacy alone absent: 1 entry, singular ({one})");

                // The positive control: everything imported -> nothing to say; no store read -> nothing to say.
                ImportOnly(a, b, l, n, d1, d2);
                assert(Notice() == null, "every hashed entry held: no sentence");
                assert(Program.NotInStoreEdgesNotice(scanned, null, catalog, store) == null, "no store read (refresh / store / edges off): no sentence");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ---------------------------------------------------------------- copy facts (keeper 067/068, TDA 052/053)
        // An ordinary --store-import of a catalog without trackId (the export cut) wrote 0 over every stored copy's trackId, and
        // a 1-dp catalog analysisDuration replaced the store's 6-dp value whenever the copy row was rewritten for any reason.
        // A copy fact the arriving catalog does not carry (absent, or JSON null) is not observed and keeps the stored value; an
        // explicit value (zero, empty, nonzero) is an observation and is written; a value equal to the stored one as the writer
        // prints it (FieldPrecision Dp) is the same observation.
        // T13 (gate d): a non-primary copy of a multi-copy media exports its media's ONE description, so only the shared CONTENT columns
        // may differ from its catalog entry. Its copy facts (tags, trackId, fingerprint, SMFM, analysisDuration, fileMd5) and its location
        // (lastModified) are stored per copy and must round-trip exactly — the gate used to excuse ANY difference on such a copy.
        // MB3 (2026-10-18): a catalog written by an older build spells some values one ulp off their decimal; the store holds each field
        // at its declared precision and gives the decimal back. The gate reported those 47 entries as NOT EQUAL — the store holds the
        // catalog, only the spelling differs — while a value one step beyond the precision must still fail it.
        static void TestRoundtripPrecision(Action<bool, string> assert)
        {
            var dir = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "rt-precision-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var store = Path.Combine(dir, "truedat.db");
            const string A = @"D:\Music\a\one.flac";
            TrackEntry Entry(double rms) => new TrackEntry
            {
                AudioStreamSha256 = new string('e', 64), LastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                Features = new TrackFeatures { Artist = "A", Title = "T", Bpm = 120.0, SpectralRms = rms, Mfcc = new double[13] },
            };
            try
            {
                var catalog = Path.Combine(dir, "mbxmoods.json");
                var oneUlp = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(0.002877) + 1);
                WriteCatalog(catalog, new Dictionary<string, TrackEntry>(PathComparer.Instance) { [A] = Entry(oneUlp) });
                ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = catalog, LibraryRoot = @"D:\Music" });
                var rt = ExportCommand.Roundtrip(store, catalog, null);
                assert(rt.SamePrecision == 1 && rt.Different == 0 && rt.Equal,
                    $"a value spelled one ulp off its decimal round-trips EQUAL at its declared precision ({rt.Same} same / {rt.SamePrecision} at precision / {rt.Different} different, equal {rt.Equal})");

                var beyond = Path.Combine(dir, "beyond.json");
                WriteCatalog(beyond, new Dictionary<string, TrackEntry>(PathComparer.Instance) { [A] = Entry(0.002878) });
                var rt2 = ExportCommand.Roundtrip(store, beyond, null);
                assert(rt2.Different == 1 && rt2.SamePrecision == 0 && !rt2.Equal && rt2.FirstDifferentPath == A,
                    $"a value one step beyond the declared precision still fails the gate ({rt2.SamePrecision} at precision / {rt2.Different} different, equal {rt2.Equal})");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        static void TestRoundtripCopyFacts(Action<bool, string> assert)
        {
            var dir = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "rt-copyfacts-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var store = Path.Combine(dir, "truedat.db");
            const string A = @"D:\Music\a\one.flac", B = @"D:\Music\b\one copy.flac";
            var lm = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
            Dictionary<string, TrackEntry> Entries(string bArtist, DateTime bLm) => new Dictionary<string, TrackEntry>(PathComparer.Instance)
            {
                [A] = new TrackEntry { AudioStreamSha256 = new string('c', 64), AnalysisDurationSecs = 1.5, LastModified = lm, Features = new TrackFeatures { Artist = "Tool", Title = "Sober", Bpm = 120.0, Mfcc = new double[13] } },
                [B] = new TrackEntry { AudioStreamSha256 = new string('c', 64), AnalysisDurationSecs = 2.5, LastModified = bLm, Features = new TrackFeatures { Artist = bArtist, Title = "Sober", Bpm = 135.0, Mfcc = new double[13] } },
            };
            try
            {
                var catalog = Path.Combine(dir, "mbxmoods.json");
                WriteCatalog(catalog, Entries("TOOL", lm));
                ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = catalog, LibraryRoot = @"D:\Music" });

                var rt = ExportCommand.Roundtrip(store, catalog, null);
                assert(rt.Same == 1 && rt.DifferentMerged == 1 && rt.Different == 0 && rt.Equal,
                    $"a merged copy differing ONLY in the shared description (bpm 135 vs the media's 120) is the expected merge: EQUAL ({rt.Same} same / {rt.DifferentMerged} merged / {rt.Different} different)");

                var changedTag = Path.Combine(dir, "changed-tag.json");
                WriteCatalog(changedTag, Entries("Tool (remaster)", lm));
                var rt2 = ExportCommand.Roundtrip(store, changedTag, null);
                assert(rt2.Different == 1 && rt2.DifferentMerged == 0 && !rt2.Equal && rt2.FirstDifferentPath == B,
                    $"a merged copy whose COPY FACT (artist) differs from the store fails the gate — tags are stored per copy ({rt2.DifferentMerged} merged / {rt2.Different} different, equal {rt2.Equal}, first {rt2.FirstDifferentPath})");

                var changedLm = Path.Combine(dir, "changed-lm.json");
                WriteCatalog(changedLm, Entries("TOOL", lm.AddDays(1)));
                var rt3 = ExportCommand.Roundtrip(store, changedLm, null);
                assert(rt3.Different == 1 && rt3.DifferentMerged == 0 && !rt3.Equal,
                    $"a merged copy whose location (lastModified) differs fails the gate too ({rt3.DifferentMerged} merged / {rt3.Different} different, equal {rt3.Equal})");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // Keeper 099: the per-file scan modes (--analyze-file, --file-list, --folder) have no iTunes XML, so their entries carry the
        // default trackId 0 — a value nobody observed. Through the REAL hand-off (Program.RecordPerFileScanEntry -> ScanSink -> the
        // store) that 0 must not replace the XML id an iTunes-XML scan stored, while every copy fact the per-file scan DID observe
        // (its tags) still lands; and an XML scan that observes a different id replaces it (the explicit replacement, proven apart).
        static void TestPerFileIdentity(Action<bool, string> assert)
        {
            var dir = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "perfile-id-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var store = Path.Combine(dir, "truedat.db");
            const string P = @"D:\Music\Tool\a.flac";
            try
            {
                (long Id, string? Title) Stored()
                {
                    using var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o"));
                    using var st = os.Db.Prepare("SELECT tc.trackId, tc.title FROM track_copy tc JOIN track_location l ON l.id = tc.location_id WHERE l.display_path = ?;");
                    st.Bind(1, P);
                    return st.Step() ? (st.IsNull(0) ? -1 : st.GetInt64(0), st.IsNull(1) ? null : st.GetString(1)) : (-2, null);
                }
                void Flush() { var r = ScanSink.Flush(store, "output-x64.5", new[] { P }); assert(r.Ok, $"the sink flushed ({r.Error ?? "ok"})"); }

                ScanSink.Reset();
                var xml = FixtureEntries(@"D:\Music")[P];
                xml.Features!.TrackId = 4242; xml.Features.Title = "XML title";
                ScanSink.Record(P, xml);                      // the iTunes-XML scan's own hand-off: every copy fact observed
                Flush();
                assert(Stored().Id == 4242, $"setup: an iTunes-XML scan stores the XML id ({Stored().Id})");

                var perFile = FixtureEntries(@"D:\Music")[P];
                perFile.Features!.TrackId = 0; perFile.Features.Title = "tag title";
                perFile.LastModified = xml.LastModified.AddMinutes(5);
                Program.RecordPerFileScanEntry(P, perFile, null);
                Flush();
                var after = Stored();
                assert(after.Id == 4242, $"a per-file scan's unobserved trackId 0 does not replace the stored XML id (stored {after.Id})");
                assert(after.Title == "tag title", $"…while the copy facts the per-file scan DID observe still land: its tag title replaced the XML one ('{after.Title}')");

                var xml2 = FixtureEntries(@"D:\Music")[P];
                xml2.Features!.TrackId = 5151; xml2.Features.Title = "XML title";
                xml2.LastModified = perFile.LastModified.AddMinutes(5);
                ScanSink.Record(P, xml2);
                Flush();
                assert(Stored().Id == 5151, $"an iTunes-XML scan that observes a different id replaces the stored one (explicit replacement; stored {Stored().Id})");
            }
            finally { ScanSink.Reset(); StoreResult.ResetScanForTest(); try { Directory.Delete(dir, true); } catch { } }
        }

        static void TestCopyMerge(Action<bool, string> assert)
        {
            var dir = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "copymerge-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var catDir = Path.Combine(dir, "lib"); Directory.CreateDirectory(catDir);
            var store = Path.Combine(dir, "truedat.db");
            var catalog = Path.Combine(catDir, "mbxmoods.json");
            const string P = @"D:\Music\old\legacy.flac";
            string Where = $"FROM track_copy p JOIN track_location l ON l.id = p.location_id WHERE l.display_path = '{P}'";
            string? Copy(string col) { using var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")); return os.Db.ExecScalar($"SELECT p.{col} {Where};"); }
            string? IsNull(string col) { using var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")); return os.Db.ExecScalar($"SELECT p.{col} IS NULL {Where};"); }
            void Import() => ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = catalog, LibraryRoot = @"D:\Music" });
            // The writer's catalog (it honours the export cut: no trackId; omit-when-0 bitDepth), then an optional edit of P's entry.
            void Write(Dictionary<string, TrackEntry> entries, Action<System.Text.Json.Nodes.JsonObject>? edit = null)
            {
                WriteCatalog(catalog, entries);
                if (edit == null) return;
                var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(catalog))!;
                edit(root["tracks"]![P]!.AsObject());
                File.WriteAllText(catalog, root.ToJsonString());
            }
            System.Text.Json.Nodes.JsonObject Fp(System.Text.Json.Nodes.JsonObject t) => t["fingerprint.v1"]!.AsObject();
            assert(TrackEntryMapper.CopyColumnNames.Count <= 64, $"the copy columns fit TrackEntry.CopyObserved's 64-bit mask ({TrackEntryMapper.CopyColumnNames.Count})");
            try
            {
                var entries = FixtureEntries(@"D:\Music");
                entries[P].AnalysisDurationSecs = 8.5;
                // A catalog that DOES carry every copy fact (a legacy v1 catalog has trackId; fileMd5 under --file-md5; SMFM when read).
                Write(entries, t =>
                {
                    t["trackId"] = 7021; t["fileMd5"] = "0123456789abcdef0123456789abcdef";
                    t["smfmScores"] = new System.Text.Json.Nodes.JsonArray(1, 2, 3, 4, 5, 6, 7, 8, 9, 10); t["smfmChannel"] = 9;
                    Fp(t)["bitDepth"] = 24; Fp(t)["encoder"] = "LAME3.100";
                });
                Import();
                {
                    using var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o"));
                    var other = os.Db.ExecScalar("SELECT (p.trackId IS NULL) || '/' || (p.fileMd5 IS NULL) FROM track_copy p JOIN track_location l ON l.id = p.location_id WHERE l.display_path = '" + @"D:\Music\Tool\b.mp3" + "';");
                    assert(other == "1/1", $"a NEW copy row stores NULL for a fact the catalog did not carry, never its default 0 (trackId/fileMd5 null: {other})");
                }
                assert(Copy("trackId") == "7021" && Copy("fileMd5") == "0123456789abcdef0123456789abcdef" && Copy("smfmChannel") == "9"
                       && Copy("fingerprint_v1_bitDepth") == "24" && Copy("fingerprint_v1_encoder") == "LAME3.100",
                    $"explicit copy facts are imported (trackId {Copy("trackId")}, fileMd5 {Copy("fileMd5")}, smfmChannel {Copy("smfmChannel")}, bitDepth {Copy("fingerprint_v1_bitDepth")}, encoder {Copy("fingerprint_v1_encoder")})");

                // What a scan stores: the duration at the store's 6 places (FieldPrecision StoreDp); the catalog says 8.5, its 1-dp text.
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                    os.Db.Exec($"UPDATE track_copy SET analysisDuration = 8543210 WHERE location_id = (SELECT l.id FROM track_location l WHERE l.display_path = '{P}');");
                var stamp = Copy("updated_utc");
                System.Threading.Thread.Sleep(20);

                // 1. The writer's own catalog: no trackId / fileMd5 / SMFM, a partial fingerprint (no bitDepth, no encoder), the agreeing
                //    1-dp duration. Nothing it lacks is erased and nothing is written — twice, so the repeat import is pinned too.
                for (int pass = 1; pass <= 2; pass++)
                {
                    Write(entries);
                    Import();
                    assert(Copy("trackId") == "7021", $"pass {pass}: a catalog that does not carry trackId leaves the stored one alone: absent is not zero (got {Copy("trackId")})");
                    assert(Copy("fileMd5") == "0123456789abcdef0123456789abcdef" && Copy("smfmChannel") == "9",
                        $"pass {pass}: omitted nullable copy facts keep their stored values (fileMd5 {Copy("fileMd5")}, smfmChannel {Copy("smfmChannel")})");
                    assert(Copy("fingerprint_v1_bitDepth") == "24" && Copy("fingerprint_v1_encoder") == "LAME3.100",
                        $"pass {pass}: fields a partial fingerprint omits keep their stored values (bitDepth {Copy("fingerprint_v1_bitDepth")}, encoder {Copy("fingerprint_v1_encoder")})");
                    assert(Copy("analysisDuration") == "8543210", $"pass {pass}: a 1-dp duration equal to Round(stored, 1) is the same observation: the 6-dp value stays (got {Copy("analysisDuration")})");
                    assert(Copy("updated_utc") == stamp, $"pass {pass}: an arrival that differs only by what it did not observe, or by its written precision, writes no copy row");
                }

                // 2. JSON null is not an observation either; a whole missing fingerprint keeps every stored fingerprint column.
                Write(entries, t => { t["trackId"] = null; t.Remove("fingerprint.v1"); });
                Import();
                assert(Copy("trackId") == "7021" && Copy("fingerprint_v1_fileSize") == "1234" && Copy("fingerprint_v1_codec") == "flac" && Copy("fingerprint_v1_bitDepth") == "24",
                    $"trackId: null and an absent fingerprint.v1 change nothing (trackId {Copy("trackId")}, fileSize {Copy("fingerprint_v1_fileSize")}, codec {Copy("fingerprint_v1_codec")}, bitDepth {Copy("fingerprint_v1_bitDepth")})");
                assert(Copy("updated_utc") == stamp, "nothing observed changed: no copy row write");
                // A fingerprint block the reader refuses (no audioHead64kMd5) yields no values: nothing in it is observed.
                Write(entries, t => { Fp(t).Remove("audioHead64kMd5"); Fp(t)["bitDepth"] = 0; Fp(t)["fileSize"] = 1; });
                Import();
                assert(Copy("fingerprint_v1_fileSize") == "1234" && Copy("fingerprint_v1_bitDepth") == "24" && Copy("updated_utc") == stamp,
                    $"a refused fingerprint block changes nothing (fileSize {Copy("fingerprint_v1_fileSize")}, bitDepth {Copy("fingerprint_v1_bitDepth")})");

                // 3. A real copy-fact change rewrites the row; the merge keeps what the arrival did not observe or only rounded.
                entries[P].Features!.Artist = "Renamed2";
                Write(entries);
                Import();
                assert(Copy("artist") == "Renamed2", $"the changed copy fact is written ({Copy("artist")})");
                assert(Copy("trackId") == "7021" && Copy("analysisDuration") == "8543210" && Copy("fileMd5") == "0123456789abcdef0123456789abcdef" && Copy("fingerprint_v1_bitDepth") == "24",
                    $"a rewritten copy row keeps every unobserved and agreeing value (trackId {Copy("trackId")}, analysisDuration {Copy("analysisDuration")}, fileMd5 {Copy("fileMd5")}, bitDepth {Copy("fingerprint_v1_bitDepth")})");

                // 3b. Keeper 070 — the pair to pass 1: the written-precision equivalence is for CATALOG reads only. Through the catalog
                //     the agreeing 1-dp / 3-dp values keep the stored 6-dp ones; through the scan's own write path (ScanSink, producer
                //     essentia — the shared WriteDescriptionAndCopy) the same numbers are fresh measurements and replace exactly.
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                    os.Db.Exec($"UPDATE track_copy SET smfmBpm = 123456789 WHERE location_id = (SELECT l.id FROM track_location l WHERE l.display_path = '{P}');");
                var stamp3 = Copy("updated_utc");
                System.Threading.Thread.Sleep(20);
                Write(entries, t => t["smfmBpm"] = 123.457);
                Import();
                assert(Copy("smfmBpm") == "123456789" && Copy("analysisDuration") == "8543210" && Copy("updated_utc") == stamp3,
                    $"catalog read: smfmBpm 123.457 == Round(123.456789, 3) and duration 8.5 == Round(8.54321, 1) keep the stored values, no write (smfmBpm {Copy("smfmBpm")}, analysisDuration {Copy("analysisDuration")})");
                var runBeforeScan = Copy("run_id");
                var fresh = FixtureEntries(@"D:\Music")[P];
                fresh.Features!.Artist = "Renamed2"; fresh.AnalysisDurationSecs = 8.5; fresh.Features.SmfmBpm = 123.457;
                ScanSink.Reset();
                ScanSink.Record(P, fresh);
                ScanSink.Flush(store, "output-x64.4", new[] { P });
                assert(Copy("analysisDuration") == "8500000" && Copy("smfmBpm") == "123457000",
                    $"fresh scan: 8.5 and 123.457 are new measurements and replace the stored 8.54321 / 123.456789 exactly (analysisDuration {Copy("analysisDuration")}, smfmBpm {Copy("smfmBpm")})");
                {
                    using var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o"));
                    var producer = os.Db.ExecScalar($"SELECT r.producer_key || '@' || r.producer_version FROM run r WHERE r.id = (SELECT p.run_id {Where});");
                    assert(Copy("run_id") != runBeforeScan && producer == "essentia@output-x64.4",
                        $"the scan's copy row carries the scan's own run (run {runBeforeScan} -> {Copy("run_id")}, {producer})");
                }
                // Restore what the catalog cases below start from (a scan's own entry observes every column: its trackId 0 is written).
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                    os.Db.Exec($"UPDATE track_copy SET analysisDuration = 8543210, trackId = 7021 WHERE location_id = (SELECT l.id FROM track_location l WHERE l.display_path = '{P}');");

                // 3c. Keeper 073 / TDA 057 — the mask must select the SAME key the reader reads. One rule, shared by both (Program.
                //     SelectSmfmKey): the modern smfm* key when present and not JSON null, else the legacy sensme* key. 5524bcf's reader
                //     stopped at a modern null while its mask went on to the legacy array, so the merge wrote the reader's null over the
                //     stored scores. Controls: modern null + legacy array, null alone, the legacy key alone, the modern key alone, [].
                string Hex(params int[] v) => string.Concat(v.Select(x => BitConverter.ToString(BitConverter.GetBytes(x)).Replace("-", "")));   // int32 LE, as stored
                int[] Ten(int first, int step) => Enumerable.Range(0, 10).Select(i => first + i * step).ToArray();
                string? CopyExpr(string expr) { using var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")); return os.Db.ExecScalar($"SELECT {expr} {Where};"); }
                Write(entries, t => t["smfmScores"] = new System.Text.Json.Nodes.JsonArray(1, 2, 3, 4, 5, 6, 7, 8, 9, 10));   // seed: 3b's scan cleared them
                Import();
                var scores0 = CopyExpr("hex(p.smfmScores)");
                assert(scores0 == Hex(Ten(1, 1)), $"seed: an explicit smfmScores array is stored exactly ({scores0})");
                Write(entries, t => { t["smfmScores"] = null; t["sensmeScores"] = new System.Text.Json.Nodes.JsonArray(10, 9, 8, 7, 6, 5, 4, 3, 2, 1); });
                Import();
                var scoresMixed = CopyExpr("hex(p.smfmScores)");
                assert(scoresMixed == Hex(Ten(10, -1)),
                    $"smfmScores: null beside a legacy sensmeScores array: the reader and the mask both select the legacy array, which is written, never a cleared vector ({scoresMixed} vs {scores0})");
                Write(entries, t => t["smfmScores"] = null);
                Import();
                assert(CopyExpr("hex(p.smfmScores)") == Hex(Ten(10, -1)), $"smfmScores: null alone keeps the stored vector exactly ({CopyExpr("hex(p.smfmScores)")})");
                Write(entries, t => t["sensmeScores"] = new System.Text.Json.Nodes.JsonArray(2, 2, 2, 2, 2, 2, 2, 2, 2, 2));
                Import();
                var scoresLegacy = CopyExpr("hex(p.smfmScores)");
                assert(scoresLegacy == Hex(Ten(2, 0)), $"the legacy sensmeScores key alone is read, so it is observed and written exactly ({scoresLegacy})");
                Write(entries, t => t["smfmScores"] = new System.Text.Json.Nodes.JsonArray(3, 3, 3, 3, 3, 3, 3, 3, 3, 3));
                Import();
                var scoresModern = CopyExpr("hex(p.smfmScores)");
                assert(scoresModern == Hex(Ten(3, 0)), $"the modern smfmScores key alone is read and written exactly ({scoresModern})");
                Write(entries, t => t["smfmScores"] = new System.Text.Json.Nodes.JsonArray());
                Import();
                assert(CopyExpr("p.smfmScores IS NOT NULL AND length(p.smfmScores) = 0") == "1",
                    $"an explicit empty smfmScores array is an observation and is written as an EMPTY blob, never NULL (null {CopyExpr("p.smfmScores IS NULL")}, length {CopyExpr("length(p.smfmScores)")}, type {CopyExpr("typeof(p.smfmScores)")})");

                // The other three SMFM pairs under the same rule: a modern null falls through to the legacy value, which is written.
                Write(entries, t => { t["smfmBpm"] = null; t["sensmeBpm"] = 99.5; t["smfmChannelName"] = null; t["sensmeChannelName"] = "legacy"; t["smfmChannel"] = null; t["sensmeChannel"] = 7; });
                Import();
                assert(Copy("smfmBpm") == "99500000" && Copy("smfmChannelName") == "legacy" && Copy("smfmChannel") == "7",
                    $"smfmBpm / smfmChannelName / smfmChannel: a modern null beside a legacy value reads and writes the legacy value (bpm {Copy("smfmBpm")}, name {Copy("smfmChannelName")}, channel {Copy("smfmChannel")})");
                Write(entries, t => { t["smfmBpm"] = 88.25; t["sensmeBpm"] = 11.5; t["smfmChannelName"] = "modern"; t["sensmeChannelName"] = "legacy2"; t["smfmChannel"] = 4; t["sensmeChannel"] = 8; });
                Import();
                assert(Copy("smfmBpm") == "88250000" && Copy("smfmChannelName") == "modern" && Copy("smfmChannel") == "4",
                    $"smfmBpm / smfmChannelName / smfmChannel: a valid modern value beats the legacy one (bpm {Copy("smfmBpm")}, name {Copy("smfmChannelName")}, channel {Copy("smfmChannel")})");

                // Keeper 078 / TDA 088 — the name pair. The reader has always treated an EMPTY modern smfmChannelName as "no value here"
                // and fallen through to the legacy name; 027d43a's shared selector picked the empty modern key instead (read as none,
                // counted observed), so an import could clear a stored name. The rule now treats "" like null, in the reader and the mask.
                string? NameRun() => CopyExpr("p.smfmChannelName || '@' || p.run_id");
                Write(entries, t => t["smfmChannelName"] = "seeded");
                Import();
                var runSeeded = Copy("run_id");
                assert(Copy("smfmChannelName") == "seeded", $"name seed stored ({Copy("smfmChannelName")})");
                Write(entries, t => { t["smfmChannelName"] = ""; t["sensmeChannelName"] = "legacyName"; });
                Import();
                assert(Copy("smfmChannelName") == "legacyName" && Copy("run_id") != runSeeded,
                    $"an empty modern smfmChannelName beside a legacy name reads the legacy name (as the reader always has) and writes it under the import's run ({NameRun()}, seeded run {runSeeded})");
                var runLegacyName = Copy("run_id");
                Write(entries, t => t["smfmChannelName"] = "");
                Import();
                assert(Copy("smfmChannelName") == "legacyName" && Copy("run_id") == runLegacyName,
                    $"an empty modern smfmChannelName alone carries no name: nothing observed, the stored name and its run stay ({NameRun()})");
                Write(entries, t => { t["smfmChannelName"] = ""; t["sensmeChannelName"] = ""; });
                Import();
                assert(Copy("smfmChannelName") == "legacyName" && Copy("run_id") == runLegacyName,
                    $"both names empty: nothing observed, the stored name and its run stay ({NameRun()})");
                Write(entries);
                Import();
                assert(Copy("smfmChannelName") == "legacyName" && Copy("run_id") == runLegacyName,
                    $"both name keys missing: nothing observed, the stored name and its run stay ({NameRun()})");
                Write(entries, t => { t["smfmChannelName"] = "modernName"; t["sensmeChannelName"] = "legacyName2"; });
                Import();
                assert(Copy("smfmChannelName") == "modernName", $"a non-empty modern name beats the legacy one ({NameRun()})");

                // 4. Explicit values are observations and replace: zero, empty, a disagreeing duration — no blanket ignore-zero rule.
                entries[P].AnalysisDurationSecs = 9.1;
                Write(entries, t => { t["trackId"] = 0; t["artist"] = ""; t["fileMd5"] = ""; Fp(t)["bitDepth"] = 0; Fp(t)["encoder"] = ""; t["sensmeChannel"] = 5; });
                Import();
                assert(Copy("trackId") == "0", $"an explicit trackId 0 is written (got {Copy("trackId")})");
                assert(Copy("fingerprint_v1_bitDepth") == "0", $"an explicit fingerprint bitDepth 0 is written (got {Copy("fingerprint_v1_bitDepth")})");
                assert(Copy("artist") == "", $"an explicit empty artist is written (got '{Copy("artist")}', null {IsNull("artist")})");
                assert(IsNull("fileMd5") == "1", $"an explicit empty fileMd5 clears it (the reader maps empty to none) (got '{Copy("fileMd5")}')");
                assert(Copy("analysisDuration") == "9100000", $"a duration that disagrees at the written precision replaces (got {Copy("analysisDuration")})");
                assert(Copy("smfmChannel") == "5", $"an SMFM fact under its legacy sensme* key is observed, as the reader's fallback reads it (got {Copy("smfmChannel")})");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        static void TestStoreLocation(Action<bool, string> assert)
        {
            var tmp = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "loc-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);
            try
            {
                var localCatalog = Path.Combine(tmp, "Library", "mbxmoods.json");
                var uncCatalog = @"\\server\share\Library\mbxmoods.json";
                var localApp = Path.Combine(tmp, "LocalAppData");

                // library id: derived, 16 lowercase hex, case- and trailing-separator-insensitive
                var id = StoreLocation.LibraryId(Path.GetDirectoryName(uncCatalog)!);
                assert(System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9a-f]{16}$"), $"library id is 16 lowercase hex ({id})");
                assert(id == StoreLocation.LibraryId(@"\\SERVER\Share\LIBRARY\"), "library id ignores case and a trailing separator");
                assert(id != StoreLocation.LibraryId(@"\\server\other\Library"), "a different share gives a different library id");

                // explicit wins, network or not
                var explicitPath = Path.Combine(tmp, "elsewhere", "mine.db");
                var r1 = StoreLocation.Resolve(explicitPath, null, uncCatalog, out var why1, localApp);
                assert(r1 == Path.GetFullPath(explicitPath) && why1.Contains("explicit"), $"explicit --store wins ({r1}; {why1})");

                // local catalog folder -> beside the catalog
                var r2 = StoreLocation.Resolve(null, null, localCatalog, out var why2, localApp);
                assert(r2 == Path.Combine(tmp, "Library", StoreLocation.FileName) && why2.Contains("beside"), $"local catalog -> truedat.db beside it ({r2}; {why2})");

                // network catalog folder -> local per-library path, never the share
                var r3 = StoreLocation.Resolve(null, null, uncCatalog, out var why3, localApp);
                assert(r3 == Path.Combine(localApp, "truedat", id, StoreLocation.FileName) && why3.Contains("network"), $"network catalog -> %LOCALAPPDATA%\\truedat\\<id>\\truedat.db ({r3}; {why3})");
                assert(!r3.StartsWith(@"\\"), "the default never resolves onto a share");

                // --store-dir relocates the per-library layout, for a local catalog too
                var storeDir = Path.Combine(tmp, "stores");
                var r4 = StoreLocation.Resolve(null, storeDir, localCatalog, out var why4, localApp);
                var localId = StoreLocation.LibraryId(Path.Combine(tmp, "Library"));
                assert(r4 == Path.Combine(storeDir, localId, StoreLocation.FileName) && why4.Contains("store-dir"), $"--store-dir -> <dir>\\<id>\\truedat.db ({r4}; {why4})");

                // network detection: UNC is network; a temp path on a fixed drive is not
                assert(StoreLocation.IsNetworkPath(@"\\server\share\x\y.db"), "a UNC path is a network path");
                assert(!StoreLocation.IsNetworkPath(tmp), "a local temp path is not a network path");

                // the open: exclusive locking on request (what a network store gets), normal otherwise
                var dbPath = Path.Combine(tmp, "excl.db");
                using (var db = SqliteDb.Open(dbPath, create: true, exclusiveLocking: true))
                    assert(string.Equals(db.ExecScalar("PRAGMA locking_mode;"), "exclusive", StringComparison.OrdinalIgnoreCase), "exclusiveLocking: true opens with locking_mode=exclusive");
                var dbPath2 = Path.Combine(tmp, "normal.db");
                using (var db = SqliteDb.Open(dbPath2, create: true))
                {
                    assert(string.Equals(db.ExecScalar("PRAGMA locking_mode;"), "normal", StringComparison.OrdinalIgnoreCase), "default open keeps locking_mode=normal");
                    db.Exec("CREATE TABLE t(x);"); db.Exec("INSERT INTO t VALUES (1);");
                }
                assert(!File.Exists(dbPath2 + "-wal") || new FileInfo(dbPath2 + "-wal").Length == 0, "closing a store leaves no WAL behind (the .db is self-contained for file sync)");
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        // ---------------------------------------------------------------- page size / vacuum
        // Measured 2026-10-11 on the real X: store: a feature row is ~2 KB, two do not fit a 4 KB leaf,
        // so SQLite stored one row per page and the store was 1.34x the JSON. At 16 KB pages it is 0.95x.
        static void TestVacuum(Action<bool, string> assert)
        {
            var dir = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "vac-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var store = Path.Combine(dir, "truedat.db");
            try
            {
                // A store built the old way, at SQLite's 4 KB default.
                using (var db = SqliteDb.Open(store, create: true, pageSize: 4096))
                {
                    SchemaMigrator.Apply(db, "selftest");
                    assert(db.ExecScalar("PRAGMA page_size;") == "4096", "Open(pageSize: 4096) creates a 4 KB-page store");
                }
                var entries = FixtureEntries(@"D:\Music");
                var catalog = Path.Combine(dir, "mbxmoods.json");
                WriteCatalog(catalog, entries);
                ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = catalog, LibraryRoot = @"D:\Music" });
                using (var db = SqliteDb.Open(store, create: false))
                    assert(db.ExecScalar("PRAGMA page_size;") == "4096", "opening an existing 4 KB store never changes its page size");

                bool bad = false;
                try { VacuumCommand.Execute(store, 1000); } catch (StoreException) { bad = true; }
                assert(bad, "a page size that is not a power of two in 512..65536 is a StoreException, nothing rewritten");
                using (var db = SqliteDb.Open(store, create: false))
                    assert(db.ExecScalar("PRAGMA page_size;") == "4096", "the refused vacuum left the page size alone");

                var r = VacuumCommand.Execute(store, null);
                assert(r.PageSizeBefore == 4096 && r.PageSizeAfter == SqliteDb.DefaultPageSize, $"vacuum with no size repacks to the default ({r.PageSizeBefore} -> {r.PageSizeAfter})");
                assert(r.BytesBefore > 0 && r.BytesAfter > 0, "vacuum reports the file size before and after");
                using (var db = SqliteDb.Open(store, create: false))
                {
                    assert(db.ExecScalar("PRAGMA page_size;") == SqliteDb.DefaultPageSize.ToString(), "the store reads back at the new page size");
                    assert(string.Equals(db.ExecScalar("PRAGMA journal_mode;"), "wal", StringComparison.OrdinalIgnoreCase), "the store is back in WAL after the vacuum");
                    assert(db.ExecScalar("SELECT count(*) FROM track_copy;") == entries.Count.ToString(), "every copy row survived the repack");
                }
                var rt = ExportCommand.Roundtrip(store, catalog, null);
                assert(rt.Equal && rt.Same == entries.Count, $"round-trip is EQUAL after the repack (same {rt.Same}, different {rt.Different})");

                var r2 = VacuumCommand.Execute(store, 8192);
                assert(r2.PageSizeBefore == SqliteDb.DefaultPageSize && r2.PageSizeAfter == 8192, "an explicit --page-size re-tunes an already-repacked store");

                // Operator 2026-10-22 ("can we do a vacuum at the end as routine maintenance"): the scan's end-of-run compaction is a plain
                // VACUUM in WAL mode — the verb's journal-mode switch fails with "database is locked" while the hub holds the store open
                // , a plain VACUUM does not. It runs every scan (no free-page gate)
                // and reports before/after.
                using (var db = SqliteDb.Open(store, create: false))
                {
                    db.Exec("CREATE TABLE junk(x BLOB); INSERT INTO junk VALUES (zeroblob(3000000)); DELETE FROM junk; DROP TABLE junk;");
                }
                long freeBefore, pagesBefore;
                using (var db = SqliteDb.Open(store, create: false)) { freeBefore = long.Parse(db.ExecScalar("PRAGMA freelist_count;") ?? "0"); pagesBefore = long.Parse(db.ExecScalar("PRAGMA page_count;") ?? "0"); }
                assert(freeBefore * 10 >= pagesBefore, $"fixture: the junk left at least 10% free pages ({freeBefore} of {pagesBefore})");
                using (var holder = SqliteDb.Open(store, create: false))   // a second connection holding the store open, as the hub does
                {
                    holder.ExecScalar("SELECT count(*) FROM track_copy;");
                    var c = VacuumCommand.Compact(store);
                    string journal; long freeAfter;
                    using (var db = SqliteDb.Open(store, create: false)) { journal = db.ExecScalar("PRAGMA journal_mode;") ?? ""; freeAfter = long.Parse(db.ExecScalar("PRAGMA freelist_count;") ?? "-1"); }
                    assert(c != null && c.BytesAfter < c.BytesBefore && c.PagesAfter < c.PagesBefore && freeAfter == 0 && string.Equals(journal, "wal", StringComparison.OrdinalIgnoreCase),
                        $"routine compaction reclaims the free pages in WAL mode with another connection open ({(c == null ? "null" : $"{c.BytesBefore:N0} -> {c.BytesAfter:N0} bytes, {c.PagesBefore} -> {c.PagesAfter} pages")}; free after {freeAfter}; journal {journal})");
                    var again = VacuumCommand.Compact(store);
                    assert(again != null && again.BytesAfter == again.BytesBefore, $"compacting an already-compact store changes nothing ({(again == null ? "skipped" : $"{again.BytesBefore:N0} -> {again.BytesAfter:N0} bytes")})");
                }
                var rt2 = ExportCommand.Roundtrip(store, catalog, null);
                assert(rt2.Equal && rt2.Same == entries.Count, $"round-trip is EQUAL after the routine compaction (same {rt2.Same}, different {rt2.Different})");

                // 2026-10-22: a store can be mostly reclaimable with almost nothing on the freelist —
                // the rest was half-empty pages, which freelist_count cannot see — so a freelist gate skipped both. Deleting every other
                // row reproduces that shape: pages half full, almost none free. The scan's compaction must still shrink the file.
                using (var db = SqliteDb.Open(store, create: false))
                {
                    db.Exec("CREATE TABLE slack(id INTEGER PRIMARY KEY, b BLOB);");
                    db.Exec("WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 6000) INSERT INTO slack SELECT i, randomblob(300) FROM n;");
                    db.Exec("PRAGMA wal_checkpoint(TRUNCATE);");
                }
                VacuumCommand.Compact(store);
                using (var db = SqliteDb.Open(store, create: false)) { db.Exec("DELETE FROM slack WHERE id % 2 = 0; PRAGMA wal_checkpoint(TRUNCATE);"); }
                long slackFree, slackPages;
                using (var db = SqliteDb.Open(store, create: false)) { slackFree = long.Parse(db.ExecScalar("PRAGMA freelist_count;") ?? "0"); slackPages = long.Parse(db.ExecScalar("PRAGMA page_count;") ?? "0"); }
                assert(slackFree * 10 < slackPages, $"fixture: the space is half-empty pages, not free ones ({slackFree} free of {slackPages})");
                var cs = VacuumCommand.Compact(store);
                assert(cs != null && cs.BytesAfter < cs.BytesBefore,
                    $"routine compaction reclaims half-empty pages the freelist cannot see ({(cs == null ? "skipped" : $"{cs.BytesBefore:N0} -> {cs.BytesAfter:N0} bytes")}; {slackFree} of {slackPages} pages free before)");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ---------------------------------------------------------------- store info
        static void TestStoreInfo(Action<bool, string> assert)
        {
            var dir = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "info-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var store = Path.Combine(dir, "truedat.db");
            try
            {
                var entries = FixtureEntries(@"D:\Music");
                entries[@"D:\Music\Tool\a.flac"].AnalysisDurationSecs = 30;      // 300 s track -> RTF 10
                entries[@"D:\Music\Tool\b.mp3"].AnalysisDurationSecs = 9;        // 180 s -> 20
                entries[@"D:\Music\old\legacy.flac"].AnalysisDurationSecs = 50;  // 250 s -> 5
                var catalog = Path.Combine(dir, "mbxmoods.json");
                WriteCatalog(catalog, entries);
                WriteLedgers(dir, @"D:\Music");
                ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = catalog, LibraryRoot = @"D:\Music" });
                // one key move: legacy.flac re-keyed
                var changed = FixtureEntries(@"D:\Music");
                changed[@"D:\Music\Tool\a.flac"].AnalysisDurationSecs = 30;      // the re-import rewrites every row; keep the RTF samples
                changed[@"D:\Music\Tool\b.mp3"].AnalysisDurationSecs = 9;
                changed[@"D:\Music\old\legacy.flac"].AnalysisDurationSecs = 50;
                changed[@"D:\Music\old\legacy.flac"].AudioStreamSha256 = "2222222222222222222222222222222222222222222222222222222222222222";
                changed[@"D:\Music\old\legacy.flac"].AudioStreamSha256Source = "flac-frames";
                WriteCatalog(catalog, changed);
                ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = catalog, LibraryRoot = @"D:\Music" });

                var info = StoreInfoCommand.Collect(store);
                assert(info.SchemaVersion == StoreSchema.CurrentVersion && info.SqliteVersion.StartsWith("3.") && info.MachineId.Length == 36, $"schema/sqlite/machine reported ({info.SchemaVersion}, {info.SqliteVersion})");
                assert(info.Roots.Count == 1 && info.Roots[0].Path == @"D:\Music" && info.Roots[0].Role == "primary", "roots listed");
                assert(info.Tracks == 5 && info.FeatureRows == 6 && info.Counts["track_copy"] == 6 && info.Counts["track_content"] == 5 && info.Counts["file_entity"] == 7 && info.Counts["run"] >= 4,
                    $"counts: 5 tracks (distinct entities with features; d1/d2 share one), 6 feature rows, 7 entities, runs {info.Counts["run"]} ({info.Tracks}/{info.FeatureRows})");
                assert(info.FileBytes > 100_000 && info.PageSize == SqliteDb.DefaultPageSize && info.PageCount * info.PageSize == info.FileBytes, $"file bytes == page_count × page_size at the default page size ({info.FileBytes} = {info.PageCount} × {info.PageSize})");
                assert(info.BytesPerTrack == (double)info.FileBytes / 5, "bytes/track (per distinct track) derives from the file size");
                assert(info.RtfSamples == 3 && info.MedianRtf.HasValue && Math.Abs(info.MedianRtf.Value - 10.0) < 1e-9, $"median RTF over the 3 rows with both durations = 10 (got {info.MedianRtf}, n={info.RtfSamples})");
                assert(info.FailedComponents.TryGetValue("sha", out var fsha) && fsha == 1 && info.FailedComponents.TryGetValue("fingerprint", out var ffp) && ffp == 1, "failed components split on ';' and counted per component");
                assert(info.ConflictsByKind.TryGetValue("sha-collision-props-differ", out var cc) && cc == 1 && info.ConflictsByKind.Count == 1, $"--store-info counts open conflicts by kind (review #6) ({string.Join(",", info.ConflictsByKind.Keys)})");
                assert(info.Coverage["loudnessMomentary"] == 1 && info.Coverage["dynamicComplexity"] == 1 && info.Coverage["fingerprint.v1"] == 6 && info.Coverage["smfmScores"] == 1,
                    $"coverage by wave: extended {info.Coverage["loudnessMomentary"]}, capture-once {info.Coverage["dynamicComplexity"]}, fingerprint {info.Coverage["fingerprint.v1"]}, smfm {info.Coverage["smfmScores"]} (only the fully populated entry has the waves)");
                assert(info.ContentKeysShared == 1 && info.LocationsOnSharedKeys == 2, $"one content key shared by 2 locations (d1/d2) ({info.ContentKeysShared}/{info.LocationsOnSharedKeys})");
                assert(info.SupersededHashes == 1 && info.NoShaFeatures == 1, $"1 superseded hash (the re-keyed FLAC), 1 no-sha feature row ({info.SupersededHashes}/{info.NoShaFeatures})");

                int rc = StoreInfoCommand.Run(store, false);
                assert(rc == 0 && File.Exists(Path.Combine(dir, StoreInfoCommand.ResultFileName)), "Run writes store-info.json beside the store and exits 0");

                // Codex review 2026-10-20 #8: the catalog is not always beside the database (--store-dir, an explicit --store, a network
                // library's local store). Given the catalog it was resolved against, store-info reports THOSE files; it used to look
                // beside the database, report them absent, and recommend exporting a second catalog into the database's folder.
                var libDir = Path.Combine(dir, "lib"); Directory.CreateDirectory(libDir);
                var libCatalog = Path.Combine(libDir, Program.MoodsFileName);
                using (StoreLog.Quiet()) ExportCommand.Execute(new ExportOptions { StorePath = store, OutPath = libCatalog });
                var elsewhere = StoreInfoCommand.Collect(store, libCatalog);
                assert(elsewhere.CatalogPath == libCatalog && elsewhere.FilesState == "at-generation" && !elsewhere.NextCommand.Contains("--store-export"),
                    $"store-info with the catalog elsewhere: reports the real catalog as published, no export into the store's folder ({elsewhere.FilesState}; next: {elsewhere.NextCommand})");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ---------------------------------------------------------------- store as the verbs' dictionary
        static void TestStoreDictionary(Action<bool, string> assert)
        {
            var dir = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "dict-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var store = Path.Combine(dir, "truedat.db");
            try
            {
                var entries = FixtureEntries(@"D:\Music");
                var catalog = Path.Combine(dir, "mbxmoods.json");
                WriteCatalog(catalog, entries);
                ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = catalog, LibraryRoot = @"D:\Music" });

                var fromJson = new System.Collections.Concurrent.ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
                Program.LoadExistingMoodsForStore(catalog, fromJson);
                var fromStore = StoreDictionary.Load(store, null, out var root);
                assert(root == @"root D:\Music" && fromStore.Count == fromJson.Count && fromStore.Count == 6, $"store dictionary has the catalog's 6 entries for the primary root ({fromStore.Count}, {root})");
                int same = 0;
                foreach (var kv in fromJson) if (fromStore.TryGetValue(kv.Key, out var s) && EntryJson(kv.Key, kv.Value) == EntryJson(kv.Key, s)) same++;
                assert(same == 6, $"every entry in the store dictionary writes the same bytes as the JSON-loaded one ({same}/6)");
                assert(fromStore.ContainsKey(@"d:\music\TOOL\A.FLAC") && fromStore.ContainsKey("D:/Music/Tool/a.flac"), "the dictionary keys compare like PathComparer (case- and separator-insensitive), as the verbs expect");

                // #6: a --store that is not a database is a plain refusal, never an escaping SqliteException.
                var notDb = Path.Combine(dir, "notadb.db");
                File.WriteAllText(notDb, "this is not a database");
                bool plain = false; string? why = null;
                try { StoreDictionary.Load(notDb, null, out _); } catch (StoreException ex) { plain = true; why = ex.Message; } catch (Exception ex) { why = ex.GetType().Name + ": " + ex.Message; }
                assert(plain && why != null && why.Contains("notadb.db"), $"#6 a non-database --store is a StoreException naming the file ({why})");

                // The verb itself, on the store's dictionary: RunDuplicates groups the two same-sha entries.
                var dupDir = Path.Combine(dir, "dupes"); Directory.CreateDirectory(dupDir);
                int rc = Program.RunDuplicatesForStore(Path.Combine(dupDir, "mbxmoods.json"), fromStore);
                assert(rc == 0 && File.Exists(Path.Combine(dupDir, "mbxmoods-duplicates.json")), "--duplicates runs on the store dictionary and writes its outputs beside the given moods path");
                var dj = File.ReadAllText(Path.Combine(dupDir, "mbxmoods-duplicates.json"));
                assert(dj.Contains("d1.flac") && dj.Contains("d2.flac"), "the exact-duplicate pair (same sha) is grouped");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
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

                // Two primary roots (the library's files in two places) and no --export-root: the whole library is published.
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                    os.Dal.EnsureRoot(os.Machine.Id, @"E:\Other", "primary", DateTime.UtcNow.ToString("o"));
                var r2r = ExportCommand.Execute(new ExportOptions { StorePath = store, OutPath = Path.Combine(outDir, "x.json") });
                assert(r2r.Entries == 7 && r2r.RootCount == 2 && r2r.Root.Contains(@"D:\Music") && r2r.Root.Contains(@"E:\Other"),
                    $"two primary roots without --export-root publish the whole library and name both ({r2r.Entries} entries, {r2r.RootCount} roots: {r2r.Root})");
                var r3 = ExportCommand.Execute(new ExportOptions { StorePath = store, OutPath = Path.Combine(outDir, "x.json"), ExportRoot = @"d:\music\" });
                assert(r3.Entries == 7 && r3.Root == @"D:\Music", "--export-root selects the root case-insensitively, tolerating a trailing separator");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>
        /// A fresh scan mints its roots from the paths analysed SO FAR, so a first checkpoint holding one sub-folder minted that
        /// sub-folder as the primary root, and a later batch under a sibling folder minted the real parent beside it. Two primary
        /// roots, one inside the other, made every later publication refuse ("2 primary roots; pass --export-root"): the MTest scan
        /// of 2026-10-18 published 154 of 3,439 tracks at its first checkpoint and nothing after. Nested primaries are never a real
        /// choice — the containing root IS the library — so the store keeps ONE: the fold runs where a root is minted (prevention)
        /// and at open (repair of a store an earlier build left nested), and every reader sees one root.
        /// </summary>
        static void TestNestedRoots(Action<bool, string> assert)
        {
            var dir = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "nested-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var catDir = Path.Combine(dir, "lib"); var outDir = Path.Combine(dir, "out");
            Directory.CreateDirectory(catDir); Directory.CreateDirectory(outDir);
            var store = Path.Combine(dir, "truedat.db");
            string Now() => DateTime.UtcNow.ToString("o");
            string? Raw(string sql) { using var db = SqliteDb.Open(store, false); return db.ExecScalar(sql); }
            void RawExec(string sql) { using var db = SqliteDb.Open(store, false); db.Exec(sql); }
            const string Outer = @"D:\Music", Inner = @"D:\Music\anchors";
            try
            {
                // 0. The scan's own path: a FRESH scan's first checkpoint flush, holding only anchors tracks, mints the library's root
                //    when the hint carries the scan's whole work list (Program.ScanRootHints) — not the sub-folder those tracks share.
                var freshStore = Path.Combine(dir, "fresh.db");
                ScanSink.Reset();
                ScanSink.Record(Inner + @"\a1.flac", Entry("1111111111111111111111111111111111111111111111111111111111111111", "flac-frames", "flac", 100000));
                var workList = new[] { Inner + @"\a1.flac", Inner + @"\a2.flac", Outer + @"\synthetic\s1.wav", Outer + @"\real\r1.mp3" };
                var f0 = ScanSink.Flush(freshStore, "output-x64.5", Program.ScanRootHints(new[] { Inner + @"\a1.flac" }, workList), final: false);
                using (var db0 = SqliteDb.Open(freshStore, false))
                    assert(f0.Ok && db0.ExecScalar("SELECT group_concat(path, ' | ') FROM library_root;") == Outer,
                        $"a fresh scan's first flush mints the library's root from the work list ({db0.ExecScalar("SELECT group_concat(path, ' | ') FROM library_root;")}; {f0.Error})");
                ScanSink.Reset();

                // 1. The first checkpoint: only the anchors folder has been analysed, so the root lands one level too deep,
                //    and the first publication names it (exactly what MTest's 20:34 catalog carries).
                var first = new Dictionary<string, TrackEntry>(PathComparer.Instance)
                {
                    [Inner + @"\a1.flac"] = Entry("1111111111111111111111111111111111111111111111111111111111111111", "flac-frames", "flac", 100000),
                    [Inner + @"\a2.flac"] = Entry("2222222222222222222222222222222222222222222222222222222222222222", "flac-frames", "flac", 110000),
                };
                var cat = Path.Combine(catDir, "mbxmoods.json");
                WriteCatalog(cat, first);
                ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = cat });
                assert(Raw("SELECT path FROM library_root;") == Inner, $"the first batch alone mints the deeper root ({Raw("SELECT path FROM library_root;")})");
                var early = Path.Combine(outDir, "early", "mbxmoods.json");
                Directory.CreateDirectory(Path.GetDirectoryName(early)!);
                var re = ExportCommand.Execute(new ExportOptions { StorePath = store, OutPath = early });
                assert(re.Published && re.Root == Inner && CatalogOrigin.TryReadHead(early, CatalogOrigin.ContractV2, out var eo) && eo.RootPath == Inner,
                    $"the first publication names the deep root ({re.Root})");
                long genEarly = re.Generation;

                // 2. PREVENTION. A later batch under sibling folders: the parent is minted, and the deep root is folded into it in
                //    the same transaction — the store never commits two nested primaries.
                var second = new Dictionary<string, TrackEntry>(first, PathComparer.Instance)
                {
                    [Outer + @"\synthetic\s1.wav"] = Entry("3333333333333333333333333333333333333333333333333333333333333333", null, "wav", 120000),
                    [Outer + @"\synthetic\s2.wav"] = Entry("4444444444444444444444444444444444444444444444444444444444444444", null, "wav", 130000),
                    [Outer + @"\real\r1.mp3"] = Entry("5555555555555555555555555555555555555555555555555555555555555555", null, "mp3", 140000),
                };
                WriteCatalog(cat, second);
                ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = cat });
                assert(Raw("SELECT count(*) FROM library_root;") == "1" && Raw("SELECT path FROM library_root;") == Outer,
                    $"the parent is minted and the deep root folded into it: one root ({Raw("SELECT group_concat(path, ' | ') FROM library_root;")})");
                assert(Raw("SELECT count(*) FROM track_location;") == "5" && Raw("SELECT count(DISTINCT root_id) FROM track_location;") == "1",
                    $"all five locations sit under the one root ({Raw("SELECT count(*) FROM track_location;")} locations)");
                assert(Raw($"SELECT rel_path || '|' || canonical_path FROM track_location WHERE display_path = '{Inner}\\a1.flac';") == "anchors/a1.flac|anchors/a1.flac",
                    $"a moved location's root-relative paths are recomputed against the outer root ({Raw($"SELECT rel_path || '|' || canonical_path FROM track_location WHERE display_path = '{Inner}\\a1.flac';")})");

                // 3. The stale first catalog (it names the folded root) must not make the next scan refuse: it resolves to the root
                //    that contains it, the store is ahead, and the scan loads all five.
                var target = new System.Collections.Concurrent.ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
                var load = StoreScan.LoadAuthoritative(store, early, target);
                assert(!load.Refused && load.Case == "store-ahead" && load.RootPath == Outer && target.Count == 5,
                    $"a catalog naming a folded root loads the containing root, store ahead ({load.Case}, refused {load.Refused}: {load.Refusal}, root {load.RootPath}, {target.Count} entries)");

                // 4. The publication that refused for 90 minutes now writes every entry.
                var outPath = Path.Combine(outDir, "mbxmoods.json");
                var rn = ExportCommand.Execute(new ExportOptions { StorePath = store, OutPath = outPath });
                var written = File.ReadAllText(outPath);
                assert(rn.Published && rn.Entries == 5 && rn.Root == Outer && rn.Generation > genEarly
                       && written.Contains(@"anchors\\a1.flac") && written.Contains(@"synthetic\\s1.wav") && written.Contains(@"real\\r1.mp3"),
                    $"the export publishes all five under the one root ({rn.Entries} entries, root {rn.Root}, generation {rn.Generation})");

                // 4b. The scan's publisher takes no root at all: it always writes the whole library, so a root it loaded and a
                //     checkpoint later folded can never narrow it. An operator's typed --export-root still means exactly that root.
                var scanPub = Path.Combine(outDir, "scanpub", "mbxmoods.json");
                Directory.CreateDirectory(Path.GetDirectoryName(scanPub)!);
                var pubEx = StoreScan.Publish(store, scanPub, out var pubRep);
                assert(pubEx == null && pubRep.Root == Outer && pubRep.Entries == 5,
                    $"the scan's publisher writes the whole library ({pubEx?.Message}; root {pubRep.Root}, {pubRep.Entries} entries)");
                string? typedMsg = null;
                try { ExportCommand.Execute(new ExportOptions { StorePath = store, OutPath = Path.Combine(outDir, "typed.json"), ExportRoot = Inner }); }
                catch (StoreException ex) { typedMsg = ex.Message; }
                assert(typedMsg != null && typedMsg.Contains("No root"), $"a typed --export-root naming a folded root still refuses ({typedMsg})");

                // 5. REPAIR ON OPEN. A store an earlier build left nested (re-created by hand, below the open path that repairs it).
                RawExec($"INSERT INTO library_root(machine_id, path, role, created_utc) SELECT machine_id, '{Inner}', 'primary', '2026-10-18T03:34:55Z' FROM library_root WHERE path = '{Outer}';" +
                        $"UPDATE track_location SET root_id = (SELECT id FROM library_root WHERE path = '{Inner}'), rel_path = substr(rel_path, 9), canonical_path = substr(canonical_path, 9) WHERE rel_path LIKE 'anchors/%';");
                assert(Raw("SELECT count(*) FROM library_root WHERE role = 'primary';") == "2", "the broken state: two nested primaries");
                var genBroken = long.Parse(Raw("SELECT value FROM store_meta WHERE key = 'generation';")!);
                using (var os = OpenStore.Open(store, false, "selftest", Now()))
                {
                    assert(os.Dal.ListRoots().Count == 1 && os.Dal.ListRoots()[0].Path == Outer, $"opening the store folds the nested root ({os.Dal.ListRoots().Count} roots)");
                    assert(os.Dal.Generation() == genBroken + 1, $"the repair is a mutation and advances the generation ({genBroken} -> {os.Dal.Generation()})");
                    assert(os.Db.ExecScalar($"SELECT canonical_path FROM track_location WHERE display_path = '{Inner}\\a2.flac';") == "anchors/a2.flac",
                        "the repaired location's paths are the outer root's again");
                }
                using (var os = OpenStore.Open(store, false, "selftest", Now()))
                    assert(os.Dal.Generation() == genBroken + 1, "a healthy store's open is a read: a second open does not advance the generation");
                assert(ExportCommand.Execute(new ExportOptions { StorePath = store, OutPath = outPath }).Entries == 5, "the repaired store exports all five");

                // 6. MERGE. One path recorded under BOTH roots (an explicit --library-root import over a store that held the deep root):
                //    the location with the newer copy survives, the other's history moves onto it, and copies stay one per location.
                string a1 = Raw($"SELECT id FROM track_location WHERE display_path = '{Inner}\\a1.flac';")!;
                RawExec($"INSERT INTO library_root(machine_id, path, role, created_utc) SELECT machine_id, '{Inner}', 'primary', '2026-10-18T03:34:55Z' FROM library_root WHERE path = '{Outer}';" +
                        $"INSERT INTO track_location(entity_id, root_id, rel_path, canonical_path, display_path, last_modified_utc, last_seen_utc, state) " +
                        $"SELECT entity_id, (SELECT id FROM library_root WHERE path = '{Inner}'), 'a1.flac', 'a1.flac', display_path, last_modified_utc, last_seen_utc, state FROM track_location WHERE id = {a1};" +
                        $"CREATE TEMP TABLE tc AS SELECT * FROM track_copy WHERE location_id = {a1};" +
                        $"UPDATE tc SET location_id = (SELECT id FROM track_location WHERE root_id = (SELECT id FROM library_root WHERE path = '{Inner}')), updated_utc = '2099-01-01T00:00:00.0000000Z';" +
                        "INSERT INTO track_copy SELECT * FROM tc; DROP TABLE tc;" +
                        $"INSERT INTO check_result(location_id, kind, result, checked_utc) VALUES ({a1}, 'verify', 'OK', '2026-10-18T00:00:00Z');");
                string dup = Raw($"SELECT id FROM track_location WHERE root_id = (SELECT id FROM library_root WHERE path = '{Inner}');")!;
                assert(Raw($"SELECT count(*) FROM track_location WHERE display_path = '{Inner}\\a1.flac';") == "2", "the broken state: a1 is recorded twice");
                using (var os = OpenStore.Open(store, false, "selftest", Now()))
                {
                    var db = os.Db;
                    assert(db.ExecScalar($"SELECT count(*) FROM track_location WHERE display_path = '{Inner}\\a1.flac';") == "1"
                           && db.ExecScalar($"SELECT id FROM track_location WHERE display_path = '{Inner}\\a1.flac';") == dup,
                        $"the location holding the newer copy survives ({db.ExecScalar($"SELECT id FROM track_location WHERE display_path = '{Inner}\\a1.flac';")}, want {dup})");
                    assert(db.ExecScalar($"SELECT canonical_path FROM track_location WHERE id = {dup};") == "anchors/a1.flac"
                           && db.ExecScalar($"SELECT updated_utc FROM track_copy WHERE location_id = {dup};") == "2099-01-01T00:00:00.0000000Z",
                        "it moved under the outer root and kept its newer copy");
                    assert(db.ExecScalar($"SELECT location_id FROM check_result;") == dup, "the dropped location's check history moved onto the survivor");
                    assert(db.ExecScalar("SELECT count(*) FROM track_copy;") == db.ExecScalar("SELECT count(*) FROM track_location;")
                           && db.ExecScalar("SELECT count(*) FROM track_copy WHERE location_id NOT IN (SELECT id FROM track_location);") == "0"
                           && db.ExecScalar("SELECT count(*) FROM verdict WHERE location_id NOT IN (SELECT id FROM track_location);") == "0",
                        "one copy per location; no copy or verdict left pointing at the dropped location");
                    assert(db.ExecScalar("PRAGMA foreign_key_check;") == null, "no foreign key left dangling");
                }

                // 7. A LIBRARY ON TWO DRIVES. Files can sit anywhere — MusicBee and the hub serve them all — so a library whose
                //    tracks are on D: and E: is one library: two roots, never folded, both published, both loaded by every scan.
                var twoDrives = new Dictionary<string, TrackEntry>(second, PathComparer.Instance)
                {
                    [@"E:\Other\x.mp3"] = Entry("6666666666666666666666666666666666666666666666666666666666666666", null, "mp3", 150000),
                };
                WriteCatalog(cat, twoDrives);
                ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = cat });
                using (var os = OpenStore.Open(store, false, "selftest", Now()))
                    assert(os.Dal.ListRoots().Count == 2 && os.Dal.LibraryRoots(os.Machine.Id).Count == 2, "a root on another drive is not folded: two library roots");
                var both = Path.Combine(outDir, "both", "mbxmoods.json");
                Directory.CreateDirectory(Path.GetDirectoryName(both)!);
                var rb = ExportCommand.Execute(new ExportOptions { StorePath = store, OutPath = both });
                bool boRead = CatalogOrigin.TryReadHead(both, CatalogOrigin.ContractV2, out var bo);
                assert(rb.Published && rb.Entries == 6 && rb.RootCount == 2 && boRead && bo.RootPath == null,
                    $"the two-drive library publishes all six entries and names no single root ({rb.Entries} entries, {rb.RootCount} roots, rootPath {bo.RootPath ?? "absent"})");
                var tb = new System.Collections.Concurrent.ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
                var lb = StoreScan.LoadAuthoritative(store, both, tb);
                assert(!lb.Refused && tb.Count == 6 && lb.RootPath == null, $"a scan over that catalog loads the whole library ({lb.Case}, refused {lb.Refused}: {lb.Refusal}, {tb.Count} entries)");
                // The 'scans it every time' case: a catalog published when the library had ONE root, loaded after it grew a second.
                var tg = new System.Collections.Concurrent.ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
                var lg = StoreScan.LoadAuthoritative(store, early, tg);
                assert(!lg.Refused && tg.Count == 6 && tg.ContainsKey(@"E:\Other\x.mp3"),
                    $"a catalog naming one root still loads every entry of the library, so the other drive's tracks are cached, not re-analysed ({lg.Case}, {tg.Count} entries)");
                var dictAll = StoreDictionary.Load(store, null, out var dictRoots);
                assert(dictAll.Count == 6 && dictRoots.StartsWith("2 roots", StringComparison.Ordinal), $"--stats / --duplicates read the whole library ({dictAll.Count}, {dictRoots})");
                // A catalog naming a root this library does not have came from another store, and still refuses.
                var foreign = Path.Combine(outDir, "foreign.json");
                File.WriteAllText(foreign, File.ReadAllText(early).Replace(@"""rootPath"":""D:\\Music\\anchors""", @"""rootPath"":""Z:\\Elsewhere"""));
                var lf = StoreScan.LoadAuthoritative(store, foreign, new System.Collections.Concurrent.ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance));
                assert(lf.Refused && lf.Refusal!.Contains(@"Z:\Elsewhere"), $"a catalog from another store's library still refuses, naming its root ({lf.Refusal})");

                // CanonicalPath.Contains is the one containment rule both the fold and the T18i fallback use.
                assert(CanonicalPath.Contains(@"D:\Music", @"D:\Music\anchors") && CanonicalPath.Contains(@"d:\music\", @"D:/Music/a/b")
                       && !CanonicalPath.Contains(@"D:\Music", @"D:\Music") && !CanonicalPath.Contains(@"D:\Music", @"D:\Music2")
                       && !CanonicalPath.Contains(@"D:\Music\anchors", @"D:\Music") && CanonicalPath.Contains(@"\\nas\share", @"\\nas\share\music"),
                    "containment: strict, case- and separator-insensitive, no prefix-sibling match");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>
        /// spectralFlatness is Essentia's spectral_flatness_db — FlatnessDB over the Bark bands, which the music extractor emits as
        /// barkbands_flatness_db — so in the store it equals barkFlatness wherever that is present. Rows an older build wrote (the
        /// 2026-08-11 .. 2026-10-19 three-band average) are converted the first time the store is opened, from the stored Bark value,
        /// with no audio read; anything written since goes through the same rule at write time, an imported old catalog included.
        /// </summary>
        static void TestFlatnessDefinition(Action<bool, string> assert)
        {
            var dir = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "flatness-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var store = Path.Combine(dir, "truedat.db");
            string? Raw(string sql) { using var db = SqliteDb.Open(store, false); return db.ExecScalar(sql); }
            void RawExec(string sql) { using var db = SqliteDb.Open(store, false); db.Exec(sql); }
            try
            {
                // An old catalog carries the three-band average: importing it must store Essentia's value.
                var a = Entry("1111111111111111111111111111111111111111111111111111111111111111", "flac-frames", "flac", 100000, full: true);
                a.Features.BarkFlatness = 0.2396; a.Features.ErbFlatness = 0.2498; a.Features.MelFlatness = 0.2594; a.Features.SpectralFlatness = 0.2496;
                var b = Entry("2222222222222222222222222222222222222222222222222222222222222222", "flac-frames", "flac", 110000, full: true);
                b.Features.BarkFlatness = 0.3100; b.Features.ErbFlatness = 0.3200; b.Features.MelFlatness = 0.3300; b.Features.SpectralFlatness = 0.3200;
                var cat = Path.Combine(dir, "mbxmoods.json");
                WriteCatalog(cat, new Dictionary<string, TrackEntry>(PathComparer.Instance) { [@"D:\Music\a.flac"] = a, [@"D:\Music\b.flac"] = b });
                ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = cat });
                assert(Raw("SELECT count(*) FROM track_content WHERE spectralFlatness IS NOT barkFlatness;") == "0"
                       && Raw("SELECT count(*) FROM track_content WHERE spectralFlatness = 2396;") == "1",
                    $"an imported three-band average is stored as Essentia's Bark flatness ({Raw("SELECT group_concat(spectralFlatness || '/' || barkFlatness) FROM track_content;")})");

                // A store an older build wrote: the average sits in the row. Opening converts it, once, and that is a mutation.
                RawExec("UPDATE track_content SET spectralFlatness = 2496 WHERE barkFlatness = 2396;" +
                        "UPDATE track_content SET barkFlatness = NULL, spectralFlatness = 3200 WHERE barkFlatness = 3100;");
                long genBefore = long.Parse(Raw("SELECT value FROM store_meta WHERE key = 'generation';")!);
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                {
                    assert(os.Db.ExecScalar("SELECT spectralFlatness FROM track_content WHERE barkFlatness = 2396;") == "2396",
                        $"opening converts the old average to Essentia's value ({os.Db.ExecScalar("SELECT spectralFlatness FROM track_content WHERE barkFlatness = 2396;")})");
                    assert(os.Db.ExecScalar("SELECT spectralFlatness FROM track_content WHERE barkFlatness IS NULL;") == "3200",
                        "a row with no Bark value keeps what it has");
                    assert(os.Dal.Generation() == genBefore + 1, $"the conversion is a mutation: the generation advances ({genBefore} -> {os.Dal.Generation()})");
                }
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                    assert(os.Dal.Generation() == genBefore + 1, "a converted store's next open is a read: the generation does not move again");
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

        internal static void WriteCatalog(string path, IEnumerable<KeyValuePair<string, TrackEntry>> entries)
        {
            using var fs = File.Create(path);
            using var jw = new System.Text.Json.Utf8JsonWriter(fs);
            jw.WriteStartObject();
            jw.WritePropertyName("tracks"); jw.WriteStartObject();
            foreach (var kv in entries) Program.WriteTrackEntryForStore(jw, kv.Key, kv.Value);
            jw.WriteEndObject(); jw.WriteEndObject();
        }

        internal static Dictionary<string, TrackEntry> FixtureEntries(string root)
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
            // The second row names a path that IS in the catalog: an entry scanned before an exclusion rule
            // existed stays in the catalog and is ledgered as skipped on every later scan (review #1).
            File.WriteAllLines(Path.Combine(dir, "mbxmoods-skipped.csv"), new[]
            {
                "path,extension,reason,timestamp",
                $"{root}\\skip\\s.dsf,.dsf,unsupported codec: DSD,2026-10-01T00:00:00Z",
                $"{root}\\Tool\\b.mp3,.mp3,excluded by rule,2026-10-02T00:00:00Z",
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
                assert(r.ErrorsRows == 1 && r.SkippedRows == 2 && r.ReviewRows == 1 && r.VerifyRows == 2 && r.ReceiptRows == 1, $"ledgers: errors {r.ErrorsRows}, skipped {r.SkippedRows}, review {r.ReviewRows}, verify {r.VerifyRows}, receipts {r.ReceiptRows}");
                var rt0 = ExportCommand.Roundtrip(store, catalog, null);
                assert(rt0.Equal, $"#1 a cataloged path that is also in skipped.csv keeps its lastModified: round-trip stays EQUAL ({rt0.Same}/{rt0.CatalogEntries}, different {rt0.Different}, first {rt0.FirstDifferentPath})");
                assert(File.Exists(r.ResultPath) && File.ReadAllText(r.ResultPath).Contains("\"entries\": 6"), "store-import-result.json written beside the store with the counts");
                assert(r.StoreBytesAfter > r.StoreBytesBefore && r.StoreBytesAfter > 100_000, $"store size is measured after a WAL checkpoint, so it reflects the rows ({r.StoreBytesBefore} -> {r.StoreBytesAfter})");
                assert(!File.Exists(store + "-wal") || new FileInfo(store + "-wal").Length == 0, "the WAL is folded into the main file at the end of an import");

                using (var os = OpenStore.Open(store, create: false, "selftest", DateTime.UtcNow.ToString("o")))
                {
                    assert(os.Dal.Count("file_entity") == 7, $"7 entities: 5 from the catalog + the errors path + the skipped path (review shares the errors path) ({os.Dal.Count("file_entity")})");
                    assert(os.Dal.Count("track_copy") == 6 && os.Dal.Count("track_content") == 5 && os.Dal.Count("track_location") == 8,
                        $"6 copy rows, 5 content rows (d1/d2 share a media); 8 locations = 6 catalog entries + the errors path + the skipped path, which are real files too ({os.Dal.Count("track_copy")}/{os.Dal.Count("track_content")}/{os.Dal.Count("track_location")})");
                    assert(os.Dal.Count("run") == 5, $"runs: import + errors + 2 skipped + receipt = 5 ({os.Dal.Count("run")})");
                    assert(os.Dal.Count("check_result") == 2 && os.Dal.Count("work_item") == 1 && os.Dal.Count("conflict") == 1, "2 verify checks, 1 review work item, 1 conflict");
                    assert(os.Db.ExecScalar("SELECT review_state FROM work_item;") == "auto" && os.Db.ExecScalar("SELECT attempts FROM work_item;") == _fixtureReviewAttempts.ToString(),
                        $"review state and attempts carried onto the work item (state {os.Db.ExecScalar("SELECT review_state FROM work_item;")}, attempts {os.Db.ExecScalar("SELECT attempts FROM work_item;")} vs ledger {_fixtureReviewAttempts})");
                    assert(os.Db.ExecScalar("SELECT outcome FROM run WHERE producer_key = 'import';") == "ok", "the import's own run is recorded ok");
                    assert(os.Db.ExecScalar("SELECT failed_components FROM run WHERE outcome = 'failed';") == "sha;fingerprint", "errors.csv row became a failed essentia run with its components");
                    assert(os.Db.ExecScalar("SELECT count(*) FROM identity_facet WHERE kind = '" + FacetKinds.ShaTaglibRegion + "';") == "1", "legacy FLAC sha recorded under the taglib-region kind");
                    assert(os.Db.ExecScalar("SELECT count(*) FROM track_content WHERE content_key_kind = 'entity';") == "1", "the no-sha entry's content row is keyed entity:<uuid> with kind 'entity'");

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
                    assert(os.Dal.Count("file_entity") == 7 && os.Dal.Count("track_copy") == 6 && os.Dal.Count("check_result") == 2 && os.Dal.Count("work_item") == 1,
                        "re-import leaves entity/feature/check/work counts unchanged");
                    assert(os.Dal.Count("run") == 6, $"re-import adds exactly one run (its own); ledger runs are not duplicated ({os.Dal.Count("run")})");
                }

                // #5: the ledger files are rewritten by every scan (mtime changes) — that must not duplicate their runs.
                File.SetLastWriteTimeUtc(Path.Combine(catDir, "mbxmoods-errors.csv"), DateTime.UtcNow.AddMinutes(1));
                File.SetLastWriteTimeUtc(Path.Combine(catDir, "mbxmoods-verify.receipt.json"), DateTime.UtcNow.AddMinutes(1));
                ImportCommand.Execute(o);
                using (var os = OpenStore.Open(store, create: false, "selftest", DateTime.UtcNow.ToString("o")))
                    assert(os.Dal.Count("run") == 7, $"#5 touching errors.csv and the receipt does not re-insert their runs on re-import (runs {os.Dal.Count("run")}, expected 7)");

                // #4b: an OLDER catalog over a newer store must not regress rows.
                var olderDir = Path.Combine(dir, "older"); Directory.CreateDirectory(olderDir);
                var older = FixtureEntries(@"D:\Music");
                older[@"D:\Music\Tool\b.mp3"].LastModified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);   // older than the store's 2026-05-01
                older[@"D:\Music\Tool\b.mp3"].Features.Bpm = 999;
                var olderCatalog = Path.Combine(olderDir, "mbxmoods.json");
                WriteCatalog(olderCatalog, older);
                var rs = ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = olderCatalog, LibraryRoot = @"D:\Music" });
                assert(rs.Stale == 1, $"#4b an entry whose lastModified is older than the store's, with the same content key, is skipped and counted stale ({rs.Stale})");
                using (var os = OpenStore.Open(store, create: false, "selftest", DateTime.UtcNow.ToString("o")))
                {
                    long root = os.Dal.EnsureRoot(os.Machine.Id, @"D:\Music", "primary", DateTime.UtcNow.ToString("o"));
                    double? bpm = null;
                    foreach (var (display, back) in os.Dal.ReadEntries(root)) if (string.Equals(display, @"D:\Music\Tool\b.mp3", StringComparison.OrdinalIgnoreCase)) bpm = back.Features.Bpm;
                    assert(bpm == 120, $"#4b the stale arrival did not overwrite the row (bpm {bpm}, expected 120)");
                }

                // Mirror: the same library under X:\Library, imported as a mirror root -> locations, not entities.
                var mirror = Path.Combine(mirrorDir, "mbxmoods.json");
                WriteCatalog(mirror, FixtureEntries(@"X:\Library"));
                var r3 = ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = mirror, LibraryRoot = @"X:\Library", RootRole = "mirror" });
                assert(r3.EntitiesCreated == 0 && r3.CreatedNoSha == 0 && r3.Locations == 6 && r3.MatchedHash == 5 && r3.MatchedPath == 1,
                    $"mirror import: 0 entities created, 6 new locations, 5 matched by sha, the no-sha one matched by canonical path across roots ({r3.EntitiesCreated}/{r3.CreatedNoSha}/{r3.Locations}/{r3.MatchedHash}/{r3.MatchedPath})");
                using (var os = OpenStore.Open(store, create: false, "selftest", DateTime.UtcNow.ToString("o")))
                {
                    assert(os.Dal.Count("file_entity") == 7 && os.Dal.Count("track_location") == 14 && os.Dal.Count("library_root") == 2,
                        $"after the mirror: 7 entities, 14 locations (8 + 6 mirrored), 2 roots ({os.Dal.Count("file_entity")}/{os.Dal.Count("track_location")}/{os.Dal.Count("library_root")})");
                    assert(os.Dal.Count("track_copy") == 12 && os.Dal.Count("track_content") == 5,
                        $"v2: a mirror adds copy rows (6 → 12) and no content rows (still 5: one description per media) ({os.Dal.Count("track_copy")}/{os.Dal.Count("track_content")})");
                }
                var infoAfterMirror = StoreInfoCommand.Collect(store);
                assert(infoAfterMirror.Tracks == 5 && infoAfterMirror.FeatureRows == 12,
                    $"#3 --store-info counts tracks as distinct entities with features (5: d1/d2 share one) and feature rows separately (12) ({infoAfterMirror.Tracks}/{infoAfterMirror.FeatureRows})");

                // Batch atomicity: fail after 3 entries with batches of 2 -> exactly the first batch is in the store.
                var store2 = Path.Combine(dir, "partial.db");
                bool threw = false;
                try { ImportCommand.Execute(new ImportOptions { StorePath = store2, CatalogPath = catalog, LibraryRoot = @"D:\Music", BatchSize = 2, FailAfterEntries = 3 }); } catch (Exception) { threw = true; }
                assert(threw, "the fail-after seam throws");
                using (var os = OpenStore.Open(store2, create: false, "selftest", DateTime.UtcNow.ToString("o")))
                {
                    assert(os.Dal.Count("track_copy") == 2, $"after a mid-import failure the store holds whole batches only ({os.Dal.Count("track_copy")} copy rows, batch size 2)");
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
            // Column set == DDL minus the meta columns, both ways (the mapper and the DDL cannot drift apart). v2: union of both tables.
            var db = FreshStore(out var dir);
            try
            {
                var ddl = new HashSet<string>(StringComparer.Ordinal);
                using (var st = db.Prepare("PRAGMA table_info(track_content);"))
                    while (st.Step()) ddl.Add(st.GetString(1)!);
                foreach (var m in StoreSchema.TrackContentMetaColumns) ddl.Remove(m);
                using (var st = db.Prepare("PRAGMA table_info(track_copy);"))
                    while (st.Step()) ddl.Add(st.GetString(1)!);
                foreach (var m in StoreSchema.TrackCopyMetaColumns) ddl.Remove(m);
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

            // Vectors round-trip at the field's DECLARED precision (T04: mfcc is FixedVec(4)); the in-memory row keeps doubles, so
            // ToRow canonicalises each element at 4 dp and nothing else changes. (The store's int32 ×10^4 column additionally bounds
            // an element to ±214,748 — real MFCC/GFCC/contrast values are a few hundred at most; the bind refuses loudly beyond it.)
            var prec = new TrackEntry { Features = new TrackFeatures { Mfcc = new[] { -821.3456789012345, 1e-9, 0.1, 12345678.9 } }, LastModified = DateTime.UtcNow };
            var precBack = TrackEntryMapper.FromValues(TrackEntryMapper.ToRow(prec), prec.LastModified);
            assert(precBack.Features.Mfcc != null && precBack.Features.Mfcc.Length == 4
                && precBack.Features.Mfcc[0] == -821.3457 && precBack.Features.Mfcc[1] == 0 && precBack.Features.Mfcc[2] == 0.1 && precBack.Features.Mfcc[3] == 12345678.9,
                $"vectors canonicalise at the field's declared precision, 4 dp ({precBack.Features.Mfcc?[0]:R}, {precBack.Features.Mfcc?[1]:R}, {precBack.Features.Mfcc?[3]:R})");
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
                    dal.UpsertFeature(loc, r.EntityId, full.AudioStreamSha256!, FacetKinds.ShaFrames, null, full, now);
                    dal.UpsertFeature(loc, r.EntityId, full.AudioStreamSha256!, FacetKinds.ShaFrames, null, full, now);
                    tx.Complete();
                }
                assert(dal.Count("track_copy") == 1 && dal.Count("track_content") == 1, "UpsertFeature twice on one location -> one copy row and one content row");
                assert(db.ExecScalar($"SELECT c.content_key_kind FROM track_copy p JOIN track_location l ON l.id = p.location_id JOIN track_content c ON c.entity_id = l.entity_id WHERE p.location_id = {loc};") == FacetKinds.ShaFrames,
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
                using var dal = new StoreDal(db);   // disposed before db: an undisposed DAL kept the file open and left the temp dir behind
                var now = "2026-10-11T10:00:00.0000000Z";
                var m1 = MachineIdentity.EnsureMinted(dal, "ATOM", now);
                assert(m1.Id.Length == 36 && m1.Id[14] == '7', "first open mints a UUIDv7 machine id");
                assert(m1.HostMatches && m1.CreatedByHost == "ATOM", "created-by host recorded, matches");
                assert(dal.Count("machine") == 1 && dal.MetaGet("machine_id") == m1.Id, "one machine row, id in store_meta");

                var m2 = MachineIdentity.EnsureMinted(dal, "atom", now);
                assert(m2.Id == m1.Id && dal.Count("machine") == 1, "second open reads the same id (case-insensitive host), mints nothing");
                assert(m2.ForWrites(dal, adopt: false, now) == m1.Id, "on the minting host, writes go under the store's own machine id");
                var warns = lines.FindAll(l => l.Contains(" WARN "));
                assert(warns.Count == 0, "matching host logs no warning (INFO lines are fine)");

                int before = lines.Count;
                var m3 = MachineIdentity.EnsureMinted(dal, "MAX1", now);
                assert(!m3.HostMatches && m3.Id == m1.Id, "a copied store opened on another host keeps the id but HostMatches is false");
                // Every run that opens a copied store used to print a WARN telling the operator to pass --machine-adopt, though a
                // scan adopts on its own and the commands that do need it refuse with the same advice. Diagnostic only now.
                assert(lines.Count == before, $"a host mismatch prints nothing at the default level ({string.Join(" | ", lines.Skip(before))})");
                StoreLog.SetVerbose(true);
                MachineIdentity.EnsureMinted(dal, "MAX1", now);
                StoreLog.SetVerbose(false);
                var diag = lines.Skip(before).Where(l => l.Contains(" DEBUG ")).ToList();
                assert(diag.Count == 1 && diag[0].Contains("ATOM") && diag[0].Contains("MAX1"),
                    $"under --audit the mismatch is one diagnostic line naming both hosts ({(diag.Count > 0 ? diag[0] : "<none>")})");
                bool refused = false;
                try { m3.ForWrites(dal, adopt: false, now); } catch (StoreException) { refused = true; }
                assert(refused, "writes as this machine are refused on a host mismatch without --machine-adopt");
                var w3 = m3.ForWrites(dal, adopt: true, now);
                assert(w3 != m1.Id && w3.Length == 36 && dal.Count("machine") == 2 && db.ExecScalar($"SELECT hostname FROM machine WHERE id = '{w3}';") == "MAX1",
                    $"#2 --machine-adopt mints a machine row for the CURRENT host and writes go under it, not under the minting machine ({dal.Count("machine")} machines)");
                assert(m3.ForWrites(dal, adopt: true, now) == w3 && dal.Count("machine") == 2, "adopting twice reuses the host's machine row");

                long r1 = dal.EnsureRoot(m1.Id, @"D:\Music", "primary", now);
                long r2 = dal.EnsureRoot(m1.Id, @"D:\Music", "primary", now);
                long r3 = dal.EnsureRoot(m1.Id, @"X:\Library", "mirror", now);
                assert(r1 == r2 && r3 != r1 && dal.Count("library_root") == 2, "EnsureRoot is idempotent per (machine, path) and distinct per path");

                // Codex review 2026-10-20 #6: after --machine-adopt the adopting host's roots live under ITS machine row, but default
                // export and the scan selected roots by the creating machine's id, so the host's own library was left out (and a scan
                // whose catalog named it refused "root-unresolved").
                long rB = dal.EnsureRoot(w3, @"E:\Music", "primary", now);
                var libB = dal.LibraryRoots(m3.LibraryMachineId(dal));
                assert(libB.Count == 1 && libB[0].Id == rB, $"#6 on the adopting host the library is that host's primary roots, E:\\Music, not the creating machine's ({string.Join(", ", libB.Select(x => x.Path))})");
                var libA = dal.LibraryRoots(m1.LibraryMachineId(dal));
                assert(libA.Count == 1 && libA[0].Id == r1, $"on the creating host the library is still its own roots ({string.Join(", ", libA.Select(x => x.Path))})");
                var mirror = MachineIdentity.EnsureMinted(dal, "MIRRORBOX", now);
                assert(dal.LibraryRoots(mirror.LibraryMachineId(dal)).Count == 1 && dal.LibraryRoots(mirror.LibraryMachineId(dal))[0].Id == r1,
                    "a host that never adopted (a metadata mirror) still reads the creating machine's library");
            }
            finally { StoreLog.Sink = oldSink; StoreLog.Threshold = oldThr; db.Dispose(); try { Directory.Delete(dir, true); } catch { } }
        }

        // ---------------------------------------------------------------- entity resolution
        static void TestEntityResolver(Action<bool, string> assert)
        {
            var db = FreshStore(out var dir);
            try
            {
                using var dal = new StoreDal(db);   // disposed before db: an undisposed DAL kept the file open and left the temp dir behind
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

                // #4a: an OLDER scheme arriving for a path must never demote the newer primary.
                const string fr2 = "3333333333333333333333333333333333333333333333333333333333333333";
                const string tl2 = "4444444444444444444444444444444444444444444444444444444444444444";
                var q1 = EntityResolver.Resolve(dal, fr2, FacetKinds.ShaFrames, root, "prec/x.flac", "import:new", now);
                dal.UpsertLocation(q1.EntityId, root, "prec/x.flac", "prec/x.flac", @"D:\Music\prec\x.flac", now, now);
                var q2 = EntityResolver.Resolve(dal, tl2, FacetKinds.ShaTaglibRegion, root, "prec/x.flac", "import:old-mirror", now);
                assert(q2.EntityId == q1.EntityId
                    && dal.HasFacet(q1.EntityId, FacetKinds.ShaFrames, fr2, out var pfr) && pfr
                    && dal.HasFacet(q1.EntityId, FacetKinds.ShaTaglibRegion, tl2, out var ptl) && !ptl,
                    "#4a a taglib-region sha arriving for a path keyed by a frames sha is kept NON-primary; the frames sha stays primary");

                long loc1 = dal.UpsertLocation(r1.EntityId, root, "Tool/a.flac", "tool/a.flac", @"D:\Music\Tool\a.flac", "2026-01-01T00:00:00.0000000Z", now);
                long loc2 = dal.UpsertLocation(r1.EntityId, root, "Tool/a.flac", "tool/a.flac", @"D:\Music\Tool\a.flac", "2026-02-02T00:00:00.0000000Z", now);
                assert(loc1 == loc2 && dal.Count("track_location") == 5, "UpsertLocation on the same (root, canonical) updates in place, same id");
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
            "value", "segment", "artifact", "track_content", "track_copy",
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
                    assert(db.ExecScalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='track_feature';") == "0", "v2: the per-location wide row is gone (split into track_content + track_copy)");
                    assert(db.ExecScalar("SELECT count(*) FROM pragma_table_info('value') WHERE name = 'segment_id';") == "1", "v2: value has segment_id (values can hang off a segment)");
                    assert(db.ExecScalar("SELECT count(*) FROM pragma_table_info('conflict') WHERE name IN ('field','run_a','run_b','value_a','value_b');") == "5", "v2: conflict carries field, run_a/run_b, value_a/value_b");

                    SchemaMigrator.Apply(db, "selftest-again");
                    assert(SchemaMigrator.ReadVersion(db) == StoreSchema.CurrentVersion && db.ExecScalar("SELECT count(*) FROM schema_version;") == StoreSchema.CurrentVersion.ToString(),
                        $"Apply is idempotent on a current store (one row per applied version = {StoreSchema.CurrentVersion}, no extra row, no error)");

                    // The drift test: every key the catalog writer emits has a column, and every column is an emitted key.
                    var emitted = EmittedCatalogKeys();
                    assert(emitted.Count > 100, $"writer emits a populated entry with >100 flattened keys (got {emitted.Count})");
                    // v2: the keys are split over two tables — content (once per media) and copy (per location).
                    var contentCols = new HashSet<string>(StringComparer.Ordinal);
                    using (var st = db.Prepare("PRAGMA table_info(track_content);"))
                        while (st.Step()) contentCols.Add(st.GetString(1)!);
                    var copyCols = new HashSet<string>(StringComparer.Ordinal);
                    using (var st = db.Prepare("PRAGMA table_info(track_copy);"))
                        while (st.Step()) copyCols.Add(st.GetString(1)!);
                    foreach (var meta in StoreSchema.TrackContentMetaColumns) contentCols.Remove(meta);
                    foreach (var meta in StoreSchema.TrackCopyMetaColumns) copyCols.Remove(meta);
                    var overlap = new List<string>(); foreach (var c in contentCols) if (copyCols.Contains(c)) overlap.Add(c);
                    assert(overlap.Count == 0, "v2: no key lives in both tables" + (overlap.Count == 0 ? "" : " — " + string.Join(", ", overlap)));
                    var columns = new HashSet<string>(contentCols, StringComparer.Ordinal); columns.UnionWith(copyCols);
                    columns.Add("lastModified");   // lives on track_location.last_modified_utc by design
                    var missing = new List<string>();
                    foreach (var k in emitted) if (!columns.Contains(k)) missing.Add(k);
                    assert(missing.Count == 0, "DRIFT: every emitted catalog key has a column in track_content or track_copy" + (missing.Count == 0 ? "" : " — missing: " + string.Join(", ", missing)));
                    var dead = new List<string>();
                    // A column the writer does not emit is dead UNLESS the emission registry (FieldPolicy) is what
                    // cuts it: trackId and bpmHistogram are TrackFeatures fields the store still captures.
                    foreach (var c in columns) if (!emitted.Contains(c) && !FieldPolicy.IsExcluded(c) && c != "lastModified") dead.Add(c);
                    assert(dead.Count == 0, "DRIFT: every content/copy column is an emitted catalog key" + (dead.Count == 0 ? "" : " — dead: " + string.Join(", ", dead)));
                    // The mapper's own split must be the DDL's split (the C4 pin, both directions).
                    var mapperContent = new HashSet<string>(TrackEntryMapper.ContentColumnNames, StringComparer.Ordinal);
                    var mapperCopy = new HashSet<string>(TrackEntryMapper.CopyColumnNames, StringComparer.Ordinal);
                    assert(mapperContent.SetEquals(contentCols), "mapper content columns == track_content DDL columns");
                    assert(mapperCopy.SetEquals(copyCols), "mapper copy columns == track_copy DDL columns");
                    assert(mapperCopy.Contains("smfmScores") && mapperCopy.Contains("fingerprint_v1_fileSize") && mapperCopy.Contains("artist") && mapperCopy.Contains("analysisDuration"),
                        "copy = tags as found in the copy, fingerprint_v1_*, SMFM (tag-sourced), analysisDuration, fileMd5");
                    assert(mapperContent.Contains("bpm") && mapperContent.Contains("mfcc") && mapperContent.Contains("audioStreamSha256") && mapperContent.Contains("bitUsage_effectiveBits") && mapperContent.Contains("hfEnergyRatio"),
                        "content = every Essentia descriptor, bitUsage, HF, the content hash itself");

                    // Noise rule edge cases (review minor): zero, negatives, NaN, vectors.
                    int kb = TrackEntryMapper.ContentIndexOf("bpm"), km = TrackEntryMapper.ContentIndexOf("mfcc"), kk = TrackEntryMapper.ContentIndexOf("key");
                    assert(kb >= 0 && km >= 0 && kk >= 0, "ContentIndexOf finds content columns");
                    assert(TrackEntryMapper.ContentEqualWithinNoise(kb, 0.0, -0.0) && TrackEntryMapper.ContentEqualWithinNoise(kb, 0.0, 1e-9), "noise: 0 vs -0 and 0 vs 1e-9 are equal (absolute floor)");
                    assert(TrackEntryMapper.ContentEqualWithinNoise(kb, -100.0, -100.5) && !TrackEntryMapper.ContentEqualWithinNoise(kb, -100.0, -102.0), "noise: negatives compare by magnitude (0.5% equal, 2% not)");
                    assert(TrackEntryMapper.ContentEqualWithinNoise(kb, double.NaN, double.NaN) && !TrackEntryMapper.ContentEqualWithinNoise(kb, double.NaN, 1.0), "noise: NaN equals NaN (no perpetual conflict), NaN never equals a number");
                    assert(!TrackEntryMapper.ContentEqualWithinNoise(km, new[] { 1.0, 2.0, 3.0 }, new[] { 1.0, 2.0 }) && TrackEntryMapper.ContentEqualWithinNoise(km, new[] { 1.0, 2.0, 3.0 }, new[] { 1.0, 2.004, 3.0 }) && !TrackEntryMapper.ContentEqualWithinNoise(km, new[] { 1.0, 2.0, 3.0 }, new[] { 1.0, 2.1, 3.0 }),
                        "noise: vectors of different length differ; element-wise 0.2% equal, 5% not");
                    assert(!TrackEntryMapper.ContentEqualWithinNoise(kk, "A", "a") && TrackEntryMapper.ContentEqualWithinNoise(kk, "A", "A") && !TrackEntryMapper.ContentEqualWithinNoise(kk, null, "A"),
                        "noise: text is exact and case-sensitive; null only equals null");

                    // Refuses a store newer than the exe.
                    db.Exec("INSERT INTO schema_version(version, applied_utc, truedat_version) VALUES (999, '2099-01-01T00:00:00Z', 'future');");
                    bool refused = false; string? msg = null;
                    try { SchemaMigrator.Apply(db, "selftest"); } catch (StoreException ex) { refused = true; msg = ex.Message; }
                    assert(refused && msg != null && msg.Contains("999") && msg.IndexOf("newer", StringComparison.OrdinalIgnoreCase) >= 0,
                        $"a store at schema 999 is refused with a sentence naming the versions ({msg})");
                }

                // ---- v1 -> v2 migration: the wide row splits; the first-imported copy's content wins; disagreements become conflicts.
                var v1Path = Path.Combine(dir, "v1.db");
                using (var db = SqliteDb.Open(v1Path, create: true))
                {
                    SchemaMigrator.Apply(db, "selftest-v1", targetVersion: 1);
                    assert(SchemaMigrator.ReadVersion(db) == 1 && db.ExecScalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='track_feature';") == "1", "a v1 store can still be built for the migration test (targetVersion seam)");
                    var now = "2026-10-12T10:00:00.0000000Z";
                    db.Exec($"INSERT INTO machine(id, hostname, created_utc) VALUES ('m1', 'ATOM', '{now}');");
                    db.Exec($"INSERT INTO library_root(id, machine_id, path, role, created_utc) VALUES (1, 'm1', 'D:\\Music', 'primary', '{now}');");
                    db.Exec($"INSERT INTO file_entity(id, created_utc) VALUES ('e1', '{now}');");
                    db.Exec($"INSERT INTO file_entity(id, created_utc) VALUES ('e2', '{now}');");
                    db.Exec($"INSERT INTO track_location(id, entity_id, root_id, rel_path, canonical_path, display_path, last_modified_utc, last_seen_utc, state) VALUES (10, 'e1', 1, 'a/one.flac', 'a/one.flac', 'D:\\Music\\a\\one.flac', '{now}', '{now}', 'present');");
                    db.Exec($"INSERT INTO track_location(id, entity_id, root_id, rel_path, canonical_path, display_path, last_modified_utc, last_seen_utc, state) VALUES (11, 'e1', 1, 'b/one copy.flac', 'b/one copy.flac', 'D:\\Music\\b\\one copy.flac', '{now}', '{now}', 'present');");
                    db.Exec($"INSERT INTO track_location(id, entity_id, root_id, rel_path, canonical_path, display_path, last_modified_utc, last_seen_utc, state) VALUES (12, 'e2', 1, 'c/two.mp3', 'c/two.mp3', 'D:\\Music\\c\\two.mp3', '{now}', '{now}', 'present');");
                    // e1 has two copies: the second differs by run-to-run NOISE on spectralCentroid (1793.4 vs 1793.7, 0.017%)
                    // and by a REAL disagreement on bpm (120 vs 135). e2 has one copy.
                    db.Exec($"INSERT INTO track_feature(location_id, content_key, content_key_kind, updated_utc, artist, bpm, spectralCentroid, analysisDuration, smfmBpm, audioStreamSha256) VALUES (10, 'sha1', '{FacetKinds.ShaFrames}', '{now}', 'Tool', 120.0, 1793.4, 1.5, 118, 'sha1');");   // values at their declared precision (T04: the typed v3 column holds spectralCentroid at 1 dp)
                    db.Exec($"INSERT INTO track_feature(location_id, content_key, content_key_kind, updated_utc, artist, bpm, spectralCentroid, analysisDuration, smfmBpm, audioStreamSha256) VALUES (11, 'sha1', '{FacetKinds.ShaFrames}', '{now}', 'TOOL', 135.0, 1793.7, 2.5, NULL, 'sha1');");
                    db.Exec($"INSERT INTO track_feature(location_id, content_key, content_key_kind, updated_utc, artist, bpm, analysisDuration, audioStreamSha256) VALUES (12, 'sha2', '{FacetKinds.ShaFrames}', '{now}', 'Zep', 90.0, 3.0, 'sha2');");
                }
                var v1Now = DateTime.ParseExact("2026-10-12T10:00:00.0000000Z", "o", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
                // A failure mid-migration leaves the v1 store exactly as it was (one transaction).
                using (var db = SqliteDb.Open(v1Path, create: false))
                {
                    SchemaV2Migration.FailAfterMoveForTest = true;
                    bool threw = false;
                    try { SchemaMigrator.Apply(db, "selftest-v2"); } catch (Exception) { threw = true; }
                    finally { SchemaV2Migration.FailAfterMoveForTest = false; }
                    assert(threw && SchemaMigrator.ReadVersion(db) == 1 && db.ExecScalar("SELECT count(*) FROM track_feature;") == "3"
                        && db.ExecScalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='track_content';") == "0",
                        $"a migration that fails after moving rows rolls back whole: still v1, 3 feature rows, no v2 tables (v{SchemaMigrator.ReadVersion(db)})");
                }
                using (var db = SqliteDb.Open(v1Path, create: false))
                {
                    SchemaMigrator.Apply(db, "selftest-v2");
                    assert(SchemaMigrator.ReadVersion(db) == StoreSchema.CurrentVersion, $"v1 store migrates forward through v2 and v3 to the current schema in one Apply (v{SchemaMigrator.ReadVersion(db)})");
                    assert(db.ExecScalar("SELECT count(*) FROM track_content;") == "2" && db.ExecScalar("SELECT count(*) FROM track_copy;") == "3",
                        $"split: 2 content rows (one per media) and 3 copy rows (one per location) ({db.ExecScalar("SELECT count(*) FROM track_content;")}/{db.ExecScalar("SELECT count(*) FROM track_copy;")})");
                    assert(db.ExecScalar("SELECT bpm FROM track_content WHERE entity_id = 'e1';") == "1200", $"the first-imported copy's content values win (bpm 120 from location 10; v3 stores it as 1200, ×10 at 1 dp: {db.ExecScalar("SELECT bpm FROM track_content WHERE entity_id = 'e1';")})");
                    assert(db.ExecScalar("SELECT content_key FROM track_content WHERE entity_id = 'e1';") == "sha1" && db.ExecScalar("SELECT audioStreamSha256 FROM track_content WHERE entity_id = 'e1';") == "sha1", "content key and the hash itself sit on the content row");
                    assert(db.ExecScalar("SELECT artist FROM track_copy WHERE location_id = 11;") == "TOOL" && db.ExecScalar("SELECT analysisDuration FROM track_copy WHERE location_id = 11;") == "2500000" && db.ExecScalar("SELECT smfmBpm FROM track_copy WHERE location_id = 10;") == "118000000",
                        $"per-copy facts (tags, analysisDuration, SMFM) stay with their own copy — v3 scaled at the store's six places: 2.5 s -> 2500000, 118 bpm -> 118000000 ({db.ExecScalar("SELECT analysisDuration FROM track_copy WHERE location_id = 11;")}, {db.ExecScalar("SELECT smfmBpm FROM track_copy WHERE location_id = 10;")})");
                    assert(db.ExecScalar("SELECT count(*) FROM conflict WHERE kind = 'content-disagree-at-split';") == "1", $"ONE conflict: bpm 120 vs 135 is a disagreement; spectralCentroid 1793.4 vs 1793.7 is run-to-run noise and is not ({db.ExecScalar("SELECT count(*) FROM conflict;")})");
                    assert(db.ExecScalar("SELECT field FROM conflict WHERE kind = 'content-disagree-at-split';") == "bpm" && db.ExecScalar("SELECT value_a FROM conflict WHERE kind = 'content-disagree-at-split';") == "120" && db.ExecScalar("SELECT value_b FROM conflict WHERE kind = 'content-disagree-at-split';") == "135",
                        $"the conflict names the field and both values ({db.ExecScalar("SELECT field || ' ' || value_a || ' ' || value_b FROM conflict WHERE kind = 'content-disagree-at-split';")})");
                    assert(db.ExecScalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='track_feature';") == "0", "the wide row is dropped after the split");
                    using (var dal = new StoreDal(db))
                    {
                        var back = new List<(string, TrackEntry)>(dal.ReadEntries(1));
                        assert(back.Count == 3, $"ReadEntries joins copy and content: 3 entries ({back.Count})");
                        var two = back.Find(x => x.Item1.EndsWith("one copy.flac"));
                        assert(two.Item2 != null && two.Item2.Features!.Bpm == 120.0 && two.Item2.Features.Artist == "TOOL" && two.Item2.AnalysisDurationSecs == 2.5,
                            "a non-primary copy exports the media's single description (bpm 120) with its own copy facts (artist TOOL, analysisDuration 2.5)");
                        var merged = dal.NonPrimaryCopyPaths(1);
                        assert(merged.Count == 1 && merged.Contains(@"D:\Music\b\one copy.flac"), $"the DAL names the non-primary copies of multi-copy media ({merged.Count})");
                    }
                }
                // The round-trip gate against a catalog holding the v1 values: the merged copy differs (by design, it now
                // carries its media's description) and is reported as merged, not as unexplained; the gate is EQUAL.
                var v1Catalog = Path.Combine(dir, "v1-mbxmoods.json");
                var v1Entries = new Dictionary<string, TrackEntry>(PathComparer.Instance)
                {
                    [@"D:\Music\a\one.flac"] = new TrackEntry { AudioStreamSha256 = "sha1", AnalysisDurationSecs = 1.5, LastModified = v1Now, Features = new TrackFeatures { Artist = "Tool", Bpm = 120.0, SpectralCentroid = 1793.4, SmfmBpm = 118 } },
                    [@"D:\Music\b\one copy.flac"] = new TrackEntry { AudioStreamSha256 = "sha1", AnalysisDurationSecs = 2.5, LastModified = v1Now, Features = new TrackFeatures { Artist = "TOOL", Bpm = 135.0, SpectralCentroid = 1793.7 } },
                    [@"D:\Music\c\two.mp3"] = new TrackEntry { AudioStreamSha256 = "sha2", AnalysisDurationSecs = 3.0, LastModified = v1Now, Features = new TrackFeatures { Artist = "Zep", Bpm = 90.0 } },
                };
                WriteCatalog(v1Catalog, v1Entries);
                var rt = ExportCommand.Roundtrip(v1Path, v1Catalog, null);
                assert(rt.Same == 2 && rt.DifferentMerged == 1 && rt.Different == 0 && rt.Equal,
                    $"round-trip after the split: 2 identical, 1 merged copy (expected), 0 unexplained → EQUAL ({rt.Same}/{rt.DifferentMerged}/{rt.Different}, equal {rt.Equal})");
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

        // ---------------------------------------------------------------- canonical precision (operator, 2026-10-12)
        /// <summary>T03 (F7): one registry of field precision. (a) schema walk both ways — every numeric store column has an entry of the
        /// matching shape and every entry names a column; (b) the pin — the parser's decimal-place literals as they stood when the
        /// registry replaced them (change the registry and this list together, on purpose); (c) behaviour — the real parser rounds a
        /// value at the registry's dp, so the registry is read, not merely declared.</summary>
        static void TestFieldPrecision(Action<bool, string> assert)
        {
            var missing = new List<string>(); var cols = new HashSet<string>(StringComparer.Ordinal); int kindMismatch = 0; string firstMismatch = "";
            foreach (var (name, kind) in TrackEntryMapper.NumericColumns())
            {
                cols.Add(name);
                if (!FieldPrecision.TryGet(name, out var spec)) { missing.Add(name + ":" + kind); continue; }
                bool kindOk = kind switch
                {
                    "Real" => spec.Shape == FieldPrecision.Shape.Fixed,
                    "Int" => spec.Shape == FieldPrecision.Shape.Int,
                    "VecF64" => spec.Shape == FieldPrecision.Shape.FixedVec || spec.Shape == FieldPrecision.Shape.SparseFixedVec,   // T04a: the sparse layout is a fixed-dp vector in memory
                    "VecI32" => spec.Shape == FieldPrecision.Shape.IntVec,
                    _ => false
                };
                if (!kindOk) { kindMismatch++; if (firstMismatch == "") firstMismatch = $"{name}: mapper {kind}, registry {spec}"; }
            }
            assert(missing.Count == 0, $"FieldPrecision: every numeric store column has an entry ({missing.Count} missing: {string.Join(", ", missing.Take(8))}{(missing.Count > 8 ? ", …" : "")})");
            assert(kindMismatch == 0, $"FieldPrecision: every entry's shape matches the mapper's column kind ({kindMismatch} mismatches; first: {firstMismatch})");
            var extra = FieldPrecision.Names.Where(n => !cols.Contains(n)).ToList();
            assert(extra.Count == 0, $"FieldPrecision: every registry entry names a store column ({extra.Count} extra: {string.Join(", ", extra)})");

            // (b) the pin — the literals as measured 2026-10-15 before T03 moved them: Opt/OptN defaults 4; dB/LU 2; Hz 1; tiny spectral 6;
            // spectralDecrease 12; vectors 4; writer-side analysisDuration 1, smfmBpm 3; ints as ints.
            var pin = new (string Name, string Spec)[]
            {
                ("trackId","Int"), ("bitUsage_lowestNonZeroBit","Int"), ("bitUsage_samplesAnalyzed","Int"), ("smfmChannel","Int"),
                ("fingerprint_v1_fileSize","Int"), ("fingerprint_v1_durationMs","Int"), ("fingerprint_v1_sampleRate","Int"), ("fingerprint_v1_channels","Int"),
                ("fingerprint_v1_bitDepth","Int"), ("fingerprint_v1_bitrate","Int"), ("fingerprint_v1_mp3LameTag_infoTagRevision","Int"), ("fingerprint_v1_mp3LameTag_vbrMethodCode","Int"),
                ("fingerprint_v1_mp3LameTag_lowpassHz","Int"), ("fingerprint_v1_mp3LameTag_encoderDelay","Int"), ("fingerprint_v1_mp3LameTag_encoderPadding","Int"), ("fingerprint_v1_mp3LameTag_musicCrc","Int"),
                ("bpm","Fixed(1)"), ("spectralCentroid","Fixed(1)"), ("spectralRolloff","Fixed(1)"), ("bpmFirstPeak","Fixed(1)"), ("bpmSecondPeak","Fixed(1)"), ("spectralCentroidStdev","Fixed(1)"), ("analysisDuration","Fixed(1)"),
                ("loudness","Fixed(2)"), ("onsetRate","Fixed(2)"), ("dynamicRange","Fixed(2)"), ("loudnessMomentary","Fixed(2)"), ("loudnessShortTerm","Fixed(2)"), ("replayGain","Fixed(2)"),
                ("spectralComplexity","Fixed(2)"), ("hfc","Fixed(2)"), ("beatsLoudness","Fixed(2)"), ("hpcpCrest","Fixed(2)"), ("tuningFrequency","Fixed(2)"), ("bitUsage_effectiveBits","Fixed(2)"), ("hfSpectralStructure_peakToMean","Fixed(2)"),
                ("smfmBpm","Fixed(3)"),
                ("spectralFlux","Fixed(4)"), ("danceability","Fixed(4)"), ("dissonance","Fixed(4)"), ("pitchSalience","Fixed(4)"), ("chordsChangesRate","Fixed(4)"),
                ("silenceRate20dB","Fixed(4)"), ("silenceRate30dB","Fixed(4)"), ("silenceRate60dB","Fixed(4)"), ("spectralEntropy","Fixed(4)"), ("spectralKurtosis","Fixed(4)"), ("spectralSkewness","Fixed(4)"), ("spectralSpread","Fixed(4)"), ("spectralStrongPeak","Fixed(4)"),
                ("barkCrest","Fixed(4)"), ("barkFlatness","Fixed(4)"), ("barkKurtosis","Fixed(4)"), ("barkSkewness","Fixed(4)"), ("barkSpread","Fixed(4)"),
                ("erbCrest","Fixed(4)"), ("erbFlatness","Fixed(4)"), ("erbKurtosis","Fixed(4)"), ("erbSkewness","Fixed(4)"), ("erbSpread","Fixed(4)"),
                ("melCrest","Fixed(4)"), ("melFlatness","Fixed(4)"), ("melKurtosis","Fixed(4)"), ("melSkewness","Fixed(4)"), ("melSpread","Fixed(4)"),
                ("chordsStrength","Fixed(4)"), ("hpcpEntropy","Fixed(4)"), ("keyVotes_krumhansl_strength","Fixed(4)"), ("keyVotes_temperley_strength","Fixed(4)"), ("keyVotes_edma_strength","Fixed(4)"),
                ("bpmFirstPeakWeight","Fixed(4)"), ("bpmFirstPeakSpread","Fixed(4)"), ("bpmSecondPeakWeight","Fixed(4)"), ("bpmSecondPeakSpread","Fixed(4)"), ("chordsNumberRate","Fixed(4)"),
                ("tuningEqualTemperedDeviation","Fixed(4)"), ("tuningDiatonicStrength","Fixed(4)"), ("tuningNontemperedEnergyRatio","Fixed(4)"), ("averageLoudness","Fixed(4)"), ("dynamicComplexity","Fixed(4)"),
                ("beatsIntervalMean","Fixed(4)"), ("beatsIntervalStdev","Fixed(4)"), ("beatsIntervalMin","Fixed(4)"), ("beatsIntervalMax","Fixed(4)"),
                ("dissonanceStdev","Fixed(4)"), ("pitchSalienceStdev","Fixed(4)"), ("hpcpEntropyStdev","Fixed(4)"), ("zeroCrossingRateStdev","Fixed(4)"),
                ("bitUsage_bottomBitActivity","Fixed(4)"), ("hfSpectralStructure_flatness","Fixed(4)"), ("hfSpectralStructure_imagingSymmetry","Fixed(4)"),
                ("spectralFlatness","Fixed(4)"),   // derived field: effective precision is the derive step's 4, not the initializer's old 6 (review finding 4)
                ("zeroCrossingRate","Fixed(6)"), ("spectralRms","Fixed(6)"), ("spectralEnergy","Fixed(6)"), ("spectralEnergyLow","Fixed(6)"),
                ("spectralEnergyMidLow","Fixed(6)"), ("spectralEnergyMidHigh","Fixed(6)"), ("spectralEnergyHigh","Fixed(6)"), ("spectralEnergyStdev","Fixed(6)"), ("hfEnergyRatio","Fixed(6)"),
                ("spectralDecrease","Fixed(12)"),
                ("mfcc","FixedVec(4)"), ("chordsHistogram","FixedVec(4)"), ("hpcp12","FixedVec(4)"), ("thpcp12","FixedVec(4)"), ("mfccStdev","FixedVec(4)"),
                ("spectralContrastCoeffs","FixedVec(4)"), ("spectralContrastValleys","FixedVec(4)"), ("beatsLoudnessBandRatio","FixedVec(4)"), ("bpmHistogram","SparseFixedVec(4)"), ("gfcc","FixedVec(4)"),   // T04a: sparse storage, same 4 dp
                ("smfmScores","IntVec"),
            };
            int pinFail = 0; string firstPinFail = "";
            foreach (var p in pin)
            {
                var got = FieldPrecision.TryGet(p.Name, out var s) ? s.ToString() : "absent";
                if (got != p.Spec) { pinFail++; if (firstPinFail == "") firstPinFail = $"{p.Name}: pinned {p.Spec}, registry {got}"; }
            }
            assert(pinFail == 0, $"FieldPrecision pin: every pinned field has its 2026-10-15 precision ({pinFail} differ; first: {firstPinFail})");
            assert(pin.Length == FieldPrecision.Count, $"FieldPrecision pin covers the whole registry ({pin.Length} pinned, {FieldPrecision.Count} entries)");
            // Shared call sites (review finding 5): WriteKeyVote renders all three profiles with the edma entry and FoldHpcp36To12 serves
            // thpcp12 with the hpcp12 entry, so those entries must stay equal or the sharing silently stops being true.
            assert(FieldPrecision.Dp("keyVotes_krumhansl_strength") == FieldPrecision.Dp("keyVotes_edma_strength") && FieldPrecision.Dp("keyVotes_temperley_strength") == FieldPrecision.Dp("keyVotes_edma_strength"),
                "FieldPrecision: the three key-vote strengths share one precision (the writer renders them through one entry)");
            assert(FieldPrecision.Dp("hpcp12") == FieldPrecision.Dp("thpcp12"), "FieldPrecision: hpcp12 and thpcp12 share one precision (one fold serves both)");
            // T04: the STORE precision equals the writer's for every parser field; only the two unrounded writer-side fields hold more.
            int storeWider = 0; string widerNames = "";
            foreach (var n in FieldPrecision.Names) { FieldPrecision.TryGet(n, out var sp); if (sp.StoreDp != sp.Dp) { storeWider++; widerNames += n + " "; } }
            assert(storeWider == 2 && FieldPrecision.StoreDp("analysisDuration") == 6 && FieldPrecision.Dp("analysisDuration") == 1 && FieldPrecision.StoreDp("smfmBpm") == 6 && FieldPrecision.Dp("smfmBpm") == 3,
                $"FieldPrecision: exactly analysisDuration (1 -> 6) and smfmBpm (3 -> 6) hold more places in the store than the writer renders ({storeWider}: {widerNames.Trim()})");
            // Typo guard (review finding 2): Opt resolves the field name BEFORE the null test, so parsing an EMPTY document touches every
            // OptN key — a mistyped name throws here instead of on the first real track that carries the field.
            TrackFeatures? empty = null; string emptyErr = "";
            try { empty = Program.ParseEssentiaOutput("{}"); } catch (Exception ex) { emptyErr = ex.Message; }
            assert(empty != null && emptyErr == "", $"FieldPrecision: parsing an empty document resolves every registered name without throwing ({(emptyErr == "" ? "ok" : emptyErr)})");

            // (c) behaviour: the real parser, a real (minimal) extractor document, values chosen so the rounding is visible.
            const string json = "{\"rhythm\":{\"bpm\":120.123456,\"onset_rate\":3.14159,\"danceability\":1.23456789,\"beats_loudness\":{\"mean\":0.123456}},"
                + "\"tonal\":{\"key_edma\":{\"key\":\"C\",\"scale\":\"major\",\"strength\":0.987654321},\"chords_changes_rate\":0.0456789,\"chords_strength\":{\"mean\":0.5},\"hpcp_crest\":{\"mean\":12.3456},\"hpcp_entropy\":{\"mean\":2.0},\"tuning_frequency\":440.123456},"
                + "\"lowlevel\":{\"spectral_centroid\":{\"mean\":1234.56789},\"spectral_flux\":{\"mean\":0.123456789},\"average_loudness\":0.9,\"dissonance\":{\"mean\":0.4567891},\"pitch_salience\":{\"mean\":0.5},"
                + "\"zerocrossingrate\":{\"mean\":0.0123456789},\"spectral_rms\":{\"mean\":0.00123456789},\"spectral_rolloff\":{\"mean\":4321.98765},\"spectral_decrease\":{\"mean\":-1.23456789012345e-9},"
                + "\"spectral_energy\":{\"mean\":0.0001234567},\"loudness_ebu128\":{\"integrated\":-14.123456,\"momentary\":{\"mean\":-13.456789},\"loudness_range\":7.891234},"
                + "\"barkbands_flatness_db\":{\"mean\":0.1},\"erbbands_flatness_db\":{\"mean\":0.2},\"melbands_flatness_db\":{\"mean\":0.3}}}";
            var f = Program.ParseEssentiaOutput(json);
            assert(f != null, "FieldPrecision behaviour: the parser accepts the minimal document");
            if (f != null)
            {
                assert(f.Bpm == 120.1 && f.SpectralCentroid == 1234.6 && f.SpectralRolloff == 4322.0, $"parser rounds 1-dp fields from the registry (bpm {f.Bpm}, centroid {f.SpectralCentroid}, rolloff {f.SpectralRolloff})");
                assert(f.OnsetRate == 3.14 && f.LoudnessMomentary == -13.46 && f.TuningFrequency == 440.12 && f.HpcpCrest == 12.35, $"parser rounds 2-dp fields from the registry (onset {f.OnsetRate}, momentary {f.LoudnessMomentary}, tuning {f.TuningFrequency}, hpcpCrest {f.HpcpCrest})");
                assert(f.Danceability == 1.2346 && f.SpectralFlux == 0.1235 && f.KeyVoteEdma?.Strength == 0.9877, $"parser rounds 4-dp fields from the registry (danceability {f.Danceability}, flux {f.SpectralFlux}, edma {f.KeyVoteEdma?.Strength})");
                assert(f.ZeroCrossingRate == 0.012346 && f.SpectralRms == 0.001235 && f.SpectralEnergy == 0.000123, $"parser rounds 6-dp fields from the registry (zcr {f.ZeroCrossingRate}, rms {f.SpectralRms}, energy {f.SpectralEnergy})");
                assert(f.SpectralDecrease == -1.235e-9, $"parser rounds spectralDecrease at 12 dp from the registry: 12 places keep 1e-9 values to 4 figures ({f.SpectralDecrease:R})");
            }
        }

        /// <summary>T03 review finding 3: wiring proof for EVERY routed site, not one. A rich extractor document gives every parser-read
        /// field a long-fraction value; the expectation is Math.Round(raw, dp) with dp taken from the test's own pin table, never from
        /// FieldPrecision — so a call site reverted to a literal that differs from the registry fails here by field name. The writer
        /// half renders a TrackEntry of long fractions through the real catalog writer and checks every writer-side rounding site the
        /// same way.</summary>
        static void TestFieldPrecisionWiring(Action<bool, string> assert)
        {
            int PinDp(string field)
            {
                // the same literals as TestFieldPrecision's pin, kept as a lookup here (a second, independent copy is the point)
                switch (field)
                {
                    case "bpm": case "spectralCentroid": case "spectralRolloff": case "bpmFirstPeak": case "bpmSecondPeak": case "spectralCentroidStdev": case "analysisDuration": return 1;
                    case "loudness": case "onsetRate": case "dynamicRange": case "loudnessMomentary": case "loudnessShortTerm": case "replayGain": case "spectralComplexity": case "hfc":
                    case "beatsLoudness": case "hpcpCrest": case "tuningFrequency": case "bitUsage_effectiveBits": case "hfSpectralStructure_peakToMean": return 2;
                    case "smfmBpm": return 3;
                    case "zeroCrossingRate": case "spectralRms": case "spectralEnergy": case "spectralEnergyLow": case "spectralEnergyMidLow": case "spectralEnergyMidHigh": case "spectralEnergyHigh":
                    case "spectralEnergyStdev": case "hfEnergyRatio": return 6;
                    case "spectralDecrease": return 12;
                    default: return 4;
                }
            }
            // ---- parser half: (extractor path, field) for every OptN/core/array/key-vote site
            var scalar = new (string Path, string Field)[]
            {
                ("rhythm.bpm","bpm"), ("lowlevel.loudness_ebu128.integrated","loudness"), ("lowlevel.spectral_centroid.mean","spectralCentroid"), ("lowlevel.spectral_flux.mean","spectralFlux"),
                ("rhythm.danceability","danceability"), ("rhythm.onset_rate","onsetRate"), ("lowlevel.zerocrossingrate.mean","zeroCrossingRate"), ("lowlevel.spectral_rms.mean","spectralRms"),
                ("lowlevel.dissonance.mean","dissonance"), ("lowlevel.pitch_salience.mean","pitchSalience"), ("tonal.chords_changes_rate","chordsChangesRate"), ("lowlevel.loudness_ebu128.loudness_range","dynamicRange"),
                ("lowlevel.loudness_ebu128.momentary.mean","loudnessMomentary"), ("lowlevel.loudness_ebu128.short_term.mean","loudnessShortTerm"), ("metadata.audio_properties.replay_gain","replayGain"),
                ("lowlevel.silence_rate_20dB.mean","silenceRate20dB"), ("lowlevel.silence_rate_30dB.mean","silenceRate30dB"), ("lowlevel.silence_rate_60dB.mean","silenceRate60dB"),
                ("lowlevel.spectral_rolloff.mean","spectralRolloff"), ("lowlevel.spectral_complexity.mean","spectralComplexity"), ("lowlevel.spectral_entropy.mean","spectralEntropy"),
                ("lowlevel.spectral_kurtosis.mean","spectralKurtosis"), ("lowlevel.spectral_skewness.mean","spectralSkewness"), ("lowlevel.spectral_spread.mean","spectralSpread"), ("lowlevel.spectral_strongpeak.mean","spectralStrongPeak"),
                ("lowlevel.spectral_decrease.mean","spectralDecrease"), ("lowlevel.spectral_energy.mean","spectralEnergy"), ("lowlevel.spectral_energyband_low.mean","spectralEnergyLow"),
                ("lowlevel.spectral_energyband_middle_low.mean","spectralEnergyMidLow"), ("lowlevel.spectral_energyband_middle_high.mean","spectralEnergyMidHigh"), ("lowlevel.spectral_energyband_high.mean","spectralEnergyHigh"),
                ("lowlevel.hfc.mean","hfc"), ("lowlevel.barkbands_crest.mean","barkCrest"), ("lowlevel.barkbands_flatness_db.mean","barkFlatness"), ("lowlevel.barkbands_kurtosis.mean","barkKurtosis"),
                ("lowlevel.barkbands_skewness.mean","barkSkewness"), ("lowlevel.barkbands_spread.mean","barkSpread"), ("lowlevel.erbbands_crest.mean","erbCrest"), ("lowlevel.erbbands_flatness_db.mean","erbFlatness"),
                ("lowlevel.erbbands_kurtosis.mean","erbKurtosis"), ("lowlevel.erbbands_skewness.mean","erbSkewness"), ("lowlevel.erbbands_spread.mean","erbSpread"), ("lowlevel.melbands_crest.mean","melCrest"),
                ("lowlevel.melbands_flatness_db.mean","melFlatness"), ("lowlevel.melbands_kurtosis.mean","melKurtosis"), ("lowlevel.melbands_skewness.mean","melSkewness"), ("lowlevel.melbands_spread.mean","melSpread"),
                ("rhythm.beats_loudness.mean","beatsLoudness"), ("tonal.chords_strength.mean","chordsStrength"), ("tonal.hpcp_crest.mean","hpcpCrest"), ("tonal.hpcp_entropy.mean","hpcpEntropy"),
                ("rhythm.bpm_histogram_first_peak_bpm","bpmFirstPeak"), ("rhythm.bpm_histogram_first_peak_weight","bpmFirstPeakWeight"), ("rhythm.bpm_histogram_first_peak_spread","bpmFirstPeakSpread"),
                ("rhythm.bpm_histogram_second_peak_bpm","bpmSecondPeak"), ("rhythm.bpm_histogram_second_peak_weight","bpmSecondPeakWeight"), ("rhythm.bpm_histogram_second_peak_spread","bpmSecondPeakSpread"),
                ("tonal.chords_number_rate","chordsNumberRate"), ("tonal.tuning_frequency","tuningFrequency"), ("tonal.tuning_equal_tempered_deviation","tuningEqualTemperedDeviation"),
                ("tonal.tuning_diatonic_strength","tuningDiatonicStrength"), ("tonal.tuning_nontempered_energy_ratio","tuningNontemperedEnergyRatio"), ("lowlevel.average_loudness","averageLoudness"),
                ("lowlevel.dynamic_complexity","dynamicComplexity"), ("lowlevel.spectral_centroid.stdev","spectralCentroidStdev"), ("lowlevel.spectral_energy.stdev","spectralEnergyStdev"),
                ("lowlevel.dissonance.stdev","dissonanceStdev"), ("lowlevel.pitch_salience.stdev","pitchSalienceStdev"), ("tonal.hpcp_entropy.stdev","hpcpEntropyStdev"), ("lowlevel.zerocrossingrate.stdev","zeroCrossingRateStdev"),
                ("tonal.key_krumhansl.strength","keyVotes_krumhansl_strength"), ("tonal.key_temperley.strength","keyVotes_temperley_strength"), ("tonal.key_edma.strength","keyVotes_edma_strength"),
            };
            var arrays = new (string Path, string Field, int N)[]
            {
                ("lowlevel.mfcc.mean","mfcc",13), ("tonal.chords_histogram","chordsHistogram",24), ("lowlevel.spectral_contrast_coeffs.mean","spectralContrastCoeffs",6),
                ("lowlevel.spectral_contrast_valleys.mean","spectralContrastValleys",6), ("rhythm.beats_loudness_band_ratio.mean","beatsLoudnessBandRatio",6), ("rhythm.bpm_histogram","bpmHistogram",8), ("lowlevel.gfcc.mean","gfcc",13),
            };
            // long fractions, all below 1 in magnitude so the mapper's six-significant-figure read-back is exact for every dp in play
            double Raw(int i) => 0.123456789 + i * 0.00100001;
            double RawDecrease(int i) => -1.23456789e-9 - i * 1e-12;
            var root = new Dictionary<string, object>(StringComparer.Ordinal);
            void Put(string path, object value)
            {
                var parts = path.Split('.'); var node = root;
                for (int p = 0; p < parts.Length - 1; p++)
                {
                    if (!node.TryGetValue(parts[p], out var child)) { child = new Dictionary<string, object>(StringComparer.Ordinal); node[parts[p]] = child; }
                    node = (Dictionary<string, object>)child;
                }
                node[parts[parts.Length - 1]] = value;
            }
            var rawOf = new Dictionary<string, double>(StringComparer.Ordinal);
            for (int i = 0; i < scalar.Length; i++) { double raw = scalar[i].Field == "spectralDecrease" ? RawDecrease(i) : Raw(i); rawOf[scalar[i].Field] = raw; Put(scalar[i].Path, raw); }
            foreach (var profile in new[] { "krumhansl", "temperley", "edma" }) { Put($"tonal.key_{profile}.key", "C"); Put($"tonal.key_{profile}.scale", "major"); }
            var rawArr = new Dictionary<string, double[]>(StringComparer.Ordinal);
            for (int a = 0; a < arrays.Length; a++) { var v = new double[arrays[a].N]; for (int j = 0; j < v.Length; j++) v[j] = Raw(100 + a * 20 + j); rawArr[arrays[a].Field] = v; Put(arrays[a].Path, v); }
            string json = System.Text.Json.JsonSerializer.Serialize(root);
            var f = Program.ParseEssentiaOutput(json);
            assert(f != null, "FieldPrecision wiring: the parser accepts the rich document");
            if (f == null) return;
            var row = TrackEntryMapper.ToRow(new TrackEntry { Features = f });
            int wrong = 0, missing = 0; string first = "";
            foreach (var s in scalar)
            {
                int k = TrackEntryMapper.ContentIndexOf(s.Field);
                var got = TrackEntryMapper.ContentValue(row, k);
                double expect = Math.Round(rawOf[s.Field], PinDp(s.Field));
                if (got == null) { missing++; if (first == "") first = s.Field + " missing"; }
                else if (!(got is double d) || d != expect) { wrong++; if (first == "") first = $"{s.Field}: parsed {got}, expected {expect} (raw {rawOf[s.Field]:R} at {PinDp(s.Field)} dp)"; }
            }
            foreach (var a in arrays)
            {
                int k = TrackEntryMapper.ContentIndexOf(a.Field);
                var got = TrackEntryMapper.ContentValue(row, k) as double[];
                if (got == null) { missing++; if (first == "") first = a.Field + " missing"; continue; }
                for (int j = 0; j < got.Length; j++)
                {
                    double expect = Math.Round(rawArr[a.Field][j], PinDp(a.Field));
                    if (got[j] != expect) { wrong++; if (first == "") first = $"{a.Field}[{j}]: parsed {got[j]:R}, expected {expect:R}"; break; }
                }
            }
            assert(missing == 0 && wrong == 0, $"FieldPrecision wiring (parser): all {scalar.Length} scalar and {arrays.Length} array sites round at their pinned dp ({missing} missing, {wrong} wrong; first: {first})");

            // ---- writer half: every writer-side rounding site, rendered through the real catalog writer
            var e = new TrackEntry
            {
                AnalysisDurationSecs = 12.3456789,
                Features = new TrackFeatures
                {
                    Bpm = 120, Key = "C", Mode = "major", Mfcc = new double[13],
                    HfEnergyRatio = 0.123456789, HfEnergyMethod = "test",
                    HfSpectralStructure = new HfSpectralStructure { Flatness = 0.234567891, PeakToMean = 3.456789123, ImagingSymmetry = 0.345678912 },
                    SmfmBpm = 128.123456789, SmfmScores = new[] { 1, 2, 3 }, SmfmChannel = 0,
                    KeyVoteKrumhansl = new KeyVote { Key = "C", Scale = "major", Strength = 0.456789123 },
                    KeyVoteTemperley = new KeyVote { Key = "C", Scale = "major", Strength = 0.567891234 },
                    KeyVoteEdma = new KeyVote { Key = "C", Scale = "major", Strength = 0.678912345 },
                    ChordsHistogram = new[] { 0.111111111, 0.222222222 }, Hpcp12 = new[] { 0.333333333, 0.444444444 }, Thpcp12 = new[] { 0.555555555, 0.666666666 },
                    MfccStdev = new[] { 0.777777777, 0.888888888 }, SpectralContrastCoeffs = new[] { 0.123412341 }, SpectralContrastValleys = new[] { 0.234523452 },
                    BeatsLoudnessBandRatio = new[] { 0.345634563 }, BpmHistogram = new[] { 0.456745674 }, Gfcc = new[] { 0.567856785 },
                },
            };
            string written;
            using (var ms = new MemoryStream())
            {
                using (var jw = new System.Text.Json.Utf8JsonWriter(ms)) { jw.WriteStartObject(); Program.WriteTrackEntryForStore(jw, @"D:\m\w.flac", e); jw.WriteEndObject(); }
                written = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
            using (var doc = System.Text.Json.JsonDocument.Parse(written))
            {
                var t = doc.RootElement.GetProperty(@"D:\m\w.flac");
                int wwrong = 0; string wfirst = "";
                void Num(System.Text.Json.JsonElement el, string label, double raw, string field)
                {
                    double expect = Math.Round(raw, PinDp(field));
                    if (el.ValueKind != System.Text.Json.JsonValueKind.Number || el.GetDouble() != expect) { wwrong++; if (wfirst == "") wfirst = $"{label}: wrote {el}, expected {expect:R}"; }
                }
                void Arr(string key, string field, double[] raw)
                {
                    if (!t.TryGetProperty(key, out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array) { wwrong++; if (wfirst == "") wfirst = key + " not written"; return; }
                    for (int j = 0; j < raw.Length; j++) Num(arr[j], $"{key}[{j}]", raw[j], field);
                }
                Num(t.GetProperty("hfEnergyRatio"), "hfEnergyRatio", 0.123456789, "hfEnergyRatio");
                var hs = t.GetProperty("hfSpectralStructure");
                Num(hs.GetProperty("flatness"), "hf.flatness", 0.234567891, "hfSpectralStructure_flatness");
                Num(hs.GetProperty("peakToMean"), "hf.peakToMean", 3.456789123, "hfSpectralStructure_peakToMean");
                Num(hs.GetProperty("imagingSymmetry"), "hf.imagingSymmetry", 0.345678912, "hfSpectralStructure_imagingSymmetry");
                Num(t.GetProperty("smfmBpm"), "smfmBpm", 128.123456789, "smfmBpm");
                Num(t.GetProperty("analysisDuration"), "analysisDuration", 12.3456789, "analysisDuration");
                var kv = t.GetProperty("keyVotes");
                Num(kv.GetProperty("krumhansl").GetProperty("strength"), "keyVotes.krumhansl", 0.456789123, "keyVotes_krumhansl_strength");
                Num(kv.GetProperty("temperley").GetProperty("strength"), "keyVotes.temperley", 0.567891234, "keyVotes_temperley_strength");
                Num(kv.GetProperty("edma").GetProperty("strength"), "keyVotes.edma", 0.678912345, "keyVotes_edma_strength");
                Arr("chordsHistogram", "chordsHistogram", new[] { 0.111111111, 0.222222222 }); Arr("hpcp12", "hpcp12", new[] { 0.333333333, 0.444444444 }); Arr("thpcp12", "thpcp12", new[] { 0.555555555, 0.666666666 });
                Arr("mfccStdev", "mfccStdev", new[] { 0.777777777, 0.888888888 }); Arr("spectralContrastCoeffs", "spectralContrastCoeffs", new[] { 0.123412341 }); Arr("spectralContrastValleys", "spectralContrastValleys", new[] { 0.234523452 });
                Arr("beatsLoudnessBandRatio", "beatsLoudnessBandRatio", new[] { 0.345634563 }); Arr("gfcc", "gfcc", new[] { 0.567856785 });
                assert(wwrong == 0, $"FieldPrecision wiring (writer): every writer-side rounding site renders at its pinned dp ({wwrong} wrong; first: {wfirst})");
                assert(!t.TryGetProperty("bpmHistogram", out _), "FieldPrecision wiring (writer): bpmHistogram is cut by FieldPolicy and not emitted (its registry entry serves the parser and the store only)");
            }
        }

        /// <summary>A stored content value as the mapper reads it back (v3 decode) and formats it — the way tests must read a numeric
        /// column now that the SQL column holds a scaled integer, never raw SELECT text.</summary>
        static string StoredText(OpenStore os, string displayPath, string column)
        {
            var entity = os.Db.ExecScalar("SELECT c.entity_id FROM track_content c JOIN track_location l ON l.entity_id = c.entity_id WHERE l.display_path = '" + displayPath.Replace("'", "''") + "';");
            if (entity == null) return "no-row";
            var back = os.Dal.ReadContent(entity);
            if (back == null) return "no-content";
            int k = TrackEntryMapper.ContentIndexOf(column);
            return TrackEntryMapper.FormatContent(k, back.Values[k]) ?? "null";
        }
        static string Canon1(double v) => Math.Round(v, 1).ToString("R", System.Globalization.CultureInfo.InvariantCulture);   // bpm's declared precision

        /// <summary>T04 (A02): schema v3 — fixed-dp fields as scaled INTEGER, fixed-dp vectors as int32 blobs, the histogram sparse, no
        /// six-figure rounding anywhere on the store path; and T04b — a v2 store migrates forward on open, backup first, every value
        /// preserved exactly (V15), columns typed afterwards.</summary>
        static void TestSchemaV3(Action<bool, string> assert)
        {
            var dir = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "v3-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                // ---- (a) fresh store: typed columns, exact values incl. the named extremes and the 1-ulp case, sparse histogram
                var store = Path.Combine(dir, "truedat.db");
                var entries = FixtureEntries(@"D:\Music");
                var f = entries[@"D:\Music\Tool\a.flac"].Features!;
                f.SpectralSpread = 22500000.1234;                 // wide-range 4 dp (MB3 maximum 2.25e7): 2.25e11 scaled, needs int64
                f.MelKurtosis = 1200000000.5678;                  // 1.2e9 at 4 dp
                f.SpectralDecrease = -0.00000007;                 // 7e-8 at 12 dp -> -70000 scaled
                f.Danceability = 0;                               // zero
                f.DynamicRange = null;                            // null stays null
                f.SpectralEnergyStdev = 0.022454000000000002;     // the 1-ulp case (C6a): net48 renders this double as 17 digits; it IS the double nearest 0.022454
                var hist = new double[250]; hist[10] = 0.1234; hist[120] = 0.5; hist[249] = 0.0001; f.BpmHistogram = hist;
                f.Mfcc = new double[13]; for (int i = 0; i < 13; i++) f.Mfcc[i] = -800.1234 + i;
                ScanSink.Record(@"D:\Music\Tool\a.flac", entries[@"D:\Music\Tool\a.flac"]);
                var r1 = ScanSink.Flush(store, "output-x64.4", entries.Keys);
                assert(r1.Ok && r1.Entries == 1, $"v3: the fixture entry is written ({r1.Entries} entries, {r1.Error})");
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                {
                    assert(SchemaMigrator.ReadVersion(os.Db) == 4, $"a fresh store is schema v4 — v3 types + the T05 edge table and value.dtype + the T19 verdict table ({SchemaMigrator.ReadVersion(os.Db)})");
                    // TDA F10 (keeper's real MB3 migration: the transient WAL reached 101.5 MB on a 139.9 MB store, beside a 103.2 MB backup): the
                    // free-space check must cover the backup AND a WAL about the store's size, plus margin — not the backup alone.
                    long gb = 1L << 30;
                    assert(SchemaMigrator.SpaceNeededForMigration(gb) == 2 * gb + gb / 10 && SchemaMigrator.SpaceNeededForMigration(0) == 0,
                        $"migration space check = backup + WAL (~ the store each) + 10 % ({SchemaMigrator.SpaceNeededForMigration(gb)} for 1 GiB)");
                    string T(string col) => os.Db.ExecScalar($"SELECT typeof({col}) FROM track_content;") ?? "";
                    assert(T("bpm") == "integer" && T("spectralSpread") == "integer" && T("melKurtosis") == "integer" && T("spectralDecrease") == "integer" && T("danceability") == "integer" && T("dynamicRange") == "null",
                        $"fixed-dp fields are stored as scaled INTEGER (bpm {T("bpm")}, spread {T("spectralSpread")}, melKurtosis {T("melKurtosis")}, decrease {T("spectralDecrease")}, zero {T("danceability")}, null {T("dynamicRange")})");
                    assert(os.Db.ExecScalar("SELECT spectralDecrease FROM track_content;") == "-70000" && os.Db.ExecScalar("SELECT danceability FROM track_content;") == "0" && os.Db.ExecScalar("SELECT spectralSpread FROM track_content;") == "225000001234",
                        $"the stored integer is the decimal × 10^dp (decrease {os.Db.ExecScalar("SELECT spectralDecrease FROM track_content;")}, zero {os.Db.ExecScalar("SELECT danceability FROM track_content;")}, spread {os.Db.ExecScalar("SELECT spectralSpread FROM track_content;")})");
                    assert(T("mfcc") == "blob" && os.Db.ExecScalar("SELECT length(mfcc) FROM track_content;") == "52", $"fixed-dp vectors are int32 ×10^dp blobs (mfcc {T("mfcc")}, {os.Db.ExecScalar("SELECT length(mfcc) FROM track_content;")} bytes; was 104 as float64)");
                    var histLen = int.Parse(os.Db.ExecScalar("SELECT length(bpmHistogram) FROM track_content;") ?? "0");
                    assert(T("bpmHistogram") == "blob" && histLen == 4 + 3 * 6, $"bpmHistogram is sparse and self-describing: u16 length + u16 count + 3 × (u16 bin, i32 value) = 22 bytes for three non-zeros ({histLen} bytes; was 2,000)");
                    var entity = os.Db.ExecScalar("SELECT entity_id FROM track_content;")!;
                    var back = os.Dal.ReadContent(entity)!;
                    object? V(string n) => back.Values[TrackEntryMapper.ContentIndexOf(n)];
                    assert(V("spectralSpread") is double ss && ss == 22500000.1234 && V("melKurtosis") is double mk && mk == 1200000000.5678 && V("spectralDecrease") is double sd && sd == -0.00000007
                        && V("danceability") is double dz && dz == 0 && V("dynamicRange") == null,
                        $"read-back is EXACT, no six-figure rounding: spread {V("spectralSpread")} (was 22500000 under Sig6), melKurtosis {V("melKurtosis")}, decrease {V("spectralDecrease"):R}, zero {V("danceability")}, null {(V("dynamicRange") == null ? "null" : V("dynamicRange"))}");
                    // THE 1-ULP CASE, measured on .NET Framework 4.8 x64 (2026-10-15): the literal 0.022454000000000002 IS the double nearest
                    // 0.022454 (its "R" text is "0.022454"); Math.Round(it, 6) returns the double ONE ULP BELOW ("0.022453999999999998"), and so
                    // does 22454.0 / 1e6. The parser's output for this family is therefore the lower neighbour, the scan writes that double, and
                    // the store must reproduce IT — not the shortest-text decimal — for the catalog bytes to round-trip. FromScaled is defined
                    // as Math.Round((double)s / scale, dp), i.e. whatever Math.Round produces, so the three agree by construction.
                    string R(double d) => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                    double parserForm = Math.Round(0.022454000000000002, 6);
                    assert(V("spectralEnergyStdev") is double se && se == parserForm && se == Math.Round(0.022454, 6) && R(se) == "0.022453999999999998" && R(0.022454000000000002) == "0.022454",
                        $"the 1-ulp case: the store reproduces the parser's double (Math.Round at 6 dp), one ulp below the shortest-text decimal: read back {R((double)V("spectralEnergyStdev")!)}, parser form {R(parserForm)}, literal {R(0.022454000000000002)}, 22454/1e6 {R(22454.0 / 1e6)}");
                    var hb = V("bpmHistogram") as double[]; var mb = V("mfcc") as double[];
                    bool histOk = hb != null && hb.Length == 250; if (histOk) for (int i = 0; i < 250; i++) if (hb![i] != hist[i]) { histOk = false; break; }
                    bool mfccOk = mb != null && mb.Length == 13; if (mfccOk) for (int i = 0; i < 13; i++) if (mb![i] != f.Mfcc[i]) { mfccOk = false; break; }
                    assert(histOk && mfccOk, $"the sparse histogram reconstructs all 250 bins exactly and the int32 vector reads back exactly (hist {(hb == null ? "null" : hb.Length.ToString())}, mfcc {(mb == null ? "null" : mb.Length.ToString())})");
                }

                // ---- (b) migration: a v2 store with rows in the v2 representation migrates on open — backup first, values exact (V15), columns typed
                var v2dir = Path.Combine(dir, "v2"); Directory.CreateDirectory(v2dir);
                var v2store = Path.Combine(v2dir, "truedat.db");
                var row = TrackEntryMapper.ToRow(entries[@"D:\Music\Tool\a.flac"]);
                int copyCols = TrackEntryMapper.CopyColumnList().Split(',').Length;
                string Q(int n) { var sb = new System.Text.StringBuilder(); for (int i = 0; i < n; i++) sb.Append(", ?"); return sb.ToString(); }
                using (var db = SqliteDb.Open(v2store, true, exclusiveLocking: false))
                {
                    SchemaMigrator.Apply(db, "selftest-v2", targetVersion: 2);
                    db.Exec("PRAGMA foreign_keys=OFF;");
                    using (var st = db.Prepare("INSERT INTO track_content(entity_id, content_key, content_key_kind, run_id, updated_utc, " + TrackEntryMapper.ContentColumnList() + ") VALUES (?, ?, ?, NULL, ?" + Q(TrackEntryMapper.ContentColumnCount) + ");"))
                    { st.Bind(1, "e1").Bind(2, "k1").Bind(3, "sha").Bind(4, "2026-10-15T00:00:00Z"); TrackEntryMapper.BindLegacyV2(st, 5, row, content: true); st.Step(); }
                    using (var st = db.Prepare("INSERT INTO track_copy(location_id, run_id, updated_utc, " + TrackEntryMapper.CopyColumnList() + ") VALUES (7, NULL, ?" + Q(copyCols) + ");"))
                    { st.Bind(1, "2026-10-15T00:00:00Z"); TrackEntryMapper.BindLegacyV2(st, 2, row, content: false); st.Step(); }
                    assert(SchemaMigrator.ReadVersion(db) == 2 && db.ExecScalar("SELECT typeof(bpm) FROM track_content;") == "real" && db.ExecScalar("SELECT length(mfcc) FROM track_content;") == "104",
                        $"v2 fixture: REAL columns and float64 blobs before migration (v{SchemaMigrator.ReadVersion(db)}, {db.ExecScalar("SELECT typeof(bpm) FROM track_content;")}, mfcc {db.ExecScalar("SELECT length(mfcc) FROM track_content;")} bytes)");
                }
                using (var os = OpenStore.Open(v2store, false, "selftest-v3", DateTime.UtcNow.ToString("o")))
                {
                    assert(SchemaMigrator.ReadVersion(os.Db) == 4, $"a v2 store migrates to the current schema (v4) on open ({SchemaMigrator.ReadVersion(os.Db)})");
                    assert(os.Db.ExecScalar("SELECT typeof(bpm) FROM track_content;") == "integer" && os.Db.ExecScalar("SELECT length(mfcc) FROM track_content;") == "52" && os.Db.ExecScalar("SELECT typeof(smfmBpm) FROM track_copy;") == "integer" && os.Db.ExecScalar("SELECT count(*) FROM track_content;") == "1" && os.Db.ExecScalar("SELECT count(*) FROM track_copy;") == "1",
                        $"migrated columns are typed and every row survived (bpm {os.Db.ExecScalar("SELECT typeof(bpm) FROM track_content;")}, mfcc {os.Db.ExecScalar("SELECT length(mfcc) FROM track_content;")} bytes, smfmBpm {os.Db.ExecScalar("SELECT typeof(smfmBpm) FROM track_copy;")}, rows {os.Db.ExecScalar("SELECT count(*) FROM track_content;")}/{os.Db.ExecScalar("SELECT count(*) FROM track_copy;")})");
                    var back = os.Dal.ReadContent("e1");
                    int diff = 0; string firstDiff = "";
                    for (int k = 0; k < TrackEntryMapper.ContentColumnCount; k++)
                        if (!TrackEntryMapper.ContentEqualExact(k, TrackEntryMapper.ContentValue(row, k), back?.Values[k])) { diff++; if (firstDiff == "") firstDiff = $"{TrackEntryMapper.ContentColumnName(k)}: {TrackEntryMapper.FormatContent(k, TrackEntryMapper.ContentValue(row, k))} -> {TrackEntryMapper.FormatContent(k, back?.Values[k])}"; }
                    var copyBack = os.Dal.ReadCopyValues(7);
                    var copyDiff = copyBack == null ? "copy row missing" : TrackEntryMapper.FirstCopyDifference(copyBack, row);
                    assert(back != null && diff == 0 && copyDiff == null, $"V15: every v2 value lands in its typed column EXACTLY ({diff} content differences{(firstDiff == "" ? "" : "; first: " + firstDiff)}; copy: {copyDiff ?? "equal"})");
                    assert(os.Db.ExecScalar("SELECT count(*) FROM sqlite_master WHERE type='index' AND name='ix_content_key';") == "1", "the content-key index exists on the migrated table");
                }
                var backups = Directory.GetFiles(v2dir, "truedat.db.bak.v2.*");
                assert(backups.Length == 1 && new FileInfo(backups[0]).Length > 0, $"the migration wrote a backup of the v2 store first, beside it ({backups.Length} found: {string.Join(", ", backups.Select(Path.GetFileName))})");
                if (backups.Length == 1)
                    using (var db = SqliteDb.Open(backups[0], false, exclusiveLocking: false))
                        assert(SchemaMigrator.ReadVersion(db) == 2 && db.ExecScalar("SELECT typeof(bpm) FROM track_content;") == "real" && db.ExecScalar("SELECT count(*) FROM track_content;") == "1",
                            $"the backup is the untouched v2 store (v{SchemaMigrator.ReadVersion(db)}, {db.ExecScalar("SELECT typeof(bpm) FROM track_content;")})");
                using (var os = OpenStore.Open(v2store, false, "selftest-v3-again", DateTime.UtcNow.ToString("o")))
                    assert(SchemaMigrator.ReadVersion(os.Db) == StoreSchema.CurrentVersion && Directory.GetFiles(v2dir, "truedat.db.bak.v2.*").Length == 1, "a second open applies nothing and writes no second backup");
                assert(SchemaV3Migration.LastCanonicalisationNote == null && SchemaV3Migration.LastRefusedValues == 0,
                    $"a v2 store whose values were already at their declared precision is migrated with NOTHING canonicalised ({SchemaV3Migration.LastCanonicalisationNote ?? "none"})");

                // ---- (c) keeper (T04 preservation gate): a REAL v2 value with more digits than the writer renders — a scan's raw elapsed
                // seconds 1.23456 in analysisDuration (writer 1 dp), a tag's float32 97.65432 in smfmBpm (writer 3 dp) — SURVIVES the
                // migration exactly, because the store holds those two fields at six places (FieldPrecision StoreDp). The fixture is
                // written in the raw v2 representation (BindLegacyV2), never through ToRow, so nothing pre-rounds it.
                var v2bdir = Path.Combine(dir, "v2b"); Directory.CreateDirectory(v2bdir);
                var v2bstore = Path.Combine(v2bdir, "truedat.db");
                var rawEntry = FixtureEntries(@"D:\Music")[@"D:\Music\Tool\b.mp3"];
                var rawRow = TrackEntryMapper.ToRow(rawEntry);
                rawRow[TrackEntryMapper.RowIndexOf("analysisDuration")] = 1.23456;
                rawRow[TrackEntryMapper.RowIndexOf("smfmBpm")] = 97.65432;
                using (var db = SqliteDb.Open(v2bstore, true, exclusiveLocking: false))
                {
                    SchemaMigrator.Apply(db, "selftest-v2", targetVersion: 2);
                    db.Exec("PRAGMA foreign_keys=OFF;");
                    using (var st = db.Prepare("INSERT INTO track_content(entity_id, content_key, content_key_kind, run_id, updated_utc, " + TrackEntryMapper.ContentColumnList() + ") VALUES (?, ?, ?, NULL, ?" + Q(TrackEntryMapper.ContentColumnCount) + ");"))
                    { st.Bind(1, "e2").Bind(2, "k2").Bind(3, "sha").Bind(4, "2026-10-15T00:00:00Z"); TrackEntryMapper.BindLegacyV2(st, 5, rawRow, content: true); st.Step(); }
                    using (var st = db.Prepare("INSERT INTO track_copy(location_id, run_id, updated_utc, " + TrackEntryMapper.CopyColumnList() + ") VALUES (8, NULL, ?" + Q(copyCols) + ");"))
                    { st.Bind(1, "2026-10-15T00:00:00Z"); TrackEntryMapper.BindLegacyV2(st, 2, rawRow, content: false); st.Step(); }
                    assert(db.ExecScalar("SELECT analysisDuration FROM track_copy;") == "1.23456" && db.ExecScalar("SELECT smfmBpm FROM track_copy;") == "97.65432", $"raw v2 fixture holds the un-rounded values ({db.ExecScalar("SELECT analysisDuration FROM track_copy;")}, {db.ExecScalar("SELECT smfmBpm FROM track_copy;")})");
                }
                using (var os = OpenStore.Open(v2bstore, false, "selftest-v3", DateTime.UtcNow.ToString("o")))
                {
                    var copyBack = os.Dal.ReadCopyValues(8)!;
                    var ad = copyBack[TrackEntryMapper.CopyIndexOf("analysisDuration")]; var sb = copyBack[TrackEntryMapper.CopyIndexOf("smfmBpm")];
                    assert(SchemaMigrator.ReadVersion(os.Db) == StoreSchema.CurrentVersion && ad is double d1 && d1 == 1.23456 && sb is double d2 && d2 == 97.65432 && SchemaV3Migration.LastCanonicalisationNote == null,
                        $"V15: the un-rounded v2 values SURVIVE the migration exactly (analysisDuration {ad}, smfmBpm {sb}; stored as {os.Db.ExecScalar("SELECT analysisDuration FROM track_copy;")} / {os.Db.ExecScalar("SELECT smfmBpm FROM track_copy;")} at six places; note {SchemaV3Migration.LastCanonicalisationNote ?? "none"})");
                    // and the writer still renders them at ITS precision, so the catalog bytes are unchanged by the finer store
                    var wEntry = new TrackEntry { AnalysisDurationSecs = 1.23456, LastModified = DateTime.UtcNow, Features = new TrackFeatures { Bpm = 120, Key = "C", Mode = "major", Mfcc = new double[13], SmfmBpm = 97.65432, SmfmScores = new[] { 1 }, SmfmChannel = 0 } };
                    string wj;
                    using (var ms = new MemoryStream()) { using (var jw = new System.Text.Json.Utf8JsonWriter(ms)) { jw.WriteStartObject(); Program.WriteTrackEntryForStore(jw, @"D:\m\w.flac", wEntry); jw.WriteEndObject(); } wj = System.Text.Encoding.UTF8.GetString(ms.ToArray()); }
                    assert(wj.Contains("\"analysisDuration\":1.2") && wj.Contains("\"smfmBpm\":97.654"), $"the catalog writer still renders analysisDuration at 1 dp and smfmBpm at 3 dp ({(wj.IndexOf("analysisDuration", StringComparison.Ordinal) is int ai && ai >= 0 ? wj.Substring(ai, Math.Min(26, wj.Length - ai)) : "?")})");
                }

                // ---- (c2) the gate: a v2 value in any OTHER column that the typed column cannot hold exactly (a hand-built mfcc element with
                // six decimals; no parser-produced value can be one) makes the migration REFUSE and roll back — the store stays v2, the
                // backup stays, the message names the column. No retained value is ever rounded away by a migration.
                var v2ddir = Path.Combine(dir, "v2d"); Directory.CreateDirectory(v2ddir);
                var v2dstore = Path.Combine(v2ddir, "truedat.db");
                var badRow = TrackEntryMapper.ToRow(rawEntry);
                badRow[TrackEntryMapper.RowIndexOf("mfcc")] = new[] { -825.123456, 227.5, 0.333333333 };
                using (var db = SqliteDb.Open(v2dstore, true, exclusiveLocking: false))
                {
                    SchemaMigrator.Apply(db, "selftest-v2", targetVersion: 2);
                    db.Exec("PRAGMA foreign_keys=OFF;");
                    using (var st = db.Prepare("INSERT INTO track_content(entity_id, content_key, content_key_kind, run_id, updated_utc, " + TrackEntryMapper.ContentColumnList() + ") VALUES (?, ?, ?, NULL, ?" + Q(TrackEntryMapper.ContentColumnCount) + ");"))
                    { st.Bind(1, "e4").Bind(2, "k4").Bind(3, "sha").Bind(4, "2026-10-15T00:00:00Z"); TrackEntryMapper.BindLegacyV2(st, 5, badRow, content: true); st.Step(); }
                }
                bool refused = false; string refusal = "";
                try { using (var os = OpenStore.Open(v2dstore, false, "selftest-v3", DateTime.UtcNow.ToString("o"))) { } } catch (StoreException ex) { refused = true; refusal = ex.Message; }
                using (var db = SqliteDb.Open(v2dstore, false, exclusiveLocking: false))
                    assert(refused && refusal.Contains("mfcc 1") && refusal.Contains("NOT migrated") && SchemaMigrator.ReadVersion(db) == 2 && db.ExecScalar("SELECT length(mfcc) FROM track_content;") == "24",
                        $"a value the typed column cannot hold makes the migration REFUSE: still v2, float64 blob intact, message names the column (refused {refused}: {refusal}; v{SchemaMigrator.ReadVersion(db)})");
                assert(Directory.GetFiles(v2ddir, "truedat.db.bak.v2.*").Length == 1, "the pre-migration backup is still there after a refusal");

                // ---- (e) review finding 1: the gate must test REPRESENTABILITY at the store's scale, not double equality. A real v2
                // value is a shortest-text double (Sig6 -> decimal -> double): double.Parse("0.022454") is the double nearest 0.022454,
                // and Math.Round(it, 6) is one ulp BELOW it — so `Canon(d) != d` would refuse a genuine store. The decimal 0.022454 IS
                // representable (22454 at scale 1e6); the migration must proceed and the read-back must be the parser's form.
                var v2edir = Path.Combine(dir, "v2e"); Directory.CreateDirectory(v2edir);
                var v2estore = Path.Combine(v2edir, "truedat.db");
                var realRow = TrackEntryMapper.ToRow(rawEntry);
                realRow[TrackEntryMapper.RowIndexOf("spectralEnergyStdev")] = double.Parse("0.022454", System.Globalization.CultureInfo.InvariantCulture);
                realRow[TrackEntryMapper.RowIndexOf("spectralSpread")] = (double)3600111.25m;
                realRow[TrackEntryMapper.RowIndexOf("mfcc")] = new[] { double.Parse("-825.1235", System.Globalization.CultureInfo.InvariantCulture), 0.3333 };
                using (var db = SqliteDb.Open(v2estore, true, exclusiveLocking: false))
                {
                    SchemaMigrator.Apply(db, "selftest-v2", targetVersion: 2);
                    db.Exec("PRAGMA foreign_keys=OFF;");
                    using (var st = db.Prepare("INSERT INTO track_content(entity_id, content_key, content_key_kind, run_id, updated_utc, " + TrackEntryMapper.ContentColumnList() + ") VALUES (?, ?, ?, NULL, ?" + Q(TrackEntryMapper.ContentColumnCount) + ");"))
                    { st.Bind(1, "e5").Bind(2, "k5").Bind(3, "sha").Bind(4, "2026-10-15T00:00:00Z"); TrackEntryMapper.BindLegacyV2(st, 5, realRow, content: true); st.Step(); }
                }
                bool refusedE = false; string refusalE = "";
                try
                {
                    using (var os = OpenStore.Open(v2estore, false, "selftest-v3", DateTime.UtcNow.ToString("o")))
                    {
                        var back = os.Dal.ReadContent("e5")!;
                        var se2 = (double)back.Values[TrackEntryMapper.ContentIndexOf("spectralEnergyStdev")]!;
                        assert(SchemaMigrator.ReadVersion(os.Db) == StoreSchema.CurrentVersion && se2 == Math.Round(double.Parse("0.022454", System.Globalization.CultureInfo.InvariantCulture), 6) && os.Db.ExecScalar("SELECT spectralEnergyStdev FROM track_content;") == "22454",
                            $"a shortest-text v2 double in the 1-ulp family migrates (decimal 0.022454 -> 22454) and reads back as the parser's form ({se2:R}, stored {os.Db.ExecScalar("SELECT spectralEnergyStdev FROM track_content;")})");
                    }
                }
                catch (StoreException ex) { refusedE = true; refusalE = ex.Message; }
                assert(!refusedE, $"the gate does NOT refuse a real v2 store whose values are shortest-text doubles of representable decimals ({refusalE})");

                // ---- (f) review finding 2 (negative zero), MEASURED: the catalog writer's zero branch is sign-blind (-0.0 == 0.0), so a
                // Math.Round(-tiny, dp) that lands on -0.0 is written "0" — the catalog never carries "-0", and the store's unsigned
                // integer 0 exports the same bytes. Pinned here so the claim stays measured, not assumed.
                var zcat = Path.Combine(dir, "zero-mbxmoods.json"); var zstore = Path.Combine(dir, "zero", "truedat.db"); Directory.CreateDirectory(Path.GetDirectoryName(zstore)!);
                var zentries = FixtureEntries(@"D:\Music");
                zentries[@"D:\Music\Tool\a.flac"].Features!.SpectralSkewness = -0.0;
                WriteCatalog(zcat, zentries);
                var ztext = File.ReadAllText(zcat);
                assert(ztext.Contains("\"spectralSkewness\":0,") && !ztext.Contains("\"spectralSkewness\":-0"), "the catalog writer prints negative zero as \"0\" — the sign of zero never reaches the catalog's bytes");
                var zr = ImportCommand.Execute(new ImportOptions { StorePath = zstore, CatalogPath = zcat, LibraryRoot = @"D:\Music" });
                var zrt = ExportCommand.Roundtrip(zstore, zcat, null);
                assert(zr.Entries == zentries.Count && zrt.Equal && zrt.Different == 0,
                    $"a -0.0 feature round-trips through the store as EQUAL ({zrt.Same} same, {zrt.Different} different; first {zrt.FirstDifferentPath})");

                // ---- (g) review finding 3: an out-of-range vector element (int32 ×10^4 holds ±214,748) fails THAT entry, recorded as a
                // failed run; the rest of the batch is written and a re-run is not poisoned.
                // Baseline first: the fixture has its own undescribed entries, so "everything else written" is measured, not assumed.
                var bstore = Path.Combine(dir, "overflow-base", "truedat.db"); Directory.CreateDirectory(Path.GetDirectoryName(bstore)!);
                foreach (var kv in FixtureEntries(@"D:\Music")) ScanSink.Record(kv.Key, kv.Value);
                var brr = ScanSink.Flush(bstore, "output-x64.4", FixtureEntries(@"D:\Music").Keys);
                string baseline;
                using (var os = OpenStore.Open(bstore, false, "selftest", DateTime.UtcNow.ToString("o"))) baseline = os.Db.ExecScalar("SELECT count(*) FROM track_content;") ?? "?";
                var ostore = Path.Combine(dir, "overflow", "truedat.db"); Directory.CreateDirectory(Path.GetDirectoryName(ostore)!);
                var oentries = FixtureEntries(@"D:\Music");
                oentries[@"D:\Music\Tool\a.flac"].Features!.Mfcc = new[] { 1e9, 0.0 };
                foreach (var kv in oentries) ScanSink.Record(kv.Key, kv.Value);
                var orr = ScanSink.Flush(ostore, "output-x64.4", oentries.Keys);
                using (var os = OpenStore.Open(ostore, false, "selftest", DateTime.UtcNow.ToString("o")))
                    assert(brr.Ok && orr.Ok && orr.Failures == brr.Failures + 1 && os.Db.ExecScalar("SELECT count(*) FROM track_content;") == (int.Parse(baseline) - 1).ToString() && os.Db.ExecScalar("SELECT count(*) FROM run WHERE outcome = 'failed' AND error_text LIKE '%mfcc%';") == "1",
                        $"an unrepresentable vector element fails only its entry (ok {orr.Ok}, failures {orr.Failures} vs baseline {brr.Failures}, {os.Db.ExecScalar("SELECT count(*) FROM track_content;")} descriptions vs baseline {baseline}, failed run naming the column: {os.Db.ExecScalar("SELECT count(*) FROM run WHERE outcome = 'failed' AND error_text LIKE '%mfcc%';")})");

                // ---- (h) review finding 4: a corrupt blob is refused loudly, never decoded into a plausible wrong vector.
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                {
                    os.Db.Exec("UPDATE track_content SET bpmHistogram = X'FA000300';");   // length 250, count 3, no pairs
                    bool threwH = false; string msgH = "";
                    try { os.Dal.ReadContent(os.Db.ExecScalar("SELECT entity_id FROM track_content;")!); } catch (StoreException ex) { threwH = true; msgH = ex.Message; }
                    assert(threwH && msgH.Contains("bpmHistogram"), $"a truncated sparse blob throws a StoreException naming the column ({threwH}: {msgH})");
                    os.Db.Exec("UPDATE track_content SET mfcc = X'010203';");               // not a multiple of 4
                    threwH = false; msgH = "";
                    try { os.Dal.ReadContent(os.Db.ExecScalar("SELECT entity_id FROM track_content;")!); } catch (StoreException ex) { threwH = true; msgH = ex.Message; }
                    assert(threwH && msgH.Contains("mfcc"), $"an int32 blob of the wrong length throws a StoreException naming the column ({threwH}: {msgH})");
                }

                // ---- (i) the 1-ulp family re-scans as Unchanged (T01 exact comparison is a fixed point of bind -> read).
                var ustore = Path.Combine(dir, "ulp", "truedat.db"); Directory.CreateDirectory(Path.GetDirectoryName(ustore)!);
                var uentries = FixtureEntries(@"D:\Music");
                uentries[@"D:\Music\Tool\a.flac"].Features!.SpectralEnergyStdev = double.Parse("0.022454", System.Globalization.CultureInfo.InvariantCulture);
                ScanSink.Record(@"D:\Music\Tool\a.flac", uentries[@"D:\Music\Tool\a.flac"]);
                var u1 = ScanSink.Flush(ustore, "output-x64.4", uentries.Keys);
                ScanSink.Record(@"D:\Music\Tool\a.flac", uentries[@"D:\Music\Tool\a.flac"]);
                var u2 = ScanSink.Flush(ustore, "output-x64.4", uentries.Keys);
                assert(u1.Ok && u2.Ok && u2.Unchanged == 1 && u2.Remeasured == 0, $"a re-scan of a 1-ulp-family value is Unchanged, not a perpetual remeasure ({u2.Unchanged} unchanged, {u2.Remeasured} remeasured)");

                // ---- (j) keeper partial-entry defect (executed on Release): a COPY value the store cannot hold, arriving AFTER a changed
                // content value, must not leave the new content committed with the old copy, an ok run and Ok=true. Every representation
                // is validated BEFORE any entry mutation: the entry fails whole, as one recorded failed run, and nothing of it changes.
                var pstore = Path.Combine(dir, "partial", "truedat.db"); Directory.CreateDirectory(Path.GetDirectoryName(pstore)!);
                var pentries = FixtureEntries(@"D:\Music");
                var pe = pentries[@"D:\Music\Tool\a.flac"]; pe.Features!.Bpm = 120; pe.Features.SmfmBpm = 100; pe.Features.SmfmScores = new[] { 1, 2 }; pe.Features.SmfmChannel = 0;
                ScanSink.Record(@"D:\Music\Tool\a.flac", pe);
                var p1 = ScanSink.Flush(pstore, "output-x64.4", pentries.Keys);
                string okRunsBefore, contentRunBefore;
                using (var os = OpenStore.Open(pstore, false, "selftest", DateTime.UtcNow.ToString("o")))
                { okRunsBefore = os.Db.ExecScalar("SELECT count(*) FROM run WHERE outcome = 'ok';") ?? "?"; contentRunBefore = os.Db.ExecScalar("SELECT run_id FROM track_content;") ?? "?"; }
                var pe2 = FixtureEntries(@"D:\Music")[@"D:\Music\Tool\a.flac"]; pe2.Features!.Bpm = 150; pe2.Features.SmfmBpm = 1e10; pe2.Features.SmfmScores = new[] { 1, 2 }; pe2.Features.SmfmChannel = 0;   // 1e10 × 10^6 > int64 range for the copy column
                ScanSink.Record(@"D:\Music\Tool\a.flac", pe2);
                var p2 = ScanSink.Flush(pstore, "output-x64.4", pentries.Keys);
                using (var os = OpenStore.Open(pstore, false, "selftest", DateTime.UtcNow.ToString("o")))
                    assert(p1.Ok && p2.Ok && p2.Failures == 1 && p2.Remeasured == 0
                        && StoredText(os, @"D:\Music\Tool\a.flac", "bpm") == "120"
                        && os.Db.ExecScalar("SELECT smfmBpm FROM track_copy;") == "100000000"
                        && os.Db.ExecScalar("SELECT count(*) FROM run WHERE outcome = 'ok';") == okRunsBefore
                        && os.Db.ExecScalar("SELECT run_id FROM track_content;") == contentRunBefore
                        && os.Db.ExecScalar("SELECT count(*) FROM run WHERE outcome = 'failed' AND error_text LIKE '%smfmBpm%';") == "1",
                        $"a rejected copy value after changed content leaves NOTHING of the entry changed: bpm {StoredText(os, @"D:\Music\Tool\a.flac", "bpm")} (120), smfmBpm {os.Db.ExecScalar("SELECT smfmBpm FROM track_copy;")} (100000000), ok runs {os.Db.ExecScalar("SELECT count(*) FROM run WHERE outcome = 'ok';")} (was {okRunsBefore}), content run {os.Db.ExecScalar("SELECT run_id FROM track_content;")} (was {contentRunBefore}), failed run naming smfmBpm {os.Db.ExecScalar("SELECT count(*) FROM run WHERE outcome = 'failed' AND error_text LIKE '%smfmBpm%';")}; flush ok {p2.Ok}, failures {p2.Failures}, remeasured {p2.Remeasured}");

                // ---- (k) keeper's second repro: a StoreException AFTER a write (an artifact refusal on a bad producer build) must NOT be
                // swallowed by the per-entry catch — the whole batch rolls back as before, and nothing of the entry (content, run) survives.
                var pe3 = FixtureEntries(@"D:\Music")[@"D:\Music\Tool\a.flac"]; pe3.Features!.Bpm = 150; pe3.Features.SmfmBpm = 100; pe3.Features.SmfmScores = new[] { 1, 2 }; pe3.Features.SmfmChannel = 0;
                // Order matters: the stash only exists under a StoreDirectory, and Record ties a stash to the entry only when it is
                // already there under the analysed path — stash first, then Record(path, entry, analysedPath).
                var prevStoreDir = ScanSink.StoreDirectory;
                ScanSink.StoreDirectory = Path.GetDirectoryName(pstore);
                ScanSink.StashMainJson(@"D:\Music\Tool\a.flac", "{\"lowlevel\":{\"spectral_centroid\":{\"mean\":1.0}}}");
                ScanSink.Record(@"D:\Music\Tool\a.flac", pe3, @"D:\Music\Tool\a.flac");
                var p3 = ScanSink.Flush(pstore, "bad/build", pentries.Keys);
                ScanSink.StoreDirectory = prevStoreDir;
                using (var os = OpenStore.Open(pstore, false, "selftest", DateTime.UtcNow.ToString("o")))
                    assert(!p3.Ok && StoredText(os, @"D:\Music\Tool\a.flac", "bpm") == "120" && os.Db.ExecScalar("SELECT count(*) FROM run WHERE outcome = 'ok';") == okRunsBefore && os.Db.ExecScalar("SELECT run_id FROM track_content;") == contentRunBefore,
                        $"a post-write store failure (artifact refusal) rolls the batch back whole — flush ok {p3.Ok} (false), bpm {StoredText(os, @"D:\Music\Tool\a.flac", "bpm")} (120), ok runs {os.Db.ExecScalar("SELECT count(*) FROM run WHERE outcome = 'ok';")} (was {okRunsBefore}), content run {os.Db.ExecScalar("SELECT run_id FROM track_content;")} (was {contentRunBefore}): {p3.Error}");
                ScanSink.Reset();

                // ---- (d) the migration's transaction rolls back whole when the data step fails after moving rows (same seam shape as v2)
                var v2cdir = Path.Combine(dir, "v2c"); Directory.CreateDirectory(v2cdir);
                var v2cstore = Path.Combine(v2cdir, "truedat.db");
                using (var db = SqliteDb.Open(v2cstore, true, exclusiveLocking: false))
                {
                    SchemaMigrator.Apply(db, "selftest-v2", targetVersion: 2);
                    db.Exec("PRAGMA foreign_keys=OFF;");
                    using (var st = db.Prepare("INSERT INTO track_content(entity_id, content_key, content_key_kind, run_id, updated_utc, " + TrackEntryMapper.ContentColumnList() + ") VALUES (?, ?, ?, NULL, ?" + Q(TrackEntryMapper.ContentColumnCount) + ");"))
                    { st.Bind(1, "e3").Bind(2, "k3").Bind(3, "sha").Bind(4, "2026-10-15T00:00:00Z"); TrackEntryMapper.BindLegacyV2(st, 5, row, content: true); st.Step(); }
                }
                SchemaV3Migration.FailAfterMoveForTest = true;
                bool threw3 = false; string threwMsg = "";
                try { using (var os = OpenStore.Open(v2cstore, false, "selftest-v3", DateTime.UtcNow.ToString("o"))) { } } catch (Exception ex) { threw3 = true; threwMsg = ex.Message; }
                finally { SchemaV3Migration.FailAfterMoveForTest = false; }
                using (var db = SqliteDb.Open(v2cstore, false, exclusiveLocking: false))
                    assert(threw3 && SchemaMigrator.ReadVersion(db) == 2 && db.ExecScalar("SELECT typeof(bpm) FROM track_content;") == "real" && db.ExecScalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='track_content_v3';") == "0",
                        $"a v3 migration that fails after moving rows rolls back whole: still v2, REAL columns, no _v3 table (threw {threw3}: {threwMsg}; v{SchemaMigrator.ReadVersion(db)})");
                assert(Directory.GetFiles(v2cdir, "truedat.db.bak.v2.*").Length == 1, "the backup written before the failed migration is still there (the operator's untouched copy)");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>T04: the store path's one number rule is now the field's DECLARED precision (FieldPrecision), applied by ToRow; six
        /// significant figures (the former CanonicalPrecision, DBA finding F1) is gone from the store path and from the exporter, so the
        /// store reproduces the parsed decimal exactly and the export writes the same bytes the scan writer would.</summary>
        static void TestStorePrecision(Action<bool, string> assert)
        {
            var e = FixtureEntries(@"D:\Music")[@"D:\Music\Tool\a.flac"];
            e.Features!.SpectralEnergyStdev = 0.022454000000000002; e.Features.SpectralSpread = 3600111.25; e.Features.Mfcc = new[] { -825.70000000000005, 227.5, 1.0 / 3.0 };
            var row = TrackEntryMapper.ToRow(e);
            double Col(string name) => (double)TrackEntryMapper.ContentValue(row, TrackEntryMapper.ContentIndexOf(name))!;
            assert(Col("spectralEnergyStdev") == 0.022454 && Col("spectralEnergyStdev") == Math.Round(0.022454000000000002, 6), $"ToRow canonicalises at the field's dp (6): the 17-digit double is the double of 0.022454 ({Col("spectralEnergyStdev"):R})");
            assert(Col("spectralSpread") == 3600111.25, $"ToRow keeps every figure the field's dp allows: 3600111.25 stays 3600111.25, not 3600110 ({Col("spectralSpread"):R})");
            var mf = (double[])TrackEntryMapper.ContentValue(row, TrackEntryMapper.ContentIndexOf("mfcc"))!;
            assert(mf.Length == 3 && mf[0] == -825.7 && mf[1] == 227.5 && mf[2] == 0.3333, $"ToRow canonicalises vector elements at the field's dp (4) ({mf[0]:R}, {mf[1]:R}, {mf[2]:R})");
            assert(Math.Round(0.022454000000000002, 6) == Math.Round(BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(0.022454000000000002) - 1), 6),
                "two doubles one bit apart canonicalise to the same double at the field's dp");

            // The exporter renders through the ordinary catalog writer: the bytes for a store entry are the bytes the scan would write.
            var a = FixtureEntries(@"D:\Music")[@"D:\Music\Tool\a.flac"];
            a.Features!.SpectralEnergyStdev = 0.022454000000000002; a.Features.SpectralSpread = 3600111.25;
            string DefaultJson(TrackEntry t) { using var ms = new MemoryStream(); using (var jw = new System.Text.Json.Utf8JsonWriter(ms)) { jw.WriteStartObject(); Program.WriteTrackEntryForStore(jw, @"D:\Music\Tool\a.flac", t); jw.WriteEndObject(); } return System.Text.Encoding.UTF8.GetString(ms.ToArray()); }
            string dj = DefaultJson(a), ej = EntryJson(@"D:\Music\Tool\a.flac", a);
            assert(dj.Contains("\"spectralSpread\":3600111.25,"), "the catalog writer still writes every figure the scan computed");
            assert(ej.Contains("\"spectralSpread\":3600111.25,") && !ej.Contains("\"spectralSpread\":3600110,"), $"the export's entry rendering is the catalog writer's — no six-figure rewrite ({(ej.IndexOf("spectralSpread", StringComparison.Ordinal) is int i && i >= 0 ? ej.Substring(i, Math.Min(40, ej.Length - i)) : "?")})");
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
                lines.Clear();
                StoreLog.SetVerbose(false);
                using (StoreLog.Quiet())
                {
                    StoreLog.Info("open-line-hidden"); StoreLog.Warn("warn-still-shown");
                }
                StoreLog.Info("info-after");
                assert(lines.Count == 2 && lines[0].EndsWith("warn-still-shown") && lines[1].EndsWith("info-after"),
                    $"Quiet() lifts the threshold to Warn for a read-only summary and restores it on dispose (got {lines.Count}: {string.Join(" | ", lines)})");
                lines.Clear();
                StoreLog.SetVerbose(true);
                using (StoreLog.Quiet()) { StoreLog.Info("audit-keeps-info"); }
                assert(lines.Count == 1, "under --audit Quiet() suppresses nothing: the operator asked for detail");
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
                    assert(SqliteDb.DefaultPageSize == 16384, "the default page size is 16 KB (measured: ~2 KB feature rows pack 7 per page instead of 1 per 4 KB page)");
                    assert(db.ExecScalar("PRAGMA page_size;") == "16384", "a fresh store is created at 16 KB pages, before WAL is switched on");

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
