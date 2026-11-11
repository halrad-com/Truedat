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
        }

        static bool Near(double a, double b, double tol = 1e-9) => Math.Abs(a - b) <= tol;

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

            // The degenerate-scale guard: it must catch mean 0 / std 1 and nothing else.
            assert(!StoreSummaryCommand.HasUsableScale(new StoreSummaryCommand.Feat("x", 0.0, 1.0)),
                   "Model scale: mean 0 / std 1 is no reference to compare against");
            assert(!StoreSummaryCommand.HasUsableScale(new StoreSummaryCommand.Feat("x", 5.0, 0.0)),
                   "Model scale: a zero standard deviation is no reference either");
            assert(StoreSummaryCommand.HasUsableScale(new StoreSummaryCommand.Feat("x", 0.0, 2.0)),
                   "Model scale: a mean of 0 with a real spread IS usable");
            assert(StoreSummaryCommand.HasUsableScale(new StoreSummaryCommand.Feat("x", 124.4, 25.07)),
                   "Model scale: an ordinary fitted scale is usable");

            // The one feature the shipped model cannot be compared on, named so the output can say why.
            var flat = scale.Single(f => f.Name == "spectralFlatness");
            assert(!StoreSummaryCommand.HasUsableScale(flat),
                   "Model scale: spectralFlatness is excluded — the shipped model's scale for it is mean 0 / std 1");
            assert(scale.Count(f => !StoreSummaryCommand.HasUsableScale(f)) == 1,
                   "Model scale: spectralFlatness is the ONLY feature without a usable fitted scale");
        }
    }
}
