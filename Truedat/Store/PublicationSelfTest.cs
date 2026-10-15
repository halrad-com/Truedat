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
            Console.WriteLine("Publication self-test (store-v3 T07: every interruption state is one row of the failure table)");
            try { TestInterruptionTable(assert); }
            catch (Exception ex) { assert(false, $"Publication.InterruptionTable: threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine("Publication self-test (store-v3 T07: re-publish after an interruption is idempotent; no-companion plan)");
            try { TestRepublishAndNoCompanion(assert); }
            catch (Exception ex) { assert(false, $"Publication.Republish: threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine("Publication self-test (store-v3 T08: catalog origin header round-trips through the bounded head read)");
            try { TestOriginHeader(assert); }
            catch (Exception ex) { assert(false, $"Publication.OriginHeader: threw {ex.GetType().Name}: {ex.Message}"); }
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

        /// <summary>A live triple at generation <paramref name="g"/> plus temps at <paramref name="g"/>+1,
        /// same track count (the hard case): the content differs only by the generation marker.</summary>
        static PublicationPlan Triple(Dir d, long g, DateTime liveMtime)
        {
            var plan = new PublicationPlan
            {
                CatalogPath = d.P("mbxmoods.json"), SidecarPath = d.P("mbxmoods.mbxs"), CompanionPath = d.P("mbxedges.v1.json"),
                CatalogTmp = d.P("mbxmoods.json.tmp"), SidecarTmp = d.P("mbxmoods.mbxs.tmp"), CompanionTmp = d.P("mbxedges.v1.json.tmp"),
                Generation = g + 1,
            };
            Put(plan.CatalogPath, "C" + g); Put(plan.SidecarPath, "S" + g); Put(plan.CompanionPath!, "E" + g);
            File.SetLastWriteTimeUtc(plan.CatalogPath, liveMtime);
            File.SetLastWriteTimeUtc(plan.SidecarPath, liveMtime.AddSeconds(Publication.MtimeMarginSeconds));
            // Real temp-write order: companion, sidecar (hashed), catalog LAST — so without the
            // explicit stamps the sidecar temp is OLDER than the catalog temp. Pinned by mtime, not
            // by write order, so the stamp is load-bearing in every test below.
            Put(plan.CompanionTmp!, "E" + (g + 1)); Put(plan.SidecarTmp, "S" + (g + 1)); Put(plan.CatalogTmp, "C" + (g + 1));
            // Both temps OLDER than anything live, so an unstamped sidecar would fail the legacy
            // predicate against the stamped catalog (mutation-checked 2026-10-15).
            File.SetLastWriteTimeUtc(plan.SidecarTmp, liveMtime.AddHours(-1));
            File.SetLastWriteTimeUtc(plan.CatalogTmp, liveMtime.AddHours(-1).AddSeconds(5));
            return plan;
        }

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
        /// the plan's table. "C0 beside S1" must be unreachable at every stop.</summary>
        static void TestInterruptionTable(Action<bool, string> assert)
        {
            var live = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
            foreach (PublicationStep stop in new[] { PublicationStep.CompanionAside, PublicationStep.Catalog, PublicationStep.Companion, PublicationStep.Sidecar, PublicationStep.Done })
            {
                using (var d = new Dir())
                {
                    var plan = Triple(d, 0, live);
                    var r = Publication.Publish(plan, live.AddMinutes(1), s => s == stop);
                    string c = Get(plan.CatalogPath), s_ = Get(plan.SidecarPath), e = Get(plan.CompanionPath!), prev = Get(plan.CompanionPath + Publication.CompanionAsideSuffix);
                    string state = $"stop-before={stop}: C={c} S={s_} E={e} prev={prev}";

                    // The one state no reader may ever see: new sidecar beside old catalog.
                    assert(!(c == "C0" && s_ == "S1"), "unreachable: C0 beside S1 — " + state);

                    switch (stop)
                    {
                        case PublicationStep.CompanionAside:
                            assert(!r.Completed && c == "C0" && s_ == "S0" && e == "E0" && prev == "(absent)", "table row 'died before any rename': C0 S0 E0 intact — " + state);
                            assert(File.Exists(plan.CatalogTmp) && File.Exists(plan.SidecarTmp) && File.Exists(plan.CompanionTmp!), "temps untouched when nothing ran");
                            break;
                        case PublicationStep.Catalog:
                            assert(!r.Completed && c == "C0" && s_ == "S0" && e == "(absent)" && prev == "E0", "table row 'aside done': C0 S0, companion absent (legacy: edges off), E0 kept as .prev — " + state);
                            break;
                        case PublicationStep.Companion:
                            assert(!r.Completed && c == "C1" && s_ == "S0" && e == "(absent)", "table row C1 S0 (no companion): legacy S0 older -> catalog parse; upgraded hash mismatch -> catalog parse — " + state);
                            assert(File.GetLastWriteTimeUtc(plan.SidecarPath) < File.GetLastWriteTimeUtc(plan.CatalogPath), "C1 S0: old sidecar strictly older than the new catalog");
                            break;
                        case PublicationStep.Sidecar:
                            assert(!r.Completed && c == "C1" && s_ == "S0" && e == "E1", "table row C1 S0 E1: new consistent on both readers after one slow boot — " + state);
                            assert(File.Exists(plan.SidecarTmp), "sidecar temp still present for the re-publish");
                            break;
                        case PublicationStep.Done:
                            assert(r.Completed && c == "C1" && s_ == "S1" && e == "E1" && prev == "(absent)", "table row C1 S1 E1: complete — " + state);
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
                assert(Get(plan.CatalogPath) == "C4" && Get(plan.SidecarPath) == "S3", "republish setup: C1 S0 E1 left behind");
                Put(plan.CatalogTmp, "C4"); Put(plan.SidecarTmp, "S4"); Put(plan.CompanionTmp!, "E4");
                var r = Publication.Publish(plan, live.AddMinutes(2));
                assert(r.Completed && Get(plan.CatalogPath) == "C4" && Get(plan.SidecarPath) == "S4" && Get(plan.CompanionPath!) == "E4",
                    "republish: completes to C1 S1 E1 (idempotent; M8 recovery path)");
                assert(File.GetLastWriteTimeUtc(plan.SidecarPath) >= File.GetLastWriteTimeUtc(plan.CatalogPath), "republish: pair satisfies the legacy predicate");

                // A missing temp is refused before anything moves.
                File.Delete(d.P("mbxmoods.mbxs.tmp"));
                Put(plan.CatalogTmp, "C5"); Put(plan.CompanionTmp!, "E5");
                bool threw = false;
                try { Publication.Publish(plan, live.AddMinutes(3)); } catch (FileNotFoundException) { threw = true; }
                assert(threw && Get(plan.CatalogPath) == "C4" && Get(plan.CompanionPath!) == "E4", "missing temp: refused up front, nothing renamed");
            }
            using (var d = new Dir())
            {
                // No companion this generation (edges off): catalog then sidecar, nothing else touched.
                var plan = new PublicationPlan
                {
                    CatalogPath = d.P("mbxmoods.json"), SidecarPath = d.P("mbxmoods.mbxs"), CompanionPath = null,
                    CatalogTmp = d.P("mbxmoods.json.tmp"), SidecarTmp = d.P("mbxmoods.mbxs.tmp"), CompanionTmp = null, Generation = 1,
                };
                Put(plan.CatalogTmp, "C1"); Put(plan.SidecarTmp, "S1");
                var r = Publication.Publish(plan, live);
                assert(r.Completed && Get(plan.CatalogPath) == "C1" && Get(plan.SidecarPath) == "S1" && !File.Exists(d.P("mbxedges.v1.json")),
                    "no-companion plan: catalog + sidecar published, no companion created");
                // FilesBehind: a catalog with no origin is 'behind' (legacy / unknown generation).
                assert(Publication.FilesBehind(plan.CatalogPath, 1, out var fg) && fg == 0, "FilesBehind: no origin block -> behind, generation unknown");
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
                assert(!Publication.FilesBehind(catalog, 9, out var g9) && g9 == 9, "FilesBehind: files at the store's generation -> not behind");
                assert(Publication.FilesBehind(catalog, 10, out var g10) && g10 == 9, "FilesBehind: store one ahead -> behind (M8), file generation reported");

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
            }
        }
    }
}
