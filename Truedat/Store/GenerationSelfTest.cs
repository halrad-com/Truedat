using System;
using System.Collections.Generic;
using System.IO;

namespace Truedat.Store
{
    /// <summary>
    /// Self-tests for the publication generation (store-v3 T07; keeper 099 item 1): every production mutator advances
    /// <c>store_meta.generation</c> inside the transaction that commits a consumer-visible change, exactly once per such
    /// transaction, and never for a no-op, a bookkeeping-only write, a dry run or a rolled-back transaction. Called from
    /// Program.RunSelfTest. Every case drives the real producer entry point (the scan sink's ExecuteEntries, the
    /// --store-import verb, --edge-arrays-drop, the open-time verdict re-derivation), never AdvanceGeneration directly.
    /// </summary>
    internal static class GenerationSelfTest
    {
        internal static void Run(Action<bool, string> assert)
        {
            Console.WriteLine("Generation self-test (T07: one advance per committed consumer-visible mutation; none for no-ops, failures-only, dry runs, rollbacks)");
            Block(assert, "scan flush", () => TestScanFlush(assert));
            Block(assert, "edge-only change", () => TestEdgeOnly(assert));
            Block(assert, "store import", () => TestStoreImport(assert));
            Block(assert, "failed import record", () => TestFailedImportRecord(assert));
            Block(assert, "ledger mirror location", () => TestLedgerMirrorLocation(assert));
            Block(assert, "edge arrays drop", () => TestArraysDrop(assert));
            Block(assert, "verdict re-derive", () => TestVerdictRederive(assert));
        }

        static void Block(Action<bool, string> assert, string name, Action body)
        {
            try { body(); }
            catch (Exception ex) { assert(false, $"generation {name}: threw {ex.GetType().Name}: {ex.Message}"); }
        }

        static string NewDir(string tag)
        {
            var d = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "gen-" + tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            return d;
        }

        static long Gen(string store)
        {
            using var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o"));
            return os.Dal.Generation();
        }

        static ImportReport Scan(string store, IReadOnlyDictionary<string, TrackEntry> entries, IReadOnlyList<ScanFailure>? failures = null)
            => ImportCommand.ExecuteEntries(store, entries, @"D:\music", "primary", true, "essentia", "selftest-gen", "scan", failures, null);

