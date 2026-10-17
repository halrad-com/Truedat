using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Truedat.Store;

namespace Truedat.Edges
{
    /// <summary>
    /// hub:H02 — the store's SELECTED edge version, store_meta 'edges.selectedVersion' (TDA 086, keeper 108, RBA 068/069). Driven
    /// through the real verbs (--store-export via ExportCommand.Run, --edges-export via Program.RunEdgesExport). Pins: the key and the
    /// value format; absent until a publish site runs on a store holding edge results; the stamp equals the companion header's
    /// producerVersion; a CHANGED stamp advances the generation once, an unchanged one never; the stamp and its advance are committed
    /// BEFORE publication, so a publish that fails afterwards leaves both committed, claims no published file, and leaves an older
    /// companion behind by the existing generation rule; an older-version-only track is absent from the companion.
    /// </summary>
    internal static class EdgeStampSelfTest
    {
        const string Current = "edges@v2;e30;p10";
        const string Older = "edges@v1;e30;p10";
        const string KindSha = "content_hash:audio-stream-sha256";

        internal static void Run(Action<bool, string> assert)
        {
            Console.WriteLine("Edge stamp self-test (hub:H02: store_meta 'edges.selectedVersion' — written by the two publish sites before their snapshot, advancing only on change)");
            Block(assert, "key and format", () => TestKeyAndFormat(assert));
            Block(assert, "edges-export restamp", () => TestEdgesExportRestamp(assert));
            Block(assert, "no edge results", () => TestNoEdgeResults(assert));
            Block(assert, "older-only rows", () => TestOlderOnly(assert));
            Block(assert, "publish fails after restamp", () => TestPublishFailsAfterRestamp(assert));
        }

        static void Block(Action<bool, string> assert, string name, Action body)
        {
            try { body(); }
            catch (Exception ex) { assert(false, $"edge stamp {name}: threw {ex.GetType().Name}: {ex.Message}"); }
        }

        sealed class Dir : IDisposable
        {
            internal readonly string Root;
            internal Dir(string tag) { Root = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "stamp-" + tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(Root); }
            internal string P(string name) => Path.Combine(Root, name);
            public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
        }

        /// <summary>A store with the verdict fixture's entries (so an export has a catalog to publish) and edge rows written through the
        /// real EdgeStore.Write: one content key per (key, version) pair given.</summary>
        static void BuildStore(string dir, string store, params (string Key, string Version)[] edges)
        {
            ImportCommand.ExecuteEntries(store, VerdictSelfTest.Fixture(), @"D:\music", "primary", true, "import", "1", "fixture", null, null);
            if (edges.Length == 0) return;
            using var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o"));
            using var tx = os.Db.BeginTransaction();
            foreach (var (key, version) in edges)
            {
                var fold = EdgeSelfTest.SyntheticFold(dir, 240, true);
                fold.ProducerVersion = version;
                EdgeStore.Write(os.Dal, key, KindSha, null, fold, false);
            }
            tx.Complete();
        }

        static (string? Stamp, long Generation) State(string store)
        {
            using var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o"));
            return (os.Dal.MetaGet(EdgeStore.SelectedVersionKey), os.Dal.Generation());
        }

        static (string? ProducerVersion, long? Generation, List<string> Tracks) Companion(string path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var pv = root.TryGetProperty("producerVersion", out var v) ? v.GetString() : null;
            long? gen = root.TryGetProperty("origin", out var o) && o.TryGetProperty("generation", out var g) ? g.GetInt64() : (long?)null;
            var tracks = new List<string>();
            if (root.TryGetProperty("tracks", out var t)) foreach (var p in t.EnumerateObject()) tracks.Add(p.Name);
            tracks.Sort(StringComparer.Ordinal);
            return (pv, gen, tracks);
        }

