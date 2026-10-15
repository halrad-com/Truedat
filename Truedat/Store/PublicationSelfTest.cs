using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Truedat.Store
{
    /// <summary>
    /// Self-tests for the export-triple publication (store-v3 T07/T08). Called from
    /// Program.RunSelfTest. Every row of the plan's failure-state table is driven here with real
    /// renames: observer after each step, abrupt stop before each step, same-count content change.
    /// </summary>
    internal static class PublicationSelfTest
    {
        internal static void Run(Action<bool, string> assert)
        {
            Console.WriteLine("Publication self-test (store-v3 T07: the ruled rename order, explicit mtimes, sidecar hash binding)");
            try { TestHashAndMtimes(assert); }
            catch (Exception ex) { assert(false, $"Publication.HashAndMtimes: threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine("Publication self-test (store-v3 T07: every interruption state is one row of the failure table, and the recovery predicate sees it)");
            try { TestInterruptionTable(assert); }
            catch (Exception ex) { assert(false, $"Publication.InterruptionTable: threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine("Publication self-test (store-v3 T07: re-publish after an interruption is idempotent; no-companion plan)");
            try { TestRepublishAndNoCompanion(assert); }
            catch (Exception ex) { assert(false, $"Publication.Republish: threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine("Publication self-test (store-v3 T07: edges on -> off retires the old companion; keeper review of a98ded7, defect 2)");
            try { TestEdgesOnToOff(assert); }
            catch (Exception ex) { assert(false, $"Publication.EdgesOnToOff: threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine("Publication self-test (store-v3 T08: catalog origin header round-trips through the bounded head read; malformed origin is false, never a throw)");
            try { TestOriginHeader(assert); }
            catch (Exception ex) { assert(false, $"Publication.OriginHeader: threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine("Publication self-test (store-v3 T07: SaveResultsPublished writes the real catalog + sidecar through the publisher; the plain save stays v1)");
            try { TestSaveResultsPublished(assert); }
            catch (Exception ex) { assert(false, $"Publication.SaveResultsPublished: threw {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>The integration: the real catalog writer and sidecar writer, published as a set with the
        /// v2 header, bound by hash; the plain save (no store) writes the v1 catalog untouched; a DOM verb
        /// re-stamp drops a stale header.</summary>
        static void TestSaveResultsPublished(Action<bool, string> assert)
        {
            using (var d = new Dir())
            {
                var dict = new System.Collections.Concurrent.ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
                for (int i = 0; i < 3; i++)
                    dict[@"D:\music\t" + i + ".flac"] = new TrackEntry { Features = new TrackFeatures { Artist = "A", Title = "T" + i, Bpm = 100 + i, Mfcc = new[] { 1.5, -2.25 } }, LastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc) };
                var moods = d.P("mbxmoods.json");
                var sidecar = d.P("mbxmoods.mbxs");
                var companion = d.P("mbxedges.v1.json");

                // Published, no companion this generation.
                var origin = new CatalogOrigin { Kind = "export", MachineId = "m-1", StoreSchema = 2, Truedat = "test", Generation = 5 };
                var r = Program.SaveResultsPublished(moods, dict, origin, null);
                assert(r != null && r.Completed && r.Generation == 5, "published: completes at the store's generation");
                assert(CatalogOrigin.TryRead(moods, out var back) && back.Generation == 5 && back.Kind == "export" && back.MachineId == "m-1" && back.StoreSchema == 2,
                    "published: the real catalog carries contract + origin in its head");
                assert(back.SidecarSha256 == Publication.Sha256Hex(sidecar) && back.SidecarSha256.Length == 64, "published: origin.sidecarSha256 is the published sidecar's bytes");
                assert(CatalogSidecar.Read(sidecar).TrackCount == 3, "published: the sidecar is the real v3 sidecar (3 records)");
                assert(!File.Exists(companion) && !File.Exists(moods + ".tmp") && !File.Exists(sidecar + ".tmp") && !File.Exists(companion + ".tmp"), "published: no companion, no temps left");
                assert(!Publication.FilesBehind(moods, sidecar, companion, false, 5, out var w0), "published: recovery predicate satisfied (" + w0 + ")");
                assert(File.GetLastWriteTimeUtc(sidecar) >= File.GetLastWriteTimeUtc(moods), "published: legacy predicate holds (sidecar >= catalog)");
                var reload = new System.Collections.Concurrent.ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
                assert(Program.LoadExistingMoodsForStore(moods, reload) == 3, "published: truedat's own loader reads the v2 catalog (unknown root keys tolerated)");

                // Published with a companion: the callback writes the temp; the publisher places it.
                var origin2 = new CatalogOrigin { Kind = "export", MachineId = "m-1", StoreSchema = 2, Truedat = "test", Generation = 6 };
                var r2 = Program.SaveResultsPublished(moods, dict, origin2, tmp => { Put(tmp, CompanionText(6, "x")); return true; });
                assert(r2 != null && r2.Completed && File.Exists(companion) && Marker(companion) == "x", "published with companion: placed beside the catalog");
                assert(!Publication.FilesBehind(moods, sidecar, companion, true, 6, out var w1), "published with companion: recovery predicate satisfied (" + w1 + ")");
                assert(!File.Exists(companion + Publication.CompanionAsideSuffix), "published with companion: no .prev left");

                // Edges off next generation: the companion is retired.
                var origin3 = new CatalogOrigin { Kind = "export", MachineId = "m-1", StoreSchema = 2, Truedat = "test", Generation = 7 };
                var r3 = Program.SaveResultsPublished(moods, dict, origin3, null);
                assert(r3 != null && r3.Completed && !File.Exists(companion), "published, edges off: the previous companion is retired");
                assert(!Publication.FilesBehind(moods, sidecar, companion, false, 7, out var w2), "published, edges off: recovery predicate satisfied (" + w2 + ")");

                // The PLAIN save (no store in force) writes the v1 catalog: no contract, no origin — bytes untouched.
                Program.SaveResultsForStore(moods, dict);
                assert(!CatalogOrigin.TryRead(moods, out _) && !File.ReadAllText(moods).Contains("\"contract\""), "plain save: v1 catalog, no publication header");
                assert(Program.LoadExistingMoodsForStore(moods, new System.Collections.Concurrent.ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance)) == 3, "plain save: still loads");

                // A DOM-verb re-stamp drops an inherited v2 header (the verb did not publish; the hash would lie).
                var root = new System.Text.Json.Nodes.JsonObject
                {
                    ["version"] = "1.0", ["contract"] = CatalogOrigin.ContractV2,
                    ["origin"] = new System.Text.Json.Nodes.JsonObject { ["generation"] = 5, ["sidecarSha256"] = "ff" },
                    ["tracks"] = new System.Text.Json.Nodes.JsonObject(),
                };
                Program.StampCatalogHeader(root, "T");
                assert(root["contract"] == null && root["origin"] == null && root["generatedBy"] != null, "StampCatalogHeader: drops contract + origin (a verb that did not publish must not carry a stale binding)");
            }
        }

        // ------------------------------------------------------------------ fixtures

        sealed class Dir : IDisposable
        {
            internal readonly string Root;
            internal Dir() { Root = Path.Combine(Path.GetTempPath(), "truedat-pub-selftest-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); }
            internal string P(string name) => Path.Combine(Root, name);
            public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
        }

        static void Put(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(false));
        static string Get(string path) => File.Exists(path) ? File.ReadAllText(path) : "(absent)";

        /// <summary>A catalog body whose head carries a v2 origin bound to <paramref name="sidecarPath"/>'s bytes.</summary>
        static string CatalogText(long generation, string sidecarPath, string marker)
            => "{\"version\":\"1.0\",\"trackCount\":1,\"contract\":\"" + CatalogOrigin.ContractV2 + "\",\"origin\":{\"kind\":\"scan\",\"machineId\":\"m\",\"storeSchema\":3,\"truedat\":\"t\",\"exportedAt\":\"x\",\"generation\":"
               + generation + ",\"sidecarSha256\":\"" + Publication.Sha256Hex(sidecarPath) + "\"},\"tracks\":{\"" + marker + "\":{}}}";

        /// <summary>A companion body whose head carries origin.generation (the shape the writer gains in T07).</summary>
        static string CompanionText(long generation, string marker)
            => "{\"contract\":\"mbxedges.v1\",\"origin\":{\"generation\":" + generation + "},\"trackCount\":1,\"tracks\":{\"" + marker + "\":{}}}";

        /// <summary>A live, self-consistent triple at generation <paramref name="g"/> plus temps at
        /// <paramref name="g"/>+1, same track count (the hard case): the content differs only by the
        /// generation marker. Sidecar bytes are "S{g}"; catalogs are bound to them by hash.</summary>
        static PublicationPlan Triple(Dir d, long g, DateTime liveMtime)
        {
            var plan = new PublicationPlan
            {
                CatalogPath = d.P("mbxmoods.json"), SidecarPath = d.P("mbxmoods.mbxs"), CompanionPath = d.P("mbxedges.v1.json"),
                CatalogTmp = d.P("mbxmoods.json.tmp"), SidecarTmp = d.P("mbxmoods.mbxs.tmp"), CompanionTmp = d.P("mbxedges.v1.json.tmp"),
                Generation = g + 1,
            };
            Put(plan.SidecarPath, "S" + g); Put(plan.CatalogPath, CatalogText(g, plan.SidecarPath, "C" + g)); Put(plan.CompanionPath, CompanionText(g, "E" + g));
            File.SetLastWriteTimeUtc(plan.CatalogPath, liveMtime);
            File.SetLastWriteTimeUtc(plan.SidecarPath, liveMtime.AddSeconds(Publication.MtimeMarginSeconds));
            // Real temp-write order: companion, sidecar (hashed), catalog LAST — so without the
            // explicit stamps the sidecar temp is OLDER than the catalog temp. Pinned by mtime, not
            // by write order, so the stamp is load-bearing in every test below.
            Put(plan.CompanionTmp!, CompanionText(g + 1, "E" + (g + 1))); Put(plan.SidecarTmp, "S" + (g + 1)); Put(plan.CatalogTmp, CatalogText(g + 1, plan.SidecarTmp, "C" + (g + 1)));
            // Both temps OLDER than anything live, so an unstamped sidecar would fail the legacy
            // predicate against the stamped catalog (mutation-checked 2026-10-15).
            File.SetLastWriteTimeUtc(plan.SidecarTmp, liveMtime.AddHours(-1));
            File.SetLastWriteTimeUtc(plan.CatalogTmp, liveMtime.AddHours(-1).AddSeconds(5));
            return plan;
        }

        /// <summary>The generation marker inside a catalog/companion body ("C3", "E4"), or the raw
        /// text for a sidecar, or "(absent)".</summary>
        static string Marker(string path)
        {
            var t = Get(path);
            int i = t.IndexOf("\"tracks\":{\"", StringComparison.Ordinal);
            if (i < 0) return t;
            int s = i + "\"tracks\":{\"".Length;
            int e = t.IndexOf('"', s);
            return e < 0 ? t : t.Substring(s, e - s);
        }

        static bool Behind(PublicationPlan plan, bool expectCompanion, out string reason)
            => Publication.FilesBehind(plan.CatalogPath, plan.SidecarPath, plan.CompanionPath, expectCompanion, plan.Generation, out reason);

        // ------------------------------------------------------------------ tests

        static void TestHashAndMtimes(Action<bool, string> assert)
        {
            using (var d = new Dir())
            {
                // FIPS 180-2 vector: SHA-256("abc").
                Put(d.P("abc"), "abc");
                assert(Publication.Sha256Hex(d.P("abc")) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                    "Sha256Hex: FIPS vector for 'abc', 64 lower-case hex");

                // The fact the design rests on: a rename preserves the temp's write time.
                Put(d.P("a.tmp"), "a");
                var x = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(d.P("a.tmp"), x);
                File.Move(d.P("a.tmp"), d.P("a"));
                assert(File.GetLastWriteTimeUtc(d.P("a")) == x, "rename preserves LastWriteTimeUtc (so mtimes are set on the temps, before any rename)");

                // CatalogMtime = max(now, previous sidecar + margin).
                var prev = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
                Put(d.P("live.mbxs"), "s"); File.SetLastWriteTimeUtc(d.P("live.mbxs"), prev);
                assert(Publication.CatalogMtime(prev.AddSeconds(-30), d.P("live.mbxs")) == prev.AddSeconds(Publication.MtimeMarginSeconds),
                    "CatalogMtime: clock behind the previous sidecar -> previous sidecar + margin");
                assert(Publication.CatalogMtime(prev.AddMinutes(5), d.P("live.mbxs")) == prev.AddMinutes(5),
                    "CatalogMtime: clock well ahead -> now");
                assert(Publication.CatalogMtime(prev, d.P("no-such.mbxs")) == prev, "CatalogMtime: no previous sidecar -> now");
                assert(Publication.SidecarMtime(prev) == prev.AddSeconds(Publication.MtimeMarginSeconds), "SidecarMtime: catalog + margin");

                // After a full publish the legacy predicate holds both ways: new sidecar >= new catalog,
                // old sidecar < new catalog (the previous publish's sidecar is strictly older).
                var live = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
                var plan = Triple(d, 7, live);
                var oldSidecarMtime = File.GetLastWriteTimeUtc(plan.SidecarPath);
                var r = Publication.Publish(plan, live.AddSeconds(-10));   // clock BEHIND the live files on purpose
                assert(r.Completed && r.Reached == PublicationStep.Done, "Publish: completes");
                var cNew = File.GetLastWriteTimeUtc(plan.CatalogPath);
                var sNew = File.GetLastWriteTimeUtc(plan.SidecarPath);
                assert(cNew == r.CatalogMtimeUtc && sNew == r.SidecarMtimeUtc, "Publish: live files carry the stamped mtimes after the renames");
                assert(sNew >= cNew, "legacy predicate: new sidecar mtime >= new catalog mtime");
                assert(oldSidecarMtime < cNew, "legacy predicate: previous sidecar mtime < new catalog mtime (so S0 beside C1 is rejected)");
                assert(r.SidecarSha256 == Publication.Sha256Hex(plan.SidecarPath), "binding: the hash in the result equals the published sidecar's bytes");
                assert(!File.Exists(plan.CompanionPath + Publication.CompanionAsideSuffix), "Publish: the aside copy is removed on completion");
                assert(!File.Exists(plan.CatalogTmp) && !File.Exists(plan.SidecarTmp) && !File.Exists(plan.CompanionTmp!), "Publish: no temps remain");
            }
        }

        /// <summary>Drive the publisher to a stop before each step and check the visible files against
        /// the plan's table. "C0 beside S1" must be unreachable at every stop, and the recovery
        /// predicate (<see cref="Publication.FilesBehind"/>) must say "behind" at every stop short of
        /// Done — including the ones where the catalog already carries the store's generation
        /// (keeper review of a98ded7, defect 1).</summary>
        static void TestInterruptionTable(Action<bool, string> assert)
        {
            var live = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
            foreach (PublicationStep stop in new[] { PublicationStep.CompanionAside, PublicationStep.Catalog, PublicationStep.Companion, PublicationStep.Sidecar, PublicationStep.Done })
            {
                using (var d = new Dir())
                {
                    var plan = Triple(d, 0, live);
                    var r = Publication.Publish(plan, live.AddMinutes(1), s => s == stop);
                    string c = Marker(plan.CatalogPath), s_ = Marker(plan.SidecarPath), e = Marker(plan.CompanionPath), prev = Marker(plan.CompanionPath + Publication.CompanionAsideSuffix);
                    string state = $"stop-before={stop}: C={c} S={s_} E={e} prev={prev}";

                    // The one state no reader may ever see: new sidecar beside old catalog.
                    assert(!(c == "C0" && s_ == "S1"), "unreachable: C0 beside S1 — " + state);

                    bool behind = Behind(plan, expectCompanion: true, out var why);
                    switch (stop)
                    {
                        case PublicationStep.CompanionAside:
                            assert(!r.Completed && c == "C0" && s_ == "S0" && e == "E0" && prev == "(absent)", "table row 'died before any rename': C0 S0 E0 intact — " + state);
                            assert(File.Exists(plan.CatalogTmp) && File.Exists(plan.SidecarTmp) && File.Exists(plan.CompanionTmp!), "temps untouched when nothing ran");
                            assert(behind, "recovery predicate: files behind the store when nothing was renamed (" + why + ")");
                            break;
                        case PublicationStep.Catalog:
                            assert(!r.Completed && c == "C0" && s_ == "S0" && e == "(absent)" && prev == "E0", "table row 'aside done': C0 S0, companion absent (legacy: edges off), E0 kept as .prev — " + state);
                            assert(behind, "recovery predicate: behind after the aside only (" + why + ")");
                            break;
                        case PublicationStep.Companion:
                            assert(!r.Completed && c == "C1" && s_ == "S0" && e == "(absent)", "table row C1 S0 (no companion): legacy S0 older -> catalog parse; upgraded hash mismatch -> catalog parse — " + state);
                            assert(File.GetLastWriteTimeUtc(plan.SidecarPath) < File.GetLastWriteTimeUtc(plan.CatalogPath), "C1 S0: old sidecar strictly older than the new catalog");
                            assert(behind, "recovery predicate: behind when the catalog is at N but the sidecar is not bound to it (" + why + ")");
                            break;
                        case PublicationStep.Sidecar:
                            assert(!r.Completed && c == "C1" && s_ == "S0" && e == "E1", "table row C1 S0 E1: new consistent on both readers after one slow boot — " + state);
                            assert(File.Exists(plan.SidecarTmp), "sidecar temp still present for the re-publish");
                            assert(behind, "recovery predicate: behind when catalog and companion are at N but the sidecar is not (" + why + ")");
                            break;
                        case PublicationStep.Done:
                            assert(r.Completed && c == "C1" && s_ == "S1" && e == "E1" && prev == "(absent)", "table row C1 S1 E1: complete — " + state);
                            assert(!behind, "recovery predicate: NOT behind once the set is complete (" + why + ")");
                            break;
                    }
                }
            }
        }

        static void TestRepublishAndNoCompanion(Action<bool, string> assert)
        {
            var live = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            using (var d = new Dir())
            {
                // Interrupted before the sidecar rename; the caller regenerates every temp from the
                // committed store and publishes again -> complete, same generation.
                var plan = Triple(d, 3, live);
                Publication.Publish(plan, live.AddMinutes(1), s => s == PublicationStep.Sidecar);
                assert(Marker(plan.CatalogPath) == "C4" && Marker(plan.SidecarPath) == "S3", "republish setup: C1 S0 E1 left behind");
                assert(Behind(plan, true, out _), "republish setup: recovery predicate says behind");
                Put(plan.CompanionTmp!, CompanionText(4, "E4")); Put(plan.SidecarTmp, "S4"); Put(plan.CatalogTmp, CatalogText(4, plan.SidecarTmp, "C4"));
                var r = Publication.Publish(plan, live.AddMinutes(2));
                assert(r.Completed && Marker(plan.CatalogPath) == "C4" && Marker(plan.SidecarPath) == "S4" && Marker(plan.CompanionPath) == "E4",
                    "republish: completes to C1 S1 E1 (idempotent; M8 recovery path)");
                assert(!Behind(plan, true, out var why) , "republish: recovery predicate satisfied afterwards (" + why + ")");
                assert(File.GetLastWriteTimeUtc(plan.SidecarPath) >= File.GetLastWriteTimeUtc(plan.CatalogPath), "republish: pair satisfies the legacy predicate");

                // A missing temp is refused before anything moves.
                File.Delete(d.P("mbxmoods.mbxs.tmp"));
                Put(plan.CatalogTmp, "C5"); Put(plan.CompanionTmp!, "E5");
                bool threw = false;
                try { Publication.Publish(plan, live.AddMinutes(3)); } catch (FileNotFoundException) { threw = true; }
                assert(threw && Marker(plan.CatalogPath) == "C4" && Marker(plan.CompanionPath) == "E4", "missing temp: refused up front, nothing renamed");
            }
            using (var d = new Dir())
            {
                // No companion this generation (edges off), nothing live either: catalog then
                // sidecar, no companion created. CompanionPath is still the KNOWN target path.
                var plan = new PublicationPlan
                {
                    CatalogPath = d.P("mbxmoods.json"), SidecarPath = d.P("mbxmoods.mbxs"), CompanionPath = d.P("mbxedges.v1.json"),
                    CatalogTmp = d.P("mbxmoods.json.tmp"), SidecarTmp = d.P("mbxmoods.mbxs.tmp"), CompanionTmp = null, Generation = 1,
                };
                Put(plan.SidecarTmp, "S1"); Put(plan.CatalogTmp, CatalogText(1, plan.SidecarTmp, "C1"));
                var r = Publication.Publish(plan, live);
                assert(r.Completed && Marker(plan.CatalogPath) == "C1" && Marker(plan.SidecarPath) == "S1" && !File.Exists(plan.CompanionPath),
                    "no-companion plan: catalog + sidecar published, no companion created");
                assert(!Behind(plan, expectCompanion: false, out var w0), "no-companion plan: not behind when no companion is expected (" + w0 + ")");
                assert(Behind(plan, expectCompanion: true, out var w1), "no-companion plan: behind when a companion IS expected and absent (" + w1 + ")");
                // A catalog with no origin block is 'behind' (legacy / unknown generation).
                Put(plan.CatalogPath, "{\"version\":\"1.0\",\"trackCount\":1,\"tracks\":{}}");
                assert(Behind(plan, false, out var w2), "FilesBehind: no origin block -> behind (" + w2 + ")");
            }
        }

        /// <summary>Keeper review of a98ded7, defect 2: a generation that publishes NO companion must
        /// still retire the previous live one, or a legacy hub keeps serving E0 beside C1/S1 forever.</summary>
        static void TestEdgesOnToOff(Action<bool, string> assert)
        {
            var live = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
            using (var d = new Dir())
            {
                var plan = Triple(d, 5, live);
                File.Delete(plan.CompanionTmp!); plan.CompanionTmp = null;          // edges off this generation; E5 is live
                var r = Publication.Publish(plan, live.AddMinutes(1));
                assert(r.Completed && Marker(plan.CatalogPath) == "C6" && Marker(plan.SidecarPath) == "S6", "on->off: catalog + sidecar at N");
                assert(!File.Exists(plan.CompanionPath), "on->off: the OLD companion is gone after a complete publish (no E0 beside C1/S1)");
                assert(!File.Exists(plan.CompanionPath + Publication.CompanionAsideSuffix), "on->off: no .prev left behind on completion");
                assert(!Behind(plan, expectCompanion: false, out var w0), "on->off: recovery predicate satisfied (" + w0 + ")");
            }
            using (var d = new Dir())
            {
                // Interrupted after the aside, before the catalog: the old companion is aside, nothing
                // live — a legacy hub reads edges-off, never E0 beside C1 — and recovery is 'behind'.
                var plan = Triple(d, 5, live);
                File.Delete(plan.CompanionTmp!); plan.CompanionTmp = null;
                var r = Publication.Publish(plan, live.AddMinutes(1), s => s == PublicationStep.Catalog);
                assert(!r.Completed && Marker(plan.CatalogPath) == "C5" && !File.Exists(plan.CompanionPath) && Marker(plan.CompanionPath + Publication.CompanionAsideSuffix) == "E5",
                    "on->off interrupted before the catalog: C0 S0, companion aside as .prev, none live");
                assert(Behind(plan, expectCompanion: false, out var w1), "on->off interrupted: recovery predicate says behind (" + w1 + ")");
                // And when a stale companion IS still live beside a complete C1/S1 (e.g. an older
                // publisher), the predicate must flag it so the next publish retires it.
                var plan2 = Triple(d, 7, live.AddHours(1));
                File.Delete(plan2.CompanionTmp!); plan2.CompanionTmp = null;
                Publication.Publish(plan2, live.AddHours(1).AddMinutes(1), s => s == PublicationStep.CompanionAside);   // nothing ran
                Put(plan2.CompanionPath, CompanionText(7, "E7"));
                Put(plan2.SidecarPath, "S8"); Put(plan2.CatalogPath, CatalogText(8, plan2.SidecarPath, "C8"));
                File.SetLastWriteTimeUtc(plan2.SidecarPath, live.AddHours(2));
                assert(Behind(plan2, expectCompanion: false, out var w2), "stale companion present when none is expected -> behind (" + w2 + ")");
            }
        }

        static void TestOriginHeader(Action<bool, string> assert)
        {
            using (var d = new Dir())
            {
                var sidecar = d.P("mbxmoods.mbxs");
                Put(sidecar, "MBXS-bytes-of-generation-9");
                var o = new CatalogOrigin
                {
                    Kind = "scan", MachineId = "m-1", StoreSchema = 3, Truedat = "test-build", ExportedAt = "2026-10-15T00:00:00Z",
                    Generation = 9, SidecarSha256 = Publication.Sha256Hex(sidecar),
                };
                var catalog = d.P("mbxmoods.json");
                using (var fs = new FileStream(catalog, FileMode.Create))
                using (var w = new Utf8JsonWriter(fs))
                {
                    w.WriteStartObject();
                    w.WriteString("version", "1.0");
                    w.WriteString("generatedAt", o.ExportedAt);
                    w.WriteString("generatedBy", o.Truedat);
                    w.WriteNumber("trackCount", 2000);
                    CatalogOrigin.WriteHeader(w, o);            // right after trackCount: inside the bounded head
                    w.WriteStartObject("tracks");
                    for (int i = 0; i < 2000; i++)                // push the file well past HeadBytes
                    {
                        w.WriteStartObject("D:\\music\\track-" + i + ".flac");
                        w.WriteNumber("bpm", 120 + i % 7); w.WriteString("key", "A"); w.WriteString("audioStreamSha256", new string('a', 64));
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                assert(new FileInfo(catalog).Length > CatalogOrigin.HeadBytes, "fixture: catalog is larger than the bounded head read");
                assert(CatalogOrigin.TryRead(catalog, out var back), "TryRead: finds contract + origin in the head without parsing the file");
                assert(back.Generation == 9 && back.SidecarSha256 == o.SidecarSha256 && back.MachineId == "m-1" && back.StoreSchema == 3
                       && back.Kind == "scan" && back.Truedat == "test-build" && back.ExportedAt == o.ExportedAt,
                    "TryRead: every origin field round-trips");
                assert(back.SidecarSha256 == Publication.Sha256Hex(sidecar), "binding: the read hash matches the sidecar beside the catalog");
                assert(!Publication.FilesBehind(catalog, sidecar, d.P("mbxedges.v1.json"), false, 9, out var w9), "FilesBehind: files at the store's generation, sidecar bound -> not behind (" + w9 + ")");
                assert(Publication.FilesBehind(catalog, sidecar, d.P("mbxedges.v1.json"), false, 10, out var w10) && w10.Contains("10"), "FilesBehind: store one ahead -> behind (M8), reason names the generations (" + w10 + ")");
                Put(d.P("other.mbxs"), "different bytes");
                assert(Publication.FilesBehind(catalog, d.P("other.mbxs"), d.P("mbxedges.v1.json"), false, 9, out var wS), "FilesBehind: sidecar not bound to the catalog -> behind (" + wS + ")");

                // Legacy v1 catalog: no contract -> false (today's freshness pair applies, labelled unverified by the hub).
                var legacy = d.P("legacy.json");
                Put(legacy, "{\"version\":\"1.0\",\"generatedAt\":\"x\",\"trackCount\":1,\"tracks\":{\"p\":{\"bpm\":1}}}");
                assert(!CatalogOrigin.TryRead(legacy, out _), "TryRead: legacy v1 catalog (no contract) -> false");
                // A catalog whose origin precedes an unrelated nested header object still reads.
                var nested = d.P("nested.json");
                Put(nested, "{\"version\":\"1.0\",\"meta\":{\"a\":[1,2,{\"b\":3}]},\"trackCount\":1,\"contract\":\"mbxmoods.v2\",\"origin\":{\"generation\":4,\"sidecarSha256\":\"ff\"},\"tracks\":{}}");
                assert(CatalogOrigin.TryRead(nested, out var n) && n.Generation == 4 && n.SidecarSha256 == "ff", "TryRead: skips unknown nested header values");
                // Missing file never throws.
                assert(!CatalogOrigin.TryRead(d.P("nope.json"), out _), "TryRead: missing file -> false, no throw");

                // Keeper review of a98ded7, defect 3: malformed origin values return false, never throw.
                var badKind = d.P("bad-kind.json");
                Put(badKind, "{\"trackCount\":1,\"contract\":\"mbxmoods.v2\",\"origin\":{\"kind\":123,\"generation\":4,\"sidecarSha256\":\"ff\"},\"tracks\":{}}");
                bool ok; try { ok = !CatalogOrigin.TryRead(badKind, out _); } catch (Exception ex) { ok = false; assert(false, "TryRead: kind:123 threw " + ex.GetType().Name); }
                assert(ok, "TryRead: 'kind' is a number -> false, no throw");
                var bigGen = d.P("big-gen.json");
                Put(bigGen, "{\"trackCount\":1,\"contract\":\"mbxmoods.v2\",\"origin\":{\"kind\":\"scan\",\"generation\":99999999999999999999999999,\"sidecarSha256\":\"ff\"},\"tracks\":{}}");
                try { ok = !CatalogOrigin.TryRead(bigGen, out _); } catch (Exception ex) { ok = false; assert(false, "TryRead: oversized generation threw " + ex.GetType().Name); }
                assert(ok, "TryRead: oversized numeric generation -> false, no throw");
                var strGen = d.P("str-gen.json");
                Put(strGen, "{\"trackCount\":1,\"contract\":\"mbxmoods.v2\",\"origin\":{\"kind\":\"scan\",\"generation\":\"4\",\"sidecarSha256\":\"ff\"},\"tracks\":{}}");
                try { ok = !CatalogOrigin.TryRead(strGen, out _); } catch (Exception ex) { ok = false; assert(false, "TryRead: string generation threw " + ex.GetType().Name); }
                assert(ok, "TryRead: 'generation' is a string -> false, no throw");
            }
        }
    }
}
