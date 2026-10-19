using System;
using System.Collections.Generic;

namespace Truedat
{
    /// <summary>The one place a numeric field's precision is declared (store v3 sprint, T03 / finding F7). The extractor parser
    /// and the catalog writer read their decimal places from here (T03), so a field is rounded the same way wherever it is
    /// rendered and a schema-walk self-test can refuse a numeric column nobody declared. Since T04 the store mapper derives its
    /// column types and scales from here too (scaled INTEGER at StoreDp, int32 vectors, the sparse histogram); the former
    /// six-significant-figure store rule (CanonicalPrecision, 2026-10-12) is gone.
    /// Before T03 the ~90 decimal-place literals lived at the call sites (<c>Opt</c>/<c>OptN</c>, the core initializer, the
    /// vector folds, the writer) and nothing asserted they agreed.</summary>
    internal static class FieldPrecision
    {
        // SparseFixedVec (T04a): a mostly-zero fixed-dp vector stored self-describing — u16 length, u16 count, then (u16 index, int32 scaled
        // value) per non-zero — reconstructing the full vector exactly; bpmHistogram is ~94 % zeros (1,992 B/track as f64 -> ~100 B).
        internal enum Shape { Int, Fixed, FixedVec, IntVec, SparseFixedVec }

        internal readonly struct Spec
        {
            /// <summary>Dp = the decimal places the catalog WRITER renders (the hub's bytes). StoreDp = the places the STORE holds
            /// (≥ Dp; T04). They differ only for the two writer-side fields a scan sets from an unrounded source: analysisDuration
            /// (raw elapsed seconds) and smfmBpm (a tag's float32). v2 stores hold those at up to six significant figures, and the
            /// operator's rule is that no retained value is lost, so the typed column keeps six places and the writer still rounds
            /// to its own. Every parser field is rounded at Dp on parse, so Dp == StoreDp there.</summary>
            public readonly Shape Shape; public readonly int Dp; public readonly int StoreDp;
            public Spec(Shape shape, int dp, int? storeDp = null) { Shape = shape; Dp = dp; StoreDp = storeDp ?? dp; }
            public override string ToString() => Shape == Shape.Int || Shape == Shape.IntVec ? Shape.ToString() : $"{Shape}({Dp})";
        }

        static readonly Dictionary<string, Spec> Table = Build();

