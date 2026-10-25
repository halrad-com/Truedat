using System;
using System.Collections.Generic;
using System.Text;

namespace Truedat.Store
{
    /// <summary>TrackEntry ↔ track_feature row. THE ONLY code that knows the column list: the C2 drift
    /// test pins the DDL to the writer's keys, the C4 test pins this list to the DDL, and the
    /// round-trip test pins both directions to the writer's bytes. Vectors are float64 LE blobs
    /// (exactness first — mfcc is written at full double precision); smfmScores int32 LE.</summary>
    internal static class TrackEntryMapper
    {
        enum Kind { Real, Int, Text, VecF64, VecI32 }

        sealed class Col
        {
            public readonly string Name; public readonly Kind Kind;
            public readonly Func<TrackEntry, object?> Get; public readonly Action<TrackEntry, object> Set;
            public Col(string name, Kind kind, Func<TrackEntry, object?> get, Action<TrackEntry, object> set) { Name = name; Kind = kind; Get = get; Set = set; }
        }

        // ---- constructors: Get returns null when the value (or its block) is absent; Set receives a non-null boxed value.
        static Col R(string n, Func<TrackEntry, double?> g, Action<TrackEntry, double> s) => new Col(n, Kind.Real, e => g(e), (e, v) => s(e, (double)v));
        static Col I(string n, Func<TrackEntry, long?> g, Action<TrackEntry, long> s) => new Col(n, Kind.Int, e => g(e), (e, v) => s(e, (long)v));
        static Col T(string n, Func<TrackEntry, string?> g, Action<TrackEntry, string> s) => new Col(n, Kind.Text, e => g(e), (e, v) => s(e, (string)v));
        static Col VF(string n, Func<TrackEntry, double[]?> g, Action<TrackEntry, double[]> s) => new Col(n, Kind.VecF64, e => g(e), (e, v) => s(e, (double[])v));
        static Col VI(string n, Func<TrackEntry, int[]?> g, Action<TrackEntry, int[]> s) => new Col(n, Kind.VecI32, e => g(e), (e, v) => s(e, (int[])v));

        // ---- nested blocks, created on first write so an absent block reads back as null, not empty.
        static TrackFeatures F(TrackEntry e) => e.Features ??= new TrackFeatures();
        static KeyVote KvK(TrackEntry e) => F(e).KeyVoteKrumhansl ??= new KeyVote();
        static KeyVote KvT(TrackEntry e) => F(e).KeyVoteTemperley ??= new KeyVote();
        static KeyVote KvE(TrackEntry e) => F(e).KeyVoteEdma ??= new KeyVote();
        static BitUsageSummary Bu(TrackEntry e) => F(e).BitUsage ??= new BitUsageSummary();
        static HfSpectralStructure Hf(TrackEntry e) => F(e).HfSpectralStructure ??= new HfSpectralStructure();
        static Program.FingerprintV1 Fp(TrackEntry e) => e.FingerprintV1 ??= new Program.FingerprintV1();