        static void TestKeyAndFormat(Action<bool, string> assert)
        {
            using var d = new Dir("key");
            var store = d.P("truedat.db");
            BuildStore(d.Root, store, (new string('5', 64), Current));
            var before = State(store);
            assert(EdgeStore.SelectedVersionKey == "edges.selectedVersion", $"the key is spelled 'edges.selectedVersion' (the hub reads it by name; got '{EdgeStore.SelectedVersionKey}')");
            assert(before.Stamp == null, $"absent until a publish site runs, even with edge rows in the store (got '{before.Stamp}')");
            assert(new EdgeSettings().ProducerVersion == Current, $"the build's producer version is the pinned format '{Current}' (got '{new EdgeSettings().ProducerVersion}')");

            var outPath = d.P(Path.Combine("out", "mbxmoods.json"));
            int code = ExportCommand.Run(new ExportOptions { StorePath = store, OutPath = outPath });
            var after = State(store);
            var comp = Companion(Path.Combine(Path.GetDirectoryName(outPath)!, EdgeCompanion.FileName));
            assert(code == 0 && after.Stamp == Current, $"--store-export stamps the selected version '{Current}' (exit {code}; stamp '{after.Stamp}')");
            assert(after.Stamp == comp.ProducerVersion, $"the stamp equals the companion header's producerVersion byte for byte ('{after.Stamp}' vs '{comp.ProducerVersion}')");
            assert(after.Generation == before.Generation + 1 && comp.Generation == after.Generation,
                $"stamping a new value is a visible mutation: one advance, and the companion carries it ({before.Generation} -> {after.Generation}; companion origin.generation {comp.Generation})");

            code = ExportCommand.Run(new ExportOptions { StorePath = store, OutPath = outPath });
            var again = State(store);
            assert(code == 0 && again.Stamp == Current && again.Generation == after.Generation, $"a second export with the same value writes nothing and does not advance ({after.Generation} -> {again.Generation})");
        }

        static void TestEdgesExportRestamp(Action<bool, string> assert)
        {
            using var d = new Dir("edgesexport");
            var store = d.P("truedat.db");
            BuildStore(d.Root, store, (new string('5', 64), Current));
            using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
                os.Dal.MetaSet(EdgeStore.SelectedVersionKey, Older);   // setup: an older build's stamp, set without its advance
            var before = State(store);
            var target = d.P(EdgeCompanion.FileName);
            int code = Program.RunEdgesExport(store, target, new EdgeSettings());
            var after = State(store);
            var comp = Companion(target);
            assert(code == 0 && after.Stamp == Current && after.Stamp == comp.ProducerVersion,
                $"--edges-export restamps an older stamp to the version its companion is built with ('{before.Stamp}' -> '{after.Stamp}'; companion '{comp.ProducerVersion}'; exit {code})");
            assert(after.Generation == before.Generation + 1 && comp.Generation == after.Generation,
                $"the restamp advances once and the companion carries the new generation ({before.Generation} -> {after.Generation}; companion {comp.Generation})");
            code = Program.RunEdgesExport(store, target, new EdgeSettings());
            assert(code == 0 && State(store).Generation == after.Generation, $"a second --edges-export does not advance (stays {after.Generation}; got {State(store).Generation})");
        }

        static void TestNoEdgeResults(Action<bool, string> assert)
        {
            using var d = new Dir("noedges");
            var store = d.P("truedat.db");
            BuildStore(d.Root, store);
            var before = State(store);
            int code = ExportCommand.Run(new ExportOptions { StorePath = store, OutPath = d.P(Path.Combine("out", "mbxmoods.json")) });
            var after = State(store);
            assert(code == 0 && after.Stamp == null && after.Generation == before.Generation,
                $"a store that never captured edges stays unstamped and the export does not advance (stamp '{after.Stamp}'; generation {before.Generation} -> {after.Generation})");
        }