        // ---- the scan's store write (ScanSink -> ExecuteEntries): one transaction per flush
        static void TestScanFlush(Action<bool, string> assert)
        {
            var dir = NewDir("scan"); var store = Path.Combine(dir, "truedat.db");
            try
            {
                var entries = VerdictSelfTest.Fixture();
                Scan(store, entries);
                assert(Gen(store) == 1, $"a fresh flush of {entries.Count} new entries advances the generation once (0 -> 1; got {Gen(store)})");

                Scan(store, entries);
                assert(Gen(store) == 1, $"the same entries again change nothing a consumer sees: no advance (stays 1; got {Gen(store)})");

                var first = new List<string>(entries.Keys)[0];
                entries[first].Features!.Title = "retitled for the generation test";
                Scan(store, entries);
                assert(Gen(store) == 2, $"a changed copy fact (title) is a visible mutation: one advance (1 -> 2; got {Gen(store)})");

                entries[first].LastModified = entries[first].LastModified.AddDays(1);
                Scan(store, entries);
                assert(Gen(store) == 3, $"a moved lastModified alone (content and copy facts equal) is visible through the location: one advance (2 -> 3; got {Gen(store)})");

                Scan(store, new Dictionary<string, TrackEntry>(), new[] { new ScanFailure { Path = @"D:\music\verdict\gone.mp3", Components = "decode-12%", Error = "truncated", DurationSec = 1 } });
                assert(Gen(store) == 3, $"a failures-only flush writes run rows only: no advance (stays 3; got {Gen(store)})");

                var fresh = VerdictSelfTest.Fixture();
                var extra = new Dictionary<string, TrackEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in fresh) extra[kv.Key.Replace(@"\verdict\", @"\verdict2\")] = kv.Value;
                ImportCommand.FailAfterEntriesForTest = 1;
                bool threw = false;
                try { Scan(store, extra); } catch (InvalidOperationException) { threw = true; }
                finally { ImportCommand.FailAfterEntriesForTest = 0; }
                assert(threw && Gen(store) == 3, $"a flush that rolls back leaves the generation where it was (threw {threw}; stays 3; got {Gen(store)})");

                Scan(store, extra);
                assert(Gen(store) == 4, $"the same flush, committed, advances once (3 -> 4; got {Gen(store)})");
            }
            finally { ImportCommand.FailAfterEntriesForTest = 0; try { Directory.Delete(dir, true); } catch { } }
        }

        // ---- TDA 082: an EDGE-ONLY change — the --refresh edge backfill on a cached track: description and copy facts equal, new edge
        // rows. Driven through the real sink: the fold is stashed and tied to the entry at Record, exactly as a scan does.
        static void TestEdgeOnly(Action<bool, string> assert)
        {
            var dir = NewDir("edgeonly"); var store = Path.Combine(dir, "truedat.db");
            var savedDir = ScanSink.StoreDirectory;
            try
            {
                const string P = @"D:\music\verdict\a-lavc.mp3";
                var entries = VerdictSelfTest.Fixture();
                ScanSink.Reset(); ScanSink.StoreDirectory = dir;
                ScanSink.Record(P, entries[P]);
                ScanSink.Flush(store, "output-x64.5", new[] { P });
                long g0 = Gen(store);
                long edgesBefore;
                using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o"))) edgesBefore = os.Dal.ScalarLong("SELECT count(*) FROM edge;");

                var analysed = Path.Combine(dir, "staged-a-lavc.mp3");
                ScanSink.StashEdges(analysed, global::Truedat.Edges.EdgeSelfTest.SyntheticFold(dir, 240, true));
                ScanSink.Record(P, VerdictSelfTest.Fixture()[P], analysed);   // the same cached entry, now carrying the fold
                var r = ScanSink.Flush(store, "output-x64.5", new[] { P });
                long edgesAfter;
                using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o"))) edgesAfter = os.Dal.ScalarLong("SELECT count(*) FROM edge;");
                assert(edgesBefore == 0 && edgesAfter == 2 && r.Unchanged == 1, $"setup: the second flush wrote edge rows only — description and copy unchanged (edges {edgesBefore} -> {edgesAfter}, unchanged {r.Unchanged})");
                assert(Gen(store) == g0 + 1, $"an edge-only change (the --refresh backfill on a cached track) is a visible mutation: one advance ({g0} -> {g0 + 1}; got {Gen(store)})");
            }
            finally { ScanSink.Reset(); ScanSink.StoreDirectory = savedDir; try { Directory.Delete(dir, true); } catch { } }
        }

        // ---- keeper 104 / TDA 083: a --store-import that fails AFTER an earlier batch committed must record the generation the store is
        // actually at, not the one read at open. The keeper's exact trigger: batches of 1, two changed entries, fail at the second.
        static void TestFailedImportRecord(Action<bool, string> assert)
        {
            var dir = NewDir("failedimport"); var store = Path.Combine(dir, "truedat.db");
            try
            {
                var two = new Dictionary<string, TrackEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in VerdictSelfTest.Fixture()) { if (two.Count < 2) two[kv.Key] = kv.Value; }
                var catalog = Path.Combine(dir, "mbxmoods.json");
                StoreSelfTest.WriteCatalog(catalog, two);
                int code = ImportCommand.Run(new ImportOptions { StorePath = store, CatalogPath = catalog, LibraryRoot = @"D:\music", BatchSize = 1, FailAfterEntries = 2 });
                long db = Gen(store);
                var rec = new StoreResultFile();
                bool read = StoreResult.TryRead(StoreResult.PathFor(store), out rec);
                assert(code != 0 && db == 1, $"setup: the import failed after its first batch committed (exit {code}; store at generation {db})");
                assert(read && rec.Outcome == "failed" && rec.Store.Generation == db,
                    $"the failed import's record carries the generation the store is at after the committed batch, not the one read at open (record {(read ? rec.Store.Generation?.ToString() ?? "null" : "unreadable")}, store {db})");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ---- keeper 106 / TDA 084 / keeper 107: a LEDGER (or scan-failure) path under a MIRROR root resolves across roots to an existing
        // DESCRIBED entity and gets a new present location on it — with NO copy row (no catalog entry observed that file). The served-track
        // contract both sides now hold is: present location JOIN content JOIN a copy ROW (its existence, not any non-null field). Such a
        // location is in neither the export nor the served set, so it changes nothing a consumer sees and must NOT advance. (Before RBA's
        // inner join the hub reader left-joined copy and served it — the defect is the reader's, not a missing advance.) A real copy row
        // whose every optional value is null IS served, and a ledger path on a description-less entity is in neither set either.
        static void TestLedgerMirrorLocation(Action<bool, string> assert)
        {
            var dir = NewDir("ledgermirror"); var store = Path.Combine(dir, "truedat.db");
            try
            {
                // the served-track predicate for one root, decided by the copy ROW's existence (keeper 107)
                List<string> ServedPaths(string rootPath)
                {
                    var paths = new List<string>();
                    using var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o"));
                    using var st = os.Db.Prepare("SELECT l.display_path FROM track_location l JOIN library_root r ON r.id = l.root_id " +
                        "JOIN track_content c ON c.entity_id = l.entity_id JOIN track_copy p ON p.location_id = l.id " +
                        "WHERE l.state = 'present' AND r.path = ? ORDER BY l.display_path;");
                    st.Bind(1, rootPath);
                    while (st.Step()) paths.Add(st.GetString(0)!);
                    return paths;
                }
                // the export's own path set for the same root (--store-export --export-root), read back through the catalog reader
                List<string> ExportPaths(string rootPath)
                {
                    var outPath = Path.Combine(dir, "out-" + Guid.NewGuid().ToString("N").Substring(0, 6), "mbxmoods.json");
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    ExportCommand.Execute(new ExportOptions { StorePath = store, OutPath = outPath, ExportRoot = rootPath });
                    var back = new System.Collections.Concurrent.ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
                    Program.LoadExistingMoodsForStore(outPath, back);
                    var paths = new List<string>(back.Keys); paths.Sort(StringComparer.Ordinal);
                    return paths;
                }
                (bool Present, bool Described, bool HasCopyRow) Facts(string displayPath)
                {
                    using var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o"));
                    using var st = os.Db.Prepare("SELECT l.state = 'present', (SELECT count(*) FROM track_content c WHERE c.entity_id = l.entity_id) > 0, " +
                        "(SELECT count(*) FROM track_copy p WHERE p.location_id = l.id) > 0 FROM track_location l WHERE l.display_path = ?;");
                    st.Bind(1, displayPath);
                    return st.Step() ? (st.GetInt64(0) == 1, st.GetInt64(1) == 1, st.GetInt64(2) == 1) : (false, false, false);
                }
                // which copy columns of a location's row hold a value (keeper 107: the served decision must not depend on any of them)
                List<string> NonNullCopyColumns(string displayPath)
                {
                    var cols = new List<string>();
                    using var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o"));
                    var names = TrackEntryMapper.CopyColumnNames;
                    using var st = os.Db.Prepare("SELECT " + string.Join(", ", System.Linq.Enumerable.Select(names, n => "p.\"" + n + "\"")) +
                        " FROM track_copy p JOIN track_location l ON l.id = p.location_id WHERE l.display_path = ?;");
                    st.Bind(1, displayPath);
                    if (st.Step())
                        for (int k = 0; k < names.Count; k++) if (!st.IsNull(k)) cols.Add(names[k]);
                    return cols;
                }
                const string Mirror = @"E:\mirror";
                const string LedgerPath = @"E:\mirror\verdict\a-lavc.mp3";
                const string BarePath = @"E:\mirror\other\bare.mp3";
                var fixture = VerdictSelfTest.Fixture();
                var dirA = Path.Combine(dir, "A"); Directory.CreateDirectory(dirA);
                var catA = Path.Combine(dirA, "mbxmoods.json");
                StoreSelfTest.WriteCatalog(catA, fixture);
                ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = catA, LibraryRoot = @"D:\music" });

                var dirB = Path.Combine(dir, "B"); Directory.CreateDirectory(dirB);
                var catB = Path.Combine(dirB, "mbxmoods.json");
                // B carries the mirrored b-lame (matched by sha) and a bare entry of its own whose copy row has every optional value null
                var bare = new TrackEntry { AudioStreamSha256 = new string('e', 64), LastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                    Features = new TrackFeatures { Bpm = 100, Key = "C", Mode = "major", Mfcc = new double[13] } };
                var b = new Dictionary<string, TrackEntry>(StringComparer.OrdinalIgnoreCase) { [@"E:\mirror\verdict\b-lame.mp3"] = fixture[@"D:\music\verdict\b-lame.mp3"], [BarePath] = bare };
                StoreSelfTest.WriteCatalog(catB, b);
                ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = catB, LibraryRoot = Mirror, RootRole = "mirror" });
                long g0 = Gen(store);
                var bareFacts = Facts(BarePath);
                assert(bareFacts.Present && bareFacts.Described && bareFacts.HasCopyRow, $"setup: the bare entry has a present, described location with a real copy row ({bareFacts})");