        static readonly Col[] Cols =
        {
            I("trackId", e => F(e).TrackId, (e, v) => F(e).TrackId = (int)v),
            T("artist", e => F(e).Artist, (e, v) => F(e).Artist = v),
            T("title", e => F(e).Title, (e, v) => F(e).Title = v),
            T("album", e => F(e).Album, (e, v) => F(e).Album = v),
            T("genre", e => F(e).Genre, (e, v) => F(e).Genre = v),
            R("bpm", e => F(e).Bpm, (e, v) => F(e).Bpm = v),
            T("key", e => F(e).Key, (e, v) => F(e).Key = v),
            T("mode", e => F(e).Mode, (e, v) => F(e).Mode = v),
            R("spectralCentroid", e => F(e).SpectralCentroid, (e, v) => F(e).SpectralCentroid = v),
            R("spectralFlux", e => F(e).SpectralFlux, (e, v) => F(e).SpectralFlux = v),
            R("loudness", e => F(e).Loudness, (e, v) => F(e).Loudness = v),
            R("danceability", e => F(e).Danceability, (e, v) => F(e).Danceability = v),
            R("onsetRate", e => F(e).OnsetRate, (e, v) => F(e).OnsetRate = v),
            R("zeroCrossingRate", e => F(e).ZeroCrossingRate, (e, v) => F(e).ZeroCrossingRate = v),
            R("spectralRms", e => F(e).SpectralRms, (e, v) => F(e).SpectralRms = v),
            R("spectralFlatness", e => F(e).SpectralFlatness, (e, v) => F(e).SpectralFlatness = v),
            R("dissonance", e => F(e).Dissonance, (e, v) => F(e).Dissonance = v),
            R("pitchSalience", e => F(e).PitchSalience, (e, v) => F(e).PitchSalience = v),
            R("chordsChangesRate", e => F(e).ChordsChangesRate, (e, v) => F(e).ChordsChangesRate = v),
            VF("mfcc", e => F(e).Mfcc, (e, v) => F(e).Mfcc = v),
            R("dynamicRange", e => F(e).DynamicRange, (e, v) => F(e).DynamicRange = v),
            T("dynamicRangeSource", e => F(e).DynamicRangeSource, (e, v) => F(e).DynamicRangeSource = v),
            R("loudnessMomentary", e => F(e).LoudnessMomentary, (e, v) => F(e).LoudnessMomentary = v),
            R("loudnessShortTerm", e => F(e).LoudnessShortTerm, (e, v) => F(e).LoudnessShortTerm = v),
            R("replayGain", e => F(e).ReplayGain, (e, v) => F(e).ReplayGain = v),
            R("silenceRate20dB", e => F(e).SilenceRate20dB, (e, v) => F(e).SilenceRate20dB = v),
            R("silenceRate30dB", e => F(e).SilenceRate30dB, (e, v) => F(e).SilenceRate30dB = v),
            R("silenceRate60dB", e => F(e).SilenceRate60dB, (e, v) => F(e).SilenceRate60dB = v),
            R("spectralRolloff", e => F(e).SpectralRolloff, (e, v) => F(e).SpectralRolloff = v),
            R("spectralComplexity", e => F(e).SpectralComplexity, (e, v) => F(e).SpectralComplexity = v),
            R("spectralEntropy", e => F(e).SpectralEntropy, (e, v) => F(e).SpectralEntropy = v),
            R("spectralKurtosis", e => F(e).SpectralKurtosis, (e, v) => F(e).SpectralKurtosis = v),
            R("spectralSkewness", e => F(e).SpectralSkewness, (e, v) => F(e).SpectralSkewness = v),
            R("spectralSpread", e => F(e).SpectralSpread, (e, v) => F(e).SpectralSpread = v),
            R("spectralStrongPeak", e => F(e).SpectralStrongPeak, (e, v) => F(e).SpectralStrongPeak = v),
            R("spectralDecrease", e => F(e).SpectralDecrease, (e, v) => F(e).SpectralDecrease = v),
            R("spectralEnergy", e => F(e).SpectralEnergy, (e, v) => F(e).SpectralEnergy = v),
            R("spectralEnergyLow", e => F(e).SpectralEnergyLow, (e, v) => F(e).SpectralEnergyLow = v),
            R("spectralEnergyMidLow", e => F(e).SpectralEnergyMidLow, (e, v) => F(e).SpectralEnergyMidLow = v),
            R("spectralEnergyMidHigh", e => F(e).SpectralEnergyMidHigh, (e, v) => F(e).SpectralEnergyMidHigh = v),
            R("spectralEnergyHigh", e => F(e).SpectralEnergyHigh, (e, v) => F(e).SpectralEnergyHigh = v),
            R("hfc", e => F(e).Hfc, (e, v) => F(e).Hfc = v),
            R("barkCrest", e => F(e).BarkCrest, (e, v) => F(e).BarkCrest = v),
            R("barkFlatness", e => F(e).BarkFlatness, (e, v) => F(e).BarkFlatness = v),
            R("barkKurtosis", e => F(e).BarkKurtosis, (e, v) => F(e).BarkKurtosis = v),
            R("barkSkewness", e => F(e).BarkSkewness, (e, v) => F(e).BarkSkewness = v),
            R("barkSpread", e => F(e).BarkSpread, (e, v) => F(e).BarkSpread = v),
            R("erbCrest", e => F(e).ErbCrest, (e, v) => F(e).ErbCrest = v),
            R("erbFlatness", e => F(e).ErbFlatness, (e, v) => F(e).ErbFlatness = v),
            R("erbKurtosis", e => F(e).ErbKurtosis, (e, v) => F(e).ErbKurtosis = v),
            R("erbSkewness", e => F(e).ErbSkewness, (e, v) => F(e).ErbSkewness = v),
            R("erbSpread", e => F(e).ErbSpread, (e, v) => F(e).ErbSpread = v),
            R("melCrest", e => F(e).MelCrest, (e, v) => F(e).MelCrest = v),
            R("melFlatness", e => F(e).MelFlatness, (e, v) => F(e).MelFlatness = v),
            R("melKurtosis", e => F(e).MelKurtosis, (e, v) => F(e).MelKurtosis = v),
            R("melSkewness", e => F(e).MelSkewness, (e, v) => F(e).MelSkewness = v),
            R("melSpread", e => F(e).MelSpread, (e, v) => F(e).MelSpread = v),
            R("beatsLoudness", e => F(e).BeatsLoudness, (e, v) => F(e).BeatsLoudness = v),
            R("chordsStrength", e => F(e).ChordsStrength, (e, v) => F(e).ChordsStrength = v),
            R("hpcpCrest", e => F(e).HpcpCrest, (e, v) => F(e).HpcpCrest = v),
            R("hpcpEntropy", e => F(e).HpcpEntropy, (e, v) => F(e).HpcpEntropy = v),
            T("keyVotes_krumhansl_key", e => F(e).KeyVoteKrumhansl?.Key, (e, v) => KvK(e).Key = v),
            T("keyVotes_krumhansl_scale", e => F(e).KeyVoteKrumhansl?.Scale, (e, v) => KvK(e).Scale = v),
            R("keyVotes_krumhansl_strength", e => F(e).KeyVoteKrumhansl?.Strength, (e, v) => KvK(e).Strength = v),
            T("keyVotes_temperley_key", e => F(e).KeyVoteTemperley?.Key, (e, v) => KvT(e).Key = v),
            T("keyVotes_temperley_scale", e => F(e).KeyVoteTemperley?.Scale, (e, v) => KvT(e).Scale = v),
            R("keyVotes_temperley_strength", e => F(e).KeyVoteTemperley?.Strength, (e, v) => KvT(e).Strength = v),
            T("keyVotes_edma_key", e => F(e).KeyVoteEdma?.Key, (e, v) => KvE(e).Key = v),
            T("keyVotes_edma_scale", e => F(e).KeyVoteEdma?.Scale, (e, v) => KvE(e).Scale = v),
            R("keyVotes_edma_strength", e => F(e).KeyVoteEdma?.Strength, (e, v) => KvE(e).Strength = v),
            R("bpmFirstPeak", e => F(e).BpmFirstPeak, (e, v) => F(e).BpmFirstPeak = v),
            R("bpmFirstPeakWeight", e => F(e).BpmFirstPeakWeight, (e, v) => F(e).BpmFirstPeakWeight = v),
            R("bpmFirstPeakSpread", e => F(e).BpmFirstPeakSpread, (e, v) => F(e).BpmFirstPeakSpread = v),
            R("bpmSecondPeak", e => F(e).BpmSecondPeak, (e, v) => F(e).BpmSecondPeak = v),
            R("bpmSecondPeakWeight", e => F(e).BpmSecondPeakWeight, (e, v) => F(e).BpmSecondPeakWeight = v),
            R("bpmSecondPeakSpread", e => F(e).BpmSecondPeakSpread, (e, v) => F(e).BpmSecondPeakSpread = v),
            T("chordsKey", e => F(e).ChordsKey, (e, v) => F(e).ChordsKey = v),
            T("chordsScale", e => F(e).ChordsScale, (e, v) => F(e).ChordsScale = v),
            VF("chordsHistogram", e => F(e).ChordsHistogram, (e, v) => F(e).ChordsHistogram = v),
            R("chordsNumberRate", e => F(e).ChordsNumberRate, (e, v) => F(e).ChordsNumberRate = v),
            R("tuningFrequency", e => F(e).TuningFrequency, (e, v) => F(e).TuningFrequency = v),
            R("tuningEqualTemperedDeviation", e => F(e).TuningEqualTemperedDeviation, (e, v) => F(e).TuningEqualTemperedDeviation = v),
            R("tuningDiatonicStrength", e => F(e).TuningDiatonicStrength, (e, v) => F(e).TuningDiatonicStrength = v),
            R("tuningNontemperedEnergyRatio", e => F(e).TuningNontemperedEnergyRatio, (e, v) => F(e).TuningNontemperedEnergyRatio = v),
            R("averageLoudness", e => F(e).AverageLoudness, (e, v) => F(e).AverageLoudness = v),
            VF("hpcp12", e => F(e).Hpcp12, (e, v) => F(e).Hpcp12 = v),
            R("dynamicComplexity", e => F(e).DynamicComplexity, (e, v) => F(e).DynamicComplexity = v),
            VF("thpcp12", e => F(e).Thpcp12, (e, v) => F(e).Thpcp12 = v),
            R("beatsIntervalMean", e => F(e).BeatsIntervalMean, (e, v) => F(e).BeatsIntervalMean = v),
            R("beatsIntervalStdev", e => F(e).BeatsIntervalStdev, (e, v) => F(e).BeatsIntervalStdev = v),
            R("beatsIntervalMin", e => F(e).BeatsIntervalMin, (e, v) => F(e).BeatsIntervalMin = v),
            R("beatsIntervalMax", e => F(e).BeatsIntervalMax, (e, v) => F(e).BeatsIntervalMax = v),
            R("spectralCentroidStdev", e => F(e).SpectralCentroidStdev, (e, v) => F(e).SpectralCentroidStdev = v),
            R("spectralEnergyStdev", e => F(e).SpectralEnergyStdev, (e, v) => F(e).SpectralEnergyStdev = v),
            R("dissonanceStdev", e => F(e).DissonanceStdev, (e, v) => F(e).DissonanceStdev = v),
            R("pitchSalienceStdev", e => F(e).PitchSalienceStdev, (e, v) => F(e).PitchSalienceStdev = v),
            R("hpcpEntropyStdev", e => F(e).HpcpEntropyStdev, (e, v) => F(e).HpcpEntropyStdev = v),
            R("zeroCrossingRateStdev", e => F(e).ZeroCrossingRateStdev, (e, v) => F(e).ZeroCrossingRateStdev = v),
            VF("mfccStdev", e => F(e).MfccStdev, (e, v) => F(e).MfccStdev = v),
            VF("spectralContrastCoeffs", e => F(e).SpectralContrastCoeffs, (e, v) => F(e).SpectralContrastCoeffs = v),
            VF("spectralContrastValleys", e => F(e).SpectralContrastValleys, (e, v) => F(e).SpectralContrastValleys = v),
            VF("beatsLoudnessBandRatio", e => F(e).BeatsLoudnessBandRatio, (e, v) => F(e).BeatsLoudnessBandRatio = v),
            VF("bpmHistogram", e => F(e).BpmHistogram, (e, v) => F(e).BpmHistogram = v),
            VF("gfcc", e => F(e).Gfcc, (e, v) => F(e).Gfcc = v),
            I("bitUsage_lowestNonZeroBit", e => F(e).BitUsage?.LowestNonZeroBit, (e, v) => Bu(e).LowestNonZeroBit = (int)v),
            R("bitUsage_bottomBitActivity", e => F(e).BitUsage?.BottomBitActivity, (e, v) => Bu(e).BottomBitActivity = v),
            R("bitUsage_effectiveBits", e => F(e).BitUsage?.EffectiveBits, (e, v) => Bu(e).EffectiveBits = v),
            I("bitUsage_samplesAnalyzed", e => F(e).BitUsage?.SamplesAnalyzed, (e, v) => Bu(e).SamplesAnalyzed = (int)v),
            T("bitUsage_method", e => F(e).BitUsage?.Method, (e, v) => Bu(e).Method = v),
            R("hfEnergyRatio", e => F(e).HfEnergyRatio, (e, v) => F(e).HfEnergyRatio = v),
            T("hfEnergyMethod", e => F(e).HfEnergyMethod, (e, v) => F(e).HfEnergyMethod = v),
            R("hfSpectralStructure_flatness", e => F(e).HfSpectralStructure?.Flatness, (e, v) => Hf(e).Flatness = v),
            R("hfSpectralStructure_peakToMean", e => F(e).HfSpectralStructure?.PeakToMean, (e, v) => Hf(e).PeakToMean = v),
            R("hfSpectralStructure_imagingSymmetry", e => F(e).HfSpectralStructure?.ImagingSymmetry, (e, v) => Hf(e).ImagingSymmetry = v),
            T("hfSpectralStructure_method", e => F(e).HfSpectralStructure?.Method, (e, v) => Hf(e).Method = v),
            VI("smfmScores", e => F(e).SmfmScores, (e, v) => F(e).SmfmScores = v),
            I("smfmChannel", e => F(e).SmfmChannel, (e, v) => F(e).SmfmChannel = (int)v),
            T("smfmChannelName", e => F(e).SmfmChannelName, (e, v) => F(e).SmfmChannelName = v),
            R("smfmBpm", e => F(e).SmfmBpm, (e, v) => F(e).SmfmBpm = v),
            R("analysisDuration", e => e.AnalysisDurationSecs, (e, v) => e.AnalysisDurationSecs = v),
            T("fileMd5", e => e.FileMd5, (e, v) => e.FileMd5 = v),
            T("audioStreamSha256", e => e.AudioStreamSha256, (e, v) => e.AudioStreamSha256 = v),
            T("audioStreamSha256Source", e => e.AudioStreamSha256Source, (e, v) => e.AudioStreamSha256Source = v),
            I("fingerprint_v1_fileSize", e => e.FingerprintV1?.FileSize, (e, v) => Fp(e).FileSize = v),
            T("fingerprint_v1_pathTail", e => e.FingerprintV1?.PathTail, (e, v) => Fp(e).PathTail = v),
            I("fingerprint_v1_durationMs", e => e.FingerprintV1?.DurationMs, (e, v) => Fp(e).DurationMs = (int)v),
            I("fingerprint_v1_sampleRate", e => e.FingerprintV1?.SampleRate, (e, v) => Fp(e).SampleRate = (int)v),
            I("fingerprint_v1_channels", e => e.FingerprintV1?.Channels, (e, v) => Fp(e).Channels = (int)v),
            I("fingerprint_v1_bitDepth", e => e.FingerprintV1?.BitDepth, (e, v) => Fp(e).BitDepth = (int)v),
            T("fingerprint_v1_codec", e => e.FingerprintV1?.Codec, (e, v) => Fp(e).Codec = v),
            T("fingerprint_v1_codecRaw", e => e.FingerprintV1?.CodecRaw, (e, v) => Fp(e).CodecRaw = v),
            I("fingerprint_v1_bitrate", e => e.FingerprintV1?.Bitrate, (e, v) => Fp(e).Bitrate = (int)v),
            T("fingerprint_v1_encoder", e => e.FingerprintV1?.Encoder, (e, v) => Fp(e).Encoder = v),
            T("fingerprint_v1_encoderRaw", e => e.FingerprintV1?.EncoderRaw, (e, v) => Fp(e).EncoderRaw = v),
            T("fingerprint_v1_mp3LameTag_version", e => e.FingerprintV1?.Mp3LameVersion, (e, v) => Fp(e).Mp3LameVersion = v),
            I("fingerprint_v1_mp3LameTag_infoTagRevision", e => e.FingerprintV1?.Mp3InfoTagRevision, (e, v) => Fp(e).Mp3InfoTagRevision = (byte)v),
            I("fingerprint_v1_mp3LameTag_vbrMethodCode", e => e.FingerprintV1?.Mp3VbrMethodCode, (e, v) => Fp(e).Mp3VbrMethodCode = (byte)v),
            T("fingerprint_v1_mp3LameTag_vbrMethod", e => e.FingerprintV1?.Mp3VbrMethod, (e, v) => Fp(e).Mp3VbrMethod = v),
            I("fingerprint_v1_mp3LameTag_lowpassHz", e => e.FingerprintV1?.Mp3LowpassHz, (e, v) => Fp(e).Mp3LowpassHz = (int)v),
            I("fingerprint_v1_mp3LameTag_encoderDelay", e => e.FingerprintV1?.Mp3EncoderDelay, (e, v) => Fp(e).Mp3EncoderDelay = (int)v),
            I("fingerprint_v1_mp3LameTag_encoderPadding", e => e.FingerprintV1?.Mp3EncoderPadding, (e, v) => Fp(e).Mp3EncoderPadding = (int)v),
            I("fingerprint_v1_mp3LameTag_musicCrc", e => e.FingerprintV1?.Mp3MusicCrc, (e, v) => Fp(e).Mp3MusicCrc = (int)v),
            T("fingerprint_v1_audioHead64kMd5", e => e.FingerprintV1?.AudioHead64kMd5, (e, v) => Fp(e).AudioHead64kMd5 = v),
            T("fingerprint_v1_audioHead64kMd5Source", e => e.FingerprintV1?.AudioHead64kMd5Source, (e, v) => Fp(e).AudioHead64kMd5Source = v),
        };