        // Keys are the store column names (TrackEntryMapper) = the catalog's JSON keys with nested blocks flattened by '_'.
        // Values are the decimal places the parser (and, for writer-side fields, the catalog writer) applied before T03, measured
        // 2026-10-15 — the catalog bytes do not change by moving them here. T04 revisits the TYPES (scaled integers, f32
        // vectors); this registry is the single declaration those types will be derived from.
        static Dictionary<string, Spec> Build()
        {
            var t = new Dictionary<string, Spec>(StringComparer.Ordinal);
            void I(params string[] names) { foreach (var n in names) t.Add(n, new Spec(Shape.Int, 0)); }
            void F(int dp, params string[] names) { foreach (var n in names) t.Add(n, new Spec(Shape.Fixed, dp)); }
            void VF(int dp, params string[] names) { foreach (var n in names) t.Add(n, new Spec(Shape.FixedVec, dp)); }
            void VI(params string[] names) { foreach (var n in names) t.Add(n, new Spec(Shape.IntVec, 0)); }

            I("trackId", "bitUsage_lowestNonZeroBit", "bitUsage_samplesAnalyzed", "smfmChannel",
              "fingerprint_v1_fileSize", "fingerprint_v1_durationMs", "fingerprint_v1_sampleRate", "fingerprint_v1_channels",
              "fingerprint_v1_bitDepth", "fingerprint_v1_bitrate", "fingerprint_v1_mp3LameTag_infoTagRevision", "fingerprint_v1_mp3LameTag_vbrMethodCode",
              "fingerprint_v1_mp3LameTag_lowpassHz", "fingerprint_v1_mp3LameTag_encoderDelay", "fingerprint_v1_mp3LameTag_encoderPadding", "fingerprint_v1_mp3LameTag_musicCrc");
            // 1 dp: Hz and bpm-like counts per minute; the frame-stdev of the centroid is Hz too; analysisDuration is the writer's own 1 dp.
            F(1, "bpm", "spectralCentroid", "spectralRolloff", "bpmFirstPeak", "bpmSecondPeak", "spectralCentroidStdev");
            // Writer-side fields a scan sets from an UNROUNDED source: the writer renders 1 / 3 dp, the store holds 6 (see Spec).
            t.Add("analysisDuration", new Spec(Shape.Fixed, 1, storeDp: 6));
            // 2 dp: dB / LU values and the few coarse scalars.
            F(2, "loudness", "onsetRate", "dynamicRange", "loudnessMomentary", "loudnessShortTerm", "replayGain",
                 "spectralComplexity", "hfc", "beatsLoudness", "hpcpCrest", "tuningFrequency", "bitUsage_effectiveBits", "hfSpectralStructure_peakToMean");
            t.Add("smfmBpm", new Spec(Shape.Fixed, 3, storeDp: 6));
            // 4 dp: the default for everything else the parser reads.
            F(4, "spectralFlux", "danceability", "dissonance", "pitchSalience", "chordsChangesRate",
                 "silenceRate20dB", "silenceRate30dB", "silenceRate60dB", "spectralEntropy", "spectralKurtosis", "spectralSkewness", "spectralSpread", "spectralStrongPeak",
                 "barkCrest", "barkFlatness", "barkKurtosis", "barkSkewness", "barkSpread",
                 "erbCrest", "erbFlatness", "erbKurtosis", "erbSkewness", "erbSpread",
                 "melCrest", "melFlatness", "melKurtosis", "melSkewness", "melSpread",
                 "chordsStrength", "hpcpEntropy", "keyVotes_krumhansl_strength", "keyVotes_temperley_strength", "keyVotes_edma_strength",
                 "bpmFirstPeakWeight", "bpmFirstPeakSpread", "bpmSecondPeakWeight", "bpmSecondPeakSpread", "chordsNumberRate",
                 "tuningEqualTemperedDeviation", "tuningDiatonicStrength", "tuningNontemperedEnergyRatio", "averageLoudness", "dynamicComplexity",
                 "beatsIntervalMean", "beatsIntervalStdev", "beatsIntervalMin", "beatsIntervalMax",
                 "dissonanceStdev", "pitchSalienceStdev", "hpcpEntropyStdev", "zeroCrossingRateStdev",
                 "bitUsage_bottomBitActivity", "hfSpectralStructure_flatness", "hfSpectralStructure_imagingSymmetry");
            // 6 dp: tiny spectral energies, rates near zero, the HF energy ratio.
            // spectralFlatness is Essentia's Bark-band flatness (DeriveSpectralFlatness, rounded at 4 — the same 4 dp as barkFlatness, so the
            // store copies one scaled integer into the other exactly); its initializer literal was 6, a no-op on a 4-dp value, so 4 is declared.
            F(4, "spectralFlatness");
            F(6, "zeroCrossingRate", "spectralRms", "spectralEnergy", "spectralEnergyLow",
                 "spectralEnergyMidLow", "spectralEnergyMidHigh", "spectralEnergyHigh", "spectralEnergyStdev", "hfEnergyRatio");
            // 12 dp: spectralDecrease lives at ~1e-9 — 6 dp rounded every track to 0 (CLAUDE.md, 8c06ec2).
            F(12, "spectralDecrease");
            VF(4, "mfcc", "chordsHistogram", "hpcp12", "thpcp12", "mfccStdev",
                  "spectralContrastCoeffs", "spectralContrastValleys", "beatsLoudnessBandRatio", "gfcc");
            t.Add("bpmHistogram", new Spec(Shape.SparseFixedVec, 4));   // T04a: ~94 % zeros — sparse in the store, the same 4 dp everywhere else
            VI("smfmScores");
            return t;
        }

        /// <summary>10^dp as a long, for the store's scaled-integer representation of a Fixed / FixedVec / SparseFixedVec field (T04);
        /// 1 for Int / IntVec. A value v is stored as round(v × Scale) and read back as stored / Scale — the same double
        /// Math.Round(v, dp) produced, because .NET's Math.Round(double, digits) is itself round(v × 10^dp) / 10^dp.</summary>
        internal static long Scale(string name)
        {
            var s = TryGet(name, out var spec) ? spec : throw new InvalidOperationException($"FieldPrecision: no entry for '{name}'");
            long p = 1; for (int i = 0; i < s.StoreDp; i++) p *= 10;
            return p;
        }

        /// <summary>The decimal places the STORE holds (≥ the writer's Dp; equal for every parser field).</summary>
        internal static int StoreDp(string name) => TryGet(name, out var s) ? s.StoreDp : throw new InvalidOperationException($"FieldPrecision: no entry for '{name}'");

        internal static Shape ShapeOf(string name) => TryGet(name, out var spec) ? spec.Shape : throw new InvalidOperationException($"FieldPrecision: no entry for '{name}'");

        internal static bool TryGet(string name, out Spec spec) => Table.TryGetValue(name, out spec);

        /// <summary>Decimal places for a declared field; a missing name is a programming error, never a default.</summary>
        internal static int Dp(string name) => TryGet(name, out var s) ? s.Dp : throw new InvalidOperationException($"FieldPrecision: no entry for '{name}'");

        internal static IEnumerable<string> Names => Table.Keys;
        internal static int Count => Table.Count;
    }
}
