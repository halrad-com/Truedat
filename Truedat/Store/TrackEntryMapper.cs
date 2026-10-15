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
                values[k] = Cols[idx[k]].Kind switch
                {
                    Kind.Real => st.GetDouble(c),
                    Kind.Int => st.GetInt64(c),
                    Kind.Text => st.GetString(c),
                    Kind.VecF64 => DecodeF64(st.GetBlob(c)!),
                    _ => DecodeI32(st.GetBlob(c)!),
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

        /// <summary>Quoted, comma-separated column lists for SQL built outside this class (the v2 migration).</summary>
        internal static string ContentColumnList(string? alias = null) => JoinQuoted(ContentIdx, alias);
        internal static string CopyColumnList(string? alias = null) => JoinQuoted(CopyIdx, alias);
        static string JoinQuoted(int[] idx, string? alias)
        {
            var sb = new StringBuilder();
            for (int k = 0; k < idx.Length; k++) { if (k > 0) sb.Append(", "); if (alias != null) sb.Append(alias).Append('.'); sb.Append(Q(Cols[idx[k]].Name)); }
            return sb.ToString();
        }

        /// <summary>The entry as a row — every REAL and every f64 vector element at the store's canonical precision (six
        /// significant figures, <see cref="CanonicalPrecision"/>), so two renderings of one measurement can never disagree in
        /// the store and the drift/unchanged tests compare like with like. Ints and text pass through.</summary>
        internal static object?[] ToRow(TrackEntry entry)
        {
            var row = new object?[Cols.Length];
            for (int i = 0; i < Cols.Length; i++)
            {
                var v = Cols[i].Get(entry);
                switch (Cols[i].Kind)
                {
                    case Kind.Real when v is double d: row[i] = CanonicalPrecision.Sig6(d); break;
                    case Kind.VecF64 when v is double[] a: { var c = new double[a.Length]; for (int k = 0; k < a.Length; k++) c[k] = CanonicalPrecision.Sig6(a[k]); row[i] = c; break; }
                    default: row[i] = v; break;
                }
            }
            return row;
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
                    Kind.Real => st.GetDouble(c),
                    Kind.Int => st.GetInt64(c),
                    Kind.Text => st.GetString(c),
                    Kind.VecF64 => DecodeF64(st.GetBlob(c)!),
                    _ => DecodeI32(st.GetBlob(c)!),
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
                    case Kind.Real: st.Bind(p, (double)v); break;
                    case Kind.Int: st.Bind(p, (long)v); break;
                    case Kind.Text: st.Bind(p, (string)v); break;
                    case Kind.VecF64: st.Bind(p, EncodeF64((double[])v)); break;
                    default: st.Bind(p, EncodeI32((int[])v)); break;
                }
            }
        }

        static byte[] EncodeF64(double[] a) { var b = new byte[a.Length * 8]; Buffer.BlockCopy(a, 0, b, 0, b.Length); return b; }
        static double[] DecodeF64(byte[] b) { var a = new double[b.Length / 8]; Buffer.BlockCopy(b, 0, a, 0, a.Length * 8); return a; }
        static byte[] EncodeI32(int[] a) { var b = new byte[a.Length * 4]; Buffer.BlockCopy(a, 0, b, 0, b.Length); return b; }
        static int[] DecodeI32(byte[] b) { var a = new int[b.Length / 4]; Buffer.BlockCopy(b, 0, a, 0, a.Length * 4); return a; }

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