        static readonly string[] Names = Array.ConvertAll(Cols, c => c.Name);
        internal static IReadOnlyList<string> ColumnNames => Names;

        // ---- v2 split (operator invariant: file 1 : locations many : audio description 1).
        // COPY = facts about one file on disk: tags as found in that copy, fingerprint_v1_* (size, codec,
        // encoder, LAME, head hash, path tail), fileMd5, SMFM (read from that copy's tags — copies differ,
        // the dedupe keeper rule depends on it), analysisDuration, trackId. Everything else describes the
        // AUDIO and is stored once per media: every Essentia descriptor, bitUsage, HF, the content hash.
        static readonly HashSet<string> CopySet = new HashSet<string>(StringComparer.Ordinal)
        {
            "trackId", "artist", "title", "album", "genre",
            "smfmScores", "smfmChannel", "smfmChannelName", "smfmBpm",
            "analysisDuration", "fileMd5",
        };
        static bool IsCopy(Col c) => CopySet.Contains(c.Name) || c.Name.StartsWith("fingerprint_v1_", StringComparison.Ordinal);

        /// <summary>Indexes into Cols (= ToRow positions) of the content and copy columns, in Cols order.</summary>
        static readonly int[] ContentIdx = BuildIdx(false);
        static readonly int[] CopyIdx = BuildIdx(true);
        static int[] BuildIdx(bool copy)
        {
            var l = new List<int>();
            for (int i = 0; i < Cols.Length; i++) if (IsCopy(Cols[i]) == copy) l.Add(i);
            return l.ToArray();
        }
        internal static IReadOnlyList<string> ContentColumnNames => Array.ConvertAll(ContentIdx, i => Cols[i].Name);
        internal static IReadOnlyList<string> CopyColumnNames => Array.ConvertAll(CopyIdx, i => Cols[i].Name);

        // ---- content comparison (the v2 split, later C2's drift rule): run-to-run extractor noise is NOT a disagreement.
        // Measured 2026-10-12 on the real store: 590 duplicate-sha copies scanned on different days differed only in the
        // 4th decimal of spectral-shape and band statistics (constraint 3); one real collision differed in 91 fields.
        internal const double NoiseRelative = 0.01;
        internal const double NoiseAbsolute = 1e-6;
        internal static int ContentColumnCount => ContentIdx.Length;
        internal static string ContentColumnName(int k) => Cols[ContentIdx[k]].Name;
        /// <summary>Position of a content column in <see cref="ContentColumnNames"/>, or -1.</summary>
        internal static int ContentIndexOf(string name)
        {
            for (int k = 0; k < ContentIdx.Length; k++) if (Cols[ContentIdx[k]].Name == name) return k;
            return -1;
        }

        /// <summary>Reads the content columns (in <see cref="ContentColumnNames"/> order) starting at <paramref name="firstColumn"/>.</summary>
        internal static object?[] ReadContentValues(SqliteStmt st, int firstColumn) => ReadIdx(st, firstColumn, ContentIdx);

        /// <summary>True when two values of content column k agree within noise: reals and float vectors within
        /// 1% relative or 1e-6 absolute (element-wise); ints, text and int vectors exactly; null only equals null.</summary>
        internal static bool ContentEqualWithinNoise(int k, object? a, object? b) => ValueEqual(Cols[ContentIdx[k]].Kind, a, b, exact: false);

        /// <summary>True when two values of content column k are exactly equal (no noise allowance) — the comparison a fresh
        /// measurement is held to (T01): a re-measurement that differs at all is the newer observation and is stored as such.</summary>
        internal static bool ContentEqualExact(int k, object? a, object? b) => ValueEqual(Cols[ContentIdx[k]].Kind, a, b, exact: true);

        /// <summary>The content column k of a ToRow() row.</summary>
        internal static object? ContentValue(object?[] row, int k) => row[ContentIdx[k]];

        /// <summary>Reads the copy columns (in <see cref="CopyColumnNames"/> order) starting at <paramref name="firstColumn"/>.</summary>
        internal static object?[] ReadCopyValues(SqliteStmt st, int firstColumn) => ReadIdx(st, firstColumn, CopyIdx);

        /// <summary>True when a stored copy row (as read by <see cref="ReadCopyValues"/>) equals the copy columns of a
        /// ToRow() row exactly — copy facts are file facts, so there is no noise to allow for.</summary>
        internal static bool CopyEqualExact(object?[] storedCopy, object?[] row) => FirstCopyDifference(storedCopy, row) == null;

