using System;
using System.Collections.Generic;
using System.Globalization;
using Truedat.Edges;

namespace Truedat.Store
{
    /// <summary>The arithmetic behind <c>--store-summary</c>, kept apart from the store reads so every rule here is
    /// testable without a database. Three of these rules are the HUB's, mirrored from its source and not ours to change
    /// (<see cref="CamelotDistance"/>, <see cref="HarmonicTrust"/>, <see cref="CloserThanPercent"/>); the rest are
    /// this report's own.
    ///
    /// <para>The Camelot table itself is NOT duplicated here — <see cref="CamelotOf"/> calls the producer's own mapping
    /// in <c>EdgeKey</c>, so the report cannot drift from the codes the store was written with. Only the enharmonic
    /// aliases a legacy catalog might carry are normalised first.</para></summary>
    internal static class StoreSummary
    {
        // ---------------------------------------------------------------- the hub's two rules, mirrored

        /// <summary>MBXH/Core/CamelotCode.cs Distance. 0 = compatible, 0.5 = near, 1 = clash. An unparseable code is a
        /// clash, never an exception — this runs over whatever a user's store happens to hold.</summary>
        internal static double CamelotDistance(string? a, string? b)
        {
            if (!TryParseCode(a, out int na, out char la) || !TryParseCode(b, out int nb, out char lb)) return 1.0;
            if (na == nb) return 0.0;                       // same number (incl. same code / letter swap)
            if (la != lb) return 1.0;                       // different number AND different letter = clash
            int hop = Math.Min(Mod(na - nb, 12), Mod(nb - na, 12));
            if (hop == 1) return 0.0;                       // +/-1 hour, same letter
            if (hop == 2) return 0.5;
            return 1.0;
        }

        /// <summary>MBXH/Core/ContinuityScorer.cs HarmonicTrust. Note a 1-of-2 split is NOT a majority.</summary>
        internal static double HarmonicTrust(int agreeing, int present)
        {
            if (present <= 0) return 1.0;                   // no votes carried — unchanged behaviour, not a penalty
            if (present == 1) return agreeing >= 1 ? 0.5 : 0.0;
            if (agreeing >= present) return 1.0;            // unanimous among those carried
            if (agreeing * 2 > present) return 0.5;         // strict majority — a 1-of-2 split is not one
            return 0.0;                                     // the key in use is outvoted or alone
        }

        internal static bool TryParseCode(string? code, out int num, out char letter)
        {
            num = 0; letter = ' ';
            if (string.IsNullOrEmpty(code) || code!.Length < 2) return false;
            letter = char.ToUpperInvariant(code[code.Length - 1]);
            if (letter != 'A' && letter != 'B') return false;
            return int.TryParse(code.Substring(0, code.Length - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out num)
                   && num >= 1 && num <= 12;
        }

        static int Mod(int x, int m) => ((x % m) + m) % m;

        // ---------------------------------------------------------------- keys

        /// <summary>The enharmonic spellings truedat's own tables do not carry, mapped onto the ones they do
        /// (EdgeKey's KeyNames: A Bb B C C# D Eb E F F# G Ab). A legacy catalog or a hand-edited store can hold
        /// D#/G#/A#/Db/Gb; the producer never writes them.</summary>
        static readonly Dictionary<string, string> Enharmonic = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["D#"] = "Eb", ["G#"] = "Ab", ["A#"] = "Bb", ["Db"] = "C#", ["Gb"] = "F#",
            ["Cb"] = "B", ["B#"] = "C", ["E#"] = "F", ["Fb"] = "E",
        };

        internal static string NormalizeKeyName(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "";
            var k = key!.Trim();
            // The producer's tables are case-SENSITIVE (EdgeKey uses StringComparer.Ordinal), and a catalog can hold
            // any casing, so canonicalise to the producer's spelling before looking anything up: "bb" -> "Bb",
            // "f#" -> "F#", "AB" -> "Ab".
            k = char.ToUpperInvariant(k[0]) + k.Substring(1).ToLowerInvariant();
            return Enharmonic.TryGetValue(k, out var mapped) ? mapped : k;
        }

        /// <summary>The Camelot code for a whole-track key/mode pair, via the producer's own table. Null when the pair
        /// names no code (absent, or a scale that is neither major nor minor).</summary>
        internal static string? CamelotOf(string? key, string? mode)
        {
            var scale = NormalizeScale(mode);
            if (scale == null) return null;
            var k = NormalizeKeyName(key);
            if (k.Length == 0) return null;
            return EdgeKey.Camelot(k, scale);
        }

        /// <summary>"major"/"minor" from whatever the catalog holds. The extractor writes the full words, but an older
        /// or hand-edited catalog abbreviates, and the hub's own parse reads a "min" prefix. Anything that is neither
        /// names no Camelot code.</summary>
        internal static string? NormalizeScale(string? mode)
        {
            if (string.IsNullOrWhiteSpace(mode)) return null;
            var m = mode!.Trim().ToLowerInvariant();
            if (m.StartsWith("min", StringComparison.Ordinal)) return "minor";
            if (m.StartsWith("maj", StringComparison.Ordinal)) return "major";
            return null;
        }

