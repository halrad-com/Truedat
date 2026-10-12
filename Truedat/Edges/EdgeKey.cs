using System;
using System.Collections.Generic;

namespace Truedat.Edges
{
    internal enum KeyProfile { Krumhansl, Temperley, Edma }

    internal sealed class KeyEstimate
    {
        public string Key = ""; public string Scale = ""; public double Strength; public double FirstToSecondRelativeStrength;
    }

    /// <summary>The per-edge key vote the hub reads: EDMA's answer (the same profile the whole-track flat key/mode use),
    /// its strength, how many of the three profiles agree with it, and how many decided at all.</summary>
    internal sealed class EdgeKeyVote
    {
        public string Name = ""; public string Code = ""; public string Scale = ""; public double Strength; public int Agreement; public int VotesPresent;
    }

    /// <summary>S2 C4 — a key estimate over a window's HPCP, ported from the extractor's Key algorithm
    /// (<c>essentia-src/src/algorithms/tonal/key.cpp</c>) with the exact parameters the music extractor votes with
    /// (<c>MusicTonalDescriptors.cpp</c>: numHarmonics 4, slope 0.6, usePolyphony, useThreeChords, pcpSize 36; profiles
    /// krumhansl, temperley, edma). truedat's KeyVote only PARSES the extractor's whole-track tonal.key_*; this is the
    /// estimator a 30 s edge needs. Its calibration test is that over the whole-track tonal.hpcp.mean it reproduces the
    /// extractor's own tonal.key_* where the three profiles agree. Quirks of the original (integer division in the key
    /// index, the ibefore &lt; iafter wrap rule in the harmonic spread, minor winning ties) are kept on purpose.</summary>
    internal static class EdgeKey
    {
        const int NumHarmonics = 4;
        const double Slope = 0.6;

        // The extractor's spelling; bin 0 = A.
        static readonly string[] KeyNames = { "A", "Bb", "B", "C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab" };

        // key.cpp profileTypes / profileTypesWithOther (major, minor).
        static readonly double[] KrumhanslM = { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
        static readonly double[] Krumhanslm = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };
        static readonly double[] TemperleyM = { 5.0, 2.0, 3.5, 2.0, 4.5, 4.0, 2.0, 4.5, 2.0, 3.5, 1.5, 4.0 };
        static readonly double[] Temperleym = { 5.0, 2.0, 3.5, 4.5, 2.0, 4.0, 2.0, 4.5, 3.5, 2.0, 1.5, 4.0 };
        static readonly double[] EdmaM = { 1.00, 0.29, 0.50, 0.40, 0.60, 0.56, 0.32, 0.80, 0.31, 0.45, 0.42, 0.39 };
        static readonly double[] Edmam = { 1.00, 0.31, 0.44, 0.58, 0.33, 0.49, 0.29, 0.78, 0.43, 0.29, 0.53, 0.32 };

        sealed class Profile { public double[] M = Array.Empty<double>(), m = Array.Empty<double>(); public double MeanM, Meanm, StdM, Stdm; public int Size; }
        static readonly Dictionary<(KeyProfile, int), Profile> Cache = new Dictionary<(KeyProfile, int), Profile>();

        internal static KeyEstimate? Estimate(double[] hpcp, KeyProfile profile)
        {
            if (hpcp == null || hpcp.Length < 12 || hpcp.Length % 12 != 0) return null;
            int size = hpcp.Length;
            var p = ProfileFor(profile, size);

            double meanPcp = 0; foreach (var v in hpcp) meanPcp += v; meanPcp /= size;
            double stdPcp = 0; foreach (var v in hpcp) stdPcp += (v - meanPcp) * (v - meanPcp); stdPcp = Math.Sqrt(stdPcp);
            if (stdPcp == 0 || double.IsNaN(stdPcp)) return null;   // a flat chroma correlates with nothing: no key, never a guess

            double maxMajor = -1, max2Major = -1, maxMinor = -1, max2Minor = -1; int idxMajor = -1, idxMinor = -1;
            for (int shift = 0; shift < size; shift++)
            {
                double cM = Correlation(hpcp, meanPcp, stdPcp, p.M, p.MeanM, p.StdM, shift);
                if (cM > maxMajor) { max2Major = maxMajor; maxMajor = cM; idxMajor = shift; }
                double cm = Correlation(hpcp, meanPcp, stdPcp, p.m, p.Meanm, p.Stdm, shift);
                if (cm > maxMinor) { max2Minor = maxMinor; maxMinor = cm; idxMinor = shift; }
            }
            int keyIndex; string scale; double max, max2;
            if (maxMajor > maxMinor) { keyIndex = (int)((long)idxMajor * 12 / size); scale = "major"; max = maxMajor; max2 = max2Major; }
            else { keyIndex = (int)((long)idxMinor * 12 / size); scale = "minor"; max = maxMinor; max2 = max2Minor; }
            if (keyIndex < 0 || keyIndex > 11) return null;
            return new KeyEstimate { Key = KeyNames[keyIndex], Scale = scale, Strength = max, FirstToSecondRelativeStrength = max != 0 ? (max - max2) / max : 0 };
        }

        /// <summary>EDMA's answer with its Camelot code, how many of the three profiles agree with it (1..3) and how many
        /// decided. Null when EDMA declines (flat chroma, bad size) or its winning correlation is under <paramref name="minStrength"/>
        /// — the hub reads null as "skip the harmonic term", never as a weak guess.</summary>
        internal static EdgeKeyVote? Vote(double[] hpcp, double minStrength = 0.4)
        {
            var e = Estimate(hpcp, KeyProfile.Edma);
            var k = Estimate(hpcp, KeyProfile.Krumhansl);
            var t = Estimate(hpcp, KeyProfile.Temperley);
            if (e == null || e.Strength < minStrength) return null;
            int present = 1 + (k != null ? 1 : 0) + (t != null ? 1 : 0);
            int agree = 1 + (Agrees(k, e) ? 1 : 0) + (Agrees(t, e) ? 1 : 0);
            return new EdgeKeyVote { Name = e.Key, Scale = e.Scale, Code = Camelot(e.Key, e.Scale) ?? "", Strength = e.Strength, Agreement = agree, VotesPresent = present };
        }
        static bool Agrees(KeyEstimate? a, KeyEstimate b) => a != null && a.Key == b.Key && a.Scale == b.Scale;