        /// <summary>Name of the first copy column that differs, with both values, or null when all agree (for the log).</summary>
        internal static string? FirstCopyDifference(object?[] storedCopy, object?[] row)
        {
            for (int k = 0; k < CopyIdx.Length; k++)
                if (!ValueEqual(Cols[CopyIdx[k]].Kind, storedCopy[k], row[CopyIdx[k]], exact: true))
                    return $"{Cols[CopyIdx[k]].Name}: stored {Describe(storedCopy[k])} vs arriving {Describe(row[CopyIdx[k]])}";
            return null;
        }
        static string Describe(object? v) => v == null ? "null" : v is double[] d ? "f64[" + d.Length + "]" : v is int[] i ? "i32[" + i.Length + "]" : v.ToString() + " (" + v.GetType().Name + ")";

        static object?[] ReadIdx(SqliteStmt st, int firstColumn, int[] idx)
        {
            var values = new object?[idx.Length];
            for (int k = 0; k < idx.Length; k++)
            {
                int c = firstColumn + k;
                if (st.IsNull(c)) continue;
                int ci = idx[k];
                values[k] = Cols[ci].Kind switch
                {
                    Kind.Real => FromScaled(st.GetInt64(c), ColScale[ci], ColDp[ci]),                             // T04: scaled INTEGER -> the double Math.Round(v, dp) produced
                    Kind.Int => st.GetInt64(c),
                    Kind.Text => st.GetString(c),
                    Kind.VecF64 => ColSparse[ci] ? DecodeSparse(st.GetBlob(c)!, ColScale[ci], ColDp[ci], Cols[ci].Name) : DecodeI32Scaled(st.GetBlob(c)!, ColScale[ci], ColDp[ci], Cols[ci].Name),
                    _ => DecodeI32(st.GetBlob(c)!, Cols[ci].Name),
                };
            }
            return values;
        }

        static bool ValueEqual(Kind kind, object? a, object? b, bool exact)
        {
            if (a == null || b == null) return a == null && b == null;
            switch (kind)
            {
                case Kind.Real: return exact ? ((double)a == (double)b || (double.IsNaN((double)a) && double.IsNaN((double)b))) : Near((double)a, (double)b);
                case Kind.Int: return (long)a == (long)b;
                case Kind.Text: return string.Equals((string)a, (string)b, StringComparison.Ordinal);
                case Kind.VecF64:
                {
                    var x = (double[])a; var y = (double[])b;
                    if (x.Length != y.Length) return false;
                    for (int i = 0; i < x.Length; i++) if (exact ? x[i] != y[i] : !Near(x[i], y[i])) return false;
                    return true;
                }
                default:
                {
                    var x = (int[])a; var y = (int[])b;
                    if (x.Length != y.Length) return false;
                    for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) return false;
                    return true;
                }
            }
        }
        // NaN equals NaN here on purpose: a stored NaN re-arriving as NaN must not be a perpetual conflict (none can reach the
        // store from JSON today; the rule is pinned so a future producer cannot create one by accident).
        static bool Near(double a, double b) => (double.IsNaN(a) && double.IsNaN(b)) || Math.Abs(a - b) <= Math.Max(NoiseAbsolute, NoiseRelative * Math.Max(Math.Abs(a), Math.Abs(b)));

        // ---- copy facts an arrival observed (keeper 067/068). The catalog reader collapses an absent key into a default (trackId 0,
        // bitDepth 0, empty text), so the row alone cannot tell "the catalog did not carry it" from an explicit zero. An entry read
        // from a catalog for the store carries a mask of the copy keys it actually holds (present and not JSON null); a scan's
        // entry carries none and observes every column, as before.
        static readonly string[][] CopyJsonPath = BuildCopyJsonPath();
        static string[][] BuildCopyJsonPath()
        {
            const string Fp = "fingerprint_v1_", Lame = "fingerprint_v1_mp3LameTag_";
            var p = new string[CopyIdx.Length][];
            for (int k = 0; k < CopyIdx.Length; k++)
            {
                var n = Cols[CopyIdx[k]].Name;
                p[k] = n.StartsWith(Lame, StringComparison.Ordinal) ? new[] { "fingerprint.v1", "mp3LameTag", n.Substring(Lame.Length) }
                     : n.StartsWith(Fp, StringComparison.Ordinal) ? new[] { "fingerprint.v1", n.Substring(Fp.Length) }
                     : new[] { n };
            }
            return p;
        }

        /// <summary>Bit k set when the catalog entry carries copy column k (CopyIdx order): the key exists and is not JSON null. An
        /// <c>smfm*</c> column is selected by the reader's own rule, <c>Program.SelectSmfmKey</c> (modern key, else legacy). Fingerprint columns count only
        /// when the reader accepted the block (<paramref name="fingerprintParsed"/>): a block it refused yields no values to observe.</summary>
        internal static ulong ObservedCopyMask(System.Text.Json.JsonElement track, bool fingerprintParsed)
        {
            ulong mask = 0;
            for (int k = 0; k < CopyIdx.Length; k++)
            {
                var path = CopyJsonPath[k];
                if (path.Length > 1 && !fingerprintParsed) continue;
                // SMFM: the reader's own key rule (Program.SelectSmfmKey), never a second copy of it (keeper 073).
                bool present = path.Length == 1 && path[0].StartsWith("smfm", StringComparison.Ordinal)
                    ? track.ValueKind == System.Text.Json.JsonValueKind.Object && Program.SelectSmfmKey(track, path[0], out _)
                    : Has(track, path);
                if (present) mask |= 1UL << k;
            }
            return mask;
        }
        static bool Has(System.Text.Json.JsonElement el, string[] path)
        {
            foreach (var key in path)
            {
                if (el.ValueKind != System.Text.Json.JsonValueKind.Object || !el.TryGetProperty(key, out el)) return false;
            }
            return el.ValueKind != System.Text.Json.JsonValueKind.Null;
        }

        /// <summary>The writer's decimal places for each copy column that is a Real (FieldPrecision Dp; -1 otherwise). The store keeps
        /// analysisDuration and smfmBpm at StoreDp 6 while the catalog prints 1 and 3.</summary>
        static readonly int[] CopyWrittenDp = BuildCopyWrittenDp();
        static int[] BuildCopyWrittenDp()
        {
            var d = new int[CopyIdx.Length];
            for (int k = 0; k < CopyIdx.Length; k++)
                d[k] = Cols[CopyIdx[k]].Kind == Kind.Real && FieldPrecision.TryGet(Cols[CopyIdx[k]].Name, out var s) && s.Dp < s.StoreDp ? s.Dp : -1;
            return d;
        }

        /// <summary>True when an arriving copy value is the stored one: exactly equal, or — only for a value from a CATALOG IMPORT
        /// (<paramref name="catalogImport"/>), in a column the catalog prints at fewer places than the store keeps — equal to the stored
        /// value as the writer prints it (<c>Math.Round(stored, Dp)</c>, the catalog writer's own expression): a 1-dp catalog duration
        /// agreeing with a 6-dp stored one is the same observation. A scan's value is a fresh measurement at the store's precision,
        /// so it is held to exact equality (keeper 070: a fresh 8.5 replaces a stored 8.54321).</summary>
        static bool CopyAgrees(int k, object? stored, object? arriving, bool catalogImport)
        {
            if (ValueEqual(Cols[CopyIdx[k]].Kind, stored, arriving, exact: true)) return true;
            return catalogImport && CopyWrittenDp[k] >= 0 && stored is double s && arriving is double a && a == Math.Round(s, CopyWrittenDp[k]);
        }

