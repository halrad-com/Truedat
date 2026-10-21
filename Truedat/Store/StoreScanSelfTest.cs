using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Truedat.Store
{
    /// <summary>Self-tests for the store-authoritative scan (store-v3 T18): the load precondition table (TDA 106 / keeper 129), cache-hit
    /// refreshes written as copy facts (never an Essentia run), a moved file's old location retired in the flush's own transaction and gone
    /// from the export, and publication of the triple from the store. No extractor needed; the CLI receipts carry the real scans.</summary>
    internal static class StoreScanSelfTest
    {
        internal static void Run(Action<bool, string> assert)
        {
            Console.WriteLine("Store-scan self-test (T18i: the scan loads from the store after the provenance precondition; T18: cache hits as copy facts, moves retired, publish from the store)");
            try { TestPreconditionTable(assert); }
            catch (Exception ex) { assert(false, $"StoreScan.Precondition: threw {ex.GetType().Name}: {ex.Message}"); }
            try { TestCacheHitAndRetire(assert); }
            catch (Exception ex) { assert(false, $"StoreScan.CacheHitRetire: threw {ex.GetType().Name}: {ex.Message}"); }
            finally { ScanSink.Reset(); }
        }

        sealed class Dir : IDisposable
        {
            internal readonly string Root;
            internal Dir() { Root = Path.Combine(Path.GetTempPath(), "truedat-storescan-selftest-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); }
            internal string P(string name) => Path.Combine(Root, name);
            public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
        }

        static TrackEntry E(string title, double bpm, char sha) => new TrackEntry
        {
            Features = new TrackFeatures { Artist = "A", Title = title, Bpm = bpm, Mfcc = new[] { 1.5, -2.25 } },
            LastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), AudioStreamSha256 = new string(sha, 64),
        };

        static Dictionary<string, TrackEntry> Lib(params (string Name, double Bpm, char Sha)[] items)
        {
            var d = new Dictionary<string, TrackEntry>(PathComparer.Instance);
            foreach (var (n, bpm, sha) in items) d[@"D:\music\" + n] = E(n, bpm, sha);
            return d;
        }

        static StoreScan.LoadResult Load(string store, string catalog, out ConcurrentDictionary<string, TrackEntry> target)
        {
            target = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
            using (StoreLog.Quiet()) return StoreScan.LoadAuthoritative(store, catalog, target);
        }

        static void TestPreconditionTable(Action<bool, string> assert)
        {
            var two = Lib(("a.flac", 120, 'a'), ("b.flac", 121, 'b'));
            // fresh: no store, no catalog
            using (var d = new Dir())
            {
                var r = Load(d.P("truedat.db"), d.P("mbxmoods.json"), out var t);
                assert(!r.Refused && r.Case == "fresh" && t.Count == 0 && !File.Exists(d.P("truedat.db")), "fresh: no store, no catalog -> loads nothing, creates nothing");
            }
            // no store yet + a catalog with entries -> refuse naming --store-import (I3)
            using (var d = new Dir())
            {
                StoreSelfTest.WriteCatalog(d.P("mbxmoods.json"), two);
                var r = Load(d.P("truedat.db"), d.P("mbxmoods.json"), out var t);
                assert(r.Refused && r.Case == "no-store-yet" && r.Refusal!.Contains("--store-import") && !File.Exists(d.P("truedat.db")),
                    $"no store yet, a catalog with entries -> refused naming --store-import, no store created ({r.Refusal})");
            }
            // a store, catalog ABSENT -> rebuild from the store
            using (var d = new Dir())
            {
                ImportCommand.ExecuteEntries(d.P("truedat.db"), two, @"D:\music", "primary", true, "essentia", "x", "fixture", null, null);
                var r = Load(d.P("truedat.db"), d.P("mbxmoods.json"), out var t);
                assert(!r.Refused && r.Case == "rebuild" && t.Count == 2 && r.RootPath == @"D:\music", $"catalog absent -> the store is authoritative, 2 entries loaded ({r.Case}, {t.Count})");
            }
            // a store + its own export (origin, equal generation) -> agree; then a catalog-only removal at the same generation -> refuse
            using (var d = new Dir())
            {
                var store = d.P("truedat.db"); var cat = d.P("mbxmoods.json");
                ImportCommand.ExecuteEntries(store, two, @"D:\music", "primary", true, "essentia", "x", "fixture", null, null);
                ExportCommand.Execute(new ExportOptions { StorePath = store, OutPath = cat });
                var r = Load(store, cat, out var t);
                assert(!r.Refused && r.Case == "equal-generation" && t.Count == 2, $"equal generation, same paths -> loads ({r.Case})");
                // drop b from the catalog file while keeping its header (a catalog-only edit)
                var text = File.ReadAllText(cat);
                var cut = text.Replace("\"D:\\\\music\\\\b.flac\":", "\"D:\\\\music\\\\zz-renamed.flac\":");
                File.WriteAllText(cat, cut);
                var r2 = Load(store, cat, out _);
                assert(r2.Refused && r2.Case == "equal-generation-disagree" && r2.Refusal!.Contains("zz-renamed.flac") && r2.Refusal.Contains("b.flac"),
                    $"equal generation but the catalog's paths changed -> refused naming both sides ({r2.Refusal})");
            }
            // store ahead: export at generation N, then a committed change -> store wins
            using (var d = new Dir())
            {
                var store = d.P("truedat.db"); var cat = d.P("mbxmoods.json");
                ImportCommand.ExecuteEntries(store, two, @"D:\music", "primary", true, "essentia", "x", "fixture", null, null);
                ExportCommand.Execute(new ExportOptions { StorePath = store, OutPath = cat });
                ImportCommand.ExecuteEntries(store, Lib(("c.flac", 122, 'c')), @"D:\music", "primary", true, "essentia", "x", "fixture", null, null);
                var r = Load(store, cat, out var t);
                assert(!r.Refused && r.Case == "store-ahead" && t.Count == 3 && t.ContainsKey(@"D:\music\c.flac"), $"store ahead of the files -> the store wins, 3 entries ({r.Case}, {t.Count})");
            }
            // no origin (a v1 catalog) that agrees exactly -> proceed; one that holds a NEWER value at a same path -> refuse (the I3 / same-path case)
            using (var d = new Dir())
            {
                var store = d.P("truedat.db"); var cat = d.P("mbxmoods.json");
                ImportCommand.ExecuteEntries(store, two, @"D:\music", "primary", true, "essentia", "x", "fixture", null, null);
                // a v1 catalog with the store's own values: write it from the store's entries
                var fromStore = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
                ExportCommand.Execute(new ExportOptions { StorePath = store, OutPath = d.P("tmp-export.json") });
                Program.LoadExistingMoodsForStore(d.P("tmp-export.json"), fromStore);
                StoreSelfTest.WriteCatalog(cat, fromStore);
                var r = Load(store, cat, out var t);
                assert(!r.Refused && r.Case == "no-origin-agree" && t.Count == 2, $"no origin, exact agreement -> proceeds ({r.Case}: {r.Refusal})");
                var newer = new Dictionary<string, TrackEntry>(fromStore, PathComparer.Instance);
                newer[@"D:\music\a.flac"] = E("a.flac", 200, 'a');                      // re-analysed: reached the catalog, not the store
                StoreSelfTest.WriteCatalog(cat, newer);
                var r2 = Load(store, cat, out _);
                assert(r2.Refused && r2.Case == "no-origin-disagree" && r2.Refusal!.Contains("1 with different values") && r2.Refusal.Contains(@"D:\music\a.flac") && r2.Refusal.Contains("--store-import"),
                    $"no origin, a same-path NEW value in the catalog -> refused, naming the path and the remedies ({r2.Refusal})");
            }
            // The operator's sequence on a real library (MB3, 2026-10-18): a catalog the SCAN wrote, then `--store-import` of it, then a
            // plain scan. The test above writes its catalog FROM the store, so it never met the two differences an import makes by
            // design, and the scan refused 140 of 7,641 entries naming a remedy (--store-import) that cannot clear them.
            using (var d = new Dir())
            {
                var store = d.P("truedat.db"); var cat = d.P("mbxmoods.json");
                // (1) the old writer's spelling: one ulp above the 6-dp decimal ("0.0028770000000000002"); the store holds the field at its
                // declared precision and gives back the double nearest 0.002877 — the same value at that precision, different text
                var ulp = E("ulp.flac", 120, 'u');
                ulp.Features.SpectralRms = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(0.002877) + 1);
                // (2) duplicate audio: two copies of one media, the second's description within run-to-run noise of the first's;
                // the store keeps ONE description per media, so the second copy comes back carrying the first's values
                var first = E("dup-1.flac", 120.0, 'd');
                var second = E("dup-2.flac", 120.5, 'd');
                var cataloged = new Dictionary<string, TrackEntry>(PathComparer.Instance)
                {
                    [@"D:\music\ulp.flac"] = ulp, [@"D:\music\dup-1.flac"] = first, [@"D:\music\dup-2.flac"] = second,
                };
                StoreSelfTest.WriteCatalog(cat, cataloged);
                var fromCatalog = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
                Program.LoadExistingMoodsForStore(cat, fromCatalog);
                using (StoreLog.Quiet()) ImportCommand.ExecuteEntries(store, fromCatalog, @"D:\music", "primary", true, "import", "1", "fixture", null, null);
                var r = Load(store, cat, out var t);
                assert(!r.Refused && r.Case == "no-origin-agree" && t.Count == 3,
                    $"no origin, a catalog just imported into the store (one-ulp spelling + a duplicate copy) -> proceeds ({r.Case}: {r.Refusal})");

                // ...but the excuse is the shared DESCRIPTION and the precision, nothing else: a copy fact still has to agree
                var moved = new Dictionary<string, TrackEntry>(cataloged, PathComparer.Instance);
                moved[@"D:\music\dup-2.flac"] = E("dup-2.flac", 120.5, 'd');
                moved[@"D:\music\dup-2.flac"].LastModified = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
                StoreSelfTest.WriteCatalog(cat, moved);
                var r2 = Load(store, cat, out _);
                assert(r2.Refused && r2.Case == "no-origin-disagree" && r2.Refusal!.Contains("1 with different values") && r2.Refusal.Contains("dup-2.flac"),
                    $"no origin, a duplicate copy whose own lastModified differs -> still refused ({r2.Refusal})");

                // ...and a value that differs beyond the declared precision on a single-copy entry is still a difference
                var changed = new Dictionary<string, TrackEntry>(cataloged, PathComparer.Instance);
                changed[@"D:\music\ulp.flac"] = E("ulp.flac", 120, 'u');
                changed[@"D:\music\ulp.flac"].Features.SpectralRms = 0.002878;
                StoreSelfTest.WriteCatalog(cat, changed);
                var r3 = Load(store, cat, out _);
                assert(r3.Refused && r3.Case == "no-origin-disagree" && r3.Refusal!.Contains("1 with different values") && r3.Refusal.Contains("ulp.flac"),
                    $"no origin, a value one step beyond the declared precision -> still refused ({r3.Refusal})");
            }
            // A catalog an older build wrote holds the old three-band spectralFlatness average; the store converts its own rows to
            // Essentia's Bark value at open (StoreOpen.ConvertSpectralFlatness). The two hold the same facts — the conversion is a
            // function of the stored Bark value — so the scan must proceed, not refuse every track naming a --store-import that
            // cannot clear it.
            using (var d = new Dir())
            {
                var store = d.P("truedat.db"); var cat = d.P("mbxmoods.json");
                var old = E("flat.flac", 120, 'f');
                old.Features.SpectralFlatness = 0.2496; old.Features.BarkFlatness = 0.2396; old.Features.ErbFlatness = 0.2498; old.Features.MelFlatness = 0.2594;
                StoreSelfTest.WriteCatalog(cat, new Dictionary<string, TrackEntry>(PathComparer.Instance) { [@"D:\music\flat.flac"] = old });
                var fromCatalog = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
                Program.LoadExistingMoodsForStore(cat, fromCatalog);
                using (StoreLog.Quiet()) ImportCommand.ExecuteEntries(store, fromCatalog, @"D:\music", "primary", true, "import", "1", "fixture", null, null);
                // ...and put the old average back in the store's row, as an older build wrote it, so the open-time conversion runs too
                using (StoreLog.Quiet()) using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
                    os.Db.Exec("UPDATE track_content SET spectralFlatness = 2496;");
                var r = Load(store, cat, out var t);
                assert(!r.Refused && r.Case == "no-origin-agree" && t.Count == 1 && t[@"D:\music\flat.flac"].Features.SpectralFlatness == 0.2396,
                    $"no origin, a catalog holding the old flatness average beside a converted store -> proceeds with Essentia's value ({r.Case}: {r.Refusal})");
            }
            // no origin + a store with no entries for the library -> refuse naming --store-import
            using (var d = new Dir())
            {
                var store = d.P("truedat.db"); var cat = d.P("mbxmoods.json");
                using (StoreLog.Quiet()) using (OpenStore.Open(store, true, VersionInfo.Display, DateTime.UtcNow.ToString("o"))) { }
                StoreSelfTest.WriteCatalog(cat, two);
                var r = Load(store, cat, out _);
                assert(r.Refused && r.Case == "no-origin-store-empty" && r.Refusal!.Contains("--store-import"), $"no origin, an empty store -> refused naming --store-import ({r.Case})");
            }
        }

        static void TestCacheHitAndRetire(Action<bool, string> assert)
        {
            using (var d = new Dir())
            {
                var store = d.P("truedat.db");
                ScanSink.Reset();
                ImportCommand.ExecuteEntries(store, Lib(("a.flac", 120, 'a'), ("b.flac", 121, 'b')), @"D:\music", "primary", true, "essentia", "x", "fixture", null, null);
                long essentiaRunsBefore, gen0;
                using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
                { essentiaRunsBefore = long.Parse(os.Db.ExecScalar("SELECT count(*) FROM run WHERE producer_key = 'essentia';") ?? "0"); gen0 = os.Dal.Generation(); }

                // a cache hit (lastModified refreshed) + a move (b -> b2, same audio): recorded, one flush
                var a2 = E("a.flac", 120, 'a'); a2.LastModified = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
                ScanSink.RecordCacheHit(@"D:\music\a.flac", a2);
                ScanSink.RecordCacheHit(@"D:\music\moved\b.flac", E("b.flac", 121, 'b'));
                ScanSink.RecordRetire(@"D:\music\b.flac");
                ScanSinkReport rep;
                using (StoreLog.Quiet()) rep = ScanSink.Flush(store, "x", null, final: false);
                long essentiaRunsAfter, gen1; string? aLm; List<string> exported;
                using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
                {
                    essentiaRunsAfter = long.Parse(os.Db.ExecScalar("SELECT count(*) FROM run WHERE producer_key = 'essentia';") ?? "0");
                    gen1 = os.Dal.Generation();
                    aLm = os.Db.ExecScalar("SELECT last_modified_utc FROM track_location WHERE display_path = 'D:\\music\\a.flac';");
                    var root = os.Dal.ListRoots().First(x => x.Role == "primary").Id;
                    exported = os.Dal.ReadEntries(root).Select(x => x.DisplayPath).OrderBy(x => x).ToList();
                }
                assert(rep.Ok && rep.CopyOnly == 2 && rep.Retired == 1, $"one flush: 2 cache-hit refreshes as copy facts, 1 location retired ({rep.Ok}/{rep.CopyOnly}/{rep.Retired}: {rep.Error})");
                assert(essentiaRunsAfter == essentiaRunsBefore, $"cache hits are never recorded as Essentia measurements (essentia runs {essentiaRunsBefore} -> {essentiaRunsAfter})");
                assert(aLm != null && aLm.StartsWith("2026-06-01"), $"the cache hit's copy fact reached the store (lastModified {aLm})");
                assert(exported.SequenceEqual(new[] { @"D:\music\a.flac", @"D:\music\moved\b.flac" }), $"the export reads present locations only: the moved file's old path is gone ({string.Join(", ", exported)})");
                assert(gen1 > gen0, $"the retirement and the new location are a consumer-visible change: the generation advanced ({gen0} -> {gen1})");
            }
        }
    }
}