                // the keeper's trigger: the SAME B catalog again, plus an errors ledger naming a B path absent as a B location
                File.WriteAllText(Path.Combine(dirB, "mbxmoods-errors.csv"), "FilePath,Error,FailedComponents\r\n" + LedgerPath + ",truncated,decode-12%\r\n");
                ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = catB, LibraryRoot = Mirror, RootRole = "mirror" });
                var ledgerFacts = Facts(LedgerPath);
                assert(ledgerFacts.Present && ledgerFacts.Described && !ledgerFacts.HasCopyRow,
                    $"setup: the ledger minted a present location on A's DESCRIBED entity with NO copy row — the keeper-106 shape ({ledgerFacts})");
                var served = ServedPaths(Mirror); var exported = ExportPaths(Mirror);
                assert(string.Join("|", served) == string.Join("|", exported),
                    $"parity: the export's path set for the mirror root equals the served-track set (present + description + copy row) — export [{string.Join(", ", exported)}] vs served [{string.Join(", ", served)}]");
                assert(!served.Contains(LedgerPath) && !exported.Contains(LedgerPath), "the ledger-only location is in neither the export nor the served set");
                assert(Gen(store) == g0, $"the ledger-only location changes nothing a consumer sees: no advance (stays {g0}; got {Gen(store)})");

                // the scan's failures-only flush (its own transaction) takes the same path: present, described, no copy row -> not served
                long g1 = Gen(store);
                ImportCommand.ExecuteEntries(store, new Dictionary<string, TrackEntry>(), Mirror, "mirror", true, "essentia", "selftest-gen", "scan",
                    new[] { new ScanFailure { Path = @"E:\mirror\verdict\c-128k.mp3", Components = "decode-12%", Error = "truncated", DurationSec = 1 } }, null);
                var failFacts = Facts(@"E:\mirror\verdict\c-128k.mp3");
                assert(failFacts.Present && failFacts.Described && !failFacts.HasCopyRow && !ServedPaths(Mirror).Contains(@"E:\mirror\verdict\c-128k.mp3") && Gen(store) == g1,
                    $"a failures-only scan flush onto a described entity under a mirror: no copy row, not served, no advance ({failFacts}; {g1} -> {Gen(store)})");

                // negative: a ledger path matching no described entity — a description-less entity in neither set
                long g2 = Gen(store); int n2 = ServedPaths(Mirror).Count;
                File.WriteAllText(Path.Combine(dirB, "mbxmoods-errors.csv"), "FilePath,Error,FailedComponents\r\n" + @"E:\mirror\elsewhere\nothing.mp3" + ",truncated,decode-12%\r\n");
                ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = catB, LibraryRoot = Mirror, RootRole = "mirror" });
                assert(ServedPaths(Mirror).Count == n2 && !ExportPaths(Mirror).Contains(@"E:\mirror\elsewhere\nothing.mp3") && Gen(store) == g2,
                    $"a ledger path on a description-less entity is in neither set and does not advance (served {n2} -> {ServedPaths(Mirror).Count}; generation {g2} -> {Gen(store)})");

                // LAST, after every import (a later import would rightly re-fill it): keeper 107's case — a copy row with EVERY value NULL must
                // still be served. No producer path writes one (the tags arrive as empty strings), so it is CONSTRUCTED: every copy column of
                // the bare entry's row set to NULL, the row itself kept.
                using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
                {
                    var set = string.Join(", ", System.Linq.Enumerable.Select(TrackEntryMapper.CopyColumnNames, n => "\"" + n + "\" = NULL"));
                    os.Dal.ExecBound("UPDATE track_copy SET " + set + " WHERE location_id = (SELECT id FROM track_location WHERE display_path = ?);", BarePath);
                }
                var bareCols = NonNullCopyColumns(BarePath); var bareNow = Facts(BarePath);
                assert(bareCols.Count == 0 && bareNow.HasCopyRow, $"constructed: the bare entry's copy row exists with every copy column NULL (non-null: [{string.Join(", ", bareCols)}]; row {bareNow.HasCopyRow})");
                served = ServedPaths(Mirror); exported = ExportPaths(Mirror);
                assert(served.Contains(BarePath) && exported.Contains(BarePath), "an all-NULL copy row IS served and exported: the copy ROW's existence decides, never a value in it");
                assert(string.Join("|", served) == string.Join("|", exported), $"parity holds with the all-NULL row too (export {exported.Count} paths, served {served.Count})");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ---- --store-import: one advance per committed batch that changed something, none for a re-import of the same catalog
        static void TestStoreImport(Action<bool, string> assert)
        {
            var dir = NewDir("import"); var store = Path.Combine(dir, "truedat.db");
            try
            {
                var entries = VerdictSelfTest.Fixture();
                var catalog = Path.Combine(dir, "mbxmoods.json");
                StoreSelfTest.WriteCatalog(catalog, entries);
                assert(entries.Count == 3, $"the fixture has three entries ({entries.Count})");

                ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = catalog, LibraryRoot = @"D:\music", BatchSize = 2 });
                assert(Gen(store) == 2, $"an import of 3 new entries in batches of 2 commits two visible batches: two advances (0 -> 2; got {Gen(store)})");

                var r = ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = catalog, LibraryRoot = @"D:\music", BatchSize = 2 });
                assert(Gen(store) == 2 && r.Generation == 2, $"re-importing the same catalog changes nothing: no advance, and the verb reports the committed generation (stays 2; store {Gen(store)}, report {r.Generation})");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ---- --edge-arrays-drop: the real verb; a dry run and a drop with nothing to drop are not mutations
        static void TestArraysDrop(Action<bool, string> assert)
        {
            var dir = NewDir("drop"); var store = Path.Combine(dir, "truedat.db");
            try
            {
                var fold = global::Truedat.Edges.EdgeSelfTest.SyntheticFold(dir, 240, true);
                using (var os = OpenStore.Open(store, true, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
                using (var tx = os.Db.BeginTransaction())
                {
                    global::Truedat.Edges.EdgeStore.Write(os.Dal, new string('6', 64), "content_hash:audio-stream-sha256", null, fold, true);
                    tx.Complete();
                }
                long g0 = Gen(store);

                Program.RunEdgeArraysDrop(store, dryRun: true);
                assert(Gen(store) == g0, $"--edge-arrays-drop --dry-run changes nothing: no advance (stays {g0}; got {Gen(store)})");

                Program.RunEdgeArraysDrop(store, dryRun: false);
                assert(Gen(store) == g0 + 1, $"--edge-arrays-drop clearing captured arrays is a visible mutation: one advance ({g0} -> {g0 + 1}; got {Gen(store)})");

                Program.RunEdgeArraysDrop(store, dryRun: false);
                assert(Gen(store) == g0 + 1, $"a second drop with nothing left to clear is no mutation: no advance (stays {g0 + 1}; got {Gen(store)})");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ---- the open-time verdict re-derivation (VerdictStore.EnsureCurrent) when the build's algorithm tag changes
        static void TestVerdictRederive(Action<bool, string> assert)
        {
            var dir = NewDir("verdict"); var store = Path.Combine(dir, "truedat.db");
            try
            {
                Scan(store, VerdictSelfTest.Fixture());
                long g0 = Gen(store);
                long rows;
                using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o"))) rows = os.Dal.ScalarLong("SELECT count(*) FROM verdict;");
                assert(rows > 0, $"the fixture store holds verdict rows to re-derive ({rows})");

                VerdictStore.MethodOverrideForTest = "truedat-v1-generation-selftest";
                try
                {
                    long g1 = Gen(store);   // this open sees the changed tag and re-derives
                    assert(g1 == g0 + 1, $"a re-derivation that rewrites the verdict rows is a visible mutation: one advance at open ({g0} -> {g0 + 1}; got {g1})");
                    assert(Gen(store) == g0 + 1, $"the next open with the same tag re-derives nothing: no advance (stays {g0 + 1}; got {Gen(store)})");
                }
                finally { VerdictStore.MethodOverrideForTest = null; }

                var empty = Path.Combine(NewDir("verdict-empty"), "truedat.db");
                using (var os = OpenStore.Open(empty, true, VersionInfo.Display, DateTime.UtcNow.ToString("o"))) { }
                VerdictStore.MethodOverrideForTest = "truedat-v1-generation-selftest";
                try { assert(Gen(empty) == 0, $"a re-derivation over an empty verdict table changes nothing: no advance (stays 0; got {Gen(empty)})"); }
                finally { VerdictStore.MethodOverrideForTest = null; try { Directory.Delete(Path.GetDirectoryName(empty)!, true); } catch { } }
            }
            finally { VerdictStore.MethodOverrideForTest = null; try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