        /// <summary>The row an arriving entry's copy facts produce over the stored ones, or null when nothing changes. Per copy column:
        /// not observed (<paramref name="observed"/> bit clear) or agreeing (<see cref="CopyAgrees"/>) keeps the STORED value — a
        /// new copy row stores NULL for an unobserved column; anything else is the arrival. Copy facts describe the file, so an
        /// supplied difference is the newer observation and wins exactly. An arriving NULL preserves the stored value unless
        /// <paramref name="clearNull"/> allows an explicit clear (a verb, or a supplied empty string parsed as NULL) — NULL is not present
        /// (operator 2026-10-25), for a scan as much as for an import. A column in <paramref name="cleared"/> is an explicit removal: it is
        /// written NULL whatever the stored value (the scan's stale whole-file MD5 after a tag-only edit). <paramref name="observed"/> null = every
        /// column eligible (a scan's entry). <paramref name="catalogImport"/> is the caller's explicit statement that the arrival is a catalog
        /// import, the only arrival the written-precision equivalence applies to (keeper 070, TDA 054: a scan's value is a fresh
        /// measurement and replaces exactly). <paramref name="firstDifference"/> names the first column written, for the log.</summary>
        internal static object?[]? MergeCopy(object?[]? storedCopy, object?[] row, ulong? observed, bool catalogImport, out string? firstDifference, bool clearNull = false, ulong cleared = 0)
        {
            firstDifference = storedCopy == null ? "no copy row yet" : null;
            var merged = (object?[])row.Clone();
            for (int k = 0; k < CopyIdx.Length; k++)
            {
                var stored = storedCopy?[k];
                var arriving = row[CopyIdx[k]];
                if ((cleared & (1UL << k)) != 0)
                {
                    merged[CopyIdx[k]] = null;
                    if (stored != null && firstDifference == null) firstDifference = $"{Cols[CopyIdx[k]].Name}: stored {Describe(stored)} cleared";
                    continue;
                }
                if ((!clearNull && arriving == null) || (observed is ulong m && (m & (1UL << k)) == 0) || (storedCopy != null && CopyAgrees(k, stored, arriving, catalogImport)))
                {
                    merged[CopyIdx[k]] = stored;
                    continue;
                }
                if (firstDifference == null)
                    firstDifference = $"{Cols[CopyIdx[k]].Name}: stored {Describe(stored)} vs arriving {Describe(arriving)}";
            }
            return firstDifference == null ? null : merged;
        }

        /// <summary>A ToRow() row whose content columns are the STORED values except the given fills, which keep the arriving
        /// value — the row an UpsertContent writes when a null is filled under the same producer (review #4).</summary>
        internal static object?[] WithStoredContentExcept(object?[] row, object?[] storedContent, ICollection<int> fills)
        {
            var merged = (object?[])row.Clone();
            for (int k = 0; k < ContentIdx.Length; k++) if (!fills.Contains(k)) merged[ContentIdx[k]] = storedContent[k];
            return merged;
        }

        /// <summary>A ToRow() row whose content columns are the ARRIVING values wherever the arrival observed one, and the STORED
        /// values where it did not (arrived NULL) — the row a remeasure writes (T01, review finding 1): a scan without ffmpeg, or
        /// with --no-bitusage, must not erase the bitUsage / HF values an earlier scan measured.</summary>
        internal static object?[] WithStoredContentWhereUnobserved(object?[] row, object?[] storedContent)
        {
            var merged = (object?[])row.Clone();
            for (int k = 0; k < ContentIdx.Length; k++) if (merged[ContentIdx[k]] == null) merged[ContentIdx[k]] = storedContent[k];
            return merged;
        }

        /// <summary>A content value as conflict text: numbers round-trip exactly, vectors as hex of their blob.</summary>
        internal static string? FormatContent(int k, object? v)
        {
            if (v == null) return null;
            switch (Cols[ContentIdx[k]].Kind)
            {
                case Kind.Real: return ((double)v).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                case Kind.Int: return ((long)v).ToString(System.Globalization.CultureInfo.InvariantCulture);
                case Kind.Text: return (string)v;
                case Kind.VecF64: return BitConverter.ToString(EncodeF64((double[])v)).Replace("-", "");
                default: return BitConverter.ToString(EncodeI32((int[])v)).Replace("-", "");
            }
        }

        /// <summary>Every column that carries a number, with its kind name (Real / Int / VecF64 / VecI32) — the schema-walk seam the
        /// FieldPrecision self-test uses to refuse a numeric column nobody declared a precision for (T03).</summary>
        internal static IEnumerable<(string Name, string Kind)> NumericColumns()
        {
            foreach (var c in Cols) if (c.Kind != Kind.Text) yield return (c.Name, c.Kind.ToString());
        }

        internal static int CopyColumnCount => CopyIdx.Length;
        /// <summary>Index of a column in a full ToRow() row (Cols order); -1 when unknown. Test seam for building raw rows.</summary>
        internal static int RowIndexOf(string name) { for (int i = 0; i < Cols.Length; i++) if (Cols[i].Name == name) return i; return -1; }
        /// <summary>Index of a copy column in CopyIdx order (what ReadCopyValues returns); -1 when unknown.</summary>
        internal static int CopyIndexOf(string name) { for (int k = 0; k < CopyIdx.Length; k++) if (Cols[CopyIdx[k]].Name == name) return k; return -1; }

        /// <summary>Keeper 099: what a per-file scan (--analyze-file, --file-list, --folder) OBSERVES of a copy — every copy fact except
        /// trackId. Those modes read tags, never an iTunes XML, so their trackId is the default 0, a value nobody observed; marked
        /// unobserved, it can never replace the XML id an iTunes-XML scan stored (MergeCopy keeps the stored value). The iTunes-XML
        /// scan leaves CopyObserved null (all observed), so an id it reads still replaces the stored one.</summary>
        internal static ulong PerFileScanCopyObserved => AllCopyObservedExcept("trackId");

        /// <summary>Every copy column observed.</summary>
        internal static ulong AllCopyObserved => AllCopyObservedExcept();

        /// <summary>The copy-column bits for the named columns — a <see cref="TrackEntry.CopyCleared"/> mask (an explicit removal).</summary>
        internal static ulong CopyMaskOf(params string[] names)
        {
            ulong mask = 0;
            foreach (var n in names)
            {
                int k = CopyIndexOf(n);
                if (k < 0) throw new InvalidOperationException($"{n} is not a copy column");
                mask |= 1UL << k;
            }
            return mask;
        }

