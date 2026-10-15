using System;
using System.Collections.Generic;

namespace Truedat
{
    /// <summary>The one place a numeric field's precision is declared (store v3 sprint, T03 / finding F7). The extractor parser
    /// and the catalog writer read their decimal places from here (T03), so a field is rounded the same way wherever it is
    /// rendered and a schema-walk self-test can refuse a numeric column nobody declared. The store mapper and the exporter still
    /// apply CanonicalPrecision.Sig6 until T04 replaces that with types derived from this registry.
    /// Before T03 the ~90 decimal-place literals lived at the call sites (<c>Opt</c>/<c>OptN</c>, the core initializer, the
    /// vector folds, the writer) and nothing asserted they agreed.</summary>
    internal static class FieldPrecision
    {
        internal enum Shape { Int, Fixed, FixedVec, IntVec }

        internal readonly struct Spec
        {
            public readonly Shape Shape; public readonly int Dp;
            public Spec(Shape shape, int dp) { Shape = shape; Dp = dp; }
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
            F(1, "bpm", "spectralCentroid", "spectralRolloff", "bpmFirstPeak", "bpmSecondPeak", "spectralCentroidStdev", "analysisDuration");
            // 2 dp: dB / LU values and the few coarse scalars.
            F(2, "loudness", "onsetRate", "dynamicRange", "loudnessMomentary", "loudnessShortTerm", "replayGain",
                 "spectralComplexity", "hfc", "beatsLoudness", "hpcpCrest", "tuningFrequency", "bitUsage_effectiveBits", "hfSpectralStructure_peakToMean");
            F(3, "smfmBpm");
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
            // spectralFlatness is DERIVED (DeriveSpectralFlatness: the mean of the three band flatnesses, rounded at 4) and its initializer
            // literal was 6 — re-rounding a 4-dp value at 6 is a no-op, so its EFFECTIVE precision is 4 and that is what is declared (review).
            F(4, "spectralFlatness");
            F(6, "zeroCrossingRate", "spectralRms", "spectralEnergy", "spectralEnergyLow",
                 "spectralEnergyMidLow", "spectralEnergyMidHigh", "spectralEnergyHigh", "spectralEnergyStdev", "hfEnergyRatio");
            // 12 dp: spectralDecrease lives at ~1e-9 — 6 dp rounded every track to 0 (CLAUDE.md, 8c06ec2).
            F(12, "spectralDecrease");
            VF(4, "mfcc", "chordsHistogram", "hpcp12", "thpcp12", "mfccStdev",
                  "spectralContrastCoeffs", "spectralContrastValleys", "beatsLoudnessBandRatio", "bpmHistogram", "gfcc");
            VI("smfmScores");
            return t;
        }

        internal static bool TryGet(string name, out Spec spec) => Table.TryGetValue(name, out spec);

        /// <summary>Decimal places for a declared field; a missing name is a programming error, never a default.</summary>
        internal static int Dp(string name) => TryGet(name, out var s) ? s.Dp : throw new InvalidOperationException($"FieldPrecision: no entry for '{name}'");

        internal static IEnumerable<string> Names => Table.Keys;
        internal static int Count => Table.Count;
    }
}
