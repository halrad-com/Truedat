using System;
using System.Collections.Generic;
using System.Linq;

namespace Truedat.Store
{
    /// <summary>Self-tests for <c>--store-summary</c>. The two mirrored HUB rules are pinned against the values read
    /// from their source (MBXH/Core/CamelotCode.cs Distance, MBXH/Core/ContinuityScorer.cs HarmonicTrust) so a local
    /// "tidy-up" of either shows up here rather than in a forum post. The rest pins this report's own arithmetic and
    /// the two asymmetries that make the key numbers mean what the output says they mean.</summary>
    internal static class StoreSummarySelfTest
    {
        internal static void Run(Action<bool, string> assert)
        {
            Console.WriteLine("Store-summary self-test (the hub's Camelot distance and harmonic trust, mirrored)");
            try { TestCamelotDistance(assert); } catch (Exception ex) { assert(false, $"StoreSummary.CamelotDistance: threw {ex.GetType().Name}: {ex.Message}"); }
            try { TestHarmonicTrust(assert); } catch (Exception ex) { assert(false, $"StoreSummary.HarmonicTrust: threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine("Store-summary self-test (keys: the producer's Camelot table, the flat-key ladder, agreement against a named reference)");
            try { TestCamelotOf(assert); } catch (Exception ex) { assert(false, $"StoreSummary.CamelotOf: threw {ex.GetType().Name}: {ex.Message}"); }
            try { TestKeyProvenance(assert); } catch (Exception ex) { assert(false, $"StoreSummary.KeyProvenance: threw {ex.GetType().Name}: {ex.Message}"); }
            try { TestAgreementWith(assert); } catch (Exception ex) { assert(false, $"StoreSummary.AgreementWith: threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine("Store-summary self-test (vectors and statistics)");
            try { TestVectors(assert); } catch (Exception ex) { assert(false, $"StoreSummary.Vectors: threw {ex.GetType().Name}: {ex.Message}"); }
            try { TestStatistics(assert); } catch (Exception ex) { assert(false, $"StoreSummary.Statistics: threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine("Store-summary self-test (the report: buckets, the fallback sentence, paste safety)");
            try { TestReportSections(assert); } catch (Exception ex) { assert(false, $"StoreSummary.Report: threw {ex.GetType().Name}: {ex.Message}"); }
            try { TestModelScale(assert); } catch (Exception ex) { assert(false, $"StoreSummary.ModelScale: threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine("Store-summary self-test (--store-summary-min: the sentences go, every row stays)");
            try { TestMinimalMode(assert); } catch (Exception ex) { assert(false, $"StoreSummary.Minimal: threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine("Store-summary self-test (tail shares and pooled statistics, key shares, closeness, a partial edge fill)");
            try { TestTailAndPooled(assert); } catch (Exception ex) { assert(false, $"StoreSummary.TailAndPooled: threw {ex.GetType().Name}: {ex.Message}"); }
            try { TestCloseness(assert); } catch (Exception ex) { assert(false, $"StoreSummary.Closeness: threw {ex.GetType().Name}: {ex.Message}"); }
            try { TestKeyShares(assert); } catch (Exception ex) { assert(false, $"StoreSummary.KeyShares: threw {ex.GetType().Name}: {ex.Message}"); }
            try { TestWholeTrackSound(assert); } catch (Exception ex) { assert(false, $"StoreSummary.Sound: threw {ex.GetType().Name}: {ex.Message}"); }
            try { TestRangeTables(assert); } catch (Exception ex) { assert(false, $"StoreSummary.RangeTables: threw {ex.GetType().Name}: {ex.Message}"); }
            try { TestPartialFill(assert); } catch (Exception ex) { assert(false, $"StoreSummary.PartialFill: threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine("Store-summary self-test (both modes end to end over real stores, incl. the shapes that compute nothing)");
            try { TestModesEndToEnd(assert); } catch (Exception ex) { assert(false, $"StoreSummary.EndToEnd: threw {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Both modes driven through the real entry point against real stores on disk. This pins two things
        /// the unit tests above cannot reach:
        ///
        /// <para>The WIRING. <see cref="StoreSummaryCommand.Run"/> is what turns the flag into the field, and the
        /// tests above set the field themselves, so a Run that ignored its argument passed the whole suite (mutation
        /// check, 2026-11-12: <c>Explanations = true</c> in Run left min printing the full block and nothing went
        /// red). Driving Run is the only thing that catches it.</para>
        ///
        /// <para>The shapes that COMPUTE NOTHING — a store too old to read, one with no typed edge table, one with no
        /// tracks. Those are the stores most likely to be posted by someone asking why their block is empty, and in
        /// min the reason is the only thing left, so it has to survive the strip. They were verified by hand on
        /// copies of a real store and nothing held them.</para></summary>
        static void TestModesEndToEnd(Action<bool, string> assert)
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "truedat-summin-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            System.IO.Directory.CreateDirectory(dir);
            bool saved = StoreSummaryCommand.Explanations;
            try
            {
                // (a) a store too old to read at all: schema v1 predates the per-media split.
                var v1 = System.IO.Path.Combine(dir, "v1.db");
                using (var db = SqliteDb.Open(v1, true, exclusiveLocking: false)) SchemaMigrator.Apply(db, "selftest-min-v1", targetVersion: 1);
                Shape(assert, v1, "schema v1", "predates the per-media split", shorterThanFull: false);

                // (b) readable, but no typed edge table: everything about edges is a shortfall, not a zero.
                var v3 = System.IO.Path.Combine(dir, "v3.db");
                using (var db = SqliteDb.Open(v3, true, exclusiveLocking: false)) SchemaMigrator.Apply(db, "selftest-min-v3", targetVersion: 3);
                Shape(assert, v3, "schema v3", "the typed edge table arrived at v4", shorterThanFull: true);

                // (c) current schema, no tracks: the range section must say that rather than print blank rows.
                var v4 = System.IO.Path.Combine(dir, "v4.db");
                using (var db = SqliteDb.Open(v4, true, exclusiveLocking: false)) SchemaMigrator.Apply(db, "selftest-min-v4", targetVersion: 4);
                Shape(assert, v4, "empty store", "the store holds no tracks", shorterThanFull: true);

                // (d) current schema with a row, no edges: the range table has data, so the subset check runs over
                //     real rows and the reference line has to survive beside them.
                var withRow = System.IO.Path.Combine(dir, "row.db");
                var e = new TrackEntry
                {
                    Features = new TrackFeatures { Artist = "A", Title = "t", Bpm = 120, Loudness = -12.1 },
                    LastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), AudioStreamSha256 = new string('h', 64),
                };
                using (StoreLog.Quiet())
                    ImportCommand.ExecuteEntries(withRow, new Dictionary<string, TrackEntry>(PathComparer.Instance) { [@"D:\music\t.flac"] = e },
                        @"D:\music", "primary", true, "essentia", "x", "fixture", null, null);
                var minLines = Shape(assert, withRow, "one track", "reference: " + StoreSummaryCommand.ModelTag, shorterThanFull: true);
                assert(minLines.Any(l => l.TrimStart().StartsWith("bpm", StringComparison.Ordinal) && l.Contains("120")),
                       "End to end: the range table's rows are printed in min — the strip takes sentences, not data");

                // The sections added after the first release are reached from the real entry point, in order. The
                // unit tests call each one directly, so only this sees a section left out of the block.
                int At(string head) => minLines.FindIndex(l => l.StartsWith(head, StringComparison.Ordinal));
                int shares = At("KEYS — share of tracks"), sound = At("SOUND — whole-track chroma"), join = At("AT A JOIN"),
                    sigmas = minLines.FindIndex(l => l.Contains("in fitted sigmas"));
                assert(shares > 0 && sound > shares && join > sound && sigmas > join,
                       $"End to end: key shares, whole-track sound, the join section and the sigma table all print, in that order (at {shares}, {sound}, {join}, {sigmas})");
            }
            finally
            {
                StoreSummaryCommand.Explanations = saved;   // Run leaves the field where the last call put it
                try { System.IO.Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>Runs one store both ways and asserts the properties that define the mode: it exits 0, every line
        /// min prints is one the full block also prints, the line saying what the store holds survives, and the block
        /// still fits its width. Returns min's lines.</summary>
        static List<string> Shape(Action<bool, string> assert, string storePath, string what, string stateLine, bool shorterThanFull)
        {
            var full = RunBlock(storePath, explanations: true, out int fullExit);
            var min = RunBlock(storePath, explanations: false, out int minExit);

            assert(fullExit == 0 && minExit == 0, $"End to end ({what}): both modes exit 0 (full {fullExit}, min {minExit})");

            var fullSet = new HashSet<string>(full);
            var extra = min.Where(l => !fullSet.Contains(l)).ToList();
            assert(extra.Count == 0, $"End to end ({what}): every line min prints appears verbatim in the full block"
                                     + (extra.Count > 0 ? $" (extra: \"{extra[0]}\")" : ""));

            assert(min.Any(l => l.Contains(stateLine)),
                   $"End to end ({what}): the line that says what the store holds survives the strip — \"{stateLine}\"");

            // The whole point of the mode. A shape whose output is nothing BUT state lines has nothing to drop, and
            // says so by passing shorterThanFull: false rather than by quietly not being checked.
            if (shorterThanFull)
                assert(min.Count < full.Count, $"End to end ({what}): min is shorter than full ({min.Count} vs {full.Count} lines)");
            else
                assert(min.Count == full.Count, $"End to end ({what}): nothing to drop, so the two are identical ({min.Count} vs {full.Count} lines)");

            // The width is the reason the block is wrapped at all: several of these posted one under another only
            // line up if every line fits. Two shipped lines did not, and composed their overflow from store state
            // (a shortfall sentence in the header, a long list of absent features), so only a real store shows it.
            var wide = full.Concat(min).Where(l => l.Length > StoreSummaryCommand.BlockWidth).ToList();
            assert(wide.Count == 0, $"End to end ({what}): every line fits the {StoreSummaryCommand.BlockWidth}-column block"
                                    + (wide.Count > 0 ? $" ({wide.Count} over, first {wide[0].Length}: \"{wide[0]}\")" : ""));

            return min;
        }

        /// <summary>The real entry point with the console captured, returning the lines BETWEEN the copy markers —
        /// the part a user actually pastes.</summary>
        static List<string> RunBlock(string storePath, bool explanations, out int exit)
        {
            var sw = new System.IO.StringWriter();
            var savedOut = Console.Out; var savedErr = Console.Error;
            Console.SetOut(sw); Console.SetError(System.IO.TextWriter.Null);
            try { using (StoreLog.Quiet()) exit = StoreSummaryCommand.Run(storePath, explanations); }
            finally { Console.SetOut(savedOut); Console.SetError(savedErr); }

            var all = sw.ToString().Replace("\r\n", "\n").Split('\n');
            var block = new List<string>();
            bool inside = false;
            foreach (var l in all)
            {
                if (l.StartsWith("--- copy from here", StringComparison.Ordinal)) { inside = true; continue; }
                if (l.StartsWith("--- to here", StringComparison.Ordinal)) break;
                if (inside) block.Add(l);
            }
            return block;
        }

        /// <summary><c>--store-summary-min</c> drops the sentences that explain what a row means, and nothing else.
        /// The load-bearing assertion is the SUBSET one: every line min prints appears verbatim in the full block, so
        /// the mode can never quietly change a number — only leave prose out.</summary>
        static void TestMinimalMode(Action<bool, string> assert)
        {
            // A fallback rung present, so the explanation under the key rows has a number in its own sentence: that
            // is exactly the case where dropping prose might have cost something, and the row below must carry it.
            var tracks = new List<SummaryTrack>
            {
                T("8B", "8B", "8B", 3, 3, "edma"), T("8B", "9B", "8B", 3, 3, "edma"),
                T("5A", "5A", "12A", 2, 2, "edma"), T("7B", "7B", "7B", 0, 3, "chords"),
            };

            var full = new List<string>();
            StoreSummaryCommand.KeyAgreement(tracks, tracks.Count, full);

            var min = new List<string>();
            bool saved = StoreSummaryCommand.Explanations;
            try
            {
                StoreSummaryCommand.Explanations = false;
                StoreSummaryCommand.KeyAgreement(tracks, tracks.Count, min);
            }
            finally { StoreSummaryCommand.Explanations = saved; }

            var minText = string.Join("\n", min);

            // The rows, every one of them, unchanged.
            assert(min.Any(l => l.Contains("vs the stored key") && l.Contains("3 of 3 50.0%") && l.Contains("0 of 3 25.0%")),
                   "Minimal: the whole-track buckets are printed with the same numbers as the full block");
            assert(min.Any(l => l.Contains("vs the EDMA vote")), "Minimal: the second whole-track row stays");
            assert(min.Any(l => l.Contains("start window")), "Minimal: the start-window row stays");
            assert(min.Any(l => l.Contains("end window")), "Minimal: the end-window row stays");
            assert(min.Any(l => l.Contains("the stored key came from: edma 3, chords 1")),
                   "Minimal: the provenance row stays — it carries as data what the dropped sentence said in prose");

            // The sentences, gone.
            assert(!minText.Contains("Agreement decides how much say"),
                   "Minimal: the agreement legend is dropped");
            assert(!minText.Contains("never 0 of n") && !minText.Contains("and 1 here did"),
                   "Minimal: the sentence explaining the two whole-track rows is dropped");

            // Nothing invented and nothing altered: min is a subset of full, line for line.
            var fullSet = new HashSet<string>(full);
            var extra = min.Where(l => !fullSet.Contains(l)).ToList();
            assert(extra.Count == 0,
                   "Minimal: every line is one the full block also prints" + (extra.Count > 0 ? " (extra: \"" + extra[0] + "\")" : ""));
            assert(min.Count < full.Count, $"Minimal: the block is shorter than the full one ({min.Count} vs {full.Count} lines)");

            // A shortfall is state, not explanation: a section that computed nothing must still say so in min, or the
            // reader sees a bare heading and cannot tell an empty store from a broken one.
            var shortfall = new List<string>();
            saved = StoreSummaryCommand.Explanations;
            try
            {
                StoreSummaryCommand.Explanations = false;
                StoreSummaryCommand.KeyAgreement(new List<SummaryTrack>(), 0, shortfall);
            }
            finally { StoreSummaryCommand.Explanations = saved; }
            assert(shortfall.Any(l => l.Contains("not computed")),
                   "Minimal: a section that computed nothing still says why");

            // The coverage gap is a row in BOTH modes — a sentence there wrapped to two lines and read as prose at
            // the top of a min block, and the number is the whole of what it says.
            var gap = StoreSummaryCommand.CoverageGapLine(640);
            assert(gap.Contains("640") && gap.Length <= StoreSummaryCommand.BlockWidth && !gap.EndsWith("."),
                   $"Minimal: the coverage gap is one row carrying its count, not a sentence — \"{gap}\"");

            // The flag the mode rides on, and the default: a bare --store-summary still explains itself.
            assert(StoreSummaryCommand.Explanations, "Minimal: explanations are on unless the min flag asks otherwise");
            assert(Program.KnownFlags.Contains("store-summary-min"),
                   "Minimal: --store-summary-min is a known flag");
        }

        static bool Near(double a, double b, double tol = 1e-9) => Math.Abs(a - b) <= tol;

        // ---------------------------------------------------------------- tails, pooled statistics, key shares, closeness

        static double[] E(int bin, int dim) { var v = new double[dim]; v[bin] = 1; return v; }

        /// <summary>A track with a whole-track key and chroma and NO edge windows — what most of a library looks
        /// like while its edges are still being filled.</summary>
        static SummaryTrack W(string code, int bin) => new SummaryTrack
        {
            WholeCode = code, W12 = E(bin, 12), FlatAgree = 3, FlatPresent = 3, EdmaAgree = 3, EdmaPresent = 3, Provenance = "edma",
        };

        /// <summary>A track with everything a join needs.</summary>
        static SummaryTrack Full(string code, int bin) => new SummaryTrack
        {
            WholeCode = code, HeadCode = code, TailCode = code, W12 = E(bin, 12), H36 = E(bin, 36), T36 = E(bin, 36),
            FlatAgree = 3, FlatPresent = 3, EdmaAgree = 3, EdmaPresent = 3,
            HeadAgree = 3, HeadPresent = 3, TailAgree = 3, TailPresent = 3, Provenance = "edma",
        };

        static string[] Cells(string line) => line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        /// <summary>The section printed both ways: every line min prints is one full prints, and full says more.</summary>
        static void MinIsSubset(Action<bool, string> assert, string what, Action<List<string>> section)
        {
            var full = new List<string>(); var min = new List<string>();
            bool saved = StoreSummaryCommand.Explanations;
            try
            {
                StoreSummaryCommand.Explanations = true; section(full);
                StoreSummaryCommand.Explanations = false; section(min);
            }
            finally { StoreSummaryCommand.Explanations = saved; }
            var fullSet = new HashSet<string>(full);
            var extra = min.Where(l => !fullSet.Contains(l)).ToList();
            assert(min.Count > 0 && extra.Count == 0,
                   $"{what}: every line min prints is one the full block also prints ({min.Count} lines"
                   + (extra.Count > 0 ? $", extra: \"{extra[0]}\")" : ")"));
            assert(min.Count < full.Count, $"{what}: min drops the explanation and nothing else ({min.Count} vs {full.Count} lines)");
            var wide = full.Where(l => l.Length > StoreSummaryCommand.BlockWidth).ToList();
            assert(wide.Count == 0, $"{what}: every line fits the {StoreSummaryCommand.BlockWidth}-column block"
                                    + (wide.Count > 0 ? $" (first {wide[0].Length}: \"{wide[0]}\")" : ""));
        }

        static void TestTailAndPooled(Action<bool, string> assert)
        {
            // Divide by n, not n-1: this is compared against the model's fitted scale, and that scaler divides by n.
            assert(Near(StoreSummary.Std(new double[] { 2, 4, 4, 4, 5, 5, 7, 9 }), 2.0, 1e-12),
                   $"Std: the population standard deviation of 2,4,4,4,5,5,7,9 is 2 (got {StoreSummary.Std(new double[] { 2, 4, 4, 4, 5, 5, 7, 9 })})");
            assert(Near(StoreSummary.Std(new double[] { 7 }), 0.0), "Std: one value has no spread (0)");
            assert(double.IsNaN(StoreSummary.Std(new double[0])), "Std: an empty sample is NaN");

            // Mean 10, std 2, bound 4: past the bound is under 2 or over 18.
            var t = StoreSummary.TailCounts(new double[] { 1.9, 2, 10, 18, 18.1, 30 }, 10, 2, 4);
            assert(t.Under == 1 && t.Over == 2, $"Tail: one value under mean - 4 std and two over mean + 4 std (got {t.Under} under, {t.Over} over)");
            var at = StoreSummary.TailCounts(new double[] { 2, 18 }, 10, 2, 4);
            assert(at.Under == 0 && at.Over == 0, $"Tail: a value exactly at the bound is not past it (got {at.Under}, {at.Over})");
            var flat = StoreSummary.TailCounts(new double[] { 1, 2, 3 }, 0, 0, 4);
            assert(flat.Under == 0 && flat.Over == 0, $"Tail: no fitted spread means no tail to count (got {flat.Under}, {flat.Over})");
        }

        static void TestCloseness(Action<bool, string> assert)
        {
            // The hub's JoinCloseness.CloserThanPercent: the share of the sample STRICTLY farther apart.
            var ramp = new double[1000];
            for (int i = 0; i < ramp.Length; i++) ramp[i] = i / 1000.0;
            assert(Near(StoreSummary.CloserThanPercent(0.25, ramp), 74.9),
                   $"Closeness: 0.25 on an even ramp of 1000 is closer than 74.9% of it (got {StoreSummary.CloserThanPercent(0.25, ramp)})");
            assert(Near(StoreSummary.CloserThanPercent(-1, ramp), 100.0), "Closeness: nearer than every pair is 100");
            assert(Near(StoreSummary.CloserThanPercent(2, ramp), 0.0), "Closeness: farther than every pair is 0");

            var ties = Enumerable.Repeat(0.5, 100).ToArray();
            assert(Near(StoreSummary.CloserThanPercent(0.5, ties), 0.0), "Closeness: a pair at the same distance is not farther, so ties count for nothing");

            assert(double.IsNaN(StoreSummary.CloserThanPercent(0.25, ramp.Take(StoreSummary.MinClosenessSample - 1).ToArray())),
                   "Closeness: a sample under the hub's minimum has no rank (NaN, never a number)");
            assert(double.IsNaN(StoreSummary.CloserThanPercent(double.NaN, ramp)), "Closeness: a distance that is not a number has no rank");
            assert(StoreSummary.MinClosenessSample == 100 && StoreSummary.MinClosenessTracks == 100,
                   "Closeness: the minimum sample and track count are the hub's (100 and 100)");
        }

        static void TestKeyShares(Action<bool, string> assert)
        {
            // Two tracks on 8B, one on 8A, one on 1A, and one with no key at all (it is in no share).
            var tracks = new List<SummaryTrack> { W("8B", 0), W("8B", 0), W("8A", 1), W("1A", 2), new SummaryTrack() };
            var o = new List<string>();
            StoreSummaryCommand.KeyShares(tracks, o);

            var head = o.FirstOrDefault(l => Cells(l).SequenceEqual(Enumerable.Range(1, 12).Select(n => n.ToString())));
            assert(head != null, "Key shares: a header row numbers the twelve Camelot hours");
            var a = o.Where(l => l.StartsWith("  A ", StringComparison.Ordinal)).Select(Cells).FirstOrDefault();
            var b = o.Where(l => l.StartsWith("  B ", StringComparison.Ordinal)).Select(Cells).FirstOrDefault();
            assert(a != null && a.Length == 13 && a[1] == "25.0" && a[8] == "25.0" && a[2] == "0.0",
                   "Key shares: the minor ring holds 25.0 at hour 1 and 25.0 at hour 8, of the tracks that carry a key"
                   + (a != null ? $" (got {string.Join(" ", a)})" : " (no A row)"));
            assert(b != null && b.Length == 13 && b[8] == "50.0" && b[1] == "0.0",
                   "Key shares: the major ring holds 50.0 at hour 8" + (b != null ? $" (got {string.Join(" ", b)})" : " (no B row)"));
            assert(o.Any(l => l.Contains("minor (A) 50.0%") && l.Contains("major (B) 50.0%") && l.Contains("(n=4)")),
                   "Key shares: the two rings are totalled, over the four tracks that carry a key");

            var none = new List<string>();
            StoreSummaryCommand.KeyShares(new List<SummaryTrack> { new SummaryTrack() }, none);
            assert(none.Any(l => l.Contains("not computed")), "Key shares: no keyed track is 'not computed', never a row of zeros");

            MinIsSubset(assert, "Key shares", l => StoreSummaryCommand.KeyShares(tracks, l));
        }

        static void TestWholeTrackSound(Action<bool, string> assert)
        {
            // 120 tracks, none with an edge window. Two keys, and chroma that follows the key exactly: a same-key pair
            // is 0 apart and any other pair is 1 apart.
            var lib = new List<SummaryTrack>();
            for (int i = 0; i < 60; i++) { lib.Add(W("8B", 0)); lib.Add(W("3A", 1)); }
            var o = new List<string>();
            StoreSummaryCommand.WholeTrackSound(lib, o);

            assert(o.Any(l => l.Contains("pairs sharing a key:") && l.Contains("0.0000")),
                   "Sound: computed from tracks that carry no edge windows at all — same-key pairs average 0.0000");
            assert(o.Any(l => l.Contains("pairs on another key:") && l.Contains("1.0000")),
                   "Sound: pairs on another key average 1.0000");
            assert(o.Any(l => l.Contains("any pair:") && l.Contains("p5 ") && l.Contains("p10 ") && l.Contains("p25 ") && l.Contains("p50 ")),
                   "Sound: the library's own pair distances are printed as percentiles");

            // About half of all pairs are other-key pairs at distance 1, strictly farther than the same-key mean of 0.
            var row = o.FirstOrDefault(l => l.Contains("% of this library's pairs"));
            var m = row == null ? null : System.Text.RegularExpressions.Regex.Match(row, @"pairs sharing a key are closer than ([0-9.]+)% of this library's pairs");
            double pct = m != null && m.Success ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : double.NaN;
            assert(pct > 45 && pct < 56,
                   $"Sound: 'pairs sharing a key are closer than N% of this library's pairs', N near 50 here (got \"{row}\")");

            // The self-check is a relationship, not a figure: an earlier wording quoted two numbers from other
            // libraries as the expected reading, and a healthy library elsewhere need not sit on them.
            // (Whitespace collapsed: the sentence is wrapped to the block width, so its words can straddle two lines.)
            var text = System.Text.RegularExpressions.Regex.Replace(string.Join(" ", o), @"\s+", " ");
            assert(text.Contains("clearly closer") && !text.Contains("0.08") && !text.Contains("0.15"),
                   "Sound: the self-check names the relationship (same key clearly closer) and quotes no other library's figures");

            // Under the hub's minimum a library is not ranked against at all; the two means still print.
            var small = new List<SummaryTrack>();
            for (int i = 0; i < 5; i++) { small.Add(W("8B", 0)); small.Add(W("3A", 1)); }
            var o2 = new List<string>();
            StoreSummaryCommand.WholeTrackSound(small, o2);
            assert(o2.Any(l => l.Contains("pairs sharing a key:") && l.Contains("0.0000")), "Sound: a small library still prints its two means");
            assert(o2.Any(l => l.Contains("closeness") && l.Contains("not computed")) && !o2.Any(l => l.Contains("% of this library's pairs")),
                   "Sound: under 100 tracks the closeness row is 'not computed', as the hub gives no rank there");

            var o3 = new List<string>();
            StoreSummaryCommand.WholeTrackSound(new List<SummaryTrack> { W("8B", 0) }, o3);
            assert(o3.Any(l => l.Contains("not computed")), "Sound: one track is 'not computed'");

            MinIsSubset(assert, "Sound", l => StoreSummaryCommand.WholeTrackSound(lib, l));
        }

        static void TestRangeTables(Action<bool, string> assert)
        {
            // Fitted mean 100, std 10. One value under 4 sigma (below 60), one over (above 140).
            var data = new List<(StoreSummaryCommand.Feat F, List<double> Values)>
            {
                (new StoreSummaryCommand.Feat("bpm", 100, 10), new List<double> { 140.1, 100, 59.9, 100, 100 }),
                (new StoreSummaryCommand.Feat("spectralFlatness", 0, 1), new List<double> { 0.1, 0.2 }),
                (new StoreSummaryCommand.Feat("hfc", 20, 13), new List<double>()),
            };
            var o = new List<string>();
            StoreSummaryCommand.RangeTables(data, o);

            int split = o.FindIndex(l => l.Contains("in fitted sigmas"));
            assert(split > 0, "Range: a second table, in fitted sigmas, follows the raw one");
            var raw = split > 0 ? o.Take(split).ToList() : new List<string>();
            var sig = split > 0 ? o.Skip(split).ToList() : new List<string>();

            assert(raw.Count > 0 && Cells(raw[0]).SequenceEqual(new[] { "feature", "n", "p5", "p50", "p95", "mean", "std" }),
                   "Range: the raw table's columns are n, p5, p50, p95, mean, std");
            var r = raw.Select(Cells).FirstOrDefault(c => c.Length > 0 && c[0] == "bpm");
            assert(r != null && r.Length == 7 && r[1] == "5" && r[2] == "67.920" && r[3] == "100.0" && r[4] == "132.1" && r[5] == "100.0" && r[6].StartsWith("25.36", StringComparison.Ordinal),
                   "Range: the raw row carries n 5, p5 67.920, p50 100.0, p95 132.1, mean 100.0, std 25.36"
                   + (r != null ? $" (got {string.Join(" ", r)})" : " (no row)"));

            assert(sig.Count > 0 && sig[0].Contains("under -4") && sig[0].Contains("over +4"),
                   "Range: the sigma table's last two columns are the shares of tracks past 4 sigma each way");
            var s = sig.Select(Cells).FirstOrDefault(c => c.Length > 0 && c[0] == "bpm");
            assert(s != null && s.SequenceEqual(new[] { "bpm", "-3.2", "0.0", "+3.2", "20.00%", "20.00%" }),
                   "Range: the sigma row carries p5 -3.2, p50 0.0, p95 +3.2, and 20.00% of tracks past the bound each way"
                   + (s != null ? $" (got {string.Join(" ", s)})" : " (no row)"));

            // Zero carries no sign, from either side: a column mixing "+0.0", "0.0" and "-0.0" for the same reading
            // looks like three different values to someone lining blocks up.
            var z = new[] { 0.0, 0.04, -0.04 }.Select(StoreSummaryCommand.Sig).ToArray();
            assert(z.All(x => x == "0.0"), $"Range: a sigma that rounds to zero prints as 0.0 whichever side it came from (got {string.Join(", ", z)})");
            assert(StoreSummaryCommand.Sig(0.2) == "+0.2" && StoreSummaryCommand.Sig(-0.2) == "-0.2",
                   $"Range: any other sigma carries its sign (got {StoreSummaryCommand.Sig(0.2)}, {StoreSummaryCommand.Sig(-0.2)})");

            // A feature the model takes as measured still has raw statistics worth pooling; it has no sigmas.
            assert(raw.Select(Cells).Any(c => c.Length == 7 && c[0] == "spectralFlatness" && c[1] == "2"),
                   "Range: a feature with no usable fitted scale still gets its raw row");
            assert(!sig.Select(Cells).Any(c => c.Length > 0 && c[0] == "spectralFlatness" && c.Length == 6),
                   "Range: and no sigma row");
            assert(o.Any(l => l.Contains("taken by the model 1:1, as measured")) && string.Join(" ", o).Contains("spectralFlatness"),
                   "Range: the output says which feature the model takes as measured");
            assert(o.Any(l => l.Contains("no value stored in this library: hfc")),
                   "Range: a feature with no stored value is named, not printed as a row of zeros");
            assert(StoreSummaryCommand.ValenceInputBound == 4.0, "Range: the bound is the model's own input bound (4 sigma)");

            MinIsSubset(assert, "Range", l => StoreSummaryCommand.RangeTables(data, l));
        }

        static void TestPartialFill(Action<bool, string> assert)
        {
            // Three tracks a scan has folded, two it has not reached yet.
            var tracks = new List<SummaryTrack> { Full("8B", 0), Full("8B", 0), Full("3A", 1), W("5A", 2), W("5A", 2) };
            var o = new List<string>();
            StoreSummaryCommand.JoinSeparation(tracks, o);
            assert(o.Any(l => l.Contains("tracks carrying everything a join needs: 3 of 5 (60.0%)")),
                   "Partial fill: the join section says how much of the library its rows describe (3 of 5)");

            var min = new List<string>();
            bool saved = StoreSummaryCommand.Explanations;
            try { StoreSummaryCommand.Explanations = false; StoreSummaryCommand.JoinSeparation(tracks, min); }
            finally { StoreSummaryCommand.Explanations = saved; }
            assert(min.Any(l => l.Contains("tracks carrying everything a join needs: 3 of 5 (60.0%)")),
                   "Partial fill: the coverage row is data, so it survives --store-summary-min");
            var fullSet = new HashSet<string>(o);
            assert(min.All(fullSet.Contains), "Partial fill: the join section in min is a subset of the full one");

            // The whole-track sections do not wait for edges: the two unfolded tracks are counted there.
            var keys = new List<string>();
            StoreSummaryCommand.KeyShares(tracks, keys);
            assert(keys.Any(l => l.Contains("(n=5)")), "Partial fill: key shares count every keyed track, folded or not");
        }

        // ---------------------------------------------------------------- the hub's rules

        static void TestCamelotDistance(Action<bool, string> assert)
        {
            var d = (Func<string?, string?, double>)StoreSummary.CamelotDistance;

            assert(Near(d("8B", "8B"), 0.0), "Camelot: the same code is compatible (0)");
            assert(Near(d("8B", "8A"), 0.0), "Camelot: the A/B swap at the same number is compatible (0)");
            assert(Near(d("8B", "9B"), 0.0), "Camelot: one hour apart, same letter, is compatible (0)");
            assert(Near(d("8B", "7B"), 0.0), "Camelot: one hour back, same letter, is compatible (0)");
            assert(Near(d("12B", "1B"), 0.0), "Camelot: the wheel wraps — 12B to 1B is one hour (0)");
            assert(Near(d("1B", "12B"), 0.0), "Camelot: the wrap is symmetric — 1B to 12B is one hour (0)");
            assert(Near(d("8B", "10B"), 0.5), "Camelot: two hours apart, same letter, is near (0.5)");
            assert(Near(d("12B", "2B"), 0.5), "Camelot: two hours across the wrap is near (0.5)");
            assert(Near(d("8B", "11B"), 1.0), "Camelot: three hours apart is a clash (1)");
            assert(Near(d("8B", "9A"), 1.0), "Camelot: a different number AND letter is a clash (1), even one hour apart");

            // A verb runs over whatever a store holds. Unparseable must be a clash, never an exception.
            assert(Near(d(null, "8B"), 1.0), "Camelot: a null code is a clash, not a throw");
            assert(Near(d("", "8B"), 1.0), "Camelot: an empty code is a clash");
            assert(Near(d("8", "8B"), 1.0), "Camelot: a code with no letter is a clash");
            assert(Near(d("8C", "8B"), 1.0), "Camelot: a letter that is not A or B is a clash");
            assert(Near(d("13B", "13B"), 1.0), "Camelot: a number outside 1..12 is a clash even against itself");
            assert(Near(d("0A", "0A"), 1.0), "Camelot: hour 0 does not exist — a clash");
            assert(Near(d("xB", "8B"), 1.0), "Camelot: a non-numeric hour is a clash");
        }

        static void TestHarmonicTrust(Action<bool, string> assert)
        {
            var t = (Func<int, int, double>)StoreSummary.HarmonicTrust;

            assert(Near(t(0, 0), 1.0), "Trust: no votes carried is 1.0 (a pre-wave entry is not penalised)");
            assert(Near(t(3, 3), 1.0), "Trust: 3 of 3 is 1.0");
            assert(Near(t(2, 2), 1.0), "Trust: 2 of 2 is 1.0 (unanimous among those carried)");
            assert(Near(t(2, 3), 0.5), "Trust: 2 of 3 is a strict majority (0.5)");
            assert(Near(t(1, 1), 0.5), "Trust: 1 of 1 is 0.5 — the only voter is not a consensus");
            assert(Near(t(1, 2), 0.0), "Trust: 1 of 2 is 0.0 — a split is NOT a majority");
            assert(Near(t(1, 3), 0.0), "Trust: 1 of 3 is 0.0 — outvoted or alone");
            assert(Near(t(0, 3), 0.0), "Trust: 0 of 3 is 0.0 — reachable for a WHOLE track on a fallback rung, never for an edge");
            assert(Near(t(0, 1), 0.0), "Trust: 0 of 1 is 0.0");
        }

        // ---------------------------------------------------------------- keys

        static void TestCamelotOf(Action<bool, string> assert)
        {
            var c = (Func<string?, string?, string?>)StoreSummary.CamelotOf;

            // The producer's own table (EdgeKey.Camelot): minor = A, major = B.
            assert(c("C", "major") == "8B", "CamelotOf: C major is 8B (the producer's table)");
            assert(c("A", "minor") == "8A", "CamelotOf: A minor is 8A");
            assert(c("Ab", "minor") == "1A", "CamelotOf: Ab minor is 1A");
            assert(c("F#", "major") == "2B", "CamelotOf: F# major is 2B");

            // The extractor's own spelling varies in case and the scale string is free text in old catalogs.
            assert(c("c", "MAJOR") == "8B", "CamelotOf: case does not matter in key or scale");
            assert(c("C", "min") == "5A", "CamelotOf: a 'min' prefix reads as minor (legacy catalogs abbreviate)");

            // Enharmonics the producer never writes, but a legacy or hand-edited store can hold.
            assert(c("G#", "minor") == "1A", "CamelotOf: G# minor normalises to Ab minor (1A)");
            assert(c("Db", "major") == "3B", "CamelotOf: Db major normalises to C# major (3B)");
            assert(c("A#", "minor") == "3A", "CamelotOf: A# minor normalises to Bb minor (3A)");

            assert(c(null, "major") == null, "CamelotOf: no key names no code");
            assert(c("C", null) == null, "CamelotOf: no scale names no code");
            assert(c("C", "") == null, "CamelotOf: an empty scale names no code");
            assert(c("H", "major") == null, "CamelotOf: a key name no table carries names no code");
        }

        static void TestKeyProvenance(Action<bool, string> assert)
        {
            var p = (Func<string?, string?, string?, string?, string?, string?, string?, string?, string>)StoreSummary.KeyProvenance;

            // The common case: EDMA decided, so the flat pair is EDMA's.
            assert(p("C", "major", "C", "major", "G", "major", "F", "major") == "edma",
                   "Provenance: the flat pair matching the edma vote is 'edma'");

            // Program.cs:27863 — EDMA declined, krumhansl supplied both.
            assert(p("G", "major", "", "", "G", "major", "F", "minor") == "krumhansl",
                   "Provenance: EDMA absent and the flat pair matching krumhansl is 'krumhansl'");

            // Program.cs:27864 — the last rung is chord-based, not a profile at all.
            assert(p("F", "minor", "", "", "", "", "F", "minor") == "chords",
                   "Provenance: both profiles absent and the flat pair matching chords_key is 'chords'");

            // The ladder is walked INDEPENDENTLY for key and scale, so a row can be mixed.
            assert(p("C", "minor", "C", "", "G", "minor", "F", "major") == "edma/krumhansl",
                   "Provenance: key from edma and scale from krumhansl reports both rungs");

            assert(p("", "", "C", "major", "G", "major", "F", "major") == "missing",
                   "Provenance: no flat key and no flat mode is 'missing'");
        }

        static void TestAgreementWith(Action<bool, string> assert)
        {
            var votes = new List<(string?, string?, bool)>
            {
                ("C", "major", true),    // krumhansl
                ("G", "major", true),    // temperley
                ("C", "major", true),    // edma
            };

            var a = StoreSummary.AgreementWith("C", "major", votes);
            assert(a.Present == 3 && a.Agreeing == 2, $"Agreement: 2 of 3 agree with C major (got {a.Agreeing} of {a.Present})");

            // Counted against EDMA's own vote, EDMA always agrees with itself — so an edge-style count is never 0.
            var e = StoreSummary.AgreementWith("C", "major", new List<(string?, string?, bool)>
            {
                ("G", "major", true), ("F", "minor", true), ("C", "major", true),
            });
            assert(e.Present == 3 && e.Agreeing == 1, $"Agreement: EDMA alone against two dissenters is 1 of 3 (got {e.Agreeing} of {e.Present})");

            // The asymmetry the report must state: counted against a FALLBACK flat key, zero profiles can agree.
            var z = StoreSummary.AgreementWith("F", "minor", votes);
            assert(z.Present == 3 && z.Agreeing == 0,
                   $"Agreement: a chords-rung flat key no profile voted for is 0 of 3 (got {z.Agreeing} of {z.Present})");

            // A profile that did not decide is not carried, and must not count as a dissenter.
            var partial = StoreSummary.AgreementWith("C", "major", new List<(string?, string?, bool)>
            {
                ("C", "major", true), ("", "", false), (null, null, false),
            });
            assert(partial.Present == 1 && partial.Agreeing == 1,
                   $"Agreement: profiles that did not decide are not carried (got {partial.Agreeing} of {partial.Present})");

            // Scale is part of the pair: the same tonic in the other mode is a dissenter, not an agreement.
            var mode = StoreSummary.AgreementWith("C", "major", new List<(string?, string?, bool)>
            {
                ("C", "minor", true), ("C", "major", true), ("C", "major", true),
            });
            assert(mode.Agreeing == 2, $"Agreement: the same tonic in the other mode does not agree (got {mode.Agreeing})");

            var none = StoreSummary.AgreementWith("", "", votes);
            assert(none.Agreeing == 0 && none.Present == 3,
                   $"Agreement: an absent reference agrees with nothing (got {none.Agreeing} of {none.Present})");

            // The hub's gate on whether a key has any say at all (PfsOrderer.TrustedKeyDistance drops the comparison
            // when HarmonicTrust is 0 on either side). The 'trusted' verdict rows keep only pairs that pass it, so the
            // report separates what the hub acts on from what it ignores.
            assert(StoreSummary.HarmonicTrust(3, 3) > 0 && StoreSummary.HarmonicTrust(2, 3) > 0 && StoreSummary.HarmonicTrust(1, 1) > 0,
                   "Trusted gate: 3 of 3, 2 of 3 and 1 of 1 all carry trust the hub acts on");
            assert(!(StoreSummary.HarmonicTrust(1, 3) > 0) && !(StoreSummary.HarmonicTrust(1, 2) > 0) && !(StoreSummary.HarmonicTrust(0, 3) > 0),
                   "Trusted gate: 1 of 3, 1 of 2 and 0 of 3 carry no trust, so the hub has no opinion on that pair");
        }

        // ---------------------------------------------------------------- vectors and statistics

        static void TestVectors(Action<bool, string> assert)
        {
            var u = StoreSummary.Unit(new double[] { 3, 4 });
            assert(u != null && Near(u![0], 0.6) && Near(u[1], 0.8), "Unit: (3,4) scales to (0.6,0.8)");
            assert(StoreSummary.Unit(new double[] { 0, 0, 0 }) == null, "Unit: a flat vector has no direction (null, never a 0-distance match)");
            assert(StoreSummary.Unit(null) == null, "Unit: null in, null out");
            assert(StoreSummary.Unit(new double[0]) == null, "Unit: an empty vector has no direction");

            var a = StoreSummary.Unit(new double[] { 1, 0 })!;
            var b = StoreSummary.Unit(new double[] { 0, 1 })!;
            assert(Near(StoreSummary.CosineDistance(a, a), 0.0), "Cosine: a vector against itself is 0");
            assert(Near(StoreSummary.CosineDistance(a, b), 1.0), "Cosine: orthogonal vectors are 1 apart");

            var c = StoreSummary.Unit(new double[] { 1, 1 })!;
            assert(Near(StoreSummary.CosineDistance(a, c), 1.0 - 1.0 / Math.Sqrt(2.0), 1e-12),
                   "Cosine: 45 degrees is 1 - 1/sqrt(2)");
        }

        static void TestStatistics(Action<bool, string> assert)
        {
            var r = StoreSummary.Ranks(new double[] { 10, 20, 30 });
            assert(r.Length == 3 && Near(r[0], 1) && Near(r[1], 2) && Near(r[2], 3), "Ranks: distinct values rank 1,2,3");

            var tied = StoreSummary.Ranks(new double[] { 5, 5, 9 });
            assert(Near(tied[0], 1.5) && Near(tied[1], 1.5) && Near(tied[2], 3), "Ranks: a tie shares the average rank (1.5,1.5,3)");

            var unsorted = StoreSummary.Ranks(new double[] { 30, 10, 20 });
            assert(Near(unsorted[0], 3) && Near(unsorted[1], 1) && Near(unsorted[2], 2), "Ranks: ranks follow the value, not the position");

            // Perfect separation: every low value below every high value.
            assert(Near(StoreSummary.Auc(new double[] { 1, 2, 3 }, new double[] { 4, 5, 6 }), 1.0),
                   "AUC: complete separation is 1.0");
            assert(Near(StoreSummary.Auc(new double[] { 4, 5, 6 }, new double[] { 1, 2, 3 }), 0.0),
                   "AUC: complete separation the wrong way round is 0.0");
            assert(Near(StoreSummary.Auc(new double[] { 1, 2, 3 }, new double[] { 1, 2, 3 }), 0.5),
                   "AUC: identical samples carry no information (0.5)");
            assert(Near(StoreSummary.Auc(new double[] { 1, 1 }, new double[] { 1, 1 }), 0.5),
                   "AUC: all ties count half (0.5)");
            assert(double.IsNaN(StoreSummary.Auc(new double[0], new double[] { 1 })),
                   "AUC: an empty side has no separation to report (NaN, never 0)");
            assert(double.IsNaN(StoreSummary.Auc(new double[] { 1 }, new double[0])),
                   "AUC: an empty high side is NaN too");

            // A class of more than 46,340 pairs: n * (n + 1) no longer fits a 32-bit integer. The report samples
            // 200,000 pairs, so any library where more than about 23% of them fall in one class reaches this, and
            // the figure came out below 0.5 for a verdict whose two means were plainly the right way round.
            var big = Enumerable.Repeat(1.0, 50000).ToArray();
            double aucBig = StoreSummary.Auc(big, new double[] { 2.0, 3.0 });
            assert(Near(aucBig, 1.0), $"AUC: 50,000 low values all under the high ones is still 1.0 (got {aucBig})");

            var s = new double[] { 0, 1, 2, 3, 4 };
            assert(Near(StoreSummary.Percentile(s, 0), 0.0), "Percentile: p0 is the smallest value");
            assert(Near(StoreSummary.Percentile(s, 100), 4.0), "Percentile: p100 is the largest value");
            assert(Near(StoreSummary.Percentile(s, 50), 2.0), "Percentile: p50 of 0..4 is 2");
            assert(Near(StoreSummary.Percentile(s, 25), 1.0), "Percentile: p25 of 0..4 is 1");
            assert(Near(StoreSummary.Percentile(new double[] { 0, 10 }, 50), 5.0), "Percentile: p50 interpolates between neighbours");
            assert(Near(StoreSummary.Percentile(new double[] { 7 }, 50), 7.0), "Percentile: a one-value sample is its own percentile");
            assert(double.IsNaN(StoreSummary.Percentile(new double[0], 50)), "Percentile: an empty sample is NaN");

            assert(Near(StoreSummary.Pearson(new double[] { 1, 2, 3, 4 }, new double[] { 2, 4, 6, 8 }), 1.0, 1e-12),
                   "Pearson: a perfect positive relationship is 1.0");
            assert(Near(StoreSummary.Pearson(new double[] { 1, 2, 3, 4 }, new double[] { 8, 6, 4, 2 }), -1.0, 1e-12),
                   "Pearson: a perfect inverse relationship is -1.0");
            assert(Near(StoreSummary.Pearson(new double[] { 1, 2, 3, 4 }, new double[] { 1, 4, 2, 3 }), 0.4, 1e-12),
                   "Pearson: a partial relationship is between (0.4 for this sample)");
            assert(double.IsNaN(StoreSummary.Pearson(new double[] { 5, 5, 5 }, new double[] { 1, 2, 3 })),
                   "Pearson: a constant side has no spread to correlate (NaN, never 0)");
            assert(double.IsNaN(StoreSummary.Pearson(new double[] { 1, 2 }, new double[] { 1, 2, 3 })),
                   "Pearson: mismatched lengths are NaN");
            assert(double.IsNaN(StoreSummary.Pearson(new double[0], new double[0])),
                   "Pearson: an empty sample is NaN");
        }

        // ---------------------------------------------------------------- the report

        static SummaryTrack T(string whole, string head, string tail, int flatAgree, int edmaAgree, string prov)
            => new SummaryTrack
            {
                WholeCode = whole, HeadCode = head, TailCode = tail,
                FlatAgree = flatAgree, FlatPresent = 3, EdmaAgree = edmaAgree, EdmaPresent = 3,
                HeadAgree = 3, HeadPresent = 3, TailAgree = 3, TailPresent = 3, Provenance = prov,
            };

        static void TestReportSections(Action<bool, string> assert)
        {
            // Four tracks, all on the EDMA rung: the two whole-track rows must then be IDENTICAL, and the report must
            // say so rather than claim a difference. (It claimed one unconditionally on the first real run.)
            var sameRung = new List<SummaryTrack>
            {
                T("8B", "8B", "8B", 3, 3, "edma"), T("8B", "9B", "8B", 3, 3, "edma"),
                T("5A", "5A", "12A", 2, 2, "edma"), T("1B", "1B", "1B", 1, 1, "edma"),
            };
            var o = new List<string>();
            StoreSummaryCommand.KeyAgreement(sameRung, sameRung.Count, o);
            var text = string.Join("\n", o);

            assert(o.Any(l => l.Contains("vs the stored key") && l.Contains("3 of 3 50.0%") && l.Contains("2 of 3 25.0%") && l.Contains("1 of 3 25.0%")),
                   "Report: the whole-track buckets are counted as percentages of the rows (2/4, 1/4, 1/4)");
            assert(text.Contains("no row here did, so the two rows above are the same"),
                   "Report: with every key on the EDMA rung the report says the two whole-track rows are the same");
            assert(!text.Contains("which is why the two rows above differ"),
                   "Report: it does NOT claim a difference between the two whole-track rows when there is none");
            assert(text.Contains("never 0 of n"),
                   "Report: it states the asymmetry that an edge key's agreement is never 0 of n");
            assert(o.Any(l => l.Contains("the stored key came from: edma 4")),
                   "Report: it counts which rung supplied the stored key");

            // One track on a fallback rung: now the sentence must flip, because the two counts really can differ.
            var mixed = new List<SummaryTrack>(sameRung) { T("7B", "7B", "7B", 0, 3, "chords") };
            var o2 = new List<string>();
            StoreSummaryCommand.KeyAgreement(mixed, mixed.Count, o2);
            var text2 = string.Join("\n", o2);
            assert(text2.Contains("and 1 here did"), "Report: a fallback-rung row is counted and named");
            assert(text2.Contains("which is why the two rows above differ"),
                   "Report: with a fallback row present it says the two whole-track rows differ");
            assert(o2.Any(l => l.Contains("vs the stored key") && l.Contains("0 of 3 20.0%")),
                   "Report: 0 of 3 appears for the fallback row counted against the stored key");
            assert(o2.Any(l => l.Contains("vs the EDMA vote") && !l.Contains("0 of 3")),
                   "Report: the same row is not 0 of 3 when counted against EDMA — that is the whole point of printing both");

            // A store with no whole-track key at all must say so, not divide by zero.
            var empty = new List<string>();
            StoreSummaryCommand.KeyAgreement(new List<SummaryTrack>(), 0, empty);
            assert(empty.Any(l => l.Contains("not computed")), "Report: no whole-track key is 'not computed', never 0.0%");

            // Regional compatibility over known codes: 8B vs 8B compatible, 8B vs 10B near, 8B vs 11B clash.
            var regional = new List<SummaryTrack>
            {
                T("8B", "8B", "8B", 3, 3, "edma"),      // all three identical
                T("8B", "10B", "8B", 3, 3, "edma"),     // start vs whole near
                T("8B", "11B", "8B", 3, 3, "edma"),     // start vs whole clash
                T("8B", "9B", "8B", 3, 3, "edma"),      // start vs whole compatible (one hour)
            };
            var o3 = new List<string>();
            StoreSummaryCommand.RegionalCompatibility(regional, o3);
            var text3 = string.Join("\n", o3);
            assert(o3.Any(l => l.Contains("all three the same Camelot code: 25.0%")),
                   "Report: exactly one of four tracks has all three regions on one code");
            assert(o3.Any(l => l.Contains("start vs whole") && l.Contains("compatible 50.0%") && l.Contains("near 25.0%") && l.Contains("clash 25.0%")),
                   "Report: the start-vs-whole split is 2 compatible, 1 near, 1 clash");
            assert(o3.Any(l => l.Contains("worst of three") && l.Contains("clash 25.0%")),
                   "Report: the worst-of-three bucket takes the largest distance of the three pairs");

            var o4 = new List<string>();
            StoreSummaryCommand.RegionalCompatibility(new List<SummaryTrack> { new SummaryTrack() }, o4);
            assert(o4.Any(l => l.Contains("not computed")), "Report: a track with no codes leaves regional compatibility 'not computed'");

            // Paste safety: the thing the user is told to copy must never carry a path, a drive or a host.
            foreach (var line in o.Concat(o2).Concat(o3))
            {
                bool clean = !line.Contains("\\") && !line.Contains("//") && !System.Text.RegularExpressions.Regex.IsMatch(line, @"[A-Za-z]:[\\/]");
                assert(clean, $"Report: the pasted block carries no path or drive letter — \"{line}\"");
            }
        }

        static void TestModelScale(Action<bool, string> assert)
        {
            var scale = StoreSummaryCommand.ModelScale;
            assert(scale.Length == 21, $"Model scale: 21 raw measurements carry a fitted scale (got {scale.Length})");

            // Every name must be a field this build knows the precision of, or the raw value cannot be read back
            // from its scaled INTEGER column at all.
            foreach (var f in scale)
            {
                bool known = FieldPrecision.TryGet(f.Name, out _);
                assert(known, $"Model scale: '{f.Name}' is a field FieldPrecision knows (the store holds it scaled)");
            }

            // The 1:1 guard: it must catch mean 0 / std 1 (the model takes the value as measured) and nothing else.
            assert(!StoreSummaryCommand.HasUsableScale(new StoreSummaryCommand.Feat("x", 0.0, 1.0)),
                   "Model scale: mean 0 / std 1 is taken 1:1, no sigma");
            assert(!StoreSummaryCommand.HasUsableScale(new StoreSummaryCommand.Feat("x", 5.0, 0.0)),
                   "Model scale: a zero standard deviation is no reference either");
            assert(StoreSummaryCommand.HasUsableScale(new StoreSummaryCommand.Feat("x", 0.0, 2.0)),
                   "Model scale: a mean of 0 with a real spread IS usable");
            assert(StoreSummaryCommand.HasUsableScale(new StoreSummaryCommand.Feat("x", 124.4, 25.07)),
                   "Model scale: an ordinary fitted scale is usable");

            // The one feature the shipped model takes as measured, named so the output can say so.
            var flat = scale.Single(f => f.Name == "spectralFlatness");
            assert(!StoreSummaryCommand.HasUsableScale(flat),
                   "Model scale: spectralFlatness is taken as measured — the shipped model's scale for it is mean 0 / std 1");
            assert(scale.Count(f => !StoreSummaryCommand.HasUsableScale(f)) == 1,
                   "Model scale: spectralFlatness is the ONLY feature taken as measured");
        }
    }
}