        /// <summary>What a store-backed VERB observes of a copy: everything the published catalog carries — i.e. every copy column except
        /// the ones the field policy keeps out of the file (trackId). Those arrive as the type's default (0), a value nobody observed, and
        /// marked observed they replaced a stored NULL (execution review 2026-10-21, D5).</summary>
        internal static ulong VerbCopyObserved => AllCopyObservedExcept(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(CopyColumnNames, FieldPolicy.IsExcluded)));

        /// <summary>The copy columns a stored row has NO value for — the only ones a known-older arrival may touch (a fill, never a
        /// replacement; execution review 2026-10-21, D4). No stored row at all = every column.</summary>
        internal static ulong CopyMaskWhereNull(object?[]? storedCopy)
        {
            if (storedCopy == null) return AllCopyObserved;
            ulong mask = 0;
            for (int k = 0; k < CopyIdx.Length; k++) if (storedCopy[k] == null) mask |= 1UL << k;
            return mask;
        }

        static ulong AllCopyObservedExcept(params string[] names)
        {
            ulong mask = CopyIdx.Length >= 64 ? ulong.MaxValue : (1UL << CopyIdx.Length) - 1;
            foreach (var n in names)
            {
                int k = CopyIndexOf(n);
                if (k < 0) throw new InvalidOperationException($"{n} is not a copy column");
                mask &= ~(1UL << k);
            }
            return mask;
        }

        /// <summary>The first column of a ToRow() row that the typed store CANNOT hold (a scalar beyond int64 at its scale, a vector
        /// element beyond int32, a sparse vector longer than u16), with the reason — or null when the whole row is storable. Called
        /// BEFORE any entry mutation (keeper partial-entry defect, T04): an entry is rejected whole or written whole, never a changed
        /// content row beside an old copy row. Mirrors exactly what BindIdx would throw on.</summary>
        internal static string? FirstUnstorable(object?[] row)
        {
            for (int i = 0; i < Cols.Length; i++)
            {
                var v = row[i]; if (v == null) continue;
                switch (Cols[i].Kind)
                {
                    case Kind.Real when v is double d:
                        if (!double.IsNaN(d) && !double.IsInfinity(d) && Math.Abs(d * ColScale[i]) > 9.0e15) return $"{Cols[i].Name}: {d:R} does not fit the store's scaled-integer column at {ColScale[i]} (|v × scale| > 9e15)";
                        break;
                    case Kind.VecF64 when v is double[] a:
                        if (ColSparse[i] && a.Length > ushort.MaxValue) return $"{Cols[i].Name}: a {a.Length}-element vector exceeds the sparse layout's u16 length";
                        for (int j = 0; j < a.Length; j++)
                        {
                            if (double.IsNaN(a[j]) || double.IsInfinity(a[j])) continue;
                            double s = Math.Round(a[j] * ColScale[i]);
                            if (s > int.MaxValue || s < int.MinValue) return $"{Cols[i].Name}: element {a[j]:R} does not fit the store's int32 ×{ColScale[i]} vector";
                        }
                        break;
                }
            }
            return null;
        }

        /// <summary>Names of the columns (ContentIdx or CopyIdx order) whose value carries more digits than the field's declared
        /// precision — what the v3 migration must canonicalise and therefore report (T04b). Parser output never appears here.</summary>
        internal static IEnumerable<string> ColumnsBeyondPrecision(object?[] values, bool content)
        {
            var idx = content ? ContentIdx : CopyIdx;
            for (int k = 0; k < idx.Length; k++)
            {
                int i = idx[k]; var v = values[k];
                if (v is double d) { if (!Representable(d, ColScale[i])) yield return Cols[i].Name; }
                else if (v is double[] a)
                {
                    const long vecMax = int.MaxValue;   // vector elements are int32 ×scale in both the dense and the sparse layout
                    for (int j = 0; j < a.Length; j++)
                        if (!Representable(a[j], ColScale[i]) || (!double.IsNaN(a[j]) && !double.IsInfinity(a[j]) && Math.Abs(Math.Round(a[j] * ColScale[i])) > vecMax)) { yield return Cols[i].Name; break; }
                }
            }
        }

        /// <summary>A full row (Cols order) whose content columns are the given content values (ContentIdx order), everything else
        /// null — the shape <see cref="BindContent"/> takes; used by the v3 migration to re-bind rows read with the v2 reader.</summary>
        internal static object?[] ContentRowFromValues(object?[] contentValues)
        {
            var row = new object?[Cols.Length];
            for (int k = 0; k < ContentIdx.Length; k++) row[ContentIdx[k]] = contentValues[k];
            return row;
        }
        internal static object?[] CopyRowFromValues(object?[] copyValues)
        {
            var row = new object?[Cols.Length];
            for (int k = 0; k < CopyIdx.Length; k++) row[CopyIdx[k]] = copyValues[k];
            return row;
        }

        /// <summary>Quoted, comma-separated column lists for SQL built outside this class (the v2 migration).</summary>
        internal static string ContentColumnList(string? alias = null) => JoinQuoted(ContentIdx, alias);
        internal static string CopyColumnList(string? alias = null) => JoinQuoted(CopyIdx, alias);
        static string JoinQuoted(int[] idx, string? alias)
        {
            var sb = new StringBuilder();
            for (int k = 0; k < idx.Length; k++) { if (k > 0) sb.Append(", "); if (alias != null) sb.Append(alias).Append('.'); sb.Append(Q(Cols[idx[k]].Name)); }
            return sb.ToString();
        }

        /// <summary>The entry as a row — every REAL and every f64 vector element at the field's STORE precision (FieldPrecision
        /// StoreDp; T04), so a value agrees with what the scaled-integer column can hold and two arrivals of one measurement
        /// compare like with like in the drift/unchanged tests. The identity for every parser-produced value. Ints and text pass
        /// through.</summary>
        internal static object?[] ToRow(TrackEntry entry)
        {
            var row = new object?[Cols.Length];
            for (int i = 0; i < Cols.Length; i++)
            {
                var v = Cols[i].Get(entry);
                switch (Cols[i].Kind)
                {
                    // T04: the canonical form of a numeric field is its declared precision (FieldPrecision), not six significant figures.
                    // Parser output is already at that dp, so this is the identity for every scanned value; it makes a hand-built entry
                    // (a fixture, a future producer) agree with what the scaled-integer column can hold, so a re-arrival compares equal.
                    case Kind.Real when v is double d: row[i] = Canon(d, ColDp[i]); break;
                    case Kind.VecF64 when v is double[] a: { var c = new double[a.Length]; for (int k = 0; k < a.Length; k++) c[k] = Canon(a[k], ColDp[i]); row[i] = c; break; }
                    default: row[i] = v; break;
                }
            }
            return row;
        }

        /// <summary>T13 (gate d): <paramref name="copySide"/> with every CONTENT column replaced by <paramref name="contentSide"/>'s — the
        /// copy facts and the location (lastModified) stay the copy's own. Two entries that render the same bytes after this differ only
        /// in the shared description, the one difference a non-primary copy of a multi-copy media is allowed.</summary>
        internal static TrackEntry WithContentOf(TrackEntry copySide, TrackEntry contentSide)
        {
            var row = ToRow(copySide);
            var content = ToRow(contentSide);
            foreach (var i in ContentIdx) row[i] = content[i];
            return FromValues(row, copySide.LastModified);
        }

        /// <summary>The entry as the store would hold it: every numeric field at its declared precision (<see cref="ToRow"/>) and back.
        /// Two entries that render the same bytes after this hold the same facts — what differs is only how an older writer spelled a
        /// value (one ulp off the decimal, "0.0028770000000000002" for 0.002877), which no column can carry.</summary>
        internal static TrackEntry AtStorePrecision(TrackEntry entry) => FromValues(ToRow(entry), entry.LastModified);

        /// <summary>The entry with every field the catalog's field policy keeps out of the published file taken from <paramref name="stored"/>.
        /// A store-backed verb reads its result back from that file, so a policy-cut field (bpmHistogram) is absent there by policy, not
        /// cleared by the verb — written back as an exact arrival it would erase what the store holds.</summary>
        internal static TrackEntry WithPolicyCutFrom(TrackEntry arriving, TrackEntry stored) => WithPolicyCutFrom(arriving, stored, null);

        /// <summary>The policy-cut columns from <paramref name="first"/> where it has a value, else from <paramref name="second"/> (codex rescan
        /// 2026-10-23 R2: a merge source's NULL is unobserved, never a clear of the destination's value under the same content).</summary>
        internal static TrackEntry WithPolicyCutFrom(TrackEntry arriving, TrackEntry? first, TrackEntry? second)
        {
            var row = ToRow(arriving); var f = first != null ? ToRow(first) : null; var s = second != null ? ToRow(second) : null; bool any = false;
            // The arriving row's OWN value first (labs 2026-10-24 defect 3: a foreign catalog that carries bpmHistogram was ignored), then
            // the supplies in order; a NULL is "not present", never a clear (the plan's null rule).
            for (int i = 0; i < Cols.Length; i++) if (FieldPolicy.IsExcluded(Cols[i].Name)) { row[i] = row[i] ?? f?[i] ?? s?[i]; any = true; }
            return any ? FromValues(row, arriving.LastModified) : arriving;
        }

        /// <summary>Every CONTENT column the arriving entry does not carry taken from <paramref name="first"/>, else <paramref name="second"/>
        /// — for a MERGE's winning entry under the same content identity (labs 2026-10-24 defect 1: the verb's exact-arrival rule made a
        /// catalog that lacked mfcc clear the stored blob). "Does not carry" is the entry's <see cref="ObservedContentMask"/> when the
        /// catalog reader recorded one — so a field the parser turned into a default (bpm 0, key "") counts as absent — else a NULL
        /// value. Present values are untouched, zero and empty included when observed. The caller has already bound both supplies to the
        /// winner's identity; this never looks across it.</summary>
        internal static TrackEntry WithContentWhereUnobservedFrom(TrackEntry arriving, ulong[]? observed, TrackEntry? first, TrackEntry? second)
        {
            if (first == null && second == null) return arriving;
            var row = ToRow(arriving); var f = first != null ? ToRow(first) : null; var s = second != null ? ToRow(second) : null; bool any = false;
            for (int k = 0; k < ContentIdx.Length; k++)
            {
                int i = ContentIdx[k];
                bool carried = observed != null ? ContentIsObserved(observed, k) : row[i] != null;
                if (carried) continue;
                var v = f?[i] ?? s?[i];
                if (v != null) { row[i] = v; any = true; }
                else if (observed != null && row[i] != null) { row[i] = null; any = true; }   // a parser default with nothing to supply is not a value
            }
            return any ? FromValues(row, arriving.LastModified) : arriving;
        }

        /// <summary>The catalog key path of content column k: a name with underscores is a nested object (<c>keyVotes_krumhansl_key</c> is
        /// <c>keyVotes.krumhansl.key</c>; <c>bitUsage_method</c> is <c>bitUsage.method</c>), anything else a top-level key.</summary>
        static readonly string[][] ContentJsonPath = Array.ConvertAll(ContentIdx, i => Cols[i].Name.Contains("_") ? Cols[i].Name.Split('_') : new[] { Cols[i].Name });

        /// <summary>Bit k set when the catalog entry CARRIES content column k (ContentIdx order): the key path exists and is not JSON null.
        /// Recorded by the catalog reader beside <see cref="ObservedCopyMask"/> so a merge can tell an omitted field from a supplied
        /// zero or empty string after parsing has turned both into the same default (the plan's null rule: "preserve presence before
        /// parsing turns missing fields into defaults; actually supplied zero/false values remain values").</summary>
        internal static ulong[] ObservedContentMask(System.Text.Json.JsonElement track)
        {
            var mask = new ulong[(ContentIdx.Length + 63) / 64];
            for (int k = 0; k < ContentIdx.Length; k++) if (Has(track, ContentJsonPath[k])) mask[k >> 6] |= 1UL << (k & 63);
            return mask;
        }
        internal static bool ContentIsObserved(ulong[] mask, int k) => (k >> 6) < mask.Length && (mask[k >> 6] & (1UL << (k & 63))) != 0;

        /// <summary>True when every policy-cut CONTENT column is exactly equal between the two entries — the comparison the catalog bytes
        /// cannot make (codex rescan 2026-10-23 R3).</summary>
        internal static bool ExcludedContentEqual(TrackEntry a, TrackEntry b)
        {
            var ra = ToRow(a); var rb = ToRow(b);
            for (int k = 0; k < ContentIdx.Length; k++)
                if (FieldPolicy.IsExcluded(ContentColumnName(k)) && !ContentEqualExact(k, ContentValue(ra, k), ContentValue(rb, k))) return false;
            return true;
        }

        internal static TrackEntry FromValues(object?[] values, DateTime lastModified)
        {
            var e = new TrackEntry { Features = new TrackFeatures(), LastModified = lastModified };
            for (int i = 0; i < Cols.Length; i++)
                if (values[i] != null) Cols[i].Set(e, values[i]!);
            return e;
        }

        internal static TrackEntry FromRow(SqliteStmt st, int firstColumn, DateTime lastModified)
        {
            var values = new object?[Cols.Length];
            for (int i = 0; i < Cols.Length; i++)
            {
                int c = firstColumn + i;
                if (st.IsNull(c)) continue;
                values[i] = Cols[i].Kind switch
                {
                    // T04: the same v3 decode as ReadIdx — this is the export path (StoreDal.ReadExportEntries), so it must read what BindIdx wrote.
                    Kind.Real => FromScaled(st.GetInt64(c), ColScale[i], ColDp[i]),
                    Kind.Int => st.GetInt64(c),
                    Kind.Text => st.GetString(c),
                    Kind.VecF64 => ColSparse[i] ? DecodeSparse(st.GetBlob(c)!, ColScale[i], ColDp[i], Cols[i].Name) : DecodeI32Scaled(st.GetBlob(c)!, ColScale[i], ColDp[i], Cols[i].Name),
                    _ => DecodeI32(st.GetBlob(c)!, Cols[i].Name),
                };
            }
            return FromValues(values, lastModified);
        }

        /// <summary>1-based parameter index of the first content column in <see cref="UpsertContentSql"/>
        /// (after entity_id, content_key, content_key_kind, run_id, updated_utc).</summary>
        internal const int UpsertContentFirstParam = 6;
        /// <summary>1-based parameter index of the first copy column in <see cref="UpsertCopySql"/>
        /// (after location_id, run_id, updated_utc).</summary>
        internal const int UpsertCopyFirstParam = 4;

        internal static void BindContent(SqliteStmt st, int firstParam, object?[] row) => BindIdx(st, firstParam, row, ContentIdx);
        internal static void BindCopy(SqliteStmt st, int firstParam, object?[] row) => BindIdx(st, firstParam, row, CopyIdx);

        /// <summary>Binds the chosen columns of a ToRow() row at parameter indexes firstParam … firstParam + idx.Length - 1.</summary>
        static void BindIdx(SqliteStmt st, int firstParam, object?[] row, int[] idx)
        {
            for (int k = 0; k < idx.Length; k++)
            {
                int i = idx[k], p = firstParam + k;
                var v = row[i];
                if (v == null) { st.BindNull(p); continue; }
                switch (Cols[i].Kind)
                {
                    // T04 storage (schema v3): scaled INTEGER for a fixed-dp scalar; int32 ×10^dp blob (or the sparse layout) for a fixed-dp vector.
                    case Kind.Real:
                    {
                        double d = (double)v;
                        if (double.IsNaN(d) || double.IsInfinity(d)) st.BindNull(p); else st.Bind(p, ToScaled(d, ColScale[i], Cols[i].Name));
                        break;
                    }
                    case Kind.Int: st.Bind(p, (long)v); break;
                    case Kind.Text: st.Bind(p, (string)v); break;
                    case Kind.VecF64: st.Bind(p, ColSparse[i] ? EncodeSparse((double[])v, ColScale[i], Cols[i].Name) : EncodeI32Scaled((double[])v, ColScale[i], Cols[i].Name)); break;
                    default: st.Bind(p, EncodeI32((int[])v)); break;
                }
            }
        }

        /// <summary>The v2 representation (REAL columns, float64 blobs), kept verbatim for the v2 → v3 migration's data step and for
        /// self-tests that build a v2 fixture: reads a v2 content row's values.</summary>
        internal static object?[] ReadContentValuesLegacyV2(SqliteStmt st, int firstColumn)
        {
            var values = new object?[ContentIdx.Length];
            for (int k = 0; k < ContentIdx.Length; k++)
            {
                int c = firstColumn + k;
                if (st.IsNull(c)) continue;
                values[k] = Cols[ContentIdx[k]].Kind switch
                {
                    Kind.Real => st.GetDouble(c),
                    Kind.Int => st.GetInt64(c),
                    Kind.Text => st.GetString(c),
                    Kind.VecF64 => DecodeF64(st.GetBlob(c)!),
                    _ => DecodeI32(st.GetBlob(c)!, Cols[ContentIdx[k]].Name),
                };
            }
            return values;
        }
        internal static object?[] ReadCopyValuesLegacyV2(SqliteStmt st, int firstColumn)
        {
            var values = new object?[CopyIdx.Length];
            for (int k = 0; k < CopyIdx.Length; k++)
            {
                int c = firstColumn + k;
                if (st.IsNull(c)) continue;
                values[k] = Cols[CopyIdx[k]].Kind switch
                {
                    Kind.Real => st.GetDouble(c),
                    Kind.Int => st.GetInt64(c),
                    Kind.Text => st.GetString(c),
                    Kind.VecF64 => DecodeF64(st.GetBlob(c)!),
                    _ => DecodeI32(st.GetBlob(c)!, Cols[CopyIdx[k]].Name),
                };
            }
            return values;
        }
        /// <summary>Binds a ToRow() row's chosen columns in the v2 representation (self-test fixture builder only).</summary>
        internal static void BindLegacyV2(SqliteStmt st, int firstParam, object?[] row, bool content)
        {
            var idx = content ? ContentIdx : CopyIdx;
            for (int k = 0; k < idx.Length; k++)
            {
                int i = idx[k], p = firstParam + k;
                var v = row[i];
                if (v == null) { st.BindNull(p); continue; }
                switch (Cols[i].Kind)
                {
                    // v2 representation, verbatim: REAL for every scalar, float64 blobs for vectors. Fixture builder only.
                    case Kind.Real: st.Bind(p, (double)v); break;
                    case Kind.Int: st.Bind(p, (long)v); break;
                    case Kind.Text: st.Bind(p, (string)v); break;
                    case Kind.VecF64: st.Bind(p, EncodeF64((double[])v)); break;
                    default: st.Bind(p, EncodeI32((int[])v)); break;
                }
            }
        }

        // ---- T04: the store's representation of numbers (schema v3). In-memory rows stay double / double[]; only the bytes change.
        // Fixed(dp)       -> INTEGER = round(v × 10^dp). Read back as stored / 10^dp, which is the double Math.Round(v, dp) produced.
        // FixedVec(dp)    -> BLOB of int32 ×10^dp, little-endian.
        // SparseFixedVec  -> BLOB: u16 length, u16 count, then (u16 index, int32 value) per non-zero — self-describing, exact.
        static readonly int[] ColDp = BuildDp();
        static readonly long[] ColScale = BuildScales();
        static readonly bool[] ColSparse = BuildSparse();
        // ColDp is the STORE precision (FieldPrecision.StoreDp): what the typed column holds and what ToRow canonicalises to. For every
        // parser field it equals the writer's Dp; for analysisDuration / smfmBpm it is 6 so a v2 store's six-figure values survive.
        static int[] BuildDp() { var d = new int[Cols.Length]; for (int i = 0; i < Cols.Length; i++) d[i] = (Cols[i].Kind == Kind.Real || Cols[i].Kind == Kind.VecF64) && FieldPrecision.TryGet(Cols[i].Name, out var s) ? s.StoreDp : 0; return d; }
        static long[] BuildScales() { var s = new long[Cols.Length]; for (int i = 0; i < Cols.Length; i++) { s[i] = 1; for (int k = 0; k < ColDp[i]; k++) s[i] *= 10; } return s; }
        static bool[] BuildSparse() { var b = new bool[Cols.Length]; for (int i = 0; i < Cols.Length; i++) b[i] = Cols[i].Kind == Kind.VecF64 && FieldPrecision.TryGet(Cols[i].Name, out var s) && s.Shape == FieldPrecision.Shape.SparseFixedVec; return b; }
        // Canon normalises -0.0 to +0.0 as well (review finding 2): Math.Round(-tiny, dp) is -0.0, the catalog writer prints it "-0", but the
        // store's integer has no signed zero. The values are equal; the exporter's round-trip classes "-0" vs "0" as the sign of zero.
        static double Canon(double v, int dp) => double.IsNaN(v) || double.IsInfinity(v) ? v : Math.Round(v, dp) + 0.0;   // + 0.0 turns -0.0 into +0.0
        /// <summary>True when the decimal d stands for is exactly representable as an integer at this scale: |d × scale| is within a few
        /// ulps of an integer. This — not double equality with Math.Round — is the migration's preservation test (review finding 1): a
        /// real v2 value is a shortest-text double, and Math.Round of it can legitimately sit one ulp away while the DECIMAL is intact.</summary>
        internal static bool Representable(double d, long scale)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return true;   // binds NULL; nothing to lose
            double s = d * scale;
            if (Math.Abs(s) > 9.0e15) return false;
            double tol = Math.Max(1e-9, Math.Abs(s) * 8.9e-16);          // ~4 ulps of s, floored for tiny magnitudes (an exact-integer gate refuses genuine data: mutation-checked)
            return Math.Abs(s - Math.Round(s)) <= tol;
        }
        static long ToScaled(double v, long scale, string name)
        {
            double s = v * scale;
            if (Math.Abs(s) > 9.0e15) throw new StoreException($"{name}: {v:R} does not fit the store's scaled-integer column at {scale} (|v × scale| > 9e15); nothing was written.");
            return (long)Math.Round(s);
        }
        /// <summary>The double a stored scaled integer stands for — DEFINED as what Math.Round(v, dp) produces, so the store
        /// reproduces the PARSER's double by construction (the parser is Math.Round(raw, dp)). THE 1-ULP CASE (named and pinned,
        /// T04; measured on .NET Framework 4.8 x64): the double nearest 0.022454 renders "0.022454", but Math.Round(it, 6) — and
        /// 22454.0 / 1e6 — return the neighbour one ulp BELOW ("0.022453999999999998"). The scan wrote that lower neighbour, the
        /// catalog's text carries it, and the store must give back exactly that double, not the shortest-text decimal, for the
        /// export to be byte-equal. Wrapping the division in Math.Round makes "same as the parser" the definition rather than a
        /// coincidence of this runtime's division.</summary>
        static double FromScaled(long s, long scale, int dp) => Math.Round((double)s / scale, dp);
        static int ToScaled32(double v, long scale, string name)
        {
            double s = Math.Round(v * scale);
            if (s > int.MaxValue || s < int.MinValue) throw new StoreException($"{name}: element {v:R} does not fit the store's int32 ×{scale} vector; nothing was written.");
            return (int)s;
        }
        static byte[] EncodeI32Scaled(double[] a, long scale, string name)
        {
            var o = new int[a.Length];
            for (int i = 0; i < a.Length; i++) o[i] = double.IsNaN(a[i]) || double.IsInfinity(a[i]) ? 0 : ToScaled32(a[i], scale, name);
            return EncodeI32(o);
        }
        static double[] DecodeI32Scaled(byte[] b, long scale, int dp, string name)
        {
            var o = DecodeI32(b, name); var a = new double[o.Length];
            for (int i = 0; i < o.Length; i++) a[i] = FromScaled(o[i], scale, dp);
            return a;
        }
        static byte[] EncodeSparse(double[] a, long scale, string name)
        {
            if (a.Length > ushort.MaxValue) throw new StoreException($"{name}: a {a.Length}-element vector exceeds the sparse layout's u16 length; nothing was written.");
            // NaN / Infinity elements are treated as 0 here exactly as the dense encoder does (review finding 3: the two must agree).
            int count = 0; for (int i = 0; i < a.Length; i++) if (a[i] != 0 && !double.IsNaN(a[i]) && !double.IsInfinity(a[i])) count++;
            var b = new byte[4 + count * 6]; int o = 0;
            b[o++] = (byte)(a.Length & 0xFF); b[o++] = (byte)(a.Length >> 8);
            b[o++] = (byte)(count & 0xFF); b[o++] = (byte)(count >> 8);
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] == 0 || double.IsNaN(a[i]) || double.IsInfinity(a[i])) continue;
                b[o++] = (byte)(i & 0xFF); b[o++] = (byte)(i >> 8);
                int v = ToScaled32(a[i], scale, name);
                b[o++] = (byte)v; b[o++] = (byte)(v >> 8); b[o++] = (byte)(v >> 16); b[o++] = (byte)(v >> 24);
            }
            return b;
        }
        // A corrupt blob is refused with the column named, never decoded into a plausible wrong vector (review finding 4).
        static double[] DecodeSparse(byte[] b, long scale, int dp, string name)
        {
            if (b.Length < 4) throw new StoreException($"{name}: sparse vector blob is {b.Length} bytes, shorter than its 4-byte header — the row is damaged.");
            int length = b[0] | (b[1] << 8), count = b[2] | (b[3] << 8);
            if (b.Length != 4 + count * 6) throw new StoreException($"{name}: sparse vector blob declares {count} non-zero(s) but holds {b.Length} bytes (expected {4 + count * 6}) — the row is damaged.");
            var a = new double[length]; int o = 4;
            for (int n = 0; n < count; n++)
            {
                int i = b[o] | (b[o + 1] << 8); o += 2;
                int v = b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24); o += 4;
                if (i >= length) throw new StoreException($"{name}: sparse vector blob names bin {i} of {length} — the row is damaged.");
                a[i] = FromScaled(v, scale, dp);
            }
            return a;
        }

        static byte[] EncodeF64(double[] a) { var b = new byte[a.Length * 8]; Buffer.BlockCopy(a, 0, b, 0, b.Length); return b; }
        static double[] DecodeF64(byte[] b) { var a = new double[b.Length / 8]; Buffer.BlockCopy(b, 0, a, 0, a.Length * 8); return a; }
        static byte[] EncodeI32(int[] a) { var b = new byte[a.Length * 4]; Buffer.BlockCopy(a, 0, b, 0, b.Length); return b; }
        static int[] DecodeI32(byte[] b, string name)
        {
            if (b.Length % 4 != 0) throw new StoreException($"{name}: int32 vector blob is {b.Length} bytes, not a multiple of 4 — the row is damaged.");
            var a = new int[b.Length / 4]; Buffer.BlockCopy(b, 0, a, 0, a.Length * 4); return a;
        }

        static string Q(string name) => "\"" + name + "\"";

        internal static readonly string UpsertContentSql = BuildUpsert(ContentIdx,
            "track_content", "entity_id, content_key, content_key_kind, run_id, updated_utc", "?, ?, ?, ?, ?", "entity_id",
            "content_key = excluded.content_key, content_key_kind = excluded.content_key_kind, run_id = excluded.run_id, updated_utc = excluded.updated_utc");
        internal static readonly string UpsertCopySql = BuildUpsert(CopyIdx,
            "track_copy", "location_id, run_id, updated_utc", "?, ?, ?", "location_id",
            "run_id = excluded.run_id, updated_utc = excluded.updated_utc");
        /// <summary>Every mapped column in Cols order, aliased c. (track_content) or p. (track_copy), for the
        /// copy ⋈ content read that <see cref="FromRow"/> consumes.</summary>
        internal static readonly string SelectList = BuildSelectList();

        static string BuildUpsert(int[] idx, string table, string metaCols, string metaQs, string conflictCol, string metaUpdate)
        {
            var cols = new StringBuilder(); var qs = new StringBuilder(); var upd = new StringBuilder();
            foreach (var i in idx)
            {
                cols.Append(", ").Append(Q(Cols[i].Name));
                qs.Append(", ?");
                upd.Append(", ").Append(Q(Cols[i].Name)).Append(" = excluded.").Append(Q(Cols[i].Name));
            }
            return $"INSERT INTO {table}({metaCols}{cols}) VALUES ({metaQs}{qs}) ON CONFLICT({conflictCol}) DO UPDATE SET {metaUpdate}{upd};";
        }

        static string BuildSelectList()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Cols.Length; i++) { if (i > 0) sb.Append(", "); sb.Append(IsCopy(Cols[i]) ? "p." : "c.").Append(Q(Cols[i].Name)); }
            return sb.ToString();
        }
    }
}
