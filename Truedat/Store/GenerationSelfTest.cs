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
            Block(assert, "store import", () => TestStoreImport(assert));
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
