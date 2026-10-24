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
            foreach (bool foreignFirst in new[] { true, false })
            {
                try { TestMirrorWritesStayOnWriter(assert, foreignFirst); }
                catch (Exception ex) { assert(false, $"StoreScan.MirrorWriter ({(foreignFirst ? "foreign-first" : "writer-first")}): threw {ex.GetType().Name}: {ex.Message}"); }
            }
            try { TestEquivalentMirrorRoleCollision(assert); }
            catch (Exception ex) { assert(false, $"StoreScan.MirrorRoleSpelling: threw {ex.GetType().Name}: {ex.Message}"); }
            foreach (bool mirrorFirst in new[] { true, false })
            {
                try { TestLegacyRootAliasRepair(assert, mirrorFirst); }
                catch (Exception ex) { assert(false, $"StoreScan.LegacyRootAliasRepair ({(mirrorFirst ? "mirror-first" : "primary-first")}): threw {ex.GetType().Name}: {ex.Message}"); }
            }
            StoreStopSelfTest.Run(assert);
            StoreStopCliSelfTest.Run(assert);
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
            // catalog ahead: the catalog came from another store state -> refused. The remedy store-info names is --store-import; after
            // it the store must be AHEAD of the catalog it absorbed, or the next scan refuses again (rescan-3 R5: an import that changes
            // nothing visible advances nothing, so the generation stayed below the catalog's).
            using (var d = new Dir())
            {
                var store = d.P("truedat.db"); var cat = d.P("mbxmoods.json");
                ImportCommand.ExecuteEntries(store, two, @"D:\music", "primary", true, "essentia", "x", "fixture", null, null);
                using (StoreLog.Quiet()) ExportCommand.Execute(new ExportOptions { StorePath = store, OutPath = cat });
                using (var os = OpenStore.Open(store, create: false, "selftest", DateTime.UtcNow.ToString("o")))
                using (var tx = os.Db.BeginTransaction()) { os.Dal.MetaSet(StoreDal.GenerationKey, "0"); tx.Complete(); }   // the catalog's origin is now ahead
                CatalogOrigin.TryRead(cat, out var origin);
                var refused = Load(store, cat, out _);
                assert(refused.Refused && refused.Case == "catalog-ahead", $"catalog ahead of the store -> refused ({refused.Case})");
                ImportReport imp; using (StoreLog.Quiet()) imp = ImportCommand.Execute(new ImportOptions { StorePath = store, CatalogPath = cat, LibraryRoot = @"D:\music" });
                var after = Load(store, cat, out var t2);
                assert(!after.Refused && after.Case == "store-ahead" && after.Generation > origin.Generation && t2.Count == 2,
                    $"after --store-import of a catalog that was ahead, the store supersedes it: the scan loads store-ahead (case {after.Case}, store generation {after.Generation} vs catalog {origin.Generation}, import generation {imp.Generation})");
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

        // Synthetic machine rows exercise the real import write path locally. They do not simulate a second OS host,
        // network delivery, concurrent two-box writes, or decide R1b/D3's same-host mirror-to-primary policy.
        static void TestMirrorWritesStayOnWriter(Action<bool, string> assert, bool foreignFirst)
        {
            using var d = new Dir();
            using var quiet = StoreLog.Quiet();
            const string mirror = @"X:\mirror", nested = @"X:\mirror\nested";
            const string a = @"X:\mirror\a.flac", b = @"X:\mirror\b.flac", c = @"X:\mirror\nested\c.flac";
            const string failed = @"X:\mirror\nested\failed.flac";
            string label = "StoreScan.MirrorWriter " + (foreignFirst ? "foreign-first" : "writer-first");
            string store = d.P("truedat.db"), now = DateTime.UtcNow.ToString("o"), foreign = Uuid7.New();
            var originalTime = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
            var updatedTime = originalTime.AddDays(1);
            var primary = Lib(("a.flac", 120, 'a'), ("b.flac", 120, 'b'), (@"nested\c.flac", 120, 'c'));
            foreach (var entry in primary.Values) { entry.AudioStreamSha256Source = "flac-frames"; entry.Features.SmfmBpm = 100; }
            ImportCommand.ExecuteEntries(store, primary, @"D:\music", "primary", true, "import", "1", "fixture", null, null);
            Dictionary<string, TrackEntry> MirrorEntries(double copyBpm)
            {
                var entries = new Dictionary<string, TrackEntry>(PathComparer.Instance);
                foreach (var item in new[] { (a, 'a'), (b, 'b'), (c, 'c') })
                {
                    var entry = E(Path.GetFileName(item.Item1), 120, item.Item2);
                    entry.AudioStreamSha256Source = "flac-frames"; entry.Features.SmfmBpm = copyBpm;
                    entries.Add(item.Item1, entry);
                }
                return entries;
            }
            void SeedWriter() => ImportCommand.ExecuteEntries(store, MirrorEntries(110), mirror, "mirror", true, "import", "1", "fixture", null, null);
            long foreignRoot = 0, foreignNested = 0;
            void SeedForeign()
            {
                using var os = OpenStore.Open(store, false, VersionInfo.Display, now);
                using var tx = os.Db.BeginTransaction();
                os.Dal.InsertMachine(foreign, "selftest-foreign-" + foreign, now);
                foreignRoot = os.Dal.EnsureRoot(foreign, mirror, "mirror", now);
                foreignNested = os.Dal.EnsureRoot(foreign, nested, "mirror", now);
                foreach (var pair in MirrorEntries(90))
                {
                    bool deep = pair.Key == c;
                    long root = deep ? foreignNested : foreignRoot;
                    CanonicalPath.Split(deep ? nested : mirror, pair.Key, out var relative, out var canonical);
                    var entity = EntityResolver.Resolve(os.Dal, pair.Value.AudioStreamSha256, FacetKinds.ShaFrames, root, canonical, "fixture", now);
                    long location = os.Dal.UpsertLocation(entity.EntityId, root, relative, canonical, pair.Key, originalTime.ToString("o"), now);
                    os.Dal.UpsertCopy(location, null, TrackEntryMapper.ToRow(pair.Value), now);
                }
                tx.Complete();
            }
            if (foreignFirst) { SeedForeign(); SeedWriter(); } else { SeedWriter(); SeedForeign(); }
            long writerRoot, generation;
            using (var os = OpenStore.Open(store, false, VersionInfo.Display, now))
            {
                writerRoot = os.Dal.RootsForWrites(os.Machine.Id, "mirror").Single().Id;
                generation = os.Dal.Generation();
                assert(os.Dal.Count("machine") == 2 && os.Dal.Count("library_root") == 4 && os.Dal.Count("file_entity") == 3
                    && (foreignRoot < writerRoot) == foreignFirst, $"{label}: two machine IDs, separate same-path mirrors, a nested foreign mirror, and three shared audio identities");
            }

            var update = MirrorEntries(130); update.Remove(b);
            foreach (var entry in update.Values) entry.LastModified = updatedTime;
            var failures = new[] { new ScanFailure { Path = failed, Components = "decode", Error = "fixture decode failure", DurationSec = 1 } };
            ImportReport Write() => ImportCommand.ExecuteEntries(store, update, null, "mirror", true, "import", "1", "mirror-writer-test", failures, null, retirePaths: new[] { b });
            var report = Write();
            assert(report.Locations == 2 && report.Retired == 1 && report.ErrorsRows == 1 && report.Generation == generation + 1,
                $"{label}: implicit-root mirror update, retirement, and failure commit together at one new generation");
            using (var os = OpenStore.Open(store, false, VersionInfo.Display, now))
            {
                var own = os.Dal.ReadEntries(writerRoot).ToDictionary(x => x.DisplayPath, x => x.Entry, PathComparer.Instance);
                assert(own.Count == 2 && own.ContainsKey(a) && own.ContainsKey(c) && own.Values.All(e => e.LastModified == updatedTime && e.Features.SmfmBpm == 130),
                    $"{label}: only the writer's copies update and its retired path disappears, including beneath the foreign nested root");
                var other = os.Dal.ReadEntries(new[] { foreignRoot, foreignNested }).ToList();
                assert(other.Count == 3 && other.Any(e => e.DisplayPath == b) && other.All(e => e.Entry.LastModified == originalTime && e.Entry.Features.SmfmBpm == 90),
                    $"{label}: the foreign machine's base and nested mirror copies remain present with their original facts");
                assert(os.Dal.FindLocation(writerRoot, "nested/failed.flac", out _) != null
                    && os.Dal.FindLocation(foreignRoot, "nested/failed.flac", out _) == null && os.Dal.FindLocation(foreignNested, "failed.flac", out _) == null,
                    $"{label}: a failure-only location is recorded under the writer, never either foreign mirror");
            }
            var catalog = d.P("mbxmoods.json");
            var publicationError = StoreScan.Publish(store, catalog, out var publication);
            assert(publicationError == null && publication.Entries == 3 && publication.Generation == report.Generation,
                $"{label}: publication succeeds with three primary entries at the committed generation ({publicationError?.Message})");
            var loaded = Load(store, catalog, out var published);
            assert(!loaded.Refused && loaded.Case == "equal-generation" && published.Count == 3 && published.Keys.All(p => p.StartsWith(@"D:\music\", StringComparison.OrdinalIgnoreCase))
                && published.Values.All(e => e.LastModified == originalTime && e.Features.SmfmBpm == 100 && e.Features.Bpm == 120),
                $"{label}: the authoritative library still contains all primary copies and their unchanged facts; mirrors do not enter publication");
            var repeated = Write();
            assert(repeated.Generation == report.Generation && repeated.Retired == 0 && repeated.ErrorsRows == 0 && repeated.VisibleChanges == 0,
                $"{label}: repeating the mirror update does not advance generation, retire again, or duplicate the failure");
        }

        // R1b/D3 decides refusal versus in-place promotion. Either policy must treat equivalent Windows paths alike;
        // use the exact-spelling outcome as the control, without choosing that policy in this regression.
        static void TestEquivalentMirrorRoleCollision(Action<bool, string> assert)
        {
            bool? controlAccepted = null;
            foreach (var shape in new[]
            {
                (Name: "exact", Held: @"X:\mirror", Requested: @"X:\mirror"),
                (Name: "incoming-case", Held: @"X:\mirror", Requested: @"x:\MIRROR"),
                (Name: "incoming-slashes", Held: @"X:\mirror", Requested: "X:/mirror"),
                (Name: "incoming-trailing", Held: @"X:\mirror", Requested: @"X:\mirror\"),
                (Name: "stored-case", Held: @"x:\MIRROR", Requested: @"X:\mirror"),
                (Name: "stored-slashes", Held: "X:/mirror", Requested: @"X:\mirror"),
                (Name: "stored-trailing", Held: @"X:\mirror\", Requested: @"X:\mirror"),
            })
            {
                using var d = new Dir();
                using var quiet = StoreLog.Quiet();
                string now = DateTime.UtcNow.ToString("o");
                using var os = OpenStore.Open(d.P("truedat.db"), true, VersionInfo.Display, now);
                long held = os.Dal.EnsureRoot(os.Machine.Id, shape.Held, "mirror", now);
                long? returned = null;
                bool accepted = true;
                try { returned = os.Dal.EnsureRoot(os.Machine.Id, shape.Requested, "primary", now); }
                catch (StoreException) { accepted = false; }
                if (!controlAccepted.HasValue) controlAccepted = accepted;
                var roots = os.Dal.ListRoots();
                int equivalent = roots.Count(r => string.Equals(r.Path.Replace('/', '\\').TrimEnd('\\'), @"X:\mirror", StringComparison.OrdinalIgnoreCase));
                string expectedRole = controlAccepted.Value ? "primary" : "mirror";
                assert(accepted == controlAccepted.Value && roots.Count == 1 && equivalent == 1
                    && roots[0].Id == held && roots[0].Role == expectedRole && (!accepted || returned == held),
                    $"StoreScan.MirrorRoleSpelling {shape.Name}: equivalent paths match the exact-path {(controlAccepted.Value ? "promotion" : "refusal")} and preserve one root"
                    + $" (accepted {accepted}; roots {roots.Count}; equivalent {equivalent}; roles {string.Join(",", roots.Select(r => r.Role))})");
            }
        }

        /// <summary>The repair half of the spelling fix (8884c86 prevents; this heals): a store an earlier build left with one path as two
        /// roots on one machine — a mirror and a primary in two spellings, child rows under both — opens as one root. The PRIMARY row
        /// survives whichever is older (nothing published may vanish), the mirror's locations move under it, a path recorded under both
        /// keeps the newer copy and takes the older copy's facts where it had none, run and check history move, another machine's mirror
        /// at the same path is untouched, the generation advances once, and a second open is a read.</summary>
        static void TestLegacyRootAliasRepair(Action<bool, string> assert, bool mirrorFirst)
        {
            string label = $"StoreScan.LegacyRootAliasRepair ({(mirrorFirst ? "mirror-first" : "primary-first")})";
            using var d = new Dir();
            using var quiet = StoreLog.Quiet();
            string now = DateTime.UtcNow.ToString("o");
            var store = d.P("truedat.db");
            const string held = @"X:\lib", alias = "x:/LIB/", other = "00000000-0000-7000-8000-00000000a11a";
            string firstRole = mirrorFirst ? "mirror" : "primary", secondRole = mirrorFirst ? "primary" : "mirror";
            var entries = new Dictionary<string, TrackEntry>(PathComparer.Instance)
            {
                [held + @"\t1.flac"] = E("t1", 120, 'a'),
                [held + @"\t2.flac"] = E("t2", 100, 'b'),
            };
            ImportCommand.ExecuteEntries(store, entries, held, firstRole, true, "import", "1", "alias-test", null, null);
            long firstRoot, secondRoot, t1, dup, t2, bOnly, generation;
            using (var os = OpenStore.Open(store, false, VersionInfo.Display, now))
            {
                var db = os.Db;
                firstRoot = os.Dal.ListRoots().Single().Id;
                t1 = long.Parse(db.ExecScalar($"SELECT id FROM track_location WHERE display_path = '{held}\\t1.flac';")!);
                t2 = long.Parse(db.ExecScalar($"SELECT id FROM track_location WHERE display_path = '{held}\\t2.flac';")!);
                db.Exec($"UPDATE track_copy SET smfmBpm = NULL WHERE location_id = {t1};" +
                        // the broken state, as an earlier EnsureRoot left it: the same path again, another spelling, the OTHER role, same machine
                        $"INSERT INTO library_root(machine_id, path, role, created_utc) VALUES ('{os.Machine.Id}', '{alias}', '{secondRole}', '2026-10-23T00:00:01Z');" +
                        // t1 recorded under both roots; the alias copy is the newer observation but lacks the artist the older one has
                        "INSERT INTO track_location(id, entity_id, root_id, rel_path, canonical_path, display_path, last_modified_utc, last_seen_utc, state) " +
                        $"SELECT id + 1000, entity_id, (SELECT max(id) FROM library_root), rel_path, canonical_path, display_path, last_modified_utc, last_seen_utc, state FROM track_location WHERE id = {t1};" +
                        $"CREATE TEMP TABLE tc AS SELECT * FROM track_copy WHERE location_id = {t1};" +
                        $"UPDATE tc SET location_id = {t1 + 1000}, updated_utc = '2099-01-01T00:00:00.0000000Z', artist = NULL, smfmBpm = 130;" +
                        "INSERT INTO track_copy SELECT * FROM tc; DROP TABLE tc;" +
                        // a path only the alias root holds
                        "INSERT INTO track_location(id, entity_id, root_id, rel_path, canonical_path, display_path, last_modified_utc, last_seen_utc, state) " +
                        $"SELECT id + 1000, entity_id, (SELECT max(id) FROM library_root), 'b-only.flac', 'b-only.flac', '{held}\\b-only.flac', last_modified_utc, last_seen_utc, state FROM track_location WHERE id = {t2};" +
                        $"CREATE TEMP TABLE tc2 AS SELECT * FROM track_copy WHERE location_id = {t2};" +
                        $"UPDATE tc2 SET location_id = {t2 + 1000};" +
                        "INSERT INTO track_copy SELECT * FROM tc2; DROP TABLE tc2;" +
                        // history hanging off the alias's duplicate location
                        $"INSERT INTO run(producer_key, producer_version, location_id, started_utc, outcome) VALUES ('verify', '1', {t1 + 1000}, '2026-10-23T00:00:02Z', 'ok');" +
                        $"INSERT INTO check_result(location_id, kind, result, checked_utc) VALUES ({t1 + 1000}, 'verify', 'OK', '2026-10-23T00:00:03Z');" +
                        // another machine's mirror of the same path: legitimate, that box's view, must not move
                        $"INSERT INTO machine(id, hostname, created_utc) VALUES ('{other}', 'OTHER', '{now}');" +
                        $"INSERT INTO library_root(machine_id, path, role, created_utc) VALUES ('{other}', '{held}', 'mirror', '2026-10-23T00:00:04Z');" +
                        "INSERT INTO track_location(id, entity_id, root_id, rel_path, canonical_path, display_path, last_modified_utc, last_seen_utc, state) " +
                        $"SELECT id + 2000, entity_id, (SELECT max(id) FROM library_root), rel_path, canonical_path, display_path, last_modified_utc, last_seen_utc, state FROM track_location WHERE id = {t1};" +
                        $"CREATE TEMP TABLE tc3 AS SELECT * FROM track_copy WHERE location_id = {t1};" +
                        $"UPDATE tc3 SET location_id = {t1 + 2000};" +
                        "INSERT INTO track_copy SELECT * FROM tc3; DROP TABLE tc3;");
                secondRoot = long.Parse(db.ExecScalar($"SELECT id FROM library_root WHERE path = '{alias}';")!);
                dup = t1 + 1000; bOnly = t2 + 1000;
                generation = os.Dal.Generation();
                assert(db.ExecScalar($"SELECT count(*) FROM library_root WHERE machine_id = '{os.Machine.Id}';") == "2"
                       && db.ExecScalar($"SELECT count(*) FROM track_location WHERE display_path = '{held}\\t1.flac';") == "3",
                    $"{label}: the broken state — one path as two roots on one machine, t1 recorded under both and under another machine's mirror");
            }
            long survivor = mirrorFirst ? secondRoot : firstRoot, folded = mirrorFirst ? firstRoot : secondRoot;
            using (var os = OpenStore.Open(store, false, VersionInfo.Display, now))
            {
                var db = os.Db;
                var mine = os.Dal.ListRoots().Where(r => r.Id != folded).ToList();
                string own = db.ExecScalar($"SELECT group_concat(id || ':' || role, ',') FROM library_root WHERE machine_id = '{os.Machine.Id}';") ?? "none";
                assert(own == $"{survivor}:primary" && os.Dal.ListRoots().Count == 2,
                    $"{label}: opening the store folds the alias into the PRIMARY row whichever is older, and the other machine's mirror stays (mine {own}; roots {os.Dal.ListRoots().Count})");
                assert(db.ExecScalar($"SELECT count(*) FROM track_location WHERE root_id = {survivor};") == "3"
                       && db.ExecScalar($"SELECT id FROM track_location WHERE root_id = {survivor} AND canonical_path = 't1.flac';") == dup.ToString()
                       && db.ExecScalar($"SELECT root_id FROM track_location WHERE id = {bOnly};") == survivor.ToString(),
                    $"{label}: t1 is one location under the survivor (the newer copy's, {dup}), t2 and the alias-only path sit beside it");
                assert(db.ExecScalar($"SELECT artist || '|' || smfmBpm || '|' || updated_utc FROM track_copy WHERE location_id = {dup};") == "A|130|2099-01-01T00:00:00.0000000Z",
                    $"{label}: the kept copy takes the artist it lacked from the dropped copy and keeps its own bpm and stamp ({db.ExecScalar($"SELECT artist || '|' || smfmBpm || '|' || updated_utc FROM track_copy WHERE location_id = {dup};")})");
                assert(db.ExecScalar($"SELECT location_id FROM run WHERE producer_key = 'verify';") == dup.ToString()
                       && db.ExecScalar("SELECT location_id FROM check_result;") == dup.ToString(),
                    $"{label}: the dropped location's run and check history moved onto the survivor");
                assert(db.ExecScalar($"SELECT count(*) FROM track_location WHERE id = {t1 + 2000} AND root_id = (SELECT id FROM library_root WHERE machine_id = '{other}');") == "1"
                       && db.ExecScalar($"SELECT count(*) FROM track_copy WHERE location_id = {t1 + 2000};") == "1",
                    $"{label}: the other machine's mirror keeps its own location and copy");
                assert(db.ExecScalar("SELECT count(*) FROM track_copy;") == db.ExecScalar("SELECT count(*) FROM track_location;")
                       && db.ExecScalar("SELECT count(*) FROM track_copy WHERE location_id NOT IN (SELECT id FROM track_location);") == "0"
                       && db.ExecScalar("SELECT count(*) FROM verdict WHERE location_id NOT IN (SELECT id FROM track_location);") == "0"
                       && db.ExecScalar("PRAGMA foreign_key_check;") == null,
                    $"{label}: one copy per location, nothing dangling, no foreign key broken");
                assert(os.Dal.Generation() == generation + 1 && os.Dal.LibraryRoots().Select(r => r.Id).SequenceEqual(new[] { survivor }),
                    $"{label}: the repair is one mutation ({generation} -> {os.Dal.Generation()}) and the library is the survivor alone");
            }
            using (var os = OpenStore.Open(store, false, VersionInfo.Display, now))
                assert(os.Dal.Generation() == generation + 1 && os.Dal.ListRoots().Count == 2 && !os.Dal.HasNestedRoots(),
                    $"{label}: a second open is a read — nothing left to repair, generation {os.Dal.Generation()}");
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
                assert(rep.Ok && rep.CacheHitKeys == 2 && rep.Retired == 1, $"one flush: 2 cache-hit refreshes as copy facts, 1 location retired ({rep.Ok}/{rep.CacheHitKeys}/{rep.Retired}: {rep.Error})");
                assert(essentiaRunsAfter == essentiaRunsBefore, $"cache hits are never recorded as Essentia measurements (essentia runs {essentiaRunsBefore} -> {essentiaRunsAfter})");
                assert(aLm != null && aLm.StartsWith("2026-06-01"), $"the cache hit's copy fact reached the store (lastModified {aLm})");
                assert(exported.SequenceEqual(new[] { @"D:\music\a.flac", @"D:\music\moved\b.flac" }), $"the export reads present locations only: the moved file's old path is gone ({string.Join(", ", exported)})");
                assert(gen1 > gen0, $"the retirement and the new location are a consumer-visible change: the generation advanced ({gen0} -> {gen1})");
            }
        }
    }
}