        static void TestOlderOnly(Action<bool, string> assert)
        {
            using var d = new Dir("olderonly");
            var store = d.P("truedat.db");
            string x = new string('7', 64), y = new string('8', 64);
            BuildStore(d.Root, store, (x, Older), (y, Current));
            var outPath = d.P(Path.Combine("out", "mbxmoods.json"));
            ExportCommand.Run(new ExportOptions { StorePath = store, OutPath = outPath });
            var comp = Companion(Path.Combine(Path.GetDirectoryName(outPath)!, EdgeCompanion.FileName));
            assert(State(store).Stamp == Current && comp.Tracks.Count == 1 && comp.Tracks[0] == y,
                $"a track with rows only under an older version is absent from the companion; the stamp names the current version (tracks [{string.Join(", ", comp.Tracks)}], stamp '{State(store).Stamp}')");

            using var d2 = new Dir("olderonly2");
            var store2 = d2.P("truedat.db");
            BuildStore(d2.Root, store2, (x, Older));
            var b2 = State(store2);
            var out2 = d2.P(Path.Combine("out", "mbxmoods.json"));
            ExportCommand.Run(new ExportOptions { StorePath = store2, OutPath = out2 });
            var a2 = State(store2);
            assert(a2.Stamp == Current && a2.Generation == b2.Generation + 1 && !File.Exists(Path.Combine(Path.GetDirectoryName(out2)!, EdgeCompanion.FileName)),
                $"a store holding ONLY older-version rows is stamped to the current version (so a reader serves what the current companion would: nothing), with one advance and no companion ('{a2.Stamp}'; {b2.Generation} -> {a2.Generation})");
        }

        static void TestPublishFailsAfterRestamp(Action<bool, string> assert)
        {
            using var d = new Dir("pubfail");
            var store = d.P("truedat.db");
            BuildStore(d.Root, store, (new string('5', 64), Current));
            var outPath = d.P(Path.Combine("out", "mbxmoods.json"));
            int code = ExportCommand.Run(new ExportOptions { StorePath = store, OutPath = outPath });
            var compPath = Path.Combine(Path.GetDirectoryName(outPath)!, EdgeCompanion.FileName);
            var first = State(store); var oldComp = Companion(compPath);
            assert(code == 0 && oldComp.Generation == first.Generation, $"setup: a first export published a companion at generation {first.Generation}");

            using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
                os.Dal.MetaSet(EdgeStore.SelectedVersionKey, Older);   // setup: the stored stamp differs, so this export restamps
            int failCode;
            using (new FileStream(outPath, FileMode.Open, FileAccess.Read, FileShare.None))   // the catalog is held open: publication must fail
                failCode = ExportCommand.Run(new ExportOptions { StorePath = store, OutPath = outPath });
            var after = State(store);
            var rec = new StoreResultFile();
            bool read = StoreResult.TryRead(StoreResult.PathFor(store), out rec);
            assert(failCode != 0, $"setup: the export failed at publication (exit {failCode})");
            assert(after.Stamp == Current && after.Generation == first.Generation + 1,
                $"the restamp and its advance were committed BEFORE publication and survive its failure (stamp '{after.Stamp}'; generation {first.Generation} -> {after.Generation})");
            assert(read && rec.Outcome == "failed" && !rec.Export.Performed && rec.Export.Catalog == null && rec.Export.Companion == null,
                $"the failed record claims no published file (outcome {rec.Outcome}; performed {rec.Export?.Performed}; catalog {(rec.Export?.Catalog == null ? "none" : rec.Export.Catalog.Path)}; companion {(rec.Export?.Companion == null ? "none" : rec.Export.Companion.Path)})");
            // The ruled publish order moves the live companion ASIDE first (operator call #7, Publication step 1), so an interrupted
            // publish leaves NO previous companion live: a hub sees "no edges", never the old generation's beside the new store.
            // The aside copy stays as evidence and still names its own, older generation.
            var aside = compPath + Publication.CompanionAsideSuffix;
            assert(!File.Exists(compPath), "after the failed publish no companion is live (it was set aside before the catalog rename failed)");
            var stale = File.Exists(aside) ? Companion(aside) : (null, (long?)null, new List<string>());
            assert(stale.Generation == first.Generation && stale.Generation != after.Generation,
                $"the aside copy keeps its own generation ({stale.Generation?.ToString() ?? "no aside file"}), behind the store ({after.Generation}) — the existing binding would reject it");
        }
    }
}