        // Camelot wheel in the hub's spelling: minor = A, major = B.
        static readonly Dictionary<string, string> CamelotMinor = new Dictionary<string, string>(StringComparer.Ordinal)
        { ["Ab"] = "1A", ["Eb"] = "2A", ["Bb"] = "3A", ["F"] = "4A", ["C"] = "5A", ["G"] = "6A", ["D"] = "7A", ["A"] = "8A", ["E"] = "9A", ["B"] = "10A", ["F#"] = "11A", ["C#"] = "12A" };
        static readonly Dictionary<string, string> CamelotMajor = new Dictionary<string, string>(StringComparer.Ordinal)
        { ["B"] = "1B", ["F#"] = "2B", ["C#"] = "3B", ["Ab"] = "4B", ["Eb"] = "5B", ["Bb"] = "6B", ["F"] = "7B", ["C"] = "8B", ["G"] = "9B", ["D"] = "10B", ["A"] = "11B", ["E"] = "12B" };

        internal static string? Camelot(string key, string scale)
        {
            if (scale == "minor") return CamelotMinor.TryGetValue(key, out var a) ? a : null;
            if (scale == "major") return CamelotMajor.TryGetValue(key, out var b) ? b : null;
            return null;
        }

        // ---- Key::configure: polyphonic three-chord profiles, interpolated to pcpSize (key.cpp 176-290, 447-500)
        static Profile ProfileFor(KeyProfile which, int size)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue((which, size), out var cached)) return cached;
                double[] M0, m0;
                switch (which)
                {
                    case KeyProfile.Krumhansl: M0 = KrumhanslM; m0 = Krumhanslm; break;
                    case KeyProfile.Temperley: M0 = TemperleyM; m0 = Temperleym; break;
                    default: M0 = EdmaM; m0 = Edmam; break;
                }
                var Mch = new double[12]; var mch = new double[12];
                // MAJOR: tonic (I), subdominant (IV), dominant (V) — useThreeChords skips II, III, VI, VII
                AddMajorTriad(0, M0[0], Mch); AddMajorTriad(5, M0[5], Mch); AddMajorTriad(7, M0[7], Mch);
                // MINOR: tonic (i), subdominant (iv), dominant (V, harmonic minor)
                AddMinorTriad(0, m0[0], mch); AddMinorTriad(5, m0[5], mch); AddMajorTriad(7, m0[7], mch);
                var p = new Profile { Size = size };
                p.M = Resize(Mch, size); p.m = Resize(mch, size);
                (p.MeanM, p.StdM) = MeanStd(p.M); (p.Meanm, p.Stdm) = MeanStd(p.m);
                Cache[(which, size)] = p;
                return p;
            }
        }

        static double[] Resize(double[] prof, int size)
        {
            int n = size / 12;
            var r = new double[size];
            for (int i = 0; i < 12; i++)
            {
                r[i * n] = prof[i];
                double incr = (i == 11 ? prof[11] - prof[0] : prof[i] - prof[i + 1]) / n;
                for (int j = 1; j <= n - 1; j++) r[i * n + j] = prof[i] - j * incr;
            }
            return r;
        }

        static (double Mean, double Std) MeanStd(double[] v)
        {
            double mean = 0; foreach (var x in v) mean += x; mean /= v.Length;
            double std = 0; foreach (var x in v) std += (x - mean) * (x - mean);
            return (mean, Math.Sqrt(std));
        }

        static double Correlation(double[] v1, double mean1, double std1, double[] v2, double mean2, double std2, int shift)
        {
            if (std1 == 0 || std2 == 0) return 0;
            double r = 0; int size = v1.Length;
            for (int i = 0; i < size; i++)
            {
                int index = (i - shift) % size; if (index < 0) index += size;
                r += (v1[i] - mean1) * (v2[index] - mean2);
            }
            return r / (std1 * std2);
        }

        static void AddContributionHarmonics(int pitchClass, double contribution, double[] chords)
        {
            double weight = contribution;
            for (int h = 1; h <= NumHarmonics; h++)
            {
                double index = pitchClass + 12 * Math.Log(h, 2);
                double before = Math.Floor(index), after = Math.Ceiling(index);
                int ibefore = (int)(before % 12.0), iafter = (int)(after % 12.0);
                if (ibefore < iafter)
                {
                    double db = index - before; chords[ibefore] += Math.Pow(Math.Cos(0.5 * Math.PI * db), 2) * weight;
                    double da = after - index; chords[iafter] += Math.Pow(Math.Cos(0.5 * Math.PI * da), 2) * weight;
                }
                else chords[ibefore] += weight;
                weight *= Slope;
            }
        }
        static void AddMajorTriad(int root, double c, double[] chords)
        {
            AddContributionHarmonics(root, c, chords);
            AddContributionHarmonics((root + 4) % 12, c, chords);
            AddContributionHarmonics((root + 7) % 12, c, chords);
        }
        static void AddMinorTriad(int root, double c, double[] chords)
        {
            AddContributionHarmonics(root, c, chords);
            AddContributionHarmonics((root + 3) % 12, c, chords);
            AddContributionHarmonics((root + 7) % 12, c, chords);
        }
    }
}
