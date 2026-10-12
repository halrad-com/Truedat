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
            Console.WriteLine("Store self-test (C7: verbs answering from the store)");
            Block(assert, "StoreDictionary", () => TestStoreDictionary(assert));
            Console.WriteLine("Store self-test (C8: --store-info measurements)");
            Block(assert, "StoreInfo", () => TestStoreInfo(assert));
            Console.WriteLine("Store self-test (page size: 16 KB pages by default, --store-vacuum repacks)");
            Block(assert, "Vacuum", () => TestVacuum(assert));
            Console.WriteLine("Store self-test (S2 C0: default store location is local; network opens exclusive)");
            Block(assert, "StoreLocation", () => TestStoreLocation(assert));
            Console.WriteLine("Store self-test (S2 C2: a differing value is a conflict, never an overwrite; equal re-imports write nothing)");
            Block(assert, "Drift", () => TestDrift(assert));
            Console.WriteLine("Store self-test (S2 C3: the scan writes the store through the import's in-memory entry)");
            Block(assert, "ScanSink", () => TestScanSink(assert));
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
                assert(r.ResultPath != null && File.Exists(r.ResultPath) && File.ReadAllText(r.ResultPath).Contains("\"producer\": \"essentia\""), $"store-result.json is written beside the store and names the producer ({r.ResultPath})");
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
                // A newer extractor build with a changed value replaces the description (different producer version), no drift.
                var newer = FixtureEntries(@"D:\Music");
                newer[@"D:\Music\Tool\a.flac"].Features!.Bpm = newer[@"D:\Music\Tool\a.flac"].Features!.Bpm * 1.5;
                ScanSink.Record(@"D:\Music\Tool\a.flac", newer[@"D:\Music\Tool\a.flac"]);
                var r3 = ScanSink.Flush(store, "output-x64.5");
                assert(r3.Ok && r3.ContentReplaced == 1 && r3.Drift == 0, $"a newer extractor build replaces the description without a drift conflict ({r3.ContentReplaced} replaced, {r3.Drift} drift)");

                // A store that cannot be opened: reported, never thrown.
                ScanSink.Record(@"D:\Music\Tool\a.flac", entries[@"D:\Music\Tool\a.flac"]);
                var bad = ScanSink.Flush(dir, "output-x64.4");   // a directory is not a database
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
                        && os.Db.ExecScalar("SELECT value_a FROM conflict WHERE kind = 'value-drift';") == origBpmA.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                        && os.Db.ExecScalar("SELECT value_b FROM conflict WHERE kind = 'value-drift';") == (origBpmA * 1.25).ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                        && os.Db.ExecScalar("SELECT count(*) FROM conflict WHERE kind = 'value-drift' AND run_a IS NOT NULL AND run_b IS NOT NULL AND run_a <> run_b;") == "1",
                        $"the drift conflict names the field, both values and both runs ({os.Db.ExecScalar("SELECT field || ' ' || value_a || ' ' || value_b || ' ' || run_a || '/' || run_b FROM conflict WHERE kind = 'value-drift';")})");
                    assert(os.Db.ExecScalar("SELECT c.bpm FROM track_content c JOIN track_location l ON l.entity_id = c.entity_id WHERE l.display_path = 'D:\\Music\\Tool\\a.flac';") == origBpmA.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + (origBpmA == Math.Floor(origBpmA) ? ".0" : ""),
                        $"the stored description is NOT overwritten by the drifted value ({os.Db.ExecScalar("SELECT c.bpm FROM track_content c JOIN track_location l ON l.entity_id = c.entity_id WHERE l.display_path = 'D:\\Music\\Tool\\a.flac';")} vs {origBpmA})");
                    assert(os.Db.ExecScalar("SELECT c.bpm FROM track_content c JOIN track_location l ON l.entity_id = c.entity_id WHERE l.display_path = 'D:\\Music\\Tool\\b.mp3';") == "120.0", "a noise-level difference writes nothing (b.mp3 bpm stays 120)");
                    assert(os.Db.ExecScalar("SELECT p.artist FROM track_copy p JOIN track_location l ON l.id = p.location_id WHERE l.display_path = 'D:\\Music\\old\\legacy.flac';") == "Renamed", "a copy fact (artist) is updated: newer wins, no conflict");
                }

                // 3. The same drifted catalog again: the drift is still a drift, but the conflict is not recorded twice.
                var r4 = ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = catalog, LibraryRoot = @"D:\Music" });
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                    assert(r4.Drift == 1 && os.Db.ExecScalar("SELECT count(*) FROM conflict WHERE kind = 'value-drift';") == "1", $"re-importing the same drifted catalog records no duplicate conflict (drift {r4.Drift}, rows {os.Db.ExecScalar("SELECT count(*) FROM conflict WHERE kind = 'value-drift';")})");

                // 4. A key move (the media gains a new content hash) re-keys and rewrites the description — not drift.
                var moved = FixtureEntries(@"D:\Music");
                moved[@"D:\Music\old\legacy.flac"].AudioStreamSha256 = "2222222222222222222222222222222222222222222222222222222222222222";
                moved[@"D:\Music\old\legacy.flac"].AudioStreamSha256Source = "flac-frames";
                WriteCatalog(catalog, moved);
                var r5 = ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = catalog, LibraryRoot = @"D:\Music" });
                using (var os = OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                    assert(r5.ContentRekeyed == 1 && os.Db.ExecScalar("SELECT c.content_key FROM track_content c JOIN track_location l ON l.entity_id = c.entity_id WHERE l.display_path = 'D:\\Music\\old\\legacy.flac';") == "2222222222222222222222222222222222222222222222222222222222222222",
                        $"a key move re-keys the description to the new hash without a drift conflict (rekeyed {r5.ContentRekeyed}, drift {r5.Drift})");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ---------------------------------------------------------------- S2 C0: store location
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
                assert(info.BytesPerTrack == (double)info.FileBytes / 5 && info.ProjectedBytesAt156k == (long)(info.BytesPerTrack * 156_000), "bytes/track (per distinct track) and the 156k projection derive from the file size");
                assert(info.RtfSamples == 3 && info.MedianRtf.HasValue && Math.Abs(info.MedianRtf.Value - 10.0) < 1e-9, $"median RTF over the 3 rows with both durations = 10 (got {info.MedianRtf}, n={info.RtfSamples})");
                assert(info.FailedComponents.TryGetValue("sha", out var fsha) && fsha == 1 && info.FailedComponents.TryGetValue("fingerprint", out var ffp) && ffp == 1, "failed components split on ';' and counted per component");
                assert(info.Coverage["loudnessMomentary"] == 1 && info.Coverage["dynamicComplexity"] == 1 && info.Coverage["fingerprint.v1"] == 6 && info.Coverage["smfmScores"] == 1,
                    $"coverage by wave: extended {info.Coverage["loudnessMomentary"]}, capture-once {info.Coverage["dynamicComplexity"]}, fingerprint {info.Coverage["fingerprint.v1"]}, smfm {info.Coverage["smfmScores"]} (only the fully populated entry has the waves)");
                assert(info.ContentKeysShared == 1 && info.LocationsOnSharedKeys == 2, $"one content key shared by 2 locations (d1/d2) ({info.ContentKeysShared}/{info.LocationsOnSharedKeys})");
                assert(info.SupersededHashes == 1 && info.NoShaFeatures == 1, $"1 superseded hash (the re-keyed FLAC), 1 no-sha feature row ({info.SupersededHashes}/{info.NoShaFeatures})");

                int rc = StoreInfoCommand.Run(store, false);
                assert(rc == 0 && File.Exists(Path.Combine(dir, StoreInfoCommand.ResultFileName)), "Run writes store-info.json beside the store and exits 0");
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
                assert(root == @"D:\Music" && fromStore.Count == fromJson.Count && fromStore.Count == 6, $"store dictionary has the catalog's 6 entries for the primary root ({fromStore.Count}, {root})");
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
                var dal = new StoreDal(db);
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

                var m3 = MachineIdentity.EnsureMinted(dal, "MAX1", now);
                assert(!m3.HostMatches && m3.Id == m1.Id, "a copied store opened on another host keeps the id but HostMatches is false");
                warns = lines.FindAll(l => l.Contains(" WARN "));
                assert(warns.Count == 1 && warns[0].Contains("ATOM") && warns[0].Contains("MAX1"),
                    $"hostname mismatch warns once, naming both hosts ({(warns.Count > 0 ? warns[0] : "<none>")})");
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
                    // e1 has two copies: the second differs by run-to-run NOISE on spectralCentroid (1793.406 vs 1793.656, 0.014%)
                    // and by a REAL disagreement on bpm (120 vs 135). e2 has one copy.
                    db.Exec($"INSERT INTO track_feature(location_id, content_key, content_key_kind, updated_utc, artist, bpm, spectralCentroid, analysisDuration, smfmBpm, audioStreamSha256) VALUES (10, 'sha1', '{FacetKinds.ShaFrames}', '{now}', 'Tool', 120.0, 1793.406, 1.5, 118, 'sha1');");
                    db.Exec($"INSERT INTO track_feature(location_id, content_key, content_key_kind, updated_utc, artist, bpm, spectralCentroid, analysisDuration, smfmBpm, audioStreamSha256) VALUES (11, 'sha1', '{FacetKinds.ShaFrames}', '{now}', 'TOOL', 135.0, 1793.656, 2.5, NULL, 'sha1');");
                    db.Exec($"INSERT INTO track_feature(location_id, content_key, content_key_kind, updated_utc, artist, bpm, analysisDuration, audioStreamSha256) VALUES (12, 'sha2', '{FacetKinds.ShaFrames}', '{now}', 'Zep', 90.0, 3.0, 'sha2');");
                }
                var v1Now = DateTime.ParseExact("2026-10-12T10:00:00.0000000Z", "o", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
                using (var db = SqliteDb.Open(v1Path, create: false))
                {
                    SchemaMigrator.Apply(db, "selftest-v2");
                    assert(SchemaMigrator.ReadVersion(db) == 2, "v1 store migrates forward to v2 in one Apply");
                    assert(db.ExecScalar("SELECT count(*) FROM track_content;") == "2" && db.ExecScalar("SELECT count(*) FROM track_copy;") == "3",
                        $"split: 2 content rows (one per media) and 3 copy rows (one per location) ({db.ExecScalar("SELECT count(*) FROM track_content;")}/{db.ExecScalar("SELECT count(*) FROM track_copy;")})");
                    assert(db.ExecScalar("SELECT bpm FROM track_content WHERE entity_id = 'e1';") == "120.0", "the first-imported copy's content values win (bpm 120 from location 10)");
                    assert(db.ExecScalar("SELECT content_key FROM track_content WHERE entity_id = 'e1';") == "sha1" && db.ExecScalar("SELECT audioStreamSha256 FROM track_content WHERE entity_id = 'e1';") == "sha1", "content key and the hash itself sit on the content row");
                    assert(db.ExecScalar("SELECT artist FROM track_copy WHERE location_id = 11;") == "TOOL" && db.ExecScalar("SELECT analysisDuration FROM track_copy WHERE location_id = 11;") == "2.5" && db.ExecScalar("SELECT smfmBpm FROM track_copy WHERE location_id = 10;") == "118.0",
                        "per-copy facts (tags, analysisDuration, SMFM) stay with their own copy");
                    assert(db.ExecScalar("SELECT count(*) FROM conflict WHERE kind = 'content-disagree-at-split';") == "1", $"ONE conflict: bpm 120 vs 135 is a disagreement; spectralCentroid 1793.406 vs 1793.656 is run-to-run noise and is not ({db.ExecScalar("SELECT count(*) FROM conflict;")})");
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
                    [@"D:\Music\a\one.flac"] = new TrackEntry { AudioStreamSha256 = "sha1", AnalysisDurationSecs = 1.5, LastModified = v1Now, Features = new TrackFeatures { Artist = "Tool", Bpm = 120.0, SpectralCentroid = 1793.406, SmfmBpm = 118 } },
                    [@"D:\Music\b\one copy.flac"] = new TrackEntry { AudioStreamSha256 = "sha1", AnalysisDurationSecs = 2.5, LastModified = v1Now, Features = new TrackFeatures { Artist = "TOOL", Bpm = 135.0, SpectralCentroid = 1793.656 } },
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