        /// <summary>Which rung of the flat-key ladder supplied this row's key and mode.
        /// <c>Program.cs:27861-27870</c> walks <c>key_edma</c> -> <c>key_krumhansl</c> -> <c>chords_key</c>, and key and
        /// scale walk it INDEPENDENTLY, so a row can be "edma/krumhansl". The source is never persisted, so it is
        /// reconstructed by comparing the flat pair against the stored votes.</summary>
        internal static string KeyProvenance(string? flatKey, string? flatMode,
                                             string? edmaKey, string? edmaScale,
                                             string? krumKey, string? krumScale,
                                             string? chordsKey, string? chordsScale)
        {
            var kr = RungOf(flatKey, edmaKey, krumKey, chordsKey);
            var sr = RungOf(flatMode, edmaScale, krumScale, chordsScale);
            if (kr == "missing" && sr == "missing") return "missing";
            return kr == sr ? kr : kr + "/" + sr;
        }

        static string RungOf(string? flat, string? edma, string? krum, string? chords)
        {
            if (string.IsNullOrWhiteSpace(flat)) return "missing";
            if (Same(flat, edma)) return "edma";
            if (Same(flat, krum)) return "krumhansl";
            if (Same(flat, chords)) return "chords";
            return "other";
        }

        static bool Same(string? a, string? b) =>
            !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
            string.Equals(NormalizeKeyName(a), NormalizeKeyName(b), StringComparison.OrdinalIgnoreCase);

        /// <summary>How many of the carried profile votes agree with a named reference pair, and how many were carried.
        /// The reference is the caller's choice and that choice is the whole point: against the FLAT key/mode this is
        /// what the hub acts on (MoodCacheEntry.KeyAgreement); against the EDMA vote it is the same quantity the edge
        /// rows report, so whole and edge can be compared on equal terms.</summary>
        internal static (int Agreeing, int Present) AgreementWith(string? refKey, string? refScale,
                                                                  IEnumerable<(string? Key, string? Scale, bool Carried)> votes)
        {
            int present = 0, agreeing = 0;
            bool haveRef = !string.IsNullOrWhiteSpace(refKey) && !string.IsNullOrWhiteSpace(refScale);
            foreach (var v in votes)
            {
                if (!v.Carried) continue;                   // a profile that did not decide is not a dissenter
                present++;
                if (haveRef && Same(v.Key, refKey) && SameScale(v.Scale, refScale)) agreeing++;
            }
            return (agreeing, present);
        }

        static bool SameScale(string? a, string? b)
        {
            var x = NormalizeScale(a); var y = NormalizeScale(b);
            return x != null && y != null && x == y;
        }

        // ---------------------------------------------------------------- vectors

        /// <summary>The vector scaled to unit length, or null when it has none (a flat or empty chroma correlates with
        /// nothing and must not become a 0-distance match).</summary>
        internal static double[]? Unit(double[]? v)
        {
            if (v == null || v.Length == 0) return null;
            double n = 0; foreach (var x in v) n += x * x;
            n = Math.Sqrt(n);
            if (n <= 0 || double.IsNaN(n) || double.IsInfinity(n)) return null;
            var o = new double[v.Length];
            for (int i = 0; i < v.Length; i++) o[i] = v[i] / n;
            return o;
        }

        /// <summary>1 - cosine over two UNIT vectors, clamped to [0,1]. Chroma is non-negative, so the clamp only
        /// absorbs rounding.</summary>
        internal static double CosineDistance(double[] a, double[] b)
        {
            int n = Math.Min(a.Length, b.Length);
            double c = 0;
            for (int i = 0; i < n; i++) c += a[i] * b[i];
            if (c < 0) c = 0; else if (c > 1) c = 1;
            return 1.0 - c;
        }

        // ---------------------------------------------------------------- statistics

        /// <summary>Average ranks, 1-based, ties shared.</summary>
        internal static double[] Ranks(IReadOnlyList<double> xs)
        {
            int n = xs.Count;
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, (p, q) => xs[p].CompareTo(xs[q]));
            var r = new double[n];
            int s = 0;
            while (s < n)
            {
                int e = s;
                while (e + 1 < n && xs[order[e + 1]] == xs[order[s]]) e++;
                double avg = (s + e) / 2.0 + 1;             // 1-based, ties share the average
                for (int k = s; k <= e; k++) r[order[k]] = avg;
                s = e + 1;
            }
            return r;
        }

        /// <summary>P(a random member of <paramref name="low"/> is smaller than a random member of
        /// <paramref name="high"/>), ties counting half. 0.5 = the split carries no information. NaN when either side is
        /// empty — a library with no clashing pairs has no separation to report, and must say so rather than print 0.</summary>
        internal static double Auc(IReadOnlyList<double> low, IReadOnlyList<double> high)
        {
            if (low.Count == 0 || high.Count == 0) return double.NaN;
            var all = new double[low.Count + high.Count];
            for (int i = 0; i < low.Count; i++) all[i] = low[i];
            for (int i = 0; i < high.Count; i++) all[low.Count + i] = high[i];
            var r = Ranks(all);
            double rLow = 0;
            for (int i = 0; i < low.Count; i++) rLow += r[i];
            // In double from the first factor: as an int product, n * (n + 1) overflows past 46,340 values, and one
            // class of a 200,000-pair sample reaches that on an ordinary library.
            double u = rLow - (double)low.Count * (low.Count + 1) / 2.0;   // Mann-Whitney U: times a low beat a high
            return 1.0 - u / ((double)low.Count * high.Count);
        }

        /// <summary>Linear-interpolated percentile over an ASCENDING-sorted sample. <paramref name="p"/> is 0..100.</summary>
        internal static double Percentile(IReadOnlyList<double> sorted, double p)
        {
            int n = sorted.Count;
            if (n == 0) return double.NaN;
            if (n == 1) return sorted[0];
            double pos = (p / 100.0) * (n - 1);
            if (pos <= 0) return sorted[0];
            if (pos >= n - 1) return sorted[n - 1];
            int lo = (int)Math.Floor(pos);
            double frac = pos - lo;
            return sorted[lo] + frac * (sorted[lo + 1] - sorted[lo]);
        }

        /// <summary>Pearson correlation. NaN when either side is constant (no spread to correlate) or they differ in
        /// length — a library where every pair scores the same has no relationship to report, not a relationship of 0.</summary>
        internal static double Pearson(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
        {
            if (xs.Count == 0 || xs.Count != ys.Count) return double.NaN;
            double mx = Mean(xs), my = Mean(ys), sxy = 0, sxx = 0, syy = 0;
            for (int i = 0; i < xs.Count; i++)
            {
                double dx = xs[i] - mx, dy = ys[i] - my;
                sxy += dx * dy; sxx += dx * dx; syy += dy * dy;
            }
            if (sxx <= 0 || syy <= 0) return double.NaN;
            return sxy / Math.Sqrt(sxx * syy);
        }

        internal static double Mean(IReadOnlyList<double> xs)
        {
            if (xs.Count == 0) return double.NaN;
            double s = 0; foreach (var x in xs) s += x; return s / xs.Count;
        }

        /// <summary>Population standard deviation — divided by n, not n-1. It is printed beside a model's fitted scale,
        /// and that scale was fitted the same way, so the two are comparable and several libraries' n / mean / std can
        /// be pooled into one scale exactly.</summary>
        internal static double Std(IReadOnlyList<double> xs)
        {
            if (xs.Count == 0) return double.NaN;
            double m = Mean(xs), s = 0;
            foreach (var x in xs) { double d = x - m; s += d * d; }
            return Math.Sqrt(s / xs.Count);
        }

        /// <summary>How many values sit strictly past <paramref name="bound"/> fitted standard deviations from the
        /// fitted mean, each way. A value exactly at the bound is not past it. No fitted spread means no tail.</summary>
        internal static (int Under, int Over) TailCounts(IReadOnlyList<double> values, double mean, double std, double bound)
        {
            if (!(std > 0) || double.IsNaN(mean)) return (0, 0);
            double lo = mean - bound * std, hi = mean + bound * std;
            int under = 0, over = 0;
            foreach (var v in values) { if (v < lo) under++; else if (v > hi) over++; }
            return (under, over);
        }

        // ---------------------------------------------------------------- the hub's closeness rank, mirrored

        /// <summary>MBXH/Core/JoinCloseness.cs MinSample and MinTracks: under either, the hub gives no rank at all.</summary>
        internal const int MinClosenessSample = 100;
        internal const int MinClosenessTracks = 100;

        /// <summary>MBXH/Core/JoinCloseness.cs CloserThanPercent: the share of an ASCENDING-sorted sample, 0..100, that
        /// is STRICTLY farther apart than <paramref name="distance"/>. NaN where the hub answers "not known".</summary>
        internal static double CloserThanPercent(double distance, IReadOnlyList<double> sortedSample)
        {
            if (sortedSample == null || sortedSample.Count < MinClosenessSample) return double.NaN;
            if (double.IsNaN(distance) || double.IsInfinity(distance)) return double.NaN;
            int lo = 0, hi = sortedSample.Count;            // first index whose value is strictly greater
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (sortedSample[mid] > distance) hi = mid; else lo = mid + 1;
            }
            return 100.0 * (sortedSample.Count - lo) / sortedSample.Count;
        }
    }
}
