using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Truedat
{
    public class TrackFeatures
    {
        public int TrackId { get; set; }
        public string Artist { get; set; } = "";
        public string Title { get; set; } = "";
        public string Album { get; set; } = "";
        public string Genre { get; set; } = "";
        public string FilePath { get; set; } = "";

        public double Bpm { get; set; }
        public string Key { get; set; } = "";
        public string Mode { get; set; } = "";

        // Raw Essentia features (MBXHub computes valence/arousal from these with tunable weights)
        public double SpectralCentroid { get; set; }
        public double SpectralFlux { get; set; }
        public double Loudness { get; set; }
        public double Danceability { get; set; }

        // Arousal features
        public double OnsetRate { get; set; }
        public double ZeroCrossingRate { get; set; }
        public double SpectralRms { get; set; }

        // Valence features
        public double SpectralFlatness { get; set; }
        public double Dissonance { get; set; }
        public double PitchSalience { get; set; }
        public double ChordsChangesRate { get; set; }
        public double[]? Mfcc { get; set; }

        /// <summary>BS.1770 Loudness Range (LRA) in LU from Essentia's loudness_ebu128.loudness_range.
        /// Null when the streaming-extractor output didn't include the EBU R128 block.</summary>
        public double? DynamicRange { get; set; }
        /// <summary>Origin tag for DynamicRange: "essentia-lra" for streaming-extractor reads,
        /// null when DynamicRange is null.</summary>
        public string? DynamicRangeSource { get; set; }

        // Extended Essentia features — all nullable for back-compat with older mbxmoods.json.
        // Source paths documented per field; all *.mean unless noted.

        // Loudness dynamics
        public double? LoudnessMomentary { get; set; }        // lowlevel.loudness_ebu128.momentary.mean
        public double? LoudnessShortTerm { get; set; }        // lowlevel.loudness_ebu128.short_term.mean
        public double? ReplayGain { get; set; }               // metadata.audio_properties.replay_gain

        // Silence profile
        public double? SilenceRate20dB { get; set; }          // lowlevel.silence_rate_20dB.mean
        public double? SilenceRate30dB { get; set; }          // lowlevel.silence_rate_30dB.mean
        public double? SilenceRate60dB { get; set; }          // lowlevel.silence_rate_60dB.mean

        // Spectral shape
        public double? SpectralRolloff { get; set; }          // lowlevel.spectral_rolloff.mean
        public double? SpectralComplexity { get; set; }       // lowlevel.spectral_complexity.mean
        public double? SpectralEntropy { get; set; }          // lowlevel.spectral_entropy.mean
        public double? SpectralKurtosis { get; set; }         // lowlevel.spectral_kurtosis.mean
        public double? SpectralSkewness { get; set; }         // lowlevel.spectral_skewness.mean
        public double? SpectralSpread { get; set; }           // lowlevel.spectral_spread.mean
        public double? SpectralStrongPeak { get; set; }       // lowlevel.spectral_strongpeak.mean
        public double? SpectralDecrease { get; set; }         // lowlevel.spectral_decrease.mean
        public double? SpectralEnergy { get; set; }           // lowlevel.spectral_energy.mean
        public double? SpectralEnergyLow { get; set; }        // lowlevel.spectral_energyband_low.mean
        public double? SpectralEnergyMidLow { get; set; }     // lowlevel.spectral_energyband_middle_low.mean
        public double? SpectralEnergyMidHigh { get; set; }    // lowlevel.spectral_energyband_middle_high.mean
        public double? SpectralEnergyHigh { get; set; }       // lowlevel.spectral_energyband_high.mean

        // High-frequency content
        public double? Hfc { get; set; }                      // lowlevel.hfc.mean

        // Psychoacoustic bands — Bark
        public double? BarkCrest { get; set; }                // lowlevel.barkbands_crest.mean
        public double? BarkFlatness { get; set; }             // lowlevel.barkbands_flatness_db.mean
        public double? BarkKurtosis { get; set; }             // lowlevel.barkbands_kurtosis.mean
        public double? BarkSkewness { get; set; }             // lowlevel.barkbands_skewness.mean
        public double? BarkSpread { get; set; }               // lowlevel.barkbands_spread.mean

        // Psychoacoustic bands — ERB
        public double? ErbCrest { get; set; }                 // lowlevel.erbbands_crest.mean
        public double? ErbFlatness { get; set; }              // lowlevel.erbbands_flatness_db.mean
        public double? ErbKurtosis { get; set; }              // lowlevel.erbbands_kurtosis.mean
        public double? ErbSkewness { get; set; }              // lowlevel.erbbands_skewness.mean
        public double? ErbSpread { get; set; }                // lowlevel.erbbands_spread.mean

        // Psychoacoustic bands — Mel
        public double? MelCrest { get; set; }                 // lowlevel.melbands_crest.mean
        public double? MelFlatness { get; set; }              // lowlevel.melbands_flatness_db.mean
        public double? MelKurtosis { get; set; }              // lowlevel.melbands_kurtosis.mean
        public double? MelSkewness { get; set; }              // lowlevel.melbands_skewness.mean
        public double? MelSpread { get; set; }                // lowlevel.melbands_spread.mean

        // Rhythm / tonal
        public double? BeatsLoudness { get; set; }            // rhythm.beats_loudness.mean
        public double? ChordsStrength { get; set; }           // tonal.chords_strength.mean
        public double? HpcpCrest { get; set; }                // tonal.hpcp_crest.mean
        public double? HpcpEntropy { get; set; }              // tonal.hpcp_entropy.mean

        // 2026-07-22 tonal/rhythm extension wave — key confidence, tempo ambiguity,
        // chord tonality, tuning, simple loudness. All nullable ride-alongs:
        // populated on fresh analysis only, NOT in the re-extract canary, not
        // backfillable (Essentia-derived) — legacy entries lack them until a
        // deliberate re-analysis. Primary consumers: Camelot/harmonic-mixing
        // confidence gating (key votes) and half/double-time disambiguation
        // (bpm histogram peaks) in MBXHub's AutoQ.
        public KeyVote? KeyVoteKrumhansl { get; set; }        // tonal.key_krumhansl
        public KeyVote? KeyVoteTemperley { get; set; }        // tonal.key_temperley
        public KeyVote? KeyVoteEdma { get; set; }             // tonal.key_edma (the profile Key/Mode come from)
        public double? BpmFirstPeak { get; set; }             // rhythm.bpm_histogram_first_peak_bpm
        public double? BpmFirstPeakWeight { get; set; }       // rhythm.bpm_histogram_first_peak_weight
        public double? BpmSecondPeak { get; set; }            // rhythm.bpm_histogram_second_peak_bpm
        public double? BpmSecondPeakWeight { get; set; }      // rhythm.bpm_histogram_second_peak_weight
        public double? BpmSecondPeakSpread { get; set; }      // rhythm.bpm_histogram_second_peak_spread
        public string? ChordsKey { get; set; }                // tonal.chords_key
        public string? ChordsScale { get; set; }              // tonal.chords_scale
        public double[]? ChordsHistogram { get; set; }        // tonal.chords_histogram[24]
        public double? ChordsNumberRate { get; set; }         // tonal.chords_number_rate
        public double? TuningFrequency { get; set; }          // tonal.tuning_frequency (Hz, ~440)
        public double? TuningEqualTemperedDeviation { get; set; } // tonal.tuning_equal_tempered_deviation
        public double? TuningDiatonicStrength { get; set; }   // tonal.tuning_diatonic_strength
        public double? TuningNontemperedEnergyRatio { get; set; } // tonal.tuning_nontempered_energy_ratio
        public double? AverageLoudness { get; set; }          // lowlevel.average_loudness (0..1)

        // Phase 2.5 — bottom-bit analysis. Populated during fresh analysis via ffmpeg
        // PCM walk; null on legacy entries / ffmpeg-absent installs / non-decodable files.
        // Detects bit-depth fakery (16-bit content padded to 24-bit container).
        public BitUsageSummary? BitUsage { get; set; }

        // Phase 3 — spectral fake-hi-res signal. Fraction of audio energy above 22.05 kHz,
        // measured at native sample rate. Only populated when sourceSampleRate > 44100
        // (no Nyquist headroom otherwise). Pairs with BitUsage as the second independent
        // signal for the Phase 4 verdict's hires_genuine question — an upsampled CD with
        // added dither can fool BitUsage but can't fabricate HF content above the original
        // Nyquist. Genuine hi-res content typically lands in 0.001–0.05; upsampled content
        // lands at 0 or essentially-zero.
        public double? HfEnergyRatio { get; set; }
        public string? HfEnergyMethod { get; set; }   // frozen tag: "managed-fft-radix2-30s-mid-native" (Phase 5); legacy: "ffmpeg-rms-hp22050-30s-mid" (Phase 3)

        // Phase 5 — FFT-derived spectral-structure for the HF band. Distinguishes
        // genuine broadband hi-res content (high flatness, low symmetry) from
        // ffmpeg-upsampled fakes whose energy is concentrated in narrow mirror
        // spikes (low flatness, high peak-to-mean, high symmetry). Same gating
        // as HfEnergyRatio (sourceSampleRate > 44100 + ffmpeg present).
        public HfSpectralStructure? HfSpectralStructure { get; set; }

        // Sony SMFM (12-TONE) — read fresh on every scan (MC analyses files progressively).
        // Not stored in the Essentia cache; never copied in RebuildCacheEntryCore.
        public int[]?   SmfmScores;         // raw STMO slot scores 0-255, null when no MC analysis
        public int?     SmfmChannel;        // dominant raw STMO slot index (argmax) — NOT a mood channel
        public string?  SmfmChannelName;    // device-refuted slot label; always null now (see SmfmReader)
        public double?  SmfmBpm;            // GBPM float32 BPM

        /// <summary>True when this track carries Sony SMFM (12-TONE) data — the reusable
        /// "has SMFM?" flag. Read-only computed getter (no state, net48-safe). Use this
        /// everywhere instead of re-deriving the null/length check inline.</summary>
        public bool HasSmfm => SmfmScores != null && SmfmScores.Length > 0;
    }

    /// <summary>--backfill-level scope. Identity = TagLib + cheap file IO; Features =
    /// ffmpeg-driven bitUsage / hfEnergyRatio / hfSpectralStructure; All = both (default).</summary>
    public enum BackfillLevel { Identity, Features, All }

    /// <summary>One tonal key-profile vote (krumhansl / temperley / edma):
    /// detected key + scale + confidence 0..1. Persisted in the keyVotes block;
    /// consumers gate harmonic-mixing on strength and cross-profile agreement.</summary>
    public class KeyVote
    {
        public string Key = "";
        public string Scale = "";
        public double Strength;
    }

    /// <summary>Phase 2.5 bit-depth-substance measurement. A 24-bit file with
    /// LowestNonZeroBit >= 8 is 16-bit content padded with zeros — the canonical
    /// fake-hi-res signature. Populated by ComputeBitUsage (ffmpeg s32le walk).
    /// Public because TrackFeatures.BitUsage is part of the serialized contract.</summary>
    public sealed class BitUsageSummary
    {
        public int LowestNonZeroBit;      // 0 = bit 0 active in at least one sample; 8 = bottom 8 bits all zero
        public double BottomBitActivity;  // fraction of non-silent samples with bit 0 != 0 (0..1)
        public double EffectiveBits;      // log2(rms / quantStep) approximation; clipped to [0, 32] (s32le sample space ceiling)
        public int SamplesAnalyzed;
        public string Method = "";        // "ffmpeg-s32le-30s-mid" — frozen tag for future-method tracking
    }

    /// <summary>Phase 4 — the multi-signal-voted authenticity verdict per track.
    /// Four-string enum (yes/no/unknown/n/a) for each question; confidence is only
    /// populated for yes/no. Computed inline at write time via ComputeTruedatVerdict
    /// (NOT persisted in TrackEntry — recomputed on every save so threshold changes
    /// take effect without rescanning). Method tag identifies the algorithm/threshold
    /// generation; bumping the tag is how we mark a verdict-algorithm change distinct
    /// from a data-schema change.</summary>
    public sealed class TruedatVerdict
    {
        public string HiresGenuine = "n/a";              // "yes" | "no" | "unknown" | "n/a"
        public double? HiresConfidence;
        public string LossyTranscodeLikely = "n/a";      // "yes" | "no" | "unknown" | "n/a"
        public double? LossyTranscodeConfidence;
        public string Method = "truedat-v1-fft-corpus1-2026-05-18";  // Phase 5 — FFT-derived Signal F + bin-sharp hfEnergyRatio retune against corpus1 (23/23 hi-res correct overall: 5/5 real → "yes", 3/3 fake-upsampled → "unknown", 15/15 n/a; the lossless-24-bit subset that actually exercises the hi-res vote is 8/8)
        public string SpeechLikely = "n/a";               // "yes" | "no" | "unknown" | "n/a" — talk content vs music
        public double? SpeechConfidence;
        public string SpeechMethod = "truedat-speech-v1.2-untuned-2026-07-22";  // rev-1.2: danceability music veto tightened to < 0.50 (spares instrumental music); still untuned — calibrate against a labeled talk corpus before tightening further
    }

    class TrackEntry
    {
        public TrackFeatures Features = null!;
        public DateTime LastModified;
        public double? AnalysisDurationSecs;
        public string? FileMd5;
        // Identity fields — already computed in the parallel scan task block but
        // historically dropped on the moods-file write path. Persisting them here
        // means every scanned track lands in mbxmoods.json with the full identity
        // signal set, no separate hash backfill needed.
        public string? AudioStreamSha256;
        public string? AudioStreamSha256Source;   // "whole-file" (invariant bounds unavailable) | "flac-frames" (frame-anchored FLAC) | null = invariant
        public Program.FingerprintV1? FingerprintV1;
    }

    class AudioDetails
    {
        public string Codec = "", Format = "";
        public int Channels;
        public int SampleRate;
        public int BitRate;
        public int BitDepth;
        public double Duration;
        public double SizeMb;
        public DateTime LastProbed;
    }

    /// <summary>
    /// Path normalization utilities for cross-source path matching.
    /// Mirrors the consumer-side helper of the same name to keep identity matching consistent.
    /// </summary>
    static class PathHelper
    {
        /// <summary>Normalize path separators to backslash (Windows native).</summary>
        public static string NormalizeSeparators(string path)
        {
            return path.Replace('/', '\\');
        }

        /// <summary>
        /// Quote a single argument for Windows CreateProcess argument string.
        /// Handles embedded double-quotes and trailing backslashes per the
        /// CommandLineToArgvW parsing rules.
        /// </summary>
        public static string QuoteArg(string arg)
        {
            // Fast path: no special characters — just wrap in quotes
            if (arg.IndexOf('"') < 0 && arg.IndexOf('\\') < 0)
                return $"\"{arg}\"";

            var sb = new StringBuilder(arg.Length + 4);
            sb.Append('"');
            for (int i = 0; i < arg.Length; i++)
            {
                // Count consecutive backslashes
                int backslashes = 0;
                while (i < arg.Length && arg[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i == arg.Length)
                {
                    // Trailing backslashes: double them (they precede the closing quote)
                    sb.Append('\\', backslashes * 2);
                }
                else if (arg[i] == '"')
                {
                    // Backslashes before a quote: double them + escape the quote
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    // Backslashes not before a quote: leave them as-is
                    sb.Append('\\', backslashes);
                    sb.Append(arg[i]);
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }

    /// <summary>
    /// Case-insensitive, separator-normalizing path comparer.
    /// Drop-in replacement for StringComparer.OrdinalIgnoreCase on
    /// dictionaries and hashsets that store file paths.
    /// </summary>
    class PathComparer : IEqualityComparer<string>
    {
        public static readonly PathComparer Instance = new PathComparer();

        public bool Equals(string x, string y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            if (x.Length != y.Length) return false;
            for (int i = 0; i < x.Length; i++)
            {
                var cx = x[i] == '/' ? '\\' : x[i];
                var cy = y[i] == '/' ? '\\' : y[i];
                if (char.ToUpperInvariant(cx) != char.ToUpperInvariant(cy))
                    return false;
            }
            return true;
        }

        public int GetHashCode(string obj)
        {
            if (obj is null) return 0;
            unchecked
            {
                int hash = (int)2166136261;
                for (int i = 0; i < obj.Length; i++)
                {
                    var c = obj[i] == '/' ? '\\' : obj[i];
                    c = char.ToUpperInvariant(c);
                    hash = (hash ^ c) * 16777619;
                }
                return hash;
            }
        }
    }

    /// <summary>
    /// Tee writer — sends all Console.WriteLine output to both console and a log file.
    /// Thread-safe via lock on the file writer. WriteThrough ensures real-time tail -f.
    /// </summary>
    class TeeWriter : TextWriter
    {
        readonly TextWriter _console;
        readonly StreamWriter _file;
        readonly object _lock = new object();
        bool _disposed;

        public TeeWriter(TextWriter console, string logPath)
        {
            _console = console;
            _file = new StreamWriter(
                new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough),
                new UTF8Encoding(false))
            { AutoFlush = true };
        }

        public override Encoding Encoding => _console.Encoding;
        public override void Write(char value) { _console.Write(value); lock (_lock) { _file.Write(value); } }
        public override void Write(string? value) { _console.Write(value); lock (_lock) { _file.Write(value); } }
        public override void WriteLine(string? value) { _console.WriteLine(value); lock (_lock) { _file.WriteLine(value); } }
        public override void WriteLine() { _console.WriteLine(); lock (_lock) { _file.WriteLine(); } }

        /// <summary>Write a (possibly multi-line) block to the LOG FILE ONLY — the console
        /// never sees it. One call = one lock hold, so concurrent workers' blocks land in
        /// the log whole, never interleaved. Used for the high-volume --audit verdict
        /// traces that would flood the console at per-entry-per-save volume.</summary>
        public void FileOnly(string block)
        {
            lock (_lock)
            {
                if (_disposed) return;
                _file.WriteLine(block);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { lock (_lock) { if (!_disposed) { _disposed = true; _file.Flush(); _file.Dispose(); } } }
            base.Dispose(disposing);
        }
    }

    class Program
    {
        static int _analyzeCount;
        static long _analyzeTicksTotal;

        // ---- Scan telemetry (MoodsMode) ----
        // Per-outcome class stats: [0]=count, [1]=Stopwatch ticks of thread-time.
        // Shown in the end-of-scan breakdown; the cached/skip classes feed the ETA
        // model's "known tracks are near-free" term.
        static readonly ConcurrentDictionary<string, long[]> _classStats = new ConcurrentDictionary<string, long[]>();
        // Bytes fully analyzed (cache misses — the Essentia-dominated cost), total +
        // trailing-window events for the live MB/s rate on the progress line.
        static long _analyzedBytesTotal;
        static readonly ConcurrentQueue<(double AtSecs, long Bytes)> _rateEvents = new ConcurrentQueue<(double, long)>();
        const double RateWindowSecs = 120.0;
        // ETA model pre-flight (set by MoodsMode; -1 = model unavailable, naive ETA):
        // tracks absent from the existing catalog need the full Essentia pass; the
        // XML <Size> key supplies their byte total so remaining work is bytes-costed
        // at the measured rate instead of count-costed at a blended average.
        static int _etaNewTotal = -1;
        static int _etaNewDone;
        static long _etaNewBytesTotal;
        static long _etaNewBytesDone;
        static int _scanParallelism;
        // Duration-based ETA model: Essentia cost scales with AUDIO DURATION, not
        // file size (a 3-hour 64kbps podcast costs ~90x a 4-minute FLAC of the same
        // byte size). The XML Total Time supplies per-track durations up front; the
        // measured real-time-factor (analysis thread-seconds per audio-second) is
        // stable from the first completion, unlike the byte-rate window which is
        // hostage to whichever file sizes happen to finish first.
        static long _etaNewAudioMsTotal;
        static long _etaNewAudioMsDone;
        static long _rtfAnalyzeTicks;   // thread ticks, only for tracks with known duration
        static long _rtfAudioMs;        // matching audio-ms sum
        static long _rtfTrackCount;     // matching track count — lets the summary report average length

        /// <summary>The --help text. Shared by the two call sites (explicit --help, and the
        /// iTunes-XML-not-found fallback) so the blocks cannot drift apart. Essentials first;
        /// specialist modes grouped under Maintenance / Exclusions / Advanced.</summary>
        static void PrintHelp()
        {
            Console.WriteLine("Usage: truedat.exe [<path-to-iTunes-Music-Library.xml>] [options]");
            Console.WriteLine();
            Console.WriteLine("Zero-arg is the canonical run: truedat auto-discovers the iTunes XML (exe-dir parent,");
            Console.WriteLine("exe dir, then current directory) and runs the default full library scan, writing");
            Console.WriteLine("mbxmoods.json next to the XML (Essentia analysis, cache-aware).");
            Console.WriteLine();
            Console.WriteLine("Common:");
            Console.WriteLine($"  -p, --parallel      Parallel threads (default: {Math.Max(1, Environment.ProcessorCount - 2)} = cores-2, leaves 2 for foreground;");
            Console.WriteLine($"                      '-p max' uses all {Environment.ProcessorCount}, '-p N' sets exactly N)");
            Console.WriteLine("  --audit             Write all console output to truedat.log (for debugging)");
            Console.WriteLine("  --preview [path]    Write the scan work plan + review candidates to preview.json");
            Console.WriteLine("                      (MBXHub review folder by default; falls back to mbxmoods-preview.json");
            Console.WriteLine("                      beside the moods file when no MBXHub instance is found). Analyzes");
            Console.WriteLine("                      nothing, never writes mbxmoods.json. Refuses (exit 1, writes nothing)");
            Console.WriteLine("                      to overwrite an .xml destination or an existing mood catalog (JSON");
            Console.WriteLine("                      with a top-level \"tracks\" property).");
            Console.WriteLine("  --stats [path]      Read-only catalog summary: Essentia-analyzed count, hash coverage");
            Console.WriteLine("                      per kind, and SMFM track count. Path defaults to ./mbxmoods.json.");
            Console.WriteLine("                      Also printed at end of every scan. With --audit, written to the log.");
            Console.WriteLine("  --stats-detail N    List per-file status when a catalog has < N tracks (default 5).");
            Console.WriteLine("  --pause             Hold the console open at exit (press any key). For double-click");
            Console.WriteLine("                      launches; no effect on redirected/scheduled runs.");
            Console.WriteLine();
            Console.WriteLine("Maintenance:");
            Console.WriteLine("  --verify            Recompute audioStreamSha256 for each entry, report drift / missing.");
            Console.WriteLine("                      Read-only. Use --moods <path> to verify a specific file.");
            Console.WriteLine("  --backfill          With --verify: fill in missing fields for entries whose audio bytes are");
            Console.WriteLine("                      unchanged. Drifted entries are flagged, not modified. No Essentia re-run.");
            Console.WriteLine("                      Default fills BOTH tiers: identity (audioStreamSha256, fileMd5 with");
            Console.WriteLine("                      --file-md5, fingerprint.v1, bitDepth, encoder, mp3LameTag — TagLib +");
            Console.WriteLine("                      cheap IO) AND features (bitUsage, hfEnergyRatio, hfSpectralStructure —");
            Console.WriteLine("                      requires ffmpeg, ~30s per applicable track). --backfill-level scopes down.");
            Console.WriteLine("  --backfill-level    With --backfill: which tier runs. Values:");
            Console.WriteLine("                        all       (default) identity + features");
            Console.WriteLine("                        identity  fast tier only (TagLib + cheap file IO)");
            Console.WriteLine("                        features  ffmpeg tier only (bitUsage / hfEnergyRatio / hfSpectralStructure)");
            Console.WriteLine("  --accept-flac-tag-drift  With --verify --backfill: re-key FLAC entries whose stored sha");
            Console.WriteLine("                      predates the frame-anchored hash AND whose tags were rewritten after");
            Console.WriteLine("                      scan (audio props must still match) instead of REANALYZE_NEEDED.");
            Console.WriteLine("  --migrate           Clean up mbxmoods.json: strip legacy fields (valence/arousal, audioMd5,");
            Console.WriteLine("                      chromaprint) + fileMd5 (kept with --file-md5), rename SMFM keys");
            Console.WriteLine("                      (sensme*->smfm*). Creates a backup; never removes entries.");
            Console.WriteLine("  --fixup             Validate and remap paths in mbxmoods.json without re-analyzing.");
            Console.WriteLine("                      With --remap, performs a pure prefix swap (see --remap below).");
            Console.WriteLine("  --remap <old>=<new> With --fixup: wholesale prefix swap on mbxmoods.json keys. Pass the");
            Console.WriteLine("                      moods file as the positional arg (no iTunes XML needed). Example:");
            Console.WriteLine("                      truedat --fixup --remap \"D:\\Music\\=\\\\nas\\share\\Music\\\" mbxmoods.json");
            Console.WriteLine("  --duplicates [path] Read-only: exact (audioStreamSha256) + probable (cross-encode candidate)");
            Console.WriteLine("                      duplicate tiers, recommended keeper per group -> mbxmoods-duplicates.csv");
            Console.WriteLine("                      + .json, plus a self-contained interactive review page (see --html).");
            Console.WriteLine("  --losers-m3u [path] With --duplicates: write non-keeper members to an .m3u8 playlist for");
            Console.WriteLine("                      review/removal inside MusicBee (path must end in .m3u/.m3u8,");
            Console.WriteLine("                      default mbxmoods-duplicate-losers.m3u8)");
            Console.WriteLine("  --manifest [path]   With --duplicates: emit the kind:dupes review-surface manifest MBXHub's");
            Console.WriteLine("                      review.html renders directly. No path = auto-locate the running MusicBee");
            Console.WriteLine("                      instance and write to its <root>\\AppData\\MBXHub\\review\\dupes.json;");
            Console.WriteLine("                      pass a path to override.");
            Console.WriteLine("  --html [path]       --duplicates always writes the interactive review page (offline; printed");
            Console.WriteLine("                      as a clickable link) — include groups in chunks, pick keepers, build the");
            Console.WriteLine("                      losers playlist. --html <path> overrides where it lands (default");
            Console.WriteLine("                      mbxmoods-duplicates.html next to the moods file).");
            Console.WriteLine("  --merge-moods       Merge multiple mbxmoods.json files into one");
            Console.WriteLine("  --merge-source <path> Source moods file (repeatable, at least 2)");
            Console.WriteLine("  --merge-output <path> Output moods file path");
            Console.WriteLine();
            Console.WriteLine("Exclusions:");
            Console.WriteLine("  --exclusions <path> Use this exclusion file instead of mbxmoods-exclude.json beside the");
            Console.WriteLine("                      moods file");
            Console.WriteLine("  --no-exclusions     Ignore the exclusion file for this run (diagnostic; prints a warning)");
            Console.WriteLine("  --apply-exclusions <path>  Merge a decisions delta into the exclusion file (backs up");
            Console.WriteLine("                      first, reports changes)");
            Console.WriteLine("  --list-speech [path] Read-only: list entries whose verdict is speechLikely=yes ->");
            Console.WriteLine("                      mbxmoods-speech.csv (candidates for an exclusion rule via a decisions");
            Console.WriteLine("                      delta + --apply-exclusions; audio files untouched)");
            Console.WriteLine("  --list-missing-smfm [path]  Read-only: list entries with no Sony SMFM (12-TONE) data");
            Console.WriteLine("                      + coverage -> mbxmoods-smfm-missing.csv");
            Console.WriteLine();
            Console.WriteLine("Advanced:");
            Console.WriteLine("  --analyze           Run analysis mode (Essentia -> mbxmoods.json) — the default");
            Console.WriteLine("  --analyze-file <f>  Analyze a single audio file with Essentia (no iTunes XML needed)");
            Console.WriteLine("  --file-list <path>  Analyze files listed in a text file (one path per line, UTF-8, # comments)");
            Console.WriteLine("                      Use '-' as <path> to read paths from stdin instead of a file");
            Console.WriteLine("                      Mutually exclusive with --analyze-file / --folder; -p sets parallelism");
            Console.WriteLine("  --folder <dir>      Walk <dir> recursively for audio files and analyze them");
            Console.WriteLine("  --moods <path>      Moods file for the per-file modes / --verify / --stats (merge target");
            Console.WriteLine("                      for --analyze-file / --file-list / --folder; default mbxmoods.json");
            Console.WriteLine("                      next to the XML, else current directory)");
            Console.WriteLine("  --json-output       With --analyze-file / --file-list: emit the result / run summary as");
            Console.WriteLine("                      JSON on stdout");
            Console.WriteLine("  --chunk M/N         Process shard M of N for two-machine same-library scans. Output");
            Console.WriteLine("                      auto-suffixed with hostname: mbxmoods.<host>.json. Combine via --merge-moods.");
            Console.WriteLine("  --hash-only         Identity-only mode (no Essentia). Requires --level, --file-list, --output");
            Console.WriteLine("  --level <name>      With --hash-only: 'fingerprint' (cheap composite) or 'stream' (durable SHA-256)");
            Console.WriteLine("  --output <path>     --hash-only mode: append identity envelopes as NDJSON to <path> (offline manifest)");
            Console.WriteLine("  --max-duration <s>  Max track length in seconds for Essentia analysis (default 12000 = 200 min,");
            Console.WriteLine("                      the stock extractor's ChordsDetection buffer limit; pass 48000 when using");
            Console.WriteLine("                      the large-buffer extractor build — see essentia-build/OUTPUT-BUILDS.md)");
            Console.WriteLine("  --long-track-mins N Duration that flags a track for review in --preview (default 30).");
            Console.WriteLine("                      A review prompt only — it never excludes anything by itself.");
            Console.WriteLine("  --no-stage          Disable UNC source staging; workers read source directly");
            Console.WriteLine("  --stage-dir <path>  Override staging dir (default %TEMP%\\.truedat-stage)");
            Console.WriteLine("  --no-quick-cache    Disable the tags-only quick cache tier (head-64k check);");
            Console.WriteLine("                      mtime-drifted files always take the full audio-hash check");
            Console.WriteLine("  --no-bitusage       Suppress ComputeBitUsage (omits bitUsage JSON block)");
            Console.WriteLine("  --no-hf-analysis    Suppress ComputeHfAnalysis (omits hfEnergyRatio + hfSpectralStructure)");
            Console.WriteLine("  --file-md5          Maintain whole-file fileMd5 (default off: never written — nothing");
            Console.WriteLine("                      consumes it; audioStreamSha256 is the durable identity). Also gates");
            Console.WriteLine("                      the --backfill fileMd5 fill and the --migrate fileMd5 strip.");
            Console.WriteLine("  --retry-errors      Re-attempt all previously failed files (clears error log)");
            Console.WriteLine("  --refresh-features  Re-analyze entries missing the 2026-07-22 tonal/rhythm fields");
            Console.WriteLine("                      (keyVotes, bpm peaks, chords, tuning, averageLoudness) during a");
            Console.WriteLine("                      normal scan. Resumable (saves every 25 tracks); everything else");
            Console.WriteLine("                      stays cached. Run in sessions until coverage is complete.");
            Console.WriteLine("  --check-filenames   Scan paths for non-ASCII / problem chars + zero-byte / small files");
            Console.WriteLine("                      -> mbxhub-filenames.json");
            Console.WriteLine("  --transcode <in>    Standalone: ffmpeg-transcode <in> to uncompressed FLAC. Requires --transcode-out.");
            Console.WriteLine("  --transcode-out <p> Output FLAC path for --transcode mode.");
            Console.WriteLine("  --sample-rate <hz>  With --transcode: override output sample rate (default: match source).");
            Console.WriteLine("  --bit-depth <16|24> With --transcode: override output bit depth (default: match source).");
            Console.WriteLine("  --background        Run child processes with 25% CPU cap (won't starve foreground apps)");
            Console.WriteLine("  --cpu-limit <n>     Cap child process CPU to n% (1-100, e.g. 20 for low-end machines)");
            Console.WriteLine("  --synthesize        Generate a synthetic MusicBee library from a catalog");
            Console.WriteLine("  --catalog <path>    With --synthesize: path to catalog JSONL (.jsonl or .jsonl.gz)");
            Console.WriteLine("  --synth-output <dir> With --synthesize: output directory for synthesized library");
            Console.WriteLine("  --count <n>         With --synthesize: number of tracks to generate (default: 430000)");
            Console.WriteLine("  --album-ratio <r>   With --synthesize: fraction of tracks in albums vs singles (default: 0.5)");
            Console.WriteLine("  --synth-moods <path> With --synthesize: existing mbxmoods.json to merge into");
            Console.WriteLine("  --seed <n>          With --synthesize: random seed for reproducibility (default: 42)");
            Console.WriteLine("  --dry-run           With --synthesize: preview without writing files");
            Console.WriteLine("  --seed-moods        Seed mbxmoods.json from AcousticBrainz catalog (positional iTunes");
            Console.WriteLine("                      XML required; requires --seed-catalog)");
            Console.WriteLine("  --seed-catalog <path> With --seed-moods: path to synthlib-catalog.jsonl.gz");
            Console.WriteLine("  --seed-target <path>  With --seed-moods: target mbxmoods.json (default: next to library XML)");
            Console.WriteLine("  --self-test         Run inline FFT sanity checks and exit (no library scan)");
            Console.WriteLine("  --version, -v       Print version (1.0.0.0-[branch-]epoch) and exit");
            Console.WriteLine("  -?, -h, --help      Show this help");
            Console.WriteLine();
            Console.WriteLine("Optional: ffmpeg on PATH enables auto-downmix of multi-channel (5.1+) audio files.");
        }

        static void RecordTrackOutcome(string cls, long swTicks)
        {
            var a = _classStats.GetOrAdd(cls, _ => new long[2]);
            Interlocked.Increment(ref a[0]);
            Interlocked.Add(ref a[1], swTicks);
        }

        static void RecordAnalyzedBytes(double atSecs, long bytes)
        {
            if (bytes <= 0) return;
            Interlocked.Add(ref _analyzedBytesTotal, bytes);
            _rateEvents.Enqueue((atSecs, bytes));
        }

        /// <summary>Analyzed throughput over the trailing window, MB/s. 0 when no
        /// analysis completed recently (pure-cached stretches).</summary>
        static double CurrentRateMBps(double nowSecs)
        {
            while (_rateEvents.TryPeek(out var head) && nowSecs - head.AtSecs > RateWindowSecs)
                _rateEvents.TryDequeue(out _);
            long bytes = 0;
            int n = 0;
            double oldest = nowSecs;
            foreach (var e in _rateEvents) { bytes += e.Bytes; n++; if (e.AtSecs < oldest) oldest = e.AtSecs; }
            if (n == 0) return 0;
            // Span from the oldest surviving event, floored at 10s so a burst of
            // fresh completions doesn't read as an absurd rate, capped at the window.
            var span = Math.Min(Math.Max(nowSecs - oldest, 10.0), RateWindowSecs);
            return bytes / span / (1024.0 * 1024.0);
        }

        /// <summary>Avg thread-seconds across the near-free outcome classes (cached tiers + skips).</summary>
        static double KnownAvgThreadSecs()
        {
            long n = 0, ticks = 0;
            foreach (var kv in _classStats)
            {
                if (kv.Key.StartsWith("cached", StringComparison.Ordinal) || kv.Key.StartsWith("skip", StringComparison.Ordinal))
                {
                    n += Interlocked.Read(ref kv.Value[0]);
                    ticks += Interlocked.Read(ref kv.Value[1]);
                }
            }
            return n == 0 ? 0.0 : (double)ticks / Stopwatch.Frequency / n;
        }

        /// <summary>
        /// ETA in seconds. Two-class model when MoodsMode primed it: new-to-catalog
        /// tracks are bytes-costed at the measured analyzed MB/s (falling back to the
        /// avg Essentia time per track / parallelism), known tracks at the measured
        /// cache-tier average. Falls back to the naive blended average (elapsed/done ×
        /// remaining) when the model has no signal yet.
        /// </summary>
        /// <summary>Negative return = no honest estimate yet (analysis work ahead,
        /// nothing measured) — the caller omits the ETA segment rather than printing
        /// a misleading near-zero blended average.</summary>
        static double ComputeEtaSecs(TimeSpan elapsed, int done, int total)
        {
            var remaining = total - done;
            double naive = elapsed.TotalSeconds / done * remaining;
            if (_etaNewTotal < 0 || _scanParallelism <= 0) return naive;

            int newRemaining = Math.Max(0, Math.Min(_etaNewTotal - Volatile.Read(ref _etaNewDone), remaining));
            int knownRemaining = remaining - newRemaining;

            double newSecs;
            long rtfAudioMs = Interlocked.Read(ref _rtfAudioMs);
            long newAudioMsRemaining = Math.Max(0, _etaNewAudioMsTotal - Interlocked.Read(ref _etaNewAudioMsDone));
            long newBytesRemaining = Math.Max(0, _etaNewBytesTotal - Interlocked.Read(ref _etaNewBytesDone));
            var rate = CurrentRateMBps(elapsed.TotalSeconds);
            if (rtfAudioMs > 0 && newAudioMsRemaining > 0)
            {
                // Duration model (preferred): Essentia cost scales with audio length,
                // so the measured real-time-factor is meaningful from the very first
                // completion — a byte rate learned from small files wildly misprices
                // long low-bitrate audio (observed: 3h podcast queue costed at a rate
                // learned from two tiny samples → 10h ETA spike).
                double rtf = (double)Interlocked.Read(ref _rtfAnalyzeTicks) / Stopwatch.Frequency / (rtfAudioMs / 1000.0);
                newSecs = newAudioMsRemaining / 1000.0 * rtf / _scanParallelism;
            }
            else if (rate > 0.01 && newBytesRemaining > 0)
                newSecs = newBytesRemaining / (1024.0 * 1024.0) / rate;
            else if (Volatile.Read(ref _analyzeCount) > 0)
                newSecs = newRemaining * ((double)Interlocked.Read(ref _analyzeTicksTotal) / Stopwatch.Frequency / _analyzeCount) / _scanParallelism;
            else if (newRemaining > 0)
                return -1;  // analysis ahead, nothing measured — omit rather than mislead
            else
                newSecs = 0;

            double knownSecs = knownRemaining * KnownAvgThreadSecs() / _scanParallelism;
            return newSecs + knownSecs;
        }
        static readonly Lazy<string?> _ffmpegPath = new Lazy<string?>(FindFfmpeg);
        static readonly Lazy<string?> _ffprobePath = new Lazy<string?>(FindFfprobe);

        static bool _audit;
        // The active --audit tee (stdout -> console + truedat.log), when one is installed.
        // Lets ComputeTruedatVerdict route its per-entry signal traces to the log FILE
        // only (TeeWriter.FileOnly) — at per-entry-per-save volume they'd flood the
        // console, and Console.Error wasn't tee'd so the detail never reached the log.
        static TeeWriter? _tee;
        internal static StageOptions _stageOpts = new StageOptions();
        // CLI signal-disable toggles live on StageOptions (siblings of the staging
        // knobs they share an end-of-scan cost profile with). Loose static bools
        // remain as forwarding properties for the few call sites that haven't been
        // threaded yet — touching one source of truth is enough.
        internal static bool _noBitUsage   { get => _stageOpts.NoBitUsage;   set => _stageOpts.NoBitUsage   = value; }
        internal static bool _noHfAnalysis { get => _stageOpts.NoHfAnalysis; set => _stageOpts.NoHfAnalysis = value; }

        // Tier-1.5 tags-only quick cache (head-64k evidence check on mtime drift).
        // Default ON; --no-quick-cache forces the full audio-hash tier instead.
        internal static bool _quickCache = true;

        // Explicit scan-exclusion file. Default location is beside the moods file;
        // --exclusions overrides it, --no-exclusions ignores it for one run.
        internal static string? _exclusionsPath;
        internal static bool _noExclusions;

        /// <summary>The loaded exclusion rules — the ONLY authority over what a scan skips
        /// for policy reasons. Empty until a scan mode loads it, so every other mode and
        /// every self-test starts from "nothing excluded".</summary>
        internal static ExclusionSet _exclusions = ExclusionSet.Empty;

        // --refresh-features: widen the re-extract canary so entries missing the
        // 2026-07-22 tonal/rhythm wave (keyVotes / bpm peaks / chords / tuning /
        // averageLoudness) re-analyze during a normal cache-aware scan. Off by
        // default — the wave is deliberately NOT in the base canary so existing
        // caches stay valid. Opt in per session to chew through coverage
        // incrementally (resumable: progress saves every 25 analyzed tracks).
        internal static bool _refreshFeatures;

        // --accept-flac-tag-drift (--verify --backfill only): re-key FLAC entries
        // whose stored sha predates the flac-frames algorithm AND whose tags were
        // rewritten after scan (both compares fail — the stored hash covered
        // metadata bytes that no longer exist). Gated on audio props still matching
        // the stored fingerprint. Opt-in because it trades certainty for avoiding a
        // mass re-analysis: a genuine same-props audio swap would slip through.
        internal static bool _acceptFlacTagDrift;

        // Whole-file MD5 maintenance. Default OFF: truedat never WRITES fileMd5 —
        // the worker fan-out task, tier-1 null backfill, tier-2/4 refreshes, and
        // --backfill Tier A all skip it, and --migrate strips stored values.
        // Nothing consumes fileMd5 (MBXHub indexes audioStreamSha256 only);
        // --file-md5 restores full maintenance for external-interop use.
        internal static bool _fileMd5Enabled = false;

        static bool _losersM3u = false;
        static string? _losersM3uPath = null;
        // --manifest [path]: emit the kind:dupes review-surface manifest that MBXHub's
        // review.html renders directly (no PowerShell producer in the middle). Default
        // path = mbxmoods-duplicates.manifest.json next to the moods file; pass a path
        // to drop it straight into <MBXHub AppData>\review\dupes.json.
        static bool _manifest = false;
        static string? _manifestPath = null;
        // --html [path]: emit a self-contained interactive review page (embedded data,
        // inline JS/CSS, offline). Pick keepers per group, click "Build losers playlist"
        // to download the .m3u8. Default mbxmoods-duplicates.html next to the moods file.
        // The interactive review page is a DEFAULT output of --duplicates (always written +
        // printed as a clickable console link). --html <path> only overrides where it lands.
        static string? _htmlPath = null;

        // Per-process cache: drive root -> isNetwork. DriveInfo.DriveType is a Win32
        // call (~µs per hit but it touches the mount table). One lookup per unique
        // root, then memoized. Concurrent-safe; readers don't lock.
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _networkDriveCache
            = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // Source-staging telemetry. Set during the per-track fan-out, summarised
        // at end-of-scan. Reset at start of each scan mode.
        internal static int _stageSuccessCount;
        internal static int _stageFallbackCount;


        // Essentia streaming ChordsDetection buffer limit caps analyzable track length.
        // Unpatched extractor (essentia-build/output-x64.1): 262144-element buffer
        // (forMultipleFrames) -> at 44100 Hz / 2048 hop = ~21.53 frames/sec the buffer
        // overflows at ~12172s; default 12000s (200 min) leaves margin.
        // Patched extractor (output-x64.2, forLargeAudioStream = 1048576 elements):
        // ceiling ~48695s -> pass --max-duration 48000 (~13.3 h) when running against it.
        // See essentia-build/OUTPUT-BUILDS.md. Overridable via --max-duration <secs>.
        static int _maxEssentiaDurationSecs = 12000;
        // True once --max-duration is passed explicitly, so --preview's manifest can report
        // whether the ceiling came from a flag or the built-in default rather than guessing.
        internal static bool _maxDurationExplicit;

        // --preview's review-candidate cap. No flag for this deliberately — the review page
        // (Phase 2b) pages client-side, so there's no operator-facing reason to raise it.
        const int PreviewReviewCap = 500;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int GetShortPathName(string lpszLongPath, StringBuilder lpszShortPath, int cchBuffer);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        // Job Object APIs for CPU rate limiting child processes
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        const int JobObjectCpuRateControlInformation = 15;
        const uint JOB_OBJECT_CPU_RATE_CONTROL_ENABLE = 0x1;
        const uint JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x4;

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            public uint ControlFlags;
            public uint CpuRate; // in hundredths of a percent (e.g. 2000 = 20%)
        }

        static IntPtr _jobHandle = IntPtr.Zero;

        /// <summary>
        /// Create a Job Object with a hard CPU rate cap. Call once during init.
        /// cpuPercent: 1-100, e.g. 20 means child processes share 20% of total CPU.
        /// </summary>
        static void InitCpuLimitJob(int cpuPercent)
        {
            _jobHandle = CreateJobObject(IntPtr.Zero, null);
            if (_jobHandle == IntPtr.Zero) return;

            var info = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
            {
                ControlFlags = JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
                CpuRate = (uint)(cpuPercent * 100) // convert percent to hundredths-of-percent
            };
            int size = Marshal.SizeOf<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(_jobHandle, JobObjectCpuRateControlInformation, ptr, (uint)size))
                {
                    Console.Error.WriteLine($"  Warning: Failed to set CPU rate limit (error {Marshal.GetLastWin32Error()})");
                    _jobHandle = IntPtr.Zero;
                }
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        /// <summary>Assign a process to the CPU-limited job. No-op if no job configured.</summary>
        static void ApplyCpuLimit(Process proc)
        {
            if (_jobHandle != IntPtr.Zero)
                AssignProcessToJobObject(_jobHandle, proc.Handle);
        }

        /// <summary>
        /// Convert non-ASCII paths to 8.3 short form for Essentia compatibility.
        /// Essentia's C++ main() receives paths in the system ANSI code page, which
        /// can't represent characters outside that code page (fullwidth ⧸ ： ＂, CJK, etc.).
        /// 8.3 short names are pure ASCII and work universally.
        /// If 8.3 is unavailable, the path is passed through — accented Latin chars
        /// survive ANSI fine; truly unsupported chars will fail in Essentia and appear
        /// in the errors CSV.
        /// </summary>
        static string SafePath(string path)
        {
            for (int i = 0; i < path.Length; i++)
                if (path[i] > 127) goto needsShortPath;
            return path;

            needsShortPath:
            try
            {
                var sb = new StringBuilder(260);
                var result = GetShortPathName(path, sb, sb.Capacity);
                if (result > sb.Capacity)
                {
                    sb.Capacity = result + 1;
                    result = GetShortPathName(path, sb, sb.Capacity);
                }
                if (result > 0 && result <= sb.Capacity)
                    return sb.ToString();
            }
            catch { }
            return path;
        }

        /// <summary>How long a scratch file must have gone untouched before the startup sweep
        /// will treat it as an orphan. Deliberately generous: a staged copy or a downmix WAV is
        /// held open for the whole of one track's analysis, so an eager sweep deletes a LIVE
        /// run's working file — and that surfaces as an analysis failure attributed to the
        /// track, in mbxmoods-errors.csv, looking exactly like a corrupt file. Leaving a
        /// handful of genuine orphans for one more run costs nothing (the stage dir is bounded
        /// by the worker count); deleting a live file costs a mis-diagnosed bug report.</summary>
        internal static readonly TimeSpan OrphanMinAge = TimeSpan.FromMinutes(60);

        /// <summary>Age gate for the startup sweep. Anything written recently might belong to a
        /// sibling invocation this process cannot see, so it is left alone.</summary>
        internal static bool IsSweepableOrphan(DateTime lastWriteUtc, DateTime nowUtc)
            => nowUtc - lastWriteUtc >= OrphanMinAge;

        /// <summary>
        /// Whether the startup sweep may run at all. Truedat is single-instance BY DESIGN
        /// (CLAUDE.md), but nothing enforced it and nothing warned: the sweep cannot tell a live
        /// sibling's staged files from a dead run's, so with a sibling running the only correct
        /// action is not to sweep. Orphans are not urgent — they are collected by the next run
        /// that has the place to itself.
        /// </summary>
        internal static bool ShouldSweepOrphans(int siblingCount) => siblingCount == 0;

        /// <summary>Other truedat processes on this machine. Best-effort: process enumeration can
        /// be blocked, and a blocked lookup returns 0 so the sweep still happens — the age gate
        /// is the backstop for that case, which is why both exist.</summary>
        internal static int CountSiblingTruedatProcesses()
        {
            try
            {
                int me = Process.GetCurrentProcess().Id;
                var name = Process.GetCurrentProcess().ProcessName;
                int n = 0;
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { if (p.Id != me) n++; } catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
                return n;
            }
            catch { return 0; }
        }

        /// <summary>Delete aged-out files matching a pattern; returns how many were left alone
        /// because they were too recent. A non-zero return means the directory itself must be
        /// kept — something may still be using it.</summary>
        static int SweepAged(string dir, string pattern, string label)
        {
            int skipped = 0, deleted = 0;
            var now = DateTime.UtcNow;
            foreach (var f in Directory.GetFiles(dir, pattern))
            {
                try
                {
                    if (!IsSweepableOrphan(File.GetLastWriteTimeUtc(f), now)) { skipped++; continue; }
                    File.Delete(f);
                    deleted++;
                }
                catch { skipped++; }
            }
            if (deleted > 0) Console.WriteLine($"  Cleaning {deleted} orphaned {label} from {dir}");
            if (skipped > 0)
                Console.WriteLine($"  Left {skipped} recent file(s) in {dir} alone (younger than {OrphanMinAge.TotalMinutes:F0} min — may belong to a running scan)");
            return skipped;
        }

        /// <summary>
        /// Startup orphan sweep. Truedat is single-instance by design — the supported model for
        /// cross-machine scaling is `--chunk M/N`, not concurrent same-library runs — and this
        /// sweep is why: it cannot distinguish a live sibling's working files from a dead run's
        /// leftovers.
        ///
        /// Two guards, because each covers the other's blind spot (I-3):
        ///  * a sibling truedat process means don't sweep AT ALL, and say so;
        ///  * an age gate, because process enumeration can be blocked and because a sibling on
        ///    another machine sharing a stage dir is invisible to any local check.
        ///
        /// --stage-dir now isolates two of the three targets: the staged copies and the downmix
        /// WAVs both live under it. The third — `&lt;drive&gt;\.truedat-tmp` hardlinks — cannot be
        /// relocated, because a hardlink must sit on the same volume as its source. That one
        /// relies on the two guards above, and is the reason they are not optional.
        /// </summary>
        static void CleanupOrphanedFiles()
        {
            int siblings = CountSiblingTruedatProcesses();
            if (!ShouldSweepOrphans(siblings))
            {
                Console.Error.WriteLine($"  WARNING: {siblings} other truedat process(es) running — skipping the orphan sweep.");
                Console.Error.WriteLine("           Truedat is single-instance by design; concurrent runs on one library are not supported");
                Console.Error.WriteLine("           (use --chunk M/N across machines). Sweeping now could delete the other run's working files.");
                return;
            }

            // Hardlinks from .truedat-tmp on every ready drive. NOT relocatable by --stage-dir:
            // a hardlink cannot cross volumes, so it has to live on the source's own drive.
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady) continue;
                    var tmpDir = Path.Combine(drive.RootDirectory.FullName, ".truedat-tmp");
                    if (!Directory.Exists(tmpDir)) continue;
                    try
                    {
                        // Only remove the directory when nothing was left behind — a kept file
                        // means something may still be using this dir.
                        if (SweepAged(tmpDir, "*", "hardlink(s)") == 0)
                            try { Directory.Delete(tmpDir); } catch { }
                    }
                    catch { }
                }
            }
            catch { }

            // Downmix WAVs and staged copies both live under the stage dir now, so one sweep
            // covers both and --stage-dir isolates both.
            try
            {
                var stageDir = _stageOpts.StageDir;
                if (Directory.Exists(stageDir))
                {
                    // One pass, not one per kind: a second glob over the same directory would
                    // report the same recent file twice in the "left alone" line.
                    if (SweepAged(stageDir, "*", "staged/downmix file(s)") == 0)
                        try { Directory.Delete(stageDir); } catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// Does this shard own <paramref name="path"/>? Hash-mod over PathComparer's FNV-1a,
        /// which is deterministic across processes and machines, separator-normalized and
        /// case-folded — so the same path lands in the same bucket everywhere regardless of XML
        /// differences. `chunkTotal &lt;= 0` means "not sharding", i.e. this run owns everything.
        ///
        /// ONE implementation, used by both the work filter and the ledger scope (I-4). If those
        /// two ever disagreed, a shard would either ledger work it never did or do work it never
        /// ledgered.
        /// </summary>
        internal static bool ChunkOwns(string? path, int chunkIndex, int chunkTotal)
        {
            if (chunkTotal <= 0) return true;
            if (string.IsNullOrEmpty(path)) return false;
            return (PathComparer.Instance.GetHashCode(path!) & 0x7FFFFFFF) % chunkTotal == chunkIndex - 1;
        }

        /// <summary>
        /// Which dropped tracks this run puts in its OWN mbxmoods-skipped.csv (I-4).
        ///
        /// The four pre-scan filters run before the chunk filter, deliberately: exclusion
        /// evaluation has to see the whole library so per-rule hit counts are library-wide and
        /// identical on every machine, which is what makes the stale-rule signal trustworthy.
        /// But the LEDGER is a per-run artifact — its filename is already hostname-suffixed and
        /// every other row in it (missing, error, structural) is shard-scoped. Writing the whole
        /// library's exclusion rows into each shard's copy gave one CSV two different scopes and
        /// N duplicates of every excluded track across an N-machine run, with --merge-moods
        /// reconciling none of it.
        ///
        /// So: evaluate library-wide, ledger shard-scoped. Rule hit counts stay library-wide and
        /// say so on the console.
        /// </summary>
        static Func<string?, bool> _ledgerScope = _ => true;
        /// <summary>Suffix that tells the operator a printed count is this shard's share, not the
        /// library's. Empty when not sharding, so an ordinary scan reads exactly as before.</summary>
        static string _ledgerScopeNote = "";

        /// <summary>Drop tracks an exclusion rule excludes. Every drop is ledgered with the
        /// rule that caused it — the whole point of the mechanism is that an absent track is
        /// explainable, so a bare count is not enough. An include rule's job is simply to
        /// keep IsExcluded from matching for that track (handled internally by
        /// ExclusionSet.IsExcluded) and it never resurrects a video, a stream URL or an
        /// over-length file — those structural filters run independently.</summary>
        static List<ITunesTrack> FilterExclusions(List<ITunesTrack> tracks, string? skippedPath = null)
        {
            if (_exclusions.IsEmpty) return tracks;
            var kept = new List<ITunesTrack>(tracks.Count);
            int removed = 0;
            foreach (var t in tracks)
            {
                if (string.IsNullOrEmpty(t.Location)) { kept.Add(t); continue; }
                // IsExcluded runs for EVERY track, in or out of this shard's bucket — it is what
                // increments per-rule MatchCount, and those counts must stay library-wide.
                // Only the ledger row and the count below are shard-scoped.
                if (_exclusions.IsExcluded(t.Location!, t.Genre, out var reason))
                {
                    if (_ledgerScope(t.Location))
                    {
                        removed++;
                        if (skippedPath != null)
                            AppendSkipped(skippedPath, t.Location!, GetExtensionSafe(t.Location!), $"excluded (rule: {reason})");
                        if (_audit)
                            Console.WriteLine($"  [skipped excluded: {reason}] {t.Artist} - {t.Name} :: {t.Location}");
                    }
                    continue;
                }
                kept.Add(t);
            }
            if (removed > 0)
                Console.WriteLine($"  Excluded {removed} track(s) by rule{_ledgerScopeNote}"
                    + (skippedPath != null ? $" — listed in {Path.GetFileName(skippedPath)}" : "")
                    + (_audit ? "" : " (--audit lists each)"));
            // Per-rule hit counts, always when rules exist: a rule matching zero tracks is
            // how a renamed folder or retagged genre announces itself instead of quietly
            // doing nothing. This is the diagnostic the previous heuristics never had.
            foreach (var rule in _exclusions.Rules)
                Console.WriteLine($"    {rule.Describe()}: {rule.MatchCount} matched"
                    + (rule.MatchCount == 0 ? "   (stale rule?)" : ""));
            // Under --chunk the two numbers above deliberately disagree, so say why rather than
            // leaving the operator to reconcile "Excluded 93" against "genre=Podcast: 374".
            if (_ledgerScopeNote.Length > 0 && !_exclusions.IsEmpty)
                Console.WriteLine("    (rule counts are library-wide and identical on every chunk; the ledger holds this chunk's share)");
            return kept;
        }

        /// <summary>
        /// The mbxmoods-skipped.csv reason for a path the scan cannot find. Shared by
        /// --file-list/--folder and --analyze-file so the two cannot word the same ledger row
        /// differently. The MAX_PATH call-out is the useful half: "file not found" on a
        /// 300-character path is almost always a length problem, not a deletion, and the
        /// operator needs to be told which.
        /// </summary>
        internal static string MissingFileSkipReason(string path)
            => (path ?? "").Length >= 260
                ? $"file not found (path {path!.Length} chars >= 260 MAX_PATH)"
                : "file not found";

        /// <summary>Path.GetExtension that never throws on invalid path chars (XML can carry anything).</summary>
        internal static string GetExtensionSafe(string path)
        {
            try { return Path.GetExtension(path) ?? ""; } catch { return ""; }
        }

        /// <summary>
        /// A missing exclusion file is only benign at the DEFAULT (beside-the-moods-file)
        /// location — that's a legitimate fresh install with nothing excluded yet. An
        /// operator-named path via --exclusions is a deliberate ask; if it doesn't exist
        /// that's almost always a typo, and ExclusionStore.Load's "missing = empty, no
        /// error" contract would otherwise scan with silently zero exclusions applied.
        /// Called once per invocation, immediately before the ExclusionStore.Load call,
        /// at all three load sites (--analyze-file, --file-list/--folder, MoodsMode).
        /// </summary>
        static bool ValidateExplicitExclusionsPath(out string? error)
        {
            error = null;
            if (_exclusionsPath != null && !File.Exists(_exclusionsPath))
            {
                error = $"--exclusions path not found: {_exclusionsPath}";
                return false;
            }
            return true;
        }

        /// <summary>Re-extract canary, single source of truth for every cache tier.
        /// Base: DynamicRange + LoudnessMomentary present (pre-LRA / pre-extended
        /// builds re-analyze). Under --refresh-features, entries lacking the
        /// 2026-07-22 tonal/rhythm wave also count as stale — averageLoudness is
        /// the marker because the extractor emits it unconditionally.</summary>
        static bool HasCurrentFeatures(TrackFeatures? f)
            => f != null && f.DynamicRange.HasValue && f.LoudnessMomentary.HasValue
               && (!_refreshFeatures || f.AverageLoudness.HasValue);

        /// <summary>Convert a path to the \\?\ extended-length form so managed IO on
        /// net48 can reach files beyond MAX_PATH (260). Used as a per-track fallback
        /// for over-length paths only — never for identity, cache keys, or display.</summary>
        internal static string ExtendedLengthPath(string path)
        {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
            return path.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + path.Substring(2)   // \\server\share\… -> \\?\UNC\server\share\…
                : @"\\?\" + path;
        }

        /// <summary>Video file extensions that should not be analyzed as audio.</summary>
        internal static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".m4v", ".mkv", ".avi", ".wmv", ".mov", ".webm", ".flv", ".mpg", ".mpeg", ".vob", ".ts"
        };

        /// <summary>Remove video files from a parsed track list. Dropped tracks land in
        /// mbxmoods-skipped.csv (when a path is supplied); --audit lists each on console.</summary>
        static List<ITunesTrack> FilterVideoFiles(List<ITunesTrack> tracks, string? skippedPath = null)
        {
            bool IsVideo(ITunesTrack t) => !string.IsNullOrEmpty(t.Location)
                && VideoExtensions.Contains(GetExtensionSafe(t.Location));
            return FilterByPredicate(tracks, IsVideo, "video file(s)", "video file extension", skippedPath);
        }

        /// <summary>Shared drop-and-ledger tail for the pre-scan extension filters.</summary>
        static List<ITunesTrack> FilterByPredicate(List<ITunesTrack> tracks, Func<ITunesTrack, bool> drop,
            string label, string reason, string? skippedPath)
        {
            var removed = tracks.Where(drop).ToList();
            if (removed.Count == 0) return tracks;
            // Removal is library-wide (a video in another shard's bucket is not this shard's work
            // either way); only the LEDGER is shard-scoped, so an N-machine run does not write N
            // copies of every structural skip row (I-4).
            int ledgered = 0;
            foreach (var t in removed)
            {
                if (!_ledgerScope(t.Location)) continue;
                ledgered++;
                if (skippedPath != null)
                    AppendSkipped(skippedPath, t.Location, GetExtensionSafe(t.Location), reason);
                if (_audit)
                    Console.WriteLine($"  [skipped {reason}] {t.Artist} - {t.Name} :: {t.Location}");
            }
            if (ledgered > 0)
                Console.WriteLine($"  Skipped {ledgered} {label}{_ledgerScopeNote}"
                    + (skippedPath != null ? $" — listed in {Path.GetFileName(skippedPath)}" : "")
                    + (_audit ? "" : " (--audit lists each)"));
            return tracks.Where(t => !drop(t)).ToList();
        }

        /// <summary>Playlist / redirector file extensions that point at audio but
        /// aren't audio themselves. Essentia wastes time on them and
        /// produces empty / nonsense output. Distinct from VideoExtensions so the
        /// log line stays accurate.</summary>
        internal static readonly HashSet<string> NonAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".asx",   // Advanced Stream Redirector (Windows Media)
            ".m3u",   // M3U playlist
            ".m3u8",  // M3U UTF-8 playlist
            ".pls",   // PLS playlist
            ".wpl",   // Windows Media Player playlist
            ".cue",   // CUE sheet (track index for a single audio file)
            ".xspf"   // XSPF playlist
        };

        /// <summary>
        /// File extensions for audio formats Essentia cannot decode. Surfaced as a
        /// distinct "unsupported format" bucket by --check-filenames so users can
        /// see them up-front rather than discovering analysis failures one-by-one.
        /// Also filtered out during --folder enumeration and skipped at scan entry
        /// by --analyze-file, --file-list, and MoodsMode (Phase 5.1) — rows land
        /// in mbxmoods-skipped.csv with reason "unsupported codec: DSD".
        /// </summary>
        static readonly HashSet<string> UnsupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".dsf",   // Sony DSD format
            ".dff",   // Philips DSDIFF format
            ".dsd"    // raw DSD stream
        };

        /// <summary>
        /// Audio file extensions used by --folder enumeration to filter out
        /// non-audio content (cover art, .nfo, .log, .txt, sidecar files, etc.).
        /// Conservative allowlist — anything not on this list is silently skipped.
        /// </summary>
        static readonly HashSet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".m4a", ".m4b", ".aac",
            ".flac", ".ogg", ".oga", ".opus",
            ".wma", ".wav", ".aiff", ".aif",
            ".ape", ".mpc", ".wv"
        };

        /// <summary>Remove playlist / redirector files from a parsed track list and log the count.</summary>
        static List<ITunesTrack> FilterNonAudio(List<ITunesTrack> tracks, string? skippedPath = null)
        {
            bool IsNonAudio(ITunesTrack t) => !string.IsNullOrEmpty(t.Location)
                && NonAudioExtensions.Contains(GetExtensionSafe(t.Location));
            return FilterByPredicate(tracks, IsNonAudio, "playlist / redirector file(s)",
                "playlist / redirector extension", skippedPath);
        }

        /// <summary>Drop remote-URL entries (un-downloaded podcast-feed episodes whose
        /// XML Location is the stream URL). Categorically unscannable — truedat
        /// analyzes files — and numerous enough to distort the ETA pre-flight and
        /// flood the missing ledger every run if allowed through.</summary>
        static List<ITunesTrack> FilterRemoteUrls(List<ITunesTrack> tracks, string? skippedPath = null)
        {
            return FilterByPredicate(tracks, t => t.IsRemote, "remote stream URL(s)",
                "remote stream URL (not a file)", skippedPath);
        }

        static void Main(string[] args)
        {
            // --pause: opt-in "hold the console open at exit" for double-click /
            // shortcut launches whose window would close before the summary can be
            // read. Wrapper-level so it fires on EVERY exit path (returns, error
            // exits, exceptions). ReadKey throws when input is redirected
            // (scheduler / piped runs) — swallowed, so automation never hangs.
            bool pause = args.Any(a =>
                string.Equals(a, "--pause", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a, "/pause", StringComparison.OrdinalIgnoreCase));
            try { MainCore(args); }
            finally
            {
                if (pause)
                {
                    Console.WriteLine();
                    Console.Write("Done - press any key to close...");
                    try { Console.ReadKey(true); } catch { }
                }
            }
        }

        static void MainCore(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            // Default leaves 2 cores for the foreground so a background scan isn't a hog.
            // `-p max` or an explicit `-p N` (including N > core count) overrides this.
            var parallelism = Math.Max(1, Environment.ProcessorCount - 2);
            string? xmlPath = null;
            bool fixupMode = false;
            string? remapPrefix = null;  // --remap "<old>=<new>" with --fixup: wholesale prefix swap, no XML lookup
            bool verifyMode = false;
            bool statsMode = false;   // --stats: read-only catalog summary over mbxmoods.json
            int statsDetailThreshold = 5;  // --stats-detail N: list per-file when catalog has < N tracks
            bool listSpeechMode = false;   // --list-speech: read-only list of speechLikely=="yes" entries (candidates for an exclusion rule; JSON-only, no --preview/XML needed)
            bool listSmfmMissingMode = false;  // --list-missing-smfm: read-only list of entries with no Sony 12-TONE data
            bool previewMode = false;          // --preview: work plan + review candidates. Read-only over the CATALOG (analyzes nothing, writes no mbxmoods.json, never touches the exclusion file) — but it DOES write preview.json + the page into the review folder (I-6).
            string? previewOutPath = null;     // optional explicit destination for preview.json
            int longTrackMins = 30;            // --long-track-mins: review prompt threshold, NOT a rule
            bool applyExclusionsMode = false;   // --apply-exclusions <path>: merge a decisions delta into mbxmoods-exclude.json
            string? applyExclusionsPath = null;
            bool verifyBackfill = false;
            // --backfill-level identity|features|all (default: all). Identity = TagLib-only
            // fields (fileMd5, fingerprint.v1 sub-fields, mp3LameTag). Features = ffmpeg-
            // dependent fields (bitUsage, hfEnergyRatio, hfSpectralStructure). All = both.
            BackfillLevel backfillLevel = BackfillLevel.All;
            bool retryErrors = false;
            bool migrateMode = false;
            bool auditLog = false;
            bool checkFilenames = false;
            bool duplicatesMode = false;
            bool analyzeMode = false;

            bool synthesize = false;
            string? synthCatalog = null;
            string? synthOutput = null;
            int synthCount = 430_000;
            double synthAlbumRatio = 0.5;
            string? synthMoods = null;
            int synthSeed = 42;
            bool synthDryRun = false;

            bool seedMoods = false;
            string? seedCatalog = null;
            string? seedTarget = null;

            bool mergeMode = false;
            var mergeSources = new List<string>();
            string? mergeOutput = null;

            bool analyzeFileMode = false;
            string? analyzeFilePath = null;
            string? analyzeFileMoods = null;
            bool jsonOutput = false;

            bool fileListMode = false;
            string? fileListPath = null;

            // --folder <dir> walks dir recursively for audio files (per AudioExtensions
            // allowlist) and feeds them into the same worker pool as --file-list.
            // Mutually exclusive with --file-list and --analyze-file.
            bool folderMode = false;
            string? folderPath = null;

            bool hashOnlyMode = false;
            string? hashLevel = null;

            // --transcode <in> --transcode-out <out.flac> [--sample-rate N] [--bit-depth N]
            // Standalone utility mode: ffmpeg-driven opus/other -> FLAC conversion.
            // No essentia, no cache, no mbxmoods.json. Overrides default to source props.
            bool transcodeMode = false;
            string? transcodeInput = null;
            string? transcodeOutput = null;
            int transcodeSampleRate = 0; // 0 = match source
            int transcodeBitDepth = 0;   // 0 = match source

            // hashOutputPath: --hash-only mode only — NDJSON manifest file the
            // identity envelopes are appended to. Enables offline determinism rigs
            // without standing up a fake HTTP server.
            string? hashOutputPath = null;

            bool showHelp = false;
            bool selfTest = false;
            int cpuLimit = 0; // 0 = no limit

            // --chunk M/N: split the scan list across machines. Two boxes with the
            // same library run `--chunk 1/2` and `--chunk 2/2` and produce two
            // non-overlapping output files keyed by hostname. Sort + range-slice
            // after all filters; chunking is invisible to the cache logic.
            int chunkIndex = 0; // 0 = chunking disabled
            int chunkTotal = 0;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                // Windows-friendly flag normalization: accept `-foo`, `/foo`, AND
                // legacy `--foo`. The matchers below all compare against the bare
                // name so authors don't have to repeat themselves three times.
                // Positional args (no leading dash/slash) bypass this and stay raw.
                string canonical;
                if (arg.StartsWith("--"))      canonical = arg.Substring(2);
                else if (arg.Length > 1 && (arg[0] == '-' || arg[0] == '/')) canonical = arg.Substring(1);
                else                            canonical = arg;

                if (canonical == "?" || canonical == "h" || canonical == "help")
                    showHelp = true;
                else if (canonical == "fixup") fixupMode = true;
                else if (canonical == "remap" && i + 1 < args.Length) remapPrefix = args[++i];
                else if (canonical == "verify") verifyMode = true;
                else if (canonical == "stats") statsMode = true;
                else if (canonical == "stats-detail" && i + 1 < args.Length && int.TryParse(args[i + 1], out var sdt) && sdt >= 0) { statsDetailThreshold = sdt; i++; }
                else if (canonical == "list-speech") listSpeechMode = true;
                else if (canonical == "list-missing-smfm") listSmfmMissingMode = true;
                else if (canonical == "backfill") verifyBackfill = true;
                else if (canonical == "backfill-level" && i + 1 < args.Length)
                {
                    var lvl = args[++i].ToLowerInvariant();
                    backfillLevel = lvl switch
                    {
                        "identity" => BackfillLevel.Identity,
                        "features" => BackfillLevel.Features,
                        "all" => BackfillLevel.All,
                        _ => BackfillLevel.All,
                    };
                }
                else if (canonical == "retry-errors") retryErrors = true;
                else if (canonical == "migrate") migrateMode = true;
                else if (canonical == "analyze") analyzeMode = true;
                else if (canonical == "audit") auditLog = true;
                else if (canonical == "check-filenames") checkFilenames = true;
                else if (canonical == "duplicates") duplicatesMode = true;
                else if (canonical == "losers-m3u")
                {
                    _losersM3u = true;
                    // Only claim the next token when it names a playlist — otherwise it's the
                    // positional library path and eager binding would hijack it (and the writer
                    // would clobber that file with playlist text).
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-")
                        && (args[i + 1].EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)
                            || args[i + 1].EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)))
                    { _losersM3uPath = args[i + 1]; i++; }
                }
                else if (canonical == "manifest")
                {
                    _manifest = true;
                    // Claim the next token as the output path only when it isn't the positional
                    // library/moods arg (must look like a file path, not a flag).
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-") && !args[i + 1].StartsWith("/")
                        && args[i + 1].IndexOf(Path.DirectorySeparatorChar) >= 0)
                    { _manifestPath = args[i + 1]; i++; }
                    else if (i + 1 < args.Length && !args[i + 1].StartsWith("-")
                        && args[i + 1].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    { _manifestPath = args[i + 1]; i++; }
                }
                else if (canonical == "html")
                {
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-")
                        && args[i + 1].EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                    { _htmlPath = args[i + 1]; i++; }
                }
                else if ((canonical == "p" || canonical == "parallel") && i + 1 < args.Length && string.Equals(args[i + 1], "max", StringComparison.OrdinalIgnoreCase)) { parallelism = Environment.ProcessorCount; i++; }
                else if ((canonical == "p" || canonical == "parallel") && i + 1 < args.Length && int.TryParse(args[i + 1], out var p) && p > 0) { parallelism = p; i++; }
                else if (canonical == "synthesize") synthesize = true;
                else if (canonical == "catalog" && i + 1 < args.Length) synthCatalog = args[++i];
                else if (canonical == "synth-output" && i + 1 < args.Length) synthOutput = args[++i];
                else if (canonical == "count" && i + 1 < args.Length && int.TryParse(args[i + 1], out var cnt) && cnt > 0)
                    { synthCount = cnt; i++; }
                else if (canonical == "album-ratio" && i + 1 < args.Length && double.TryParse(args[i + 1], out var ar))
                    { synthAlbumRatio = Math.Max(0, Math.Min(1, ar)); i++; }
                else if (canonical == "synth-moods" && i + 1 < args.Length) synthMoods = args[++i];
                else if (canonical == "seed" && i + 1 < args.Length && int.TryParse(args[i + 1], out var sd))
                    { synthSeed = sd; i++; }
                else if (canonical == "dry-run") synthDryRun = true;
                else if (canonical == "seed-moods") seedMoods = true;
                else if (canonical == "seed-catalog" && i + 1 < args.Length) seedCatalog = args[++i];
                else if (canonical == "seed-target" && i + 1 < args.Length) seedTarget = args[++i];
                else if (canonical == "merge-moods") mergeMode = true;
                else if (canonical == "merge-source" && i + 1 < args.Length) mergeSources.Add(args[++i]);
                else if (canonical == "merge-output" && i + 1 < args.Length) mergeOutput = args[++i];
                else if (canonical == "analyze-file" && i + 1 < args.Length) { analyzeFileMode = true; analyzeFilePath = args[++i]; }
                else if (canonical == "file-list" && i + 1 < args.Length) { fileListMode = true; fileListPath = args[++i]; }
                else if (canonical == "folder" && i + 1 < args.Length) { folderMode = true; folderPath = args[++i]; }
                else if (canonical == "hash-only") hashOnlyMode = true;
                else if (canonical == "level" && i + 1 < args.Length) hashLevel = args[++i].ToLowerInvariant();
                else if (canonical == "transcode" && i + 1 < args.Length) { transcodeMode = true; transcodeInput = args[++i]; }
                else if (canonical == "transcode-out" && i + 1 < args.Length) transcodeOutput = args[++i];
                else if (canonical == "sample-rate" && i + 1 < args.Length && int.TryParse(args[i + 1], out var tsr) && tsr > 0) { transcodeSampleRate = tsr; i++; }
                else if (canonical == "bit-depth" && i + 1 < args.Length && int.TryParse(args[i + 1], out var tbd) && (tbd == 16 || tbd == 24)) { transcodeBitDepth = tbd; i++; }
                else if (canonical == "moods" && i + 1 < args.Length) analyzeFileMoods = args[++i];
                else if (canonical == "json-output") jsonOutput = true;
                else if (canonical == "output" && i + 1 < args.Length && !args[i + 1].StartsWith("-") && !args[i + 1].StartsWith("/"))
                    hashOutputPath = args[++i];
                else if (canonical == "chunk" && i + 1 < args.Length && TryParseChunk(args[i + 1], out var cIdx, out var cTot))
                    { chunkIndex = cIdx; chunkTotal = cTot; i++; }
                else if (canonical == "self-test") selfTest = true;
                else if (canonical == "no-stage") _stageOpts.NoStage = true;
                else if (canonical == "no-quick-cache") _quickCache = false;
                else if (canonical == "file-md5") _fileMd5Enabled = true;
                else if (canonical == "apply-exclusions" && i + 1 < args.Length) { applyExclusionsMode = true; applyExclusionsPath = args[++i]; }
                else if (canonical == "preview")
                {
                    previewMode = true;
                    // Optional OUTPUT path, claimed with exactly the same restraint as
                    // --manifest / --losers-m3u: only when the token looks like a path of the
                    // right kind (directory separator, or a .json suffix). Consuming the next
                    // non-flag token unconditionally was data loss waiting to happen —
                    // every adjacent read-only mode (--stats, --duplicates, --list-speech,
                    // --list-missing-smfm) takes its moods file POSITIONALLY, so
                    // `--preview mbxmoods.json` reads naturally as "preview this catalog"
                    // while the writer would truncate it. PreviewWriter.IsProtectedTarget is
                    // the second line of defence for the case that still binds here.
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-") && !args[i + 1].StartsWith("/")
                        && args[i + 1].IndexOf(Path.DirectorySeparatorChar) >= 0)
                    { previewOutPath = args[i + 1]; i++; }
                    else if (i + 1 < args.Length && !args[i + 1].StartsWith("-")
                        && args[i + 1].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    { previewOutPath = args[i + 1]; i++; }
                }
                else if (canonical == "long-track-mins" && i + 1 < args.Length)
                {
                    if (!int.TryParse(args[++i], out longTrackMins) || longTrackMins <= 0)
                    {
                        Console.Error.WriteLine($"Error: --long-track-mins requires a positive integer (minutes), got '{args[i]}'");
                        Environment.ExitCode = 1;
                        return;
                    }
                }
                else if (canonical == "exclusions" && i + 1 < args.Length) _exclusionsPath = args[++i];
                else if (canonical == "no-exclusions") _noExclusions = true;
                else if (canonical == "accept-flac-tag-drift") _acceptFlacTagDrift = true;
                else if (canonical == "refresh-features") _refreshFeatures = true;
                else if (canonical == "pause") { /* consumed by the Main wrapper (hold console at exit) */ }
                else if (canonical == "stage-dir" && i + 1 < args.Length) { _stageOpts.StageDir = args[++i]; }
                else if (canonical == "max-duration" && i + 1 < args.Length)
                {
                    if (!int.TryParse(args[++i], out _maxEssentiaDurationSecs) || _maxEssentiaDurationSecs <= 0)
                    {
                        Console.Error.WriteLine($"Error: --max-duration requires a positive integer (seconds), got '{args[i]}'");
                        Environment.ExitCode = 1;
                        return;
                    }
                    _maxDurationExplicit = true;
                }
                else if (canonical == "no-bitusage") _noBitUsage = true;
                else if (canonical == "no-hf-analysis") _noHfAnalysis = true;
                else if (canonical == "version" || canonical == "v")
                {
                    Console.WriteLine(VersionInfo.Display);
                    Environment.ExitCode = 0;
                    return;
                }
                else if (canonical == "background") cpuLimit = 25;
                else if (canonical == "cpu-limit" && i + 1 < args.Length && int.TryParse(args[i + 1], out var cl) && cl >= 1 && cl <= 100) { cpuLimit = cl; i++; }
                else if (!arg.StartsWith("-") && !arg.StartsWith("/") && xmlPath == null) xmlPath = args[i];
            }

            _audit = auditLog;

            if (selfTest)
            {
                Environment.ExitCode = RunSelfTest();
                return;
            }



            if (fileListMode && analyzeFileMode)
            {
                Console.Error.WriteLine("Error: Cannot use --file-list and --analyze-file together.");
                Environment.ExitCode = 1;
                return;
            }
            if (chunkTotal > 0 && (chunkIndex < 1 || chunkIndex > chunkTotal))
            {
                Console.Error.WriteLine($"Error: --chunk M/N requires 1 <= M <= N (got {chunkIndex}/{chunkTotal}).");
                Environment.ExitCode = 1;
                return;
            }
            if (chunkTotal > 0 && (analyzeFileMode || fileListMode || migrateMode || fixupMode || verifyMode || statsMode || listSpeechMode || listSmfmMissingMode || applyExclusionsMode || duplicatesMode || mergeMode || synthesize || seedMoods || hashOnlyMode || previewMode))
            {
                Console.Error.WriteLine("Error: --chunk applies to the default iTunes-XML scan path only.");
                Environment.ExitCode = 1;
                return;
            }
            if (folderMode && (fileListMode || analyzeFileMode))
            {
                Console.Error.WriteLine("Error: --folder is mutually exclusive with --file-list and --analyze-file.");
                Environment.ExitCode = 1;
                return;
            }
            if (folderMode && !Directory.Exists(folderPath!))
            {
                Console.Error.WriteLine($"Error: Folder not found: {folderPath}");
                Environment.ExitCode = 1;
                return;
            }
            // --folder reuses the --file-list worker code path entirely; the only
            // difference is path discovery (directory walk vs reading a list file).
            if (folderMode) fileListMode = true;

            if (hashOnlyMode)
            {
                if (hashLevel != "fingerprint" && hashLevel != "stream")
                {
                    Console.Error.WriteLine("Error: --hash-only requires --level fingerprint or --level stream.");
                    Environment.ExitCode = 1;
                    return;
                }
                if (analyzeFileMode)
                {
                    Console.Error.WriteLine("Error: Cannot use --hash-only and --analyze-file together.");
                    Environment.ExitCode = 1;
                    return;
                }
                if (!fileListMode)
                {
                    Console.Error.WriteLine("Error: --hash-only requires --file-list <path>.");
                    Environment.ExitCode = 1;
                    return;
                }
                // --output <path> is the only sink — appends NDJSON identity envelopes.
                if (string.IsNullOrEmpty(hashOutputPath))
                {
                    Console.Error.WriteLine("Error: --hash-only requires --output <path>.");
                    Environment.ExitCode = 1;
                    return;
                }
            }

            if (transcodeMode)
            {
                if (string.IsNullOrEmpty(transcodeInput) || !File.Exists(transcodeInput))
                {
                    Console.Error.WriteLine($"Error: --transcode input not found: {transcodeInput}");
                    Environment.ExitCode = 1;
                    return;
                }
                if (string.IsNullOrEmpty(transcodeOutput))
                {
                    Console.Error.WriteLine("Error: --transcode requires --transcode-out <path.flac>.");
                    Environment.ExitCode = 1;
                    return;
                }
                if (analyzeFileMode || fileListMode || hashOnlyMode || migrateMode || fixupMode || verifyMode || statsMode || listSpeechMode || listSmfmMissingMode || applyExclusionsMode || duplicatesMode || mergeMode || synthesize || seedMoods || chunkTotal > 0 || previewMode)
                {
                    Console.Error.WriteLine("Error: --transcode is a standalone mode (mutually exclusive with scan/hash/merge/etc).");
                    Environment.ExitCode = 1;
                    return;
                }
                Environment.ExitCode = RunTranscode(transcodeInput!, transcodeOutput!, transcodeSampleRate, transcodeBitDepth);
                return;
            }

            if (cpuLimit > 0)
            {
                InitCpuLimitJob(cpuLimit);
                if (_jobHandle != IntPtr.Zero)
                    Console.Error.WriteLine($"CPU limit: {cpuLimit}% (child processes capped via Job Object)");
            }

            if (showHelp)
            {
                PrintHelp();
                return;
            }

            // --seed-moods is independent of the XML-based modes
            if (seedMoods)
            {
                if (string.IsNullOrEmpty(seedCatalog))
                {
                    Console.WriteLine("Error: --seed-moods requires --seed-catalog <path>");
                    Environment.ExitCode = 1;
                    return;
                }
                if (xmlPath == null)
                {
                    Console.WriteLine("Error: --seed-moods requires an iTunes XML library path");
                    Environment.ExitCode = 1;
                    return;
                }
                string targetMoods = seedTarget ?? Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(xmlPath)) ?? ".", "mbxmoods.json");
                Environment.ExitCode = new SeedCommand(seedCatalog!, xmlPath, targetMoods).Run();
                return;
            }

            // --synthesize is independent of the XML-based modes
            if (synthesize)
            {
                if (string.IsNullOrEmpty(synthCatalog))
                {
                    Console.WriteLine("Error: --synthesize requires --catalog <path>");
                    Environment.ExitCode = 1;
                    return;
                }
                if (string.IsNullOrEmpty(synthOutput) && !synthDryRun)
                {
                    Console.WriteLine("Error: --synthesize requires --synth-output <path> (or --dry-run)");
                    Environment.ExitCode = 1;
                    return;
                }
                Environment.ExitCode = new SynthesizeCommand(synthCatalog!, synthOutput ?? "", synthCount,
                    synthAlbumRatio, synthMoods, synthSeed, synthDryRun).Run();
                return;
            }

            // --verify: read-only diagnostic. Walk a moods file, recompute
            // audioStreamSha256 for each entry, classify drift / missing / no-hash.
            // No iTunes XML required — moods file location comes from --moods,
            // or defaults to mbxmoods.json next to the XML if given, else cwd.
            if (verifyMode)
            {
                string? verifyPath = analyzeFileMoods;
                if (string.IsNullOrEmpty(verifyPath))
                {
                    var dir = !string.IsNullOrEmpty(xmlPath)
                        ? Path.GetDirectoryName(Path.GetFullPath(xmlPath)) ?? "."
                        : Environment.CurrentDirectory;
                    verifyPath = Path.Combine(dir, "mbxmoods.json");
                }
                if (!File.Exists(verifyPath))
                {
                    Console.Error.WriteLine($"Error: moods file not found: {verifyPath}");
                    Console.Error.WriteLine("Hint: pass --moods <path> to verify a specific file.");
                    Environment.ExitCode = 1;
                    return;
                }
                Environment.ExitCode = RunVerify(verifyPath!, parallelism, verifyBackfill, backfillLevel);
                return;
            }

            // --stats: read-only catalog summary. Counts Essentia-analyzed tracks,
            // hash coverage per kind, and SMFM (12-TONE) tracks in an mbxmoods.json.
            // Path resolution mirrors --verify: --moods <path>, else the positional
            // arg, else ./mbxmoods.json (a bare directory resolves to mbxmoods.json
            // inside it). Writes nothing.
            if (statsMode)
            {
                string? statsPath = ResolveMoodsCatalog(analyzeFileMoods, xmlPath, out var statsRefusal);
                if (statsRefusal != null)
                {
                    Console.Error.WriteLine(statsRefusal);
                    Environment.ExitCode = 1;
                    return;
                }
                if (!File.Exists(statsPath))
                {
                    Console.Error.WriteLine($"Error: moods file not found: {statsPath}");
                    Console.Error.WriteLine("Hint: pass the path to mbxmoods.json (or --moods <path>).");
                    Environment.ExitCode = 1;
                    return;
                }
                var statsTracks = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
                LoadExistingMoods(statsPath!, statsTracks);
                // --audit: tee the summary to truedat.log next to the moods file.
                TeeWriter? statsTee = null;
                string statsLog = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(statsPath!)) ?? ".", "truedat.log");
                if (auditLog) { statsTee = new TeeWriter(Console.Out, statsLog); Console.SetOut(statsTee); _tee = statsTee; }
                ReportCatalog(statsPath!, statsTracks.Values, statsDetailThreshold);
                if (statsTee != null) { Console.WriteLine($"Log:    {statsLog}"); _tee = null; statsTee.Dispose(); }
                Environment.ExitCode = 0;
                return;
            }

            // --list-speech: read-only. Lists the entries whose talk-vs-music verdict is a
            // confident "yes" — candidates for an exclusion rule, not a --migrate prune
            // (--migrate never removes entries) — so the operator can review them first
            // (the verdict is -untuned; high-zcr real music such as ambient / field
            // recordings can reach "yes"). Recomputes the verdict from stored features per
            // entry, count-equal with the --stats "Speech (acoustic)" line (label-only
            // Podcast entries are --stats' separate line). Path resolution mirrors --stats.
            // Writes mbxmoods-speech.csv next to the moods file; console gets a count +
            // first-N preview. Writes no changes to the moods file itself.
            if (listSpeechMode)
            {
                string? speechPath = ResolveMoodsCatalog(analyzeFileMoods, xmlPath, out var speechRefusal);
                if (speechRefusal != null)
                {
                    Console.Error.WriteLine(speechRefusal);
                    Environment.ExitCode = 1;
                    return;
                }
                if (!File.Exists(speechPath))
                {
                    Console.Error.WriteLine($"Error: moods file not found: {speechPath}");
                    Console.Error.WriteLine("Hint: pass the path to mbxmoods.json (or --moods <path>).");
                    Environment.ExitCode = 1;
                    return;
                }
                var speechTracks = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
                LoadExistingMoods(speechPath!, speechTracks);
                RunListSpeech(speechPath!, speechTracks.Values);
                Environment.ExitCode = 0;
                return;
            }

            // --list-missing-smfm: read-only. Lists catalog entries carrying no Sony
            // SMFM (12-TONE) data, so the operator can see which files still need Sony
            // tagging. Path resolution mirrors --stats / --list-speech. Writes only its
            // CSV; the moods file is untouched.
            if (listSmfmMissingMode)
            {
                string? smfmPath = ResolveMoodsCatalog(analyzeFileMoods, xmlPath, out var smfmRefusal);
                if (smfmRefusal != null)
                {
                    Console.Error.WriteLine(smfmRefusal);
                    Environment.ExitCode = 1;
                    return;
                }
                if (!File.Exists(smfmPath))
                {
                    Console.Error.WriteLine($"Error: moods file not found: {smfmPath}");
                    Console.Error.WriteLine("Hint: pass the path to mbxmoods.json (or --moods <path>).");
                    Environment.ExitCode = 1;
                    return;
                }
                var smfmTracks = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
                LoadExistingMoods(smfmPath!, smfmTracks);
                RunListMissingSmfm(smfmPath!, smfmTracks.Values);
                Environment.ExitCode = 0;
                return;
            }

            // --apply-exclusions <decisions.json>: merge a decisions delta into the
            // canonical mbxmoods-exclude.json. This is the offline CRUD path (a review
            // page downloads the delta; MBXHub relays it by invoking this), and it is
            // the single implementation of the merge semantics so no second author can
            // drift from them. Backs up before rewriting; reports every outcome.
            if (applyExclusionsMode)
            {
                string? moodsForExcl = analyzeFileMoods ?? xmlPath;
                if (string.IsNullOrEmpty(moodsForExcl))
                {
                    // Auto-discover the library exactly as --preview and the scan do. The old
                    // behaviour defaulted to the CURRENT DIRECTORY, which made the README's own
                    // documented invocation (`truedat --apply-exclusions decisions.json`) create a
                    // BRAND-NEW exclusion file wherever the operator happened to be standing,
                    // print "Added: N" and exit 0 — while --preview and the scan read the
                    // library's file and excluded nothing, with no error anywhere to explain it.
                    // All three modes must agree on which file they mean.
                    var discoveredXml = ResolveITunesXml(null);
                    if (File.Exists(discoveredXml))
                        moodsForExcl = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(discoveredXml)) ?? ".", "mbxmoods.json");
                }
                else if (Directory.Exists(moodsForExcl))
                    moodsForExcl = Path.Combine(moodsForExcl, "mbxmoods.json");

                if (string.IsNullOrEmpty(moodsForExcl) && string.IsNullOrEmpty(_exclusionsPath))
                {
                    // Refuse rather than fall back to the CWD. CREATING an exclusion file is
                    // legitimate — first use has none, and Merge creates it — but creating it in
                    // an arbitrary directory is indistinguishable from success while achieving
                    // nothing, which is the whole defect. Make the operator name the target.
                    Console.Error.WriteLine("Error: cannot tell which library to apply exclusions to — no iTunes Music Library.xml found.");
                    Console.Error.WriteLine("  Pass the library (or the folder holding it) as a positional argument, or name the exclusion file directly with --exclusions <path>.");
                    Environment.ExitCode = 1;
                    return;
                }

                var canonicalExcl = _exclusionsPath ?? ExclusionStore.Resolve(moodsForExcl!);
                // Print the target BEFORE merging, not only on success: if this is not the file
                // the operator expected, they need to see that even when the merge then fails.
                Console.WriteLine($"Applying to: {canonicalExcl}");

                if (!File.Exists(applyExclusionsPath))
                {
                    var notFoundError = $"decisions file not found: {applyExclusionsPath}";
                    var notFoundResultPath = ExclusionStore.WriteApplyResult(canonicalExcl, new MergeReport(), notFoundError);
                    Console.Error.WriteLine($"Error: {notFoundError}");
                    Console.Error.WriteLine($"  Result:     {notFoundResultPath}");
                    Environment.ExitCode = 1;
                    return;
                }
                string decisionsJson;
                try { decisionsJson = File.ReadAllText(applyExclusionsPath!); }
                catch (Exception ex)
                {
                    var unreadableError = $"cannot read {applyExclusionsPath}: {ex.Message}";
                    var unreadableResultPath = ExclusionStore.WriteApplyResult(canonicalExcl, new MergeReport(), unreadableError);
                    Console.Error.WriteLine($"Error: {unreadableError}");
                    Console.Error.WriteLine($"  Result:     {unreadableResultPath}");
                    Environment.ExitCode = 1;
                    return;
                }

                var report = ExclusionStore.Merge(canonicalExcl, decisionsJson,
                    $"truedat --apply-exclusions {VersionInfo.Display}", out var mergeError);
                if (mergeError != null)
                {
                    var errResultPath = ExclusionStore.WriteApplyResult(canonicalExcl, report, mergeError);
                    Console.Error.WriteLine($"Error: {mergeError}");
                    // Say what is actually true. The old wording ("Nothing was written.") was
                    // false in the one case that matters — a failure inside the non-atomic
                    // Write had already truncated the file — and it was also false in every
                    // case about apply-result.json, which the line below names. Merge's write
                    // is now tmp + atomic replace and every other refusal path returns before
                    // touching the file, so the canonical file really is unmodified on any error.
                    Console.Error.WriteLine("The exclusion file was not modified.");
                    Console.Error.WriteLine($"  Result:     {errResultPath}");
                    Environment.ExitCode = 1;
                    return;
                }
                var resultPath = ExclusionStore.WriteApplyResult(canonicalExcl, report, null);
                Console.WriteLine($"Exclusions: {canonicalExcl}");
                Console.WriteLine($"  Added:      {report.Added}");
                Console.WriteLine($"  Removed:    {report.Removed}");
                if (report.AlreadyPresent > 0) Console.WriteLine($"  Already set: {report.AlreadyPresent}");
                if (report.NotFound > 0) Console.WriteLine($"  Not present: {report.NotFound}  (nothing to remove)");
                if (report.BackupPath != null) Console.WriteLine($"  Backup:     {report.BackupPath}");
                if (!report.Changed) Console.WriteLine("  No changes.");
                foreach (var diag in report.Diagnostics)
                    Console.Error.WriteLine($"  WARNING: exclusion rule ignored — {diag}");
                Console.WriteLine($"  Result:     {resultPath}");
                Environment.ExitCode = 0;
                return;
            }

            // --preview: builds the scan work plan and the review-candidate list. Analyzes
            // nothing, writes no mbxmoods.json, and never touches the exclusion file — those
            // are the guarantees, and they are what "read-only" used to be shorthand for.
            // It is NOT read-only over the filesystem (I-6): with no explicit path it creates
            // <library root>\AppData\MBXHub\review\ and writes preview.json + the review page
            // there, i.e. into a running application's data directory. Anchored to the SCANNED
            // library via ResolveReviewDir — the same resolver the duplicates manifest uses —
            // so a multi-instance box cannot receive another instance's plan.
            if (previewMode)
            {
                // Same auto-discovery the default scan path uses (exe-dir parent, exe-dir,
                // cwd) — without this, --preview run from inside the library directory with
                // no positional arg (the operator's habitual invocation) would reject a
                // perfectly resolvable XML with a message that lies about "run from the
                // library directory" already having been tried.
                xmlPath = ResolveITunesXml(xmlPath);
                if (!File.Exists(xmlPath))
                {
                    Console.Error.WriteLine("Error: --preview needs the iTunes XML (pass it positionally, or run from the library directory).");
                    Environment.ExitCode = 1;
                    return;
                }
                var pvOutputDir = Path.GetDirectoryName(Path.GetFullPath(xmlPath)) ?? ".";
                var pvMoodsPath = analyzeFileMoods ?? Path.Combine(pvOutputDir, "mbxmoods.json");
                var pvExclPath = _exclusionsPath ?? ExclusionStore.Resolve(pvMoodsPath);

                var pvExclusions = ExclusionSet.Empty;
                if (!_noExclusions)
                {
                    if (!ValidateExplicitExclusionsPath(out var pvPathErr))
                    {
                        Console.Error.WriteLine($"Error: {pvPathErr}");
                        Environment.ExitCode = 1;
                        return;
                    }
                    pvExclusions = ExclusionStore.Load(pvExclPath, out var pvExclErr);
                    if (pvExclErr != null)
                    {
                        Console.Error.WriteLine($"Error: {pvExclErr}");
                        Console.Error.WriteLine("Refusing to preview: fix the exclusion file, or pass --no-exclusions.");
                        Environment.ExitCode = 1;
                        return;
                    }
                    foreach (var diag in pvExclusions.Diagnostics)
                        Console.Error.WriteLine($"  WARNING: exclusion rule ignored — {diag}");
                }

                Console.WriteLine($"Loading iTunes library: {xmlPath}");
                var pvTracks = ITunesParser.Parse(xmlPath, out _);
                Console.WriteLine($"Found {pvTracks.Count} tracks");

                var pvCatalog = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
                if (File.Exists(pvMoodsPath)) LoadExistingMoods(pvMoodsPath, pvCatalog);
                Console.WriteLine($"Existing moods: {pvCatalog.Count}");

                // A preview run measures nothing, so the real-time factor comes from the
                // catalog's own history: stored analysisDuration against the track's known
                // length. It was recorded under whatever parallelism that scan used, so it is
                // an approximation — which is exactly why the basis is reported as
                // "catalog-rtf" rather than claiming a fresh measurement.
                double pvRtf = MedianCatalogRtf(pvTracks, pvCatalog);

                var pvPlan = PreviewPlanner.Build(new PreviewPlannerInput
                {
                    Tracks = pvTracks,
                    Catalog = pvCatalog,
                    Exclusions = pvExclusions,
                    XmlPath = Path.GetFullPath(xmlPath),
                    MoodsPath = pvMoodsPath,
                    ExclusionsPath = pvExclPath,
                    MaxDurationSecs = _maxEssentiaDurationSecs,
                    MaxDurationSource = _maxDurationExplicit ? "--max-duration" : "default",
                    LongTrackSecs = longTrackMins * 60,
                    ReviewCap = PreviewReviewCap,
                    MeasuredRtf = pvRtf,
                    EtaBasisLabel = "catalog-rtf",
                    Parallelism = Math.Max(1, parallelism),
                    FileExists = File.Exists,
                    // Share the scan's own cache-hit classifier so --preview counts new work
                    // the same way the pre-flight and the scan do (I1): a legacy entry with
                    // features but not the re-extract canary re-analyzes, so it is NOT
                    // "already analyzed" — Features != null would have hidden it.
                    IsAnalyzed = e => HasCurrentFeatures(e?.Features),
                    SniffMarkers = p =>
                    {
                        var m = SpeechTagSniffer.TryDetectAll(p);
                        return m == null ? null : m.Describe();
                    },
                    SpeechVerdict = e => e?.Features == null
                        ? null
                        : ComputeTruedatVerdict(e.Features.FilePath ?? "", e).SpeechLikely,
                });

                var pvDest = previewOutPath ?? PreviewWriter.ResolveDest(pvOutputDir);
                // Co-emit the review page BEFORE the manifest, and name it in the manifest only
                // if it actually landed. Writing the manifest first meant a page-write failure
                // left source.reviewHtml pointing at a file that does not exist — and MBXHub
                // follows that pointer to serve the page, so the broken link would surface on
                // the hub rather than here. A page-write failure is still a warning, not a mode
                // failure: the JSON is the machine contract and stays valid without the pointer.
                var pvHtml = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(pvDest)) ?? pvOutputDir,
                                          PreviewWriter.PreviewHtmlFileName);
                string? pvHtmlName = null;
                try
                {
                    PreviewWriter.WritePreviewHtml(pvHtml, pvPlan);
                    pvHtmlName = PreviewWriter.PreviewHtmlFileName;
                }
                catch (Exception ex) { Console.Error.WriteLine($"  (warning: preview page not written: {ex.Message})"); }
                if (!PreviewWriter.TryWritePreviewJson(pvDest, pvPlan, pvHtmlName))
                {
                    Environment.ExitCode = 1;
                    return;
                }

                Console.WriteLine();
                Console.WriteLine("Scan preview (nothing was analyzed):");
                Console.WriteLine($"  Library:    {pvPlan.Counts.LibraryTotal} tracks");
                Console.WriteLine($"  Analyzed:   {pvPlan.Counts.Analyzed}   New: {pvPlan.Estimate.NewTracks}");
                if (pvPlan.Estimate.EtaSecs >= 0)
                    Console.WriteLine($"  Estimate:   {FormatTimeSpan(TimeSpan.FromSeconds(pvPlan.Estimate.EtaSecs))} ({pvPlan.Estimate.EtaBasis})");
                else
                    Console.WriteLine($"  Estimate:   {PreviewNoEtaReason(pvPlan.Estimate.NewTracks)}");
                foreach (var b in pvPlan.AutoSkip)
                    Console.WriteLine($"  Skipped:    {b.Count,6:N0}  {b.Class}  (structural — cannot be analyzed)");
                if (pvPlan.Counts.Excluded > 0)
                    Console.WriteLine($"  Excluded:   {pvPlan.Counts.Excluded,6:N0}  by rule");
                foreach (var r in pvPlan.Rules)
                    Console.WriteLine($"    {r.Rule}: {r.MatchCount} matched{PreviewRuleNote(r)}");
                Console.WriteLine($"  To review:  {pvPlan.ReviewTotal}"
                    + (pvPlan.ReviewTruncated ? $"  (listing the first {pvPlan.Review.Count})" : ""));
                Console.WriteLine($"  Ceiling:    {pvPlan.Limits.MaxDurationSecs / 60} min ({pvPlan.Limits.MaxDurationSource})"
                    + $"   long-track prompt: {pvPlan.Limits.LongTrackSecs / 60} min");
                Console.WriteLine();
                Console.WriteLine($"Preview: {Linkify(pvDest)}");
                Console.WriteLine($"Review page: {Linkify(pvHtml)}");
                Environment.ExitCode = 0;
                return;
            }

            // --merge-moods is independent of the XML-based modes
            if (mergeMode)
            {
                if (mergeSources.Count < 2)
                {
                    Console.WriteLine("Error: --merge-moods requires at least 2 --merge-source <path> arguments");
                    Environment.ExitCode = 1;
                    return;
                }
                if (string.IsNullOrEmpty(mergeOutput))
                {
                    Console.WriteLine("Error: --merge-moods requires --merge-output <path>");
                    Environment.ExitCode = 1;
                    return;
                }
                Environment.ExitCode = RunMergeMoods(mergeSources, mergeOutput!);
                return;
            }

            // --analyze-file: single file Essentia analysis (no iTunes XML needed)
            if (analyzeFileMode)
            {
                // No path at all is a usage error and stays exit 1 — there is nothing to
                // ledger and nothing the caller could have meant.
                if (string.IsNullOrEmpty(analyzeFilePath))
                {
                    Console.Error.WriteLine("Error: --analyze-file needs a file path.");
                    Environment.ExitCode = 1;
                    return;
                }
                if (!File.Exists(analyzeFilePath))
                {
                    // Missing is a structural skip, not an analysis failure — the same call the
                    // M6 fix made for --file-list, which the very same plugin workflow invokes.
                    // CLAUDE.md:98 requires the skipped.csv row on ALL scan modes, and a piped
                    // path that no longer exists is not a scan failure, so exit 0.
                    var afMissReason = MissingFileSkipReason(analyzeFilePath!);
                    var afMissDir = !string.IsNullOrEmpty(analyzeFileMoods)
                        ? (Path.GetDirectoryName(Path.GetFullPath(analyzeFileMoods)) ?? ".")
                        : Environment.CurrentDirectory;
                    AppendSkipped(Path.Combine(afMissDir, "mbxmoods-skipped.csv"),
                        analyzeFilePath!, GetExtensionSafe(analyzeFilePath!), afMissReason);
                    Console.Error.WriteLine($"[skipped missing] {analyzeFilePath} ({afMissReason})");
                    Environment.ExitCode = 0;
                    return;
                }

                // Phase 5.1 — structural skip (DSD, video, playlist/redirector):
                // bail before invoking TagLib / Essentia / fingerprint helpers.
                var afStructReason = StructuralSkipReason(analyzeFilePath);
                if (afStructReason != null)
                {
                    var afSkipExt = Path.GetExtension(analyzeFilePath);
                    var afSkippedDir = !string.IsNullOrEmpty(analyzeFileMoods)
                        ? (Path.GetDirectoryName(Path.GetFullPath(analyzeFileMoods)) ?? ".")
                        : Environment.CurrentDirectory;
                    var afSkippedPath = Path.Combine(afSkippedDir, "mbxmoods-skipped.csv");
                    AppendSkipped(afSkippedPath, analyzeFilePath!, afSkipExt ?? "", afStructReason);
                    Console.Error.WriteLine($"[skipped {StructuralSkipConsoleTag(afStructReason)}] {analyzeFilePath}");
                    Environment.ExitCode = 0;
                    return;
                }

                // Hoisted above the exclusion guard (and reused at the cache-key site
                // below) so the guard matches a normalized absolute path instead of
                // whatever the operator typed. IsExcluded folds separators/case but
                // never makes a relative path absolute, so without this a relative
                // invocation could silently dodge a folder/file rule that an absolute
                // invocation of the same file would hit. Everything between here and
                // the staging block is cheap metadata — safe to compute early.
                var afKey = Path.GetFullPath(analyzeFilePath!);

                // Explicit exclusions — same load-once-per-invocation semantics as
                // MoodsMode. A missing file excludes nothing; a broken one refuses the scan.
                var afExclDir = !string.IsNullOrEmpty(analyzeFileMoods)
                    ? (Path.GetDirectoryName(Path.GetFullPath(analyzeFileMoods)) ?? ".")
                    : Environment.CurrentDirectory;
                if (!_noExclusions)
                {
                    if (!ValidateExplicitExclusionsPath(out var afExclPathErr))
                    {
                        Console.Error.WriteLine($"Error: {afExclPathErr}");
                        Environment.ExitCode = 1;
                        return;
                    }
                    _exclusions = ExclusionStore.Load(_exclusionsPath ?? Path.Combine(afExclDir, ExclusionStore.FileName), out var afExclErr);
                    if (afExclErr != null)
                    {
                        Console.Error.WriteLine($"Error: {afExclErr}");
                        Console.Error.WriteLine("Refusing to scan: fix the exclusion file, or pass --no-exclusions.");
                        Environment.ExitCode = 1;
                        return;
                    }
                    foreach (var afDiag in _exclusions.Diagnostics)
                        Console.Error.WriteLine($"  WARNING: exclusion rule ignored — {afDiag}");
                }

                // Exclusion check runs before any cache tier — an excluded file never
                // enters the catalog, so it can never hit tier 1, and letting it fall
                // through to tier 4 would stage + hash it over the network for nothing.
                // Matched on afKey (normalized full path), not the raw analyzeFilePath,
                // so a relative invocation can't dodge an absolute file rule or a folder
                // fragment the same way --file-list already guards against.
                var afGenre = (!_noExclusions && _exclusions.HasGenreRules) ? ReadGenreForExclusion(afKey) : null;
                if (!_noExclusions && _exclusions.IsExcluded(afKey, afGenre, out var afExclReason))
                {
                    // Ledgered like every other exclusion skip (--file-list's guard does
                    // the same) — README promises every exclusion lands in the CSV, not
                    // just the batch-mode ones. Report the path as typed; match on afKey.
                    var afExclSkippedPath = Path.Combine(afExclDir, "mbxmoods-skipped.csv");
                    AppendSkipped(afExclSkippedPath, analyzeFilePath!, GetExtensionSafe(analyzeFilePath!), $"excluded (rule: {afExclReason})");
                    Console.Error.WriteLine($"[skipped excluded: {afExclReason}] {analyzeFilePath}");
                    Environment.ExitCode = 0;
                    return;
                }

                var afBaseDir = AppDomain.CurrentDomain.BaseDirectory;
                var afFileDir = Path.GetDirectoryName(Path.GetFullPath(analyzeFilePath)) ?? ".";
                var afEssentiaExe = FindTool("essentia_streaming_extractor_music.exe", afBaseDir, afFileDir, Environment.CurrentDirectory);

                if (afEssentiaExe == null)
                {
                    Console.Error.WriteLine("Error: essentia_streaming_extractor_music.exe not found");
                    Environment.ExitCode = 2;
                    return;
                }

                Console.Error.WriteLine($"Analyzing: {analyzeFilePath}");
                var afSw = System.Diagnostics.Stopwatch.StartNew();

                var afFileSize = new FileInfo(analyzeFilePath!).Length;
                DateTime afCurrentLastMod = DateTime.MinValue;
                try { afCurrentLastMod = File.GetLastWriteTimeUtc(analyzeFilePath!); } catch { }

                // Pre-load moods for cache check (only meaningful when --moods is set).
                ConcurrentDictionary<string, TrackEntry>? afMoodsTracks = null;
                if (!string.IsNullOrEmpty(analyzeFileMoods) && File.Exists(analyzeFileMoods))
                {
                    afMoodsTracks = new ConcurrentDictionary<string, TrackEntry>(StringComparer.OrdinalIgnoreCase);
                    LoadExistingMoods(analyzeFileMoods!, afMoodsTracks);
                }

                TrackEntry? trackEntry = null;
                string afHitTag = "analyzed";
                FingerprintV1? afFingerprintV1 = null;
                string? afAudioStreamSha256 = null;
                string afAudioStreamSha256Source = "";

                // FIX 4 — staging handle opened lazily by EnsureStagedSrc() on the first
                // body read after a tier-1 miss. Reused by every tier-2/3/4 body read and
                // the cache-miss worker fan-out. Dispose runs at end of the analyze-file
                // block (after the JSON-write / moods-save), so the worker fan-out and the
                // identity write both see the same staged copy. Tier-1 (mtime-equality)
                // does NOT trigger staging.
                SourceHandle? afStagedSrc = null;
                string EnsureStagedSrc()
                {
                    afStagedSrc ??= OpenStagedSource(analyzeFilePath!, _stageOpts, afFileSize);
                    return afStagedSrc.Path;
                }

                // One body read serves tiers 2/3/4: all digests come from a single pass
                // over the staged copy. Lazy — tier-1 hits never read the body.
                // leg = legacy TagLib-region sha (FLAC transition compare).
                (string? md5, string? sha, string? leg)? afBodyHashes = null;
                (string? md5, string? sha, string? leg) EnsureBodyHashes()
                {
                    if (afBodyHashes == null)
                    {
                        var (m, s, _) = ComputeFileMd5AndAudioSha(EnsureStagedSrc(), afFileSize, out _, out var leg);
                        afBodyHashes = (m, s, leg);
                    }
                    return afBodyHashes.Value;
                }
                try
                {

                // Cache hierarchy — same tiers as MoodsMode and --file-list.
                if (afMoodsTracks != null)
                {
                    var afMoodShaIndex = BuildHashIndex(afMoodsTracks, e => e.AudioStreamSha256);

                    if (afMoodsTracks.TryGetValue(afKey, out var afEx)
                        && HasCurrentFeatures(afEx.Features))
                    {
                        // Tier 1: path-mtime. No body read — staging not opened.
                        if (TruncateToSeconds(afCurrentLastMod) == TruncateToSeconds(afEx.LastModified))
                        {
                            var freshTags = ExtractFileTags(analyzeFilePath!);
                            trackEntry = RebuildCacheEntryFromTags(afEx, freshTags.Artist, freshTags.Title,
                                freshTags.Album, freshTags.Genre, afKey, afCurrentLastMod, null, null);
                            afHitTag = "cached";
                            afFingerprintV1 = trackEntry.FingerprintV1;
                            afAudioStreamSha256 = trackEntry.AudioStreamSha256;
                            afAudioStreamSha256Source = trackEntry.AudioStreamSha256Source ?? "";
                        }
                        else
                        {
                            // Tier 1.5: quick tags-only check — ~64 KB read on the ORIGINAL
                            // path (no staging: copying the whole file would defeat the point).
                            // Evidence-based: head-64k + audio props must match the stored
                            // fingerprint; any doubt falls through to the full-SHA tier below.
                            // fileMd5 is cleared, not recomputed — the tag write invalidated
                            // it and recomputing costs the full read this tier avoids
                            // (--verify --backfill refills it).
                            FingerprintV1? afQuickFp = null;
                            if (_quickCache && afEx.FingerprintV1 != null)
                            {
                                afQuickFp = ComputeFingerprintV1(analyzeFilePath!, afFileSize, out _);
                                if (afQuickFp != null && IsTagsOnlyChange(afQuickFp, afEx.FingerprintV1))
                                {
                                    var freshTags = ExtractFileTags(analyzeFilePath!);
                                    trackEntry = RebuildCacheEntryFromTags(afEx, freshTags.Artist, freshTags.Title,
                                        freshTags.Album, freshTags.Genre, afKey, afCurrentLastMod, null, afQuickFp);
                                    trackEntry.FileMd5 = null;  // stale whole-file MD5; --verify --backfill refills
                                    afHitTag = "cached·head";
                                    afFingerprintV1 = trackEntry.FingerprintV1;
                                    afAudioStreamSha256 = trackEntry.AudioStreamSha256;
                                    afAudioStreamSha256Source = trackEntry.AudioStreamSha256Source ?? "";
                                }
                            }

                            // Tier 2: path-sha (mtime drifted, audio bytes unchanged).
                            // Body reads go through the staged copy.
                            if (trackEntry == null && !string.IsNullOrEmpty(afEx.AudioStreamSha256))
                            {
                                var afStagedPath = EnsureStagedSrc();
                                var afHashes = EnsureBodyHashes();
                                var recompSha = afHashes.sha;
                                bool afShaHit = !string.IsNullOrEmpty(recompSha)
                                    && string.Equals(recompSha, afEx.AudioStreamSha256, StringComparison.OrdinalIgnoreCase);
                                bool afLegacyHit = !afShaHit && !string.IsNullOrEmpty(afHashes.leg)
                                    && string.Equals(afHashes.leg, afEx.AudioStreamSha256, StringComparison.OrdinalIgnoreCase);
                                if (afShaHit || afLegacyHit)
                                {
                                    var refreshedMd5 = afHashes.md5;
                                    var refreshedFp = afQuickFp ?? ComputeFingerprintV1(afStagedPath, afFileSize, out _);
                                    var freshTags = ExtractFileTags(afStagedPath);
                                    trackEntry = afLegacyHit
                                        ? RebuildCacheEntryFromTags(afEx, freshTags.Artist, freshTags.Title,
                                            freshTags.Album, freshTags.Genre, afKey, afCurrentLastMod, refreshedMd5, refreshedFp, recompSha, "flac-frames")
                                        : RebuildCacheEntryFromTags(afEx, freshTags.Artist, freshTags.Title,
                                            freshTags.Album, freshTags.Genre, afKey, afCurrentLastMod, refreshedMd5, refreshedFp);
                                    if (!_fileMd5Enabled) trackEntry.FileMd5 = null;  // unconsumed field; not written without --file-md5
                                    afHitTag = "cached·sha";
                                    afFingerprintV1 = trackEntry.FingerprintV1;
                                    afAudioStreamSha256 = trackEntry.AudioStreamSha256;
                                    afAudioStreamSha256Source = trackEntry.AudioStreamSha256Source ?? "";
                                }
                            }
                        }
                    }

                    // Tier 4: cross-SHA
                    if (trackEntry == null && afMoodShaIndex != null)
                    {
                        var afStagedPath = EnsureStagedSrc();
                        var afXHashes = EnsureBodyHashes();
                        var localSha = afXHashes.sha;
                        (TrackEntry Entry, string OldKey) xs = default;
                        bool afXHit = !string.IsNullOrEmpty(localSha) && afMoodShaIndex.TryGetValue(localSha!, out xs);
                        bool afXLegacyHit = !afXHit && !string.IsNullOrEmpty(afXHashes.leg)
                            && afMoodShaIndex.TryGetValue(afXHashes.leg!, out xs);
                        if ((afXHit || afXLegacyHit)
                            && HasCurrentFeatures(xs.Entry!.Features))
                        {
                            var refreshedMd5 = afXHashes.md5;
                            var refreshedFp = ComputeFingerprintV1(afStagedPath, afFileSize, out _);
                            var freshTags = ExtractFileTags(afStagedPath);
                            trackEntry = afXLegacyHit
                                ? RebuildCacheEntryFromTags(xs.Entry, freshTags.Artist, freshTags.Title,
                                    freshTags.Album, freshTags.Genre, afKey, afCurrentLastMod, refreshedMd5, refreshedFp, localSha, "flac-frames")
                                : RebuildCacheEntryFromTags(xs.Entry, freshTags.Artist, freshTags.Title,
                                    freshTags.Album, freshTags.Genre, afKey, afCurrentLastMod, refreshedMd5, refreshedFp);
                            if (!_fileMd5Enabled) trackEntry.FileMd5 = null;  // unconsumed field; not written without --file-md5
                            RemoveIfMoved(afMoodsTracks, xs.OldKey!);
                            afHitTag = "cached·sha";
                            afFingerprintV1 = trackEntry.FingerprintV1;
                            afAudioStreamSha256 = trackEntry.AudioStreamSha256;
                            afAudioStreamSha256Source = trackEntry.AudioStreamSha256Source ?? "";
                        }
                    }
                }

                // Cache miss (or no --moods): full Essentia + identity ride-along.
                if (trackEntry == null)
                {
                    EnsureStagedSrc();  // open if a no-moods scan skipped the cache hierarchy
                    var afResults = RunSourceWorkers(
                        afEssentiaExe, afStagedSrc!, afFileSize, analyzeFilePath!, knownDurationSec: 0,
                        extractTags: true, _stageOpts, CancellationToken.None);

                    var features = afResults.Features;
                    var afFileMd5 = afResults.FileMd5;
                    afFingerprintV1 = afResults.FingerprintV1;
                    afAudioStreamSha256 = afResults.AudioStreamSha256;
                    afAudioStreamSha256Source = afResults.AudioStreamSha256Source;
                    var afTags = afResults.Tags!;  // extractTags: true
                    var afBitUsage = afResults.BitUsage;
                    var afSmfmResult = afResults.Smfm;

                    if (features == null)
                    {
                        Console.Error.WriteLine($"Error: {afResults.EssentiaError}");
                        Environment.ExitCode = 3;
                        return;
                    }

                    features.Artist = afTags.Artist;
                    features.Title = afTags.Title;
                    features.Album = afTags.Album;
                    features.Genre = afTags.Genre;
                    features.FilePath = analyzeFilePath!;
                    if (afBitUsage != null) features.BitUsage = afBitUsage;
                    if (afResults.HfEnergyRatio.HasValue) { features.HfEnergyRatio = afResults.HfEnergyRatio; features.HfEnergyMethod = afResults.HfEnergyMethod; }
                    if (afResults.HfSpectralStructure != null) features.HfSpectralStructure = afResults.HfSpectralStructure;
                    if (afSmfmResult.HasValue)
                    {
                        features.SmfmScores      = afSmfmResult.Value.Scores;
                        features.SmfmChannel     = afSmfmResult.Value.Channel;
                        features.SmfmChannelName = SmfmReader.ChannelName(afSmfmResult.Value.Channel);
                        features.SmfmBpm           = Math.Round(afSmfmResult.Value.Bpm, 3);
                    }

                    // FIX 5 — record the mtime that was true when we captured the
                    // bytes we analyzed (snapshot from SourceHandle). Falls back to
                    // afCurrentLastMod if the snapshot failed (e.g. stat threw).
                    DateTime afLastMod = afStagedSrc!.SourceLastWriteUtc != DateTime.MinValue
                        ? afStagedSrc.SourceLastWriteUtc
                        : afCurrentLastMod;
                    if (afLastMod == DateTime.MinValue)
                        afLastMod = File.GetLastWriteTimeUtc(analyzeFilePath);

                    trackEntry = new TrackEntry
                    {
                        Features = features,
                        LastModified = afLastMod,
                        AnalysisDurationSecs = afSw.Elapsed.TotalSeconds,
                        FileMd5 = afFileMd5,
                        AudioStreamSha256 = string.IsNullOrEmpty(afAudioStreamSha256) ? null : afAudioStreamSha256,
                        AudioStreamSha256Source = afAudioStreamSha256Source,
                        FingerprintV1 = afFingerprintV1,
                    };
                }

                // For cache-hit paths, SMFM wasn't in the concurrent task batch — apply now.
                // For cache-miss, SmfmScores is already set from afSmfmTask.Result above.
                if (trackEntry!.Features.SmfmScores == null)
                    ApplySmfmInPlace(trackEntry.Features, analyzeFilePath!);

                afSw.Stop();

                // Output features as JSON to stdout. Identity fields ride on the outer wrapper.
                if (jsonOutput)
                {
                    using var ms = new MemoryStream();
                    using (var jw = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
                    {
                        jw.WriteStartObject();
                        WriteTrackEntry(jw, afKey, trackEntry);
                        if (afFingerprintV1 != null)
                            WriteFingerprintV1(jw, afFingerprintV1);
                        if (!string.IsNullOrEmpty(afAudioStreamSha256))
                        {
                            jw.WriteString("audioStreamSha256", afAudioStreamSha256);
                            if (afAudioStreamSha256Source == "whole-file")
                                jw.WriteString("audioStreamSha256Source", "whole-file");
                        }
                        jw.WriteEndObject();
                    }
                    Console.WriteLine(System.Text.Encoding.UTF8.GetString(ms.ToArray()));
                }

                // Save moods file if --moods specified
                if (!string.IsNullOrEmpty(analyzeFileMoods))
                {
                    afMoodsTracks ??= new ConcurrentDictionary<string, TrackEntry>(StringComparer.OrdinalIgnoreCase);
                    afMoodsTracks[afKey] = trackEntry;
                    SaveResults(analyzeFileMoods!, afMoodsTracks);
                    Console.Error.WriteLine($"Saved to: {analyzeFileMoods}");
                    ReportCatalog(analyzeFileMoods!, afMoodsTracks.Values, statsDetailThreshold);
                }

                Console.Error.WriteLine($"Done ({afHitTag}) in {afSw.Elapsed.TotalSeconds:F1}s");
                Environment.ExitCode = 0;
                }
                finally { afStagedSrc?.Dispose(); }
                return;
            }

            // --hash-only mode: batch identity computation (no Essentia analysis)
            // level=fingerprint: TagLib parse + 64KB MD5 at InvariantStartPosition
            // level=stream: full audio region SHA-256 (includes fingerprint.v1 as superset)
            if (hashOnlyMode)
            {
                if (!File.Exists(fileListPath!))
                {
                    Console.Error.WriteLine($"Error: File list not found: {fileListPath}");
                    Environment.ExitCode = 1;
                    return;
                }

                var hoPaths = File.ReadAllLines(fileListPath!, Encoding.UTF8)
                    .Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith("#"))
                    .Select(line => line.Trim())
                    .ToList();

                if (hoPaths.Count == 0)
                {
                    Console.Error.WriteLine("Error: File list is empty.");
                    Environment.ExitCode = 1;
                    return;
                }

                Console.Error.WriteLine($"Hash-only mode: level={hashLevel}, files={hoPaths.Count}, parallelism={parallelism}");
                if (!string.IsNullOrEmpty(hashOutputPath))
                    Console.Error.WriteLine($"  NDJSON manifest: {hashOutputPath}");

                var hoOutputDir = Path.GetDirectoryName(Path.GetFullPath(fileListPath!)) ?? ".";
                var hoErrorCsv = Path.Combine(hoOutputDir, "mbxhub-hash-only-errors.csv");

                // NDJSON sink: one shared FileStream + lock so concurrent workers can
                // append envelopes without partial-line interleaving. Truncate-and-write
                // (FileMode.Create) so each rig run starts from a clean manifest.
                FileStream? hoOutputStream = null;
                object hoOutputLock = new object();
                if (!string.IsNullOrEmpty(hashOutputPath))
                {
                    var hoFullOutPath = Path.GetFullPath(hashOutputPath!);
                    var hoOutDir = Path.GetDirectoryName(hoFullOutPath);
                    if (!string.IsNullOrEmpty(hoOutDir) && !Directory.Exists(hoOutDir))
                        Directory.CreateDirectory(hoOutDir!);
                    hoOutputStream = new FileStream(hoFullOutPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                }

                var hoProcessed = 0;
                var hoFailed = 0;
                var hoWritten = 0;
                var hoErrors = new ConcurrentBag<string>();
                var hoSw = System.Diagnostics.Stopwatch.StartNew();

                Parallel.ForEach(hoPaths, new ParallelOptions
                {
                    MaxDegreeOfParallelism = parallelism,
                    CancellationToken = CancellationToken.None
                }, filePath =>
                {
                    if (!File.Exists(filePath))
                    {
                        Interlocked.Increment(ref hoFailed);
                        hoErrors.Add($"{filePath}\tfile not found");
                        Console.Error.WriteLine($"[SKIP] {filePath}: file not found");
                        return;
                    }

                    try
                    {
                        var fi = new FileInfo(filePath);
                        var fpV1 = ComputeFingerprintV1(filePath, fi.Length, out var fpErr);
                        if (fpV1 == null)
                        {
                            Interlocked.Increment(ref hoFailed);
                            hoErrors.Add($"{filePath}\t{fpErr}");
                            Console.Error.WriteLine($"[FAIL] {Path.GetFileName(filePath)}: {fpErr}");
                            return;
                        }

                        string? streamSha = null;
                        // Source for audioStreamSha256 piggybacks on fingerprint.v1's same-parse
                        // result (audioHead64kMd5Source). Both reads come from the same TagLib
                        // open above (ComputeFingerprintV1), so they share invariant validity.
                        // "whole-file-start" on fp ↔ "whole-file" on stream — different value
                        // namespace, same semantic (invariant region was unavailable).
                        string? streamSource = null;
                        if (hashLevel == "stream")
                        {
                            streamSha = ComputeAudioStreamSha256(filePath, fpV1.InvariantStart, fpV1.InvariantEnd, out var shaErr);
                            if (streamSha == null)
                            {
                                Interlocked.Increment(ref hoFailed);
                                hoErrors.Add($"{filePath}\t{shaErr}");
                                Console.Error.WriteLine($"[FAIL] {Path.GetFileName(filePath)}: {shaErr}");
                                return;
                            }
                            streamSource = fpV1.AudioHead64kMd5Source == "invariant" ? "invariant" : "whole-file";
                        }

                        // Append NDJSON manifest line. Worker pool serializes writes via
                        // hoOutputLock so concurrent envelopes don't interleave.
                        bool wroteManifest = false;
                        try
                        {
                            var envelope = BuildIdentityOnlyEnvelope(filePath, fi.Length, fpV1, streamSha, hashLevel!, streamSource);
                            lock (hoOutputLock)
                            {
                                hoOutputStream!.Write(envelope, 0, envelope.Length);
                                hoOutputStream.WriteByte((byte)'\n');
                            }
                            wroteManifest = true;
                            Interlocked.Increment(ref hoWritten);
                        }
                        catch (Exception ex)
                        {
                            hoErrors.Add($"{filePath}\toutput-write: {ex.Message}");
                            Console.Error.WriteLine($"[FAIL] {Path.GetFileName(filePath)}: output-write {ex.Message}");
                        }

                        Interlocked.Increment(ref hoProcessed);
                        if (wroteManifest)
                            Console.Error.WriteLine($"[OK] {Path.GetFileName(filePath)}");
                        else
                            Interlocked.Increment(ref hoFailed);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref hoFailed);
                        hoErrors.Add($"{filePath}\t{ex.Message}");
                        Console.Error.WriteLine($"[FAIL] {Path.GetFileName(filePath)}: {ex.Message}");
                    }
                });

                hoSw.Stop();

                if (hoOutputStream != null)
                {
                    try { hoOutputStream.Flush(); hoOutputStream.Dispose(); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  WARNING: could not close NDJSON manifest cleanly: {ex.Message}");
                    }
                }

                if (!hoErrors.IsEmpty)
                {
                    try
                    {
                        File.WriteAllLines(hoErrorCsv,
                            new[] { "path\terror" }.Concat(hoErrors),
                            Encoding.UTF8);
                        Console.Error.WriteLine($"  Errors written to: {hoErrorCsv}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  WARNING: could not write error CSV: {ex.Message}");
                    }
                }

                Console.Error.WriteLine($"Done: {hoProcessed} processed ({hoWritten} written), {hoFailed} failed in {hoSw.Elapsed.TotalSeconds:F1}s");
                Environment.ExitCode = hoFailed > 0 ? 1 : 0;
                return;
            }

            // --file-list / --folder mode: batch analysis from one of three path sources.
            //   --folder <dir>      walk the directory recursively for audio files
            //   --file-list <path>  read paths from a UTF-8 text file
            //   --file-list -       read paths from stdin (one per line, # comments)
            if (fileListMode)
            {
                List<string> filePaths;

                // Phase 5.1 — skipped-CSV resolves like errors.csv would: next to the
                // moods file if --moods is set, else under the folder for --folder mode,
                // else current dir. Folder walk and worker both append rows here.
                string flSkippedDir = !string.IsNullOrEmpty(analyzeFileMoods)
                    ? (Path.GetDirectoryName(Path.GetFullPath(analyzeFileMoods)) ?? ".")
                    : (folderMode && !string.IsNullOrEmpty(folderPath)
                        ? Path.GetFullPath(folderPath)
                        : Environment.CurrentDirectory);
                var flSkippedPath = Path.Combine(flSkippedDir, "mbxmoods-skipped.csv");

                // Explicit exclusions — loaded once for the whole batch (walk + workers).
                // A missing file excludes nothing; a broken one refuses the scan.
                if (!_noExclusions)
                {
                    if (!ValidateExplicitExclusionsPath(out var flExclPathErr))
                    {
                        Console.Error.WriteLine($"Error: {flExclPathErr}");
                        Environment.ExitCode = 1;
                        return;
                    }
                    _exclusions = ExclusionStore.Load(_exclusionsPath ?? Path.Combine(flSkippedDir, ExclusionStore.FileName), out var flExclErr);
                    if (flExclErr != null)
                    {
                        Console.Error.WriteLine($"Error: {flExclErr}");
                        Console.Error.WriteLine("Refusing to scan: fix the exclusion file, or pass --no-exclusions.");
                        Environment.ExitCode = 1;
                        return;
                    }
                    foreach (var flDiag in _exclusions.Diagnostics)
                        Console.Error.WriteLine($"  WARNING: exclusion rule ignored — {flDiag}");
                }

                if (folderMode)
                {
                    Console.Error.WriteLine($"Walking folder: {folderPath}");
                    int unsupportedCount = 0;
                    var walked = new List<string>();
                    foreach (var p in Directory.EnumerateFiles(folderPath!, "*", SearchOption.AllDirectories))
                    {
                        var ext = Path.GetExtension(p);
                        if (string.IsNullOrEmpty(ext)) continue;
                        if (AudioExtensions.Contains(ext)) { walked.Add(p); continue; }
                        // Non-audio: ledger the recognized structural types (DSD / video /
                        // playlist-redirector) so a .mp4 / .m3u in the tree is visible rather
                        // than silently dropped (M7). Genuinely irrelevant files (art, text,
                        // logs) are ignored without a row so the ledger is not flooded.
                        var folderSkip = StructuralSkipReason(p);
                        if (folderSkip != null)
                        {
                            unsupportedCount++;
                            AppendSkipped(flSkippedPath, p, ext, folderSkip);
                            Console.Error.WriteLine($"[skipped {StructuralSkipConsoleTag(folderSkip)}] {p}");
                        }
                    }
                    filePaths = walked;
                    Console.Error.WriteLine($"  Found {filePaths.Count} audio file(s)" +
                        (unsupportedCount > 0 ? $" (skipped {unsupportedCount} non-audio: DSD/video/playlist — see mbxmoods-skipped.csv)" : ""));
                }
                else if (fileListPath == "-")
                {
                    Console.Error.WriteLine("Reading file list from stdin (one path per line, # comments, EOF to start)...");
                    filePaths = new List<string>();
                    string? line;
                    while ((line = Console.In.ReadLine()) != null)
                    {
                        var trimmed = line.Trim();
                        if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.StartsWith("#"))
                            filePaths.Add(trimmed);
                    }
                    Console.Error.WriteLine($"  Read {filePaths.Count} path(s) from stdin");
                }
                else
                {
                    if (!File.Exists(fileListPath!))
                    {
                        Console.Error.WriteLine($"Error: File list not found: {fileListPath}");
                        Environment.ExitCode = 1;
                        return;
                    }
                    filePaths = File.ReadAllLines(fileListPath!, Encoding.UTF8)
                        .Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith("#"))
                        .Select(line => line.Trim())
                        .ToList();
                }

                if (filePaths.Count == 0)
                {
                    Console.Error.WriteLine("Error: No paths to process.");
                    Environment.ExitCode = 1;
                    return;
                }

                Console.Error.WriteLine($"Processing {filePaths.Count} files (parallelism: {parallelism})");

                // Find Essentia (mirror MoodsMode resolution).
                var flBaseDir = AppDomain.CurrentDomain.BaseDirectory;
                var flEssentiaExe = FindTool("essentia_streaming_extractor_music.exe", flBaseDir, Environment.CurrentDirectory);
                if (flEssentiaExe == null)
                {
                    Console.Error.WriteLine("Error: essentia_streaming_extractor_music.exe not found");
                    Environment.ExitCode = 2;
                    return;
                }

                var flProcessed = 0;
                var flAnalyzed = 0;
                var flCachedByMtime = 0;
                var flCachedByHeadPath = 0;  // tier 1.5: same path, mtime drifted, head-64k says tags-only
                var flCachedByShaPath = 0;
                var flCachedByShaCross = 0;
                var flFailed = 0;
                var flDsdSkipped = 0;
                var flSmfmAdded = 0;
                var flErrors = new ConcurrentBag<string>();
                var flSw = System.Diagnostics.Stopwatch.StartNew();

                // Accumulate for moods file (optional)
                var flMoodsTracks = new ConcurrentDictionary<string, TrackEntry>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(analyzeFileMoods) && File.Exists(analyzeFileMoods))
                    LoadExistingMoods(analyzeFileMoods!, flMoodsTracks);

                // Cross-index for cache reuse — same shape as MoodsMode. Built once
                // before the worker pool spins up. Plugin-driven workflows where this
                // mode is invoked repeatedly (e.g. after MusicBee scans add new files)
                // get path-cache hits ~free; only genuinely new files run Essentia.
                Dictionary<string, (TrackEntry Entry, string OldKey)>? flMoodShaIndex =
                    BuildHashIndex(flMoodsTracks, e => e.AudioStreamSha256);
                if (flMoodsTracks.Count > 0)
                    Console.Error.WriteLine($"  Loaded {flMoodsTracks.Count} existing entries (sha={flMoodShaIndex?.Count ?? 0})");

                // Two-lane triage (see MoodsMode for the rationale): the per-file body below
                // is IDENTICAL to the single loop it replaces. Split dispatch only, so cheap
                // cache hits catch up fast while the slow analyses spread evenly across threads.
                Action<string> processFile = filePath =>
                {
                    if (!File.Exists(filePath))
                    {
                        // Missing is a structural skip, not an analysis failure (M6): ledger it
                        // to mbxmoods-skipped.csv like MoodsMode does, count it as a skip (reuse
                        // the counter fl already reports), and DON'T trip exit 1 — a piped path
                        // that no longer exists is not a scan failure. CLAUDE.md:98 requires the
                        // skipped.csv row on all scan modes.
                        var flMissReason = MissingFileSkipReason(filePath);
                        AppendSkipped(flSkippedPath, filePath, GetExtensionSafe(filePath), flMissReason);
                        Console.Error.WriteLine($"[skipped missing] {filePath} ({flMissReason})");
                        Interlocked.Increment(ref flDsdSkipped);
                        return;
                    }

                    // Phase 5.1 — structural skip (DSD, video, playlist/redirector):
                    // bail before any TagLib / Essentia / fingerprint work. (--folder
                    // mode already filters video/DSD/non-audio at walk time via its
                    // AudioExtensions allowlist; this catches --file-list and stdin
                    // sources, which have no such pre-filter.)
                    var flStructReason = StructuralSkipReason(filePath);
                    if (flStructReason != null)
                    {
                        var flSkipExt = Path.GetExtension(filePath);
                        AppendSkipped(flSkippedPath, filePath, flSkipExt, flStructReason);
                        Console.Error.WriteLine($"[skipped {StructuralSkipConsoleTag(flStructReason)}] {Path.GetFileName(filePath)}");
                        Interlocked.Increment(ref flDsdSkipped);
                        return;
                    }

                    SourceHandle? flStagedSrc = null;
                    try
                    {
                        var fullPath = Path.GetFullPath(filePath);
                        var hasMoods = !string.IsNullOrEmpty(analyzeFileMoods);

                        // Exclusion check runs before any cache tier — an excluded file
                        // never enters the catalog, so it can never hit tier 1, and
                        // falling through to tier 4 would stage + hash it (a full network
                        // copy on UNC) every single run for nothing. Matched on fullPath
                        // (not the raw filePath) so a relative file-list entry can't dodge
                        // an absolute file rule or a folder fragment.
                        var flGenre = (!_noExclusions && _exclusions.HasGenreRules) ? ReadGenreForExclusion(fullPath) : null;
                        if (!_noExclusions && _exclusions.IsExcluded(fullPath, flGenre, out var flExclReason))
                        {
                            AppendSkipped(flSkippedPath, filePath, GetExtensionSafe(filePath), $"excluded (rule: {flExclReason})");
                            Console.Error.WriteLine($"[skipped excluded: {flExclReason}] {Path.GetFileName(filePath)}");
                            Interlocked.Increment(ref flDsdSkipped);   // reuse the skip counter fl already reports
                            return;
                        }

                        // Capture mtime ONCE at entry; threaded through every tier so the
                        // recorded mtime matches the bytes any tier-2/3/4 body read or
                        // worker block analyzes. (FIX 5 — pairs with the source-handle
                        // snapshot for the staging happy path; this is the non-staged
                        // fallback / cache-hit path.)
                        DateTime currentLastMod = DateTime.MinValue;
                        try { currentLastMod = File.GetLastWriteTimeUtc(filePath); } catch { }
                        long flFileSize = 0;
                        try { flFileSize = new FileInfo(filePath).Length; } catch { }

                        // Lazy staging — opened on first tier-2/3/4 body read or at cache miss.
                        // Tier-1 (path-mtime equality) doesn't open it.
                        string EnsureStagedSrc()
                        {
                            flStagedSrc ??= OpenStagedSource(filePath, _stageOpts, flFileSize);
                            return flStagedSrc.Path;
                        }

                        // One body read serves tiers 2/3/4: both digests come from a single pass
                        // over the staged copy. Lazy — tier-1 hits never read the body.
                        (string? md5, string? sha, string? leg)? flBodyHashes = null;
                        (string? md5, string? sha, string? leg) EnsureBodyHashes()
                        {
                            if (flBodyHashes == null)
                            {
                                var (m, s, _) = ComputeFileMd5AndAudioSha(EnsureStagedSrc(), flFileSize, out _, out var leg);
                                flBodyHashes = (m, s, leg);
                            }
                            return flBodyHashes.Value;
                        }

                        // Cache hierarchy — same tiers as MoodsMode. Plugin-driven
                        // workflows (re-invoke after MusicBee scan finds new files) get
                        // path-mtime hits ~free; only new / changed audio runs Essentia.
                        if (hasMoods)
                        {
                            // Tier 1: path-mtime hit — no body read, staging not opened.
                            if (flMoodsTracks.TryGetValue(fullPath, out var fEx)
                                && HasCurrentFeatures(fEx.Features))
                            {
                                if (TruncateToSeconds(currentLastMod) == TruncateToSeconds(fEx.LastModified))
                                {
                                    var freshTags = ExtractFileTags(filePath);
                                    var flMtimeEntry = RebuildCacheEntryFromTags(
                                        fEx, freshTags.Artist, freshTags.Title, freshTags.Album,
                                        freshTags.Genre, fullPath, currentLastMod, null, null);
                                    var flMtimeSmfmTag = ApplySmfmInPlace(flMtimeEntry.Features, filePath, fEx.Features.SmfmScores) ? " +smfm" : "";
                                    if (flMtimeSmfmTag.Length > 0) Interlocked.Increment(ref flSmfmAdded);
                                    flMoodsTracks[fullPath] = flMtimeEntry;
                                    Interlocked.Increment(ref flProcessed);
                                    Interlocked.Increment(ref flCachedByMtime);
                                    Console.Error.WriteLine($"[CACHED{flMtimeSmfmTag}] {Path.GetFileName(filePath)}");
                                    return;
                                }

                                // Tier 1.5: quick tags-only check — ~64 KB read on the ORIGINAL
                                // path (no staging: copying the whole file would defeat the point).
                                // Evidence-based: head-64k + audio props must match the stored
                                // fingerprint; any doubt falls through to the full-SHA tier below.
                                // fileMd5 is cleared, not recomputed — the tag write invalidated
                                // it and recomputing costs the full read this tier avoids
                                // (--verify --backfill refills it).
                                FingerprintV1? flQuickFp = null;
                                if (_quickCache && fEx.FingerprintV1 != null)
                                {
                                    flQuickFp = ComputeFingerprintV1(filePath, flFileSize, out _);
                                    if (flQuickFp != null && IsTagsOnlyChange(flQuickFp, fEx.FingerprintV1))
                                    {
                                        var freshTags = ExtractFileTags(filePath);
                                        var flHeadEntry = RebuildCacheEntryFromTags(
                                            fEx, freshTags.Artist, freshTags.Title, freshTags.Album,
                                            freshTags.Genre, fullPath, currentLastMod, null, flQuickFp);
                                        flHeadEntry.FileMd5 = null;  // stale whole-file MD5; --verify --backfill refills
                                        var flHeadSmfmTag = ApplySmfmInPlace(flHeadEntry.Features, filePath, fEx.Features.SmfmScores) ? " +smfm" : "";
                                        if (flHeadSmfmTag.Length > 0) Interlocked.Increment(ref flSmfmAdded);
                                        flMoodsTracks[fullPath] = flHeadEntry;
                                        Interlocked.Increment(ref flProcessed);
                                        Interlocked.Increment(ref flCachedByHeadPath);
                                        Console.Error.WriteLine($"[CACHED·head{flHeadSmfmTag}] {Path.GetFileName(filePath)}");
                                        return;
                                    }
                                }

                                // Tier 2: path-sha hit (mtime drifted but audio bytes unchanged).
                                // Body reads go through the staged copy.
                                if (!string.IsNullOrEmpty(fEx.AudioStreamSha256))
                                {
                                    var flStagedPath = EnsureStagedSrc();
                                    var flHashes = EnsureBodyHashes();
                                    var recomputedSha = flHashes.sha;
                                    bool flShaHit = !string.IsNullOrEmpty(recomputedSha)
                                        && string.Equals(recomputedSha, fEx.AudioStreamSha256, StringComparison.OrdinalIgnoreCase);
                                    bool flLegacyHit = !flShaHit && !string.IsNullOrEmpty(flHashes.leg)
                                        && string.Equals(flHashes.leg, fEx.AudioStreamSha256, StringComparison.OrdinalIgnoreCase);
                                    if (flShaHit || flLegacyHit)
                                    {
                                        var refreshedMd5 = flHashes.md5;
                                        var refreshedFp = flQuickFp ?? ComputeFingerprintV1(flStagedPath, flFileSize, out _);
                                        var freshTags = ExtractFileTags(flStagedPath);
                                        var flShaPathEntry = flLegacyHit
                                            ? RebuildCacheEntryFromTags(
                                                fEx, freshTags.Artist, freshTags.Title, freshTags.Album,
                                                freshTags.Genre, fullPath, currentLastMod, refreshedMd5, refreshedFp, recomputedSha, "flac-frames")
                                            : RebuildCacheEntryFromTags(
                                                fEx, freshTags.Artist, freshTags.Title, freshTags.Album,
                                                freshTags.Genre, fullPath, currentLastMod, refreshedMd5, refreshedFp);
                                        if (!_fileMd5Enabled) flShaPathEntry.FileMd5 = null;  // unconsumed field; not written without --file-md5
                                        var flShaPathSmfmTag = ApplySmfmInPlace(flShaPathEntry.Features, filePath, fEx.Features.SmfmScores) ? " +smfm" : "";
                                        if (flShaPathSmfmTag.Length > 0) Interlocked.Increment(ref flSmfmAdded);
                                        flMoodsTracks[fullPath] = flShaPathEntry;
                                        Interlocked.Increment(ref flProcessed);
                                        Interlocked.Increment(ref flCachedByShaPath);
                                        Console.Error.WriteLine($"[CACHED·sha{flShaPathSmfmTag}] {Path.GetFileName(filePath)}");
                                        return;
                                    }
                                }
                            }

                            // Tier 4: cross-SHA hit (file moved AND tag-edited)
                            if (flMoodShaIndex != null)
                            {
                                var flStagedPath = EnsureStagedSrc();
                                var flXHashes = EnsureBodyHashes();
                                var localSha = flXHashes.sha;
                                (TrackEntry Entry, string OldKey) xs = default;
                                bool flXHit = !string.IsNullOrEmpty(localSha) && flMoodShaIndex.TryGetValue(localSha!, out xs);
                                bool flXLegacyHit = !flXHit && !string.IsNullOrEmpty(flXHashes.leg)
                                    && flMoodShaIndex.TryGetValue(flXHashes.leg!, out xs);
                                if ((flXHit || flXLegacyHit)
                                    && HasCurrentFeatures(xs.Entry!.Features))
                                {
                                    var refreshedMd5 = flXHashes.md5;
                                    var refreshedFp = ComputeFingerprintV1(flStagedPath, flFileSize, out _);
                                    var freshTags = ExtractFileTags(flStagedPath);
                                    var flCrossShaEntry = flXLegacyHit
                                        ? RebuildCacheEntryFromTags(
                                            xs.Entry, freshTags.Artist, freshTags.Title, freshTags.Album,
                                            freshTags.Genre, fullPath, currentLastMod, refreshedMd5, refreshedFp, localSha, "flac-frames")
                                        : RebuildCacheEntryFromTags(
                                            xs.Entry, freshTags.Artist, freshTags.Title, freshTags.Album,
                                            freshTags.Genre, fullPath, currentLastMod, refreshedMd5, refreshedFp);
                                    if (!_fileMd5Enabled) flCrossShaEntry.FileMd5 = null;  // unconsumed field; not written without --file-md5
                                    var flCrossShaSmfmTag = ApplySmfmInPlace(flCrossShaEntry.Features, filePath, xs.Entry.Features.SmfmScores) ? " +smfm" : "";
                                    if (flCrossShaSmfmTag.Length > 0) Interlocked.Increment(ref flSmfmAdded);
                                    flMoodsTracks[fullPath] = flCrossShaEntry;
                                    RemoveIfMoved(flMoodsTracks, xs.OldKey!);
                                    Interlocked.Increment(ref flProcessed);
                                    Interlocked.Increment(ref flCachedByShaCross);
                                    Console.Error.WriteLine($"[CACHED·sha{flCrossShaSmfmTag}] {Path.GetFileName(filePath)}");
                                    return;
                                }
                            }
                        }

                        // Cache miss — full Essentia + identity ride-along on the staged copy.
                        EnsureStagedSrc();
                        var flResults = RunSourceWorkers(
                            flEssentiaExe, flStagedSrc!, flFileSize, filePath, knownDurationSec: 0,
                            extractTags: true, _stageOpts, CancellationToken.None);

                        var features = flResults.Features;
                        var fileMd5 = flResults.FileMd5;
                        var fingerprintV1 = flResults.FingerprintV1;
                        var audioStreamSha256 = flResults.AudioStreamSha256;
                        var audioStreamSha256Source = flResults.AudioStreamSha256Source;
                        var tags = flResults.Tags!;  // extractTags: true
                        var bitUsage = flResults.BitUsage;
                        var flSmfmResult = flResults.Smfm;

                        if (features == null)
                        {
                            Interlocked.Increment(ref flFailed);
                            flErrors.Add($"{filePath}: {flResults.EssentiaError}");
                            Console.Error.WriteLine($"[FAIL] {Path.GetFileName(filePath)}: {flResults.EssentiaError}");
                            return;
                        }

                        features.Artist = tags.Artist;
                        features.Title = tags.Title;
                        features.Album = tags.Album;
                        features.Genre = tags.Genre;
                        features.FilePath = filePath;
                        if (bitUsage != null) features.BitUsage = bitUsage;
                        if (flResults.HfEnergyRatio.HasValue) { features.HfEnergyRatio = flResults.HfEnergyRatio; features.HfEnergyMethod = flResults.HfEnergyMethod; }
                        if (flResults.HfSpectralStructure != null) features.HfSpectralStructure = flResults.HfSpectralStructure;
                        var flOkSmfmTag = "";
                        if (flSmfmResult.HasValue)
                        {
                            var flSmfmWasAbsent = !(flMoodsTracks.TryGetValue(fullPath, out var flSmfmPrior) && flSmfmPrior.Features?.SmfmScores != null);
                            features.SmfmScores      = flSmfmResult.Value.Scores;
                            features.SmfmChannel     = flSmfmResult.Value.Channel;
                            features.SmfmChannelName = SmfmReader.ChannelName(flSmfmResult.Value.Channel);
                            features.SmfmBpm           = Math.Round(flSmfmResult.Value.Bpm, 3);
                            if (flSmfmWasAbsent) { flOkSmfmTag = " +smfm"; Interlocked.Increment(ref flSmfmAdded); }
                        }

                        // FIX 5 — snapshot from SourceHandle. Falls back to the entry-time
                        // capture if the snapshot threw (DateTime.MinValue).
                        DateTime flLastMod = flStagedSrc!.SourceLastWriteUtc != DateTime.MinValue
                            ? flStagedSrc.SourceLastWriteUtc
                            : currentLastMod;
                        var trackEntry = new TrackEntry
                        {
                            Features = features,
                            LastModified = flLastMod,
                            AnalysisDurationSecs = 0, // individual timing not tracked in batch
                            FileMd5 = fileMd5,
                            AudioStreamSha256 = string.IsNullOrEmpty(audioStreamSha256) ? null : audioStreamSha256,
                            AudioStreamSha256Source = audioStreamSha256Source,
                            FingerprintV1 = fingerprintV1,
                        };

                        // Accumulate for moods file (only saved if --moods is set).
                        if (!string.IsNullOrEmpty(analyzeFileMoods))
                        {
                            var flKey = Path.GetFullPath(filePath);
                            flMoodsTracks[flKey] = trackEntry;
                        }

                        Interlocked.Increment(ref flProcessed);
                        Interlocked.Increment(ref flAnalyzed);
                        Console.Error.WriteLine($"[OK{flOkSmfmTag}] {Path.GetFileName(filePath)}");
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref flFailed);
                        flErrors.Add($"{filePath}: {ex.Message}");
                        Console.Error.WriteLine($"[FAIL] {Path.GetFileName(filePath)}: {ex.Message}");
                    }
                    finally { flStagedSrc?.Dispose(); }
                };

                // Perf-hint split (no file IO): a path already in the loaded catalog with
                // complete features almost certainly hits a cache tier -> fast lane; new /
                // absent / stale paths -> work lane. Advisory only: the body re-runs every
                // cache tier, so a mis-hinted path is still correct, just under the other
                // partitioner. The dict is keyed by Path.GetFullPath, so the hint matches the
                // body's tier-1 key; an empty dict (no --moods) sends everything to the work lane.
                var flLikelyCached = new List<string>();
                var flLikelyWork = new List<string>();
                foreach (var filePath in filePaths)
                {
                    bool flHint = false;
                    try
                    {
                        flHint = flMoodsTracks.TryGetValue(Path.GetFullPath(filePath), out var flHintEntry)
                                 && HasCurrentFeatures(flHintEntry.Features);
                    }
                    catch { flHint = false; }
                    if (flHint) flLikelyCached.Add(filePath);
                    else flLikelyWork.Add(filePath);
                }
                if (flLikelyCached.Count + flLikelyWork.Count != filePaths.Count)
                    throw new InvalidOperationException("two-lane split dropped or duplicated files");
                var flOptions = new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = CancellationToken.None };
                // Fast lane: default (chunked) partitioner -> % races up on cheap cache hits.
                Parallel.ForEach(flLikelyCached, flOptions, processFile);
                // Work lane: single-item NoBuffering -> slow analyses spread across all threads.
                Parallel.ForEach(
                    Partitioner.Create(flLikelyWork, EnumerablePartitionerOptions.NoBuffering),
                    flOptions, processFile);

                flSw.Stop();

                // Save moods file if --moods specified
                if (!string.IsNullOrEmpty(analyzeFileMoods) && flMoodsTracks.Count > 0)
                {
                    SaveResults(analyzeFileMoods!, flMoodsTracks);
                    Console.Error.WriteLine($"Saved {flMoodsTracks.Count} entries to: {analyzeFileMoods}");
                }

                int flCachedTotal = flCachedByMtime + flCachedByHeadPath + flCachedByShaPath + flCachedByShaCross;

                // Summary JSON on stdout
                if (jsonOutput || flFailed > 0)
                {
                    var summary = new
                    {
                        processed = flProcessed,
                        analyzed = flAnalyzed,
                        cached = flCachedTotal,
                        cachedByMtime = flCachedByMtime,
                        cachedByHeadPath = flCachedByHeadPath,
                        cachedByShaPath = flCachedByShaPath,
                        cachedByShaCross = flCachedByShaCross,
                        failed = flFailed,
                        skipped = flDsdSkipped,
                        smfmAdded = flSmfmAdded,
                        elapsed = flSw.Elapsed.TotalSeconds,
                        errors = flErrors.ToArray()
                    };
                    var summaryJson = System.Text.Json.JsonSerializer.Serialize(summary,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    Console.WriteLine(summaryJson);
                }

                Console.Error.WriteLine($"Done: {flProcessed} processed ({flCachedTotal} cached, {flAnalyzed} analyzed), {flFailed} failed, {flDsdSkipped} skipped{(flSmfmAdded > 0 ? $", {flSmfmAdded} SMFM-added" : "")} in {flSw.Elapsed.TotalSeconds:F1}s");
                EmitStagingSummary();
                if (!string.IsNullOrEmpty(analyzeFileMoods))
                    ReportCatalog(analyzeFileMoods!, flMoodsTracks.Values, statsDetailThreshold);
                Environment.ExitCode = flFailed > 0 ? 1 : 0;
                return;
            }

            // --fixup --remap "<old>=<new>" — wholesale prefix swap on mbxmoods.json keys,
            // no iTunes XML required. Positional arg is the moods file path directly
            // (defaults to ./mbxmoods.json when omitted). Use case: library scanned at
            // one path (e.g. local copy at D:\Music\) needs to be re-keyed to a different
            // root (e.g. \\nas\share\Music\) so a downstream consumer reads the right paths.
            if (fixupMode && !string.IsNullOrEmpty(remapPrefix))
            {
                var parts = remapPrefix!.Split(new[] { '=' }, 2);
                if (parts.Length != 2 || parts[0].Length == 0)
                {
                    Console.WriteLine("Error: --remap requires the form <oldPrefix>=<newPrefix> (oldPrefix must be non-empty).");
                    Environment.ExitCode = 2;
                    return;
                }
                var moodsForRemap = xmlPath ?? "mbxmoods.json";
                RunFixupRemap(moodsForRemap, parts[0], parts[1]);
                return;
            }

            xmlPath = ResolveITunesXml(xmlPath);

            if (!File.Exists(xmlPath))
            {
                Console.WriteLine($"iTunes library not found: {xmlPath}");
                Console.WriteLine("Probed: exe-dir parent, exe-dir, current working directory.");
                PrintHelp();
                return;
            }

            var outputDir = Path.GetDirectoryName(Path.GetFullPath(xmlPath)) ?? ".";
            var moodsPath = Path.Combine(outputDir, "mbxmoods.json");
            // --chunk: each shard writes to its own hostname-suffixed moods + errors
            // file so two machines pointing at the same library directory don't
            // stomp each other. Plugin discovery picks the union back up by glob.
            string? chunkHostSuffix = null;
            if (chunkTotal > 0)
            {
                chunkHostSuffix = SanitizeForFilename(Environment.MachineName);
                moodsPath = InsertFilenameSuffix(moodsPath, chunkHostSuffix);
            }
            var logPath = Path.Combine(outputDir, "truedat.log");
            TeeWriter? tee = null;
            if (auditLog)
            {
                tee = new TeeWriter(Console.Out, logPath);
                Console.SetOut(tee);
                _tee = tee;
            }

            var modeList = new List<string>();
            if (checkFilenames) modeList.Add("check-filenames");
            if (duplicatesMode) modeList.Add("duplicates");
            if (migrateMode) modeList.Add("migrate");
            if (fixupMode) modeList.Add("fixup");
            if (analyzeMode || (!checkFilenames && !duplicatesMode && !migrateMode && !fixupMode)) modeList.Add("analyze");
            Console.WriteLine($"  Modes: {string.Join("+", modeList)} | Parallelism: {parallelism}{(retryErrors ? " | RetryErrors" : "")}");

            // Clean up orphaned hardlinks from previous crashed runs
            CleanupOrphanedFiles();

            if (checkFilenames) { RunCheckFilenames(xmlPath, outputDir); if (auditLog) Console.WriteLine($"Log:    {logPath}"); tee?.Dispose(); return; }
            if (duplicatesMode) { RunDuplicates(outputDir); if (auditLog) Console.WriteLine($"Log:    {logPath}"); tee?.Dispose(); return; }
            if (migrateMode) { RunMigrate(moodsPath); if (auditLog) Console.WriteLine($"Log:    {logPath}"); tee?.Dispose(); return; }
            if (fixupMode) { RunFixup(xmlPath, moodsPath); if (auditLog) Console.WriteLine($"Log:    {logPath}"); tee?.Dispose(); return; }

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var essentiaExe = FindTool("essentia_streaming_extractor_music.exe", baseDir, outputDir, Environment.CurrentDirectory);
            var catalogPath = FindCatalog(baseDir, Environment.CurrentDirectory,
                Path.GetDirectoryName(Path.GetDirectoryName(baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))) ?? "");

            Console.WriteLine("=== Tool Check ===");
            Console.WriteLine($"  truedat:    {VersionInfo.Display}");
            Console.WriteLine($"  App dir:    {baseDir}");
            Console.WriteLine($"  Output dir: {outputDir}");
            Console.WriteLine($"  Essentia:   {essentiaExe ?? "NOT FOUND"}");
            Console.WriteLine($"  ffmpeg:     {_ffmpegPath.Value ?? "not found (multi-channel files will be skipped)"}");

            // --- Source staging config summary + validation -----------------
            if (_stageOpts.NoStage)
            {
                Console.WriteLine("  Source staging: DISABLED (--no-stage)");
            }
            else
            {
                // Validate the staging dir is writable. Per-track failures are
                // robocopy semantics; an unusable dir at startup is a config bug.
                try
                {
                    Directory.CreateDirectory(_stageOpts.StageDir);
                    var probe = Path.Combine(_stageOpts.StageDir, $".truedat-write-probe-{Guid.NewGuid():N}");
                    File.WriteAllBytes(probe, new byte[] { 0 });
                    File.Delete(probe);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error: --stage-dir is not writable: {_stageOpts.StageDir}: {ex.Message}");
                    Environment.ExitCode = 1;
                    return;
                }
                Console.WriteLine($"  Source staging: enabled, dir={_stageOpts.StageDir}");
            }

            // Signal-extraction opt-outs — only print when at least one is set.
            if (_noBitUsage || _noHfAnalysis)
            {
                string bu = _noBitUsage   ? "DISABLED" : "enabled";
                string hf = _noHfAnalysis ? "DISABLED" : "enabled";
                Console.WriteLine($"  Signal extraction: bitUsage={bu} hfAnalysis={hf}");
            }
            Console.WriteLine($"  Catalog:    {(catalogPath != null ? catalogPath : "not found (run: python src/catalog-prep.py --download --build)")}");
            Console.WriteLine();

            if (essentiaExe == null)
            {
                Console.Error.WriteLine("Error: essentia_streaming_extractor_music.exe not found in any search directory.");
                Console.Error.WriteLine("Download from: https://essentia.upf.edu/extractors/");
                Environment.ExitCode = 2;
                tee?.Dispose();
                return;
            }

            var errorsPath = Path.Combine(outputDir, "mbxmoods-errors.csv");
            if (chunkHostSuffix != null)
                errorsPath = InsertFilenameSuffix(errorsPath, chunkHostSuffix);
            // Phase 5.1 — DSD/DSF skip ledger. Same dir / chunk-suffix convention
            // as errors.csv so two-machine scans don't stomp each other.
            var skippedPath = Path.Combine(outputDir, "mbxmoods-skipped.csv");
            if (chunkHostSuffix != null)
                skippedPath = InsertFilenameSuffix(skippedPath, chunkHostSuffix);

            Console.WriteLine($"Loading iTunes library: {xmlPath}");
            var tracks = ITunesParser.Parse(xmlPath, out var xmlIssues);
            if (_audit && xmlIssues != null)
                foreach (var issue in xmlIssues) Console.WriteLine(issue);
            Console.WriteLine($"Found {tracks.Count} tracks");
            // True library membership, captured BEFORE the video/remote/non-audio/exclusion
            // filters run. An mbxmoods.json entry whose path isn't in here is genuinely gone
            // from the library (→ --fixup); a still-present entry that a filter excluded
            // (e.g. a video file, or a track matching an exclusion rule) must NOT read as
            // an orphan. Feeds the wave-missing breakdown in ReportCatalog.
            var libraryKeys = new HashSet<string>(
                tracks.Where(t => !string.IsNullOrEmpty(t.Location)).Select(t => t.Location!),
                PathComparer.Instance);
            // Explicit exclusions. A missing file means nothing is excluded; a file we
            // cannot parse is fatal, because continuing would analyze everything while
            // the operator believes their rules are in force.
            var exclusionsFile = _exclusionsPath ?? ExclusionStore.Resolve(moodsPath);
            if (_noExclusions)
            {
                Console.WriteLine("  WARNING: --no-exclusions — the exclusion file is being ignored for this run");
            }
            else
            {
                if (!ValidateExplicitExclusionsPath(out var exclPathErr))
                {
                    Console.Error.WriteLine($"Error: {exclPathErr}");
                    Environment.ExitCode = 1;
                    tee?.Dispose();
                    return;
                }
                _exclusions = ExclusionStore.Load(exclusionsFile, out var exclusionError);
                if (exclusionError != null)
                {
                    Console.Error.WriteLine($"Error: {exclusionError}");
                    Console.Error.WriteLine("Refusing to scan: fix the exclusion file, or pass --no-exclusions to ignore it deliberately.");
                    Environment.ExitCode = 1;
                    tee?.Dispose();
                    return;
                }
                if (!_exclusions.IsEmpty)
                    Console.WriteLine($"  Exclusions: {_exclusions.Rules.Count} rule(s) from {exclusionsFile}");
                foreach (var diag in _exclusions.Diagnostics)
                    Console.Error.WriteLine($"  WARNING: exclusion rule ignored — {diag}");
            }

            // Scope this shard's ledger BEFORE the filters run (I-4). The filters themselves stay
            // library-wide — exclusion evaluation has to see every track so per-rule hit counts
            // are identical on every machine — but each shard writes only its own bucket's rows
            // into its own hostname-suffixed mbxmoods-skipped.csv, so the union of N ledgers is
            // the library rather than N copies of it.
            if (chunkTotal > 0)
            {
                _ledgerScope = p => ChunkOwns(p, chunkIndex, chunkTotal);
                _ledgerScopeNote = " (this chunk)";
            }

            tracks = FilterRemoteUrls(tracks, skippedPath);
            tracks = FilterExclusions(tracks, skippedPath);
            tracks = FilterVideoFiles(tracks, skippedPath);
            tracks = FilterNonAudio(tracks, skippedPath);

            // --chunk M/N: hash-mod assignment across machines. Each track's path
            // hashes to a fixed bucket via PathComparer's FNV-1a (deterministic
            // across processes/machines, separator-normalized, case-folded). Two
            // machines independently keep only "their" buckets — no need for
            // identical XMLs, identical track counts, or identical sort order.
            // Asymmetric libraries converge: each machine works the paths it
            // actually has, in the buckets it owns. Load balance is statistical,
            // ~equal at scale; locality isn't a real concern (Essentia is CPU-
            // bound at ~70s/track, not seek-bound).
            if (chunkTotal > 0)
            {
                int n = tracks.Count;
                int bucket = chunkIndex - 1;
                // Same predicate the ledger scope uses, so what a shard DOES and what it
                // RECORDS can never diverge (I-4).
                tracks = tracks.Where(t => ChunkOwns(t.Location, chunkIndex, chunkTotal)).ToList();
                Console.WriteLine($"Chunk {chunkIndex}/{chunkTotal} on {Environment.MachineName}: {tracks.Count} of {n} tracks (hash-mod, bucket {bucket})");
                Console.WriteLine($"Output: {moodsPath}");
            }

            // Single in-memory dataset — loaded from disk once, updated by workers, streamed on save.
            // Eliminates the old pattern of re-reading/re-parsing the entire JSON on every save.
            var allTracks = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
            int existingCount = LoadExistingMoods(moodsPath, allTracks);
            Console.WriteLine($"Existing moods: {existingCount}");

            // Cross-index keyed by audioStreamSha256 — invariant-region hash, stable
            // across tag edits. Catches "audio bytes unchanged but tags/path drifted",
            // including clean moves and cross-machine matching.
            Dictionary<string, (TrackEntry Entry, string OldKey)>? moodShaIndex = BuildHashIndex(allTracks, e => e.AudioStreamSha256);
            if (moodShaIndex != null)
                Console.WriteLine($"  SHA index:  {moodShaIndex.Count} entries available for tag-edit / cross-machine matching");

            // ETA model pre-flight: catalog membership splits the work list into
            // near-free cache hits vs full Essentia passes. Dictionary lookups only —
            // no file IO. The XML <Size> key supplies the byte total so remaining
            // analysis work is bytes-costed at the measured MB/s.
            {
                int etaNew = 0; long etaNewBytes = 0; long etaNewAudioMs = 0;
                foreach (var tt in tracks)
                {
                    if (string.IsNullOrEmpty(tt.Location)) continue;
                    // Count only what the scan will actually analyze, so this estimate does not
                    // diverge from the loop the way it used to (I1): a current-features entry is
                    // a cache hit, and a structural skip (DSD) or over-max-duration track is
                    // dropped in the loop, so neither is new work here either. Refresh-stale
                    // entries (see HasCurrentFeatures) DO re-analyze, so they stay in the pool.
                    // Still an upper bound — the sha/head cache tiers catch more hits at scan
                    // time, which needs IO this pre-flight avoids (hence "up to" in the print).
                    bool current = allTracks.TryGetValue(tt.Location, out var etaEntry) && HasCurrentFeatures(etaEntry.Features);
                    if (!IsAnalysisCandidate(current, StructuralSkipReason(tt.Location), OverLengthSkipReason(tt.TotalTimeMs / 1000, _maxEssentiaDurationSecs)))
                        continue;
                    etaNew++; etaNewBytes += tt.SizeBytes; etaNewAudioMs += tt.TotalTimeMs;
                }
                _etaNewTotal = etaNew;
                _etaNewBytesTotal = etaNewBytes;
                _etaNewAudioMsTotal = etaNewAudioMs;
                _scanParallelism = parallelism;
                if (etaNew > 0)
                {
                    var newMb = etaNewBytes / (1024.0 * 1024.0);
                    var newSizeTag = etaNewBytes <= 0 ? "" : newMb >= 1024 ? $" / {newMb / 1024.0:F1} GB" : $" / {newMb:F0} MB";
                    Console.WriteLine($"  New to catalog: up to {etaNew} track(s){newSizeTag} — full analysis expected (estimate; cache tiers may reduce this at scan time)");
                    // Duration-based pre-flight ETA (I2): bytes under-cost long low-bitrate
                    // speech by ~10x, so cost new work by audio duration × the catalog's
                    // measured RTF / parallelism — the model --preview already uses. The byte
                    // figure above stays as a secondary signal; this is the honest time.
                    var preflightRtf = MedianCatalogRtf(tracks, allTracks);
                    var preflightEta = PreflightEtaSecs(etaNewAudioMs, preflightRtf, parallelism);
                    if (preflightEta >= 0)
                        Console.WriteLine($"  Estimated time: {FormatTimeSpan(TimeSpan.FromSeconds(preflightEta))}  ({FormatTimeSpan(TimeSpan.FromMilliseconds(etaNewAudioMs))} of new audio, {preflightRtf:F2}x realtime, {Math.Max(1, parallelism)} workers)");
                    else
                        Console.WriteLine("  Estimated time: not estimable yet (no analyzed history to learn a rate from)");
                }
                if (!_refreshFeatures)
                {
                    int waveMissing = 0;
                    foreach (var e in allTracks.Values)
                        if (e.Features != null && e.Features.Mfcc != null && e.Features.Mfcc.Length > 0
                            && e.Features.AverageLoudness == null)
                            waveMissing++;
                    if (waveMissing > 0)
                        Console.WriteLine($"  {waveMissing:N0} entries lack the latest features — run: truedat --refresh-features");
                }
            }
            int flacRekeyed = 0;       // tier 2.5: FLAC transition rescue (pre-flac-frames sha + tag rewrite; props-gated re-key)
            int cachedByHeadPath = 0;  // tier 1.5: same path, mtime drifted, head-64k evidence says tags-only
            int cachedByShaPath = 0;   // tier A: same path, mtime drifted, audio bytes unchanged
            int cachedByShaCross = 0;  // tier B: different path, audio bytes unchanged
            int shaBackfilled = 0;     // tier 0 cache hits that gained audioStreamSha256 (legacy entries)
            int smfmAdded = 0;         // tracks that gained SMFM data this scan (file newly carries 12-TONE)

            Dictionary<string, string> existingErrors;
            if (retryErrors)
            {
                existingErrors = new Dictionary<string, string>(PathComparer.Instance);
                if (File.Exists(errorsPath))
                {
                    File.Delete(errorsPath);
                    Console.WriteLine("Errors CSV cleared (--retry-errors)");
                }
            }
            else
            {
                existingErrors = LoadExistingErrors(errorsPath);
            }
            Console.WriteLine($"Existing errors: {existingErrors.Count}");

            int cachedCount = 0;
            int analyzed = 0;
            int skipped = 0;
            int dsdSkipped = 0;
            int overLengthSkipped = 0;
            int missingSkipped = 0;
            int failed = 0;
            int timedOut = 0;
            int processed = 0;
            int total = tracks.Count;
            int lastSaveAnalyzed = 0;
            const int SaveInterval = 25;
            var saveLock = new object();

            var startTime = DateTime.Now;
            var sw = Stopwatch.StartNew();

            var cts = new CancellationTokenSource();
            var cancelRequested = 0;
            Console.CancelKeyPress += (_, e) =>
            {
                if (Interlocked.Exchange(ref cancelRequested, 1) == 0)
                {
                    e.Cancel = true;
                    Console.WriteLine();
                    Console.WriteLine("Ctrl+C received - finishing current tracks and saving...");
                    cts.Cancel();
                }
                else
                {
                    Console.WriteLine("Force exit.");
                }
            };

            WarnLowDiskSpace(outputDir);
            Console.WriteLine($"Started:     {startTime:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"Parallelism: {parallelism} threads");

            Console.WriteLine();

            try
            {
                // Two-lane triage: the per-track body below is IDENTICAL to the single
                // loop it replaces. We only split dispatch so the scan gets BOTH the fast
                // cache-hit catch-up (% races up) AND an even spread of the slow Essentia
                // analyses across threads. See the split + two Parallel.ForEach passes after
                // this lambda.
                Action<ITunesTrack> processTrack = t =>
                {
                    if (cts.IsCancellationRequested) return;
                    var current = Interlocked.Increment(ref processed);
                    // Outcome telemetry: thread-time + class per track, recorded in the
                    // finally below. Default "failed" covers exception paths.
                    var trackSw = Stopwatch.StartNew();
                    var trackClass = "failed";
                    // "Known" for ETA purposes = present AND feature-complete; a
                    // refresh-stale entry analyzes, so it lives in the new-work pool.
                    bool wasKnown = allTracks.TryGetValue(t.Location, out var wkEntry) && HasCurrentFeatures(wkEntry.Features);

                    // Long-path fallback: over-MAX_PATH paths fail normal Win32 IO
                    // ("file not found" even though the file exists). Managed IO on
                    // net48 accepts the \\?\ extended-length form, and \\?\ paths
                    // trigger staging (StartsWith(@"\\")), so subprocess tools
                    // (Essentia/ffmpeg) only ever see the short staged copy. Used
                    // for IO ONLY — t.Location stays the cache key, JSON key,
                    // fingerprint pathTail, and display path throughout.
                    var scanPath = t.Location.Length >= 260 ? ExtendedLengthPath(t.Location) : t.Location;

                    // A new-to-catalog track that exits via a skip path never analyzes —
                    // drain it from the ETA new-work pool or the estimate hangs on it
                    // (and stays suppressed when nothing has been measured yet).
                    void EtaDrainNewPool()
                    {
                        if (!wasKnown)
                        {
                            Interlocked.Increment(ref _etaNewDone);
                            Interlocked.Add(ref _etaNewBytesDone, t.SizeBytes);
                            Interlocked.Add(ref _etaNewAudioMsDone, t.TotalTimeMs);
                        }
                    }

                    // FIX 4 — lazy-opened staging handle, reused by every tier-2/3/4
                    // body read and the cache-miss worker fan-out. Tier-1 (path-mtime
                    // equality) does NOT open it; SHA-backfill within tier-1 still
                    // reads the original source so a single legacy-backfill body read
                    // doesn't trigger a stage (the savings come on tier-2/3/4 misses
                    // and full re-analysis, where the file is read multiple times).
                    SourceHandle? msStagedSrc = null;
                    long msSourceSize = 0;
                    string EnsureStagedSrc()
                    {
                        msStagedSrc ??= OpenStagedSource(scanPath, _stageOpts, msSourceSize);
                        return msStagedSrc.Path;
                    }

                    // One body read serves tiers 2/3/4: all digests come from a single pass
                    // over the staged copy. Lazy — tier-1 hits never read the body.
                    // msSourceSize is assigned lazily in multiple branches below, so resolve
                    // it at call time rather than capturing a stale 0.
                    // leg = legacy TagLib-region sha (FLAC only) — recognizes stored
                    // pre-flac-frames values so the transition costs zero re-analysis.
                    (string? md5, string? sha, string? leg)? msBodyHashes = null;
                    (string? md5, string? sha, string? leg) EnsureBodyHashes()
                    {
                        if (msBodyHashes == null)
                        {
                            if (msSourceSize == 0)
                                try { msSourceSize = new FileInfo(scanPath).Length; } catch { }
                            var (m, s, _) = ComputeFileMd5AndAudioSha(EnsureStagedSrc(), msSourceSize, out _, out var leg);
                            msBodyHashes = (m, s, leg);
                        }
                        return msBodyHashes.Value;
                    }
                    try
                    {
                        var pct = (current * 100) / total;
                        var eta = FormatEta(sw.Elapsed, current, total);

                        // Phase 5.1 — structural skip (DSD, video, playlist/redirector):
                        // bail before any cache lookup, TagLib, Essentia, or
                        // fingerprint helper. Belt-and-braces here — MoodsMode's list
                        // filters (FilterVideoFiles / FilterNonAudio) already remove
                        // video and playlist/redirector tracks before the scan loop —
                        // but this guard keeps every mode agreeing on the same
                        // definition of "structurally unanalyzable" rather than only
                        // the XML path. Existing entries in allTracks (if any) pass
                        // through unchanged on save.
                        var msStructReason = StructuralSkipReason(t.Location);
                        if (msStructReason != null)
                        {
                            var skipExt = Path.GetExtension(t.Location);
                            AppendSkipped(skippedPath, t.Location, skipExt, msStructReason);
                            Console.WriteLine($"[skipped {StructuralSkipConsoleTag(msStructReason)}] {t.Location}");
                            Interlocked.Increment(ref dsdSkipped);
                            EtaDrainNewPool();
                            trackClass = "skip·dsd";
                            return;
                        }

                        if (existingErrors.TryGetValue(t.Location, out var prevError))
                        {
                            Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name} (skip: {prevError})");
                            Interlocked.Increment(ref skipped);
                            EtaDrainNewPool();
                            trackClass = "skip·error";
                            return;
                        }

                        // Missing-file skip: catch it at scan entry with an accurate reason
                        // instead of letting it die deep in staging/Essentia as a generic
                        // analysis error. scanPath already carries the \\?\ fallback for
                        // over-MAX_PATH paths, so a miss here means the file is genuinely
                        // gone (not merely unreachable by normal Win32 IO).
                        if (!File.Exists(scanPath))
                        {
                            var reason = t.Location.Length >= 260
                                ? $"file not found (path {t.Location.Length} chars >= 260 MAX_PATH)"
                                : "file not found";
                            AppendSkipped(skippedPath, t.Location, GetExtensionSafe(t.Location), reason);
                            Console.WriteLine($"[skipped missing] {t.Location} ({reason})");
                            Interlocked.Increment(ref missingSkipped);
                            EtaDrainNewPool();
                            trackClass = "skip·missing";
                            return;
                        }
                        if (!ReferenceEquals(scanPath, t.Location) && _audit)
                            Console.WriteLine($"  DEBUG long-path: \\\\?\\ fallback engaged ({t.Location.Length} chars): {t.Location}");

                        // Check if already in moods and file unchanged
                        if (allTracks.TryGetValue(t.Location, out var existing))
                        {
                            try
                            {
                                var currentLastMod = File.GetLastWriteTimeUtc(scanPath);
                                if (TruncateToSeconds(currentLastMod) == TruncateToSeconds(existing.LastModified))
                                {
                                    // Re-extract when the canary says the entry is stale (pre-LRA /
                                    // pre-extended builds; with --refresh-features also entries
                                    // missing the 2026-07-22 tonal/rhythm wave).
                                    if (!HasCurrentFeatures(existing.Features))
                                    {
                                        if (_audit) Console.WriteLine($"  DEBUG cache: re-extracting (DR / extended missing)");
                                    }
                                    else
                                    {
                                        // Path-cache hit: same path, same mtime, canary passed.
                                        // Backfill fileMd5 if older entry lacked it — only under
                                        // --file-md5: this is a dedicated full read on the original
                                        // path (the wave that follows a retag scan's tier-1.5 clears).
                                        var refreshedMd5 = existing.FileMd5 ?? (_fileMd5Enabled ? ComputeFileMd5(t.Location) : null);
                                        // Backfill canonical audioStreamSha256 if missing — legacy entries
                                        // from before the Phase 2 identity layer lack it, which silently
                                        // weakens the cross-SHA cache tier on future runs. Cheap (~50ms).
                                        string? backfilledSha = null;
                                        string? backfilledShaSource = null;
                                        string backfillTag = "";
                                        if (string.IsNullOrEmpty(existing.AudioStreamSha256))
                                        {
                                            long fileSizeForBackfill = 0;
                                            try { fileSizeForBackfill = new FileInfo(scanPath).Length; } catch { }
                                            if (fileSizeForBackfill > 0)
                                            {
                                                var (sha, shaSource) = ComputeAudioStreamSha256FromFile(scanPath, fileSizeForBackfill, out _);
                                                if (!string.IsNullOrEmpty(sha))
                                                {
                                                    backfilledSha = sha;
                                                    backfilledShaSource = shaSource;
                                                    backfillTag = " +sha";
                                                    Interlocked.Increment(ref shaBackfilled);
                                                }
                                            }
                                        }
                                        var mtimeEntry = RebuildCacheEntry(existing, t, currentLastMod, refreshedMd5, null, backfilledSha, backfilledShaSource);
                                        var mtimeSmfmTag = ApplySmfmInPlace(mtimeEntry.Features, scanPath, existing.Features.SmfmScores) ? " +smfm" : "";
                                        if (mtimeSmfmTag.Length > 0) Interlocked.Increment(ref smfmAdded);
                                        allTracks[t.Location] = mtimeEntry;
                                        Interlocked.Increment(ref cachedCount);
                                        trackClass = "cached";
                                        Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name} (cached{backfillTag}{mtimeSmfmTag})");
                                        return;
                                    }
                                }
                                else
                                {
                                    // Mtime mismatched.
                                    // Tier 1.5: quick tags-only check — ~64 KB read on the
                                    // ORIGINAL path (no staging: copying the whole file would
                                    // defeat the point). Evidence-based: head-64k + audio props
                                    // must match the stored fingerprint; any doubt falls through
                                    // to the full-SHA tier below. fileMd5 is cleared, not
                                    // recomputed — the tag write invalidated it and recomputing
                                    // costs the full read this tier avoids (--verify --backfill
                                    // refills it).
                                    FingerprintV1? msQuickFp = null;
                                    if (_quickCache
                                        && existing.FingerprintV1 != null
                                        && HasCurrentFeatures(existing.Features))
                                    {
                                        if (msSourceSize == 0)
                                            try { msSourceSize = new FileInfo(scanPath).Length; } catch { }
                                        if (msSourceSize > 0)
                                        {
                                            msQuickFp = ComputeFingerprintV1(scanPath, msSourceSize, out _);
                                            if (msQuickFp != null && IsTagsOnlyChange(msQuickFp, existing.FingerprintV1))
                                            {
                                                var headEntry = RebuildCacheEntry(existing, t, currentLastMod, null, msQuickFp);
                                                headEntry.FileMd5 = null;  // stale whole-file MD5; --verify --backfill refills
                                                var headSmfmTag = ApplySmfmInPlace(headEntry.Features, scanPath, existing.Features.SmfmScores) ? " +smfm" : "";
                                                if (headSmfmTag.Length > 0) Interlocked.Increment(ref smfmAdded);
                                                allTracks[t.Location] = headEntry;
                                                Interlocked.Increment(ref cachedCount);
                                                Interlocked.Increment(ref cachedByHeadPath);
                                                trackClass = "cached·head";
                                                Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name} (cached·head{headSmfmTag})");
                                                return;
                                            }
                                        }
                                    }

                                    // Try the audioStreamSha256 path-tier:
                                    // if the audio bytes are unchanged (only tags / container
                                    // metadata drifted), reuse Essentia features and refresh
                                    // identity fields the tag edit invalidated. Body reads
                                    // go through the staged copy (FIX 4).
                                    if (!string.IsNullOrEmpty(existing.AudioStreamSha256)
                                        && HasCurrentFeatures(existing.Features))
                                    {
                                        try { msSourceSize = new FileInfo(scanPath).Length; } catch { }
                                        if (msSourceSize > 0)
                                        {
                                            var msStagedPath = EnsureStagedSrc();
                                            var bodyHashes = EnsureBodyHashes();
                                            var recomputedSha = bodyHashes.sha;
                                            // Transition: a stored pre-flac-frames value matches the
                                            // LEGACY (TagLib-region) sha instead — same "audio bytes
                                            // unchanged" conclusion; upgrade the stored sha in place.
                                            bool shaHit = !string.IsNullOrEmpty(recomputedSha)
                                                && string.Equals(recomputedSha, existing.AudioStreamSha256, StringComparison.OrdinalIgnoreCase);
                                            bool legacyHit = !shaHit && !string.IsNullOrEmpty(bodyHashes.leg)
                                                && string.Equals(bodyHashes.leg, existing.AudioStreamSha256, StringComparison.OrdinalIgnoreCase);
                                            if (shaHit || legacyHit)
                                            {
                                                // Audio bytes unchanged — tag edit only. Refresh
                                                // fileMd5 + fingerprint.v1 (both tag-affected),
                                                // reuse everything else.
                                                var refreshedMd5 = bodyHashes.md5;
                                                var refreshedFp = msQuickFp ?? ComputeFingerprintV1(msStagedPath, msSourceSize, out _);
                                                var shaPathEntry = legacyHit
                                                    ? RebuildCacheEntry(existing, t, currentLastMod, refreshedMd5, refreshedFp, recomputedSha, "flac-frames")
                                                    : RebuildCacheEntry(existing, t, currentLastMod, refreshedMd5, refreshedFp);
                                                if (!_fileMd5Enabled) shaPathEntry.FileMd5 = null;  // unconsumed field; not written without --file-md5
                                                var shaPathSmfmTag = ApplySmfmInPlace(shaPathEntry.Features, scanPath, existing.Features.SmfmScores) ? " +smfm" : "";
                                                if (shaPathSmfmTag.Length > 0) Interlocked.Increment(ref smfmAdded);
                                                allTracks[t.Location] = shaPathEntry;
                                                Interlocked.Increment(ref cachedCount);
                                                Interlocked.Increment(ref cachedByShaPath);
                                                trackClass = "cached·sha";
                                                Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name} (cached·sha{shaPathSmfmTag})");
                                                return;
                                            }

                                            // Tier 2.5 — FLAC transition rescue. Stored sha predates
                                            // the flac-frames algorithm AND the tags were rewritten
                                            // before any migration ran, so NEITHER sha form can match
                                            // (the stored hash covered metadata bytes that no longer
                                            // exist). Without this, the track falls through to a full
                                            // Essentia re-analysis it doesn't need. Evidence gate:
                                            // audio props must match the stored fingerprint exactly
                                            // (same rule as --verify's --accept-flac-tag-drift, now
                                            // automatic in the default scan). Fires only for
                                            // transition-era entries — self-extinguishing.
                                            if (bodyHashes.leg != null   // current file is FLAC (legacy sha computed)
                                                && (existing.AudioStreamSha256Source ?? "invariant") != "flac-frames"
                                                && existing.FingerprintV1 != null
                                                && !string.IsNullOrEmpty(recomputedSha))
                                            {
                                                var rescueFp = msQuickFp ?? ComputeFingerprintV1(msStagedPath, msSourceSize, out _);
                                                var storedFp = existing.FingerprintV1;
                                                bool propsOk = rescueFp != null
                                                    && rescueFp.Codec == storedFp.Codec
                                                    && rescueFp.SampleRate == storedFp.SampleRate
                                                    && rescueFp.Channels == storedFp.Channels
                                                    && rescueFp.BitDepth == storedFp.BitDepth
                                                    && Math.Abs(rescueFp.DurationMs - storedFp.DurationMs) <= 500;
                                                if (propsOk)
                                                {
                                                    var rekeyEntry = RebuildCacheEntry(existing, t, currentLastMod, bodyHashes.md5, rescueFp, recomputedSha, "flac-frames");
                                                    if (!_fileMd5Enabled) rekeyEntry.FileMd5 = null;
                                                    var rekeySmfmTag = ApplySmfmInPlace(rekeyEntry.Features, scanPath, existing.Features.SmfmScores) ? " +smfm" : "";
                                                    if (rekeySmfmTag.Length > 0) Interlocked.Increment(ref smfmAdded);
                                                    allTracks[t.Location] = rekeyEntry;
                                                    Interlocked.Increment(ref cachedCount);
                                                    Interlocked.Increment(ref flacRekeyed);
                                                    trackClass = "cached·rekey";
                                                    Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name} (cached·rekey{rekeySmfmTag})");
                                                    return;
                                                }
                                            }
                                        }
                                    }
                                    if (_audit)
                                        Console.WriteLine($"  DEBUG cache: stale (file:{currentLastMod:o} != cached:{existing.LastModified:o})");
                                }
                            }
                            catch (Exception ex) { if (_audit) Console.WriteLine($"  DEBUG cache: lastmod error: {ex.Message}"); }
                        }

                        // Cross-machine SHA fallback \u2014 same audio bytes (invariant region)
                        // at a different path. Catches clean moves and "moved + tag-edited"
                        // alike. Body read goes through the staged copy (FIX 4).
                        if (moodShaIndex != null)
                        {
                            if (msSourceSize == 0)
                                try { msSourceSize = new FileInfo(scanPath).Length; } catch { }
                            if (msSourceSize > 0)
                            {
                                var msStagedPath = EnsureStagedSrc();
                                var crossHashes = EnsureBodyHashes();
                                var localSha = crossHashes.sha;
                                // Index holds STORED values — for un-migrated FLAC entries that's
                                // the legacy TagLib-region sha, so look up both forms.
                                (TrackEntry Entry, string OldKey) xs = default;
                                bool crossHit = !string.IsNullOrEmpty(localSha) && moodShaIndex.TryGetValue(localSha!, out xs);
                                bool crossLegacyHit = !crossHit && !string.IsNullOrEmpty(crossHashes.leg)
                                    && moodShaIndex.TryGetValue(crossHashes.leg!, out xs);
                                if (crossHit || crossLegacyHit)
                                {
                                    var xsf = xs.Entry!.Features;
                                    if (!HasCurrentFeatures(xsf))
                                    {
                                        if (_audit) Console.WriteLine($"  DEBUG cache-sha: re-extracting (DR / extended missing)");
                                    }
                                    else
                                    {
                                        // Audio bytes match; fingerprint.v1 is tag-affected —
                                        // recompute it, reuse Essentia features.
                                        var refreshedMd5 = crossHashes.md5;
                                        var refreshedFp = ComputeFingerprintV1(msStagedPath, msSourceSize, out _);
                                        var currentLastMod = DateTime.MinValue;
                                        try { currentLastMod = File.GetLastWriteTimeUtc(scanPath); } catch { }
                                        var crossShaEntry = crossLegacyHit
                                            ? RebuildCacheEntry(xs.Entry, t, currentLastMod, refreshedMd5, refreshedFp, localSha, "flac-frames")
                                            : RebuildCacheEntry(xs.Entry, t, currentLastMod, refreshedMd5, refreshedFp);
                                        if (!_fileMd5Enabled) crossShaEntry.FileMd5 = null;  // unconsumed field; not written without --file-md5
                                        var crossShaSmfmTag = ApplySmfmInPlace(crossShaEntry.Features, scanPath, xs.Entry.Features.SmfmScores) ? " +smfm" : "";
                                        if (crossShaSmfmTag.Length > 0) Interlocked.Increment(ref smfmAdded);
                                        allTracks[t.Location] = crossShaEntry;
                                        RemoveIfMoved(allTracks, xs.OldKey!);
                                        Interlocked.Increment(ref cachedByShaCross);
                                        Interlocked.Increment(ref cachedCount);
                                        trackClass = "cached\u00b7sha\u00b7x";
                                        Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name} (cached\u00b7sha{crossShaSmfmTag})");
                                        return;
                                    }
                                }
                            }
                        }

                        long fileSizeBytes = 0;
                        var sizeTag = "";
                        try
                        {
                            fileSizeBytes = new FileInfo(scanPath).Length;
                            var sizeMb = fileSizeBytes / (1024.0 * 1024.0);
                            if (sizeMb >= 100) sizeTag = $" [{sizeMb:F0} MB]";
                        }
                        catch { }

                        Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name}{sizeTag}");

                        // Pre-flight: a track past the Essentia ChordsDetection ceiling is a
                        // STRUCTURAL skip, not a failure — a scan cannot analyze it at this
                        // ceiling. Ledger it to mbxmoods-skipped.csv and keep it OUT of
                        // mbxmoods-errors.csv, so raising --max-duration re-evaluates it rather
                        // than it being remembered as a prior-run error and skipped forever (I4).
                        var trackDurationSecs = t.TotalTimeMs / 1000;
                        var overReason = OverLengthSkipReason(trackDurationSecs, _maxEssentiaDurationSecs);
                        if (overReason != null)
                        {
                            AppendSkipped(skippedPath, t.Location, GetExtensionSafe(t.Location), overReason);
                            Console.WriteLine($"[skipped over-length] {t.Location} ({overReason})");
                            Interlocked.Increment(ref overLengthSkipped);
                            EtaDrainNewPool();
                            trackClass = "skip·overlength";
                            return;
                        }

                        // Cache miss — full Essentia + ride-along, all reads through the
                        // staged copy (FIX 4 / FIX 7). Wall-clock per track ≈ max(analysis,
                        // slowest-task).
                        msSourceSize = fileSizeBytes;
                        EnsureStagedSrc();
                        var msResults = RunSourceWorkers(
                            essentiaExe, msStagedSrc!, fileSizeBytes, t.Location,
                            knownDurationSec: trackDurationSecs,
                            extractTags: false, _stageOpts, cts.Token);
                        Interlocked.Add(ref _analyzeTicksTotal, msResults.AnalyzeTicks);
                        Interlocked.Increment(ref _analyzeCount);
                        // Rate + ETA model: bytes were processed whether the analysis
                        // succeeded or failed, and a new-to-catalog track leaves the
                        // remaining-work pool either way.
                        RecordAnalyzedBytes(sw.Elapsed.TotalSeconds, fileSizeBytes);
                        if (t.TotalTimeMs > 0)
                        {
                            // Real-time-factor sample (duration-known tracks only, so
                            // ticks and audio-ms stay a matched pair).
                            Interlocked.Add(ref _rtfAnalyzeTicks, msResults.AnalyzeTicks);
                            Interlocked.Add(ref _rtfAudioMs, t.TotalTimeMs);
                            Interlocked.Increment(ref _rtfTrackCount);
                        }
                        EtaDrainNewPool();  // XML-size based, matching the pre-flight totals

                        var feat = msResults.Features;
                        var fileMd5 = msResults.FileMd5;
                        var fingerprintV1 = msResults.FingerprintV1;
                        var audioStreamSha256 = msResults.AudioStreamSha256;
                        var audioStreamSha256Source = msResults.AudioStreamSha256Source;
                        var bitUsageResult = msResults.BitUsage;
                        var smfmResult = msResults.Smfm;
                        var analyzeDuration = msResults.AnalyzeDuration;

                        if (feat == null)
                        {
                            var err = msResults.EssentiaError ?? "Unknown error";
                            var sizeMb = fileSizeBytes / (1024.0 * 1024.0);
                            AppendError(errorsPath, t.Location, t.Artist, t.Name, err, sizeMb, analyzeDuration.TotalSeconds, saveLock);
                            Console.WriteLine($"  FAILED: {err}");
                            Interlocked.Increment(ref failed);
                            if (err.Contains("Timeout")) Interlocked.Increment(ref timedOut);
                            return;
                        }

                        feat.TrackId = t.TrackId;
                        feat.Artist = t.Artist;
                        feat.Title = t.Name;
                        feat.Album = t.Album;
                        feat.Genre = t.Genre;
                        feat.FilePath = t.Location;
                        if (bitUsageResult != null) feat.BitUsage = bitUsageResult;
                        if (msResults.HfEnergyRatio.HasValue) { feat.HfEnergyRatio = msResults.HfEnergyRatio; feat.HfEnergyMethod = msResults.HfEnergyMethod; }
                        if (msResults.HfSpectralStructure != null) feat.HfSpectralStructure = msResults.HfSpectralStructure;
                        if (smfmResult.HasValue)
                        {
                            var smfmWasAbsent = !(allTracks.TryGetValue(t.Location, out var smfmPrior) && smfmPrior.Features?.SmfmScores != null);
                            feat.SmfmScores      = smfmResult.Value.Scores;
                            feat.SmfmChannel     = smfmResult.Value.Channel;
                            feat.SmfmChannelName = SmfmReader.ChannelName(smfmResult.Value.Channel);
                            feat.SmfmBpm           = Math.Round(smfmResult.Value.Bpm, 3);
                            if (smfmWasAbsent)
                            {
                                Interlocked.Increment(ref smfmAdded);
                                Console.WriteLine($"  +smfm: {t.Artist} - {t.Name}");
                            }
                        }

                        // FIX 5 — record the mtime captured inside OpenStagedSource right
                        // after File.Copy. Falls back to a fresh stat if the snapshot
                        // failed (kept for the rare case both stats throw).
                        var lastMod = msStagedSrc!.SourceLastWriteUtc;
                        if (lastMod == DateTime.MinValue)
                            try { lastMod = File.GetLastWriteTimeUtc(scanPath); } catch { }

                        allTracks[t.Location] = new TrackEntry
                        {
                            Features = feat,
                            LastModified = lastMod,
                            AnalysisDurationSecs = analyzeDuration.TotalSeconds,
                            FileMd5 = fileMd5,
                            AudioStreamSha256 = string.IsNullOrEmpty(audioStreamSha256) ? null : audioStreamSha256,
                            AudioStreamSha256Source = audioStreamSha256Source,
                            FingerprintV1 = fingerprintV1,
                        };
                        trackClass = "analyzed";
                        var newAnalyzed = Interlocked.Increment(ref analyzed);

                        if (newAnalyzed - Volatile.Read(ref lastSaveAnalyzed) >= SaveInterval)
                        {
                            lock (saveLock)
                            {
                                if (newAnalyzed - lastSaveAnalyzed >= SaveInterval)
                                {
                                    lastSaveAnalyzed = newAnalyzed;
                                    var saveSw = Stopwatch.StartNew();
                                    SaveResults(moodsPath, allTracks);
                                    saveSw.Stop();
                                    Console.WriteLine($"  [Saved {allTracks.Count} tracks in {saveSw.Elapsed.TotalSeconds:F1}s]");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            Console.WriteLine($"Error: {t.Artist} - {t.Name}: {ex.Message}");
                            AppendError(errorsPath, t.Location, t.Artist, t.Name, ex.Message, 0, 0, saveLock);
                        }
                        catch { }
                        Interlocked.Increment(ref failed);
                    }
                    finally
                    {
                        msStagedSrc?.Dispose();
                        RecordTrackOutcome(trackClass, trackSw.ElapsedTicks);
                    }
                };

                // Split by a cheap perf-hint (no file IO): a track already in the loaded
                // catalog with complete features will almost certainly hit a cache tier, so
                // it goes in the fast lane. Everything else — new, absent, or stale — goes in
                // the work lane. The hint is advisory ONLY: the body re-runs every cache tier,
                // so a mis-hinted track is still processed correctly, just under the other
                // partitioner. No mtime/hash/staging here — that is exactly the IO this avoids.
                var msLikelyCached = new List<ITunesTrack>();
                var msLikelyWork = new List<ITunesTrack>();
                foreach (var t in tracks)
                {
                    if (allTracks.TryGetValue(t.Location, out var msHintEntry) && HasCurrentFeatures(msHintEntry.Features))
                        msLikelyCached.Add(t);
                    else
                        msLikelyWork.Add(t);
                }
                if (msLikelyCached.Count + msLikelyWork.Count != tracks.Count)
                    throw new InvalidOperationException("two-lane split dropped or duplicated tracks");
                var msOptions = new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cts.Token };
                // Fast lane: default (chunked) partitioner -> high throughput on cheap
                // cache hits -> the % races up to the already-scanned count quickly.
                Parallel.ForEach(msLikelyCached, msOptions, processTrack);
                // Work lane: single-item NoBuffering -> the slow analyses spread evenly
                // across all threads instead of pinning 1-2 at low CPU on an incremental scan.
                Parallel.ForEach(
                    Partitioner.Create(msLikelyWork, EnumerablePartitionerOptions.NoBuffering),
                    msOptions, processTrack);
            }
            catch (OperationCanceledException) { }

            sw.Stop();
            var endTime = DateTime.Now;
            var wasCancelled = Volatile.Read(ref cancelRequested) != 0;

            var finalSaveSw = Stopwatch.StartNew();
            SaveResults(moodsPath, allTracks);
            finalSaveSw.Stop();

            Console.WriteLine();
            if (wasCancelled)
                Console.WriteLine("=== Interrupted (Ctrl+C) - progress saved ===");
            Console.WriteLine($"Started:    {startTime:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"Finished:   {endTime:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"Elapsed:    {FormatTimeSpan(sw.Elapsed)}");
            Console.WriteLine();
            Console.WriteLine($"  Cached:     {cachedCount}");
            if (cachedByHeadPath > 0)
                Console.WriteLine($"  Head-quick: {cachedByHeadPath}  (of {cachedCount} cached: tags-only via audioHead64kMd5)");
            if (cachedByShaPath > 0 || cachedByShaCross > 0)
                Console.WriteLine($"  Cross-SHA:  {cachedByShaPath + cachedByShaCross}  (of {cachedCount} cached: {cachedByShaPath} same-path tag-edits, {cachedByShaCross} cross-path)");
            if (shaBackfilled > 0)
                Console.WriteLine($"  SHA backfill: {shaBackfilled}  (legacy cache hits gained audioStreamSha256)");
            if (flacRekeyed > 0)
                Console.WriteLine($"  FLAC re-key: {flacRekeyed}  (pre-transition hash + tag rewrite; audio props verified, no re-analysis)");
            if (smfmAdded > 0)
                Console.WriteLine($"  SMFM added: {smfmAdded}  (tracks that gained Sony 12-TONE data this scan)");
            Console.WriteLine($"  Analyzed:   {analyzed}");
            Console.WriteLine($"  Skipped:    {skipped}  (errors from previous run)");
            if (dsdSkipped > 0)
                Console.WriteLine($"  Skipped (structural): {dsdSkipped}  (DSD / video / playlist — see mbxmoods-skipped.csv)");
            if (overLengthSkipped > 0)
                Console.WriteLine($"  Over-length: {overLengthSkipped}  (exceeds --max-duration ceiling — see mbxmoods-skipped.csv)");
            if (missingSkipped > 0)
                Console.WriteLine($"  Missing:    {missingSkipped}  (file not found — see mbxmoods-skipped.csv)");
            Console.WriteLine($"  Failed:     {failed}{(timedOut > 0 ? $"  ({timedOut} timed out)" : "")}");
            Console.WriteLine($"  --------    -----");
            Console.WriteLine($"  Processed:  {cachedCount + analyzed + skipped + dsdSkipped + overLengthSkipped + missingSkipped + failed}");
            Console.WriteLine($"  Output:     {allTracks.Count} tracks in moods file");
            if (analyzed > 0)
            {
                var avgAnalyze = StopwatchTicksToTimeSpan(_analyzeTicksTotal / analyzed);
                Console.WriteLine($"  Avg/track:  {avgAnalyze.TotalSeconds:F1}s (analysis only)");
            }
            // Average analyzed track LENGTH next to the average analysis time and the MB/s.
            // Those three are the same story from different angles, and length is the one
            // that actually explains a slow scan: Essentia cost scales with audio duration,
            // so a 40-minute average track costs ~20x a 2-minute one at identical MB/s.
            // Printing the real-time factor makes the relationship explicit instead of
            // leaving it to be inferred. Sampled from the duration-known tracks only, the
            // same matched pair the ETA model uses (see ComputeEtaSecs).
            long rtfTracks = Interlocked.Read(ref _rtfTrackCount);
            if (rtfTracks > 0)
            {
                long rtfMs = Interlocked.Read(ref _rtfAudioMs);
                var avgLen = TimeSpan.FromMilliseconds((double)rtfMs / rtfTracks);
                double rtfAudioSecs = rtfMs / 1000.0;
                double rtf = rtfAudioSecs > 0
                    ? (double)Interlocked.Read(ref _rtfAnalyzeTicks) / Stopwatch.Frequency / rtfAudioSecs
                    : 0;
                var sampledTag = rtfTracks == analyzed ? "" : $", {rtfTracks} of {analyzed} sampled";
                Console.WriteLine($"  Avg length: {FormatTimeSpan(avgLen)} of audio per analyzed track"
                    + (rtf > 0 ? $"  ({rtf:F3}x realtime{sampledTag})" : sampledTag.Length > 0 ? $"  ({rtfTracks} sampled)" : ""));
            }
            // Per-outcome cost breakdown: what each track-handling class actually
            // cost this run (thread-time, so per-track, not wall). Makes the
            // Essentia-dominated class visible next to the near-free cache tiers.
            if (!_classStats.IsEmpty)
            {
                Console.WriteLine();
                Console.WriteLine("  Per-track cost by outcome (thread-time avg):");
                foreach (var kv in _classStats.OrderByDescending(k => k.Value[1]))
                {
                    var n = kv.Value[0];
                    if (n == 0) continue;
                    var avgSecs = (double)kv.Value[1] / Stopwatch.Frequency / n;
                    var avgTag = avgSecs >= 1 ? $"{avgSecs:F1}s" : $"{avgSecs * 1000:F0}ms";
                    Console.WriteLine($"    {kv.Key,-14} {avgTag,8} avg  ({n} track{(n == 1 ? "" : "s")})");
                }
            }
            if (_analyzedBytesTotal > 0)
            {
                var mb = _analyzedBytesTotal / (1024.0 * 1024.0);
                var sizeTagStr = mb >= 1024 ? $"{mb / 1024.0:F1} GB" : $"{mb:F0} MB";
                Console.WriteLine($"  Analyzed IO: {sizeTagStr} @ {mb / Math.Max(sw.Elapsed.TotalSeconds, 1):F1} MB/s scan-wide");
            }
            if (finalSaveSw != null)
                Console.WriteLine($"  Last save:  {finalSaveSw.Elapsed.TotalSeconds:F1}s");
            try
            {
                using var currentProcess = Process.GetCurrentProcess();
                var peakMb = currentProcess.PeakWorkingSet64 / (1024.0 * 1024.0);
                Console.WriteLine($"  Peak mem:   {peakMb:F0} MB");
            }
            catch { }
            EmitStagingSummary();
            // The FILTERED work list = exactly what a rescan re-analyzes. Wave-missing
            // entries outside it (video/URL/non-audio/exclusion-rule filtered) can't be
            // reached by --refresh-features, so the advisor must not point there for them.
            var workListKeys = new HashSet<string>(
                tracks.Where(t => !string.IsNullOrEmpty(t.Location)).Select(t => t.Location!),
                PathComparer.Instance);
            ReportCatalog(moodsPath, allTracks.Values, statsDetailThreshold, libraryKeys, workListKeys);
            Console.WriteLine();
            Console.WriteLine($"Output: {moodsPath}");
            if (auditLog) Console.WriteLine($"Log:    {logPath}");
            tee?.Dispose();

            if (failed > 0)
                Environment.ExitCode = 1;
        }

        // Known-bad: fullwidth Unicode substitutions for OS-illegal filename characters.
        // These WILL break Essentia tools — they can't survive ANSI conversion.
        static readonly HashSet<char> _errorChars = new HashSet<char>
        {
            '\u29F8',  // ⧸ BIG SOLIDUS
            '\uFF0F',  // ／ FULLWIDTH SOLIDUS
            '\uFF1A',  // ： FULLWIDTH COLON
            '\uFF02',  // ＂ FULLWIDTH QUOTATION MARK
            '\uFF1C',  // ＜ FULLWIDTH LESS-THAN
            '\uFF1E',  // ＞ FULLWIDTH GREATER-THAN
            '\uFF5C',  // ｜ FULLWIDTH VERTICAL LINE
            '\uFF1F',  // ？ FULLWIDTH QUESTION MARK
            '\uFF0A',  // ＊ FULLWIDTH ASTERISK
        };

        static string DescribeChar(char c)
        {
            switch (c)
            {
                case '\u29F8': return "BIG SOLIDUS (for /)";
                case '\uFF0F': return "FULLWIDTH SOLIDUS (for /)";
                case '\uFF1A': return "FULLWIDTH COLON (for :)";
                case '\uFF02': return "FULLWIDTH QUOTATION MARK (for \")";
                case '\uFF1C': return "FULLWIDTH LESS-THAN (for <)";
                case '\uFF1E': return "FULLWIDTH GREATER-THAN (for >)";
                case '\uFF5C': return "FULLWIDTH VERTICAL LINE (for |)";
                case '\uFF1F': return "FULLWIDTH QUESTION MARK (for ?)";
                case '\uFF0A': return "FULLWIDTH ASTERISK (for *)";
                case '\u2018': return "LEFT SINGLE QUOTATION MARK";
                case '\u2019': return "RIGHT SINGLE QUOTATION MARK";
                case '\u201C': return "LEFT DOUBLE QUOTATION MARK";
                case '\u201D': return "RIGHT DOUBLE QUOTATION MARK";
                case '\u2013': return "EN DASH";
                case '\u2014': return "EM DASH";
                case '\u2026': return "HORIZONTAL ELLIPSIS";
                case '\u00BD': return "VULGAR FRACTION ONE HALF";
                default:
                    if (c >= 0xFF01 && c <= 0xFF5E) return $"FULLWIDTH {(char)(c - 0xFEE0)}";
                    if (c >= 0x0080 && c <= 0x00FF) return "LATIN EXTENDED";
                    if (c >= 0x0100 && c <= 0x024F) return "LATIN EXTENDED";
                    if (c >= 0x3000 && c <= 0x9FFF) return "CJK";
                    if (c >= 0xAC00 && c <= 0xD7AF) return "KOREAN";
                    return "UNICODE";
            }
        }

        static void RunCheckFilenames(string xmlPath, string outputDir)
        {
            Console.WriteLine("=== Check Filenames ===");
            Console.WriteLine();

            Console.WriteLine($"Loading iTunes library: {xmlPath}");
            var tracks = ITunesParser.Parse(xmlPath, out _);
            Console.WriteLine($"Found {tracks.Count} tracks");
            // Deliberately does NOT consult _exclusions: --check-filenames reports
            // FILESYSTEM problems (over-length paths, zero-byte files), which are worth
            // knowing about whether or not a track is excluded from analysis — "check is
            // check, not exclude." A 300-character path or a zero-byte file is a filesystem
            // problem regardless of analysis policy, so this mode deliberately never applies
            // exclusion rules (or any other analysis-policy filter) before reporting.
            tracks = FilterRemoteUrls(tracks);
            tracks = FilterVideoFiles(tracks);
            tracks = FilterNonAudio(tracks);
            Console.WriteLine();

            var errors = new List<(ITunesTrack Track, List<char> Chars)>();
            var warnings = new List<(ITunesTrack Track, List<char> Chars, bool Has83)>();
            var zeroByteFiles = new List<ITunesTrack>();
            var smallFiles = new List<(ITunesTrack Track, long Bytes)>();
            var unsupportedFiles = new List<(ITunesTrack Track, string Ext)>();
            var longPaths = new List<(ITunesTrack Track, int Length)>();
            const long SmallFileThreshold = 50 * 1024; // 50 KB
            const int MaxPathLimit = 260;              // Windows MAX_PATH

            foreach (var t in tracks)
            {
                var pathToScan = t.Location;
                if (string.IsNullOrEmpty(pathToScan)) continue;

                // Unsupported format check is independent of the other categories —
                // a .dsf with a non-ASCII path AND zero bytes shows up in three buckets.
                var ext = Path.GetExtension(pathToScan);
                if (!string.IsNullOrEmpty(ext) && UnsupportedExtensions.Contains(ext))
                    unsupportedFiles.Add((t, ext));

                // Over-MAX_PATH paths make normal Win32 IO report "file not found"
                // even when the file exists — the scan can never reach them.
                if (pathToScan.Length >= MaxPathLimit)
                    longPaths.Add((t, pathToScan.Length));

                List<char>? errorList = null;
                List<char>? warnList = null;

                // Scan the full path (not just the filename) — non-ASCII chars in
                // any directory component are the same portability hazard as in the
                // filename (subprocess tools and 8.3-disabled volumes choke on them;
                // the default scan sidesteps via staged-fallback copies). ASCII chars
                // (<=127) include the path separators (\ /) and drive colon, so
                // the threshold-only filter is sufficient.
                var seen = new HashSet<char>();
                foreach (var c in pathToScan)
                {
                    if (c <= 127 || !seen.Add(c)) continue;

                    if (_errorChars.Contains(c))
                    {
                        if (errorList == null) errorList = new List<char>();
                        errorList.Add(c);
                    }
                    else
                    {
                        if (warnList == null) warnList = new List<char>();
                        warnList.Add(c);
                    }
                }

                // Check file size — split zero-byte (almost certainly broken) from
                // small-but-nonzero (could be a legit short clip or a truncation).
                try
                {
                    var size = new FileInfo(t.Location).Length;
                    if (size == 0)
                        zeroByteFiles.Add(t);
                    else if (size < SmallFileThreshold)
                        smallFiles.Add((t, size));
                }
                catch { }

                if (errorList != null)
                    errors.Add((t, errorList));
                else if (warnList != null)
                {
                    // Check if 8.3 short path is available — if so, Essentia will be fine
                    bool has83 = false;
                    try
                    {
                        var sb = new StringBuilder(260);
                        var result = GetShortPathName(t.Location, sb, sb.Capacity);
                        has83 = result > 0 && result <= sb.Capacity;
                    }
                    catch { }
                    warnings.Add((t, warnList, has83));
                }
            }

            // Errors — these WILL break
            if (errors.Count > 0)
            {
                Console.WriteLine($"ERRORS: {errors.Count} file(s) with characters that WILL break Essentia:");
                Console.WriteLine();
                foreach (var (t, chars) in errors)
                {
                    Console.WriteLine($"  {t.Artist} - {t.Name}");
                    Console.WriteLine($"    {t.Location}");
                    foreach (var c in chars)
                        Console.WriteLine($"    ERROR  '{c}' U+{(int)c:X4} {DescribeChar(c)}");
                    Console.WriteLine();
                }
            }

            // Warnings — may or may not work depending on 8.3 and code page
            var warnsNo83 = warnings.Where(w => !w.Has83).ToList();
            var warnsOk = warnings.Where(w => w.Has83).ToList();

            if (warnsNo83.Count > 0)
            {
                Console.WriteLine($"WARNINGS: {warnsNo83.Count} file(s) with non-ASCII characters (no 8.3 short path available):");
                Console.WriteLine();
                foreach (var (t, chars, _) in warnsNo83)
                {
                    Console.WriteLine($"  {t.Artist} - {t.Name}");
                    Console.WriteLine($"    {t.Location}");
                    foreach (var c in chars)
                        Console.WriteLine($"    WARN   '{c}' U+{(int)c:X4} {DescribeChar(c)}");
                    Console.WriteLine();
                }
            }

            // Unsupported audio formats — Essentia can't decode these
            if (unsupportedFiles.Count > 0)
            {
                Console.WriteLine($"UNSUPPORTED: {unsupportedFiles.Count} file(s) in formats Essentia cannot decode:");
                Console.WriteLine();
                foreach (var (t, ext) in unsupportedFiles)
                {
                    Console.WriteLine($"  {t.Artist} - {t.Name}");
                    Console.WriteLine($"    {t.Location}  ({ext.ToLowerInvariant()})");
                }
                Console.WriteLine();
            }

            // Zero-byte files — almost certainly broken
            if (zeroByteFiles.Count > 0)
            {
                Console.WriteLine($"ZERO BYTES: {zeroByteFiles.Count} file(s) of zero length:");
                Console.WriteLine();
                foreach (var t in zeroByteFiles)
                {
                    Console.WriteLine($"  {t.Artist} - {t.Name}");
                    Console.WriteLine($"    {t.Location}  (0 bytes)");
                }
                Console.WriteLine();
            }

            // Small files — could be a short legit clip or truncated
            if (smallFiles.Count > 0)
            {
                Console.WriteLine($"SUSPECT: {smallFiles.Count} file(s) under {SmallFileThreshold / 1024} KB (may be corrupt/truncated):");
                Console.WriteLine();
                foreach (var (t, bytes) in smallFiles)
                {
                    var kb = bytes / 1024.0;
                    Console.WriteLine($"  {t.Artist} - {t.Name}");
                    Console.WriteLine($"    {t.Location}  ({kb:F1} KB)");
                }
                Console.WriteLine();
            }

            // Over-MAX_PATH paths — the scan handles these via its \\?\ long-path
            // fallback (staged copy), but other tools may still choke on them.
            if (longPaths.Count > 0)
            {
                Console.WriteLine($"PATH TOO LONG: {longPaths.Count} file(s) at or over {MaxPathLimit} chars (scanned via \\?\\ long-path fallback; consider shortening for other tools):");
                Console.WriteLine();
                foreach (var (t, len) in longPaths)
                {
                    Console.WriteLine($"  {t.Artist} - {t.Name}");
                    Console.WriteLine($"    {t.Location}  ({len} chars)");
                }
                Console.WriteLine();
            }

            // Summary
            Console.WriteLine("=== Summary ===");
            Console.WriteLine($"  Total tracks:     {tracks.Count}");
            Console.WriteLine($"  Errors:           {errors.Count}  (will break - rename these)");
            Console.WriteLine($"  Warnings (no 8.3):{warnsNo83.Count}  (may break - check these)");
            Console.WriteLine($"  Warnings (8.3 ok):{warnsOk.Count}  (safe - 8.3 short path available)");
            Console.WriteLine($"  Unsupported:      {unsupportedFiles.Count}  (DSD/DSF — Essentia cannot decode)");
            Console.WriteLine($"  Zero-byte files:  {zeroByteFiles.Count}  (length == 0)");
            Console.WriteLine($"  Suspect files:    {smallFiles.Count}  (over 0 and under {SmallFileThreshold / 1024} KB)");
            Console.WriteLine($"  Long paths:       {longPaths.Count}  (>= {MaxPathLimit} chars — scan uses \\?\\ fallback; other tools may not)");
            Console.WriteLine($"  Clean:            {tracks.Count - errors.Count - warnings.Count}");
            if (errors.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Rename files listed as ERRORS to remove fullwidth Unicode characters.");
            }

            // Write JSON report
            var reportPath = Path.Combine(outputDir, "mbxhub-filenames.json");
            var tmpPath = reportPath + ".tmp";
            try { File.Delete(tmpPath); } catch { }

            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            using (var jw = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
            {
                jw.WriteStartObject();
                jw.WriteString("version", "1.0");
                jw.WriteString("generatedAt", DateTime.UtcNow.ToString("o"));

                jw.WriteStartObject("summary");
                jw.WriteNumber("totalTracks", tracks.Count);
                jw.WriteNumber("errors", errors.Count);
                jw.WriteNumber("warningsNo83", warnsNo83.Count);
                jw.WriteNumber("warnings83Ok", warnsOk.Count);
                jw.WriteNumber("unsupportedFiles", unsupportedFiles.Count);
                jw.WriteNumber("zeroByteFiles", zeroByteFiles.Count);
                jw.WriteNumber("suspectFiles", smallFiles.Count);
                jw.WriteNumber("clean", tracks.Count - errors.Count - warnings.Count);
                jw.WriteEndObject();

                if (errors.Count > 0)
                {
                    jw.WriteStartArray("errors");
                    foreach (var (t, chars) in errors)
                    {
                        jw.WriteStartObject();
                        jw.WriteNumber("trackId", t.TrackId);
                        jw.WriteString("artist", t.Artist);
                        jw.WriteString("title", t.Name);
                        jw.WriteString("path", t.Location);
                        jw.WriteStartArray("chars");
                        foreach (var c in chars)
                        {
                            jw.WriteStartObject();
                            jw.WriteString("char", c.ToString());
                            jw.WriteString("codepoint", $"U+{(int)c:X4}");
                            jw.WriteString("description", DescribeChar(c));
                            jw.WriteEndObject();
                        }
                        jw.WriteEndArray();
                        jw.WriteEndObject();
                    }
                    jw.WriteEndArray();
                }

                if (warnsNo83.Count > 0)
                {
                    jw.WriteStartArray("warningsNo83");
                    foreach (var (t, chars, _) in warnsNo83)
                    {
                        jw.WriteStartObject();
                        jw.WriteNumber("trackId", t.TrackId);
                        jw.WriteString("artist", t.Artist);
                        jw.WriteString("title", t.Name);
                        jw.WriteString("path", t.Location);
                        jw.WriteStartArray("chars");
                        foreach (var c in chars)
                        {
                            jw.WriteStartObject();
                            jw.WriteString("char", c.ToString());
                            jw.WriteString("codepoint", $"U+{(int)c:X4}");
                            jw.WriteString("description", DescribeChar(c));
                            jw.WriteEndObject();
                        }
                        jw.WriteEndArray();
                        jw.WriteEndObject();
                    }
                    jw.WriteEndArray();
                }

                if (warnsOk.Count > 0)
                {
                    jw.WriteStartArray("warnings83Ok");
                    foreach (var (t, chars, _) in warnsOk)
                    {
                        jw.WriteStartObject();
                        jw.WriteNumber("trackId", t.TrackId);
                        jw.WriteString("artist", t.Artist);
                        jw.WriteString("title", t.Name);
                        jw.WriteString("path", t.Location);
                        jw.WriteStartArray("chars");
                        foreach (var c in chars)
                        {
                            jw.WriteStartObject();
                            jw.WriteString("char", c.ToString());
                            jw.WriteString("codepoint", $"U+{(int)c:X4}");
                            jw.WriteString("description", DescribeChar(c));
                            jw.WriteEndObject();
                        }
                        jw.WriteEndArray();
                        jw.WriteEndObject();
                    }
                    jw.WriteEndArray();
                }

                if (unsupportedFiles.Count > 0)
                {
                    jw.WriteStartArray("unsupportedFiles");
                    foreach (var (t, ext) in unsupportedFiles)
                    {
                        jw.WriteStartObject();
                        jw.WriteNumber("trackId", t.TrackId);
                        jw.WriteString("artist", t.Artist);
                        jw.WriteString("title", t.Name);
                        jw.WriteString("path", t.Location);
                        jw.WriteString("extension", ext.ToLowerInvariant());
                        jw.WriteEndObject();
                    }
                    jw.WriteEndArray();
                }

                if (zeroByteFiles.Count > 0)
                {
                    jw.WriteStartArray("zeroByteFiles");
                    foreach (var t in zeroByteFiles)
                    {
                        jw.WriteStartObject();
                        jw.WriteNumber("trackId", t.TrackId);
                        jw.WriteString("artist", t.Artist);
                        jw.WriteString("title", t.Name);
                        jw.WriteString("path", t.Location);
                        jw.WriteNumber("bytes", 0);
                        jw.WriteEndObject();
                    }
                    jw.WriteEndArray();
                }

                if (smallFiles.Count > 0)
                {
                    jw.WriteStartArray("suspectFiles");
                    foreach (var (t, bytes) in smallFiles)
                    {
                        jw.WriteStartObject();
                        jw.WriteNumber("trackId", t.TrackId);
                        jw.WriteString("artist", t.Artist);
                        jw.WriteString("title", t.Name);
                        jw.WriteString("path", t.Location);
                        jw.WriteNumber("bytes", bytes);
                        jw.WriteEndObject();
                    }
                    jw.WriteEndArray();
                }

                jw.WriteEndObject();
            }

            AtomicReplace(tmpPath, reportPath);
            Console.WriteLine();
            Console.WriteLine($"Output: {reportPath}");
        }

        /// <summary>Drop a cross-path-matched cache entry ONLY when its path is genuinely
        /// gone from disk (a real move/rename). If the old file still exists it's a
        /// duplicate, not a move — removing it makes two live duplicate files ping-pong
        /// between scans forever: each scan cross-matches one twin, deletes the other,
        /// and the next scan recreates the deleted one and deletes the first. The library
        /// never converges (visible as Cross-SHA counts and "SMFM added" oscillating
        /// run-to-run on an otherwise-unchanged library). Guarding the removal on
        /// File.Exists lets legitimate duplicates coexist as two stable entries. Surface
        /// them with --duplicates.</summary>
        static void RemoveIfMoved(ConcurrentDictionary<string, TrackEntry> dict, string oldKey)
        {
            bool stillThere;
            try { stillThere = File.Exists(oldKey); }
            catch { stillThere = false; }   // unreadable path → treat as gone (old behavior)
            if (!stillThere) dict.TryRemove(oldKey, out _);
        }

        static void RunDuplicates(string outputDir)
        {
            // --duplicates reports over mbxmoods.json (audioStreamSha256 = content
            // identity — catches same audio even when tags/path differ). Thin wrapper:
            // resolve the moods file next to the output dir and hand off to the
            // shared reporter.
            var moodsPath = Path.Combine(outputDir, "mbxmoods.json");
            if (!File.Exists(moodsPath))
            {
                Console.WriteLine("=== Duplicate Audio ===");
                Console.WriteLine($"Moods file not found: {moodsPath}");
                Console.WriteLine("Run a scan first to produce mbxmoods.json.");
                return;
            }
            var tracks = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
            LoadExistingMoods(moodsPath, tracks);
            RunDuplicates(moodsPath, tracks);
        }

        static void RunFixup(string xmlPath, string moodsPath)
        {
            Console.WriteLine("=== Fixup Mode ===");
            Console.WriteLine();

            if (!File.Exists(moodsPath)) { Console.WriteLine($"No moods file found: {moodsPath}"); return; }

            Console.WriteLine($"Loading moods: {moodsPath}");
            var json = File.ReadAllText(moodsPath);
            var docOptions = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            var root = JsonNode.Parse(json, null, docOptions)?.AsObject();
            if (root == null) { Console.WriteLine("Invalid JSON in moods file."); return; }
            var tracks = root["tracks"]?.AsObject();
            if (tracks == null || tracks.Count == 0) { Console.WriteLine("No tracks in moods file."); return; }
            Console.WriteLine($"Moods entries: {tracks.Count}");

            Console.WriteLine($"Loading iTunes library: {xmlPath}");
            var library = ITunesParser.Parse(xmlPath, out _);
            Console.WriteLine($"Library tracks: {library.Count}");

            var byFilename = new Dictionary<string, List<ITunesTrack>>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in library)
            {
                var filename = Path.GetFileName(t.Location);
                if (string.IsNullOrEmpty(filename)) continue;
                if (!byFilename.TryGetValue(filename, out var list)) { list = new List<ITunesTrack>(); byFilename[filename] = list; }
                list.Add(t);
            }

            // Content-hash index of entries whose file STILL EXISTS on disk. Lets us drop
            // an entry whose own file is gone but whose audio survives at another path (the
            // kept copy of a deleted duplicate) instead of keeping a dead entry via a stale
            // XML filename match.
            var survivingByHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in tracks)
            {
                if (kv.Value == null) continue;
                if (!File.Exists(PathHelper.NormalizeSeparators(kv.Key))) continue;
                var sha = kv.Value.AsObject()["audioStreamSha256"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(sha) && !survivingByHash.ContainsKey(sha!)) survivingByHash[sha!] = kv.Key;
            }

            int unchanged = 0, remapped = 0, orphaned = 0, resolvedByHash = 0;
            var newTracks = new JsonObject();
            var orphanedEntries = new List<(string OldPath, string Artist, string Title)>();

            foreach (var kv in tracks.ToList())
            {
                var oldPath = kv.Key;
                var trackNode = kv.Value;
                if (trackNode == null) continue;
                tracks.Remove(oldPath); // detach from old parent so it can be re-parented
                var trackData = trackNode.AsObject();
                var normalizedOldPath = PathHelper.NormalizeSeparators(oldPath);

                if (File.Exists(normalizedOldPath)) { newTracks[normalizedOldPath] = trackData; unchanged++; continue; }

                var filename = Path.GetFileName(normalizedOldPath);
                var moodArtist = trackData["artist"]?.GetValue<string>() ?? "";
                var moodTitle = trackData["title"]?.GetValue<string>() ?? "";
                var moodAlbum = trackData["album"]?.GetValue<string>() ?? "";
                var moodGenre = trackData["genre"]?.GetValue<string>() ?? "";

                // File gone. If its audio survives at a DIFFERENT existing path (the kept
                // copy of a deleted duplicate), drop this redundant entry — the survivor
                // carries the same content + mood data. This is the dedupe cleanup path.
                var goneSha = trackData["audioStreamSha256"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(goneSha)
                    && survivingByHash.TryGetValue(goneSha!, out var survivorPath)
                    && !PathComparer.Instance.Equals(survivorPath, normalizedOldPath))
                {
                    resolvedByHash++;
                    Console.WriteLine($"  RESOLVED (identical copy kept): {moodArtist} - {moodTitle}");
                    Console.WriteLine($"    gone: {oldPath}");
                    Console.WriteLine($"    kept: {survivorPath}");
                    continue;
                }

                ITunesTrack? match = null;

                if (!string.IsNullOrEmpty(filename) && byFilename.TryGetValue(filename, out var candidates))
                {
                    // A remap target must actually EXIST on disk — a stale XML that still
                    // lists a deleted file must not resurrect it via a no-op same-path remap.
                    var strictMatches = candidates.Where(c =>
                        File.Exists(c.Location) &&
                        string.Equals(c.Artist, moodArtist, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(c.Name, moodTitle, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(c.Album, moodAlbum, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(c.Genre, moodGenre, StringComparison.OrdinalIgnoreCase)
                    ).ToList();

                    if (strictMatches.Count == 1) match = strictMatches[0];
                    else if (strictMatches.Count > 1) Console.WriteLine($"  AMBIGUOUS ({strictMatches.Count} matches): {moodArtist} - {moodTitle}");
                }

                if (match != null)
                {
                    trackData["trackId"] = match.TrackId;
                    newTracks[match.Location] = trackData;
                    remapped++;
                    Console.WriteLine($"  REMAP: {moodArtist} - {moodTitle}");
                    Console.WriteLine($"    {oldPath}");
                    Console.WriteLine($"    -> {match.Location}");
                }
                else { orphaned++; orphanedEntries.Add((oldPath, moodArtist, moodTitle)); }
            }

            var moodPaths = new HashSet<string>(newTracks.Select(kv => kv.Key), PathComparer.Instance);
            int unanalyzed = library.Count(t => !moodPaths.Contains(t.Location));

            Console.WriteLine();
            Console.WriteLine("=== Results ===");
            Console.WriteLine($"  Unchanged:   {unchanged}");
            Console.WriteLine($"  Remapped:    {remapped}");
            Console.WriteLine($"  Resolved:    {resolvedByHash} (file gone, identical copy kept)");
            Console.WriteLine($"  Orphaned:    {orphaned}");
            Console.WriteLine($"  Unanalyzed:  {unanalyzed} (in library, no mood data)");
            Console.WriteLine($"  Total out:   {newTracks.Count}");

            if (orphanedEntries.Count > 0 && orphanedEntries.Count <= 20)
            {
                Console.WriteLine(); Console.WriteLine("Orphaned entries (no match found):");
                foreach (var (path, artist, title) in orphanedEntries) Console.WriteLine($"  {artist} - {title}: {path}");
            }
            else if (orphanedEntries.Count > 20)
            {
                Console.WriteLine(); Console.WriteLine($"Orphaned entries ({orphanedEntries.Count} total, showing first 20):");
                foreach (var (path, artist, title) in orphanedEntries.Take(20)) Console.WriteLine($"  {artist} - {title}: {path}");
                Console.WriteLine($"  ... and {orphanedEntries.Count - 20} more");
            }

            if (remapped > 0 || orphaned > 0 || resolvedByHash > 0)
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd.HHmmss");
                var bakPath = $"{moodsPath}.bak.{timestamp}";
                File.Copy(moodsPath, bakPath);
                Console.WriteLine(); Console.WriteLine($"Backup: {bakPath}");
                root["tracks"] = newTracks; root["trackCount"] = newTracks.Count; root["generatedAt"] = DateTime.UtcNow.ToString("o");
                var tmpPath = moodsPath + ".tmp";
                File.WriteAllText(tmpPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                AtomicReplace(tmpPath, moodsPath);
                Console.WriteLine($"Updated: {moodsPath}");
            }
            else { Console.WriteLine(); Console.WriteLine("All paths valid, no changes needed."); }
        }

        /// <summary>
        /// Wholesale prefix-swap on mbxmoods.json keys. Case-insensitive prefix match
        /// (Windows convention). Use case: library was scanned at one path (e.g. a
        /// robocopy mirror at D:\music\) but downstream consumers (iTunes XML, MBXHub)
        /// expect the canonical path (e.g. \\nas\share\music\). Entries that don't start
        /// with the oldPrefix are left untouched. No iTunes XML required, no per-file
        /// existence check on the new path — this is a pure key rewrite.
        ///
        /// On collision (two old keys remap to the same new key), the later one wins —
        /// reported in the summary. Backup is written as &lt;moodsPath&gt;.bak.&lt;timestamp&gt;
        /// before the atomic replace.
        /// </summary>
        static void RunFixupRemap(string moodsPath, string oldPrefix, string newPrefix)
        {
            Console.WriteLine("=== Fixup Mode (prefix remap) ===");
            Console.WriteLine($"  Moods file:  {moodsPath}");
            Console.WriteLine($"  Old prefix:  {oldPrefix}");
            Console.WriteLine($"  New prefix:  {newPrefix}");
            Console.WriteLine();

            if (!File.Exists(moodsPath)) { Console.WriteLine($"No moods file found: {moodsPath}"); Environment.ExitCode = 2; return; }
            if (string.Equals(oldPrefix, newPrefix, StringComparison.Ordinal))
            { Console.WriteLine("Old prefix equals new prefix; nothing to do."); return; }

            var json = File.ReadAllText(moodsPath);
            var docOptions = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            var root = JsonNode.Parse(json, null, docOptions)?.AsObject();
            if (root == null) { Console.WriteLine("Invalid JSON in moods file."); Environment.ExitCode = 2; return; }
            var tracks = root["tracks"]?.AsObject();
            if (tracks == null || tracks.Count == 0) { Console.WriteLine("No tracks in moods file."); return; }
            Console.WriteLine($"Moods entries: {tracks.Count}");

            int remapped = 0, unchanged = 0, collided = 0;
            var newTracks = new JsonObject();
            var collisionExamples = new List<(string OldKey, string NewKey)>();

            foreach (var kv in tracks.ToList())
            {
                var oldKey = kv.Key;
                var trackNode = kv.Value;
                if (trackNode == null) continue;
                tracks.Remove(oldKey);  // detach so it can be re-parented
                var trackData = trackNode.AsObject();

                if (oldKey.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var newKey = newPrefix + oldKey.Substring(oldPrefix.Length);
                    if (newTracks.ContainsKey(newKey))
                    {
                        collided++;
                        if (collisionExamples.Count < 10) collisionExamples.Add((oldKey, newKey));
                        newTracks.Remove(newKey);  // later-wins
                    }
                    newTracks[newKey] = trackData;
                    remapped++;
                }
                else
                {
                    newTracks[oldKey] = trackData;
                    unchanged++;
                }
            }

            Console.WriteLine();
            Console.WriteLine("=== Results ===");
            Console.WriteLine($"  Remapped:   {remapped}");
            Console.WriteLine($"  Unchanged:  {unchanged} (did not start with old prefix)");
            if (collided > 0)
            {
                Console.WriteLine($"  Collisions: {collided} (later entry overwrote earlier — verify your old/new prefixes are exact)");
                foreach (var (o, n) in collisionExamples)
                {
                    Console.WriteLine($"    {o}");
                    Console.WriteLine($"      -> {n}");
                }
                if (collided > collisionExamples.Count)
                    Console.WriteLine($"    ... and {collided - collisionExamples.Count} more");
            }
            Console.WriteLine($"  Total out:  {newTracks.Count}");

            if (remapped == 0)
            {
                Console.WriteLine();
                Console.WriteLine("No entries matched the old prefix; no changes written.");
                return;
            }

            var timestamp = DateTime.Now.ToString("yyyyMMdd.HHmmss");
            var bakPath = $"{moodsPath}.bak.{timestamp}";
            File.Copy(moodsPath, bakPath);
            Console.WriteLine();
            Console.WriteLine($"Backup: {bakPath}");
            root["tracks"] = newTracks;
            root["trackCount"] = newTracks.Count;
            root["generatedAt"] = DateTime.UtcNow.ToString("o");
            var tmpPath = moodsPath + ".tmp";
            File.WriteAllText(tmpPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            AtomicReplace(tmpPath, moodsPath);
            Console.WriteLine($"Updated: {moodsPath}");
        }

        /// <summary>
        /// Read-only diagnostic. Walk a moods file and, for each entry, classify:
        ///   OK       — file present, audioStreamSha256 recomputes to the cached value
        ///   DRIFT    — file present, audioStreamSha256 differs (audio bytes changed)
        ///   MISSING  — path doesn't exist on disk
        ///   NO_HASH  — entry has no audioStreamSha256 to verify against (older cache)
        ///   ERROR    — TagLib parse / IO failure
        /// Prints summary; writes mbxmoods-verify.csv next to the moods file with
        /// per-entry detail. No writes to the moods file itself.
        /// </summary>
        // Staging deliberately not applied here — verify is a single-pass read
        // (one SHA per entry) and backfill features-tier already gates its ffmpeg
        // cost via the --backfill-level flag. Adding staging to verify-mode would
        // pay one File.Copy per track to save zero downstream reads.
        static int RunVerify(string moodsPath, int parallelism, bool backfill, BackfillLevel level)
        {
            Console.WriteLine(backfill ? "=== Verify + Backfill Mode ===" : "=== Verify Mode ===");
            Console.WriteLine($"Moods file: {moodsPath}");
            if (backfill)
            {
                string scope = level switch
                {
                    BackfillLevel.Identity => "identity only (TagLib + cheap file IO)",
                    BackfillLevel.Features => "features only (ffmpeg — bitUsage / hfEnergyRatio / hfSpectralStructure)",
                    _ => "identity + features (ffmpeg engaged; ~30s per applicable lossless 24-bit track)",
                };
                Console.WriteLine($"Backfill scope: {scope}");
                if (level != BackfillLevel.Identity && string.IsNullOrEmpty(_ffmpegPath.Value))
                    Console.WriteLine("WARNING: ffmpeg not found on PATH — features tier will silently skip every entry.");
            }
            Console.WriteLine();

            var allTracks = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
            int loaded = LoadExistingMoods(moodsPath, allTracks);
            Console.WriteLine($"Loaded {loaded} entries");
            if (loaded == 0) return 0;

            int ok = 0, drift = 0, missing = 0, noHash = 0, errored = 0, backfilled = 0;
            var details = new ConcurrentBag<string>();
            var sw = Stopwatch.StartNew();
            int done = 0;

            Parallel.ForEach(allTracks, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, kvp =>
            {
                var path = kvp.Key;
                var entry = kvp.Value;
                string status;
                string detail = "";
                var filled = new List<string>();   // tracks which fields this entry got backfilled (CSV column 4)

                if (!File.Exists(path))
                {
                    status = "MISSING";
                    Interlocked.Increment(ref missing);
                }
                else
                {
                    try
                    {
                        var fileSize = new FileInfo(path).Length;
                        var (recomputed, recomputedSource) = ComputeAudioStreamSha256FromFile(path, fileSize, out var err, out var legacySha);
                        if (string.IsNullOrEmpty(recomputed))
                        {
                            status = "ERROR";
                            detail = err ?? "hash compute failed";
                            Interlocked.Increment(ref errored);
                        }
                        else if (string.IsNullOrEmpty(entry.AudioStreamSha256))
                        {
                            // Tier A: SHA missing on entry. In backfill mode populate it
                            // (no drift to check against). In read-only mode flag NO_HASH.
                            if (backfill)
                            {
                                entry.AudioStreamSha256 = recomputed;
                                entry.AudioStreamSha256Source = recomputedSource;
                                filled.Add("audioStreamSha256");
                                ApplyBackfill(path, fileSize, entry, level, filled);
                                status = "BACKFILLED";
                                detail = string.Join("|", filled);
                                Interlocked.Increment(ref backfilled);
                            }
                            else
                            {
                                status = "NO_HASH";
                                Interlocked.Increment(ref noHash);
                            }
                        }
                        else
                        {
                            var cached = entry.AudioStreamSha256!;
                            bool shaOk = string.Equals(recomputed, cached, StringComparison.OrdinalIgnoreCase);
                            // Transition: a stored pre-flac-frames FLAC value matches the
                            // legacy TagLib-region sha (computed in the same read) — audio
                            // is equally proven unchanged; upgrade in place under backfill.
                            bool legacyOk = !shaOk && !string.IsNullOrEmpty(legacySha)
                                && string.Equals(legacySha, cached, StringComparison.OrdinalIgnoreCase);
                            if (shaOk || legacyOk)
                            {
                                if (backfill)
                                {
                                    if (legacyOk)
                                    {
                                        entry.AudioStreamSha256 = recomputed;
                                        entry.AudioStreamSha256Source = recomputedSource;
                                        filled.Add("audioStreamSha256-upgraded");
                                    }
                                    ApplyBackfill(path, fileSize, entry, level, filled);
                                    if (filled.Count > 0)
                                    {
                                        status = "BACKFILLED";
                                        detail = string.Join("|", filled);
                                        Interlocked.Increment(ref backfilled);
                                    }
                                    else
                                    {
                                        status = "OK";
                                        Interlocked.Increment(ref ok);
                                    }
                                }
                                else
                                {
                                    status = "OK";
                                    Interlocked.Increment(ref ok);
                                }
                            }
                            else if (backfill && _acceptFlacTagDrift && recomputedSource == "flac-frames")
                            {
                                // --accept-flac-tag-drift: FLAC whose stored sha predates the
                                // flac-frames algorithm AND whose tags were rewritten after the
                                // scan — neither compare can succeed because the stored hash
                                // covered metadata bytes that no longer exist. Operator-accepted
                                // re-key: audio props must still match the stored fingerprint.
                                var freshFp = ComputeFingerprintV1(path, fileSize, out _);
                                var storedFp = entry.FingerprintV1;
                                bool propsOk = freshFp != null && storedFp != null
                                    && freshFp.Codec == storedFp.Codec
                                    && freshFp.SampleRate == storedFp.SampleRate
                                    && freshFp.Channels == storedFp.Channels
                                    && freshFp.BitDepth == storedFp.BitDepth
                                    && Math.Abs(freshFp.DurationMs - storedFp.DurationMs) <= 500;
                                if (propsOk)
                                {
                                    entry.AudioStreamSha256 = recomputed;
                                    entry.AudioStreamSha256Source = recomputedSource;
                                    entry.FingerprintV1 = freshFp;
                                    filled.Add("audioStreamSha256-accepted-tag-drift");
                                    ApplyBackfill(path, fileSize, entry, level, filled);
                                    status = "BACKFILLED";
                                    detail = string.Join("|", filled);
                                    Interlocked.Increment(ref backfilled);
                                }
                                else
                                {
                                    status = "REANALYZE_NEEDED";
                                    detail = $"cached={cached.Substring(0, 12)}.. disk={recomputed!.Substring(0, 12)}.. (props changed — not accepted)";
                                    Interlocked.Increment(ref drift);
                                }
                            }
                            else
                            {
                                // Drift: audio bytes changed since last analysis. Don't lie by
                                // backfilling — flag for re-analyze.
                                status = backfill ? "REANALYZE_NEEDED" : "DRIFT";
                                detail = $"cached={cached.Substring(0, 12)}.. disk={recomputed!.Substring(0, 12)}..";
                                Interlocked.Increment(ref drift);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        status = "ERROR";
                        detail = ex.Message;
                        Interlocked.Increment(ref errored);
                    }
                }

                if (status != "OK")
                    details.Add($"{status}\t{path}\t{detail}\t{string.Join("|", filled)}");

                var n = Interlocked.Increment(ref done);
                if (n % 250 == 0)
                {
                    var pct = (n * 100) / loaded;
                    Console.WriteLine($"[{n}/{loaded} {pct}%{FormatEta(sw.Elapsed, n, loaded)}] {(backfill ? "backfilling" : "verifying")}...");
                }
            });

            sw.Stop();

            // In backfill mode write the merged file back atomically — only when we actually
            // changed something. Idempotent re-runs do zero IO on the moods file.
            if (backfill && backfilled > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"Writing backfilled entries to {moodsPath}...");
                try
                {
                    SaveResults(moodsPath, allTracks);
                    Console.WriteLine($"  wrote {allTracks.Count} entries");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ERROR: could not save moods file: {ex.Message}");
                    return 1;
                }
            }

            var csvPath = Path.Combine(
                Path.GetDirectoryName(moodsPath) ?? ".",
                "mbxmoods-verify.csv");
            try
            {
                var lines = new List<string> { "status\tpath\tdetail\tbackfilledFields" };
                lines.AddRange(details.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
                File.WriteAllLines(csvPath, lines, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARNING: could not write {csvPath}: {ex.Message}");
                csvPath = "(not written)";
            }

            Console.WriteLine();
            Console.WriteLine("=== Results ===");
            Console.WriteLine($"  OK:                {ok}");
            if (backfill)
                Console.WriteLine($"  Backfilled:        {backfilled}");
            Console.WriteLine($"  {(backfill ? "Reanalyze needed:" : "Drift:           ")}  {drift}");
            Console.WriteLine($"  Missing:           {missing}");
            if (!backfill)
                Console.WriteLine($"  No hash:           {noHash}");
            Console.WriteLine($"  Errored:           {errored}");
            Console.WriteLine($"  Elapsed:           {FormatTimeSpan(sw.Elapsed)}");
            Console.WriteLine($"  Detail:            {csvPath}");

            // Surface the entries that need human action right in the console — otherwise
            // they're a needle in a multi-thousand-row CSV. OK / BACKFILLED aren't listed.
            //
            // The important nuance: a "changed" file that is ALSO on the error skip-list
            // (mbxmoods-errors.csv) is SKIPPED by a normal scan — so telling the user to
            // "rescan" it would be a lie; it needs --retry-errors. We cross-reference that
            // list here and bucket accordingly, and show the prior Essentia failure reason
            // (the actionable fact) instead of the sha pair for those entries.
            var errIndex = LoadExistingErrors(
                Path.Combine(Path.GetDirectoryName(moodsPath) ?? ".", "mbxmoods-errors.csv"));

            var attention = details
                .Select(d => d.Split('\t'))
                .Where(p => p.Length >= 2 && p[0] != "BACKFILLED")
                .Select(p =>
                {
                    var st = p[0];
                    var ap = p.Length > 1 ? p[1] : "";
                    var ad = p.Length > 2 ? p[2] : "";
                    string bucket;
                    if (st == "MISSING") bucket = "missing";
                    else if (st == "NO_HASH") bucket = "nohash";
                    else if (st == "ERROR") bucket = "error";
                    else if (st == "DRIFT" || st == "REANALYZE_NEEDED")
                        bucket = errIndex.ContainsKey(ap) ? "drift_errored" : "drift_plain";
                    else bucket = "other";
                    return new { Path = ap, Detail = ad, Bucket = bucket };
                })
                .ToList();

            if (attention.Count > 0)
            {
                const int perGroupCap = 25;
                // Fixed, logical presentation order with the *real* remediation per bucket.
                var order = new (string Key, string Header)[]
                {
                    ("drift_plain",   "CHANGED — audio differs from last analysis; a normal rescan re-analyzes these"),
                    ("drift_errored", "CHANGED + PREVIOUSLY FAILED — on the error list, so a normal scan SKIPS them. Re-attempt with  --retry-errors  (if they still fail, the file itself is bad)"),
                    ("missing",       "MISSING — file not found on disk; restore it or drop the entry from your library"),
                    ("nohash",        "NO HASH — no audioStreamSha256 stored to verify against"),
                    ("error",         "ERROR — could not read/hash the file during verify"),
                    ("other",         "OTHER"),
                };
                var byBucket = attention
                    .GroupBy(a => a.Bucket)
                    .ToDictionary(g => g.Key, g => g.OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase).ToList());

                Console.WriteLine();
                Console.WriteLine($"=== Needs attention ({attention.Count}) ===");
                foreach (var (key, header) in order)
                {
                    if (!byBucket.TryGetValue(key, out var items) || items.Count == 0) continue;
                    Console.WriteLine();
                    Console.WriteLine($"  {header} ({items.Count}):");
                    foreach (var a in items.Take(perGroupCap))
                    {
                        // For errored entries the prior failure reason is the useful thing to
                        // show, not the sha pair.
                        string note = a.Detail;
                        if (key == "drift_errored" && errIndex.TryGetValue(a.Path, out var reason) && !string.IsNullOrEmpty(reason))
                            note = reason.Length > 100 ? reason.Substring(0, 97) + "..." : reason;
                        Console.WriteLine(string.IsNullOrEmpty(note) ? $"    {a.Path}" : $"    {a.Path}  ({note})");
                    }
                    if (items.Count > perGroupCap)
                        Console.WriteLine($"    … +{items.Count - perGroupCap} more — full list in {Path.GetFileName(csvPath)}");
                }
            }

            // Exit non-zero on any condition that needs human attention.
            // Backfill is a successful outcome -> doesn't fail the exit code.
            return (drift > 0 || missing > 0 || errored > 0) ? 1 : 0;
        }

        /// <summary>Per-entry backfill. Identity tier: TagLib + cheap file IO (Tier A
        /// fileMd5, Tier B whole fingerprint.v1, Tier C sub-fields, MP3 LAME tag).
        /// <summary>Apply a fresh SMFM read to the features of an already-resolved cache entry.
        /// Called inline at every cache-hit return point. Fast (~5 ms header-only read).
        /// Returns true when SMFM was read AND the prior cache entry had none — i.e. the
        /// data is newly added this scan, so the caller can surface a "+smfm" marker.
        /// Pass the prior entry's SmfmScores so the marker fires once (when the file first
        /// gains SMFM) instead of on every rescan of an already-tagged track.</summary>
        static bool ApplySmfmInPlace(TrackFeatures f, string filePath, int[]? priorScores = null)
        {
            var smfm = SmfmReader.TryRead(filePath);
            if (!smfm.HasValue) return false;
            f.SmfmScores      = smfm.Value.Scores;
            f.SmfmChannel     = smfm.Value.Channel;
            f.SmfmChannelName = SmfmReader.ChannelName(smfm.Value.Channel);
            f.SmfmBpm           = Math.Round(smfm.Value.Bpm, 3);
            return priorScores == null;
        }

        /// Features tier: ffmpeg-driven bitUsage + hfEnergyRatio + hfSpectralStructure. Level selects which
        /// tiers run; All (default) does both. Caller must have already SHA-validated
        /// that audio bytes match before invoking — backfill MUST NOT touch entries
        /// whose audio drifted.</summary>
        static void ApplyBackfill(string path, long fileSize, TrackEntry entry, BackfillLevel level, List<string> filled)
        {
            if (level != BackfillLevel.Features)
                ApplyBackfillIdentity(path, fileSize, entry, filled);

            if (level != BackfillLevel.Identity)
                ApplyBackfillFeatures(path, entry, filled);
        }

        /// <summary>Identity-tier backfill (Tier A fileMd5, Tier B whole fingerprint.v1,
        /// Tier C sub-fields, MP3 LAME tag). TagLib + cheap file IO only; no audio decode.</summary>
        static void ApplyBackfillIdentity(string path, long fileSize, TrackEntry entry, List<string> filled)
        {
            // Tier A — fileMd5 (only under --file-md5: whole-file MD5 costs a full
            // read per entry and nothing consumes it; sha/fingerprint fills below
            // are unaffected)
            if (_fileMd5Enabled && string.IsNullOrEmpty(entry.FileMd5))
            {
                var md5 = ComputeFileMd5(path);
                if (!string.IsNullOrEmpty(md5))
                {
                    entry.FileMd5 = md5;
                    filled.Add("fileMd5");
                }
            }

            // Tier B — whole fingerprint.v1 missing (legacy entries)
            if (entry.FingerprintV1 == null)
            {
                var fp = ComputeFingerprintV1(path, fileSize, out _);
                if (fp != null)
                {
                    entry.FingerprintV1 = fp;
                    filled.Add("fingerprint.v1");
                    return;  // a fresh fingerprint already includes every IdentityField — Tier C is moot
                }
                // ComputeFingerprintV1 failed (rare — corrupt tags). Nothing more to backfill.
                return;
            }

            // Tier C — fingerprint.v1 exists but some IdentityField specs are missing
            var missingSpecs = IdentityFields.Where(s => !s.IsPresent(entry.FingerprintV1)).ToList();
            if (missingSpecs.Count > 0)
            {
                try
                {
                    using var tf = TagLib.File.Create(path);
                    foreach (var spec in missingSpecs)
                    {
                        spec.Populate(tf, entry.FingerprintV1);
                        // Only report BACKFILLED when Populate actually produced a value —
                        // re-check via the spec's own IsPresent. A large fraction of a normal
                        // library genuinely carries no encoder tag (Zune/older rippers never
                        // wrote TSSE/ENCODER) and some files have no TagLib-readable bit depth;
                        // for those Populate leaves the field at its default and IsPresent stays
                        // false. Without this guard every pass re-counted them as backfilled
                        // forever (the field can never converge), so --verify --backfill never
                        // reached a clean "Backfilled: 0". (Same loop class the bitDepth
                        // CodecLacksBitDepth guard prevents, generalised to any unfillable spec.)
                        if (spec.IsPresent(entry.FingerprintV1))
                            filled.Add(spec.Name);
                    }
                }
                catch
                {
                    // TagLib threw on the file (corrupt / unsupported). Leave the entry as-is;
                    // the CSV will still mark BACKFILLED for any Tier A/B fills already done.
                }
            }

            // Phase 2 — MP3 LAME tag backfill (FileBytesShallow tier). Off the IdentityFields
            // list because it doesn't use TagLib; gated on codec=mp3 + at least one field missing.
            if (entry.FingerprintV1.Codec == "mp3" &&
                string.IsNullOrEmpty(entry.FingerprintV1.Mp3LameVersion) &&
                entry.FingerprintV1.Mp3LowpassHz == 0 &&
                entry.FingerprintV1.Mp3VbrMethodCode == 0)
            {
                var info = Mp3LameTagParser.TryParse(path);
                if (info != null)
                {
                    entry.FingerprintV1.Mp3LameVersion = info.LameVersion;
                    entry.FingerprintV1.Mp3InfoTagRevision = info.InfoTagRevision;
                    entry.FingerprintV1.Mp3VbrMethodCode = info.VbrMethodCode;
                    entry.FingerprintV1.Mp3VbrMethod = info.VbrMethod;
                    entry.FingerprintV1.Mp3LowpassHz = info.LowpassHz;
                    entry.FingerprintV1.Mp3EncoderDelay = info.EncoderDelay;
                    entry.FingerprintV1.Mp3EncoderPadding = info.EncoderPadding;
                    entry.FingerprintV1.Mp3MusicCrc = info.MusicCrc;
                    filled.Add("mp3LameTag");
                }
            }
        }

        /// <summary>Features-tier backfill — ffmpeg-driven bitUsage + hfEnergyRatio + hfSpectralStructure.
        /// Runs only when fields are currently missing AND the helpers return non-null
        /// (which they self-gate on ffmpeg presence, codec / bit-depth / sample-rate
        /// applicability). Pulls duration from fingerprint.v1 to avoid an extra TagLib
        /// open. If FingerprintV1 is null (Tier B above failed and we're running with
        /// --backfill-level features alone) we skip — duration is required for the
        /// bitUsage mid-track seek and we can't risk an unbounded decode.</summary>
        static void ApplyBackfillFeatures(string path, TrackEntry entry, List<string> filled)
        {
            var ffmpeg = _ffmpegPath.Value;
            if (string.IsNullOrEmpty(ffmpeg)) return;
            if (entry.Features == null) return;

            double durationSec = (entry.FingerprintV1?.DurationMs ?? 0) / 1000.0;
            if (durationSec <= 0) return;

            if (entry.Features.BitUsage == null)
            {
                var bu = ComputeBitUsage(path, durationSec, ffmpeg);
                if (bu != null)
                {
                    entry.Features.BitUsage = bu;
                    filled.Add("bitUsage");
                }
            }

            // Phase 5 — single FFT analysis backfills hfEnergyRatio AND hfSpectralStructure
            // at once. Both are populated/null together; either being absent triggers the
            // ffmpeg pass. (After fresh analysis they're always co-populated; gap arises
            // only on entries last-touched by Phase-3 code that wrote hfEnergyRatio
            // without the structure block.)
            bool hfMissing = !entry.Features.HfEnergyRatio.HasValue || entry.Features.HfSpectralStructure == null;
            if (hfMissing)
            {
                var (hr, hm, hs) = ComputeHfAnalysis(path, ffmpeg);
                if (hr.HasValue && !entry.Features.HfEnergyRatio.HasValue)
                {
                    entry.Features.HfEnergyRatio = hr;
                    entry.Features.HfEnergyMethod = hm;
                    filled.Add("hfEnergyRatio");
                }
                if (hs != null && entry.Features.HfSpectralStructure == null)
                {
                    entry.Features.HfSpectralStructure = hs;
                    filled.Add("hfSpectralStructure");
                }
            }
        }

        static int RunMergeMoods(List<string> sources, string outputPath)
        {
            Console.WriteLine("=== Merge Moods ===");
            Console.WriteLine();

            var docOptions = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            var mergedTracks = new JsonObject();
            int totalLoaded = 0;
            int duplicatesOverwritten = 0;

            foreach (var source in sources)
            {
                if (!File.Exists(source))
                {
                    Console.WriteLine($"Source not found: {source}");
                    return 1;
                }

                Console.WriteLine($"Loading: {source}");
                var json = File.ReadAllText(source);
                var root = JsonNode.Parse(json, null, docOptions)?.AsObject();
                if (root == null)
                {
                    Console.WriteLine($"  Invalid JSON: {source}");
                    return 1;
                }

                var tracks = root["tracks"]?.AsObject();
                if (tracks == null)
                {
                    Console.WriteLine($"  No tracks object: {source}");
                    return 1;
                }

                int count = 0;
                foreach (var kvp in tracks)
                {
                    if (mergedTracks.ContainsKey(kvp.Key))
                        duplicatesOverwritten++;
                    mergedTracks[kvp.Key] = kvp.Value?.DeepClone();
                    count++;
                }
                totalLoaded += count;
                Console.WriteLine($"  {count:N0} entries");
            }

            Console.WriteLine();
            Console.WriteLine($"Total loaded:    {totalLoaded:N0}");
            Console.WriteLine($"Duplicates:      {duplicatesOverwritten:N0} (later source wins)");
            Console.WriteLine($"Merged entries:  {mergedTracks.Count:N0}");

            // Backup if output already exists
            if (File.Exists(outputPath))
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd.HHmmss");
                var bakPath = $"{outputPath}.bak.{timestamp}";
                File.Copy(outputPath, bakPath);
                Console.WriteLine($"Backup: {bakPath}");
            }

            var result = new JsonObject
            {
                ["tracks"] = mergedTracks,
                ["trackCount"] = mergedTracks.Count,
                ["generatedAt"] = DateTime.UtcNow.ToString("o"),
                ["mergedFrom"] = new JsonArray(sources.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray())
            };

            var tmpPath = outputPath + ".tmp";
            File.WriteAllText(tmpPath, result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            AtomicReplace(tmpPath, outputPath);

            Console.WriteLine($"Written: {outputPath}");
            Console.WriteLine();
            return 0;
        }

        /// <summary>Renames a JSON object key in place, preserving the value (clone-detach to avoid
        /// parent-ownership errors). No-op if oldKey absent; if newKey already exists, just drops the
        /// stale oldKey (idempotent re-migration).</summary>
        static bool RenameJsonKey(System.Text.Json.Nodes.JsonObject o, string oldKey, string newKey)
        {
            if (o == null || !o.ContainsKey(oldKey)) return false;
            var node = o[oldKey];
            o.Remove(oldKey);
            if (!o.ContainsKey(newKey))
                o[newKey] = node == null ? null : System.Text.Json.Nodes.JsonNode.Parse(node.ToJsonString());
            return true;
        }

        /// <summary>Read a string property from a JsonNode without ever throwing —
        /// malformed shapes (non-object parents, non-string values) return null.
        /// --migrate must never crash on the files it exists to repair.</summary>
        static string? SafeStr(JsonNode? node, params string[] path)
        {
            foreach (var seg in path)
            {
                node = (node as JsonObject)?[seg];
                if (node == null) return null;
            }
            return (node as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null;
        }

        static double? SafeDbl(JsonNode? node, params string[] path)
        {
            foreach (var seg in path)
            {
                node = (node as JsonObject)?[seg];
                if (node == null) return null;
            }
            return (node as JsonValue)?.TryGetValue<double>(out var d) == true ? d : null;
        }

        /// <summary>Recompute speechLikely for a raw JSON track node from the CURRENT
        /// thresholds rather than from whatever verdict happened to be persisted. Not
        /// called by any production path as of 2026-07-25 — RunMigrate's speech purge
        /// was its only caller, and that purge is gone. Kept as the JSON-facing twin of
        /// ComputeTruedatVerdict (which --list-speech and the --stats class-stat count
        /// use on the TrackEntry side) for any future JSON-only caller, and to pin that
        /// the two computations agree.
        ///
        /// Reads exactly the inputs ComputeTruedatVerdict's speech block consumes. If that
        /// signal list changes, this must change with it — the self-test
        /// "list-speech recompute matches the TrackEntry path" is the drift guard.</summary>
        static string RecomputeSpeechLikely(JsonNode? track)
        {
            var f = new TrackFeatures
            {
                Danceability       = SafeDbl(track, "danceability") ?? 0,
                ChordsStrength     = SafeDbl(track, "chordsStrength"),
                SilenceRate30dB    = SafeDbl(track, "silenceRate30dB"),
                ZeroCrossingRate   = SafeDbl(track, "zeroCrossingRate") ?? 0,
                BpmFirstPeakWeight = SafeDbl(track, "bpmFirstPeakWeight"),
            };
            var edmaStrength = SafeDbl(track, "keyVotes", "edma", "strength");
            if (edmaStrength.HasValue)
                f.KeyVoteEdma = new KeyVote
                {
                    Key = SafeStr(track, "keyVotes", "edma", "key") ?? "",
                    Scale = SafeStr(track, "keyVotes", "edma", "scale") ?? "",
                    Strength = edmaStrength.Value,
                };
            return ComputeTruedatVerdict("", new TrackEntry { Features = f }).SpeechLikely;
        }

        static void RunMigrate(string moodsPath)
        {
            Console.WriteLine("=== Migrate Mode ===");
            Console.WriteLine("Cleans up mbxmoods.json: strips legacy fields (valence/arousal, audioMd5, chromaprint), renames SMFM keys (sensme*->smfm*)");
            if (!_fileMd5Enabled)
                Console.WriteLine("Also strips fileMd5 (nothing consumes it; pass --file-md5 to keep it)");
            Console.WriteLine();

            if (!File.Exists(moodsPath)) { Console.WriteLine($"No moods file found: {moodsPath}"); return; }

            Console.WriteLine($"Loading: {moodsPath}");
            var json = File.ReadAllText(moodsPath);
            var docOptions = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            var root = JsonNode.Parse(json, null, docOptions)?.AsObject();
            if (root == null) { Console.WriteLine("Invalid JSON in moods file."); return; }
            var tracks = root["tracks"]?.AsObject();
            if (tracks == null || tracks.Count == 0) { Console.WriteLine("No tracks in moods file."); return; }

            int stripped = 0, renamed = 0, md5Stripped = 0, legacyStripped = 0, total = tracks.Count;
            foreach (var kv in tracks)
            {
                var trackData = kv.Value?.AsObject();
                if (trackData == null) continue;
                bool va = false;
                if (trackData.Remove("valence")) va = true;
                if (trackData.Remove("arousal")) va = true;
                if (va) stripped++;
                // fileMd5 cleanup — the field is unconsumed (MBXHub indexes
                // audioStreamSha256 only) and no longer maintained by default.
                // --migrate --file-md5 keeps it for external-interop users.
                if (!_fileMd5Enabled && trackData.Remove("fileMd5")) md5Stripped++;
                // Legacy fingerprint-pipeline keys — the producing modes are gone;
                // nothing reads these. Strip unconditionally.
                bool legacy = false;
                if (trackData.Remove("audioMd5")) legacy = true;
                if (trackData.Remove("chromaprint")) legacy = true;
                if (trackData.Remove("chromaprintDuration")) legacy = true;
                if (legacy) legacyStripped++;
                // SMFM key rename: sensme* -> smfm* (rename, not removal — the data is kept).
                bool tr = false;
                if (RenameJsonKey(trackData, "sensmeScores",      "smfmScores"))      tr = true;
                if (RenameJsonKey(trackData, "sensmeChannel",     "smfmChannel"))     tr = true;
                if (RenameJsonKey(trackData, "sensmeChannelName", "smfmChannelName")) tr = true;
                if (tr) renamed++;
            }

            Console.WriteLine($"Tracks: {total}");
            if (stripped > 0)
                Console.WriteLine($"Stripped valence/arousal from: {stripped}");
            if (renamed > 0)
                Console.WriteLine($"Renamed SMFM keys (sensme*->smfm*) on: {renamed}");
            if (md5Stripped > 0)
                Console.WriteLine($"Stripped fileMd5 from: {md5Stripped}");
            if (legacyStripped > 0)
                Console.WriteLine($"Stripped audioMd5/chromaprint from: {legacyStripped}");

            if (stripped == 0 && renamed == 0 && md5Stripped == 0 && legacyStripped == 0)
            {
                Console.WriteLine();
                Console.WriteLine("Nothing to migrate.");
                return;
            }

            root["trackCount"] = tracks.Count;
            var timestamp = DateTime.Now.ToString("yyyyMMdd.HHmmss");
            var bakPath = $"{moodsPath}.bak.{timestamp}";
            File.Copy(moodsPath, bakPath);
            Console.WriteLine(); Console.WriteLine($"Backup: {bakPath}");
            root["generatedAt"] = DateTime.UtcNow.ToString("o");
            var tmpPath = moodsPath + ".tmp";
            File.WriteAllText(tmpPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            AtomicReplace(tmpPath, moodsPath);
            Console.WriteLine($"Updated: {moodsPath} ({tracks.Count} tracks)");
        }

        // -- Tool discovery ----------------------------------------------------

        /// <summary>
        /// Find a tool exe by checking: app base directory, output/library directory, working directory.
        /// </summary>
        static string? FindTool(string exeName, params string[] searchDirs)
        {
            foreach (var dir in searchDirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                var path = Path.Combine(dir, exeName);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        // --preview measures nothing itself, so its ETA's real-time-factor comes from
        // the catalog's own analysis history: stored analysisDuration against each
        // track's known length. Median (upper median on a tie) rather than mean, so a
        // handful of outliers cannot skew it — extracted as its own method so it is
        // testable without a live catalog file or a full Main() run.
        internal static double MedianCatalogRtf(IEnumerable<ITunesTrack> tracks, IDictionary<string, TrackEntry> catalog)
        {
            var samples = new List<double>();
            foreach (var t in tracks)
            {
                if (t.TotalTimeMs <= 0 || string.IsNullOrEmpty(t.Location)) continue;
                if (!catalog.TryGetValue(t.Location!, out var e) || e == null) continue;
                // A missing AnalysisDurationSecs (null) or a non-positive one (0 or
                // negative, which should not occur but must not be trusted blindly)
                // is skipped rather than treated as 0 — a 0 sample would silently drag
                // the whole median toward "instant", understating every ETA it feeds.
                if (!(e.AnalysisDurationSecs > 0)) continue;
                samples.Add(e.AnalysisDurationSecs!.Value / (t.TotalTimeMs / 1000.0));
            }
            if (samples.Count == 0) return 0;
            samples.Sort();
            return samples[samples.Count / 2];   // median: immune to a few outliers
        }

        // Resolve the iTunes Music Library.xml location with a fallback probe order.
        // Supports the "drop the truedat folder into musicbee\library\ and run it"
        // install pattern: the exe-dir-parent probe picks up the XML that sits one
        // level up from where the exe lives, regardless of cwd.
        //
        // Probe order (first hit wins):
        //   1. Explicit positional arg (caller's responsibility to pass)
        //   2. <exe-dir>\..\iTunes Music Library.xml   (install-parent — primary case)
        //   3. <exe-dir>\iTunes Music Library.xml      (alongside the exe)
        //   4. <cwd>\iTunes Music Library.xml          (legacy behavior — backward compat)
        //
        // On no hit: returns "iTunes Music Library.xml" so the caller's File.Exists
        // check fails with the same shape as before. moodsPath continues to derive
        // from Path.GetDirectoryName(xmlPath), so the output lands next to whichever
        // copy was found.
        /// <summary>
        /// Resolve the mood catalog for the read-only JSON modes (`--stats`, `--list-speech`,
        /// `--list-missing-smfm`). They take their catalog POSITIONALLY, and when the operator
        /// gives nothing the catalog is derived from the LIBRARY'S DIRECTORY — never the library
        /// file itself.
        ///
        /// That distinction is the whole point. These modes used to fall back to
        /// `analyzeFileMoods ?? xmlPath`, so a bare `truedat --list-missing-smfm` handed the
        /// auto-discovered `iTunes Music Library.xml` straight to <see cref="LoadExistingMoods"/>.
        /// The XML exists, so the File.Exists guard passed; the JSON reader then failed on the
        /// leading '&lt;', the file was declared **corrupt**, a `.corrupt.&lt;date&gt;` copy of the
        /// user's library was written, and the console advised DELETING it. A read-only lister
        /// must never be able to reach that state — reported by a user on 0.5.4.4.
        ///
        /// Returns null and sets <paramref name="refusal"/> when the caller named something that
        /// cannot be a catalog; the caller prints it and exits 1.
        /// </summary>
        internal const string MoodsFileName = "mbxmoods.json";

        static string? ResolveMoodsCatalog(string? explicitPath, string? libraryXmlPath, out string? refusal)
        {
            refusal = null;

            if (!string.IsNullOrEmpty(explicitPath))
            {
                // A directory means "the catalog in here".
                if (Directory.Exists(explicitPath))
                    return Path.Combine(explicitPath!, MoodsFileName);

                // Naming the library is a plausible mistake — it is the positional argument every
                // OTHER mode takes — so say what to do instead of parsing it as JSON and calling
                // the result corrupt.
                if (explicitPath!.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    refusal = $"Error: {explicitPath} is an iTunes library XML, not a mood catalog." + Environment.NewLine
                            + $"  These modes read {MoodsFileName}. Pass its path or its folder, or omit the argument to use the one beside your library.";
                    return null;
                }

                return explicitPath;
            }

            // Nothing given: the catalog sits beside the library, so use the library's DIRECTORY.
            if (!string.IsNullOrEmpty(libraryXmlPath))
            {
                var dir = Directory.Exists(libraryXmlPath)
                    ? libraryXmlPath!
                    : Path.GetDirectoryName(Path.GetFullPath(libraryXmlPath!));
                if (!string.IsNullOrEmpty(dir))
                    return Path.Combine(dir!, MoodsFileName);
            }

            return Path.Combine(Environment.CurrentDirectory, MoodsFileName);
        }

        static string ResolveITunesXml(string? explicitArg)
        {
            const string XmlName = "iTunes Music Library.xml";
            if (!string.IsNullOrEmpty(explicitArg)) return explicitArg!;

            string exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? exeParent = null;
            try { exeParent = Path.GetDirectoryName(exeDir); } catch { }

            // 1. Exe-dir parent (musicbee\library\ when installed at musicbee\library\truedat\)
            if (!string.IsNullOrEmpty(exeParent))
            {
                var cand = Path.Combine(exeParent!, XmlName);
                if (File.Exists(cand)) return cand;
            }
            // 2. Exe-dir
            var exeCand = Path.Combine(exeDir, XmlName);
            if (File.Exists(exeCand)) return exeCand;
            // 3. Cwd (legacy)
            if (File.Exists(XmlName)) return XmlName;

            // No hit — return the cwd-relative name so the caller's File.Exists check
            // surfaces a familiar error.
            return XmlName;
        }

        /// <summary>
        /// Search for the MusicBrainz/AcousticBrainz catalog file in standard locations.
        /// Checks data/ subdirectory and root of each search dir, for both .jsonl.gz and .jsonl.
        /// </summary>
        static string? FindCatalog(params string[] searchDirs)
        {
            var names = new[] { "synthlib-catalog.jsonl.gz", "synthlib-catalog.jsonl" };
            foreach (var dir in searchDirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                foreach (var name in names)
                {
                    var dataPath = Path.Combine(dir, "data", name);
                    if (File.Exists(dataPath)) return dataPath;
                    var rootPath = Path.Combine(dir, name);
                    if (File.Exists(rootPath)) return rootPath;
                }
            }
            return null;
        }

        // -- Path resolution helpers -------------------------------------------

        static bool HasNonAscii(string s)
        {
            for (int i = 0; i < s.Length; i++)
                if (s[i] > 127) return true;
            return false;
        }

        /// <summary>
        /// Try to create a hardlink with an ASCII name for a non-ASCII audio path.
        /// Tries the file's own drive root first, then falls back to temp directory.
        /// Returns (linkPath, "hardlink") on success, or (null, description) on failure.
        /// </summary>
        static (string? LinkPath, string Method) TryCreateHardlink(string audioPath)
        {
            var ext = Path.GetExtension(audioPath);
            var root = Path.GetPathRoot(Path.GetFullPath(audioPath)) ?? Path.GetTempPath();
            // First candidate MUST be the source's own volume — a hardlink cannot cross
            // volumes, which is why that one is not relocatable by --stage-dir (I-3) and why
            // the sweep of <drive>\.truedat-tmp leans on the age gate + sibling check instead.
            // The FALLBACK is relocatable, so it follows --stage-dir like everything else.
            string[] candidates = { Path.Combine(root, ".truedat-tmp"), Path.Combine(_stageOpts.StageDir, ".truedat-tmp") };

            int lastErr = 0;
            foreach (var tempDir in candidates)
            {
                try
                {
                    Directory.CreateDirectory(tempDir);
                    var linkPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}{ext}");
                    if (CreateHardLink(linkPath, audioPath, IntPtr.Zero))
                    {
                        Console.WriteLine($"  DEBUG: hardlink created for non-ASCII path: {audioPath} -> {linkPath}");
                        return (linkPath, "hardlink");
                    }
                    lastErr = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                }
                catch { }
            }

            Console.Error.WriteLine($"  DEBUG: hardlink failed for non-ASCII path (err={lastErr}): {audioPath}");
            return (null, "original (hardlink failed)");
        }

        // -- Source staging (UNC + hardlink-failure fallback) ----------------

        /// <summary>
        /// CLI-derived staging + signal configuration. Default: staging enabled,
        /// dir = %TEMP%\.truedat-stage. Override via --no-stage / --stage-dir.
        /// NoBitUsage / NoHfAnalysis live here too — same per-scan lifetime,
        /// same end-of-scan cost surface.
        /// </summary>
        internal sealed class StageOptions
        {
            public bool NoStage { get; set; }
            public string StageDir { get; set; } = Path.Combine(Path.GetTempPath(), ".truedat-stage");
            public bool NoBitUsage { get; set; }
            public bool NoHfAnalysis { get; set; }
        }

        /// <summary>
        /// Handle returned by SourceStager.Open. The Path property is what
        /// every worker should read; Dispose deletes the staged copy if any.
        /// Always non-null — on stage failure, Method="direct" and Path is
        /// the original source.
        ///
        /// SourceLastWriteUtc is a snapshot of the source file's mtime captured
        /// inside OpenStagedSource (after copy when staging, at entry otherwise).
        /// Scan modes record this on the TrackEntry instead of re-stat'ing at
        /// end-of-work — guards against an mtime touch between the copy and
        /// the entry being persisted (the analyzed bytes match the recorded mtime).
        /// DateTime.MinValue when the stat call threw.
        /// </summary>
        internal sealed class SourceHandle : IDisposable
        {
            public string Path { get; }
            public string Method { get; }    // "direct" | "hardlink" | "staged" | "staged-fallback"
            public long StageMs { get; }
            public long StageBytes { get; }
            public DateTime SourceLastWriteUtc { get; }
            private readonly string? _toDelete;

            public SourceHandle(string path, string method, long stageMs, long stageBytes, string? toDelete, DateTime sourceLastWriteUtc)
            {
                Path = path;
                Method = method;
                StageMs = stageMs;
                StageBytes = stageBytes;
                SourceLastWriteUtc = sourceLastWriteUtc;
                _toDelete = toDelete;
            }

            public void Dispose()
            {
                if (_toDelete == null) return;
                try { File.Delete(_toDelete); } catch { }
            }
        }

        /// <summary>
        /// Return true if <paramref name="path"/> lives on a network drive (drive
        /// letter mapped to a remote share). UNC paths (`\\server\share\…`) are
        /// handled by the caller's prefix check — this covers the conceptually-UNC
        /// case where the user mapped Z:\ to a share. Per-root memoized so the
        /// fan-out only pays the DriveInfo cost once per unique drive root.
        /// Failures (unmounted drive, GetPathRoot throws) are cached as false.
        /// </summary>
        static bool IsNetworkDrivePath(string sourcePath)
        {
            string root;
            try
            {
                root = Path.GetPathRoot(Path.GetFullPath(sourcePath)) ?? "";
            }
            catch { return false; }
            if (string.IsNullOrEmpty(root)) return false;
            return _networkDriveCache.GetOrAdd(root, r =>
            {
                try { return new DriveInfo(r).DriveType == DriveType.Network; }
                catch { return false; }
            });
        }

        /// <summary>
        /// Validate that every char of <paramref name="ext"/> is ASCII printable
        /// (0x20..0x7E). Music extensions in the wild are all ASCII (.mp3, .flac,
        /// .m4a, .wav, .aiff, .ogg, .opus, .wma) so the common case is unchanged.
        /// </summary>
        static bool IsAsciiPrintable(string ext)
        {
            for (int i = 0; i < ext.Length; i++)
            {
                char c = ext[i];
                if (c < 0x20 || c > 0x7E) return false;
            }
            return true;
        }

        /// <summary>
        /// Decide whether to stage, hardlink, or pass through the source path.
        /// Always returns a non-null handle.
        ///
        /// Decision matrix:
        ///   Local NTFS, ASCII             -> direct (Method="direct")
        ///   Local NTFS, non-ASCII path     -> stage (Method="staged-fallback")
        ///   UNC (\\server\share\…)         -> stage (Method="staged")
        ///   Mapped network drive (Z:\ etc) -> stage (Method="staged")
        ///
        /// On stage failure: emits a stderr WARN, returns Method="direct" with
        /// the original sourcePath, so the workers fall back to direct read.
        ///
        /// <paramref name="sourceSize"/> is the file size the caller already has
        /// from its own FileInfo lookup — passed in for the audit log so we don't
        /// re-stat the staged destination. Pass 0 if unknown; the audit line falls
        /// back to the staged-file size (still one stat in that case).
        /// </summary>
        internal static SourceHandle OpenStagedSource(string sourcePath, StageOptions opts, long sourceSize = 0)
        {
            // Snapshot mtime once at entry. Used by the staging happy path AFTER the
            // copy so it reflects "the mtime that was true when we captured the bytes".
            // Used by direct-passthrough as the uniform mtime contract on the handle.
            DateTime mtime = DateTime.MinValue;

            // Pass-through #1: staging is disabled globally.
            if (opts.NoStage)
            {
                try { mtime = File.GetLastWriteTimeUtc(sourcePath); } catch { }
                return new SourceHandle(sourcePath, "direct", 0, 0, null, mtime);
            }

            // Pass-through #2: purely local ASCII path — nothing to gain.
            bool isUnc = sourcePath.StartsWith(@"\\", StringComparison.Ordinal);
            bool isNetworkDrive = !isUnc && IsNetworkDrivePath(sourcePath);
            bool isNetwork = isUnc || isNetworkDrive;
            bool hasNonAscii = HasNonAscii(sourcePath);
            bool shouldStage = isNetwork || hasNonAscii;
            if (!shouldStage)
            {
                try { mtime = File.GetLastWriteTimeUtc(sourcePath); } catch { }
                return new SourceHandle(sourcePath, "direct", 0, 0, null, mtime);
            }

            // Otherwise: stage. The staged filename is GUID-based so the staged
            // path is always ASCII regardless of the source.
            string method = isNetwork ? "staged" : "staged-fallback";
            string dest = "";
            var sw = Stopwatch.StartNew();
            try
            {
                // Sanitize the extension to ASCII printable. Music extensions in
                // the wild are all ASCII; a non-ASCII or empty ext lands on .bin
                // and TagLib / Essentia / ffmpeg fall back to magic-byte probing.
                string ext = Path.GetExtension(sourcePath);
                if (string.IsNullOrEmpty(ext) || !IsAsciiPrintable(ext))
                    ext = ".bin";
                dest = Path.Combine(opts.StageDir, $"{Guid.NewGuid():N}{ext}");
                Directory.CreateDirectory(opts.StageDir);
                File.Copy(sourcePath, dest, overwrite: false);
                // Capture mtime IMMEDIATELY after the copy completes. If the file
                // is tag-touched between now and end-of-work, the snapshot still
                // matches the bytes we just copied — preventing TrackEntry from
                // recording an mtime that's newer than the analyzed audio.
                try { mtime = File.GetLastWriteTimeUtc(sourcePath); } catch { }
                sw.Stop();
                long bytes = sourceSize;
                if (bytes <= 0)
                {
                    try { bytes = new FileInfo(dest).Length; } catch { }
                }
                if (_audit)
                    Console.Error.WriteLine($"  STAGE: {method} {sourcePath} -> {dest} ({sw.ElapsedMilliseconds}ms, {(bytes / 1024.0 / 1024.0):F1}MB)");
                Interlocked.Increment(ref _stageSuccessCount);
                return new SourceHandle(dest, method, sw.ElapsedMilliseconds, bytes, dest, mtime);
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.Error.WriteLine($"  Warning: stage failed: {sourcePath} -> {dest}: {ex.Message}; falling back to direct read");
                try { if (dest.Length > 0) File.Delete(dest); } catch { }
                try { mtime = File.GetLastWriteTimeUtc(sourcePath); } catch { }
                Interlocked.Increment(ref _stageFallbackCount);
                return new SourceHandle(sourcePath, "direct", 0, 0, null, mtime);
            }
        }

        /// <summary>
        /// Emit the end-of-scan staging summary to stdout, and a stderr WARNING if
        /// staging fell back to direct read for >5% of attempted stages. Suppressed
        /// when no staging was attempted (happy path stays quiet on local-only scans).
        /// Resets the counters so back-to-back scan modes report independently.
        /// </summary>
        static void EmitStagingSummary()
        {
            int success = Interlocked.Exchange(ref _stageSuccessCount, 0);
            int fallback = Interlocked.Exchange(ref _stageFallbackCount, 0);
            int total = success + fallback;
            if (total == 0) return;
            if (fallback == 0)
            {
                Console.WriteLine($"  staging:    {success} staged");
                return;
            }
            Console.WriteLine($"  staging:    {success} staged, {fallback} direct-fallback");
            if ((double)fallback / total > 0.05)
            {
                Console.Error.WriteLine(
                    $"WARNING: staging degraded ({fallback} of {total} tracks fell back to direct read) — check stage-dir disk space / permissions");
            }
        }

        /// <summary>Per-entry presence flags for the catalog summary. Computed once
        /// per entry by <see cref="Probe"/> and reused by both the aggregate tally
        /// (<see cref="ComputeCatalogStats"/>) and the small-catalog per-file listing,
        /// so the two views can never disagree.</summary>
        sealed class EntryProbe
        {
            public bool Essentia;     // has core features (non-empty mfcc[]) — all-or-nothing
            public bool Sha256;       // audioStreamSha256 — primary cross-system identity
            public bool FileMd5;
            public bool Head64k;      // fingerprint.v1.audioHead64kMd5
            public bool Smfm;         // Sony 12-TONE (smfmScores populated)
        }

        static EntryProbe Probe(TrackEntry e)
        {
            var f = e?.Features;
            return new EntryProbe
            {
                Essentia    = f?.Mfcc != null && f.Mfcc.Length > 0,
                Sha256      = e != null && !string.IsNullOrEmpty(e.AudioStreamSha256),
                FileMd5     = e != null && !string.IsNullOrEmpty(e.FileMd5),
                Head64k     = e?.FingerprintV1 != null && !string.IsNullOrEmpty(e.FingerprintV1.AudioHead64kMd5),
                Smfm        = f?.SmfmScores != null && f.SmfmScores.Length > 0,
            };
        }

        sealed class CatalogStats
        {
            public int Total;
            public int EssentiaAnalyzed;
            public int AudioStreamSha256;
            public int FileMd5;
            public int AudioHead64kMd5;
            public int Smfm;
            public int DuplicateGroups;   // distinct audioStreamSha256 values shared by 2+ entries
            public int RedundantCopies;   // sum over groups of (members - 1)
            public int MissingWave;       // Essentia-analyzed but lacking the 2026-07-22 tonal/rhythm wave
            // Speech splits into two disjoint counts so each summary line can point at a
            // surface that actually shows what it counted (the old union count pointed at
            // --list-speech, which lists only the acoustic subset — live: "Speech: 1" vs
            // an empty --list-speech, because the 1 was label-only).
            public int SpeechAcoustic;    // recomputed SpeechLikely=="yes" — exactly the set RunListSpeech lists
            public int SpeechLabelOnly;   // genre=="Podcast" label but NOT acoustic-yes — invisible to --list-speech; a genre rule reaches them
            public int MissingFingerprint; // entries with no fingerprint.v1 block
        }

        /// <summary>Read-only: list the entries whose stored features recompute to a
        /// confident speechLikely=="yes" verdict — candidates for an exclusion rule, not
        /// a --migrate prune (--migrate never removes entries; audio files are never
        /// touched either way). Writes mbxmoods-speech.csv
        /// (path,artist,title,album,genre,codec,speechConfidence,method) next to the
        /// moods file and prints a count + first-20 preview. The verdict is recomputed
        /// via ComputeTruedatVerdict (write-time verdict, not persisted in the entry).
        /// This list is count-equal with the --stats "Speech (acoustic)" line — both are
        /// exactly the recomputed speechLikely=="yes" set, regardless of genre label
        /// (see ComputeCatalogStats). Entries whose stored genre is "Podcast" but whose
        /// acoustic verdict did NOT reach yes are deliberately NOT listed here — --stats
        /// reports them separately as "Speech (genre label)" and points at a genre rule,
        /// because this surface structurally cannot show them (the old union count
        /// pointed here and the numbers disagreed). This is the only speech surface that works on
        /// the moods JSON alone — --preview needs the iTunes XML and can't run on a
        /// metadata-mirror box.</summary>
        static void RunListSpeech(string moodsPath, IEnumerable<TrackEntry> entries)
        {
            var hits = new List<(string Path, string Artist, string Title, string Album, string Genre, string Codec, double? Conf, string Method)>();
            foreach (var e in entries)
            {
                if (e?.Features == null) continue;
                var v = ComputeTruedatVerdict(e.Features.FilePath ?? "", e);
                if (!string.Equals(v.SpeechLikely, "yes", StringComparison.OrdinalIgnoreCase)) continue;
                hits.Add((e.Features.FilePath ?? "", e.Features.Artist, e.Features.Title,
                    e.Features.Album, e.Features.Genre, e.FingerprintV1?.Codec ?? "", v.SpeechConfidence, v.SpeechMethod));
            }
            hits.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

            var csvPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(moodsPath)) ?? ".", "mbxmoods-speech.csv");
            var sb = new StringBuilder();
            sb.AppendLine("path,artist,title,album,genre,codec,speechConfidence,method");
            foreach (var h in hits)
                sb.AppendLine($"{CsvEscape(h.Path)},{CsvEscape(h.Artist)},{CsvEscape(h.Title)},{CsvEscape(h.Album)},{CsvEscape(h.Genre)},{CsvEscape(h.Codec)},{(h.Conf.HasValue ? h.Conf.Value.ToString("F2") : "")},{CsvEscape(h.Method)}");
            File.WriteAllText(csvPath, sb.ToString());

            Console.WriteLine($"Speech-likely entries: {hits.Count:N0}");
            if (hits.Count > 0)
            {
                int preview = Math.Min(20, hits.Count);
                foreach (var h in hits.Take(preview))
                    Console.WriteLine($"  [{(h.Conf.HasValue ? h.Conf.Value.ToString("F2") : "  ? ")}] {h.Path}");
                if (hits.Count > preview)
                    Console.WriteLine($"  ... {hits.Count - preview:N0} more (see CSV)");
                Console.WriteLine();
                Console.WriteLine($"CSV:    {csvPath}");
                Console.WriteLine("These are candidates for an exclusion rule, not a --migrate prune (--migrate never removes entries).");
                Console.WriteLine("Review, then add a rule to a decisions delta and run: truedat --apply-exclusions <delta>");
            }
        }

        /// <summary>Read-only: list catalog entries with no Sony SMFM (12-TONE) data.
        /// Writes mbxmoods-smfm-missing.csv (path,artist,title,album,codec) next to the
        /// moods file and prints coverage + a first-20 preview.
        ///
        /// NOTE this is deliberately NOT wired into the --stats Recommended block. SMFM
        /// is embedded in the file by Sony tooling and only READ by truedat (fresh every
        /// scan, never cached) — there is no truedat command that adds it, so a rescan
        /// cannot close this gap. The advisor only ever points at gaps a command fixes;
        /// listing this one there would be the same defect fixed in d146a04.</summary>
        static void RunListMissingSmfm(string moodsPath, IEnumerable<TrackEntry> entries)
        {
            var missing = new List<(string Path, string Artist, string Title, string Album, string Codec)>();
            int total = 0, present = 0;
            foreach (var e in entries)
            {
                if (e?.Features == null) continue;
                total++;
                if (e.Features.HasSmfm) { present++; continue; }
                missing.Add((e.Features.FilePath ?? "", e.Features.Artist, e.Features.Title,
                    e.Features.Album, e.FingerprintV1?.Codec ?? ""));
            }
            missing.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

            var csvPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(moodsPath)) ?? ".", "mbxmoods-smfm-missing.csv");
            var sb = new StringBuilder();
            sb.AppendLine("path,artist,title,album,codec");
            foreach (var m in missing)
                sb.AppendLine($"{CsvEscape(m.Path)},{CsvEscape(m.Artist)},{CsvEscape(m.Title)},{CsvEscape(m.Album)},{CsvEscape(m.Codec)}");
            File.WriteAllText(csvPath, sb.ToString());

            int pct = total > 0 ? (int)(100.0 * present / total) : 0;
            Console.WriteLine($"Sony SMFM (12-TONE): {present:N0} of {total:N0} tracks ({pct}%) — {missing.Count:N0} missing");
            if (missing.Count > 0)
            {
                int preview = Math.Min(20, missing.Count);
                foreach (var m in missing.Take(preview))
                    Console.WriteLine($"  {m.Path}");
                if (missing.Count > preview)
                    Console.WriteLine($"  ... {missing.Count - preview:N0} more (see CSV)");
                Console.WriteLine();
                Console.WriteLine($"CSV:    {csvPath}");
                Console.WriteLine("SMFM is embedded by Sony tooling and only read by truedat — rescanning will NOT add it.");
            }
        }

        static CatalogStats ComputeCatalogStats(IEnumerable<TrackEntry> entries)
        {
            var s = new CatalogStats();
            var shaCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
            {
                if (e == null) continue;
                s.Total++;
                var pr = Probe(e);
                if (pr.Essentia)    s.EssentiaAnalyzed++;
                if (pr.Sha256)      s.AudioStreamSha256++;
                if (pr.FileMd5)     s.FileMd5++;
                if (pr.Head64k)     s.AudioHead64kMd5++;
                if (pr.Smfm)        s.Smfm++;
                if (pr.Sha256)
                {
                    shaCount.TryGetValue(e.AudioStreamSha256!, out var c);
                    shaCount[e.AudioStreamSha256!] = c + 1;
                }
                // Wave-missing: analyzed but lacking the 2026-07-22 tonal/rhythm wave.
                // Deliberately flag-independent (HasCurrentFeatures widens only under
                // --refresh-features; the advisor must see the gap regardless).
                if (pr.Essentia && e.Features?.AverageLoudness == null) s.MissingWave++;
                if (e.FingerprintV1 == null) s.MissingFingerprint++;
                if (e.Features != null)
                {
                    // ONE speech genus (audiobook, comedy, lecture, news, interview,
                    // talk-dominant), but TWO kinds of evidence, counted disjointly:
                    // the acoustic verdict (speechLikely=="yes" — exactly the set
                    // --list-speech lists) and the label-only remainder (stored genre
                    // "Podcast" — an explicit author claim — whose acoustic verdict did
                    // NOT reach yes). Disjoint because the summary must point each count
                    // at a surface that can show it: --list-speech cannot show a
                    // label-only entry, so lumping them (the old union count) made the
                    // advisor's number and its review surface disagree. An entry carrying
                    // both kinds of evidence counts once, as acoustic.
                    bool isSpeechGenre = string.Equals(e.Features.Genre, "Podcast", StringComparison.OrdinalIgnoreCase);
                    if (ComputeTruedatVerdict(e.Features.FilePath ?? "", e).SpeechLikely == "yes")
                        s.SpeechAcoustic++;
                    else if (isSpeechGenre)
                        s.SpeechLabelOnly++;
                }
            }
            foreach (var c in shaCount.Values)
                if (c > 1) { s.DuplicateGroups++; s.RedundantCopies += c - 1; }
            return s;
        }

        /// <summary>Render a human-readable catalog summary to stdout (so the MoodsMode
        /// --audit tee captures it in truedat.log). When the catalog has fewer than
        /// <paramref name="detailThreshold"/> tracks it lists each file with its per-field
        /// status instead of aggregate counts — aggregates over 1-4 files aren't useful.</summary>
        static void ReportCatalog(string path, IEnumerable<TrackEntry> entriesEnum, int detailThreshold, ISet<string>? libraryKeys = null, ISet<string>? scanWorkList = null)
        {
            var entries = entriesEnum as IList<TrackEntry> ?? new List<TrackEntry>(entriesEnum);
            int total = entries.Count;
            Console.WriteLine();
            if (total == 0)
            {
                Console.WriteLine($"Catalog summary: {path}");
                Console.WriteLine("  (empty — no track entries)");
                return;
            }

            string YN(bool b) => b ? "yes" : "NO ";

            // Small catalog: list each file with its per-field status.
            if (total < detailThreshold)
            {
                Console.WriteLine($"Catalog summary: {path}  ({total} track{(total == 1 ? "" : "s")})");
                // Show the opt-in fileMd5 column only when some entry actually carries it,
                // matching the aggregate view (the two views must never disagree).
                bool anyFileMd5 = entries.Any(e => e != null && !string.IsNullOrEmpty(e.FileMd5));
                int n = 0;
                foreach (var e in entries)
                {
                    var pr = Probe(e);
                    Console.WriteLine($"  {++n}. {e?.Features?.FilePath ?? "(unknown path)"}");
                    Console.WriteLine($"       essentia:{YN(pr.Essentia)}  audioStreamSha256:{YN(pr.Sha256)}{(anyFileMd5 ? $"  fileMd5:{YN(pr.FileMd5)}" : "")}  audioHead64kMd5:{YN(pr.Head64k)}  smfm:{YN(pr.Smfm)}");
                }
                return;
            }

            // Aggregate view. Right-aligned counts, thousands separators, percent +
            // a plain-English status note so the gaps read at a glance.
            // Floor the percent so a near-complete count never rounds up to a
            // misleading "100%" while tracks are still missing.
            string Cov(string label, int present, bool showComplete = true, bool showGap = true)
            {
                int pct = total == 0 ? 0 : (int)Math.Floor(100.0 * present / total);
                string note = present == total
                    ? (showComplete ? "complete" : "")
                    : (showGap ? $"{total - present:N0} missing" : "");
                string line = $"  {label,-20} {present,9:N0} / {total,-9:N0} {pct,3}%";
                return note.Length > 0 ? line + "   " + note : line;
            }

            var s = ComputeCatalogStats(entries);
            Console.WriteLine($"Catalog summary: {path}");
            Console.WriteLine($"  {total:N0} tracks total");
            Console.WriteLine();
            Console.WriteLine(Cov("Essentia analysis", s.EssentiaAnalyzed));
            Console.WriteLine(Cov("audioStreamSha256", s.AudioStreamSha256) + "   (primary identity)");
            // Internal hash plumbing only earns a line when it's ACTIONABLE:
            //  - fileMd5 is opt-in (--file-md5) and unconsumed; without the flag, stray
            //    stored values just mean "--migrate strips these" — say that, once.
            //  - audioHead64kMd5 (quick-cache evidence inside fingerprint.v1) only
            //    matters when incomplete (legacy entries → --verify --backfill fills).
            if (_fileMd5Enabled && s.FileMd5 > 0)
                Console.WriteLine(Cov("fileMd5", s.FileMd5));
            else if (s.FileMd5 > 0)
                Console.WriteLine($"  {"fileMd5",-20} {s.FileMd5,9:N0} stray values   (unused; truedat --migrate strips)");
            if (s.AudioHead64kMd5 < total)
                Console.WriteLine(Cov("audioHead64kMd5", s.AudioHead64kMd5) + "   (quick-cache; --verify --backfill fills)");
            Console.WriteLine(Cov("Sony SMFM (12-TONE)", s.Smfm, showComplete: false, showGap: false));
            if (s.DuplicateGroups > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"  Duplicate audio       {s.DuplicateGroups,9:N0} groups, {s.RedundantCopies:N0} redundant   (list: truedat --duplicates)");
            }
            // Speech is a class stat like Duplicate audio above — a fact about the
            // catalog, not a nag. Two lines, not one: each must point ONLY at a surface
            // that can show what it counted (d146a04 advisor rule). The acoustic count
            // IS --list-speech's list; label-only entries are invisible there (it lists
            // acoustic-yes regardless of genre), so the old union count produced
            // "Speech: 1" while --list-speech printed 0. Label-only entries are reached
            // by a genre rule, not by the acoustic review flow. Not a --migrate
            // recommendation: the purge that used to sit behind it is gone.
            if (s.SpeechAcoustic > 0 || s.SpeechLabelOnly > 0)
                Console.WriteLine();
            if (s.SpeechAcoustic > 0)
                Console.WriteLine($"  {"Speech (acoustic)",-20} {s.SpeechAcoustic,9:N0} tracks   (review: truedat --list-speech, then write a decisions delta and truedat --apply-exclusions)");
            if (s.SpeechLabelOnly > 0)
                Console.WriteLine($"  {"Speech (genre label)",-20} {s.SpeechLabelOnly,9:N0} tracks   (labeled Podcast in the library; not acoustically speech — a genre rule excludes them: kind:genre, value:Podcast)");

            // Recommended next steps — detected state -> exact command. Advisory
            // only: truedat NEVER prompts; it prints what to run and moves on.
            var rec = new List<string>();
            // The wave-missing count is one number but several different fixes. Split it
            // into disjoint, individually-actionable buckets so the operator can see WHY a
            // plain --refresh-features never clears the total (errored + orphaned entries
            // are unreachable by a rescan). Precedence: errored, then orphaned, then the
            // genuinely-stale remainder. Zero-count buckets are hidden.
            List<string>? waveBreakdown = null;
            if (s.MissingWave > 0)
            {
                var errs = LoadExistingErrors(
                    Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", "mbxmoods-errors.csv"));
                // Only entries the scan actually re-analyzes can be cleared by
                // --refresh-features. Precedence per entry:
                //   errored (on the error list)          -> --retry-errors   (bad file)
                //   orphan  (not in the library at all)  -> --fixup          (drop it)
                //   filtered (in library, excluded from  -> unreachable by a rescan; a
                //             the scan work list)            rule-excluded/video/URL entry
                //   stale   (in the work list, no error) -> --refresh-features (the ONLY
                //                                            bucket a rescan fixes)
                int waveErrored = 0, waveOrphan = 0, waveFiltered = 0, waveStale = 0;
                foreach (var e in entries)
                {
                    if (e?.Features == null) continue;
                    if (!(e.Features.Mfcc != null && e.Features.Mfcc.Length > 0
                          && e.Features.AverageLoudness == null)) continue;
                    var key = e.Features.FilePath ?? "";
                    if (key.Length > 0 && errs.ContainsKey(key)) waveErrored++;
                    else if (libraryKeys != null && key.Length > 0 && !libraryKeys.Contains(key)) waveOrphan++;
                    else if (scanWorkList != null && key.Length > 0 && !scanWorkList.Contains(key)) waveFiltered++;
                    else waveStale++;
                }
                waveBreakdown = new List<string>();
                // --retry-errors only surfaces when there ARE errored entries — it re-attempts
                // known-bad files, so it's a one-off, not a step to run on every scan.
                if (waveErrored > 0)
                    waveBreakdown.Add($"{waveErrored,6:N0}  previously errored      ->  truedat --retry-errors   (one-off; bad files keep failing)");
                if (waveOrphan > 0)
                    waveBreakdown.Add($"{waveOrphan,6:N0}  no longer in library    ->  truedat --fixup          (drops orphaned entries)");
                if (waveFiltered > 0)
                    waveBreakdown.Add($"{waveFiltered,6:N0}  excluded from scanning  ->  a rescan skips these (filtered: exclusion-rule/video/URL/non-audio)");
                if (waveStale > 0)
                    waveBreakdown.Add(scanWorkList == null
                        ? $"{waveStale,6:N0}  lack the latest features ->  truedat --refresh-features  (from --stats this can't split out orphaned/excluded entries a rescan won't clear — run from the library dir for the exact breakdown)"
                        : $"{waveStale,6:N0}  analyzable, just stale   ->  truedat --refresh-features");
            }
            if (s.MissingFingerprint > 0)
                rec.Add($"{s.MissingFingerprint:N0} entries lack fingerprint.v1          ->  truedat --verify --backfill --backfill-level identity");
            if (!_fileMd5Enabled && s.FileMd5 > 0)
                rec.Add($"{s.FileMd5:N0} stray fileMd5 values                ->  truedat --migrate  (needs the library's iTunes XML; run from the library dir, not a metadata mirror)");
            bool anyRec = rec.Count > 0 || (waveBreakdown != null && waveBreakdown.Count > 0);
            if (anyRec)
            {
                Console.WriteLine();
                Console.WriteLine("  Recommended:");
                if (waveBreakdown != null && waveBreakdown.Count > 0)
                {
                    Console.WriteLine($"    {s.MissingWave:N0} entries lack the latest features:");
                    foreach (var w in waveBreakdown) Console.WriteLine($"      {w}");
                }
                foreach (var r in rec) Console.WriteLine($"    {r}");
            }
        }

        // Cross-encode candidate-key buckets (spec §1). Sized so lossy re-encode
        // perturbation of the aggregate MFCC means stays inside one bucket. Coeff 0
        // carries overall energy (values ~-1200..0) and needs a wider bucket than 1-12.
        // Calibrated 2026-07-04 against a 71k-track library mirror: 1,277 probable
        // groups, 69.1% title-agreement, 78.2% artist-agreement, mean size 2.05 (max 9)
        // — healthy. A 30%-tighter trial was tested and rejected: group count fell to
        // 1,153 but title-agreement also fell (67.3%), and the one persistent junk
        // cluster (era-matched hip-hop/dance tracks with near-identical bpm/key/
        // duration from compilation mastering) survived unchanged — it's genuine
        // feature-space proximity, not quantization slop, so tightening only cost
        // recall on real cross-encode matches. Frozen at original widths.
        internal static readonly double[] DupQMfcc =
        {
            24.0,                   // mfcc[0]
            8.0, 8.0, 8.0, 8.0,     // mfcc[1..4]
            6.0, 6.0, 6.0, 6.0,     // mfcc[5..8]
            5.0, 5.0, 5.0, 5.0      // mfcc[9..12]
        };

        /// <summary>Composite quantized-feature key for the probable (cross-encode)
        /// duplicate tier: 13 bucketed mfcc means + rounded bpm + key/mode + 2s duration
        /// bucket. Returns null when any required component is missing — the caller
        /// counts those as skipped. Authenticity/HF/loudness signals are deliberately
        /// excluded (they differ across encodes by design). Boundary misses are
        /// accepted: this tier is candidates-only, a human confirms.</summary>
        internal static string? BuildDupCandidateKey(TrackEntry e)
        {
            var f = e?.Features;
            if (f == null) return null;
            var mfcc = f.Mfcc;
            if (mfcc == null || mfcc.Length < 13) return null;
            if (f.Bpm <= 0 || string.IsNullOrEmpty(f.Key) || string.IsNullOrEmpty(f.Mode)) return null;
            int durationMs = e!.FingerprintV1?.DurationMs ?? 0;
            if (durationMs <= 0) return null;

            var sb = new StringBuilder(64);
            for (int i = 0; i < 13; i++)
                sb.Append((long)Math.Round(mfcc[i] / DupQMfcc[i])).Append(',');
            sb.Append((int)Math.Round(f.Bpm)).Append('|');
            sb.Append(f.Key.ToLowerInvariant()).Append('|');
            sb.Append(f.Mode.ToLowerInvariant()).Append('|');
            sb.Append(durationMs / 2000);
            return sb.ToString();
        }

        /// <summary>Pick the recommended keeper in a duplicate group (spec §2):
        /// lossless codec > bitDepth > sampleRate > bitrate > fileSize > shortest path,
        /// ordinal compare as the deterministic final tie-break. Members missing a
        /// field rank below members that have it (missing == 0). Pure annotation —
        /// truedat never acts on it.</summary>
        internal static string PickKeeper(IReadOnlyList<string> paths, Func<string, TrackEntry?> lookup)
        {
            string best = paths[0];
            for (int i = 1; i < paths.Count; i++)
                if (CompareKeeper(paths[i], best, lookup) > 0) best = paths[i];
            return best;
        }

        /// <summary>A copy that CLAIMS hi-res (>44.1 kHz or >=24-bit) but has zero
        /// ultrasonic energy is an upsample. Standalone this can false-positive on a
        /// genuine narrow-band 24/48, but inside a duplicate group — where a lower-rate
        /// twin of the same audio exists — it is provably fake, so the keeper uses it to
        /// avoid preferring an upsampled YouTube/re-encode copy over a real original.</summary>
        internal static bool IsFakeHires(TrackEntry? e)
        {
            var fp = e?.FingerprintV1;
            var f = e?.Features;
            if (fp == null || f == null) return false;
            bool claimsHires = fp.SampleRate > 44100 || fp.BitDepth >= 24;
            return claimsHires && f.HfEnergyRatio.HasValue && f.HfEnergyRatio.Value < 1e-6;
        }

        /// <summary>Positive when pa outranks pb as keeper.</summary>
        static int CompareKeeper(string pa, string pb, Func<string, TrackEntry?> lookup)
        {
            var ea = lookup(pa);
            var eb = lookup(pb);
            var a = ea?.FingerprintV1;
            var b = eb?.FingerprintV1;
            int LosslessRank(FingerprintV1? fp) =>
                fp != null && IsLosslessCodecForHiresCheck(fp.Codec) ? 1 : 0;
            int c = LosslessRank(a).CompareTo(LosslessRank(b));
            if (c != 0) return c;
            // Genuine beats fake hi-res: an upsampled copy (claims hi-res, no ultrasonic)
            // must not win on its inflated bitDepth/sampleRate over the real original.
            c = (IsFakeHires(ea) ? 0 : 1).CompareTo(IsFakeHires(eb) ? 0 : 1);
            if (c != 0) return c;
            c = (a?.BitDepth ?? 0).CompareTo(b?.BitDepth ?? 0);
            if (c != 0) return c;
            c = (a?.SampleRate ?? 0).CompareTo(b?.SampleRate ?? 0);
            if (c != 0) return c;
            c = (a?.Bitrate ?? 0).CompareTo(b?.Bitrate ?? 0);
            if (c != 0) return c;
            // SMFM-tagged copy wins when audio quality ties — the Sony-organized file
            // (12-TONE tagged, usually the renamed keeper) beats the old untagged copy.
            c = (ea?.Features?.HasSmfm == true ? 1 : 0).CompareTo(eb?.Features?.HasSmfm == true ? 1 : 0);
            if (c != 0) return c;
            c = (a?.FileSize ?? 0L).CompareTo(b?.FileSize ?? 0L);
            if (c != 0) return c;
            c = pb.Length.CompareTo(pa.Length);            // shorter path wins
            if (c != 0) return c;
            return string.CompareOrdinal(pb, pa);          // deterministic
        }

        /// <summary>Pure grouping core of the --duplicates report, extracted out of
        /// <see cref="RunDuplicates"/> so the keeper-uniqueness contract invariant
        /// (exactly one keeper:true per group, keeper is one of the group's own
        /// members) can be exercised directly under --self-test. Tier 1 ("exact")
        /// groups by audioStreamSha256; tier 2 ("probable") groups whatever is left
        /// over by the quantized-feature candidate key. Returns groups in
        /// deterministic order: exact before probable, same-folder before
        /// cross-folder, then first path.</summary>
        internal static (List<DupGroup> Groups, int NoHash, int NoFeatures) BuildDuplicateGroups(IDictionary<string, TrackEntry> tracks)
        {
            // ---- Tier 1: exact (audioStreamSha256) ----
            var byHash = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            int noHash = 0;
            foreach (var kv in tracks)
            {
                var sha = kv.Value?.AudioStreamSha256;
                if (string.IsNullOrEmpty(sha)) { noHash++; continue; }
                if (!byHash.TryGetValue(sha!, out var list)) { list = new List<string>(); byHash[sha!] = list; }
                list.Add(kv.Key);
            }
            var exactGroups = byHash.Where(g => g.Value.Count > 1).ToList();
            var inExact = new HashSet<string>(exactGroups.SelectMany(g => g.Value), StringComparer.OrdinalIgnoreCase);

            // ---- Tier 2: probable (quantized-feature candidate key), over entries not
            // already claimed by an exact group. No-sha entries are still eligible here. ----
            var byKey = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            int noFeatures = 0;
            foreach (var kv in tracks)
            {
                if (inExact.Contains(kv.Key)) continue;
                var key = BuildDupCandidateKey(kv.Value);
                if (key == null) { noFeatures++; continue; }
                if (!byKey.TryGetValue(key, out var list)) { list = new List<string>(); byKey[key] = list; }
                list.Add(kv.Key);
            }
            var probableGroups = byKey.Where(g => g.Value.Count > 1).ToList();

            // ---- Unified group records: keeper + scope, deterministic order ----
            string DirOf(string p) { try { return Path.GetDirectoryName(p) ?? ""; } catch { return ""; } }
            DupGroup Build(string tier, string key, List<string> paths)
            {
                var ordered = paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                bool same = ordered.Select(DirOf).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
                return new DupGroup
                {
                    Tier = tier,
                    Scope = same ? "same-folder" : "cross-folder",
                    Key = tier == "exact" ? key.Substring(0, Math.Min(16, key.Length)) : key,
                    Paths = ordered,
                    Keeper = PickKeeper(ordered, p => tracks.TryGetValue(p, out var e) ? e : null),
                };
            }
            var groups = exactGroups.Select(g => Build("exact", g.Key, g.Value))
                .Concat(probableGroups.Select(g => Build("probable", g.Key, g.Value)))
                .OrderBy(g => g.Tier == "exact" ? 0 : 1)
                .ThenBy(g => g.Scope == "same-folder" ? 0 : 1)
                .ThenBy(g => g.Paths[0], StringComparer.OrdinalIgnoreCase)
                .ToList();
            // Sequential 1-based id, assigned once here so the CSV writer and the JSON
            // writer (WriteDuplicatesJson) reference the same group numbering instead of
            // keeping two independent counters over the same ordered list.
            for (int gi = 0; gi < groups.Count; gi++) groups[gi].Id = gi + 1;

            return (groups, noHash, noFeatures);
        }

        /// <summary>Read-only duplicate-audio report, two tiers. Tier 1 ("exact") groups
        /// mbxmoods.json entries by audioStreamSha256 — content identity, same audio
        /// regardless of tags or path. Tier 2 ("probable") takes whatever's left over
        /// (not already in an exact group) and groups by the quantized-feature candidate
        /// key from <see cref="BuildDupCandidateKey"/> — catches cross-encode duplicates
        /// (e.g. FLAC vs MP3 of the same track) that don't share a byte-identical hash;
        /// these are recall-best-effort candidates for a human to confirm, not certainty.
        /// Both tiers split into "same folder" (usually accidental — e.g. tagging
        /// software renamed a file and orphaned the old copy, "01 Track" vs "1-01 Track")
        /// and "different folders" (usually intentional — one track legitimately on two
        /// albums). Each group carries a recommended keeper (<see cref="PickKeeper"/>),
        /// marked with "keep " in the console dump. No judgement is applied and nothing
        /// is modified; the console + CSV just give the info to decide. Entries with no
        /// audioStreamSha256 can't join the exact tier; entries missing mfcc/bpm/key/
        /// duration can't join the probable tier — both are counted-but-skipped.</summary>
        static int RunDuplicates(string moodsPath, IDictionary<string, TrackEntry> tracks)
        {
            Console.WriteLine("=== Duplicate Audio ===");
            Console.WriteLine($"Moods file: {moodsPath}");
            Console.WriteLine();

            var (groups, noHash, noFeatures) = BuildDuplicateGroups(tracks);

            if (groups.Count == 0)
            {
                Console.WriteLine($"No duplicate audio across {tracks.Count:N0} entries.");
                if (noHash > 0) Console.WriteLine($"  ({noHash:N0} entries had no audioStreamSha256 and couldn't join the exact tier)");
                if (noFeatures > 0) Console.WriteLine($"  ({noFeatures:N0} entries lacked mfcc/bpm/key/duration and couldn't join the probable tier)");
            }
            else
            {
                int exactCount = groups.Count(g => g.Tier == "exact");
                int probableCount = groups.Count - exactCount;
                int redundant = groups.Sum(g => g.Paths.Count - 1);
                Console.WriteLine($"  {exactCount:N0} exact groups + {probableCount:N0} probable groups, {redundant:N0} redundant copies");
                if (noHash > 0) Console.WriteLine($"  ({noHash:N0} entries had no audioStreamSha256 and couldn't join the exact tier)");
                if (noFeatures > 0) Console.WriteLine($"  ({noFeatures:N0} entries lacked mfcc/bpm/key/duration and couldn't join the probable tier)");

                const int cap = 40;
                void Dump(string header, List<DupGroup> gs)
                {
                    if (gs.Count == 0) return;
                    Console.WriteLine();
                    Console.WriteLine($"  {header} ({gs.Count}):");
                    int shown = 0;
                    foreach (var g in gs)
                    {
                        if (shown >= cap) { Console.WriteLine($"    … +{gs.Count - shown} more groups — full list in the CSV"); break; }
                        Console.WriteLine($"    [{(g.Tier == "exact" ? g.Key.Substring(0, Math.Min(12, g.Key.Length)) : "probable")}]");
                        foreach (var p in g.Paths)
                        {
                            tracks.TryGetValue(p, out var de);
                            var smfmTag = de?.Features?.HasSmfm == true ? " [smfm]" : "";
                            Console.WriteLine($"      {(string.Equals(p, g.Keeper, StringComparison.OrdinalIgnoreCase) ? "keep " : "     ")}{Linkify(p)}{smfmTag}");
                        }
                        shown++;
                    }
                }
                Dump("EXACT, same folder — byte-identical audio, usually accidental", groups.Where(g => g.Tier == "exact" && g.Scope == "same-folder").ToList());
                Dump("EXACT, different folders — byte-identical audio, usually intentional", groups.Where(g => g.Tier == "exact" && g.Scope == "cross-folder").ToList());
                Dump("PROBABLE, same folder — feature match, confirm by ear/eye", groups.Where(g => g.Tier == "probable" && g.Scope == "same-folder").ToList());
                Dump("PROBABLE, different folders — feature match, confirm by ear/eye", groups.Where(g => g.Tier == "probable" && g.Scope == "cross-folder").ToList());
            }

            // Writer tail: always runs, even with zero groups. A prior run's stale
            // mbxmoods-duplicates.{csv,json} must not linger — a consumer needs to be
            // able to tell "clean library" (freshly-written, empty groups) from
            // "report never ran" (file missing / from an old scan).
            var outDir = Path.GetDirectoryName(Path.GetFullPath(moodsPath)) ?? ".";
            var csvPath = Path.Combine(outDir, "mbxmoods-duplicates.csv");
            string Csv(string? v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
            try
            {
                var lines = new List<string> { "group,tier,scope,hash,path,artist,title,keeper" };
                foreach (var g in groups)
                {
                    foreach (var p in g.Paths)
                    {
                        tracks.TryGetValue(p, out var e);
                        bool keep = string.Equals(p, g.Keeper, StringComparison.OrdinalIgnoreCase);
                        lines.Add($"{g.Id},{g.Tier},{g.Scope},{Csv(g.Key)},{Csv(p)},{Csv(e?.Features?.Artist)},{Csv(e?.Features?.Title)},{(keep ? "true" : "")}");
                    }
                }
                File.WriteAllLines(csvPath, lines, Encoding.UTF8);
                Console.WriteLine();
                Console.WriteLine($"  Full list: {csvPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  WARNING: could not write {csvPath}: {ex.Message}");
            }

            var jsonPath = Path.Combine(outDir, "mbxmoods-duplicates.json");
            try
            {
                WriteDuplicatesJson(jsonPath, moodsPath, groups, noHash, noFeatures, tracks);
                Console.WriteLine($"  Machine report: {jsonPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  WARNING: could not write {jsonPath}: {ex.Message}");
            }

            if (_losersM3u)
            {
                var m3uPath = _losersM3uPath ?? Path.Combine(outDir, "mbxmoods-duplicate-losers.m3u8");
                try
                {
                    // Belt-and-braces: if the target file already exists and isn't a playlist,
                    // refuse to write (prevents accidental clobber of iTunes XML or other files).
                    if (File.Exists(m3uPath)
                        && !m3uPath.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)
                        && !m3uPath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"  WARNING: refusing to overwrite non-playlist file: {m3uPath}");
                    }
                    else
                    {
                        var m3u = new List<string> { "#EXTM3U" };
                        foreach (var g in groups)
                            foreach (var p in g.Paths)
                                if (!string.Equals(p, g.Keeper, StringComparison.OrdinalIgnoreCase))
                                    m3u.Add(p);
                        File.WriteAllLines(m3uPath, m3u, new UTF8Encoding(false));
                        Console.WriteLine($"  Losers playlist: {m3uPath} ({m3u.Count - 1} files) — review/delete inside MusicBee so library entries aren't orphaned");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  WARNING: could not write {m3uPath}: {ex.Message}");
                }
            }

            if (_manifest)
            {
                var manifestPath = _manifestPath ?? ResolveManifestDest(outDir);
                try
                {
                    WriteDuplicatesManifest(manifestPath, moodsPath, groups, tracks);
                    // Co-emit the interactive review page beside the manifest (same review
                    // folder) so the hub's read-only display can link straight to the
                    // "mark and build a playlist" tool. Manifest's source.reviewHtml names it.
                    var manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? outDir;
                    var siblingHtml = Path.Combine(manifestDir, "dupes.html");
                    try { WriteDuplicatesHtml(siblingHtml, moodsPath, groups, tracks); } catch { }
                    Console.WriteLine($"  Review manifest: {manifestPath} (kind:dupes; interactive page beside it: {siblingHtml})");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  WARNING: could not write {manifestPath}: {ex.Message}");
                }
            }

            // Interactive review page — a default output of --duplicates (not gated on a
            // flag). --html <path> only overrides where it lands.
            {
                var htmlPath = _htmlPath ?? Path.Combine(outDir, "mbxmoods-duplicates.html");
                try
                {
                    WriteDuplicatesHtml(htmlPath, moodsPath, groups, tracks);
                    // Clickable console link (OSC 8 file:// hyperlink — click/ctrl-click in
                    // Windows Terminal opens it in the default browser).
                    Console.WriteLine($"  Review page (click to open): {Linkify(htmlPath)}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  WARNING: could not write {htmlPath}: {ex.Message}");
                }
            }
            return 0;
        }

        /// <summary>Resolve where `--manifest` (no explicit path) writes. Anchors to the
        /// instance that OWNS the library being scanned, not "some running MusicBee":
        /// MusicBee's layout puts the moods file in &lt;root&gt;\Library\, so &lt;root&gt; is the
        /// parent of the moods dir, and MBXHub's data folder is &lt;root&gt;\AppData\MBXHub
        /// (per MBXHub.Shell PluginLocator). This is correct even with several MusicBee
        /// instances running — the manifest follows the library truedat actually scanned,
        /// not whichever process the OS happens to list first. Falls back to matching a
        /// running MusicBee by its exe folder (MusicBeeDetector's pure-Win32 lookup), then
        /// to next-to-moods. An explicit --manifest &lt;path&gt; always wins over all of this.</summary>
        internal static string ResolveManifestDest(string moodsDir)
        {
            var rd = ResolveReviewDir(moodsDir);
            if (rd != null) return Path.Combine(rd, "dupes.json");
            Console.WriteLine("  (no MusicBee/MBXHub instance found — writing manifest next to moods; pass --manifest <path> to override)");
            return Path.Combine(moodsDir, "mbxmoods-duplicates.manifest.json");
        }

        /// <summary>Resolve (and create) the MBXHub review directory for the instance that
        /// OWNS the library being scanned, not "some running MusicBee": MusicBee's layout
        /// puts the moods file in &lt;root&gt;\Library\, so &lt;root&gt; is the parent of the
        /// moods dir, and MBXHub's data folder is &lt;root&gt;\AppData\MBXHub (per MBXHub.Shell
        /// PluginLocator). This is correct even with several MusicBee instances running —
        /// the manifest follows the library truedat actually scanned, not whichever process
        /// the OS happens to list first. Falls back to matching a running MusicBee by its
        /// exe folder (MusicBeeDetector's pure-Win32 lookup). Returns null (silently — the
        /// filename-specific give-up line is the caller's concern) when no instance is
        /// found.</summary>
        internal static string? ResolveReviewDir(string moodsDir)
        {
            // 1. Library-anchored: <moodsDir>\..\AppData\MBXHub\review — the instance that owns this library.
            try
            {
                var root = Path.GetDirectoryName(Path.GetFullPath(moodsDir));
                if (!string.IsNullOrEmpty(root))
                {
                    var mbxhub = Path.Combine(root!, "AppData", "MBXHub");
                    if (Directory.Exists(mbxhub))
                    {
                        var reviewDir = Path.Combine(mbxhub, "review");
                        Directory.CreateDirectory(reviewDir);
                        Console.WriteLine($"  (targeting the scanned library's instance: {root})");
                        return reviewDir;
                    }
                }
            }
            catch { /* fall through to process match */ }

            // 2. Fallback: a running MusicBee whose own exe folder IS the scanned library's
            //    root. Match on root ONLY — never "some other running MusicBee". Writing a
            //    review surface into an instance that does not own this library (I5) silently
            //    drops the operator's work plan into a stranger's hub; when nothing owns the
            //    library, fall through to null so the caller writes next-to-moods instead.
            try
            {
                string? scannedRoot = null;
                try { scannedRoot = Path.GetDirectoryName(Path.GetFullPath(moodsDir)); } catch { }
                var instances = new List<(string root, string mbxhubDir)>();
                foreach (var proc in System.Diagnostics.Process.GetProcessesByName("MusicBee"))
                {
                    try
                    {
                        var exe = proc.MainModule?.FileName;
                        if (string.IsNullOrEmpty(exe)) continue;
                        var root = Path.GetDirectoryName(exe);
                        if (string.IsNullOrEmpty(root)) continue;
                        var mbxhub = Path.Combine(root!, "AppData", "MBXHub");
                        if (!Directory.Exists(mbxhub)) continue;
                        instances.Add((root!, mbxhub));
                    }
                    catch { /* access denied / 32-vs-64-bit mismatch — try the next process */ }
                }
                var matched = MatchReviewMbxhub(scannedRoot, instances);
                if (matched != null)
                {
                    var rd = Path.Combine(matched, "review");
                    Directory.CreateDirectory(rd);
                    Console.WriteLine($"  (matched running MusicBee to the scanned library: {Path.GetDirectoryName(matched)})");
                    return rd;
                }
            }
            catch { /* Process enumeration blocked — fall through */ }

            return null;
        }

        /// <summary>Pick the MBXHub data dir of the running instance that OWNS the scanned
        /// library — the one whose exe root equals the library root (case-insensitive).
        /// Returns null when none matches; deliberately does NOT fall back to "first instance
        /// found", because writing a review surface into an instance that does not own this
        /// library (I5) silently drops the operator's work plan into a stranger's hub. Pure,
        /// testable seam over ResolveReviewDir's process enumeration.</summary>
        internal static string? MatchReviewMbxhub(string? scannedRoot, IEnumerable<(string root, string mbxhubDir)> instances)
        {
            if (scannedRoot == null || instances == null) return null;
            foreach (var inst in instances)
                if (string.Equals(inst.root, scannedRoot, StringComparison.OrdinalIgnoreCase))
                    return inst.mbxhubDir;
            return null;
        }

        /// <summary>Emit the kind:dupes review-surface manifest that MBXHub's review.html
        /// renders directly — one class per tier×scope, one row per member, keeper flagged.
        /// truedat owns the dedup logic, so it emits the renderable manifest itself rather
        /// than depending on an offline PowerShell producer. Schema mirrors the
        /// review-surface contract (id/kind/title/generated/source/classes[columns,rows]);
        /// album/bitDepth columns light up only when those member fields are present.
        /// DISPLAY ONLY — marking a keeper here does nothing downstream.</summary>
        static void WriteDuplicatesManifest(string manifestPath, string moodsPath,
            List<DupGroup> groups, IDictionary<string, TrackEntry> tracks)
        {
            bool hasAlbum = groups.Any(g => g.Paths.Any(p => tracks.TryGetValue(p, out var e) && !string.IsNullOrEmpty(e.Features?.Album)));
            bool hasBitDepth = groups.Any(g => g.Paths.Any(p => tracks.TryGetValue(p, out var e) && (e.FingerprintV1?.BitDepth ?? 0) > 0));

            void WriteColumns(Utf8JsonWriter w)
            {
                void Col(string key, string label, string type)
                {
                    w.WriteStartObject(); w.WriteString("key", key); w.WriteString("label", label); w.WriteString("type", type); w.WriteEndObject();
                }
                w.WriteStartArray("columns");
                Col("path", "path", "path");
                Col("title", "title", "text");
                Col("artist", "artist", "text");
                if (hasAlbum) Col("album", "album", "text");
                Col("codec", "codec", "text");
                Col("bitrate", "kbps", "num");
                Col("sampleRate", "hz", "num");
                if (hasBitDepth) Col("bitDepth", "bit", "num");
                Col("durationMs", "ms", "num");
                Col("fileSize", "bytes", "num");
                Col("smfm", "smfm", "text");
                Col("fakeHires", "hi-res", "text");
                w.WriteEndArray();
            }

            var defs = new (string Key, string Tier, string Scope, string Title, string Hint)[]
            {
                ("exact-same-folder",     "exact",    "same-folder",  "Exact duplicates - same folder",        "Byte-identical audio (same audioStreamSha256), both copies in one folder. Certain - zero false positives."),
                ("exact-cross-folder",    "exact",    "cross-folder", "Exact duplicates - different folders",  "Byte-identical audio (same audioStreamSha256), copies spread across folders. Certain."),
                ("probable-same-folder",  "probable", "same-folder",  "Probable duplicates - same folder",     "Same acoustic fingerprint (mfcc/bpm/key/duration), likely different encodes (e.g. FLAC vs MP3), one folder. Candidates - confirm by ear."),
                ("probable-cross-folder", "probable", "cross-folder", "Probable duplicates - different folders","Same acoustic fingerprint, copies across folders. Candidates - confirm by ear."),
            };

            using var fs = File.Create(manifestPath);
            using var jw = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
            jw.WriteStartObject();
            jw.WriteString("id", "dupes");
            jw.WriteString("kind", "dupes");
            jw.WriteString("title", "Duplicate groups (truedat)");
            jw.WriteString("generated", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
            jw.WriteStartObject("source");
            jw.WriteString("tool", "truedat --duplicates");
            jw.WriteString("moodsFile", Path.GetFullPath(moodsPath));
            jw.WriteString("reviewHtml", "dupes.html");  // interactive "mark + build playlist" page co-emitted beside this manifest
            jw.WriteString("note", "Catalog is only as fresh as truedat's last scan. Display only - marking a keeper here does nothing downstream; the interactive page (source.reviewHtml) is where you act.");
            jw.WriteEndObject();
            jw.WriteStartArray("classes");
            foreach (var d in defs)
            {
                var gs = groups.Where(g => g.Tier == d.Tier && g.Scope == d.Scope).ToList();
                if (gs.Count == 0) continue;
                int rowCount = gs.Sum(g => g.Paths.Count);
                jw.WriteStartObject();
                jw.WriteString("key", d.Key);
                jw.WriteString("title", d.Title);
                jw.WriteString("hint", d.Hint);
                WriteColumns(jw);
                jw.WriteNumber("groupCount", gs.Count);
                jw.WriteNumber("rowCount", rowCount);
                jw.WriteStartArray("rows");
                foreach (var g in gs)
                {
                    foreach (var p in g.Paths)
                    {
                        tracks.TryGetValue(p, out var e);
                        var fp = e?.FingerprintV1;
                        jw.WriteStartObject();
                        jw.WriteNumber("group", g.Id);
                        jw.WriteBoolean("keeper", string.Equals(p, g.Keeper, StringComparison.OrdinalIgnoreCase));
                        jw.WriteString("path", p);
                        jw.WriteString("title", e?.Features?.Title ?? "");
                        jw.WriteString("artist", e?.Features?.Artist ?? "");
                        if (hasAlbum) jw.WriteString("album", e?.Features?.Album ?? "");
                        jw.WriteString("codec", fp?.Codec ?? "");
                        jw.WriteNumber("bitrate", fp?.Bitrate ?? 0);
                        jw.WriteNumber("sampleRate", fp?.SampleRate ?? 0);
                        if (hasBitDepth) jw.WriteNumber("bitDepth", fp?.BitDepth ?? 0);
                        jw.WriteNumber("durationMs", fp?.DurationMs ?? 0);
                        jw.WriteNumber("fileSize", fp?.FileSize ?? 0);
                        jw.WriteString("smfm", e?.Features?.HasSmfm == true ? "smfm" : "");
                        jw.WriteString("fakeHires", IsFakeHires(e) ? "upsampled" : "");
                        jw.WriteEndObject();
                    }
                }
                jw.WriteEndArray();
                jw.WriteEndObject();
            }
            jw.WriteEndArray();
            jw.WriteEndObject();
        }

        /// <summary>Wrap a path in an OSC 8 file:// hyperlink so Windows Terminal makes
        /// it clickable. Plain path when stdout is redirected or we're not in Windows
        /// Terminal (legacy conhost would print the escape bytes literally). When
        /// --audit tees console to truedat.log the ESC bytes land in the log too —
        /// accepted, the path text stays readable.</summary>
        static string Linkify(string path)
        {
            if (Console.IsOutputRedirected) return path;
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION"))) return path;
            try
            {
                var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri;
                return "\x1b]8;;" + uri + "\x1b\\" + path + "\x1b]8;;\x1b\\";
            }
            catch { return path; }
        }

        /// <summary>mbxmoods-duplicates.json — the machine contract consumed by the
        /// MBXHub /duplicates page (tolerant-reader pattern: consumers must skip
        /// unknown fields; member fields are omit-when-missing). Version bumps on
        /// breaking shape changes only.</summary>
        static void WriteDuplicatesJson(string jsonPath, string moodsPath, List<DupGroup> groups,
            int noHash, int noFeatures, IDictionary<string, TrackEntry> tracks)
        {
            using var fs = File.Create(jsonPath);
            using var jw = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
            jw.WriteStartObject();
            jw.WriteNumber("version", 1);
            jw.WriteString("generated", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
            jw.WriteString("moodsFile", Path.GetFullPath(moodsPath));
            jw.WriteStartObject("skipped");
            jw.WriteNumber("noHash", noHash);
            jw.WriteNumber("noFeatures", noFeatures);
            jw.WriteEndObject();
            jw.WriteStartArray("groups");
            foreach (var g in groups)
            {
                jw.WriteStartObject();
                jw.WriteNumber("id", g.Id);
                jw.WriteString("tier", g.Tier);
                jw.WriteString("scope", g.Scope);
                jw.WriteString("key", g.Key);
                jw.WriteStartArray("members");
                foreach (var p in g.Paths)
                {
                    tracks.TryGetValue(p, out var e);
                    var fp = e?.FingerprintV1;
                    jw.WriteStartObject();
                    jw.WriteString("path", p);
                    if (!string.IsNullOrEmpty(e?.Features?.Artist)) jw.WriteString("artist", e!.Features.Artist);
                    if (!string.IsNullOrEmpty(e?.Features?.Title)) jw.WriteString("title", e!.Features.Title);
                    if (!string.IsNullOrEmpty(e?.Features?.Album)) jw.WriteString("album", e!.Features.Album);
                    if (!string.IsNullOrEmpty(fp?.Codec)) jw.WriteString("codec", fp!.Codec);
                    if (fp?.Bitrate > 0) jw.WriteNumber("bitrate", fp!.Bitrate);
                    if (fp?.SampleRate > 0) jw.WriteNumber("sampleRate", fp!.SampleRate);
                    if (fp?.BitDepth > 0) jw.WriteNumber("bitDepth", fp!.BitDepth);
                    if (fp?.FileSize > 0) jw.WriteNumber("fileSize", fp!.FileSize);
                    if (fp?.DurationMs > 0) jw.WriteNumber("durationMs", fp!.DurationMs);
                    jw.WriteBoolean("smfm", e?.Features?.HasSmfm == true);
                    jw.WriteBoolean("fakeHires", IsFakeHires(e));
                    if (string.Equals(p, g.Keeper, StringComparison.OrdinalIgnoreCase)) jw.WriteBoolean("keeper", true);
                    jw.WriteEndObject();
                }
                jw.WriteEndArray();
                jw.WriteEndObject();
            }
            jw.WriteEndArray();
            jw.WriteEndObject();
        }

        /// <summary>Emit a self-contained interactive dedupe review page: embedded group
        /// data, inline CSS/JS, no external requests, opens offline in a browser. Groups
        /// start NOT included (chunk-friendly) — the operator ticks the groups to act on
        /// now, confirms a keeper per included group (recommended pre-selected; the SMFM
        /// copy is preferred), then clicks Build losers playlist to download an .m3u8 of
        /// every non-keeper in the included groups. Decisions persist in localStorage.
        /// truedat removes nothing — the playlist is reviewed/removed inside MusicBee.</summary>
        static void WriteDuplicatesHtml(string htmlPath, string moodsPath, List<DupGroup> groups, IDictionary<string, TrackEntry> tracks)
        {
            string dataJson;
            using (var ms = new MemoryStream())
            {
                using (var jw = new Utf8JsonWriter(ms))
                {
                    jw.WriteStartObject();
                    jw.WriteString("moodsFile", Path.GetFullPath(moodsPath));
                    jw.WriteString("generated", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
                    jw.WriteStartArray("groups");
                    foreach (var g in groups)
                    {
                        jw.WriteStartObject();
                        jw.WriteNumber("id", g.Id);
                        jw.WriteString("tier", g.Tier);
                        jw.WriteString("scope", g.Scope);
                        jw.WriteString("keeper", g.Keeper);
                        jw.WriteStartArray("members");
                        foreach (var p in g.Paths)
                        {
                            tracks.TryGetValue(p, out var e);
                            var fp = e?.FingerprintV1;
                            jw.WriteStartObject();
                            jw.WriteString("path", p);
                            jw.WriteString("title", e?.Features?.Title ?? "");
                            jw.WriteString("artist", e?.Features?.Artist ?? "");
                            jw.WriteString("album", e?.Features?.Album ?? "");
                            jw.WriteString("codec", fp?.Codec ?? "");
                            jw.WriteNumber("bitrate", fp?.Bitrate ?? 0);
                            jw.WriteNumber("sampleRate", fp?.SampleRate ?? 0);
                            jw.WriteNumber("bitDepth", fp?.BitDepth ?? 0);
                            jw.WriteNumber("durationMs", fp?.DurationMs ?? 0);
                            jw.WriteNumber("fileSize", fp?.FileSize ?? 0);
                            jw.WriteBoolean("smfm", e?.Features?.HasSmfm == true);
                            jw.WriteBoolean("fakeHires", IsFakeHires(e));
                            jw.WriteEndObject();
                        }
                        jw.WriteEndArray();
                        jw.WriteEndObject();
                    }
                    jw.WriteEndArray();
                    jw.WriteEndObject();
                }
                dataJson = Encoding.UTF8.GetString(ms.ToArray());
            }
            File.WriteAllText(htmlPath, DuplicatesHtmlTemplate.Replace("__DATA__", dataJson), new UTF8Encoding(false));
        }

        // Self-contained review page. All HTML attributes + JS strings use single quotes /
        // backticks so this C# verbatim literal needs almost no doubled quotes (only esc()).
        // __DATA__ is replaced with the embedded groups JSON at write time.
        const string DuplicatesHtmlTemplate = @"<!doctype html>
<html lang='en'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>truedat — duplicate review</title>
<style>
  :root{color-scheme:dark light}
  body{font-family:system-ui,'Segoe UI',sans-serif;margin:0;background:#141414;color:#e8e8e8}
  header{position:sticky;top:0;z-index:5;background:#1c1c1c;border-bottom:1px solid #333;padding:10px 16px;display:flex;gap:14px;align-items:center;flex-wrap:wrap}
  header h1{font-size:15px;margin:0;font-weight:600}
  .counts{font-size:13px;color:#9cf}
  .pivot{display:flex;border:1px solid #3a3a3a;border-radius:7px;overflow:hidden}
  .pivot .ptog{background:#242424;color:#bbb;border:0;border-radius:0;padding:6px 15px;font-size:13px}
  .pivot .ptog.on{background:#2e7d46;color:#fff}
  .atitle{color:#e8e8e8;font-weight:600}
  button{background:#2e7d46;color:#fff;border:0;padding:8px 14px;border-radius:6px;cursor:pointer;font-size:14px}
  button:hover{background:#37984f}
  button.sec{background:#3a3a3a}
  button.sec:hover{background:#4a4a4a}
  .tools{margin-left:auto;display:flex;gap:10px;align-items:center;font-size:13px;color:#bbb}
  main{padding:16px;max-width:1200px;margin:0 auto}
  .grp{border:1px solid #333;border-radius:8px;margin-bottom:12px;background:#191919;overflow:hidden}
  .grp.inc{border-color:#2e7d46}
  .ghead{padding:8px 12px;display:flex;gap:10px;align-items:center;font-size:12px;color:#aaa;border-bottom:1px solid #262626}
  .ghead label{display:flex;gap:6px;align-items:center;color:#ddd;font-size:13px;cursor:pointer}
  .badge{background:#333;border-radius:4px;padding:2px 7px}
  .badge.exact{background:#22503a}
  .badge.prob{background:#5a3a1e}
  .hassmfm{color:#7d7;margin-left:auto;font-size:12px}
  table{width:100%;border-collapse:collapse;font-size:13px}
  th,td{padding:6px 10px;text-align:left;border-top:1px solid #232323;vertical-align:top}
  th{color:#888;font-weight:500;font-size:11px;text-transform:uppercase;letter-spacing:.04em}
  td.num,th.num{text-align:right}
  td.num{color:#bbb}
  tr.keep td{background:#17281b}
  .smfm{color:#7d7;font-weight:600}
  .path{word-break:break-all;color:#dcdcdc}
  .grp:not(.inc) tbody{opacity:.5}
  .grp.cluster>.ghead{background:#1e2230}
  .folders{padding:8px 12px;display:flex;gap:22px;flex-wrap:wrap;border-bottom:1px solid #262626;align-items:center}
  .fchoice{display:flex;gap:6px;align-items:center;font-size:13px;color:#ddd;cursor:pointer}
  .fchoice b{color:#9cf;font-weight:600;word-break:break-all}
  .savings{color:#7c9;font-size:12px}
  .fnote{color:#c99;font-size:11px;background:#3a2e2e;border-radius:4px;padding:1px 6px}
  .fpair{display:flex;gap:8px;align-items:center;flex-wrap:wrap}
  .flink{color:#9cf;text-decoration:none;word-break:break-all}
  .flink:hover{text-decoration:underline}
  .player{height:32px;max-width:280px;vertical-align:middle}
  .play{background:#333;color:#7d7;border:0;border-radius:4px;padding:1px 7px;font-size:12px;cursor:pointer;line-height:1.4}
  .play:hover{background:#3a5}
  .play.on{background:#2e7d46;color:#fff}
  td.fake{color:#f77;font-weight:600}
  .pairwrap{overflow-x:auto}
  table.pair td.sep,table.pair th:empty{width:12px;background:#111;border-top:0}
  table.pair .fhdr{color:#9cf;text-align:center}
  table.pair td.kc{text-align:center}
  table.pair td.muted{color:#666;text-align:center}
  table.pair tr:has(td.kc input:checked) td{background:#17281b}
  .expbtn{margin-left:auto;font-size:12px;padding:4px 8px}
  .exp{border-top:1px solid #262626}
  .hint{color:#888;font-size:12px;margin:0 0 14px;line-height:1.5}
</style>
</head>
<body>
<header>
  <h1>Duplicate review</h1>
  <div class='pivot'><button class='ptog on' id='mAlbum'>Album</button><button class='ptog' id='mTrack'>Track</button></div>
  <span class='counts' id='counts'></span>
  <button id='accept'>Accept all recommended</button>
  <button id='build'>Build losers playlist</button>
  <audio id='player' class='player' controls preload='none'></audio>
  <div class='tools'>
    <button class='sec' id='expand'>expand all</button>
    <button class='sec' id='collapse'>collapse all</button>
    <button class='sec' id='incall'>include all shown</button>
    <button class='sec' id='clearall'>clear</button>
    <label><input type='checkbox' id='onlysmfm'> only groups with an SMFM copy</label>
  </div>
</header>
<main>
  <p class='hint'><b>Album</b> mode rolls every duplicated track of an album into one card — pick the copy to keep once (biggest space wins on top); <b>Track</b> mode lists the scattered one-off dupes. Byte-identical (exact) dupes start <b>included</b> at the recommended keeper; cross-encode (probable) matches start off — they need eyes. <b>Accept all recommended</b> clears the easy pile in one click. Then <b>Build losers playlist</b> for an .m3u8 of the non-keepers — open it in MusicBee to remove. Nothing here touches your files.</p>
  <div id='list'></div>
</main>
<script id='data' type='application/json'>__DATA__</script>
<script>
const D=JSON.parse(document.getElementById('data').textContent);
const KEY='truedat-dupes:'+(D.moodsFile||'');
const hadSaved=localStorage.getItem(KEY)!=null;
let st={inc:{},keep:{},folderKeep:{}};
let expAll=false;
try{const s=JSON.parse(localStorage.getItem(KEY)||'{}');if(s.inc)st.inc=s.inc;if(s.keep)st.keep=s.keep;if(s.folderKeep)st.folderKeep=s.folderKeep;}catch(e){}
function save(){try{localStorage.setItem(KEY,JSON.stringify(st));}catch(e){}}
// First visit (no saved state): pre-include the byte-identical exact dupes at
// their recommended keeper so the easy pile is a review-and-accept. Probable
// (cross-encode) groups stay off — they need eyes.
if(!hadSaved){D.groups.forEach(g=>{if(g.tier==='exact')st.inc[g.id]=true;});save();}
let mode=(st.mode==='track')?'track':'album';
function esc(s){return(s==null?'':''+s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/""/g,'&quot;').replace(/'/g,'&#39;');}
function bytes(n){if(!n)return '';const u=['B','KB','MB','GB'];let i=0,x=n;while(x>=1024&&i<3){x/=1024;i++;}return x.toFixed(i?1:0)+' '+u[i];}
function dur(ms){if(!ms)return '';const s=Math.round(ms/1000);return Math.floor(s/60)+':'+String(s%60).padStart(2,'0');}
function folderOf(p){const i=Math.max(p.lastIndexOf('\\'),p.lastIndexOf('/'));return i<0?p:p.slice(0,i);}
function folderUrl(p){return 'file:///'+encodeURI(p.replace(/\\/g,'/'));}
function fileOf(p){const i=Math.max(p.lastIndexOf('\\'),p.lastIndexOf('/'));return i<0?p:p.slice(i+1);}
// Case-insensitive, separator-normalized folder identity, mirroring the C#
// PathComparer. Windows paths are case-insensitive, so D:\Music\x and
// D:\music\x are the SAME folder — comparing raw strings invents phantom
// folders and produces bogus keep-this-vs-that choices. Identity only; keep the
// original-case folderOf() string for display.
function folderKey(p){return folderOf(p).replace(/\//g,'\\').replace(/\\+$/,'').toLowerCase();}
function keeperOf(g){return st.keep[g.id]||g.keeper||g.members[0].path;}
function visible(){const only=document.getElementById('onlysmfm').checked;return D.groups.filter(g=>!only||g.members.some(m=>m.smfm));}
// A group is a folder-pair candidate when its members span exactly two folders.
function pairKey(g){const fs=[...new Set(g.members.map(m=>folderKey(m.path)))].sort();return fs.length===2?fs.join('|'):null;}
// The folder (by identity key) holding the recommended keeper for the most
// tracks — the sensible default source to keep. Generalizes to N folders.
function defWinner(groups,keys){
  const cnt={};keys.forEach(k=>cnt[k]=0);
  groups.forEach(g=>{const k=folderKey(g.keeper||g.members[0].path);if(k in cnt)cnt[k]++;});
  let best=keys[0];keys.forEach(k=>{if(cnt[k]>cnt[best])best=k;});
  return best;
}
// Space reclaimed if we act on a group / album: sum of the non-keeper members.
function groupFrees(g){const kp=keeperOf(g);let s=0;g.members.forEach(m=>{if(m.path!==kp)s+=m.fileSize||0;});return s;}
function clusterFrees(groups){let s=0;groups.forEach(g=>s+=groupFrees(g));return s;}
// Album rollup key: artist+album when the group's members agree on the album
// tag (the normal single-album case); falls back to the folder-pair key so
// untagged dupes still cluster by their two folders. Compilations (per-track
// artists) won't roll up — they land as loose track cards, which is fine.
function albumKey(g){
  const alb=(g.members[0].album||'').trim();
  if(alb && g.members.every(m=>(m.album||'').trim().toLowerCase()===alb.toLowerCase())){
    const art=(g.members[0].artist||'').trim().toLowerCase();
    return 'alb:'+art+'/'+alb.toLowerCase();
  }
  return pairKey(g);
}
let CLUSTERS={};
function memberRow(g,m){
  const k=m.path===keeperOf(g);
  return `<tr class='${k?'keep':''}'>`
    +`<td><label><input type='radio' name='k${g.id}' ${k?'checked':''} data-g='${g.id}' data-p='${esc(m.path)}'> keep</label></td>`
    +`<td class='path'><button class='play' data-u='${esc(folderUrl(m.path))}' title='play'>&#9654;</button> <a class='flink' href='${esc(folderUrl(folderOf(m.path)))}' title='open containing folder'>${esc(m.path)}</a></td>`
    +`<td>${esc(m.title)}</td><td>${esc(m.artist)}</td><td>${esc(m.album)}</td>`
    +`<td>${esc(m.codec)}</td>`
    +`<td class='num'>${m.bitrate||''}</td><td class='num'>${m.sampleRate||''}</td><td class='num ${m.fakeHires?'fake':''}' title='${m.fakeHires?'upsampled: claims hi-res but no ultrasonic energy':''}'>${m.bitDepth||''}${m.fakeHires?' fake':''}</td>`
    +`<td class='num'>${bytes(m.fileSize)}</td><td class='num'>${dur(m.durationMs)}</td>`
    +`<td class='smfm'>${m.smfm?'smfm':''}</td></tr>`;
}
function tableFor(groups){
  const rows=groups.map(g=>g.members.map(m=>memberRow(g,m)).join('')).join('');
  return `<table><thead><tr><th></th><th>path</th><th>title</th><th>artist</th><th>album</th><th>codec</th><th class='num'>kbps</th><th class='num'>hz</th><th class='num'>bit</th><th class='num'>size</th><th class='num'>len</th><th>smfm</th></tr></thead><tbody>${rows}</tbody></table>`;
}
// Side-by-side A vs B for a folder-pair cluster: one row per duplicate track, the
// folder-A copy on the left and the folder-B copy on the right, so you compare
// them row for row. The keep radios (left=keep A, right=keep B) override the
// folder-level choice per track.
function pairSide(m){
  if(!m) return `<td class='muted' colspan='7'>&mdash;</td>`;
  return `<td class='path'><button class='play' data-u='${esc(folderUrl(m.path))}' title='play'>&#9654;</button> <a class='flink' href='${esc(folderUrl(folderOf(m.path)))}' title='${esc(m.path)}'>${esc(fileOf(m.path))}</a></td>`
    +`<td>${esc(m.codec)}</td><td class='num'>${m.bitrate||''}</td><td class='num ${m.fakeHires?'fake':''}' title='${m.fakeHires?'upsampled: claims hi-res but no ultrasonic energy':''}'>${m.bitDepth||''}${m.fakeHires?' fake':''}</td><td class='num'>${bytes(m.fileSize)}</td><td class='num'>${dur(m.durationMs)}</td><td class='smfm'>${m.smfm?'smfm':''}</td>`;
}
function pairTable(groups,kA,kB,disp){
  const sub=`<th>file</th><th>codec</th><th class='num'>kbps</th><th class='num'>bit</th><th class='num'>size</th><th class='num'>len</th><th>smfm</th>`;
  const rows=groups.map(g=>{
    const A=g.members.find(m=>folderKey(m.path)===kA),B=g.members.find(m=>folderKey(m.path)===kB);
    const kp=keeperOf(g);
    const rA=A?`<input type='radio' name='kg${g.id}' data-g='${g.id}' data-p='${esc(A.path)}' ${A.path===kp?'checked':''}>`:'';
    const rB=B?`<input type='radio' name='kg${g.id}' data-g='${g.id}' data-p='${esc(B.path)}' ${B.path===kp?'checked':''}>`:'';
    return `<tr><td class='kc'>${rA}</td>${pairSide(A)}<td class='sep'></td>${pairSide(B)}<td class='kc'>${rB}</td></tr>`;
  }).join('');
  return `<div class='pairwrap'><table class='pair'><thead>`
    +`<tr><th></th><th colspan='7' class='fhdr'>${esc(fileOf(disp[kA]))}</th><th></th><th colspan='7' class='fhdr'>${esc(fileOf(disp[kB]))}</th><th></th></tr>`
    +`<tr><th>keep</th>${sub}<th></th>${sub}<th>keep</th></tr></thead><tbody>${rows}</tbody></table></div>`;
}
function groupCard(g){
  const included=!!st.inc[g.id];const hasSmfm=g.members.some(m=>m.smfm);
  const div=document.createElement('div');div.className='grp'+(included?' inc':'');
  div.innerHTML=`<div class='ghead'>`
    +`<label><input type='checkbox' class='inc' data-g='${g.id}' ${included?'checked':''}> include</label>`
    +`<span class='badge ${g.tier==='exact'?'exact':'prob'}'>${esc(g.tier)}</span>`
    +`<span class='badge'>${esc(g.scope)}</span><span>group ${g.id}</span>`
    +(hasSmfm?`<span class='hassmfm'>has SMFM</span>`:'')+`</div>`+tableFor([g]);
  return div;
}
// Album-level keep-source radios (one per distinct folder). Picking one sets the
// keeper to that folder's copy for every track that has one; tracks missing from
// it keep their per-track choice. data-f carries the folder IDENTITY key; the
// label shows the original-case path from disp.
function folderChoice(key,eid,fkeys,disp,chosen){
  const opts=fkeys.map(fk=>
    `<span class='fpair'><label class='fchoice'><input type='radio' name='f${eid}' class='ckeep' data-key='${esc(key)}' data-f='${esc(fk)}' ${chosen===fk?'checked':''}> keep</label> <a class='flink' href='${esc(folderUrl(disp[fk]))}' title='open folder'>${esc(disp[fk])}</a></span>`
  ).join('');
  return `<div class='folders'>keep:${opts}</div>`;
}
// A rollup card for a set of duplicate groups that belong together — either an
// album (keyed on artist+album) or a folder-pair fallback. Folders are identified
// case-insensitively. 2 folders => side-by-side pairTable; 3+ => keep-source list
// over a flat table; 1 (same-folder dupes) => flat per-track keep only.
function albumCard(key,groups){
  const isAlbum=key.slice(0,4)==='alb:';
  const m0=groups[0].members[0];
  // distinct folders by identity, each with a representative original-case path
  const disp={};
  groups.forEach(g=>g.members.forEach(m=>{const fk=folderKey(m.path);if(!(fk in disp))disp[fk]=folderOf(m.path);}));
  const fkeys=Object.keys(disp).sort();
  const included=groups.every(g=>st.inc[g.id]);
  const lose=clusterFrees(groups);
  const hasSmfm=groups.some(g=>g.members.some(m=>m.smfm));
  const eid='exp'+key.replace(/[^a-z0-9]/gi,'');
  const div=document.createElement('div');div.className='grp cluster'+(included?' inc':'');
  const label=isAlbum?'include album':'include set';
  const badge=isAlbum
    ?`<span class='badge exact'>album</span><span class='atitle'>${esc(m0.artist?m0.artist+' — ':'')}${esc(m0.album||'(unknown album)')}</span>`
    :`<span class='badge exact'>folder duplicate</span>`;
  const fnote=fkeys.length>2?`<span class='fnote'>${fkeys.length} folders</span>`:(fkeys.length===1?`<span class='fnote'>same folder</span>`:'');
  let head=`<div class='ghead'>`
    +`<label><input type='checkbox' class='cinc' data-key='${esc(key)}' ${included?'checked':''}> ${label}</label>`
    +badge+`<span>${groups.length} tracks</span>${fnote}`
    +`<span class='savings'>frees ${bytes(lose)}</span>`
    +(hasSmfm?`<span class='hassmfm'>has SMFM</span>`:'')
    +`<button class='sec expbtn' data-exp='${eid}'>${expAll?'hide tracks':'show tracks'}</button></div>`;
  const exp=inner=>`<div class='exp' id='${eid}' style='display:${expAll?'':'none'}'>${inner}</div>`;
  let body;
  if(fkeys.length===2){
    const chosen=st.folderKeep[key]||defWinner(groups,fkeys);
    body=folderChoice(key,eid,fkeys,disp,chosen)+exp(pairTable(groups,fkeys[0],fkeys[1],disp));
  }else if(fkeys.length>=3){
    const chosen=st.folderKeep[key]||defWinner(groups,fkeys);
    body=folderChoice(key,eid,fkeys,disp,chosen)+exp(tableFor(groups));
  }else{
    body=exp(tableFor(groups));  // 1 folder: same-folder dupes, per-track keep only
  }
  div.innerHTML=head+body;
  return div;
}
function render(){
  const list=document.getElementById('list');list.innerHTML='';
  const vis=visible();
  CLUSTERS={};
  let inc=0,losers=0,nClusters=0;
  const tally=g=>{if(st.inc[g.id]){inc++;const kp=keeperOf(g);losers+=g.members.filter(m=>m.path!==kp).length;}};
  if(mode==='track'){
    vis.slice().sort((a,b)=>groupFrees(b)-groupFrees(a)).forEach(g=>{list.appendChild(groupCard(g));tally(g);});
  }else{
    const map=new Map();
    vis.forEach(g=>{const k=albumKey(g);if(k){if(!map.has(k))map.set(k,[]);map.get(k).push(g);}});
    const inClu=new Set();const clist=[];
    map.forEach((groups,k)=>{if(groups.length>=2){CLUSTERS[k]=groups;clist.push([k,groups]);groups.forEach(g=>inClu.add(g.id));}});
    clist.sort((a,b)=>clusterFrees(b[1])-clusterFrees(a[1]));  // biggest space wins on top
    nClusters=clist.length;
    clist.forEach(([k,groups])=>{list.appendChild(albumCard(k,groups));groups.forEach(tally);});
    vis.filter(g=>!inClu.has(g.id)).sort((a,b)=>groupFrees(b)-groupFrees(a)).forEach(g=>{list.appendChild(groupCard(g));tally(g);});
  }
  const mid=mode==='track'?'':' · '+nClusters+' albums';
  document.getElementById('counts').textContent=vis.length+' groups'+mid+' · '+inc+' included · '+losers+' losers queued';
}
document.addEventListener('change',e=>{
  if(e.target.matches('input.inc')){if(e.target.checked)st.inc[e.target.dataset.g]=true;else delete st.inc[e.target.dataset.g];save();render();}
  else if(e.target.matches('input.cinc')){(CLUSTERS[e.target.dataset.key]||[]).forEach(g=>{if(e.target.checked)st.inc[g.id]=true;else delete st.inc[g.id];});save();render();}
  else if(e.target.matches('input.ckeep')){const key=e.target.dataset.key,f=e.target.dataset.f;st.folderKeep[key]=f;(CLUSTERS[key]||[]).forEach(g=>{const m=g.members.find(x=>folderKey(x.path)===f);if(m)st.keep[g.id]=m.path;});save();render();}
  else if(e.target.matches('input[type=radio][data-g]')){st.keep[e.target.dataset.g]=e.target.dataset.p;save();render();}
  else if(e.target.id==='onlysmfm'){render();}
});
document.addEventListener('click',e=>{
  if(e.target.matches('[data-exp]')){const el=document.getElementById(e.target.dataset.exp);if(el){const show=el.style.display==='none';el.style.display=show?'':'none';e.target.textContent=show?'hide tracks':'show tracks';}}
  else if(e.target.matches('.play')){const p=document.getElementById('player');p.src=e.target.dataset.u;p.play().catch(()=>{});document.querySelectorAll('.play.on').forEach(b=>b.classList.remove('on'));e.target.classList.add('on');}
});
document.getElementById('accept').addEventListener('click',()=>{D.groups.forEach(g=>{st.inc[g.id]=true;delete st.keep[g.id];});st.folderKeep={};save();render();});
document.getElementById('expand').addEventListener('click',()=>{expAll=true;render();});
document.getElementById('collapse').addEventListener('click',()=>{expAll=false;render();});
document.getElementById('incall').addEventListener('click',()=>{visible().forEach(g=>st.inc[g.id]=true);save();render();});
document.getElementById('clearall').addEventListener('click',()=>{st.inc={};save();render();});
function setMode(m){mode=m;st.mode=m;document.getElementById('mAlbum').classList.toggle('on',m==='album');document.getElementById('mTrack').classList.toggle('on',m==='track');save();render();}
document.getElementById('mAlbum').addEventListener('click',()=>setMode('album'));
document.getElementById('mTrack').addEventListener('click',()=>setMode('track'));
document.getElementById('build').addEventListener('click',()=>{
  const lines=['#EXTM3U'];
  D.groups.forEach(g=>{if(!st.inc[g.id])return;const c=keeperOf(g);g.members.forEach(m=>{if(m.path!==c)lines.push(m.path);});});
  if(lines.length===1){alert('No groups included yet — tick include on the folder sets / groups you want to remove.');return;}
  const blob=new Blob([lines.join('\r\n')+'\r\n'],{type:'audio/x-mpegurl'});
  const a=document.createElement('a');a.href=URL.createObjectURL(blob);a.download='mbxmoods-duplicate-losers.m3u8';
  document.body.appendChild(a);a.click();a.remove();
});
setMode(mode);  // sync the pivot toggle UI + initial render
</script>
</body>
</html>";

        /// <summary>One duplicate group in the --duplicates report. Tier: "exact"
        /// (audioStreamSha256) or "probable" (quantized-feature candidate key).</summary>
        internal sealed class DupGroup
        {
            public int Id;
            public string Tier = "exact";
            public string Scope = "";
            public string Key = "";
            public List<string> Paths = new List<string>();
            public string Keeper = "";
        }

        /// <summary>
        /// Result bundle returned by RunSourceWorkers. Named fields so callers don't
        /// have to remember positional unpacking. Tags is nullable — null means the
        /// caller passed extractTags=false (MoodsMode has iTunes XML metadata
        /// already; it skips TagLib re-read to save one parse per track).
        /// </summary>
        internal sealed class WorkerResults
        {
            public TrackFeatures? Features;
            public string? EssentiaError;
            public string? FileMd5;
            public FingerprintV1? FingerprintV1;
            public string? AudioStreamSha256;
            public string AudioStreamSha256Source = "";
            public FileTags? Tags;
            public BitUsageSummary? BitUsage;
            public double? HfEnergyRatio;
            public string? HfEnergyMethod;
            public HfSpectralStructure? HfSpectralStructure;
            public SmfmReader.SmfmResult? Smfm;
            public TimeSpan AnalyzeDuration;
            public long AnalyzeTicks;
        }

        /// <summary>Skip-or-run helper for the optional-signal pattern.
        /// `skip=true` returns a completed task with the disabled value (no thread
        /// pool spin). `skip=false` runs <paramref name="work"/> on the pool.</summary>
        internal static Task<T> ConditionalTask<T>(bool skip, Func<T> work, T disabledValue)
            => skip ? Task.FromResult(disabledValue) : Task.Run(work);

        /// <summary>
        /// Run the per-track concurrent worker fan-out: Essentia + ComputeFileMd5 +
        /// ComputeFingerprintV1 + ComputeAudioStreamSha256FromFile + ComputeBitUsage
        /// + ComputeHfAnalysis + SMFM (+ optional ExtractFileTags). Wall-clock
        /// ≈ max(analysis, slowest-task). All workers read from <c>src.Path</c>
        /// (the staged copy if staging happened, the original otherwise).
        ///
        /// <paramref name="auditDisplayPath"/> is what appears in the [AUDIT] file=
        /// label — typically the original source path so audit lines stay readable
        /// regardless of staging.
        /// <paramref name="knownDurationSec"/> short-circuits the bitUsage probe's
        /// TagLib re-parse when the caller already has duration (e.g. MoodsMode
        /// pulls it from the iTunes XML). 0 means "no known duration; probe inline".
        /// <paramref name="extractTags"/>: MoodsMode passes false (XML supplies
        /// metadata); --file-list / --analyze-file pass true.
        /// </summary>
        internal static WorkerResults RunSourceWorkers(
            string essentiaExe,
            SourceHandle src,
            long fileSize,
            string auditDisplayPath,
            double knownDurationSec,
            bool extractTags,
            StageOptions stageOpts,
            CancellationToken ct)
        {
            string readPath = src.Path;
            string auditName = Path.GetFileName(auditDisplayPath);
            var analyzeStart = Stopwatch.GetTimestamp();

            var essentiaTask = Task.Run(() => AnalyzeWithEssentia(essentiaExe, readPath, fileSize, ct));
            var fileMd5Task = Task.Run(() => _fileMd5Enabled ? ComputeFileMd5(readPath) : null);
            var fingerprintTask = Task.Run(() =>
            {
                var swFp = Stopwatch.StartNew();
                var fp = ComputeFingerprintV1(readPath, fileSize, out _);
                swFp.Stop();
                if (_audit)
                    Console.Error.WriteLine($"[AUDIT] taglibParseMs={swFp.ElapsedMilliseconds} file=\"{auditName}\"");
                return fp;
            });
            var audioStreamSha256Task = Task.Run(() =>
            {
                var swSha = Stopwatch.StartNew();
                var r = ComputeAudioStreamSha256FromFile(readPath, fileSize, out _);
                swSha.Stop();
                if (_audit)
                    Console.Error.WriteLine($"[AUDIT] audioStreamSha256Ms={swSha.ElapsedMilliseconds} file=\"{auditName}\"");
                return r;
            });
            var tagsTask = extractTags
                ? Task.Run(() => (FileTags?)ExtractFileTags(readPath))
                : Task.FromResult<FileTags?>(null);
            var bitUsageTask = ConditionalTask<BitUsageSummary?>(stageOpts.NoBitUsage, () =>
            {
                double dur = knownDurationSec;
                if (dur <= 0)
                {
                    try { using var tf = TagLib.File.Create(readPath); dur = tf.Properties?.Duration.TotalSeconds ?? 0; } catch { }
                }
                return ComputeBitUsage(readPath, dur, _ffmpegPath.Value);
            }, null);
            var hfEnergyTask = ConditionalTask(stageOpts.NoHfAnalysis,
                () => ComputeHfAnalysis(readPath, _ffmpegPath.Value),
                ((double?)null, (string?)null, (HfSpectralStructure?)null));
            var smfmTask = Task.Run(() => SmfmReader.TryRead(readPath));

            Task.WaitAll(new Task[] { essentiaTask, fileMd5Task, fingerprintTask, audioStreamSha256Task, tagsTask, bitUsageTask, hfEnergyTask, smfmTask });

            long ticks = Stopwatch.GetTimestamp() - analyzeStart;
            var (features, error) = essentiaTask.Result;
            var (sha, shaSource) = audioStreamSha256Task.Result;
            var (hfRatio, hfMethod, hfStructure) = hfEnergyTask.Result;
            return new WorkerResults
            {
                Features = features,
                EssentiaError = error,
                FileMd5 = fileMd5Task.Result,
                FingerprintV1 = fingerprintTask.Result,
                AudioStreamSha256 = sha,
                AudioStreamSha256Source = shaSource,
                Tags = tagsTask.Result,
                BitUsage = bitUsageTask.Result,
                HfEnergyRatio = hfRatio,
                HfEnergyMethod = hfMethod,
                HfSpectralStructure = hfStructure,
                Smfm = smfmTask.Result,
                AnalyzeDuration = StopwatchTicksToTimeSpan(ticks),
                AnalyzeTicks = ticks,
            };
        }

        static string? FindFfmpeg()
        {
            // Check app dir, working dir
            var found = FindTool("ffmpeg.exe", AppDomain.CurrentDomain.BaseDirectory, Environment.CurrentDirectory);
            if (found != null) return found;

            // Check PATH
            try
            {
                var psi = new ProcessStartInfo("where", "ffmpeg.exe")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi)!;
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                if (proc.ExitCode == 0)
                {
                    var path = output.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                }
            }
            catch { }

            return null;
        }

        static string? FindFfprobe()
        {
            var found = FindTool("ffprobe.exe", AppDomain.CurrentDomain.BaseDirectory, Environment.CurrentDirectory);
            if (found != null) return found;

            try
            {
                var psi = new ProcessStartInfo("where", "ffprobe.exe")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi)!;
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                if (proc.ExitCode == 0)
                {
                    var path = output.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                }
            }
            catch { }

            return null;
        }

        /// <summary>Phase 2.5 — decode 30s mid-track to s32le mono via ffmpeg, walk the
        /// samples once, build a trailing-zeros histogram, derive the bit-usage summary.
        /// Returns null on ffmpeg absence / decode failure / silent file — never throws.
        /// Designed to run concurrently with Essentia (single ffmpeg child process,
        /// ~5.5 MB total decoded payload, typically completes in 1–3s warm).</summary>
        static BitUsageSummary? ComputeBitUsage(string filePath, double durationSec, string? ffmpegExe)
        {
            if (string.IsNullOrEmpty(ffmpegExe)) return null;

            // Applicability peek — TagLib-only, ~5ms. Skip lossy / sub-24-bit files
            // BEFORE spending 30s on an ffmpeg decode whose output would be meaningless
            // (lossy codecs flood the LSBs with reconstruction noise; sub-24-bit claims
            // can't tell us anything about hi-res authenticity). Fail-safe: if TagLib
            // can't be opened or the codec/bitDepth can't be derived, fall through to
            // the analysis rather than silently dropping a potentially-applicable file.
            try
            {
                using var peek = TagLib.File.Create(filePath);
                var (peekCodec, _) = NormalizeCodec(peek);
                int peekBitDepth = peek.Properties?.BitsPerSample ?? 0;
                bool applicable = IsLosslessCodecForHiresCheck(peekCodec) && peekBitDepth >= 24;
                if (!applicable) return null;
            }
            catch
            {
                // TagLib failed — proceed with the analysis. Better to do unnecessary
                // work occasionally than to drop measurements on hard-to-parse files.
            }

            // Sample 25% into the track when it's long enough — skips intros / fades.
            // For short tracks fall back to decoding from the start.
            double startSec = durationSec > 60 ? durationSec * 0.25 : 0;
            string ssArg = startSec > 0 ? $"-ss {startSec.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} " : "";
            // NOTE: No -ar argument. The whole point of bit-usage measurement is to inspect
            // the SOURCE's bit depth as it lands in the s32le output. Resampling
            // inherently introduces sub-bit interpolation noise that fills all 32 bits,
            // making "lowestNonZeroBit" reflect resample artifacts instead of source
            // resolution. Letting ffmpeg keep the native sample rate preserves the
            // bit-alignment property (int24 source -> int32 with lowest-active-bit at 8;
            // int16 padded-to-int24 source -> int32 with lowest-active-bit at 16).
            // -ac 1 (mono) is safe for clean stereo sources because (L+R)/2 over int32
            // doesn't add bits; only resampling does.
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegExe!,
                Arguments = $"-v error {ssArg}-t 30 -i {PathHelper.QuoteArg(filePath)} -f s32le -ac 1 pipe:1",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            try
            {
                using var proc = Process.Start(psi);
                if (proc == null) return null;
                // Drain stderr async so a chatty ffmpeg can't deadlock the pipe.
                var stderrTask = proc.StandardError.ReadToEndAsync();

                // 33 buckets: 0..31 for trailing-zero counts of nonzero samples; index 32 for fully-zero samples.
                var tzHistogram = new long[33];
                double signalSqSum = 0;   // double to avoid int-overflow accumulation
                long count = 0;
                long countNonZero = 0;

                var buf = new byte[64 * 1024];  // 16 K samples per read
                var stream = proc.StandardOutput.BaseStream;
                int residue = 0;
                while (true)
                {
                    int got = stream.Read(buf, residue, buf.Length - residue);
                    if (got <= 0) break;
                    int total = residue + got;
                    int wholeSamples = total / 4;
                    for (int i = 0; i < wholeSamples; i++)
                    {
                        int s = BitConverter.ToInt32(buf, i * 4);
                        count++;
                        signalSqSum += (double)s * s;
                        if (s == 0) { tzHistogram[32]++; continue; }
                        countNonZero++;
                        // Math.Abs(int.MinValue) overflows — clamp.
                        uint abs = s == int.MinValue ? (uint)int.MaxValue : (uint)Math.Abs(s);
                        tzHistogram[TrailingZeroCountUInt32(abs)]++;
                    }
                    int used = wholeSamples * 4;
                    residue = total - used;
                    if (residue > 0) Buffer.BlockCopy(buf, used, buf, 0, residue);
                }

                if (!proc.WaitForExit(60000))
                {
                    try { proc.Kill(); } catch { }
                    return null;
                }
                if (proc.ExitCode != 0 && count == 0) return null;  // tolerate non-zero exit if we got data

                if (count == 0 || countNonZero == 0) return null;   // pure silence — can't infer bit depth

                // Lowest non-zero bit across the 30s window.
                int lowestNonZeroBit = 0;
                for (int i = 0; i < 32; i++)
                {
                    if (tzHistogram[i] > 0) { lowestNonZeroBit = i; break; }
                }

                // Activity at the resolution boundary — fraction of non-silent samples
                // whose lowest set bit is at the boundary position.
                double bottomBitActivity = (double)tzHistogram[lowestNonZeroBit] / countNonZero;

                // EffectiveBits = log2(rms / quantStep). Approximate; useful continuous signal
                // for downstream confidence scoring.
                double rms = Math.Sqrt(signalSqSum / count);
                double effectiveBits;
                if (rms <= 0)
                {
                    effectiveBits = 0;
                }
                else
                {
                    double quantStep = Math.Pow(2, lowestNonZeroBit);
                    effectiveBits = quantStep > 0 ? Math.Log(rms / quantStep, 2) : 0;
                    if (effectiveBits < 0) effectiveBits = 0;
                    if (effectiveBits > 32) effectiveBits = 32;
                }

                return new BitUsageSummary
                {
                    LowestNonZeroBit = lowestNonZeroBit,
                    BottomBitActivity = Math.Round(bottomBitActivity, 4),
                    EffectiveBits = Math.Round(effectiveBits, 2),
                    SamplesAnalyzed = (int)Math.Min(count, int.MaxValue),
                    // Method tag bumped from "ffmpeg-s32le-30s-mid" because the prior
                    // method forced -ar 48000 which contaminated the bit-depth measurement
                    // with resample interpolation noise (saw lowestNonZeroBit=0 and
                    // effectiveBits>24 on verified hi-res sources — impossible for true
                    // 24-bit content). Native sample rate restores measurement integrity.
                    Method = "ffmpeg-s32le-30s-mid-native",
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Phase 3 — fraction of audio energy above 22.05 kHz, measured at
        /// the source's native sample rate. Returns (null, null) when not applicable:
        /// ffmpeg absent, sample rate at or below 44.1 kHz (no Nyquist headroom), or
        /// total RMS is zero (silent file). The ratio is bounded [0, ~1]; genuine hi-res
        /// music typically lands at 0.001–0.05; upsampled-from-44.1 content lands at 0
        /// or essentially zero. Pairs with bitUsage as a second independent fake-hi-res
        /// signal that dither evasion can't fabricate.
        /// Self-contained: opens TagLib internally for duration + sample rate so call
        /// sites are one line, same pattern as ComputeBitUsage.</summary>
        /// <summary>Phase 5 — single-pass FFT analysis of a 30 s mid-track segment.
        /// Returns the Phase-3 HF energy ratio (now bin-sharp Parseval over FFT bins,
        /// not IIR-highpass RMS) AND the Phase-5 spectral-structure scalars
        /// (flatness / peak-to-mean / imaging symmetry). One ffmpeg subprocess
        /// replaces the two Phase-3 highpass/total invocations — net −1 subprocess
        /// per track. Same applicability gate (sourceSampleRate &gt; 44100; ffmpeg
        /// required); all return values are null on gate-miss or analysis failure.
        /// Window: 4096 samples Hann, 50 % overlap, mean aggregation across windows.</summary>
        static (double? HfEnergyRatio, string? HfEnergyMethod, HfSpectralStructure? Structure) ComputeHfAnalysis(string filePath, string? ffmpegExe)
        {
            if (string.IsNullOrEmpty(ffmpegExe)) return (null, null, null);

            int sourceSampleRate = 0;
            double durationSec = 0;
            try
            {
                using var tf = TagLib.File.Create(filePath);
                sourceSampleRate = tf.Properties?.AudioSampleRate ?? 0;
                durationSec = tf.Properties?.Duration.TotalSeconds ?? 0;
            }
            catch { return (null, null, null); }

            if (sourceSampleRate <= 44100) return (null, null, null);   // no Nyquist headroom above 22 kHz

            double startSec = durationSec > 60 ? durationSec * 0.25 : 0;
            string ssArg = startSec > 0 ? $"-ss {startSec.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} " : "";
            string args = $"-v error {ssArg}-t 30 -i {PathHelper.QuoteArg(filePath)} -ac 1 -ar {sourceSampleRate} -f s32le pipe:1";

            const int FftSize = 4096;
            const int Hop = FftSize / 2; // 50% overlap
            int halfBins = FftSize / 2;
            // Bin spacing in Hz: sampleRate / FftSize. HF band starts at the first
            // bin whose centre is >= 22050 Hz. Mirror axis sits at the original
            // CD-rate Nyquist (22050 Hz); only bins in [hfStart, mirrorEnd] have
            // a partner in the source band for the imaging-symmetry test.
            double binHz = (double)sourceSampleRate / FftSize;
            int hfStart = (int)Math.Ceiling(22050.0 / binHz);
            int origNyqBin = (int)Math.Round(22050.0 / binHz); // mirror axis
            int mirrorEnd = Math.Min(2 * origNyqBin, halfBins); // upper end of bins with a source-band partner
            if (hfStart >= halfBins) return (null, null, null);  // shouldn't happen given the >44.1k gate

            var hann = Fft.Hann(FftSize);
            var real = new double[FftSize];
            var imag = new double[FftSize];
            var mag2 = new double[FftSize];
            // Rolling input buffer holds the most recent FftSize samples in [0, fill).
            // After each window we memmove the back Hop samples to the front and refill
            // — one shift per window, not per sample.
            var rolling = new double[FftSize];
            int rollingFill = 0;
            const double SampleScale = 1.0 / 2147483648.0; // s32 -> [-1, 1)

            // Per-window metric accumulators (means across windows aggregated at end).
            double sumFlatness = 0;
            double sumPeakToMean = 0;
            double sumImaging = 0;
            int windowsValid = 0;          // windows that produced finite flatness/peakToMean
            int windowsValidImaging = 0;   // windows that produced a finite Pearson r
            // Energy ratio aggregator: energy-weighted (sum HF / sum total across all windows,
            // matches Parseval intent). Bin 0 (DC) excluded from total denominator per spec.
            double sumHfEnergy = 0;
            double sumTotalEnergy = 0;

            void ProcessWindow()
            {
                // Apply Hann + copy to FFT buffers.
                for (int i = 0; i < FftSize; i++)
                {
                    real[i] = rolling[i] * hann[i];
                    imag[i] = 0;
                }
                Fft.Forward(real, imag);
                Fft.Magnitude2(real, imag, mag2);

                // HF band statistics.
                double hfMax = 0, hfMean = 0;
                int hfCount = halfBins - hfStart;
                if (hfCount <= 0) return;
                double sumHf = 0;
                for (int k = hfStart; k < halfBins; k++)
                {
                    double v = mag2[k];
                    sumHf += v;
                    if (v > hfMax) hfMax = v;
                }
                hfMean = sumHf / hfCount;

                // Total energy (exclude DC bin 0 per spec); HF subset within total.
                double sumTotal = 0;
                for (int k = 1; k < halfBins; k++) sumTotal += mag2[k];
                if (sumTotal > 0)
                {
                    sumTotalEnergy += sumTotal;
                    sumHfEnergy += sumHf;
                }

                if (hfMean <= 0) return;  // silent window — skip structural metrics

                // Flatness — Wiener entropy: geomean(mag2) / arithmean(mag2).
                // Log-floor at 1e-12 * window-max prevents log(0) on near-empty bins.
                double floor = 1e-12 * hfMax;
                if (floor <= 0) return;
                double logSum = 0;
                for (int k = hfStart; k < halfBins; k++)
                {
                    double v = mag2[k];
                    if (v < floor) v = floor;
                    logSum += Math.Log(v);
                }
                double geomean = Math.Exp(logSum / hfCount);
                double flatness = geomean / hfMean;
                if (double.IsNaN(flatness) || double.IsInfinity(flatness)) return;
                if (flatness < 0) flatness = 0;
                if (flatness > 1) flatness = 1;

                double peakToMean = hfMax / hfMean;
                if (double.IsNaN(peakToMean) || double.IsInfinity(peakToMean)) return;

                sumFlatness += flatness;
                sumPeakToMean += peakToMean;
                windowsValid++;

                // Imaging symmetry — Pearson r between mag2[i] and mag2[mirror(i)] over
                // bins in [hfStart, mirrorEnd). Single-pass formula; skip windows where
                // either band lacks variance (all bins equal → undefined r).
                // usedN tracks pairs ACTUALLY accumulated (some `i` get skipped when the
                // partner falls outside the source band); using the loop iteration count
                // in the Pearson denominator produces a biased r.
                if (mirrorEnd - hfStart >= 4)
                {
                    double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
                    int usedN = 0;
                    for (int i = hfStart; i < mirrorEnd; i++)
                    {
                        int mirror = 2 * origNyqBin - i;
                        if (mirror < 1 || mirror >= hfStart) continue; // partner must be in the source band, not in HF itself
                        double x = mag2[i];
                        double y = mag2[mirror];
                        sx += x; sy += y;
                        sxx += x * x; syy += y * y;
                        sxy += x * y;
                        usedN++;
                    }
                    if (usedN < 4) return;
                    double denom2 = (usedN * sxx - sx * sx) * (usedN * syy - sy * sy);
                    if (denom2 > 0)
                    {
                        double r = (usedN * sxy - sx * sy) / Math.Sqrt(denom2);
                        if (!double.IsNaN(r) && !double.IsInfinity(r))
                        {
                            if (r < -1) r = -1;
                            if (r > 1) r = 1;
                            sumImaging += r;
                            windowsValidImaging++;
                        }
                    }
                }
            }

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegExe!,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            try
            {
                using var proc = Process.Start(psi);
                if (proc == null) return (null, null, null);
                var stderrTask = proc.StandardError.ReadToEndAsync();

                var buf = new byte[64 * 1024];
                var stream = proc.StandardOutput.BaseStream;
                int byteResidue = 0;
                while (true)
                {
                    int got = stream.Read(buf, byteResidue, buf.Length - byteResidue);
                    if (got <= 0) break;
                    int total = byteResidue + got;
                    int wholeSamples = total / 4;
                    int srcIdx = 0;
                    while (srcIdx < wholeSamples)
                    {
                        int room = FftSize - rollingFill;
                        int take = Math.Min(room, wholeSamples - srcIdx);
                        for (int i = 0; i < take; i++)
                        {
                            int s = BitConverter.ToInt32(buf, (srcIdx + i) * 4);
                            rolling[rollingFill + i] = s * SampleScale;
                        }
                        rollingFill += take;
                        srcIdx += take;
                        if (rollingFill == FftSize)
                        {
                            ProcessWindow();
                            // Shift back half to front for 50% overlap: next window starts
                            // FftSize-Hop samples back. One memmove per window.
                            Buffer.BlockCopy(rolling, Hop * sizeof(double), rolling, 0, (FftSize - Hop) * sizeof(double));
                            rollingFill = FftSize - Hop;
                        }
                    }
                    int used = wholeSamples * 4;
                    byteResidue = total - used;
                    if (byteResidue > 0) Buffer.BlockCopy(buf, used, buf, 0, byteResidue);
                }
                if (!proc.WaitForExit(60000))
                {
                    try { proc.Kill(); } catch { }
                    return (null, null, null);
                }
                stderrTask.Wait(5000);
            }
            catch
            {
                return (null, null, null);
            }

            const string method = "managed-fft-radix2-30s-mid-native";
            if (windowsValid == 0)
            {
                // Got audio bytes but no windows produced finite metrics (silent / all-DC).
                // Treat as analysis-success-but-no-signal: return zero ratio, no structure.
                double? ratio = sumTotalEnergy > 0 ? (double?)Math.Round(sumHfEnergy / sumTotalEnergy, 6) : null;
                return (ratio, ratio.HasValue ? method : null, null);
            }

            double hfRatio = sumTotalEnergy > 0 ? sumHfEnergy / sumTotalEnergy : 0;
            if (hfRatio < 0) hfRatio = 0;
            if (hfRatio > 2) hfRatio = 2;

            var structure = new HfSpectralStructure
            {
                Flatness = Math.Round(sumFlatness / windowsValid, 4),
                PeakToMean = Math.Round(sumPeakToMean / windowsValid, 2),
                ImagingSymmetry = windowsValidImaging > 0
                    ? Math.Round(sumImaging / windowsValidImaging, 4)
                    : 0.0,
                Method = method,
            };
            if (_audit)
            {
                Console.Error.WriteLine($"[AUDIT] hfAnalysis file=\"{Path.GetFileName(filePath)}\" sr={sourceSampleRate} hfStart={hfStart} bins=[{hfStart}..{halfBins}) hfRatioRaw={hfRatio:E4} winValid={windowsValid} winValidSym={windowsValidImaging} sumHF={sumHfEnergy:E4} sumTot={sumTotalEnergy:E4}");
            }
            return (Math.Round(hfRatio, 6), method, structure);
        }

        /// <summary>Inline FFT sanity checks. Run via --self-test; exits 0 on
        /// success, 1 on first failure. No external test project — kept here
        /// so the merged single-file exe is fully self-contained.</summary>
        static int RunSelfTest()
        {
            int failures = 0;
            void Assert(bool cond, string msg)
            {
                if (cond) { Console.WriteLine($"  PASS  {msg}"); return; }
                Console.WriteLine($"  FAIL  {msg}");
                failures++;
            }

            Console.WriteLine("Truedat FFT self-test");

            // Hann cache: same array reference on repeat calls at the same size.
            var h1 = Fft.Hann(4096);
            var h2 = Fft.Hann(4096);
            Assert(ReferenceEquals(h1, h2), "Hann(4096) cached by size");
            Assert(Math.Abs(h1[0]) < 1e-12 && Math.Abs(h1[4095]) < 1e-12, "Hann tapers to 0 at both ends");

            // 1 kHz sine at 44.1 kHz over 4096 samples → energy in bin ~93.
            const int N = 4096;
            const int Sr = 44100;
            double binHz = (double)Sr / N;
            int expectedBin1k = (int)Math.Round(1000.0 / binHz);
            var real = new double[N];
            var imag = new double[N];
            for (int i = 0; i < N; i++)
                real[i] = Math.Sin(2 * Math.PI * 1000.0 * i / Sr);
            // Time-domain energy first, for Parseval below.
            double timeEnergy = 0;
            for (int i = 0; i < N; i++) timeEnergy += real[i] * real[i];
            Fft.Forward(real, imag);
            var mag2 = new double[N];
            Fft.Magnitude2(real, imag, mag2);
            int peakBin = 0;
            double peakMag = 0;
            for (int k = 1; k < N / 2; k++)
            {
                if (mag2[k] > peakMag) { peakMag = mag2[k]; peakBin = k; }
            }
            Assert(Math.Abs(peakBin - expectedBin1k) <= 1, $"1 kHz sine peaks at bin {peakBin} (expected {expectedBin1k})");

            // Parseval: Σ|X[k]|² == N · Σ|x[n]|² for our unnormalized FFT.
            double specEnergy = 0;
            for (int k = 0; k < N; k++) specEnergy += mag2[k];
            double parsevalLhs = specEnergy;
            double parsevalRhs = N * timeEnergy;
            double parsevalErr = Math.Abs(parsevalLhs - parsevalRhs) / parsevalRhs;
            Assert(parsevalErr < 1e-9, $"Parseval holds (relative error {parsevalErr:E2})");

            // Two-tone: 1 kHz + 10 kHz → both bins populated, rest near zero.
            int expectedBin10k = (int)Math.Round(10000.0 / binHz);
            for (int i = 0; i < N; i++)
            {
                real[i] = Math.Sin(2 * Math.PI * 1000.0 * i / Sr) +
                          Math.Sin(2 * Math.PI * 10000.0 * i / Sr);
                imag[i] = 0;
            }
            Fft.Forward(real, imag);
            Fft.Magnitude2(real, imag, mag2);
            double e1k = mag2[expectedBin1k];
            double e10k = mag2[expectedBin10k];
            double offBand = 0;
            // Off-band reference: pick a bin midway between the two tones.
            int midBin = (expectedBin1k + expectedBin10k) / 2;
            offBand = mag2[midBin];
            Assert(e1k > 100 * offBand, $"two-tone: 1 kHz bin energy ({e1k:E2}) >> off-band ({offBand:E2})");
            Assert(e10k > 100 * offBand, $"two-tone: 10 kHz bin energy ({e10k:E2}) >> off-band ({offBand:E2})");

            // Wrong-size input throws.
            bool threw = false;
            try { Fft.Forward(new double[100], new double[100]); }
            catch (ArgumentException) { threw = true; }
            Assert(threw, "non-power-of-2 size throws ArgumentException");

            // -- SourceStager: pass-through / staging / failure ----------------
            Console.WriteLine("SourceStager");

            // Pass-through: --no-stage disables everything.
            var optsDisabled = new StageOptions { NoStage = true };
            using (var h = OpenStagedSource(@"\\server\share\test.mp3", optsDisabled))
            {
                Assert(h.Method == "direct", $"--no-stage returns direct (got {h.Method})");
                Assert(h.Path == @"\\server\share\test.mp3", "--no-stage preserves source path");
            }

            // IsAsciiPrintable — extension sanitization invariant.
            Assert(IsAsciiPrintable(".mp3"), "ASCII ext passes");
            Assert(IsAsciiPrintable(".flac"), "ASCII ext (multi-char) passes");
            Assert(!IsAsciiPrintable(".tëst"), "non-ASCII ext rejected");
            Assert(!IsAsciiPrintable(".½"), "vulgar-fraction ext rejected");

            // Pass-through: local ASCII path is left alone.
            var stStageDir = Path.Combine(Path.GetTempPath(), $".truedat-stage-test-{Guid.NewGuid():N}");
            var optsEnabled = new StageOptions { StageDir = stStageDir };
            string localAscii = Path.Combine(Path.GetTempPath(), $"truedat-test-{Guid.NewGuid():N}.bin");
            File.WriteAllBytes(localAscii, new byte[] { 1, 2, 3, 4 });
            try
            {
                using (var h = OpenStagedSource(localAscii, optsEnabled))
                {
                    Assert(h.Method == "direct", $"local-ASCII returns direct (got {h.Method})");
                    Assert(h.Path == localAscii, "local-ASCII preserves source path");
                }
            }
            finally { try { File.Delete(localAscii); } catch { } }

            // Happy path: non-ASCII local source is staged, file is copied,
            // staged path differs, and Dispose cleans up.
            string nonAsciiSrc = Path.Combine(Path.GetTempPath(), $"truedat-tëst-{Guid.NewGuid():N}.bin");
            File.WriteAllBytes(nonAsciiSrc, new byte[] { 9, 8, 7, 6, 5 });
            string? stagedPathSeen = null;
            try
            {
                using (var h = OpenStagedSource(nonAsciiSrc, optsEnabled))
                {
                    Assert(h.Method == "staged-fallback", $"non-ASCII local stages with fallback method (got {h.Method})");
                    Assert(h.Path != nonAsciiSrc, "staged Path differs from source");
                    Assert(File.Exists(h.Path), "staged file exists during use");
                    Assert(new FileInfo(h.Path).Length == 5, "staged file has the source's bytes");
                    Assert(h.StageBytes == 5, $"StageBytes == file size (got {h.StageBytes})");
                    Assert(h.SourceLastWriteUtc != DateTime.MinValue, "SourceLastWriteUtc captured after copy");
                    // The non-ASCII source has a `.bin` ext already so the sanitize
                    // pass is a no-op here. Use a non-ASCII ext to exercise the .bin
                    // fallback explicitly.
                    string stagedExt = Path.GetExtension(h.Path);
                    Assert(IsAsciiPrintable(stagedExt), $"staged ext is ASCII printable (got {stagedExt})");
                    stagedPathSeen = h.Path;
                }
                Assert(stagedPathSeen != null && !File.Exists(stagedPathSeen), "staged file is deleted after Dispose");
            }
            finally
            {
                try { File.Delete(nonAsciiSrc); } catch { }
                try { if (Directory.Exists(stStageDir)) Directory.Delete(stStageDir, recursive: true); } catch { }
            }

            // Non-ASCII extension: source's `.tëst` ext is replaced by `.bin` on the staged path.
            var stStageDirExt = Path.Combine(Path.GetTempPath(), $".truedat-stage-test-{Guid.NewGuid():N}");
            var optsExt = new StageOptions { StageDir = stStageDirExt };
            string nonAsciiExtSrc = Path.Combine(Path.GetTempPath(), $"truedat-test-{Guid.NewGuid():N}.tëst");
            File.WriteAllBytes(nonAsciiExtSrc, new byte[] { 1, 2 });
            try
            {
                using (var h = OpenStagedSource(nonAsciiExtSrc, optsExt))
                {
                    Assert(h.Method == "staged-fallback", $"non-ASCII ext stages (got {h.Method})");
                    Assert(Path.GetExtension(h.Path) == ".bin", $"non-ASCII ext sanitised to .bin (got {Path.GetExtension(h.Path)})");
                }
            }
            finally
            {
                try { File.Delete(nonAsciiExtSrc); } catch { }
                try { if (Directory.Exists(stStageDirExt)) Directory.Delete(stStageDirExt, recursive: true); } catch { }
            }

            // Direct-passthrough still captures mtime once at entry.
            string mtimeProbe = Path.Combine(Path.GetTempPath(), $"truedat-mtime-{Guid.NewGuid():N}.bin");
            File.WriteAllBytes(mtimeProbe, new byte[] { 7 });
            try
            {
                using (var h = OpenStagedSource(mtimeProbe, optsEnabled))
                {
                    Assert(h.Method == "direct", "local-ASCII still direct");
                    Assert(h.SourceLastWriteUtc != DateTime.MinValue, "direct-passthrough SourceLastWriteUtc captured");
                }
            }
            finally { try { File.Delete(mtimeProbe); } catch { } }

            // Failure mode: unwritable staging dir -> direct fallback, no throw.
            // Trigger by pointing StageDir at a path under a non-existent volume.
            var optsBadDir = new StageOptions { StageDir = @"Z:\nonexistent-volume-stage-test\.truedat-stage" };
            using (var h = OpenStagedSource(@"\\server\share\test.mp3", optsBadDir))
            {
                Assert(h.Method == "direct", $"stage failure falls back to direct (got {h.Method})");
                Assert(h.Path == @"\\server\share\test.mp3", "stage failure preserves source path");
            }

            Console.WriteLine();
            Console.WriteLine("Duplicate candidate-key self-test");
            TrackEntry MakeDup(double[]? mfcc, double bpm, string key, string mode, int durMs) => new TrackEntry
            {
                Features = new TrackFeatures { Bpm = bpm, Key = key, Mode = mode, Mfcc = mfcc },
                FingerprintV1 = durMs > 0 ? new FingerprintV1 { DurationMs = durMs } : (FingerprintV1?)null
            };
            var dkBase = new double[] { -700, 100, 20, 8, 4, 2, 1, 0.5, 0.2, 0.1, 0.05, 0.02, 0.01 };
            var dkJit = (double[])dkBase.Clone();
            dkJit[0] += 5; dkJit[3] += 1.5;   // well inside the 24 / 8 buckets
            var dkA = MakeDup(dkBase, 120.2, "C", "major", 215000);
            var dkB = MakeDup(dkJit, 119.8, "C", "major", 214500);
            Assert(BuildDupCandidateKey(dkA) != null, "candidate key: eligible entry produces a key");
            Assert(BuildDupCandidateKey(dkA) == BuildDupCandidateKey(dkB), "candidate key: in-bucket mfcc jitter + bpm/duration rounding -> same key");
            Assert(BuildDupCandidateKey(dkA) != BuildDupCandidateKey(MakeDup(dkBase, 120, "D", "major", 215000)), "candidate key: different musical key -> different group");
            Assert(BuildDupCandidateKey(MakeDup(null, 120, "C", "major", 215000)) == null, "candidate key: missing mfcc -> ineligible (null)");
            Assert(BuildDupCandidateKey(MakeDup(dkBase, 120, "C", "major", 0)) == null, "candidate key: missing durationMs -> ineligible (null)");

            Console.WriteLine();
            Console.WriteLine("Duplicate keeper self-test");
            TrackEntry MakeKp(string codec, int bitDepth, int sampleRate, int bitrate, long fileSize) => new TrackEntry
            {
                Features = new TrackFeatures(),
                FingerprintV1 = new FingerprintV1 { Codec = codec, BitDepth = bitDepth, SampleRate = sampleRate, Bitrate = bitrate, FileSize = fileSize }
            };
            var kpMap = new Dictionary<string, TrackEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["C:\\m\\a.flac"] = MakeKp("flac", 16, 44100, 900, 30_000_000),
                ["C:\\m\\a.mp3"]  = MakeKp("mp3", 0, 44100, 320, 9_000_000),
                ["C:\\m\\hi.flac"] = MakeKp("flac", 24, 96000, 2800, 90_000_000),
                ["C:\\m\\deep\\sub\\a.flac"] = MakeKp("flac", 16, 44100, 900, 30_000_000),
            };
            Func<string, TrackEntry?> kpLookup = p => kpMap.TryGetValue(p, out var v) ? v : null;
            Assert(PickKeeper(new[] { "C:\\m\\a.mp3", "C:\\m\\a.flac" }, kpLookup) == "C:\\m\\a.flac", "keeper: lossless beats lossy");
            Assert(PickKeeper(new[] { "C:\\m\\a.flac", "C:\\m\\hi.flac" }, kpLookup) == "C:\\m\\hi.flac", "keeper: higher bitDepth wins among lossless");
            Assert(PickKeeper(new[] { "C:\\m\\deep\\sub\\a.flac", "C:\\m\\a.flac" }, kpLookup) == "C:\\m\\a.flac", "keeper: equal quality -> shortest path wins");

            Console.WriteLine();
            Console.WriteLine("Duplicate grouping self-test");
            {
                // Cross-repo contract: mbxmoods-duplicates.json consumers can't express
                // "exactly one keeper:true per group" on their side — this invariant is
                // guarded here, against BuildDuplicateGroups (the pure core extracted
                // out of RunDuplicates).
                TrackEntry MakeGrp(string? sha, double[]? mfcc, double bpm, string key, string mode, int durMs) => new TrackEntry
                {
                    Features = new TrackFeatures { Bpm = bpm, Key = key, Mode = mode, Mfcc = mfcc },
                    FingerprintV1 = durMs > 0 ? new FingerprintV1 { DurationMs = durMs } : (FingerprintV1?)null,
                    AudioStreamSha256 = sha,
                };
                var gBase = new double[] { -700, 100, 20, 8, 4, 2, 1, 0.5, 0.2, 0.1, 0.05, 0.02, 0.01 };
                const string shaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

                var grpTracks = new Dictionary<string, TrackEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    // Exact group: two same-sha entries in the same folder.
                    ["C:\\m\\exact\\a1.flac"] = MakeGrp(shaA, gBase, 120, "C", "major", 215000),
                    ["C:\\m\\exact\\a2.mp3"] = MakeGrp(shaA, gBase, 120, "C", "major", 215000),
                    // Same features as the exact pair but no sha -> must NOT join the exact group.
                    ["C:\\m\\exact\\a3-nosha.mp3"] = MakeGrp(null, gBase, 120, "C", "major", 215000),
                    // Probable group: same candidate key, no sha, different folders.
                    ["C:\\m\\probA\\p1.flac"] = MakeGrp(null, gBase, 130, "D", "minor", 200000),
                    ["C:\\m\\probB\\p2.mp3"] = MakeGrp(null, gBase, 130, "D", "minor", 200000),
                    // Unrelated singleton -> no group.
                    ["C:\\m\\lonely\\only.mp3"] = MakeGrp("shaunique000000000000000000000000000000000000000000000000000", gBase, 140, "E", "major", 190000),
                };
                var (grpGroups, grpNoHash, grpNoFeatures) = BuildDuplicateGroups(grpTracks);

                Assert(grpGroups.Count == 2, $"grouping: exact 2-member group + probable 2-member group only (got {grpGroups.Count})");

                var exactG = grpGroups.FirstOrDefault(g => g.Tier == "exact");
                Assert(exactG != null && exactG.Paths.Count == 2, "grouping: exact group has exactly the 2 same-sha members");
                Assert(exactG != null && !exactG.Paths.Contains("C:\\m\\exact\\a3-nosha.mp3", StringComparer.OrdinalIgnoreCase),
                    "grouping: same-features-no-sha entry is NOT pulled into the exact group");

                var probG = grpGroups.FirstOrDefault(g => g.Tier == "probable");
                Assert(probG != null && probG.Paths.Count == 2, "grouping: probable group has exactly the 2 matching-key members");
                Assert(probG != null && !probG.Paths.Contains("C:\\m\\exact\\a3-nosha.mp3", StringComparer.OrdinalIgnoreCase),
                    "grouping: entry already claimed by an exact group is NOT also in a probable group (path-disjointness)");

                // THE contract invariant: every group has exactly one keeper, contained in its Paths.
                foreach (var g in grpGroups)
                {
                    int keeperCount = g.Paths.Count(p => string.Equals(p, g.Keeper, StringComparison.OrdinalIgnoreCase));
                    Assert(keeperCount == 1, $"grouping: group '{g.Key}' has exactly one keeper (got {keeperCount})");
                    Assert(g.Paths.Contains(g.Keeper, StringComparer.OrdinalIgnoreCase), $"grouping: group '{g.Key}' keeper is one of its own Paths");
                }

                // Every group has >= 2 members.
                Assert(grpGroups.All(g => g.Paths.Count >= 2), "grouping: every returned group has at least 2 members");

                // Ordering: exact groups precede probable groups.
                int firstProbableIdx = grpGroups.FindIndex(g => g.Tier == "probable");
                int lastExactIdx = grpGroups.FindLastIndex(g => g.Tier == "exact");
                Assert(firstProbableIdx == -1 || lastExactIdx < firstProbableIdx, "grouping: exact groups ordered before probable groups");

                // Scope classification.
                Assert(exactG != null && exactG.Scope == "same-folder", $"grouping: exact group in same dir -> scope 'same-folder' (got {exactG?.Scope})");
                Assert(probG != null && probG.Scope == "cross-folder", $"grouping: probable group across dirs -> scope 'cross-folder' (got {probG?.Scope})");

                Assert(grpNoHash == 3, $"grouping: noHash counts entries without audioStreamSha256 (got {grpNoHash})");
                Assert(grpNoFeatures == 0, $"grouping: all synthetic entries have valid candidate-key features (got {grpNoFeatures})");

                // Sequential id: assigned once in BuildDuplicateGroups, shared by the CSV
                // and JSON writers instead of two independent counters over the same list.
                Assert(grpGroups.Select(g => g.Id).SequenceEqual(Enumerable.Range(1, grpGroups.Count)),
                    $"grouping: ids are sequential 1-based in output order (got [{string.Join(",", grpGroups.Select(g => g.Id))}])");

                // Zero-group / empty-input contract: an empty catalog must still produce a
                // well-formed (empty) grouping result, not throw — this is what lets
                // RunDuplicates write an empty-but-valid mbxmoods-duplicates.json/.csv
                // instead of leaving a stale prior report on disk.
                var emptyTracks = new Dictionary<string, TrackEntry>(StringComparer.OrdinalIgnoreCase);
                var (emptyGroups, emptyNoHash, emptyNoFeatures) = BuildDuplicateGroups(emptyTracks);
                Assert(emptyGroups.Count == 0, $"grouping: empty input -> zero groups (got {emptyGroups.Count})");
                Assert(emptyNoHash == 0 && emptyNoFeatures == 0, "grouping: empty input -> zero skip counts");
            }

            // --- audioStreamSha256 known vector -------------------------------------
            // FIPS-180 test vector: SHA-256("abc"). Guards the hash implementation —
            // any swap of the underlying provider must keep producing standard SHA-256.
            {
                var tmp = Path.Combine(Path.GetTempPath(), $".truedat-selftest-{Guid.NewGuid():N}.bin");
                try
                {
                    File.WriteAllBytes(tmp, new byte[] { (byte)'a', (byte)'b', (byte)'c' });
                    var vec = ComputeAudioStreamSha256(tmp, 0, 3, out var shaErr);
                    Assert(shaErr == null && vec == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                        $"SHA-256 known vector (got {vec ?? "null"})");
                    // Region subsetting: hash of [1,3) == SHA-256("bc") (verified vector).
                    var sub = ComputeAudioStreamSha256(tmp, 1, 3, out _);
                    Assert(sub == "1e0bbd6c686ba050b8eb03ffeedc64fdc9d80947fce821abbe5d6dc8d252c5ac",
                        $"SHA-256 region subset [1,3) == SHA-256(\"bc\") (got {sub ?? "null"})");
                }
                finally { try { File.Delete(tmp); } catch { } }
            }

            // --- single-pass MD5+SHA equals the two-pass helpers ---------------------
            {
                var tmp = Path.Combine(Path.GetTempPath(), $".truedat-selftest-{Guid.NewGuid():N}.bin");
                try
                {
                    // Deterministic pseudo-random 200 000 bytes; invariant region [1000, 150000).
                    var data = new byte[200000];
                    uint seed = 0x2fa1d05u;
                    for (int i = 0; i < data.Length; i++) { seed = seed * 1664525u + 1013904223u; data[i] = (byte)(seed >> 24); }
                    File.WriteAllBytes(tmp, data);
                    long invS = 1000, invE = 150000;

                    var expectMd5 = ComputeFileMd5(tmp);
                    var expectSha = ComputeAudioStreamSha256(tmp, invS, invE, out _);
                    var (gotMd5, gotSha) = ComputeFileMd5AndAudioShaCore(tmp, data.Length, invS, invE, out var cErr);
                    Assert(cErr == null && gotMd5 == expectMd5, "single-pass fileMd5 matches ComputeFileMd5");
                    Assert(gotSha == expectSha, "single-pass audioSha matches ComputeAudioStreamSha256");

                    // Degenerate region (invEnd <= invStart) → sha null, md5 still produced.
                    var (dMd5, dSha) = ComputeFileMd5AndAudioShaCore(tmp, data.Length, 5000, 5000, out _);
                    Assert(dMd5 == expectMd5 && dSha == null, "single-pass degenerate region: md5 ok, sha null");

                    // Region at file tail (invEnd == fileSize).
                    var expTail = ComputeAudioStreamSha256(tmp, 0, data.Length, out _);
                    var (_, tailSha) = ComputeFileMd5AndAudioShaCore(tmp, data.Length, 0, data.Length, out _);
                    Assert(tailSha == expTail, "single-pass full-range sha matches");
                }
                finally { try { File.Delete(tmp); } catch { } }
            }

            // --- single-pass wrapper: TagLib-unparseable file degrades to md5-only ---
            {
                var tmp = Path.Combine(Path.GetTempPath(), $".truedat-selftest-{Guid.NewGuid():N}.xyz");
                try
                {
                    var junk = new byte[4096];
                    for (int i = 0; i < junk.Length; i++) junk[i] = (byte)(i * 31 + 7);
                    File.WriteAllBytes(tmp, junk);
                    var expectMd5 = ComputeFileMd5(tmp);
                    var (wMd5, wSha, wSrc) = ComputeFileMd5AndAudioSha(tmp, junk.Length, out _);
                    Assert(wMd5 == expectMd5 && wMd5 != null, "wrapper on TagLib-unparseable file still yields fileMd5");
                    Assert(wSha == null && wSrc == "", "wrapper on TagLib-unparseable file yields null sha, empty source");
                }
                finally { try { File.Delete(tmp); } catch { } }
            }

            // --- FLAC frame-offset walker (flac-frames hash anchor) ---
            {
                // Synthetic chain: "fLaC" + STREAMINFO (34 bytes, not last) +
                // PADDING (10 bytes, last) → frames start at 4+4+34+4+10 = 56.
                byte[] Flac(bool id3, bool footer = false)
                {
                    using var ms = new MemoryStream();
                    if (id3)
                    {
                        // ID3v2 header: "ID3", v2.4, flags (footer bit optional), size=20 (syncsafe)
                        ms.Write(new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, (byte)(footer ? 0x10 : 0), 0, 0, 0, 20 }, 0, 10);
                        ms.Write(new byte[20 + (footer ? 10 : 0)], 0, 20 + (footer ? 10 : 0));
                    }
                    ms.Write(new byte[] { (byte)'f', (byte)'L', (byte)'a', (byte)'C' }, 0, 4);
                    ms.Write(new byte[] { 0x00, 0, 0, 34 }, 0, 4);   // STREAMINFO, not last
                    ms.Write(new byte[34], 0, 34);
                    ms.Write(new byte[] { 0x81, 0, 0, 10 }, 0, 4);   // PADDING, last-flag set
                    ms.Write(new byte[10], 0, 10);
                    ms.Write(new byte[] { 0xFF, 0xF8, 1, 2, 3 }, 0, 5);  // fake frame bytes
                    ms.Position = 0;
                    return ms.ToArray();
                }
                Assert(GetFlacAudioStartCore(new MemoryStream(Flac(false))) == 56, "flac walker: plain chain frames at 56");
                Assert(GetFlacAudioStartCore(new MemoryStream(Flac(true))) == 30 + 56, "flac walker: ID3v2 prefix skipped");
                Assert(GetFlacAudioStartCore(new MemoryStream(Flac(true, footer: true))) == 40 + 56, "flac walker: ID3v2 footer flag adds 10");
                Assert(GetFlacAudioStartCore(new MemoryStream(new byte[] { 1, 2, 3, 4, 5 })) == -1, "flac walker: non-flac returns -1");
                var trunc = Flac(false);
                Assert(GetFlacAudioStartCore(new MemoryStream(trunc, 0, 40)) == -1, "flac walker: truncated chain returns -1");

                // Two-region single-pass: shaA/shaB match independent region hashes.
                var tmp2 = Path.Combine(Path.GetTempPath(), $".truedat-selftest-{Guid.NewGuid():N}.bin");
                try
                {
                    var data = new byte[200000];
                    new Random(99).NextBytes(data);
                    File.WriteAllBytes(tmp2, data);
                    var expA = ComputeAudioStreamSha256(tmp2, 56, data.Length, out _);
                    var expB = ComputeAudioStreamSha256(tmp2, 0, data.Length, out _);
                    var (m2, a2, b2) = ComputeMd5AndTwoShas(tmp2, data.Length, true, 56, data.Length, 0, data.Length, out _);
                    Assert(a2 == expA && b2 == expB, "two-region single pass matches independent hashes");
                    Assert(m2 == ComputeFileMd5(tmp2), "two-region single pass md5 matches");
                    var (_, a3, b3) = ComputeMd5AndTwoShas(tmp2, data.Length, false, 56, data.Length, -1, -1, out _);
                    Assert(a3 == expA && b3 == null, "two-region: invalid region B yields null");
                }
                finally { try { File.Delete(tmp2); } catch { } }
            }

            // --- SpeechTagSniffer (spec 2026-07-22 A1) ---
            {
                // Synthetic ID3v2.3 tag: header + one frame, no padding.
                byte[] Id3(string frameId, byte[] payload)
                {
                    using var ms = new MemoryStream();
                    int frameLen = 10 + payload.Length;
                    ms.Write(new byte[] { (byte)'I', (byte)'D', (byte)'3', 3, 0, 0 }, 0, 6);
                    int tagSize = frameLen;   // syncsafe
                    ms.Write(new byte[] { (byte)((tagSize >> 21) & 0x7F), (byte)((tagSize >> 14) & 0x7F), (byte)((tagSize >> 7) & 0x7F), (byte)(tagSize & 0x7F) }, 0, 4);
                    var id = Encoding.ASCII.GetBytes(frameId);
                    ms.Write(id, 0, 4);
                    ms.Write(new byte[] { (byte)((payload.Length >> 24) & 0xFF), (byte)((payload.Length >> 16) & 0xFF), (byte)((payload.Length >> 8) & 0xFF), (byte)(payload.Length & 0xFF) }, 0, 4);
                    ms.Write(new byte[2], 0, 2);   // frame flags
                    ms.Write(payload, 0, payload.Length);
                    ms.Write(new byte[] { 0xFF, 0xFB, 0x90, 0x00 }, 0, 4);   // fake MPEG frame
                    ms.Position = 0;
                    return ms.ToArray();
                }
                byte[] Text(string s) { var b = new byte[1 + s.Length]; b[0] = 0; Encoding.ASCII.GetBytes(s, 0, s.Length, b, 1); return b; }

                // Multi-frame builder: ID3v2.4 header + N frames (v2.4 frame sizes are
                // syncsafe, unlike the v2.3 single-frame Id3 helper above — both shapes
                // are walked by ScanId3v2, so the two fixtures exercise both encodings).
                byte[] Id3Multi((string Id, byte[] Body)[] frames)
                {
                    var body = new List<byte>();
                    foreach (var f in frames)
                    {
                        body.AddRange(Encoding.ASCII.GetBytes(f.Id));
                        body.Add((byte)((f.Body.Length >> 21) & 0x7F));
                        body.Add((byte)((f.Body.Length >> 14) & 0x7F));
                        body.Add((byte)((f.Body.Length >> 7) & 0x7F));
                        body.Add((byte)(f.Body.Length & 0x7F));
                        body.Add(0); body.Add(0);
                        body.AddRange(f.Body);
                    }
                    var size = body.Count;
                    var head = new List<byte> { 0x49, 0x44, 0x33, 4, 0, 0,
                        (byte)((size >> 21) & 0x7F), (byte)((size >> 14) & 0x7F),
                        (byte)((size >> 7) & 0x7F), (byte)(size & 0x7F) };
                    head.AddRange(body);
                    return head.ToArray();
                }

                SpeechMarkers? Detect(byte[] bytes) => SpeechTagSniffer.TryDetectAllCore(new MemoryStream(bytes));

                var strong = Detect(Id3("PCST", new byte[] { 0, 0, 0, 0 }));
                Assert(strong != null && strong.Strong.Contains("PCST"), "sniffer: PCST is a strong marker");
                Assert(strong != null && strong.Provenance.Count == 0 && !strong.GenreText, "sniffer: PCST alone sets nothing else");

                var feed = Detect(Id3("WFED", Text("https://feed.example/rss")));
                Assert(feed != null && feed.Provenance.Contains("WFED"), "sniffer: WFED is provenance, not strong");
                Assert(feed != null && feed.Strong.Count == 0, "sniffer: a feed URL does not assert 'is a podcast'");

                var guid = Detect(Id3("TGID", Text("guid-123")));
                Assert(guid != null && guid.Provenance.Contains("TGID"), "sniffer: TGID is provenance");

                var genreExact = Detect(Id3("TCON", Text("Podcast")));
                Assert(genreExact != null && genreExact.GenreText, "sniffer: TCON exactly 'Podcast' counts as genre-text evidence");
                Assert(genreExact != null && genreExact.Strong.Count == 0 && genreExact.Provenance.Count == 0, "sniffer: genre text is neither strong nor provenance");

                // The substring loosening this task exists to remove. The library-side genre
                // rule is exact-equals; the file-side check must not be looser than it.
                Assert(Detect(Id3("TCON", Text("Comedy Podcast"))) == null, "sniffer: TCON 'Comedy Podcast' no longer matches (exact-equals now)");
                Assert(Detect(Id3("TCON", Text("Podcasts"))) == null, "sniffer: TCON 'Podcasts' no longer matches");
                Assert(Detect(Id3("TCON", Text("Rock"))) == null, "sniffer: TCON 'Rock' is not speech");
                var trimmedGenre = Detect(Id3("TCON", Text("  podcast  ")));
                Assert(trimmedGenre != null && trimmedGenre.GenreText, "sniffer: TCON match is trimmed and case-insensitive");

                Assert(Detect(new byte[] { 1, 2, 3 }) == null, "sniffer: junk stream returns null");
                var truncated = Id3("PCST", new byte[] { 0, 0, 0, 0 });
                Assert(SpeechTagSniffer.TryDetectAllCore(new MemoryStream(truncated, 0, 8)) == null, "sniffer: truncated header returns null");

                // Synthetic MP4: ftyp + moov>udta>meta(v/f)>ilst>pcst
                byte[] Mp4WithAtom(string leaf)
                {
                    byte[] Atom(string type, byte[] body)
                    {
                        var b = new byte[8 + body.Length];
                        int sz = b.Length;
                        b[0] = (byte)((sz >> 24) & 0xFF); b[1] = (byte)((sz >> 16) & 0xFF); b[2] = (byte)((sz >> 8) & 0xFF); b[3] = (byte)(sz & 0xFF);
                        Encoding.ASCII.GetBytes(type, 0, 4, b, 4);
                        Array.Copy(body, 0, b, 8, body.Length);
                        return b;
                    }
                    var pcst = Atom(leaf, new byte[4]);
                    var ilst = Atom("ilst", pcst);
                    var metaBody = new byte[4 + ilst.Length];   // 4-byte version/flags
                    Array.Copy(ilst, 0, metaBody, 4, ilst.Length);
                    var meta = Atom("meta", metaBody);
                    var udta = Atom("udta", meta);
                    var moov = Atom("moov", udta);
                    var ftyp = Atom("ftyp", Encoding.ASCII.GetBytes("M4A "));
                    using var ms = new MemoryStream();
                    ms.Write(ftyp, 0, ftyp.Length);
                    ms.Write(moov, 0, moov.Length);
                    ms.Position = 0;
                    return ms.ToArray();
                }
                var mp4Strong = Detect(Mp4WithAtom("pcst"));
                Assert(mp4Strong != null && mp4Strong.Strong.Contains("pcst"), "sniffer: mp4 pcst atom is strong");
                var mp4Feed = Detect(Mp4WithAtom("purl"));
                Assert(mp4Feed != null && mp4Feed.Provenance.Contains("purl"), "sniffer: mp4 purl atom is provenance");
                Assert(Detect(Mp4WithAtom("cprt")) == null, "sniffer: mp4 without podcast atoms returns null");

                // First-match-wins is what this task removes: a file carrying BOTH a strong
                // flag and a feed URL must report both, whichever came first in the tag.
                var both = Detect(Id3Multi(new[] { ("WFED", Text("https://feed.example/rss")), ("PCST", new byte[] { 0, 0, 0, 0 }) }));
                Assert(both != null && both.Strong.Contains("PCST") && both.Provenance.Contains("WFED"),
                    "sniffer: all markers are reported, not just the first");
                Assert(both != null && both.Describe().Contains("PCST"), "sniffer: Describe names the strong marker");
            }

            // --- SmfmReader M4A path (ID3v2 GEOB inside MP4 ID32 box) ---
            // Portable regression for the .m4a/.mp4 SMFM reader: a synthetic in-code MP4
            // (moov>udta>meta[FullBox]>ID32>ID3v2.3 tag>GEOB 'application/SMFMF' > GBPM+STMO)
            // so the container path has coverage independent of any operator file.
            {
                byte[] Be32(int v) => new byte[] { (byte)((v >> 24) & 0xFF), (byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF) };
                byte[] Cat(params byte[][] parts)
                {
                    using var ms = new MemoryStream();
                    foreach (var pp in parts) ms.Write(pp, 0, pp.Length);
                    return ms.ToArray();
                }
                // SMFM sub-block: tag(4) + magic(8) + 4 reserved + BE length(4) + payload.
                byte[] SubBlock(string tag, byte[] payload) => Cat(
                    Encoding.ASCII.GetBytes(tag),
                    Encoding.ASCII.GetBytes("STAEMMLW"),   // PC-firmware magic
                    new byte[4],                            // reserved
                    Be32(payload.Length),
                    payload);

                // GBPM: big-endian float32 = 120.0f (0x42F00000).
                var gbpm = SubBlock("GBPM", new byte[] { 0x42, 0xF0, 0x00, 0x00 });
                // STMO: 40 records [type, channel, sub, value]; channel = i%10, value = (ch+1)*10
                // → averaged scores [10,20,...,100], argmax channel 9.
                var stmoPayload = new byte[160];
                for (int i = 0; i < 40; i++)
                {
                    int ch = i % 10;
                    stmoPayload[i * 4 + 1] = (byte)ch;
                    stmoPayload[i * 4 + 3] = (byte)((ch + 1) * 10);
                }
                var stmo = SubBlock("STMO", stmoPayload);
                var smfmObject = Cat(gbpm, stmo);

                // GEOB frame content: encoding(0) + mime\0 + filename\0 + description\0 + object.
                var geobContent = Cat(new byte[] { 0 },
                    Encoding.ASCII.GetBytes("application/SMFMF"), new byte[] { 0 },
                    new byte[] { 0 }, new byte[] { 0 }, smfmObject);
                // ID3v2.3 GEOB frame: id(4) + plain BE size(4) + flags(2) + content.
                var geobFrame = Cat(Encoding.ASCII.GetBytes("GEOB"), Be32(geobContent.Length), new byte[2], geobContent);
                // ID3v2.3 tag: "ID3" v3 rev0 flags0 + syncsafe int28 size + frames.
                int tagBodyLen = geobFrame.Length;
                var syncsafe = new byte[] { (byte)((tagBodyLen >> 21) & 0x7F), (byte)((tagBodyLen >> 14) & 0x7F), (byte)((tagBodyLen >> 7) & 0x7F), (byte)(tagBodyLen & 0x7F) };
                var id3Tag = Cat(new byte[] { (byte)'I', (byte)'D', (byte)'3', 3, 0, 0 }, syncsafe, geobFrame);

                // MP4 box: size(4, includes 8-byte header) + type(4) + body.
                byte[] Box(string type, byte[] body)
                {
                    var b = new byte[8 + body.Length];
                    Array.Copy(Be32(b.Length), 0, b, 0, 4);
                    Encoding.ASCII.GetBytes(type, 0, 4, b, 4);
                    Array.Copy(body, 0, b, 8, body.Length);
                    return b;
                }
                // ID32 body: 4 fullbox version/flags + 2 language + ID3 tag.
                var id32 = Box("ID32", Cat(new byte[4], new byte[2], id3Tag));
                var meta = Box("meta", Cat(new byte[4], id32));   // meta is a FullBox
                var udta = Box("udta", meta);
                var moov = Box("moov", udta);
                var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("M4A "));
                var mp4 = Cat(ftyp, moov);

                var tmpM4a = Path.Combine(Path.GetTempPath(), $".truedat-selftest-{Guid.NewGuid():N}.m4a");
                try
                {
                    File.WriteAllBytes(tmpM4a, mp4);
                    var res = SmfmReader.TryRead(tmpM4a);
                    Assert(res.HasValue, "smfm m4a: synthetic MP4 ID32/GEOB yields a result");
                    Assert(res.HasValue && Math.Abs(res.Value.Bpm - 120.0) < 0.001, $"smfm m4a: GBPM decodes to 120 (got {res?.Bpm})");
                    Assert(res.HasValue && res.Value.Scores.Length == 10, $"smfm m4a: 10 STMO scores (got {res?.Scores?.Length})");
                    Assert(res.HasValue && res.Value.Scores[0] == 10 && res.Value.Scores[9] == 100,
                        $"smfm m4a: per-channel averages [10..100] (got [{(res.HasValue ? string.Join(",", res.Value.Scores) : "")}])");
                    Assert(res.HasValue && res.Value.Channel == 9, $"smfm m4a: argmax channel is 9 (got {res?.Channel})");
                }
                finally { try { File.Delete(tmpM4a); } catch { } }

                // Defensive: missing and truncated containers return null, never throw.
                Assert(SmfmReader.TryRead(Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.m4a")) == null,
                    "smfm m4a: missing file returns null");
                var tmpTrunc = Path.Combine(Path.GetTempPath(), $".truedat-selftest-{Guid.NewGuid():N}.m4a");
                try
                {
                    File.WriteAllBytes(tmpTrunc, new byte[] { 0, 0, 0, 8, (byte)'m', (byte)'o', (byte)'o' });  // short/truncated box
                    Assert(SmfmReader.TryRead(tmpTrunc) == null, "smfm m4a: truncated container returns null");
                }
                finally { try { File.Delete(tmpTrunc); } catch { } }
            }

            // --- speech labelling: explicit labels only (2026-07-24) ---
            // The former "Episode Date + corroborator" vote is deleted: MusicBee maps
            // ID3v2.4 TDRL (release date) into the Episode Date key, so the anchor rides
            // on ordinary music. These assertions are the regression guard against it
            // coming back a third time (it was already removed once, 050868f).
            {
                string Entry(int id, string name, string extra, int totalTimeMs) => $@"
        <key>{id}</key>
        <dict>
            <key>Track ID</key><integer>{id}</integer>
            <key>Name</key><string>{name}</string>
            <key>Artist</key><string>A</string>
            <key>Total Time</key><integer>{totalTimeMs}</integer>
            {extra}
            <key>Location</key><string>file://localhost/C:/tmp/{id}.mp3</string>
        </dict>";
                var xml = "<?xml version=\"1.0\"?><plist version=\"1.0\"><dict><key>Tracks</key><dict>"
                    + Entry(1, "YtRipMusic", "<key>Episode Date</key><string>1/1/2020</string>", 180000)
                    + Entry(2, "LongDatedAlbum", "<key>Episode Date</key><string>1/1/2020</string>", 3120000)
                    + Entry(3, "PublishedDatedTrack", "<key>Episode Date</key><string>1/1/2020</string><key>Publisher</key><string>Label</string>", 180000)
                    + Entry(4, "LongClassical", "", 3120000)
                    + Entry(5, "LongPublishedLiveSet", "<key>Publisher</key><string>Label</string>", 3120000)
                    + Entry(6, "GenrePodcast", "<key>Genre</key><string>Podcast</string>", 180000)
                    + Entry(7, "GenrePodcastLowerCase", "<key>Genre</key><string>podcast</string>", 180000)
                    + Entry(8, "ITunesFlagged", "<key>Podcast</key><true/>", 180000)
                    + "</dict></dict></plist>";
                var tmpXml = Path.Combine(Path.GetTempPath(), $".truedat-selftest-{Guid.NewGuid():N}.xml");
                try
                {
                    File.WriteAllText(tmpXml, xml);
                    var parsed = ITunesParser.Parse(tmpXml, out _);
                    // The whole TDRL class must now pass through untouched.
                    Assert(!parsed.First(p => p.Name == "YtRipMusic").IsSpeech, "label: Episode Date alone is not speech");
                    Assert(!parsed.First(p => p.Name == "LongDatedAlbum").IsSpeech, "label: Episode Date + 52min is not speech");
                    Assert(!parsed.First(p => p.Name == "PublishedDatedTrack").IsSpeech, "label: Episode Date + Publisher is not speech");
                    Assert(!parsed.First(p => p.Name == "LongClassical").IsSpeech, "label: duration alone is not speech");
                    Assert(!parsed.First(p => p.Name == "LongPublishedLiveSet").IsSpeech, "label: Publisher + 52min is not speech");
                    // Explicit labels still decide, and still name themselves.
                    Assert(parsed.First(p => p.Name == "GenrePodcast").IsSpeech, "label: Genre=Podcast makes it speech");
                    Assert(parsed.First(p => p.Name == "GenrePodcast").SpeechReason == "Genre=Podcast", "label: Genre reason recorded");
                    Assert(parsed.First(p => p.Name == "GenrePodcastLowerCase").IsSpeech, "label: genre match is case-insensitive");
                    Assert(parsed.First(p => p.Name == "ITunesFlagged").IsSpeech, "label: iTunes Podcast=true makes it speech");
                    Assert(parsed.First(p => p.Name == "ITunesFlagged").SpeechReason == "Podcast=true", "label: Podcast=true reason recorded");
                }
                finally { try { File.Delete(tmpXml); } catch { } }
            }

            // --- PreviewPlanner (Phase 2a) ---
            {
                ITunesTrack Trk(string loc, string genre, int ms, long size)
                    => new ITunesTrack { Location = loc, Name = Path.GetFileName(loc), Artist = "A",
                                         Album = "Alb", Genre = genre, TotalTimeMs = ms, SizeBytes = size };

                var tracks = new List<ITunesTrack>
                {
                    Trk(@"D:\M\short.flac",           "Rock",    180000,  5_000_000),
                    Trk(@"D:\M\long.flac",            "Rock",   2400000, 60_000_000),  // 40 min -> long
                    Trk(@"D:\M\huge.flac",            "Rock",  13000000, 90_000_000),  // over a 12000s ceiling
                    Trk(@"D:\M\clip.mp4",             "Rock",    180000,  5_000_000),  // structural: video
                    Trk(@"D:\M\list.m3u",             "Rock",    180000,       1_000),  // structural: non-audio
                    Trk(@"D:\Podcasts\ep1.mp3",       "Podcast",3000000, 30_000_000),
                    Trk(@"D:\M\analyzed.flac",        "Rock",    180000,  5_000_000),
                };
                tracks[6].Location = @"D:\M\analyzed.flac";
                var remote = Trk("https://feed.example/ep2.mp3", "Podcast", 3000000, 0);
                remote.IsRemote = true;
                tracks.Add(remote);

                var catalog = new Dictionary<string, TrackEntry>(PathComparer.Instance);
                catalog[@"D:\M\analyzed.flac"] = new TrackEntry
                {
                    Features = new TrackFeatures { FilePath = @"D:\M\analyzed.flac", Genre = "Rock" }
                };

                var exclSet = ExclusionSet.FromJson(
                    "{\"schemaVersion\":1,\"rules\":[" +
                    "{\"kind\":\"folder\",\"action\":\"exclude\",\"pattern\":\"\\\\Podcasts\\\\**\"}," +
                    "{\"kind\":\"folder\",\"action\":\"exclude\",\"pattern\":\"\\\\NoSuchDir\\\\**\"}]}",
                    out var exclErr);
                Assert(exclErr == null, "preview: test exclusion set parses");

                var input = new PreviewPlannerInput
                {
                    Tracks = tracks,
                    Catalog = catalog,
                    Exclusions = exclSet,
                    XmlPath = @"D:\M\iTunes Music Library.xml",
                    MoodsPath = @"D:\M\mbxmoods.json",
                    ExclusionsPath = @"D:\M\mbxmoods-exclude.json",
                    MaxDurationSecs = 12000,
                    MaxDurationSource = "default",
                    LongTrackSecs = 1800,
                    ReviewCap = 500,
                    MeasuredRtf = 0.10,
                    Parallelism = 2,
                    SniffMarkers = p => p.EndsWith("long.flac", StringComparison.OrdinalIgnoreCase) ? "PCST" : null,
                    SpeechVerdict = null,
                };
                var plan = PreviewPlanner.Build(input);

                // structural skips are counted by class and never treated as review candidates
                int Bucket(string cls)
                {
                    var b = plan.AutoSkip.Find(x => x.Class == cls);
                    return b == null ? 0 : b.Count;
                }
                Assert(Bucket("video") == 1, $"preview: video counted structurally (got {Bucket("video")})");
                Assert(Bucket("nonAudio") == 1, $"preview: non-audio counted structurally (got {Bucket("nonAudio")})");
                Assert(Bucket("streamUrl") == 1, $"preview: remote URL counted structurally (got {Bucket("streamUrl")})");
                Assert(Bucket("overLimit") == 1, $"preview: over-ceiling counted structurally (got {Bucket("overLimit")})");
                Assert(!plan.Review.Exists(c => c.Path.EndsWith("clip.mp4", StringComparison.OrdinalIgnoreCase)),
                    "preview: a structural skip is not a review candidate");

                // counts
                Assert(plan.Counts.LibraryTotal == tracks.Count, $"preview: libraryTotal is every parsed track (got {plan.Counts.LibraryTotal})");
                Assert(plan.Counts.Analyzed == 1, $"preview: analyzed counts catalog hits (got {plan.Counts.Analyzed})");
                Assert(plan.Counts.Excluded == 1, $"preview: excluded counts rule hits (got {plan.Counts.Excluded})");
                Assert(plan.Counts.AwaitingReview == plan.ReviewTotal, "preview: awaitingReview matches reviewTotal");

                // rule stats surface a stale rule
                var live = plan.Rules.Find(r => r.Rule == @"folder=\Podcasts\**");
                var stale = plan.Rules.Find(r => r.Rule == @"folder=\NoSuchDir\**");
                Assert(live != null && live.MatchCount == 1, "preview: matched rule reports its hits");
                Assert(stale != null && stale.MatchCount == 0, "preview: unmatched rule reports zero (stale-rule signal)");

                // review candidates and their evidence
                var longCand = plan.Review.Find(c => c.Path.EndsWith("long.flac", StringComparison.OrdinalIgnoreCase));
                Assert(longCand != null, "preview: a 40-minute track is a review candidate");
                Assert(longCand!.Reasons.Contains("long"), "preview: long duration is recorded as a reason");
                Assert(longCand.Reasons.Contains("marker:PCST"), "preview: a sniffed marker is recorded as a reason");
                // exactly 3: every review candidate is sniffed (marker or not, via the else
                // branch) — long.flac, huge.flac, ep1.mp3. A regression that skipped sniffing
                // whenever the marker came back null would leave this at 1.
                Assert(plan.SniffedCount == 3, $"preview: sniffed count is the true count, not just markers found (got {plan.SniffedCount})");
                var shortCand = plan.Review.Find(c => c.Path.EndsWith("short.flac", StringComparison.OrdinalIgnoreCase));
                Assert(shortCand == null, "preview: an ordinary short track is not a candidate");

                // an excluded track is a candidate so the decision is reversible
                var exCand = plan.Review.Find(c => c.Path.EndsWith("ep1.mp3", StringComparison.OrdinalIgnoreCase));
                // Null-safe on purpose: a regression that drops the excluded track from the
                // review list must FAIL these assertions by name, not crash the whole
                // self-test run on a null deref before the later blocks get to speak.
                Assert(exCand != null, "preview: an excluded track is still listed for review");
                Assert(exCand != null && exCand.CurrentDecision == "excluded", $"preview: current decision is reported (got {exCand?.CurrentDecision})");
                Assert(exCand != null && exCand.Reasons.Contains("excluded"), "preview: exclusion is recorded as a reason");

                // over-limit candidate carries the flag AND the structural count
                var overCand = plan.Review.Find(c => c.Path.EndsWith("huge.flac", StringComparison.OrdinalIgnoreCase));
                Assert(overCand != null && overCand.OverLimit, "preview: over-ceiling track is flagged for review");

                // estimate: new vs cached, and a measured basis
                Assert(plan.Estimate.CachedTracks == 1, $"preview: cached count (got {plan.Estimate.CachedTracks})");
                // exactly 2: short.flac and long.flac are the only new work. huge.flac is
                // over-limit, ep1.mp3 is rule-excluded, analyzed.flac is already cached — a
                // scan analyzes none of the three, so none of them may inflate the estimate.
                // A regression letting the over-limit or the excluded track leak into new
                // work would make this 3 or 4.
                Assert(plan.Estimate.NewTracks == 2, $"preview: new-track count excludes cached, over-limit and rule-excluded tracks (got {plan.Estimate.NewTracks})");
                Assert(plan.Estimate.EtaBasis == "measured-rtf", $"preview: measured RTF is preferred (got {plan.Estimate.EtaBasis})");
                Assert(plan.Estimate.EtaSecs > 0, "preview: ETA is estimated when RTF is known");

                // no measured RTF -> ETA omitted rather than guessed
                input.MeasuredRtf = 0;
                var plan2 = PreviewPlanner.Build(input);
                Assert(plan2.Estimate.EtaSecs < 0, "preview: ETA omitted (negative) when nothing was measured");
                Assert(plan2.Estimate.EtaBasis == "unavailable", "preview: basis says unavailable rather than implying a measurement");

                // genre histogram
                var gPod = plan.Genres.Find(g => g.Name == "Podcast");
                // exactly 2: D:\Podcasts\ep1.mp3 plus the remote ep2.mp3, both genre
                // "Podcast". A regression that computed the histogram after the IsRemote
                // continue (the whole-library-histogram rule exists to prevent exactly that)
                // would drop this to 1.
                Assert(gPod != null && gPod.Tracks == 2, $"preview: genre histogram counts every track incl. remote (got {gPod?.Tracks})");
                Assert(plan.Genres.TrueForAll(g => g.TotalSecs >= 0), "preview: genre durations are non-negative");

                // limits are echoed, never invented
                Assert(plan.Limits.MaxDurationSecs == 12000 && plan.Limits.LongTrackSecs == 1800,
                    "preview: limits echo the configured values");

                // the cap is honoured and reported honestly
                input.MeasuredRtf = 0.10;
                input.ReviewCap = 1;
                var plan3 = PreviewPlanner.Build(input);
                Assert(plan3.Review.Count == 1, $"preview: review list honours the cap (got {plan3.Review.Count})");
                Assert(plan3.ReviewTruncated, "preview: truncation is flagged");
                Assert(plan3.ReviewTotal > 1, "preview: reviewTotal reports the true count, not the capped one");
                Assert(plan3.Counts.AwaitingReview == plan3.ReviewTotal, "preview: awaitingReview is the true count even when truncated");

                // sort order: longest first, so triage starts with the worst offenders
                for (int i = 1; i < plan.Review.Count; i++)
                    Assert(plan.Review[i - 1].DurationSecs >= plan.Review[i].DurationSecs,
                        "preview: candidates are sorted by duration descending");

                // structural: a dead catalog entry (file gone from disk) is "missing", not
                // "new work". FileExists is injected so this stays filesystem-free; null
                // disables the check entirely, which is what the assertions above rely on.
                var missingInput = new PreviewPlannerInput
                {
                    Tracks = tracks,
                    Catalog = catalog,
                    Exclusions = exclSet,
                    XmlPath = input.XmlPath,
                    MoodsPath = input.MoodsPath,
                    ExclusionsPath = input.ExclusionsPath,
                    MaxDurationSecs = 12000,
                    MaxDurationSource = "default",
                    LongTrackSecs = 1800,
                    ReviewCap = 500,
                    MeasuredRtf = 0.10,
                    Parallelism = 2,
                    SniffMarkers = input.SniffMarkers,
                    SpeechVerdict = null,
                    FileExists = null,
                };
                var planNoMissingCheck = PreviewPlanner.Build(missingInput);

                missingInput.FileExists = p => !p.EndsWith("short.flac", StringComparison.OrdinalIgnoreCase);
                var planWithMissing = PreviewPlanner.Build(missingInput);

                int MissingBucket(PreviewPlan p)
                {
                    var b = p.AutoSkip.Find(x => x.Class == "missing");
                    return b == null ? 0 : b.Count;
                }
                Assert(MissingBucket(planWithMissing) == 1, $"preview: missing file counted structurally (got {MissingBucket(planWithMissing)})");
                Assert(!planWithMissing.Review.Exists(c => c.Path.EndsWith("short.flac", StringComparison.OrdinalIgnoreCase)),
                    "preview: a missing file is not a review candidate");
                // the load-bearing assertion: without this, a dead catalog entry silently
                // inflates NewTracks (and the ETA built from it) as phantom new work. Both
                // sides are pinned to exact values — a relative assertion would still pass if
                // some other filter regression moved both counts together.
                Assert(planNoMissingCheck.Estimate.NewTracks == 2,
                    $"preview: with no existence check, short.flac + long.flac are the new work (got {planNoMissingCheck.Estimate.NewTracks})");
                Assert(planWithMissing.Estimate.NewTracks == 1,
                    $"preview: a missing file drops out of new-work count (got {planWithMissing.Estimate.NewTracks})");

                // C-1 regression: a rule that matches a MISSING file must still report its hit.
                // The scan's chain runs FilterExclusions before it ever checks existence, so the
                // rule counts there; preview used to `continue` on missing first and count zero.
                // On a metadata-mirror box every file is missing, so EVERY rule read as
                // "0 matched (stale rule?)" while working — the stale-rule signal inverted from
                // exposing dead rules to condemning live ones. Exact value, not an inequality:
                // one track matches, so one hit.
                var mirrorTarget = missingInput.Tracks[0].Location!;
                var mirrorExcl = ExclusionSet.FromJson(
                    "{\"schemaVersion\":1,\"rules\":[{\"kind\":\"file\",\"action\":\"exclude\",\"path\":"
                        + JsonSerializer.Serialize(mirrorTarget) + "}]}",
                    out var mirrorFatal);
                Assert(mirrorFatal == null, $"preview: mirror-box exclusion fixture parses ({mirrorFatal})");
                // Reuse the same input, but with NOTHING on disk — the mirror-box condition.
                missingInput.Exclusions = mirrorExcl;
                missingInput.FileExists = _ => false;
                var mirrorPlan = PreviewPlanner.Build(missingInput);
                var mirrorRule = mirrorPlan.Rules.Count > 0 ? mirrorPlan.Rules[0] : null;
                Assert(mirrorRule != null && mirrorRule.MatchCount == 1,
                    $"preview: a rule matching a MISSING file still reports its hit (got {(mirrorRule == null ? "no rule" : mirrorRule.MatchCount.ToString())})");

                // composite: a track that is BOTH over-limit AND rule-excluded must still
                // appear exactly once, carry both reasons, respect the exclusion decision,
                // and never be counted as new work — isolated fixture so it doesn't disturb
                // the exact counts pinned above.
                {
                    var compositeTracks = new List<ITunesTrack>
                    {
                        Trk(@"D:\Podcasts\hugecast.mp3", "Podcast", 13000000, 90_000_000),
                    };
                    var compositeExcl = ExclusionSet.FromJson(
                        "{\"schemaVersion\":1,\"rules\":[" +
                        "{\"kind\":\"folder\",\"action\":\"exclude\",\"pattern\":\"\\\\Podcasts\\\\**\"}]}",
                        out var compositeExclErr);
                    Assert(compositeExclErr == null, "preview: composite exclusion set parses");

                    var compositeInput = new PreviewPlannerInput
                    {
                        Tracks = compositeTracks,
                        Catalog = new Dictionary<string, TrackEntry>(PathComparer.Instance),
                        Exclusions = compositeExcl,
                        XmlPath = input.XmlPath,
                        MoodsPath = input.MoodsPath,
                        ExclusionsPath = input.ExclusionsPath,
                        MaxDurationSecs = 12000,
                        MaxDurationSource = "default",
                        LongTrackSecs = 1800,
                        ReviewCap = 500,
                        MeasuredRtf = 0.10,
                        Parallelism = 2,
                        SniffMarkers = null,
                        SpeechVerdict = null,
                        FileExists = null,
                    };
                    var compositePlan = PreviewPlanner.Build(compositeInput);
                    var compositeMatches = compositePlan.Review.FindAll(
                        c => c.Path.EndsWith("hugecast.mp3", StringComparison.OrdinalIgnoreCase));
                    Assert(compositeMatches.Count == 1, $"preview: over-limit+excluded track appears exactly once (got {compositeMatches.Count})");
                    var compositeCand = compositeMatches[0];
                    Assert(compositeCand.Reasons.Contains("over-limit"), "preview: composite candidate carries the over-limit reason");
                    Assert(compositeCand.Reasons.Contains("excluded"), "preview: composite candidate carries the excluded reason");
                    Assert(compositeCand.CurrentDecision == "excluded", $"preview: composite candidate's decision is excluded (got {compositeCand.CurrentDecision})");
                    Assert(compositePlan.Estimate.NewTracks == 0, $"preview: over-limit+excluded track is not counted as new work (got {compositePlan.Estimate.NewTracks})");
                }
            }

            // --- I-2: a `file` rule whose target MOVED must not read as stale -------------
            // MatchCount == 0 is the documented stale-rule signal and the documented response is
            // to DELETE the rule; on a moved file that re-admits the very file the operator
            // rejected. Spec 4.2 requires the rule be reported moved-or-deleted and its content
            // re-resolvable against the catalog's shas. REPORTING only — matching stays exact
            // path equality, so nothing starts excluding a path nobody named.
            {
                const string movedPath  = @"D:\M\gone\setlist.flac";
                const string copyA      = @"D:\M\albums\setlist.flac";
                const string copyB      = @"D:\M\archive\setlist.flac";
                const string offlinePath = @"X:\Mirror\offline.flac";
                const string livePath   = @"D:\M\here.flac";
                const string sha = "aaaa1111bbbb2222cccc3333dddd4444eeee5555ffff6666aaaa7777bbbb8888";

                TrackEntry Ent(string s) => new TrackEntry { AudioStreamSha256 = s };
                var cat = new Dictionary<string, TrackEntry>(PathComparer.Instance)
                {
                    // Deliberately inserted copyB-before-copyA so the sorted candidate list is
                    // proving the sort, not the insertion order.
                    [copyB] = Ent(sha),
                    [copyA] = Ent(sha),
                    [offlinePath] = Ent("ffff0000ffff0000ffff0000ffff0000ffff0000ffff0000ffff0000ffff0000"),
                };

                string FileRule(string p, string? s) =>
                    "{\"kind\":\"file\",\"action\":\"exclude\",\"path\":" + JsonSerializer.Serialize(p)
                    + (s == null ? "" : ",\"audioStreamSha256\":" + JsonSerializer.Serialize(s)) + "}";

                var i2Excl = ExclusionSet.FromJson(
                    "{\"schemaVersion\":1,\"rules\":["
                    + FileRule(movedPath, sha) + ","
                    + FileRule(offlinePath, sha) + ","
                    + FileRule(@"D:\M\vanished\nosha.flac", null) + ","
                    + FileRule(livePath, sha) + ","
                    + "{\"kind\":\"folder\",\"action\":\"exclude\",\"pattern\":\"\\\\Podcasts\\\\**\"}]}",
                    out var i2Err);
                Assert(i2Err == null, $"I-2: fixture rule set parses ({i2Err})");
                Assert(i2Excl.Rules.Count == 5, $"I-2: all five fixture rules survived parsing (got {i2Excl.Rules.Count})");
                // Sha round-trips through the parser — the reserved-field contract every part of
                // this feature stands on.
                Assert(i2Excl.Rules[0].Sha == sha, "I-2: a file rule's audioStreamSha256 survives parsing");

                var i2Plan = PreviewPlanner.Build(new PreviewPlannerInput
                {
                    Tracks = new List<ITunesTrack>(),
                    Catalog = cat,
                    Exclusions = i2Excl,
                    MaxDurationSecs = 12000,
                    LongTrackSecs = 1800,
                    ReviewCap = 500,
                    Parallelism = 1,
                    // Only livePath is on disk. Everything else is absent, which is the whole
                    // point: absent-and-uncatalogued is moved, absent-but-catalogued is a mirror.
                    FileExists = p => p == livePath,
                });
                Assert(i2Plan.Rules.Count == 5, $"I-2: every rule still gets a stat row (got {i2Plan.Rules.Count})");

                var rMoved = i2Plan.Rules[0];
                Assert(rMoved.State == PreviewRuleStat.StateMoved,
                    $"I-2: a file rule absent from disk AND from the catalog is reported moved-or-deleted (got {rMoved.State})");
                Assert(rMoved.Candidates.Count == 2,
                    $"I-2: ALL catalog copies sharing the sha are offered, not an arbitrary one (got {rMoved.Candidates.Count})");
                Assert(rMoved.Candidates[0] == copyA && rMoved.Candidates[1] == copyB,
                    $"I-2: candidates are sorted, so the list does not depend on catalog order (got {string.Join(",", rMoved.Candidates)})");
                Assert(PreviewRuleNote(rMoved) == $"   (moved — content now at: {copyA} | {copyB})",
                    $"I-2: the moved note names the current paths instead of prompting a delete (got '{PreviewRuleNote(rMoved)}')");

                var rMirror = i2Plan.Rules[1];
                Assert(rMirror.State == PreviewRuleStat.StateUnreachable,
                    $"I-2: a file rule absent from disk but PRESENT in the catalog is path-unreachable, not moved (got {rMirror.State})");
                Assert(rMirror.Candidates.Count == 0,
                    $"I-2: an unreachable rule offers no candidates — nothing moved (got {rMirror.Candidates.Count})");
                Assert(PreviewRuleNote(rMirror) == "   (file not reachable from here — still in the catalog, rule is live)",
                    $"I-2: the unreachable note says the rule is live (got '{PreviewRuleNote(rMirror)}')");

                var rNoSha = i2Plan.Rules[2];
                Assert(rNoSha.State == PreviewRuleStat.StateMoved,
                    $"I-2: a sha-less file rule whose path is gone is still moved-or-deleted (got {rNoSha.State})");
                Assert(rNoSha.Candidates.Count == 0,
                    $"I-2: with no recorded sha there is nothing to re-resolve against (got {rNoSha.Candidates.Count})");
                Assert(PreviewRuleNote(rNoSha) == "   (moved or deleted — no catalog entry holds this content)",
                    $"I-2: the no-candidate note does not claim to know where the file went (got '{PreviewRuleNote(rNoSha)}')");

                var rLive = i2Plan.Rules[3];
                Assert(rLive.State == PreviewRuleStat.StateLive,
                    $"I-2: a file rule whose path is on disk stays live (got {rLive.State})");
                Assert(PreviewRuleNote(rLive) == "   (stale rule?)",
                    $"I-2: a live rule with zero hits still gets the stale prompt (got '{PreviewRuleNote(rLive)}')");

                Assert(i2Plan.Rules[4].State == PreviewRuleStat.StateLive,
                    $"I-2: a folder rule is never path-anchored, so never moved (got {i2Plan.Rules[4].State})");

                // Matching is UNCHANGED: the rule still only matches the path it names. A moved
                // rule must not start excluding the copies its sha resolved to.
                Assert(!i2Excl.IsExcluded(copyA, null, out _),
                    "I-2: a moved rule's sha-resolved candidate is NOT excluded (report, never re-match)");
                Assert(i2Excl.IsExcluded(movedPath, null, out _),
                    "I-2: the moved rule still matches exactly the path it names");

                // No existence check injected (a caller that cannot check) must say nothing
                // rather than guess: everything reads live.
                var i2NoCheck = PreviewPlanner.Build(new PreviewPlannerInput
                {
                    Tracks = new List<ITunesTrack>(), Catalog = cat, Exclusions = i2Excl,
                    MaxDurationSecs = 12000, LongTrackSecs = 1800, ReviewCap = 500, Parallelism = 1,
                    FileExists = null,
                });
                Assert(i2NoCheck.Rules.TrueForAll(r => r.State == PreviewRuleStat.StateLive),
                    "I-2: with no existence check injected, no rule is claimed moved");
            }

            // --- --preview's new-work accounting must agree with the scan's own filters
            // (whole-branch fix wave). Every one of these classes is dropped before Essentia
            // sees it, so counting it as new work inflates newTracks, newBytes AND the ETA
            // derived from them — and for the exclusion case it inverts the mode's purpose,
            // reporting the saving on one line while leaving it inside the cost on another.
            // One single-track fixture per class so every count is exact.
            {
                ITunesTrack One(string loc, int ms, long size)
                    => new ITunesTrack { Location = loc, Name = Path.GetFileName(loc), Artist = "A",
                                         Album = "Alb", Genre = "Rock", TotalTimeMs = ms, SizeBytes = size };

                PreviewPlannerInput In(ITunesTrack t, ExclusionSet excl) => new PreviewPlannerInput
                {
                    Tracks = new List<ITunesTrack> { t },
                    Catalog = new Dictionary<string, TrackEntry>(PathComparer.Instance),
                    Exclusions = excl,
                    MaxDurationSecs = 12000,
                    LongTrackSecs = 1800,
                    ReviewCap = 500,
                    MeasuredRtf = 0.10,
                    Parallelism = 1,
                };
                int Bkt(PreviewPlan p, string cls)
                {
                    var b = p.AutoSkip.Find(x => x.Class == cls);
                    return b == null ? 0 : b.Count;
                }

                // baseline: an ordinary new track IS new work, so a gate that over-fires is
                // caught by the same fixture family rather than passing silently.
                var okPlan = PreviewPlanner.Build(In(One(@"D:\M\ordinary.flac", 180000, 5_000_000), ExclusionSet.Empty));
                Assert(okPlan.Estimate.NewTracks == 1, $"preview accounting: an ordinary new track counts as new work (got {okPlan.Estimate.NewTracks})");
                Assert(okPlan.Estimate.NewBytes == 5_000_000, $"preview accounting: an ordinary new track contributes its bytes (got {okPlan.Estimate.NewBytes})");

                // rule-excluded: FilterExclusions drops it, so it is never analyzed and never
                // gets an mbxmoods.json entry (spec §10a Layering).
                var accExcl = ExclusionSet.FromJson(
                    "{\"schemaVersion\":1,\"rules\":[{\"kind\":\"folder\",\"action\":\"exclude\",\"pattern\":\"\\\\Podcasts\\\\**\"}]}",
                    out var accExclErr);
                Assert(accExclErr == null, "preview accounting: exclusion fixture parses");
                var exPlan = PreviewPlanner.Build(In(One(@"D:\Podcasts\ep.mp3", 3000000, 30_000_000), accExcl));
                Assert(exPlan.Estimate.NewTracks == 0, $"preview accounting: a rule-excluded new track contributes nothing to new work (got {exPlan.Estimate.NewTracks})");
                Assert(exPlan.Estimate.NewBytes == 0, $"preview accounting: a rule-excluded new track contributes no bytes (got {exPlan.Estimate.NewBytes})");
                Assert(exPlan.Estimate.EtaBasis == "unavailable", "preview accounting: with only excluded work there is no audio to estimate, so the ETA is unavailable");
                // ...and it must STILL be reviewable, which is what makes the decision reversible.
                Assert(exPlan.Review.Count == 1 && exPlan.Review[0].CurrentDecision == "excluded",
                    $"preview accounting: an excluded track is still listed for review with its decision (got {exPlan.Review.Count} row(s))");

                // DSD: structurally unanalyzable in every scan mode, and never checked here at
                // all before the shared StructuralSkipReason classifier was wired in.
                var dsdPlan = PreviewPlanner.Build(In(One(@"D:\M\hires.dsf", 300000, 200_000_000), ExclusionSet.Empty));
                Assert(Bkt(dsdPlan, "dsd") == 1, $"preview accounting: a DSD track lands in the dsd autoSkip bucket (got {Bkt(dsdPlan, "dsd")})");
                Assert(dsdPlan.Estimate.NewTracks == 0, $"preview accounting: a DSD track contributes nothing to new work (got {dsdPlan.Estimate.NewTracks})");
                Assert(dsdPlan.Review.Count == 0, $"preview accounting: a structural DSD skip is not a review candidate (got {dsdPlan.Review.Count})");
                // the classifier is shared, so video/playlist keep their existing class names
                var vidPlan = PreviewPlanner.Build(In(One(@"D:\M\clip.mkv", 180000, 5_000_000), ExclusionSet.Empty));
                Assert(Bkt(vidPlan, "video") == 1, $"preview accounting: video keeps its autoSkip class name through the shared classifier (got {Bkt(vidPlan, "video")})");
                var plsPlan = PreviewPlanner.Build(In(One(@"D:\M\set.wpl", 180000, 1_000), ExclusionSet.Empty));
                Assert(Bkt(plsPlan, "nonAudio") == 1, $"preview accounting: playlists keep the nonAudio autoSkip class name (got {Bkt(plsPlan, "nonAudio")})");

                // speech-labelled: Phase 3 — a label is evidence, not a filter, so a scan
                // analyzes it and the estimate must count it as new work like any other track.
                var pod = One(@"D:\M\show.mp3", 3000000, 30_000_000);
                pod.IsSpeech = true;
                pod.SpeechReason = "Genre=Podcast";
                var podPlan = PreviewPlanner.Build(In(pod, ExclusionSet.Empty));
                Assert(podPlan.Estimate.NewTracks == 1, $"preview accounting: a speech-labelled new track now contributes to new work (got {podPlan.Estimate.NewTracks})");
                Assert(podPlan.Review.Count == 1 && podPlan.Review[0].Reasons.Contains("speech-labelled"),
                    "preview accounting: a speech-labelled track is still surfaced for review");

                // --- preview honors the injected cache-hit classifier, matching the scan (I1) ---
                // A catalog entry WITH features but not "current" (fails the re-extract canary)
                // re-analyzes in the scan, so preview must count it as new work too. The default
                // Features != null classifier would hide it (the refresh-features under-report).
                var lgLoc = @"D:\M\legacy.flac";
                var lgTrk = One(lgLoc, 180000, 5_000_000);
                var lgCat = new Dictionary<string, TrackEntry>(PathComparer.Instance)
                {
                    [lgLoc] = new TrackEntry { Features = new TrackFeatures { FilePath = lgLoc } }
                };
                PreviewPlannerInput LgIn(Func<TrackEntry?, bool>? isAnalyzed) => new PreviewPlannerInput
                {
                    Tracks = new List<ITunesTrack> { lgTrk }, Catalog = lgCat, Exclusions = ExclusionSet.Empty,
                    MaxDurationSecs = 12000, LongTrackSecs = 1800, ReviewCap = 500, MeasuredRtf = 0.10,
                    Parallelism = 1, IsAnalyzed = isAnalyzed,
                };
                Assert(PreviewPlanner.Build(LgIn(null)).Estimate.NewTracks == 0,
                    "preview-classifier: default (Features != null) treats an entry with features as analyzed");
                Assert(PreviewPlanner.Build(LgIn(_ => false)).Estimate.NewTracks == 1,
                    "preview-classifier: injected classifier makes a non-current entry new work, matching the scan (I1)");
            }

            // --- --preview must never truncate a file that isn't a preview (whole-branch fix
            // wave). Proven live before the fix: `--preview <path-to>/mbxmoods.json` replaced
            // the mood catalog with preview JSON and exited 0. The arg parser now only claims
            // a JSON-looking token, and this guard is the second line of defence for the case
            // that still binds. Expect two stderr lines from the refusal below — they are the
            // guard working, not a failure.
            {
                var gdir = Path.Combine(Path.GetTempPath(), $".truedat-selftest-pvguard-{Guid.NewGuid():N}");
                Directory.CreateDirectory(gdir);
                try
                {
                    string why;

                    var xmlTarget = Path.Combine(gdir, "iTunes Music Library.xml");
                    File.WriteAllText(xmlTarget, "<?xml version=\"1.0\"?><plist></plist>");
                    Assert(PreviewWriter.IsProtectedTarget(xmlTarget, out why),
                        "previewguard: an .xml target is refused (the library is the input)");

                    // --- catalog resolution for the read-only JSON modes -------------------
                    // Reported by a user on 0.5.4.4: a bare `--list-missing-smfm` fell back to
                    // the auto-discovered library XML, File.Exists passed, the JSON reader hit
                    // '<', and truedat declared their LIBRARY corrupt, wrote a .corrupt copy of
                    // it, and printed advice to delete it. The catalog must be derived from the
                    // library's DIRECTORY, never be the library file.
                    string? resolveWhy;
                    var derived = ResolveMoodsCatalog(null, xmlTarget, out resolveWhy);
                    Assert(resolveWhy == null, "catalogpath: an auto-discovered library is not a refusal");
                    Assert(derived == Path.Combine(gdir, "mbxmoods.json"),
                        $"catalogpath: no explicit path derives the catalog beside the library (got {derived})");
                    Assert(!string.Equals(derived, xmlTarget, StringComparison.OrdinalIgnoreCase),
                        "catalogpath: the resolved catalog is never the library XML itself");

                    var namedXml = ResolveMoodsCatalog(xmlTarget, null, out resolveWhy);
                    Assert(namedXml == null && resolveWhy != null,
                        "catalogpath: naming an .xml explicitly is refused, not parsed as JSON");
                    Assert(resolveWhy != null && resolveWhy.IndexOf("not a mood catalog", StringComparison.OrdinalIgnoreCase) >= 0,
                        "catalogpath: the refusal says what the file actually is");

                    var fromDir = ResolveMoodsCatalog(gdir, null, out resolveWhy);
                    Assert(resolveWhy == null && fromDir == Path.Combine(gdir, "mbxmoods.json"),
                        "catalogpath: a directory resolves to the catalog inside it");

                    var explicitJson = Path.Combine(gdir, "elsewhere.json");
                    Assert(ResolveMoodsCatalog(explicitJson, xmlTarget, out resolveWhy) == explicitJson && resolveWhy == null,
                        "catalogpath: an explicit catalog path wins over the library");

                    var catalogTarget = Path.Combine(gdir, "mbxmoods.json");
                    File.WriteAllText(catalogTarget, "{\"tracks\":{}}");
                    Assert(PreviewWriter.IsProtectedTarget(catalogTarget, out why),
                        "previewguard: a mood catalog target is refused");

                    // realistic shape: "tracks" is the fourth top-level property
                    var realisticTarget = Path.Combine(gdir, "real-moods.json");
                    File.WriteAllText(realisticTarget,
                        "{\"version\":\"1.0\",\"generatedAt\":\"x\",\"trackCount\":1,\"tracks\":{\"D:\\\\a.flac\":{}}}");
                    Assert(PreviewWriter.IsProtectedTarget(realisticTarget, out why),
                        "previewguard: a catalog whose tracks property is not first is still recognised");

                    var previewTarget = Path.Combine(gdir, "preview.json");
                    File.WriteAllText(previewTarget, "{\"kind\":\"preview\",\"review\":[]}");
                    Assert(!PreviewWriter.IsProtectedTarget(previewTarget, out why),
                        "previewguard: an existing preview.json target is allowed");

                    var absentTarget = Path.Combine(gdir, "does-not-exist.json");
                    Assert(!PreviewWriter.IsProtectedTarget(absentTarget, out why),
                        "previewguard: a nonexistent target is allowed");

                    // tolerant read: a file that is not JSON at all is not a catalog
                    var junkTarget = Path.Combine(gdir, "junk.json");
                    File.WriteAllText(junkTarget, "not json at all {{{");
                    Assert(!PreviewWriter.IsProtectedTarget(junkTarget, out why),
                        "previewguard: an unparseable file is not a catalog and may be overwritten");

                    // the invariant that actually matters: the refusal writes NOTHING.
                    var before = File.ReadAllBytes(catalogTarget);
                    Assert(!PreviewWriter.TryWritePreviewJson(catalogTarget, new PreviewPlan()),
                        "previewguard: the guarded write refuses a mood catalog");
                    var after = File.ReadAllBytes(catalogTarget);
                    Assert(Convert.ToBase64String(before) == Convert.ToBase64String(after),
                        "previewguard: the refused target is byte-identical afterwards");
                    // and a legitimate target still gets written
                    var okTarget = Path.Combine(gdir, "out.json");
                    Assert(PreviewWriter.TryWritePreviewJson(okTarget, new PreviewPlan()),
                        "previewguard: the guarded write still writes an ordinary preview target");
                    Assert(File.Exists(okTarget), "previewguard: the allowed target actually receives the preview");
                }
                finally { try { Directory.Delete(gdir, true); } catch { } }
            }

            // --- MedianCatalogRtf (Phase 2a Task 4 fix round 1): --preview's ETA
            // real-time-factor input, pinned directly. Every preview live-check in the
            // first pass ran against an empty catalog, so this logic — the median math,
            // the null/zero skip, the zero-samples fallback — was never actually
            // exercised by anything but a human eyeballing one manual run.
            {
                ITunesTrack RtfTrk(string loc, int ms) => new ITunesTrack { Location = loc, TotalTimeMs = ms };
                var rtfTracks = new List<ITunesTrack>
                {
                    RtfTrk(@"D:\M\a.flac", 40000),
                    RtfTrk(@"D:\M\b.flac", 40000),
                };

                // Two usable samples: 20s/40s = 0.5, 60s/40s = 1.5. Upper median is 1.5.
                // A mean would give 1.0 and would still "look right" — asserting the exact
                // value is what catches a median-to-mean regression that a loose sanity
                // check (">0", "not NaN") would let straight through.
                var catalogTwo = new Dictionary<string, TrackEntry>(PathComparer.Instance);
                catalogTwo[@"D:\M\a.flac"] = new TrackEntry { AnalysisDurationSecs = 20 };
                catalogTwo[@"D:\M\b.flac"] = new TrackEntry { AnalysisDurationSecs = 60 };
                Assert(MedianCatalogRtf(rtfTracks, catalogTwo) == 1.5,
                    $"preview: median RTF is the upper median of {{0.5, 1.5}}, not the mean (got {MedianCatalogRtf(rtfTracks, catalogTwo)})");

                // A missing AnalysisDurationSecs (null) is skipped, not treated as 0.
                var catalogNull = new Dictionary<string, TrackEntry>(PathComparer.Instance);
                catalogNull[@"D:\M\a.flac"] = new TrackEntry { AnalysisDurationSecs = null };
                catalogNull[@"D:\M\b.flac"] = new TrackEntry { AnalysisDurationSecs = 60 };
                Assert(MedianCatalogRtf(rtfTracks, catalogNull) == 1.5,
                    $"preview: an entry with no analysisDuration is skipped, not treated as 0 (got {MedianCatalogRtf(rtfTracks, catalogNull)})");

                // A zero or negative AnalysisDurationSecs is skipped too, so it cannot
                // poison the median toward "instant".
                var rtfTracksThree = new List<ITunesTrack>
                {
                    RtfTrk(@"D:\M\a.flac", 40000),
                    RtfTrk(@"D:\M\b.flac", 40000),
                    RtfTrk(@"D:\M\c.flac", 40000),
                };
                var catalogZero = new Dictionary<string, TrackEntry>(PathComparer.Instance);
                catalogZero[@"D:\M\a.flac"] = new TrackEntry { AnalysisDurationSecs = 0 };
                catalogZero[@"D:\M\b.flac"] = new TrackEntry { AnalysisDurationSecs = -5 };
                catalogZero[@"D:\M\c.flac"] = new TrackEntry { AnalysisDurationSecs = 60 };
                Assert(MedianCatalogRtf(rtfTracksThree, catalogZero) == 1.5,
                    $"preview: zero/negative analysisDuration cannot poison the median (got {MedianCatalogRtf(rtfTracksThree, catalogZero)})");

                // No usable samples at all -> 0, not a guess.
                var catalogEmpty = new Dictionary<string, TrackEntry>(PathComparer.Instance);
                Assert(MedianCatalogRtf(rtfTracks, catalogEmpty) == 0,
                    "preview: no usable samples in the catalog -> 0");

                // And that 0 must make Build() honestly report the ETA as unavailable
                // rather than a fabricated instant estimate.
                var noHistoryInput = new PreviewPlannerInput
                {
                    Tracks = rtfTracks,
                    Catalog = catalogEmpty,
                    MeasuredRtf = MedianCatalogRtf(rtfTracks, catalogEmpty),
                    MaxDurationSecs = 12000,
                    LongTrackSecs = 1800,
                    ReviewCap = 500,
                    Parallelism = 2,
                };
                var noHistoryPlan = PreviewPlanner.Build(noHistoryInput);
                Assert(noHistoryPlan.Estimate.EtaSecs < 0, "preview: zero RTF from an empty catalog leaves the ETA unestimable (negative sentinel)");
                Assert(noHistoryPlan.Estimate.EtaBasis == "unavailable", $"preview: zero RTF reports basis unavailable (got {noHistoryPlan.Estimate.EtaBasis})");
            }

            // --- the ETA basis label is INJECTED, not relabelled after the fact (whole-branch
            // fix wave). The old shape had the dispatch overwrite "measured-rtf" with
            // "catalog-rtf" afterwards, duplicating the planner's string literal in a second
            // file and needing a test that copied the relabel rather than invoking it. Now the
            // caller states its basis up front and the planner honours it, so there is one
            // literal per basis and nothing to keep in sync.
            {
                var labelInput = new PreviewPlannerInput
                {
                    Tracks = new List<ITunesTrack> { new ITunesTrack { Location = @"D:\M\a.flac", TotalTimeMs = 40000 } },
                    Catalog = new Dictionary<string, TrackEntry>(PathComparer.Instance),
                    MeasuredRtf = 0.5,
                    MaxDurationSecs = 12000,
                    LongTrackSecs = 1800,
                    ReviewCap = 500,
                    Parallelism = 2,
                    EtaBasisLabel = "catalog-rtf",
                };
                var labelPlan = PreviewPlanner.Build(labelInput);
                Assert(labelPlan.Estimate.EtaBasis == "catalog-rtf", $"preview: the planner emits the caller's ETA basis label verbatim (got {labelPlan.Estimate.EtaBasis})");
                // An unestimable ETA overrides the label — a basis name for an estimate that
                // does not exist would be worse than no name at all.
                labelInput.MeasuredRtf = 0;
                Assert(PreviewPlanner.Build(labelInput).Estimate.EtaBasis == "unavailable",
                    "preview: an unestimable ETA reports 'unavailable', never the caller's label");
            }

            // --- PreviewWriter: preview.json shape (Phase 2a) ---
            {
                var dir = Path.Combine(Path.GetTempPath(), $".truedat-selftest-prev-{Guid.NewGuid():N}");
                Directory.CreateDirectory(dir);
                try
                {
                    var plan = new PreviewPlan
                    {
                        XmlPath = @"D:\M\iTunes Music Library.xml",
                        MoodsPath = @"D:\M\mbxmoods.json",
                        ExclusionsPath = @"D:\M\mbxmoods-exclude.json",
                        ReviewTotal = 3,
                        ReviewTruncated = true,
                        SniffedCount = 2,
                    };
                    plan.Limits.MaxDurationSecs = 12000;
                    plan.Limits.MaxDurationSource = "--max-duration";
                    plan.Limits.LongTrackSecs = 1800;
                    plan.Limits.ExtractorNote = "note";
                    plan.Counts.LibraryTotal = 100;
                    plan.Counts.Analyzed = 60;
                    plan.Counts.Excluded = 5;
                    plan.Counts.AwaitingReview = 3;
                    plan.Estimate.NewTracks = 40;
                    plan.Estimate.NewBytes = 1234567;
                    plan.Estimate.CachedTracks = 60;
                    plan.Estimate.EtaSecs = 987.5;
                    plan.Estimate.EtaBasis = "catalog-rtf";
                    plan.AutoSkip.Add(new PreviewBucket { Class = "video", Count = 7 });
                    plan.Rules.Add(new PreviewRuleStat { Rule = @"folder=\Podcasts\**", Action = "exclude", MatchCount = 5 });
                    plan.Genres.Add(new PreviewGenre { Name = "Rock", Tracks = 80, TotalSecs = 20000 });
                    plan.Review.Add(new PreviewCandidate
                    {
                        Path = @"D:\M\long.flac", Artist = "A", Title = "T", Album = "Alb", Genre = "Rock",
                        Codec = "flac", DurationSecs = 2400, State = "analyzed",
                        Reasons = new List<string> { "long", "marker:PCST" },
                        SpeechLikely = "no", OverLimit = false, CurrentDecision = "undecided",
                    });

                    var outPath = Path.Combine(dir, "preview.json");
                    PreviewWriter.WritePreviewJson(outPath, plan);
                    Assert(File.Exists(outPath), "previewjson: file written");

                    var root = JsonNode.Parse(File.ReadAllText(outPath))!;
                    // review-surface envelope — preview.json IS the manifest the hub serves
                    Assert(root["kind"]!.GetValue<string>() == "preview", "previewjson: kind is preview");
                    Assert(root["id"] != null && root["id"]!.GetValue<string>().Length > 0, "previewjson: carries an id");
                    Assert(root["schemaVersion"]!.GetValue<int>() == 1, "previewjson: schemaVersion 1");
                    Assert(root["generated"] != null, "previewjson: carries a generated timestamp");
                    Assert(root["source"]!["moodsPath"]!.GetValue<string>() == @"D:\M\mbxmoods.json", "previewjson: source names the moods file");
                    Assert(root["source"]!["exclusionsPath"] != null, "previewjson: source names the exclusion file");
                    // Default (no page name): reviewHtml stays absent — never point at a file
                    // that does not exist.
                    Assert(root["source"]!["reviewHtml"] == null, "previewjson: no reviewHtml pointer when no page name given");

                    // Phase 2b: with a page name, the manifest names the co-emitted page.
                    var namedPath = Path.Combine(dir, "preview-named.json");
                    PreviewWriter.WritePreviewJson(namedPath, plan, PreviewWriter.PreviewHtmlFileName);
                    var namedRoot = JsonNode.Parse(File.ReadAllText(namedPath))!;
                    Assert(namedRoot["source"]!["reviewHtml"]!.GetValue<string>() == "mbxmoods-preview.html",
                        "previewjson: reviewHtml names the co-emitted page when a page name is passed");

                    // The page embeds the SAME manifest and is a real self-contained document.
                    var pagePath = Path.Combine(dir, PreviewWriter.PreviewHtmlFileName);
                    PreviewWriter.WritePreviewHtml(pagePath, plan);
                    var pageText = File.ReadAllText(pagePath);
                    Assert(!pageText.Contains("__DATA__"), "previewhtml: data placeholder is substituted");
                    Assert(pageText.Contains("<!doctype html>"), "previewhtml: is a full HTML document");
                    Assert(pageText.Contains("\"kind\": \"preview\""), "previewhtml: embeds the preview manifest");
                    Assert(!pageText.Contains("http://") && !pageText.Contains("https://"),
                        "previewhtml: no external references (self-contained)");

                    Assert(root["limits"]!["maxDurationSecs"]!.GetValue<int>() == 12000, "previewjson: limits echo the ceiling");
                    Assert(root["limits"]!["maxDurationSource"]!.GetValue<string>() == "--max-duration", "previewjson: limits name where the ceiling came from");
                    Assert(root["limits"]!["longTrackSecs"]!.GetValue<int>() == 1800, "previewjson: limits carry the review threshold");
                    Assert(root["counts"]!["awaitingReview"]!.GetValue<int>() == 3, "previewjson: counts block for the hub badge");
                    Assert(root["estimate"]!["etaBasis"]!.GetValue<string>() == "catalog-rtf", "previewjson: estimate names its basis");
                    Assert(root["reviewTotal"]!.GetValue<int>() == 3, "previewjson: reviewTotal is the true count");
                    Assert(root["reviewTruncated"]!.GetValue<bool>(), "previewjson: truncation is declared");
                    Assert(root["reviewSort"]!.GetValue<string>() == "durationSecs-desc", "previewjson: sort order is declared, not guessed");
                    Assert(root["sniffedCount"]!.GetValue<int>() == 2, "previewjson: sniff count is surfaced");

                    var row = root["review"]!.AsArray()[0]!;
                    Assert(row["path"]!.GetValue<string>() == @"D:\M\long.flac", "previewjson: candidate path");
                    Assert(row["durationSecs"]!.GetValue<int>() == 2400, "previewjson: candidate duration (client-side re-filtering needs it)");
                    Assert(row["reasons"]!.AsArray().Count == 2, "previewjson: candidate reasons are a list");
                    Assert(row["currentDecision"]!.GetValue<string>() == "undecided", "previewjson: candidate carries its current decision");
                    Assert(root["autoSkip"]!.AsArray()[0]!["class"]!.GetValue<string>() == "video", "previewjson: autoSkip buckets");
                    Assert(root["rules"]!.AsArray()[0]!["matchCount"]!.GetValue<int>() == 5, "previewjson: rule hit counts");
                    Assert(root["genres"]!.AsArray()[0]!["name"]!.GetValue<string>() == "Rock", "previewjson: genre histogram");

                    // a negative ETA means "not estimable" and must be OMITTED, never serialised as -1
                    plan.Estimate.EtaSecs = -1;
                    plan.Estimate.EtaBasis = "unavailable";
                    var outPath2 = Path.Combine(dir, "preview2.json");
                    PreviewWriter.WritePreviewJson(outPath2, plan);
                    var root2 = JsonNode.Parse(File.ReadAllText(outPath2))!;
                    Assert(root2["estimate"]!["etaSecs"] == null, "previewjson: unestimable ETA is omitted, not written as -1");
                    Assert(root2["estimate"]!["etaBasis"]!.GetValue<string>() == "unavailable", "previewjson: basis still says unavailable");
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            }

            // --- ExclusionSet: rule parsing + matching (spec 2026-07-24) ---
            {
                string Rules(string inner) => "{\"schemaVersion\":1,\"rules\":[" + inner + "]}";
                string Folder(string action, string pattern) =>
                    "{\"kind\":\"folder\",\"action\":\"" + action + "\",\"pattern\":\"" + pattern.Replace("\\", "\\\\") + "\"}";
                string Genre(string action, string value) =>
                    "{\"kind\":\"genre\",\"action\":\"" + action + "\",\"value\":\"" + value + "\"}";
                string FileRule(string action, string path) =>
                    "{\"kind\":\"file\",\"action\":\"" + action + "\",\"path\":\"" + path.Replace("\\", "\\\\") + "\"}";

                ExclusionSet Set(string inner)
                {
                    var s = ExclusionSet.FromJson(Rules(inner), out var err);
                    Assert(err == null, $"exclusion: parse without fatal error ({err})");
                    return s;
                }

                // fragment folder rule: root-independent, boundary-aligned
                var frag = Set(Folder("exclude", @"\Podcasts\**"));
                Assert(frag.IsExcluded(@"D:\Music\Podcasts\JRE 2100.mp3", null, out var fragWhy), "exclusion: fragment folder rule matches");
                Assert(fragWhy == @"folder=\Podcasts\**", $"exclusion: reason names the rule (got {fragWhy})");
                Assert(frag.IsExcluded(@"P:\Library\Podcasts\JRE 2100.mp3", null, out _), "exclusion: fragment matches a different root (mirrored library)");
                Assert(frag.IsExcluded(@"D:/Music/Podcasts/JRE 2100.mp3", null, out _), "exclusion: fragment matches forward slashes");
                Assert(frag.IsExcluded(@"D:\Music\PODCASTS\x.mp3", null, out _), "exclusion: fragment match is case-insensitive");
                Assert(!frag.IsExcluded(@"D:\Music\MyPodcastsBackup\x.mp3", null, out _), "exclusion: fragment is boundary-aligned, not substring");
                Assert(!frag.IsExcluded(@"D:\Music\Rock\x.mp3", null, out _), "exclusion: unrelated path not excluded");

                // absolute folder rule: root-specific
                var abs = Set(Folder("exclude", @"D:\Music\Podcasts\**"));
                Assert(abs.IsExcluded(@"D:\Music\Podcasts\a.mp3", null, out _), "exclusion: absolute folder rule matches its root");
                Assert(!abs.IsExcluded(@"P:\Music\Podcasts\a.mp3", null, out _), "exclusion: absolute folder rule does not match another root");

                // genre rule: exact, case-insensitive, trimmed, NEVER substring
                var gen = Set(Genre("exclude", "Podcast"));
                Assert(gen.IsExcluded(@"D:\a.mp3", "Podcast", out var genWhy), "exclusion: genre rule matches");
                Assert(genWhy == "genre=Podcast", $"exclusion: genre reason names the rule (got {genWhy})");
                Assert(gen.IsExcluded(@"D:\a.mp3", "podcast", out _), "exclusion: genre match is case-insensitive");
                Assert(gen.IsExcluded(@"D:\a.mp3", "  Podcast  ", out _), "exclusion: genre match is trimmed");
                Assert(!gen.IsExcluded(@"D:\a.mp3", "Comedy Podcast", out _), "exclusion: genre is exact, not substring");
                Assert(!gen.IsExcluded(@"D:\a.mp3", null, out _), "exclusion: null genre never matches a genre rule");
                // HasGenreRules gates whether the per-file scan paths spend a TagLib open
                // reading genre before their pre-cache exclusion check. Those paths used to
                // pass a hard-coded null, which made every genre rule silently inert on the
                // autoscan path. If this flag ever reports false while a genre rule exists,
                // the rules go inert again and nothing else fails — so pin it directly.
                Assert(gen.HasGenreRules, "exclusion: a genre rule sets HasGenreRules (gates the per-file genre read)");
                Assert(!frag.HasGenreRules, "exclusion: a folder-only set does not set HasGenreRules (no needless IO)");
                Assert(!ExclusionSet.Empty.HasGenreRules, "exclusion: the empty set does not set HasGenreRules");

                // file rule
                var fil = Set(FileRule("exclude", @"D:\Music\setlist.mp3"));
                Assert(fil.IsExcluded(@"D:\Music\setlist.mp3", null, out _), "exclusion: file rule matches exact path");
                Assert(fil.IsExcluded(@"d:/music/SETLIST.mp3", null, out _), "exclusion: file rule normalizes separators and case");
                Assert(!fil.IsExcluded(@"D:\Music\setlist2.mp3", null, out _), "exclusion: file rule does not match a different path");

                // include always wins, regardless of order in the file
                var incAfter = Set(Folder("exclude", @"\Podcasts\**") + "," + Folder("include", @"\Podcasts\KEXP\**"));
                Assert(!incAfter.IsExcluded(@"D:\Music\Podcasts\KEXP\session.flac", null, out _), "exclusion: include rule wins over exclude (include listed second)");
                Assert(incAfter.IsExcluded(@"D:\Music\Podcasts\JRE.mp3", null, out _), "exclusion: sibling still excluded");
                var incBefore = Set(Folder("include", @"\Podcasts\KEXP\**") + "," + Folder("exclude", @"\Podcasts\**"));
                Assert(!incBefore.IsExcluded(@"D:\Music\Podcasts\KEXP\session.flac", null, out _), "exclusion: include rule wins over exclude (include listed first)");
                var incFile = Set(Genre("exclude", "Podcast") + "," + FileRule("include", @"D:\Music\kexp.flac"));
                Assert(!incFile.IsExcluded(@"D:\Music\kexp.flac", "Podcast", out _), "exclusion: file include rescues a genre exclude");
                // IsIncluded must separate "deliberately included" from "never mentioned"
                Assert(incAfter.IsIncluded(@"D:\Music\Podcasts\KEXP\session.flac", null), "exclusion: IsIncluded true for an include match");
                Assert(!incAfter.IsIncluded(@"D:\Music\Podcasts\JRE.mp3", null), "exclusion: IsIncluded false for an excluded track");
                Assert(!incAfter.IsIncluded(@"D:\Music\Rock\song.flac", null), "exclusion: IsIncluded false for an unmentioned track");

                // match counting drives the stale-rule report
                var counted = Set(Folder("exclude", @"\Podcasts\**") + "," + Folder("exclude", @"\Gone\**"));
                counted.IsExcluded(@"D:\Music\Podcasts\a.mp3", null, out _);
                counted.IsExcluded(@"D:\Music\Podcasts\b.mp3", null, out _);
                Assert(counted.Rules[0].MatchCount == 2, $"exclusion: matched rule counts hits (got {counted.Rules[0].MatchCount})");
                Assert(counted.Rules[1].MatchCount == 0, "exclusion: unmatched rule stays at zero (stale-rule signal)");

                // validation: bad rules are skipped, counted, and diagnosed - not fatal
                var partial = ExclusionSet.FromJson(Rules(
                    Folder("exclude", @"\Podcasts\**") + ","
                    + "{\"kind\":\"folder\",\"action\":\"exclude\",\"pattern\":\"\\\\Podcasts\\\\\"}" + ","
                    + "{\"kind\":\"wombat\",\"action\":\"exclude\",\"value\":\"x\"}" + ","
                    + "{\"kind\":\"genre\",\"action\":\"maybe\",\"value\":\"x\"}" + ","
                    + "{\"kind\":\"genre\",\"action\":\"exclude\",\"value\":\"\"}"), out var partialErr);
                Assert(partialErr == null, "exclusion: some-invalid-rules is not fatal");
                Assert(partial.Rules.Count == 1, $"exclusion: only valid rules are kept (got {partial.Rules.Count})");
                Assert(partial.InvalidRuleCount == 4, $"exclusion: invalid rules are counted (got {partial.InvalidRuleCount})");
                Assert(partial.Diagnostics.Count == 4, "exclusion: every invalid rule gets a diagnostic");

                // fatal: unparseable, and non-empty-but-nothing-valid
                ExclusionSet.FromJson("{ this is not json", out var brokenErr);
                Assert(brokenErr != null, "exclusion: unparseable JSON is fatal");
                ExclusionSet.FromJson(Rules("{\"kind\":\"wombat\",\"action\":\"exclude\",\"value\":\"x\"}"), out var noneValidErr);
                Assert(noneValidErr != null, "exclusion: non-empty rules with zero valid is fatal");

                // empty is legitimate and excludes nothing
                var emptySet = ExclusionSet.FromJson("{\"schemaVersion\":1,\"rules\":[]}", out var emptyErr);
                Assert(emptyErr == null, "exclusion: empty rules array is not an error");
                Assert(emptySet.IsEmpty, "exclusion: empty rules array yields IsEmpty");
                Assert(!emptySet.IsExcluded(@"D:\Music\Podcasts\a.mp3", "Podcast", out _), "exclusion: empty set excludes nothing");
                Assert(!ExclusionSet.Empty.IsExcluded(@"D:\a.mp3", "Podcast", out _), "exclusion: Empty excludes nothing");

                // unknown fields are ignored (forward compatibility)
                var futureSet = ExclusionSet.FromJson(
                    "{\"schemaVersion\":9,\"somethingNew\":true,\"rules\":[{\"kind\":\"genre\",\"action\":\"exclude\",\"value\":\"Podcast\",\"futureField\":42}]}",
                    out var futureErr);
                Assert(futureErr == null, "exclusion: unknown top-level and rule fields are tolerated");
                Assert(futureSet.Rules.Count == 1, "exclusion: rule with an unknown field still parses");

                // identity ignores note/sha so merges dedupe correctly (used in Task 3)
                var idA = ExclusionSet.FromJson(Rules("{\"kind\":\"file\",\"action\":\"exclude\",\"path\":\"D:\\\\a.mp3\",\"note\":\"one\"}"), out _).Rules[0];
                var idB = ExclusionSet.FromJson(Rules("{\"kind\":\"file\",\"action\":\"exclude\",\"path\":\"d:/A.MP3\",\"audioStreamSha256\":\"abc\"}"), out _).Rules[0];
                Assert(idA.Identity() == idB.Identity(), "exclusion: identity ignores note/sha and normalizes the path");

                // IMPORTANT (final review, 2026-07-25): IsExcluded folds separators/case
                // but never anchors a relative path — a caller MUST normalize via
                // Path.GetFullPath before matching (this is what the --analyze-file guard
                // was fixed to do, matching --file-list's fullPath guard). Pin the
                // equivalence here: the same rule matches an absolute path and matches
                // the same file expressed relatively, once normalized — and does NOT
                // match the unnormalized relative form, proving why the normalization
                // step is load-bearing rather than cosmetic.
                var savedCwd = Environment.CurrentDirectory;
                var relRoot = Path.Combine(Path.GetTempPath(), $".truedat-selftest-relpath-{Guid.NewGuid():N}");
                Directory.CreateDirectory(Path.Combine(relRoot, "tests"));
                try
                {
                    Environment.CurrentDirectory = relRoot;
                    var relFragRule = Set(Folder("exclude", @"\tests\**"));
                    var absForm = Path.Combine(relRoot, "tests", "x.flac");
                    Assert(relFragRule.IsExcluded(absForm, null, out _), "exclusion: fragment rule matches the absolute form");
                    const string typedRelative = @"tests\x.flac";
                    Assert(!relFragRule.IsExcluded(typedRelative, null, out _),
                        "exclusion: unnormalized relative path misses the same fragment rule (why a caller must normalize first)");
                    Assert(relFragRule.IsExcluded(Path.GetFullPath(typedRelative), null, out _),
                        "exclusion: Path.GetFullPath-normalizing the relative path makes it match, same as the absolute form");
                }
                finally
                {
                    Environment.CurrentDirectory = savedCwd;
                    try { Directory.Delete(relRoot, true); } catch { }
                }
            }

            // --- ExclusionStore: merge semantics (spec 2026-07-24) ---
            {
                var dir = Path.Combine(Path.GetTempPath(), $".truedat-selftest-excl-{Guid.NewGuid():N}");
                Directory.CreateDirectory(dir);
                try
                {
                    var canonical = Path.Combine(dir, "mbxmoods-exclude.json");
                    Assert(ExclusionStore.Resolve(Path.Combine(dir, "mbxmoods.json")) == canonical,
                        "store: resolves beside the moods file");

                    // absent file is not an error and excludes nothing
                    var absent = ExclusionStore.Load(canonical, out var absentErr);
                    Assert(absentErr == null, "store: absent file is not an error");
                    Assert(absent.IsEmpty, "store: absent file yields an empty set");

                    string Delta(string add, string remove) =>
                        "{\"schemaVersion\":1,\"kind\":\"exclusion-decisions\",\"add\":[" + add + "],\"remove\":[" + remove + "]}";
                    const string genrePodcast = "{\"kind\":\"genre\",\"action\":\"exclude\",\"value\":\"Podcast\"}";
                    const string genreAudiobook = "{\"kind\":\"genre\",\"action\":\"exclude\",\"value\":\"Audiobook\"}";

                    // first apply creates the file
                    var r1 = ExclusionStore.Merge(canonical, Delta(genrePodcast, ""), "self-test", out var e1);
                    Assert(e1 == null, $"store: first merge succeeds ({e1})");
                    Assert(r1.Added == 1 && r1.Changed, "store: first merge adds one rule");
                    Assert(r1.BackupPath == null, "store: no backup when there was no file");
                    Assert(File.Exists(canonical), "store: merge created the file");

                    var loaded = ExclusionStore.Load(canonical, out var loadErr);
                    Assert(loadErr == null && loaded.Rules.Count == 1, "store: written file round-trips");
                    Assert(loaded.IsExcluded(@"D:\a.mp3", "Podcast", out _), "store: round-tripped rule still matches");

                    // idempotent: same delta twice changes nothing
                    var r2 = ExclusionStore.Merge(canonical, Delta(genrePodcast, ""), "self-test", out var e2);
                    Assert(e2 == null, "store: repeat merge succeeds");
                    Assert(r2.Added == 0 && r2.AlreadyPresent == 1 && !r2.Changed, "store: repeat merge is a no-op");

                    // adding a second rule backs the old file up
                    var r3 = ExclusionStore.Merge(canonical, Delta(genreAudiobook, ""), "self-test", out _);
                    Assert(r3.Added == 1 && r3.Changed, "store: second rule added");
                    Assert(r3.BackupPath != null && File.Exists(r3.BackupPath), "store: existing file is backed up before rewrite");
                    Assert(ExclusionStore.Load(canonical, out _).Rules.Count == 2, "store: both rules present");

                    // note/sha differences must UPDATE, not duplicate (identity ignores them)
                    var r4 = ExclusionStore.Merge(canonical,
                        Delta("{\"kind\":\"genre\",\"action\":\"exclude\",\"value\":\"Podcast\",\"note\":\"talk\"}", ""), "self-test", out _);
                    Assert(r4.AlreadyPresent == 1 && r4.Added == 0, "store: same identity with a new note is not a duplicate");
                    Assert(ExclusionStore.Load(canonical, out _).Rules.Count == 2, "store: rule count unchanged by the note");

                    // remove
                    var r5 = ExclusionStore.Merge(canonical, Delta("", genrePodcast), "self-test", out _);
                    Assert(r5.Removed == 1 && r5.Changed, "store: remove drops the rule");
                    Assert(ExclusionStore.Load(canonical, out _).Rules.Count == 1, "store: one rule left after remove");

                    // removing something absent is reported, not an error
                    var r6 = ExclusionStore.Merge(canonical, Delta("", genrePodcast), "self-test", out var e6);
                    Assert(e6 == null && r6.NotFound == 1 && !r6.Changed, "store: removing an absent rule is a reported no-op");

                    // invalid decisions document: error, file untouched
                    var before = File.ReadAllText(canonical);
                    ExclusionStore.Merge(canonical, "{ not json", "self-test", out var e7);
                    Assert(e7 != null, "store: unparseable decisions document is an error");
                    Assert(File.ReadAllText(canonical) == before, "store: failed merge leaves the file untouched");
                    ExclusionStore.Merge(canonical, Delta("{\"kind\":\"wombat\",\"action\":\"exclude\",\"value\":\"x\"}", ""), "self-test", out var e8);
                    Assert(e8 != null, "store: decisions document with no valid rules is an error");
                    Assert(File.ReadAllText(canonical) == before, "store: second failed merge also leaves the file untouched");

                    // a corrupt canonical file must not be silently overwritten
                    var corruptDir = Path.Combine(dir, "corrupt");
                    Directory.CreateDirectory(corruptDir);
                    var corrupt = Path.Combine(corruptDir, "mbxmoods-exclude.json");
                    File.WriteAllText(corrupt, "{ broken");
                    ExclusionStore.Merge(corrupt, Delta(genrePodcast, ""), "self-test", out var e9);
                    Assert(e9 != null, "store: refuses to merge into an unparseable canonical file");
                    Assert(File.ReadAllText(corrupt) == "{ broken", "store: corrupt canonical file is left as-is");

                    // CRITICAL (final review, 2026-07-25): a canonical file with a valid
                    // rule PLUS a rule Load could not parse (unknown kind — the stand-in
                    // for a future rule kind a newer MBXHub might write) must not be
                    // silently rewritten down to just the valid rule. Hand-write the file
                    // directly since Merge itself now refuses to ever produce one shaped
                    // like this.
                    var partialDir = Path.Combine(dir, "partial");
                    Directory.CreateDirectory(partialDir);
                    var partialCanonical = Path.Combine(partialDir, "mbxmoods-exclude.json");
                    const string partialJson =
                        "{\"schemaVersion\":1,\"rules\":["
                        + "{\"kind\":\"genre\",\"action\":\"exclude\",\"value\":\"Podcast\"},"
                        + "{\"kind\":\"artist\",\"action\":\"exclude\",\"value\":\"Some Artist\"}"
                        + "]}";
                    File.WriteAllText(partialCanonical, partialJson);
                    var preLoad = ExclusionStore.Load(partialCanonical, out var preLoadErr);
                    Assert(preLoadErr == null && preLoad.Rules.Count == 1 && preLoad.InvalidRuleCount == 1,
                        "store: sanity check — hand-written partial file loads with one valid, one invalid rule");

                    var beforeBytes = File.ReadAllBytes(partialCanonical);
                    // Count .bak files specifically, not every sibling: Merge legitimately
                    // creates the zero-byte .lock sidecar (I-1) even on a refusal, and a
                    // whole-directory count would read that as "a backup was taken".
                    var filesBeforeMerge = Directory.GetFiles(partialDir, "*.bak.*").Length;
                    var rPartial = ExclusionStore.Merge(partialCanonical, Delta(genreAudiobook, ""), "self-test", out var ePartial);
                    Assert(ePartial != null, "CRITICAL: merge refuses when the canonical file has an unparseable rule");
                    Assert(ePartial != null && ePartial.Contains("1") && ePartial.ToLowerInvariant().Contains("artist"),
                        $"CRITICAL: refusal names the count and lists the diagnostic ({ePartial})");
                    Assert(rPartial.BackupPath == null, "CRITICAL: no backup path reported on refusal");
                    Assert(File.ReadAllBytes(partialCanonical).SequenceEqual(beforeBytes),
                        "CRITICAL: canonical file is byte-identical after the refused merge");
                    Assert(Directory.GetFiles(partialDir, "*.bak.*").Length == filesBeforeMerge,
                        "CRITICAL: no .bak file was created by the refused merge");

                    // --- apply-result.json (Phase 2a) ---
                    {
                        var okReport = new MergeReport { Added = 2, Removed = 1, AlreadyPresent = 3, NotFound = 0, Changed = true, BackupPath = @"C:\x\mbxmoods-exclude.json.bak.20260725.010203" };
                        var resPath = ExclusionStore.WriteApplyResult(canonical, okReport, null);
                        Assert(File.Exists(resPath), "applyresult: file written on success");
                        var r = JsonNode.Parse(File.ReadAllText(resPath))!;
                        Assert(r["kind"]!.GetValue<string>() == "exclusion-apply-result", "applyresult: kind names itself");
                        Assert(r["schemaVersion"]!.GetValue<int>() == 1, "applyresult: schemaVersion 1");
                        Assert(r["ok"]!.GetValue<bool>(), "applyresult: ok true on success");
                        Assert(r["added"]!.GetValue<int>() == 2 && r["removed"]!.GetValue<int>() == 1, "applyresult: carries the counts");
                        Assert(r["alreadyPresent"]!.GetValue<int>() == 3, "applyresult: carries alreadyPresent");
                        Assert(r["changed"]!.GetValue<bool>(), "applyresult: carries changed");
                        Assert(r["backupPath"]!.GetValue<string>() == okReport.BackupPath, "applyresult: names the backup");
                        Assert(r["exclusionsPath"]!.GetValue<string>() == canonical, "applyresult: names the file it applied to");
                        Assert(r["error"] == null, "applyresult: no error key on success");

                        // failure must ALSO produce a result file — that is when the hub needs it most
                        var failPath = ExclusionStore.WriteApplyResult(canonical, new MergeReport(), "refusing: 1 existing rule(s) failed to parse");
                        Assert(File.Exists(failPath), "applyresult: file written on failure");
                        var f = JsonNode.Parse(File.ReadAllText(failPath))!;
                        Assert(!f["ok"]!.GetValue<bool>(), "applyresult: ok false on failure");
                        Assert(f["error"]!.GetValue<string>().Contains("refusing"), "applyresult: failure carries the reason");
                        Assert(f["changed"]!.GetValue<bool>() == false, "applyresult: failure reports nothing changed");
                        Assert(f["backupPath"] == null, "applyresult: no backup key when none was made");
                    }

                    // Delta-side twin: an "add" array with one good rule and one typo'd
                    // one must apply the good one AND report the dropped one, not swallow
                    // it silently.
                    var diagDir = Path.Combine(dir, "diag");
                    Directory.CreateDirectory(diagDir);
                    var diagCanonical = Path.Combine(diagDir, "mbxmoods-exclude.json");
                    var rDiag = ExclusionStore.Merge(diagCanonical,
                        Delta(genrePodcast + ",{\"kind\":\"wombat\",\"action\":\"exclude\",\"value\":\"x\"}", ""),
                        "self-test", out var eDiag);
                    Assert(eDiag == null && rDiag.Added == 1, "store: delta with one good + one bad add rule still applies the good one");
                    Assert(rDiag.Diagnostics.Count == 1 && rDiag.Diagnostics[0].Contains("wombat"),
                        $"store: the dropped delta rule is reported as a diagnostic, not swallowed (got {rDiag.Diagnostics.Count})");

                    // MINOR 2: an all-invalid decisions side must not talk about "scanning"
                    // — MBXHub relays this string verbatim into a UI response.
                    ExclusionStore.Merge(diagCanonical, Delta("{\"kind\":\"wombat\",\"action\":\"exclude\",\"value\":\"x\"}", ""), "self-test", out var eWording);
                    Assert(eWording != null && !eWording.ToLowerInvariant().Contains("scan"),
                        $"MINOR: decisions-document error does not mention scanning (got: {eWording})");

                    // --- I-1: atomic write + cross-process lock ---
                    {
                        var atomicDir = Path.Combine(dir, "atomic");
                        Directory.CreateDirectory(atomicDir);
                        var ac = Path.Combine(atomicDir, "mbxmoods-exclude.json");
                        ExclusionStore.Merge(ac, Delta(genrePodcast, ""), "self-test", out var seedErr);
                        Assert(seedErr == null, $"I-1: seed merge succeeds ({seedErr})");
                        var seeded = File.ReadAllBytes(ac);

                        Assert(Path.GetDirectoryName(ExclusionStore.TempWritePath(ac)) == atomicDir
                               && ExclusionStore.TempWritePath(ac) != ac,
                            "I-1: the atomic write stages in the target's OWN directory (File.Replace is same-volume)");

                        // Block the tmp path with a DIRECTORY of the same name: the tmp write
                        // then fails, the atomic swap never happens, and the canonical file is
                        // untouched. A plain File.WriteAllText(canonical) would have SUCCEEDED
                        // here and rewritten the file — which is precisely what these two
                        // assertions pin.
                        var blocked = ExclusionStore.TempWritePath(ac);
                        Directory.CreateDirectory(blocked);
                        ExclusionStore.Merge(ac, Delta(genreAudiobook, ""), "self-test", out var eBlocked);
                        Assert(eBlocked != null, "I-1: a merge whose staged write fails reports the error instead of succeeding");
                        Assert(File.ReadAllBytes(ac).SequenceEqual(seeded),
                            "I-1: the canonical file is byte-identical when the write fails (tmp + atomic replace)");
                        Directory.Delete(blocked);

                        // Destination open for READ sharing: File.Copy (the backup) still works,
                        // File.Replace cannot — so the tmp file IS created and then orphaned.
                        using (new FileStream(ac, FileMode.Open, FileAccess.Read, FileShare.Read))
                            ExclusionStore.Merge(ac, Delta(genreAudiobook, ""), "self-test", out _);
                        Assert(Directory.GetFiles(atomicDir, "*.tmp").Length == 0,
                            "I-1: a failed atomic swap leaves no .tmp sibling beside the policy file");

                        // Cross-process lock: a held sidecar lock must refuse the merge, not
                        // silently lose the other author's rules.
                        using (new FileStream(ac + ExclusionStore.LockFileSuffix, FileMode.OpenOrCreate,
                                              FileAccess.ReadWrite, FileShare.None))
                        {
                            var rLocked = ExclusionStore.Merge(ac, Delta(genreAudiobook, ""), "self-test", out var eLocked);
                            Assert(eLocked != null && eLocked.ToLowerInvariant().Contains("lock"),
                                $"I-1: merge refuses while another writer holds the exclusion-file lock (got: {eLocked})");
                            Assert(!rLocked.Changed, "I-1: a lock-refused merge reports nothing changed");
                            Assert(File.ReadAllBytes(ac).SequenceEqual(seeded),
                                "I-1: a lock-refused merge leaves the canonical file byte-identical");
                        }
                    }
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            }

            // --- exclusion layering: structural skips beat include (spec 2026-07-24) ---
            {
                var saveSet = _exclusions;
                try
                {
                    _exclusions = ExclusionSet.FromJson(
                        "{\"schemaVersion\":1,\"rules\":[" +
                        "{\"kind\":\"folder\",\"action\":\"exclude\",\"pattern\":\"\\\\Podcasts\\\\**\"}," +
                        "{\"kind\":\"folder\",\"action\":\"include\",\"pattern\":\"\\\\Podcasts\\\\KEXP\\\\**\"}]}",
                        out var setErr);
                    Assert(setErr == null, "layering: test set parses");

                    var tracks = new List<ITunesTrack>
                    {
                        new ITunesTrack { Location = @"D:\Music\Podcasts\JRE.mp3",         Name = "JRE" },
                        new ITunesTrack { Location = @"D:\Music\Podcasts\KEXP\live.flac",  Name = "KEXP" },
                        new ITunesTrack { Location = @"D:\Music\Rock\song.flac",           Name = "Rock" },
                        new ITunesTrack { Location = @"D:\Music\Podcasts\KEXP\clip.mp4",   Name = "KexpVideo" },
                    };
                    var kept = FilterExclusions(tracks, null);
                    Assert(kept.Count == 3, $"layering: excluded track dropped (kept {kept.Count})");
                    // The marker is gone; the behaviour it supported is not. An include rule
                    // still keeps a track that an exclude rule would otherwise drop — that
                    // logic lives inside ExclusionSet.IsExcluded, not in a flag on the track.
                    // (Proven by the two assertions immediately below, which already covered
                    // this before ExclusionIncluded retired — no duplicate needed.)
                    Assert(!kept.Exists(t => t.Name == "JRE"), "layering: folder exclude drops the track");
                    Assert(kept.Exists(t => t.Name == "KEXP"), "layering: include rule keeps the nested track");

                    // structural filters run independently of exclusion-rule outcomes
                    var afterVideo = FilterVideoFiles(kept, null);
                    Assert(!afterVideo.Exists(t => t.Name == "KexpVideo"), "layering: include does NOT resurrect a video file");

                    // Phase 3: a speech label is EVIDENCE now, not a verdict — the filter
                    // chain must let it through so the operator can decide with a rule.
                    var labelledNow = new List<ITunesTrack>
                    {
                        new ITunesTrack { Location = @"D:\Music\Talk\show.mp3", Name = "Show",
                                          IsSpeech = true, SpeechReason = "Genre=Podcast" },
                        new ITunesTrack { Location = @"D:\Music\Rock\song.flac", Name = "Rock" },
                    };
                    var afterChain = FilterVideoFiles(FilterNonAudio(labelledNow, null), null);
                    var showAfterChain = afterChain.Find(t => t.Name == "Show");

                    // The FULL MoodsMode chain, in its real order, not just two of the four
                    // filters. MoodsMode is the operator's primary (zero-arg) path, and a drop
                    // reinstated in FilterRemoteUrls or FilterExclusions would sail straight past
                    // a test that only exercises the video/non-audio pair — which is exactly the
                    // coverage gap the replay-gate review found in its file-list-only gate.
                    // _exclusions is neutralised so this pins the CHAIN, not whatever rule set a
                    // neighbouring test happened to leave behind.
                    var savedExclusions = _exclusions;
                    _exclusions = ExclusionSet.Empty;
                    var fullChain = FilterNonAudio(
                                        FilterVideoFiles(
                                            FilterExclusions(
                                                FilterRemoteUrls(labelledNow, null), null), null), null);
                    _exclusions = savedExclusions;
                    Assert(fullChain.Count == 2,
                        $"phase3: the full MoodsMode filter chain keeps a speech-labelled track (kept {fullChain.Count})");
                    Assert(fullChain.Exists(t => t.Name == "Show"),
                        "phase3: the speech-labelled track survives all four MoodsMode filters");
                    Assert(afterChain.Count == 2, $"phase3: a speech-labelled track is no longer filtered (kept {afterChain.Count})");
                    Assert(afterChain.Exists(t => t.Name == "Show"), "phase3: the labelled track specifically survives");
                    Assert(showAfterChain != null && showAfterChain.IsSpeech, "phase3: IsSpeech survives as evidence for the preview surface");
                    Assert(showAfterChain != null && showAfterChain.SpeechReason == "Genre=Podcast", "phase3: SpeechReason survives as evidence");
                }
                finally { _exclusions = saveSet; }
            }

            // --- ResolveReviewDir instance match (I5): never write into a non-owning instance ---
            {
                var owning = MatchReviewMbxhub(@"C:\Lib", new[] { (@"C:\Other", @"C:\Other\AppData\MBXHub"), (@"C:\Lib", @"C:\Lib\AppData\MBXHub") });
                Assert(owning == @"C:\Lib\AppData\MBXHub", "review-match: picks the instance whose exe root owns the scanned library");
                var stranger = MatchReviewMbxhub(@"C:\Lib", new[] { (@"C:\Other", @"C:\Other\AppData\MBXHub"), (@"C:\Third", @"C:\Third\AppData\MBXHub") });
                Assert(stranger == null, "review-match: no owning instance -> null, never a stranger's hub (I5)");
                Assert(MatchReviewMbxhub(null, new[] { (@"C:\X", @"C:\X\AppData\MBXHub") }) == null, "review-match: null scanned root -> null");
                Assert(MatchReviewMbxhub(@"c:\lib", new[] { (@"C:\LIB", @"C:\LIB\AppData\MBXHub") }) == @"C:\LIB\AppData\MBXHub", "review-match: root comparison is case-insensitive");
            }

            // --- I-6: --preview's auto-locate uses the SAME library anchor the duplicates
            // manifest does, not a second implementation. Both go through ResolveReviewDir, so
            // the destination follows the library truedat was pointed at rather than whichever
            // MusicBee the OS happens to list first — pinned here so the two cannot fork later.
            {
                var i6Root = Path.Combine(Path.GetTempPath(), $".truedat-selftest-review-{Guid.NewGuid():N}");
                var i6Lib = Path.Combine(i6Root, "Library");
                try
                {
                    Directory.CreateDirectory(i6Lib);
                    Directory.CreateDirectory(Path.Combine(i6Root, "AppData", "MBXHub"));
                    var expectedReview = Path.Combine(i6Root, "AppData", "MBXHub", "review");

                    Assert(ResolveReviewDir(i6Lib) == expectedReview,
                        $"I-6: the review dir is derived from the SCANNED library's root (got {ResolveReviewDir(i6Lib)})");
                    Assert(PreviewWriter.ResolveDest(i6Lib) == Path.Combine(expectedReview, PreviewWriter.FileName),
                        $"I-6: --preview auto-locates into that same review dir (got {PreviewWriter.ResolveDest(i6Lib)})");
                    Assert(ResolveManifestDest(i6Lib) == Path.Combine(expectedReview, "dupes.json"),
                        $"I-6: --manifest auto-locates into that same review dir (got {ResolveManifestDest(i6Lib)})");
                    Assert(Path.GetDirectoryName(PreviewWriter.ResolveDest(i6Lib))
                           == Path.GetDirectoryName(ResolveManifestDest(i6Lib)),
                        "I-6: preview and the duplicates manifest share ONE anchor, two filenames");

                    // No MBXHub tree under the scanned library's root: fall back beside the moods
                    // file. Never into some other running instance's hub.
                    var i6Bare = Path.Combine(i6Root, "Bare", "Library");
                    Directory.CreateDirectory(i6Bare);
                    Assert(ResolveReviewDir(i6Bare) == null,
                        "I-6: no MBXHub tree under the scanned library -> no review dir, never a stranger's");
                    Assert(PreviewWriter.ResolveDest(i6Bare) == Path.Combine(i6Bare, "mbxmoods-preview.json"),
                        $"I-6: --preview then lands beside the moods file (got {PreviewWriter.ResolveDest(i6Bare)})");
                }
                finally { try { Directory.Delete(i6Root, true); } catch { } }
            }

            // --- speechLikely verdict (spec 2026-07-22 B1) ---
            {
                TrackEntry Mk(double dance, double? chords, double? silence, double zcr) => new TrackEntry
                {
                    Features = new TrackFeatures
                    {
                        Danceability = dance, ChordsStrength = chords,
                        SilenceRate30dB = silence, ZeroCrossingRate = zcr,
                    },
                };
                var talk = ComputeTruedatVerdict("t", Mk(0.4, 0.40, 0.35, 0.12));
                Assert(talk.SpeechLikely == "yes", "speech: talk-shaped features -> yes");
                var music = ComputeTruedatVerdict("m", Mk(1.4, 0.58, 0.01, 0.05));
                Assert(music.SpeechLikely == "no", "speech: music-shaped features -> no");
                var sparse = ComputeTruedatVerdict("s", new TrackEntry { Features = new TrackFeatures() });
                Assert(sparse.SpeechLikely == "n/a" || sparse.SpeechLikely == "unknown", "speech: sparse features -> n/a or unknown");
                // Rev-1 safety gate: a sine-tone/ambient bed shares talk's shape on
                // danceability/chords/silence but sits LOW on zcr — must NOT reach
                // "yes" (a verdict AutoQ and the exclusion workflow both treat as
                // confident talk).
                var tone = ComputeTruedatVerdict("z", Mk(0.4, 0.40, 0.35, 0.05));
                Assert(tone.SpeechLikely == "unknown", "speech: tone-shaped features (low zcr) -> unknown, not yes");
                // Rev-1.2 music veto: sparse/live INSTRUMENTAL music matches talk on
                // chords/silence/zcr but is unmistakably musical on danceability.
                // Real cases that --migrate wrongly pruned before this gate existed:
                // Charlie Parker "Hot House" (1.09), NIN "Burn" live (1.10),
                // Travis "Outro" (0.78) — plus the 0.50-0.70 band the rev-1.2
                // tightening added: Beatles "Sgt. Pepper Effects Tape 2" (0.657),
                // Southern Culture "For Lovers Only (Reprise)" (0.698).
                // All must demote to "unknown", not "yes".
                //
                // The panel below reproduces the REAL incident shape (Hot House measured
                // chords 0.447, silence 0.997, zcr 0.129, bpmFirstPeakWeight 0, edma
                // strength ~0.49 -> score 0.95 / maxWeight 1.25 = 0.76 -> "yes"). The
                // earlier 4-signal panel scored only 0.60/0.90 = 0.667, which returns
                // "unknown" BEFORE the veto is ever consulted — so for the >=0.7 cases it
                // asserted nothing and would have passed with the veto deleted
                // (2026-07-23 review, I1). All six signals are supplied here so every
                // parametrized value genuinely exercises the demotion path.
                TrackEntry MkInstrumental(double dance) => new TrackEntry
                {
                    Features = new TrackFeatures
                    {
                        Danceability = dance, ChordsStrength = 0.44,
                        SilenceRate30dB = 0.99, ZeroCrossingRate = 0.13,
                        BpmFirstPeakWeight = 0.0,
                        KeyVoteEdma = new KeyVote { Key = "A", Scale = "minor", Strength = 0.49 },
                    },
                };
                foreach (var dance in new[] { 0.657, 0.698, 0.78, 1.09, 1.10 })
                {
                    var instr = ComputeTruedatVerdict("i", MkInstrumental(dance));
                    Assert(instr.SpeechLikely != "yes", $"speech: instrumental music (danceability={dance:F2}) must not reach yes");
                }
                // The load-bearing half of that pair: the SAME panel with danceability at
                // 0 must still reach "yes". Identical inputs but for danceability proves
                // the veto is what demotes the cases above — without this, tightening the
                // gate into uselessness would still pass every assertion.
                Assert(ComputeTruedatVerdict("p0", MkInstrumental(0.0)).SpeechLikely == "yes",
                    "speech: same panel at danceability 0 DOES reach yes (proves the veto is load-bearing)");
                // ...and genuine speech on the plain 4-signal panel still reaches "yes",
                // otherwise the gate has neutered the signal entirely.
                var spoken = ComputeTruedatVerdict("p", Mk(0.0, 0.43, 1.0, 0.27));
                Assert(spoken.SpeechLikely == "yes", "speech: genuine spoken word (danceability 0) still -> yes");

                // --- Recompute-path drift guard -------------------------------------
                // RecomputeSpeechLikely (raw JSON) must agree with ComputeTruedatVerdict
                // (TrackEntry) for equivalent inputs, and must not trust a stale persisted
                // verdict over the current features. This is a computation guard, not a
                // purge guard — RunMigrate no longer prunes on this verdict (2026-07-25),
                // but --list-speech and the --stats class-stat count both depend on the
                // TrackEntry path, and RecomputeSpeechLikely is the JSON-facing twin any
                // future JSON-only caller would reach for; the two must never disagree.
                {
                    var spokenJson = JsonNode.Parse("{\"danceability\":0.0,\"chordsStrength\":0.43,\"silenceRate30dB\":1.0,\"zeroCrossingRate\":0.27}");
                    Assert(RecomputeSpeechLikely(spokenJson) == spoken.SpeechLikely,
                        "list-speech recompute matches the TrackEntry path");
                    // A stale persisted "yes" must not leak through: the recompute is
                    // honest about the CURRENT features/thresholds, not whatever verdict
                    // an earlier build happened to write to truedat.speechLikely.
                    var staleJson = JsonNode.Parse("{\"danceability\":1.09,\"chordsStrength\":0.44,\"silenceRate30dB\":0.99,\"zeroCrossingRate\":0.13,\"bpmFirstPeakWeight\":0.0,\"keyVotes\":{\"edma\":{\"key\":\"A\",\"scale\":\"minor\",\"strength\":0.49}},\"truedat\":{\"speechLikely\":\"yes\"}}");
                    Assert(RecomputeSpeechLikely(staleJson) != "yes",
                        "speech recompute ignores a stale stored 'yes' and trusts current features");
                }
            }

            // --- IsTagsOnlyChange acceptance envelope --------------------------------
            {
                FingerprintV1 Mk() => new FingerprintV1
                {
                    FileSize = 1000, PathTail = "a/b/c.flac", DurationMs = 200000,
                    SampleRate = 44100, Channels = 2, BitDepth = 16, Codec = "flac",
                    Bitrate = 900, AudioHead64kMd5 = "aabbcc", AudioHead64kMd5Source = "invariant",
                };
                var s0 = Mk(); var f0 = Mk(); f0.FileSize = 1042; f0.Bitrate = 897; f0.Encoder = "retagger";
                Assert(IsTagsOnlyChange(f0, s0), "tags-only: size/bitrate/encoder drift alone is accepted");

                var f1 = Mk(); f1.AudioHead64kMd5 = "ddeeff";
                Assert(!IsTagsOnlyChange(f1, s0), "tags-only: head md5 mismatch rejected");

                var f2 = Mk(); f2.DurationMs = 205000;
                Assert(!IsTagsOnlyChange(f2, s0), "tags-only: duration drift > 500ms rejected");

                var f3 = Mk(); f3.DurationMs = 200400;
                Assert(IsTagsOnlyChange(f3, s0), "tags-only: duration drift <= 500ms accepted");

                var f4 = Mk(); f4.AudioHead64kMd5Source = "whole-file-start";
                Assert(!IsTagsOnlyChange(f4, s0), "tags-only: whole-file-start head rejected (fresh)");
                var s4 = Mk(); s4.AudioHead64kMd5Source = "whole-file-start";
                Assert(!IsTagsOnlyChange(f0, s4), "tags-only: whole-file-start head rejected (stored)");

                var f5 = Mk(); f5.SampleRate = 48000;
                Assert(!IsTagsOnlyChange(f5, s0), "tags-only: sampleRate change rejected");

                var s6 = Mk(); s6.AudioHead64kMd5 = "";
                Assert(!IsTagsOnlyChange(f0, s6), "tags-only: empty stored head rejected");
            }

            // --- StructuralSkipReason: DSD/video/playlist all agree with the
            // pre-scan XML-list filters (FilterVideoFiles / FilterNonAudio) so a
            // structural skip reads the same whether it happened via the XML path
            // or a per-file guard (--analyze-file / --file-list / MoodsMode). ---
            {
                Assert(StructuralSkipReason("C:\\music\\track.dsf") == "unsupported codec: DSD",
                    "structural skip: .dsf reports the DSD reason");
                Assert(StructuralSkipReason("C:\\music\\track.dff") == "unsupported codec: DSD",
                    "structural skip: .dff reports the DSD reason");
                Assert(StructuralSkipReason("C:\\music\\video.mp4") == "video file extension",
                    "structural skip: .mp4 reports the video reason");
                Assert(StructuralSkipReason("C:\\music\\clip.mkv") == "video file extension",
                    "structural skip: .mkv reports the video reason");
                Assert(StructuralSkipReason("C:\\music\\list.m3u") == "playlist / redirector extension",
                    "structural skip: .m3u reports the playlist/redirector reason");
                Assert(StructuralSkipReason("C:\\music\\list.wpl") == "playlist / redirector extension",
                    "structural skip: .wpl reports the playlist/redirector reason");
                Assert(StructuralSkipReason("C:\\music\\track.flac") == null,
                    "structural skip: .flac (ordinary audio) is not flagged");
                Assert(StructuralSkipReason("C:\\music\\track.mp3") == null,
                    "structural skip: .mp3 (ordinary audio) is not flagged");
                Assert(StructuralSkipReason(null) == null, "structural skip: null path is not flagged");
                Assert(StructuralSkipReason("") == null, "structural skip: empty path is not flagged");
                Assert(StructuralSkipReason("C:\\music\\VIDEO.MP4") == "video file extension",
                    "structural skip: matching is case-insensitive (.MP4 behaves like .mp4)");
            }

            // --- I-4: a chunked shard evaluates library-wide but LEDGERS its own bucket -----
            // Every shard used to write the whole library's exclusion rows into its own
            // hostname-suffixed skipped.csv, so an N-machine run produced N duplicates of every
            // excluded track while missing/error/structural rows stayed shard-scoped: one CSV,
            // two scopes. Rule hit counts must NOT become shard-scoped in the fix — they are the
            // stale-rule signal and their cross-machine agreement is what makes them trustworthy.
            {
                Assert(ChunkOwns(@"D:\M\a.flac", 1, 0), "chunk-scope: chunkTotal 0 means not sharding — this run owns everything");
                Assert(ChunkOwns(@"D:\M\a.flac", 3, -1), "chunk-scope: a negative chunkTotal also means not sharding");
                Assert(!ChunkOwns("", 1, 4), "chunk-scope: an empty location is owned by no bucket");
                Assert(!ChunkOwns(null, 1, 4), "chunk-scope: a null location is owned by no bucket");
                foreach (var p in new[] { @"D:\M\a.flac", @"D:\M\b.mp3", @"\\srv\share\c.wav" })
                {
                    int owners = 0;
                    for (int m = 1; m <= 4; m++) if (ChunkOwns(p, m, 4)) owners++;
                    Assert(owners == 1, $"chunk-scope: {p} is owned by exactly one bucket of 4 (got {owners})");
                }
                Assert(ChunkOwns(@"D:/M/a.flac", 1, 4) == ChunkOwns(@"D:\M\a.flac", 1, 4),
                    "chunk-scope: ownership is separator-normalized, so shards agree across XML dialects");

                // Ledger scoping, over FilterExclusions itself.
                var i4Dir = Path.Combine(Path.GetTempPath(), $".truedat-selftest-chunk-{Guid.NewGuid():N}");
                Directory.CreateDirectory(i4Dir);
                var i4SavedExcl = _exclusions;
                var i4SavedScope = _ledgerScope;
                var i4SavedNote = _ledgerScopeNote;
                try
                {
                    const string mine  = @"D:\Music\Podcasts\mine.mp3";
                    const string yours = @"D:\Music\Podcasts\yours.mp3";
                    _exclusions = ExclusionSet.FromJson(
                        "{\"schemaVersion\":1,\"rules\":[{\"kind\":\"folder\",\"action\":\"exclude\",\"pattern\":\"\\\\Podcasts\\\\**\"}]}",
                        out var i4Err);
                    Assert(i4Err == null, $"chunk-ledger: fixture rule parses ({i4Err})");

                    var i4Tracks = new List<ITunesTrack>
                    {
                        new ITunesTrack { Location = mine,  Name = "Mine" },
                        new ITunesTrack { Location = yours, Name = "Yours" },
                        new ITunesTrack { Location = @"D:\Music\Rock\keep.flac", Name = "Keep" },
                    };
                    // This shard owns exactly one of the two excluded tracks.
                    _ledgerScope = p => string.Equals(p, mine, StringComparison.OrdinalIgnoreCase);
                    _ledgerScopeNote = " (this chunk)";

                    var i4Csv = Path.Combine(i4Dir, "mbxmoods-skipped.csv");
                    var i4Kept = FilterExclusions(i4Tracks, i4Csv);

                    Assert(i4Kept.Count == 1, $"chunk-ledger: both excluded tracks are still dropped from the work list (got {i4Kept.Count} kept)");
                    var i4Rows = File.ReadAllLines(i4Csv);
                    Assert(i4Rows.Length == 2, $"chunk-ledger: header + exactly ONE row — the out-of-bucket exclusion is not duplicated here (got {i4Rows.Length} lines)");
                    Assert(i4Rows[1].StartsWith(mine, StringComparison.Ordinal),
                        $"chunk-ledger: the row written is this shard's own track (got '{i4Rows[1]}')");
                    Assert(_exclusions.Rules[0].MatchCount == 2,
                        $"chunk-ledger: the rule's hit count stays LIBRARY-wide, not shard-scoped (got {_exclusions.Rules[0].MatchCount})");
                }
                finally
                {
                    _exclusions = i4SavedExcl;
                    _ledgerScope = i4SavedScope;
                    _ledgerScopeNote = i4SavedNote;
                    try { Directory.Delete(i4Dir, true); } catch { }
                }
            }

            // --- I-3: the startup orphan sweep must not eat a live sibling's working files.
            // Exact boundaries, because both guards are the difference between "collect
            // leftovers" and "delete a running scan's staged copy and blame the track".
            {
                var i3Now = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);
                Assert(OrphanMinAge == TimeSpan.FromMinutes(60),
                    $"sweep: the orphan age gate is 60 minutes (got {OrphanMinAge.TotalMinutes})");
                Assert(!IsSweepableOrphan(i3Now, i3Now),
                    "sweep: a file written just now is NOT an orphan");
                Assert(!IsSweepableOrphan(i3Now.AddMinutes(-59), i3Now),
                    "sweep: 59 minutes old is still too recent to sweep");
                Assert(IsSweepableOrphan(i3Now.AddMinutes(-60), i3Now),
                    "sweep: exactly 60 minutes old is sweepable (boundary is >=)");
                Assert(IsSweepableOrphan(i3Now.AddDays(-3), i3Now),
                    "sweep: a three-day-old leftover is swept");

                Assert(ShouldSweepOrphans(0), "sweep: with no sibling truedat running, the sweep proceeds");
                Assert(!ShouldSweepOrphans(1), "sweep: ONE sibling truedat is enough to refuse the sweep");
                Assert(!ShouldSweepOrphans(7), "sweep: several siblings also refuse");
                // This process is itself a truedat, and must not count itself — otherwise the
                // sweep would refuse on every run and never collect anything. Asserted as a
                // difference so it holds whether or not the operator has other scans running.
                int i3Named = Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName).Length;
                Assert(i3Named - CountSiblingTruedatProcesses() == 1,
                    $"sweep: the sibling count excludes exactly this process ({i3Named} named, {CountSiblingTruedatProcesses()} siblings)");
            }

            // --- I-5: one wording for "the scan cannot find this file", shared by
            // --file-list/--folder and --analyze-file. Exact strings, because these land
            // verbatim in mbxmoods-skipped.csv and an operator greps them.
            {
                Assert(MissingFileSkipReason(@"D:\M\gone.flac") == "file not found",
                    $"missing-skip: an ordinary absent path reads 'file not found' (got '{MissingFileSkipReason(@"D:\M\gone.flac")}')");
                var long260 = @"D:\M\" + new string('x', 255);   // 260 exactly
                Assert(long260.Length == 260, $"missing-skip: fixture path is exactly 260 chars (got {long260.Length})");
                Assert(MissingFileSkipReason(long260) == "file not found (path 260 chars >= 260 MAX_PATH)",
                    $"missing-skip: at exactly MAX_PATH the reason names the length (got '{MissingFileSkipReason(long260)}')");
                var long259 = long260.Substring(0, 259);
                Assert(MissingFileSkipReason(long259) == "file not found",
                    $"missing-skip: one char under MAX_PATH is an ordinary miss (got '{MissingFileSkipReason(long259)}')");
            }

            // --- over-max-duration is a structural skip, not a failure (I4) ---
            {
                var over = OverLengthSkipReason(3600, 1200);   // 60-min track, 20-min ceiling
                Assert(over != null, "overlength: a track past the ceiling is a skip");
                Assert(over != null && over.Contains("--max-duration"), "overlength: reason names the --max-duration flag (I4)");
                Assert(over != null && over.Contains("20 min"), "overlength: reason names the ceiling in minutes");
                Assert(OverLengthSkipReason(1000, 1200) == null, "overlength: a track under the ceiling is analyzed, not skipped");
                Assert(OverLengthSkipReason(1200, 1200) == null, "overlength: exactly at the ceiling is analyzed (strict >)");
                Assert(OverLengthSkipReason(90, 30)!.Contains("30 s"), "overlength: sub-minute ceiling shows seconds, not '0 min' (M8)");
            }

            // --- --preview no-ETA line distinguishes "nothing new" from "no history" (I3) ---
            {
                Assert(PreviewNoEtaReason(0) == "nothing new to analyze", "preview-eta: New:0 says nothing new to analyze, not 'not estimable' (I3)");
                Assert(PreviewNoEtaReason(5).Contains("not estimable"), "preview-eta: new work but no history is genuinely not estimable");
            }

            // --- zero-arg pre-flight ETA is duration-based, not bytes (I2) ---
            {
                // 600s of new audio at 0.10x realtime across 4 workers -> 600*0.10/4 = 15s
                Assert(Math.Abs(PreflightEtaSecs(600_000, 0.10, 4) - 15.0) < 1e-6, "preflight-eta: duration x rtf / parallelism");
                // Longer audio costs more even at identical byte-rate — duration-driven, not bytes
                Assert(PreflightEtaSecs(3_600_000, 0.10, 1) > PreflightEtaSecs(600_000, 0.10, 1), "preflight-eta: longer audio costs more (duration-driven, not bytes) (I2)");
                Assert(PreflightEtaSecs(0, 0.10, 4) < 0, "preflight-eta: no new audio -> not estimable");
                Assert(PreflightEtaSecs(600_000, 0, 4) < 0, "preflight-eta: no measured RTF -> not estimable");
                Assert(Math.Abs(PreflightEtaSecs(600_000, 0.10, 0) - 60.0) < 1e-6, "preflight-eta: parallelism floored to 1");
            }

            // --- pre-flight new-work classifier matches what the scan analyzes (I1) ---
            {
                Assert(IsAnalysisCandidate(false, null, null), "preflight-work: uncached and analyzable -> counted as new work");
                Assert(!IsAnalysisCandidate(true, null, null), "preflight-work: current-features cache entry -> not new work");
                Assert(!IsAnalysisCandidate(false, "unsupported codec: DSD", null), "preflight-work: a structural skip is not new work (I1)");
                Assert(!IsAnalysisCandidate(false, null, "over max duration: 60 min > 20 min ceiling"), "preflight-work: an over-length skip is not new work (I1)");
            }

            Console.WriteLine(failures == 0
                ? "All self-tests passed."
                : $"{failures} self-test(s) FAILED.");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>Trailing-zero count for a uint32 value; returns 32 for zero.
        /// Pure-managed; net48 lacks System.Numerics.BitOperations.TrailingZeroCount.</summary>
        static int TrailingZeroCountUInt32(uint x)
        {
            if (x == 0) return 32;
            int n = 0;
            if ((x & 0xFFFF) == 0) { n += 16; x >>= 16; }
            if ((x & 0xFF)   == 0) { n += 8;  x >>= 8; }
            if ((x & 0xF)    == 0) { n += 4;  x >>= 4; }
            if ((x & 0x3)    == 0) { n += 2;  x >>= 2; }
            if ((x & 0x1)    == 0) { n += 1; }
            return n;
        }

        /// <summary>
        /// Downmix a multi-channel audio file to stereo using ffmpeg.
        /// Returns path to temp WAV file, or null on failure. Caller must delete the temp file.
        /// </summary>
        static string? DownmixToStereo(string audioPath)
        {
            var ffmpeg = _ffmpegPath.Value;
            if (ffmpeg == null) return null;

            // Into the STAGE DIR, not %TEMP% (I-3). The startup sweep deletes
            // truedat_stereo_*.wav, and while the producer hard-coded %TEMP% a second
            // invocation could not be isolated from a live one no matter what --stage-dir
            // said: it would delete the running scan's in-flight downmix WAVs, and losing a
            // WAV mid-Essentia surfaces in mbxmoods-errors.csv as if the TRACK were corrupt.
            // Multi-channel sources are exactly the hi-res material the authenticity work
            // targets, so this was the collision most likely to be mis-diagnosed.
            string downmixDir = _stageOpts.StageDir;
            try { Directory.CreateDirectory(downmixDir); }
            catch { downmixDir = Path.GetTempPath(); }   // never fail a downmix over the dir
            string tempPath = Path.Combine(downmixDir, $"truedat_stereo_{Guid.NewGuid():N}.wav");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = $"-i {PathHelper.QuoteArg(audioPath)} -ac 2 -y {PathHelper.QuoteArg(tempPath)}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi)!;
                ApplyCpuLimit(proc);
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(300000)) // 5 min timeout
                {
                    try { proc.Kill(); proc.WaitForExit(5000); } catch { }
                    Console.Error.WriteLine($"  DEBUG downmix timed out (300s)");
                    try { File.Delete(tempPath); } catch { }
                    return null;
                }
                proc.WaitForExit(); // flush async I/O buffers
                stdoutTask.Wait(5000);
                if (proc.ExitCode == 0 && File.Exists(tempPath))
                {
                    if (_audit)
                    {
                        var srcMb = 0.0; try { srcMb = new FileInfo(audioPath).Length / (1024.0 * 1024.0); } catch { }
                        var tmpMb = new FileInfo(tempPath).Length / (1024.0 * 1024.0);
                        Console.Error.WriteLine($"  DEBUG downmix: {srcMb:F1} MB -> {tmpMb:F1} MB stereo WAV");
                    }
                    return tempPath;
                }
                var stderr = stderrTask.Wait(5000) ? stderrTask.Result : "";
                Console.Error.WriteLine($"  DEBUG downmix failed (exit {proc.ExitCode}): {stderr.Substring(0, Math.Min(200, stderr.Length))}");
                try { File.Delete(tempPath); } catch { }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  DEBUG downmix exception: {ex.Message}");
                try { File.Delete(tempPath); } catch { }
            }
            return null;
        }

        /// <summary>
        /// --transcode mode: ffmpeg-transcode input -> uncompressed FLAC.
        /// Defaults to source sample rate / bit depth; overrides supplied via CLI.
        /// Returns process exit code (0 = success).
        /// </summary>
        static int RunTranscode(string inputPath, string outputPath, int overrideRate, int overrideDepth)
        {
            var ffmpeg = _ffmpegPath.Value;
            if (ffmpeg == null)
            {
                Console.Error.WriteLine("Error: ffmpeg not found on PATH (required for --transcode).");
                return 1;
            }

            int rate = overrideRate;
            int depth = overrideDepth;
            if (rate == 0 || depth == 0)
            {
                var ffprobe = FindTool("ffprobe.exe", AppDomain.CurrentDomain.BaseDirectory, Environment.CurrentDirectory);
                if (ffprobe != null)
                {
                    var details = ProbeAudio(ffprobe, inputPath);
                    if (details != null)
                    {
                        if (rate == 0 && details.SampleRate > 0) rate = details.SampleRate;
                        if (depth == 0 && details.BitDepth > 0) depth = details.BitDepth;
                    }
                }
                // Fallbacks when ffprobe is missing or the source reports nothing
                // (e.g. opus is internally float — container reports no bit depth).
                if (rate == 0) rate = 48000;
                if (depth == 0) depth = 24;
            }
            if (depth != 16 && depth != 24)
            {
                Console.Error.WriteLine($"Error: unsupported --bit-depth {depth} (allowed: 16, 24).");
                return 1;
            }

            // FLAC native sample formats: s16 for 16-bit, s32 for 24-bit (FLAC
            // packs 24-bit samples in s32 containers; -bits_per_raw_sample 24
            // sets the FLAC stream's declared depth so decoders round-trip it.)
            string sampleFmt = depth == 16 ? "s16" : "s32";
            string bitsArg = depth == 24 ? "-bits_per_raw_sample 24 " : "";

            try { var dir = Path.GetDirectoryName(outputPath); if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir); } catch { }

            Console.WriteLine($"Transcoding: {inputPath}");
            Console.WriteLine($"  -> {outputPath}");
            Console.WriteLine($"  rate={rate} Hz, depth={depth} bits, codec=flac (compression_level 0)");

            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-i {PathHelper.QuoteArg(inputPath)} -c:a flac -compression_level 0 -ar {rate} -sample_fmt {sampleFmt} {bitsArg}-y {PathHelper.QuoteArg(outputPath)}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            try
            {
                using var proc = Process.Start(psi)!;
                ApplyCpuLimit(proc);
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(300000)) // 5 min, matches DownmixToStereo
                {
                    try { proc.Kill(); proc.WaitForExit(5000); } catch { }
                    Console.Error.WriteLine("Error: ffmpeg transcode timed out (300s).");
                    return 1;
                }
                proc.WaitForExit();
                stdoutTask.Wait(5000);
                var stderr = stderrTask.Wait(5000) ? stderrTask.Result : "";
                if (proc.ExitCode != 0)
                {
                    var tail = stderr.Trim();
                    if (tail.Length > 500) tail = "…" + tail.Substring(tail.Length - 500);
                    Console.Error.WriteLine($"ffmpeg exit {proc.ExitCode}:\n{tail}");
                    return proc.ExitCode == 0 ? 1 : proc.ExitCode;
                }
                if (!File.Exists(outputPath))
                {
                    Console.Error.WriteLine("Error: ffmpeg reported success but output file is missing.");
                    return 1;
                }
                Console.WriteLine($"OK ({new FileInfo(outputPath).Length / 1024} KB)");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: transcode failed: {ex.Message}");
                return 1;
            }
        }

        static AudioDetails? ProbeAudio(string ffprobe, string audioPath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffprobe,
                    Arguments = $"-v quiet -print_format json -show_streams -show_format {PathHelper.QuoteArg(audioPath)}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) { if (_audit) Console.WriteLine($"  DEBUG probe: failed to start: {audioPath}"); return null; }
                ApplyCpuLimit(proc);
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(30000))
                {
                    try { proc.Kill(); proc.WaitForExit(3000); } catch { }
                    if (_audit) Console.WriteLine($"  DEBUG probe: timeout after 30s: {audioPath}");
                    return null;
                }
                proc.WaitForExit(); // flush async I/O buffers
                var stdout = stdoutTask.Wait(5000) ? stdoutTask.Result : "";
                stderrTask.Wait(5000);
                if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                {
                    if (_audit) Console.WriteLine($"  DEBUG probe: exit {proc.ExitCode}{(string.IsNullOrWhiteSpace(stdout) ? " (no output)" : "")}: {audioPath}");
                    return null;
                }

                using var doc = JsonDocument.Parse(stdout);
                var root = doc.RootElement;

                string codec = "", format = "";
                int channels = 0, sampleRate = 0, bitRate = 0, bitDepth = 0;
                double duration = 0;

                if (root.TryGetProperty("streams", out var streams))
                {
                    foreach (var stream in streams.EnumerateArray())
                    {
                        if (GetStr(stream, "codec_type") == "audio")
                        {
                            codec = GetStr(stream, "codec_name");
                            channels = GetInt(stream, "channels");
                            var srStr = GetStr(stream, "sample_rate");
                            if (int.TryParse(srStr, out var sr)) sampleRate = sr;
                            var brStr = GetStr(stream, "bit_rate");
                            if (long.TryParse(brStr, out var br)) bitRate = (int)(br / 1000);
                            var bpsStr = GetStr(stream, "bits_per_raw_sample");
                            if (!string.IsNullOrEmpty(bpsStr) && int.TryParse(bpsStr, out var bps))
                                bitDepth = bps;
                            else
                                bitDepth = GetInt(stream, "bits_per_sample");
                            break;
                        }
                    }
                }
                if (_audit && string.IsNullOrEmpty(codec))
                    Console.WriteLine($"  DEBUG probe: no audio stream found: {audioPath}");

                if (root.TryGetProperty("format", out var fmt))
                {
                    format = GetStr(fmt, "format_name");
                    var durStr = GetStr(fmt, "duration");
                    if (double.TryParse(durStr, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var d))
                        duration = Math.Round(d, 1);
                    if (bitRate == 0)
                    {
                        var fbrStr = GetStr(fmt, "bit_rate");
                        if (long.TryParse(fbrStr, out var fbr)) bitRate = (int)(fbr / 1000);
                    }
                }

                double sizeMb = 0;
                try { sizeMb = Math.Round(new FileInfo(audioPath).Length / (1024.0 * 1024.0), 1); } catch { }

                return new AudioDetails
                {
                    Codec = codec,
                    Format = format,
                    Channels = channels,
                    SampleRate = sampleRate,
                    BitRate = bitRate,
                    BitDepth = bitDepth,
                    Duration = duration,
                    SizeMb = sizeMb,
                    LastProbed = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                if (_audit) Console.WriteLine($"  DEBUG probe: parse error: {ex.Message}: {audioPath}");
                return null;
            }
        }

        // -- Shared helpers ---------------------------------------------------

        static Dictionary<string, string> LoadExistingErrors(string path)
        {
            var result = new Dictionary<string, string>(PathComparer.Instance);
            if (!File.Exists(path)) return result;
            try
            {
                foreach (var line in File.ReadAllLines(path).Skip(1))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = ParseCsvLine(line);
                    if (parts.Length >= 4) result[parts[3]] = parts[0];
                }
            }
            catch { }
            return result;
        }

        static string[] ParseCsvLine(string line)
        {
            var result = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '"') { if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else inQuotes = !inQuotes; }
                else if (c == ',' && !inQuotes) { result.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            result.Add(sb.ToString());
            return result.ToArray();
        }

        static readonly object _skippedCsvLock = new object();

        /// <summary>
        /// Returns the reason <paramref name="filePath"/> is structurally unanalyzable
        /// (an extension no amount of retrying or `include` re-inclusion can fix), or
        /// null when the extension is fine to attempt. Covers the same three buckets
        /// the pre-scan XML-list filters (<see cref="FilterVideoFiles"/> /
        /// <see cref="FilterNonAudio"/>) already drop before the scan loop —
        /// <see cref="UnsupportedExtensions"/> (DSD), <see cref="VideoExtensions"/>,
        /// and <see cref="NonAudioExtensions"/> — plus the reason text those filters
        /// pass into <see cref="FilterByPredicate"/>, so mbxmoods-skipped.csv reads
        /// consistently regardless of which mode wrote the row. Shared scan-entry
        /// guard for the three per-file call sites (--analyze-file, --file-list /
        /// --folder, MoodsMode) — a structural skip must be structural in every mode,
        /// not just the XML path, or a file the XML filters would have dropped can
        /// slip through --file-list (e.g. the MBXHub autoscan plugin) into a wasted
        /// Essentia subprocess and a misleading errors-CSV row. <see cref="PreviewPlanner"/>
        /// is the fourth caller: it open-coded two of these three checks and omitted
        /// DSD entirely, which is precisely the drift this helper exists to make
        /// impossible — do not re-inline the extension sets anywhere.
        /// </summary>
        internal static string? StructuralSkipReason(string? filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            var ext = GetExtensionSafe(filePath!);
            if (string.IsNullOrEmpty(ext)) return null;
            if (UnsupportedExtensions.Contains(ext)) return "unsupported codec: DSD";
            if (VideoExtensions.Contains(ext)) return "video file extension";
            if (NonAudioExtensions.Contains(ext)) return "playlist / redirector extension";
            return null;
        }

        /// <summary>Reason string for a track that exceeds the Essentia analysis ceiling, or
        /// null when it is within the ceiling. This is a STRUCTURAL skip (a scan cannot analyze
        /// it at this ceiling), not an analysis failure — the caller ledgers it to
        /// mbxmoods-skipped.csv and must NOT write it to mbxmoods-errors.csv, so raising
        /// --max-duration re-evaluates it instead of it being remembered as a prior error and
        /// skipped forever (I4). Strict &gt;: exactly at the ceiling is analyzable.</summary>
        internal static string? OverLengthSkipReason(int trackDurationSecs, int maxDurationSecs)
        {
            if (trackDurationSecs <= maxDurationSecs) return null;
            // M8: a sub-minute value must not render as "0 min" (F0 of e.g. 0.33) — show
            // seconds below a minute so a small --max-duration reads honestly.
            string Dur(int s) => s < 60 ? $"{s} s" : $"{s / 60.0:F0} min";
            return $"over max duration: {Dur(trackDurationSecs)} > {Dur(maxDurationSecs)} ceiling (--max-duration to override)";
        }

        /// <summary>The `--preview` Estimate line for the no-ETA case. A negative EtaSecs means
        /// two very different things and must not share one message (I3): when there is no new
        /// work (NewTracks == 0) the honest line is "nothing new to analyze"; "not estimable" is
        /// reserved for genuinely absent history — new work exists but no prior analysisDuration
        /// yet to learn an RTF from. Saying "nothing analyzed yet" on a fully-analyzed,
        /// caught-up library is the wrong answer to the wrong question.</summary>
        internal static string PreviewNoEtaReason(int newTracks) =>
            newTracks == 0
                ? "nothing new to analyze"
                : "not estimable (no analyzed history yet to learn from)";

        /// <summary>
        /// The trailing note on a `--preview` rule line (I-2). `(stale rule?)` on a zero count is
        /// the documented prompt to DELETE the rule, and for a `file` rule whose target moved that
        /// advice is both wrong and destructive — deleting it re-admits the file the operator
        /// rejected. So the moved/unreachable states suppress the stale prompt and say what is
        /// actually true, naming the content's current path(s) when the rule recorded a sha and
        /// the catalog still holds it. A non-zero count gets no note at all.
        /// </summary>
        internal static string PreviewRuleNote(PreviewRuleStat r)
        {
            if (r.State == PreviewRuleStat.StateUnreachable)
                return "   (file not reachable from here — still in the catalog, rule is live)";
            if (r.State == PreviewRuleStat.StateMoved)
                return r.Candidates.Count > 0
                    ? $"   (moved — content now at: {string.Join(" | ", r.Candidates)})"
                    : "   (moved or deleted — no catalog entry holds this content)";
            return r.MatchCount == 0 ? "   (stale rule?)" : "";
        }

        /// <summary>Pre-flight ETA in seconds for a zero-arg scan, or -1 when it cannot be
        /// estimated. Duration-based, NOT bytes: wall-clock ≈ total new-audio duration × the
        /// catalog's measured real-time factor / parallelism — the same model --preview uses.
        /// Bytes systematically under-cost long low-bitrate speech (a 45-min 64 kbps podcast
        /// is ~20 MB but ~45 min of Essentia work), which is exactly the content this release
        /// newly analyzes (I2). Returns -1 when there is no new audio or no measured RTF yet
        /// (rtf comes from MedianCatalogRtf, which returns 0 with no analyzed history).</summary>
        internal static double PreflightEtaSecs(long newAudioMs, double rtf, int parallelism)
        {
            if (newAudioMs <= 0 || rtf <= 0) return -1;
            var p = parallelism < 1 ? 1 : parallelism;
            return newAudioMs / 1000.0 * rtf / p;
        }

        /// <summary>True when the scan will actually hand this track to Essentia, from the
        /// cheap (no-IO) signals the zero-arg pre-flight can see (I1): it is not already a
        /// current-features cache entry, and it is neither a structural skip (DSD/video/
        /// non-audio) nor over the --max-duration ceiling — both of which the scan loop drops
        /// but the old pre-flight counted. This is an UPPER BOUND on new work: the sha/head
        /// cache tiers can still catch a hit at scan time, which needs per-file IO the
        /// pre-flight deliberately avoids, so the count is worded as an estimate, not a total.</summary>
        internal static bool IsAnalysisCandidate(bool hasCurrentFeatures, string? structuralReason, string? overLengthReason)
            => !hasCurrentFeatures && structuralReason == null && overLengthReason == null;

        /// <summary>Short bracket tag for the per-file console skip lines
        /// (<c>[skipped {tag}]</c>). Kept distinct from the full
        /// <see cref="StructuralSkipReason"/> text — the DSD tag is the literal
        /// "DSD" console output that shipped with Phase 5.1 and must not change.</summary>
        static string StructuralSkipConsoleTag(string reason) =>
            reason == "unsupported codec: DSD" ? "DSD"
            : reason == "video file extension" ? "video"
            : "playlist/redirector";

        /// <summary>
        /// Append a row to mbxmoods-skipped.csv (Phase 5.1). Mirrors AppendError's
        /// retry + append-with-header pattern. Thread-safe via a dedicated static
        /// lock so worker pools across modes can call this concurrently.
        /// </summary>
        static void AppendSkipped(string skippedPath, string filePath, string ext, string reason)
        {
            if (string.IsNullOrEmpty(skippedPath)) return;
            lock (_skippedCsvLock)
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        bool needsHeader = !File.Exists(skippedPath) || new FileInfo(skippedPath).Length == 0;
                        using (var fs = new FileStream(skippedPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                        using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
                        {
                            if (needsHeader) writer.WriteLine("path,extension,reason,timestamp");
                            writer.WriteLine($"{CsvEscape(filePath)},{CsvEscape(ext)},{CsvEscape(reason)},{DateTime.UtcNow:o}");
                        }
                        return;
                    }
                    catch (IOException) when (attempt < 4)
                    {
                        Thread.Sleep(200 * (attempt + 1));
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  Warning: Could not write to skipped CSV: {ex.Message}");
                        return;
                    }
                }
            }
        }

        static void AppendError(string errorsPath, string filePath, string artist, string title,
            string error, double sizeMb, double durationSecs, object lockObj)
        {
            lock (lockObj)
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        bool needsHeader = !File.Exists(errorsPath) || new FileInfo(errorsPath).Length == 0;
                        using (var fs = new FileStream(errorsPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                        using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
                        {
                            if (needsHeader) writer.WriteLine("Error,Artist,Title,FilePath,SizeMB,Duration");
                            writer.WriteLine($"{CsvEscape(error)},{CsvEscape(artist)},{CsvEscape(title)},{CsvEscape(filePath)},{sizeMb:F1},{durationSecs:F1}");
                        }
                        return;
                    }
                    catch (IOException) when (attempt < 4)
                    {
                        if (_audit) Console.WriteLine($"  DEBUG errors-csv: retry {attempt + 1}/5 for {filePath}");
                        Thread.Sleep(200 * (attempt + 1));
                    }
                    catch (Exception ex) { Console.WriteLine($"  Warning: Could not write to errors CSV: {ex.Message}"); return; }
                }
            }
        }

        /// <summary>
        /// Stream allTracks to disk using Utf8JsonWriter — writes UTF-8 directly to FileStream.
        /// No intermediate strings, no StreamWriter. Memory usage is O(1) per track.
        /// </summary>
        static void SaveResults(string moodsPath, ConcurrentDictionary<string, TrackEntry> allTracks)
        {
            var tmpPath = moodsPath + ".tmp";
            try { File.Delete(tmpPath); } catch { }

            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            using (var jw = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
            {
                jw.WriteStartObject();
                jw.WriteString("version", "1.0");
                jw.WriteString("generatedAt", DateTime.UtcNow.ToString("o"));
                jw.WriteNumber("trackCount", allTracks.Count);
                jw.WritePropertyName("tracks");
                jw.WriteStartObject();
                foreach (var kvp in allTracks)
                    WriteTrackEntry(jw, kvp.Key, kvp.Value);
                jw.WriteEndObject();
                jw.WriteEndObject();
            }

            AtomicReplace(tmpPath, moodsPath);
            if (_audit) { try { Console.WriteLine($"  DEBUG save: {moodsPath} ({new FileInfo(moodsPath).Length / 1024} KB, {allTracks.Count} tracks)"); } catch { } }
        }

        // ----------------------------------------------------------------------
        // Phase 4 — TruedatVerdict computation
        //
        // Multi-signal weighted voting per the plan
        // (docs/plans/2026-05-18-data-plumbing-phase4.md). Two independent
        // questions per track:
        //   - hiresGenuine     : is a claimed >=24-bit lossless file actually
        //                        carrying hi-res content?
        //   - lossyTranscodeLikely : is an MP3/AAC at >=192 kbps likely a
        //                            transcode from a lower-bitrate lossy source?
        //
        // Each question has its own applicability gate. When the gate fails the
        // verdict is "n/a". When it passes the signals vote; weighted sum is
        // normalized by maxWeight of voted-on signals; ±0.7 threshold decides
        // yes/no, anything weaker is "unknown". The thresholds below are
        // initial educated guesses — the method tag declares them as untuned
        // until a labeled test corpus calibrates them (BACKLOG Phase 4 corpus).
        // ----------------------------------------------------------------------

        static bool IsLosslessCodecForHiresCheck(string? codec) => codec switch
        {
            "flac" => true, "alac" => true, "wav" => true, "aiff" => true, _ => false,
        };
        static bool IsLossyCodecForTranscodeCheck(string? codec) => codec switch
        {
            "mp3" => true, "aac" => true, _ => false,
        };

        /// <summary>Compute the per-track verdict. Pure function: depends only on
        /// the entry's already-extracted features. Runs inline in WriteTrackEntry
        /// on every save so threshold changes ship without a rescan.</summary>
        static TruedatVerdict ComputeTruedatVerdict(string trackPath, TrackEntry entry)
        {
            var v = new TruedatVerdict();
            var f = entry.Features;
            var fp = entry.FingerprintV1;

            // --audit signal trace: built per verdict computation into a local buffer and
            // emitted as ONE file-only block at the end (TeeWriter.FileOnly holds the lock
            // for the whole block, so 14 workers' traces never interleave in the log).
            // The console NEVER sees these — at per-entry-per-save volume (recomputed for
            // every catalog entry on save) they flooded a 72k-track scan with a million+
            // unreadable lines; worse, they went to stderr which the tee didn't capture,
            // so the detail never reached truedat.log either. Null when !_audit — the
            // ?. calls skip argument evaluation, keeping the common path zero-cost.
            var trace = _audit ? new StringBuilder() : null;
            trace?.AppendLine($"TRUEDAT verdict: {(string.IsNullOrEmpty(trackPath) ? "(unknown)" : trackPath)}");

            // ----- hi-res verdict -----
            // Applicability gate: lossless container + claim of >=24-bit.
            if (fp != null && IsLosslessCodecForHiresCheck(fp.Codec) && fp.BitDepth >= 24)
            {
                double score = 0, maxWeight = 0;

                // Signal: bitUsage.lowestNonZeroBit. After ffmpeg's int24->int32 shift,
                // real 24-bit content lands at ~7-8; 16-bit padded to 24 lands at ~16.
                // Margins: <=10 -> real; >=14 -> fake; in between abstain.
                if (f.BitUsage != null)
                {
                    int lnz = f.BitUsage.LowestNonZeroBit;
                    int vote = lnz <= 10 ? 1 : lnz >= 14 ? -1 : 0;
                    if (vote != 0) { score += vote * 0.40; maxWeight += 0.40; }
                    trace?.AppendLine($"  TRUEDAT hires lowestNonZeroBit={lnz} vote={vote:+#;-#;0} weight=0.40");
                }

                // Signal: hfEnergyRatio. Phase 5 corpus1 retune: bin-sharp FFT
                // values are 3 orders of magnitude smaller than the old IIR-highpass
                // values the original Phase 3 threshold was calibrated against
                // (genuine 24/96 hi-res lands at ~1e-5; upsampled fakes round to 0
                // due to Lanczos suppression at 22 kHz). Real hi-res with a narrow
                // HF band (e.g. 24/48) can also round to 0, so the "fake" vote is
                // dropped — Signal F handles fake-hi-res discrimination instead.
                if (f.HfEnergyRatio.HasValue)
                {
                    double hr = f.HfEnergyRatio.Value;
                    int vote = hr >= 1e-5 ? 1 : 0;
                    if (vote != 0) { score += vote * 0.40; maxWeight += 0.40; }
                    trace?.AppendLine($"  TRUEDAT hires hfEnergyRatio={hr:E2} vote={vote:+#;-#;0} weight=0.40");
                }

                // Signal: bitUsage.effectiveBits. Real 24-bit easily exceeds 18 bits
                // effective resolution; <=14 means the file behaves like 16-bit.
                if (f.BitUsage != null)
                {
                    double eb = f.BitUsage.EffectiveBits;
                    int vote = eb >= 18 ? 1 : eb <= 14 ? -1 : 0;
                    if (vote != 0) { score += vote * 0.20; maxWeight += 0.20; }
                    trace?.AppendLine($"  TRUEDAT hires effectiveBits={eb:F2} vote={vote:+#;-#;0} weight=0.20");
                }

                // Signal F: hfSpectralStructure (Phase 5 — FFT-derived). Distinguishes
                // ffmpeg-upsampled fake hi-res (very low flatness AND high peak-to-mean
                // — narrow imaging spikes against an otherwise-empty HF band) from
                // genuine broadband or narrow-harmonic HF content. Corpus1 retune
                // (2026-05-18): imagingSymmetry was useless on this corpus (Lanczos
                // suppresses imaging enough that mirror correlation never fires) so
                // dropped from the vote. Flatness alone can't separate NIN's peaky-
                // but-genuine cymbal content (fl=0.011) from upsamples (fl=0.001-
                // 0.003), so the "fake" vote pairs flatness with peak-to-mean: NIN
                // has p2m=478 from a single strong harmonic, while imagings spread
                // over multiple mirrored bins land at p2m ~80-180 — different shape.
                // The "real" vote requires fl > 0.5 (only broadband real HF qualifies,
                // e.g. orchestral 24/48); most real hi-res abstains on Signal F and
                // relies on bitUsage / hfEnergyRatio to vote +1.
                if (f.HfSpectralStructure != null)
                {
                    double fl = f.HfSpectralStructure.Flatness;
                    double pm = f.HfSpectralStructure.PeakToMean;
                    double sy = f.HfSpectralStructure.ImagingSymmetry;
                    int vote = 0;
                    if (fl < 0.005 && pm > 50) vote = -1;                  // imaging-spike signature → fake
                    else if (fl > 0.5) vote = +1;                          // broadband HF → reinforce real
                    if (vote != 0) { score += vote * 0.35; maxWeight += 0.35; }
                    trace?.AppendLine($"  TRUEDAT hires hfSpectralStructure: flatness={fl:F4} peakToMean={pm:F2} imagingSymmetry={sy:F4} vote={vote:+#;-#;0} weight=0.35");
                }

                (v.HiresGenuine, v.HiresConfidence) = ResolveVerdict(score, maxWeight, minMaxWeight: 0.40);
                trace?.AppendLine($"  TRUEDAT hires SCORE={score:F2} maxWeight={maxWeight:F2} -> verdict={v.HiresGenuine}");
            }

            // ----- lossy-transcode verdict -----
            // TagLib reports AudioBitrate in kbps (Bitrate field follows the same convention).
            if (fp != null && IsLossyCodecForTranscodeCheck(fp.Codec) && fp.Bitrate >= 192)
            {
                double score = 0, maxWeight = 0;

                // Signal A: encoder string. Lavc/Lavf = re-muxer (transcode); LAME = original-ish.
                if (!string.IsNullOrEmpty(fp.Encoder))
                {
                    var enc = fp.Encoder!.ToLowerInvariant();
                    int vote = enc.StartsWith("lavc") || enc.StartsWith("lavf") ? 1
                             : enc.StartsWith("lame") ? -1 : 0;
                    if (vote != 0) { score += vote * 0.30; maxWeight += 0.30; }
                    trace?.AppendLine($"  TRUEDAT transcode encoder=\"{fp.Encoder}\" vote={vote:+#;-#;0} weight=0.30");
                }

                // Signal B: mp3LameTag.lowpassHz (MP3 only — LAME tag is MP3-specific).
                // LAME at 128 kbps sets ~16 kHz lowpass; LAME at 320 kbps sets ~22.1 kHz.
                // High lowpass = encoder targeted quality (vote "original" regardless of
                // actual measured bitrate — VBR varies on content so the bitrate value
                // is unreliable here; the LAME tag itself is the high-fidelity signal).
                // Low lowpass + high-bitrate-claim = encoder targeted compression but
                // file claims premium quality = transcode signature.
                // The outer applicability gate already ensures bitrate >= 192; dropping
                // the inner >= 256 check (which corpus testing 2026-05-18 showed was too
                // strict for VBR --preset extreme on simple content like orchestral —
                // symphonic 320k came in at 250 kbps and abstained when it shouldn't).
                if (fp.Codec == "mp3" && fp.Mp3LowpassHz > 0)
                {
                    int vote = 0;
                    if (fp.Mp3LowpassHz < 17500) vote = 1;
                    else if (fp.Mp3LowpassHz >= 19000) vote = -1;
                    if (vote != 0) { score += vote * 0.35; maxWeight += 0.35; }
                    trace?.AppendLine($"  TRUEDAT transcode lameLowpassHz={fp.Mp3LowpassHz} bitrate={fp.Bitrate} vote={vote:+#;-#;0} weight=0.35");
                }

                // Signal C: mp3LameTag presence vs encoder cross-check.
                if (fp.Codec == "mp3")
                {
                    bool hasLameTag = !string.IsNullOrEmpty(fp.Mp3LameVersion);
                    var enc = (fp.Encoder ?? "").ToLowerInvariant();
                    int vote = 0;
                    if (!hasLameTag && (enc.StartsWith("lavc") || enc.StartsWith("lavf"))) vote = 1;
                    else if (hasLameTag && fp.Mp3LameVersion!.StartsWith("LAME", StringComparison.OrdinalIgnoreCase)) vote = -1;
                    if (vote != 0) { score += vote * 0.20; maxWeight += 0.20; }
                    trace?.AppendLine($"  TRUEDAT transcode lameTagPresent={hasLameTag} encoder=\"{fp.Encoder}\" vote={vote:+#;-#;0} weight=0.20");
                }

                // Signal D: spectralRolloff — DISABLED (weight 0).
                //
                // The Phase 4 plan included this signal with weight 0.15, calling out the
                // "Fakin the Funk" false-positive risk and assuming the low weight would
                // prevent it from dominating. Corpus validation (2026-05-18) showed this
                // assumption was wrong: rolloff fights the LAME-original signals on
                // natural music (orchestral spectralRolloff=1198 Hz, real LAME 320k of
                // CD-rate rock at 2624 Hz) by voting +1 ("transcoded") with enough weight
                // to push -0.55 (signals B+C both saying "original") to -0.40, below the
                // -0.7 threshold so the verdict becomes "unknown" instead of "no".
                //
                // The fundamental problem: low spectralRolloff means EITHER heavy
                // compression OR naturally low-HF content. We can't distinguish these
                // from a single number. Any threshold trades false positives for false
                // negatives — there's no useful setting. Drop the signal entirely; revisit
                // in Phase 5+ with content-aware reasoning (e.g., genre-tag-conditional
                // thresholds, or spectral artifact detection).
                //
                // Kept in source as comment + zero-weight block so the structural intent
                // is preserved and future re-introduction is a one-line change.
                if (false && f.SpectralRolloff.HasValue && fp.Bitrate >= 256)
                {
                    double sr = f.SpectralRolloff.Value;
                    int vote = sr < 14000 ? 1 : sr >= 18000 ? -1 : 0;
                    if (vote != 0) { score += vote * 0.0; maxWeight += 0.0; }
                    trace?.AppendLine($"  TRUEDAT transcode spectralRolloff={sr:F0} vote={vote:+#;-#;0} weight=0 (disabled)");
                }

                (v.LossyTranscodeLikely, v.LossyTranscodeConfidence) = ResolveVerdict(score, maxWeight, minMaxWeight: 0.30);
                trace?.AppendLine($"  TRUEDAT transcode SCORE={score:F2} maxWeight={maxWeight:F2} -> verdict={v.LossyTranscodeLikely}");
            }

            // ----- speechLikely verdict (spec 2026-07-22 B1) -----
            // Talk content vs music, from features every analyzed entry already
            // stores — write-time like the hi-res verdict, so the whole catalog
            // gains it on the next save and thresholds retune without rescan.
            // Rev-1 thresholds are UNTUNED and conservative: borderline lands on
            // "unknown"; only confident talk returns "yes" (downstream consumers —
            // AutoQ's own candidate-pool filter, and the operator's exclusion workflow
            // via --list-speech — act on "yes" alone). Calibrate against known talk
            // entries before tightening.
            //
            // Denominator rule: maxWeight counts every APPLICABLE signal, not just
            // ones that fired a vote — abstains still dilute confidence. A minority
            // of weak agreeing signals must not normalize to a high-confidence
            // "yes" just because the rest of the panel abstained (e.g. a sine-tone
            // WAV where only danceability+zcr fire). This is what keeps a confident
            // "yes" trustworthy for those downstream consumers.
            {
                double score = 0, maxWeight = 0;

                // Danceability: Essentia's value sits >1 for most music, far lower for speech.
                // Core field, always present — no presence gate (0 is a legitimate value).
                {
                    int vote = f.Danceability < 0.7 ? 1 : f.Danceability > 1.1 ? -1 : 0;
                    maxWeight += 0.30;
                    if (vote != 0) score += vote * 0.30;
                    trace?.AppendLine($"  TRUEDAT speech danceability={f.Danceability:F2} vote={vote:+#;-#;0} weight=0.30");
                }
                // Chords strength: speech has no harmonic bed.
                if (f.ChordsStrength.HasValue)
                {
                    double cs = f.ChordsStrength.Value;
                    int vote = cs < 0.46 ? 1 : cs > 0.53 ? -1 : 0;
                    maxWeight += 0.25;
                    if (vote != 0) score += vote * 0.25;
                    trace?.AppendLine($"  TRUEDAT speech chordsStrength={cs:F3} vote={vote:+#;-#;0} weight=0.25");
                }
                // Silence rate: conversational pauses push it up.
                if (f.SilenceRate30dB.HasValue)
                {
                    double sr = f.SilenceRate30dB.Value;
                    int vote = sr > 0.25 ? 1 : sr < 0.05 ? -1 : 0;
                    maxWeight += 0.20;
                    if (vote != 0) score += vote * 0.20;
                    trace?.AppendLine($"  TRUEDAT speech silenceRate30dB={sr:F3} vote={vote:+#;-#;0} weight=0.20");
                }
                // Zero-crossing rate: speech sits higher than most music.
                // Core field, always present — no presence gate (0 is a legitimate value).
                int zcrVote = f.ZeroCrossingRate > 0.09 ? 1 : 0;
                {
                    maxWeight += 0.15;
                    if (zcrVote != 0) score += zcrVote * 0.15;
                    trace?.AppendLine($"  TRUEDAT speech zcr={f.ZeroCrossingRate:F3} vote={zcrVote:+#;-#;0} weight=0.15");
                }
                // Tempo-peak weight (2026-07-22 wave, when present): speech has no
                // stable tempo peak.
                if (f.BpmFirstPeakWeight.HasValue)
                {
                    double bw = f.BpmFirstPeakWeight.Value;
                    int vote = bw < 0.05 ? 1 : bw > 0.15 ? -1 : 0;
                    maxWeight += 0.20;
                    if (vote != 0) score += vote * 0.20;
                    trace?.AppendLine($"  TRUEDAT speech bpmFirstPeakWeight={bw:F3} vote={vote:+#;-#;0} weight=0.20");
                }
                // Key confidence (2026-07-22 wave, when present): craters on speech.
                if (f.KeyVoteEdma != null)
                {
                    double ks = f.KeyVoteEdma.Strength;
                    int vote = ks < 0.50 ? 1 : ks > 0.65 ? -1 : 0;
                    maxWeight += 0.15;
                    if (vote != 0) score += vote * 0.15;
                    trace?.AppendLine($"  TRUEDAT speech keyStrength={ks:F3} vote={vote:+#;-#;0} weight=0.15");
                }

                if (maxWeight > 0)
                    (v.SpeechLikely, v.SpeechConfidence) = ResolveVerdict(score, maxWeight, minMaxWeight: 0.50);
                // Rev-1 safety gate: "yes" additionally requires the zero-crossing
                // signal to have voted speech. Tones/drones/ambient share talk's
                // no-rhythm/no-chords/high-silence shape but sit LOW on zcr, while
                // real speech sits high — without this gate a sine tone reaches
                // "yes", a verdict AutoQ and the exclusion workflow both treat as
                // confident talk. Demote to "unknown", never to "no" (the content is
                // genuinely not-music-shaped).
                if (v.SpeechLikely == "yes" && zcrVote != 1)
                    v.SpeechLikely = "unknown";
                // Rev-1.2 music veto: "yes" additionally requires danceability < 0.50.
                // Sparse / live / free-form INSTRUMENTAL music craters on the rest of
                // the panel exactly like speech does — no stable tempo peak, weak
                // chords, weak key, high silence, high zcr — so those signals alone
                // can't tell a bebop sax solo from a spoken word. Danceability can:
                // measured against confirmed cases 2026-07-22, genuine speech sits at
                // 0.00 (Wiggles "Food Poem", alert tone) while real-music false
                // positives ran 0.66-1.10 (Beatles "Sgt. Pepper Effects Tape 2" 0.66,
                // Southern Culture "For Lovers Only (Reprise)" 0.70, Travis "Outro"
                // 0.78, Charlie Parker "Hot House" 1.09, NIN "Burn" live 1.10).
                // Without this gate --migrate pruned the last three from the catalog.
                //
                // 0.50 is deliberately TIGHTER than the danceability signal's own
                // talk-vote threshold (0.70): the 0.50-0.70 band held real music, and
                // this gate guards a pruning action, so the margin belongs on the side
                // of keeping music. Genuine speech sits at 0.00, nowhere near 0.50, so
                // the tightening costs no real recall.
                // Demote to "unknown", never "no" — same reasoning as the zcr gate.
                if (v.SpeechLikely == "yes" && f.Danceability >= 0.50)
                    v.SpeechLikely = "unknown";
                trace?.AppendLine($"  TRUEDAT speech SCORE={score:F2} maxWeight={maxWeight:F2} danceability={f.Danceability:F2} -> verdict={v.SpeechLikely}");
            }

            // One atomic write of the whole per-track block to the log file only. When
            // --audit is set but no tee/log is active (a mode without a log file), the
            // detail is dropped — the console must never get it either way.
            if (trace != null)
                _tee?.FileOnly(trace.ToString().TrimEnd());

            return v;
        }

        /// <summary>Convert score + maxWeight into the four-string-enum verdict.
        /// Requires at least minMaxWeight of signals voted (else abstain as "unknown");
        /// normalized score must cross ±0.7 for a yes/no, otherwise also "unknown".</summary>
        static (string verdict, double? confidence) ResolveVerdict(double score, double maxWeight, double minMaxWeight)
        {
            if (maxWeight < minMaxWeight) return ("unknown", null);
            double normalized = score / maxWeight;
            if (normalized >= 0.70) return ("yes", Math.Round(Math.Abs(score), 2));
            if (normalized <= -0.70) return ("no", Math.Round(Math.Abs(score), 2));
            return ("unknown", null);
        }

        static void WriteTrackEntry(Utf8JsonWriter jw, string path, TrackEntry entry)
        {
            var f = entry.Features;
            jw.WritePropertyName(path);
            jw.WriteStartObject();
            jw.WriteNumber("trackId", f.TrackId);
            jw.WriteString("artist", f.Artist);
            jw.WriteString("title", f.Title);
            jw.WriteString("album", f.Album);
            jw.WriteString("genre", f.Genre);
            jw.WriteNumber("bpm", f.Bpm);
            jw.WriteString("key", f.Key);
            jw.WriteString("mode", f.Mode);
            jw.WriteNumber("spectralCentroid", f.SpectralCentroid);
            jw.WriteNumber("spectralFlux", f.SpectralFlux);
            jw.WriteNumber("loudness", f.Loudness);
            jw.WriteNumber("danceability", f.Danceability);
            jw.WriteNumber("onsetRate", f.OnsetRate);
            jw.WriteNumber("zeroCrossingRate", f.ZeroCrossingRate);
            jw.WriteNumber("spectralRms", f.SpectralRms);
            jw.WriteNumber("spectralFlatness", f.SpectralFlatness);
            jw.WriteNumber("dissonance", f.Dissonance);
            jw.WriteNumber("pitchSalience", f.PitchSalience);
            jw.WriteNumber("chordsChangesRate", f.ChordsChangesRate);
            // DR is emitted only when present — old entries pass through without gaining empty keys.
            // MBXHub plugin treats missing as null and falls back to loudness-derived / genre-default.
            if (f.DynamicRange.HasValue)
            {
                jw.WriteNumber("dynamicRange", f.DynamicRange.Value);
                if (!string.IsNullOrEmpty(f.DynamicRangeSource))
                    jw.WriteString("dynamicRangeSource", f.DynamicRangeSource);
            }
            // Extended features — same omit-when-null pattern.
            static void WriteOpt(Utf8JsonWriter w, string name, double? v) { if (v.HasValue) w.WriteNumber(name, v.Value); }
            WriteOpt(jw, "loudnessMomentary", f.LoudnessMomentary);
            WriteOpt(jw, "loudnessShortTerm", f.LoudnessShortTerm);
            WriteOpt(jw, "replayGain", f.ReplayGain);
            WriteOpt(jw, "silenceRate20dB", f.SilenceRate20dB);
            WriteOpt(jw, "silenceRate30dB", f.SilenceRate30dB);
            WriteOpt(jw, "silenceRate60dB", f.SilenceRate60dB);
            WriteOpt(jw, "spectralRolloff", f.SpectralRolloff);
            WriteOpt(jw, "spectralComplexity", f.SpectralComplexity);
            WriteOpt(jw, "spectralEntropy", f.SpectralEntropy);
            WriteOpt(jw, "spectralKurtosis", f.SpectralKurtosis);
            WriteOpt(jw, "spectralSkewness", f.SpectralSkewness);
            WriteOpt(jw, "spectralSpread", f.SpectralSpread);
            WriteOpt(jw, "spectralStrongPeak", f.SpectralStrongPeak);
            WriteOpt(jw, "spectralDecrease", f.SpectralDecrease);
            WriteOpt(jw, "spectralEnergy", f.SpectralEnergy);
            WriteOpt(jw, "spectralEnergyLow", f.SpectralEnergyLow);
            WriteOpt(jw, "spectralEnergyMidLow", f.SpectralEnergyMidLow);
            WriteOpt(jw, "spectralEnergyMidHigh", f.SpectralEnergyMidHigh);
            WriteOpt(jw, "spectralEnergyHigh", f.SpectralEnergyHigh);
            WriteOpt(jw, "hfc", f.Hfc);
            WriteOpt(jw, "barkCrest", f.BarkCrest);
            WriteOpt(jw, "barkFlatness", f.BarkFlatness);
            WriteOpt(jw, "barkKurtosis", f.BarkKurtosis);
            WriteOpt(jw, "barkSkewness", f.BarkSkewness);
            WriteOpt(jw, "barkSpread", f.BarkSpread);
            WriteOpt(jw, "erbCrest", f.ErbCrest);
            WriteOpt(jw, "erbFlatness", f.ErbFlatness);
            WriteOpt(jw, "erbKurtosis", f.ErbKurtosis);
            WriteOpt(jw, "erbSkewness", f.ErbSkewness);
            WriteOpt(jw, "erbSpread", f.ErbSpread);
            WriteOpt(jw, "melCrest", f.MelCrest);
            WriteOpt(jw, "melFlatness", f.MelFlatness);
            WriteOpt(jw, "melKurtosis", f.MelKurtosis);
            WriteOpt(jw, "melSkewness", f.MelSkewness);
            WriteOpt(jw, "melSpread", f.MelSpread);
            WriteOpt(jw, "beatsLoudness", f.BeatsLoudness);
            WriteOpt(jw, "chordsStrength", f.ChordsStrength);
            WriteOpt(jw, "hpcpCrest", f.HpcpCrest);
            WriteOpt(jw, "hpcpEntropy", f.HpcpEntropy);
            // 2026-07-22 tonal/rhythm wave — all omit-when-null.
            static void WriteKeyVote(Utf8JsonWriter w, string name, KeyVote? v)
            {
                if (v == null) return;
                w.WritePropertyName(name);
                w.WriteStartObject();
                w.WriteString("key", v.Key);
                w.WriteString("scale", v.Scale);
                w.WriteNumber("strength", Math.Round(v.Strength, 4));
                w.WriteEndObject();
            }
            if (f.KeyVoteKrumhansl != null || f.KeyVoteTemperley != null || f.KeyVoteEdma != null)
            {
                jw.WritePropertyName("keyVotes");
                jw.WriteStartObject();
                WriteKeyVote(jw, "krumhansl", f.KeyVoteKrumhansl);
                WriteKeyVote(jw, "temperley", f.KeyVoteTemperley);
                WriteKeyVote(jw, "edma", f.KeyVoteEdma);
                jw.WriteEndObject();
            }
            WriteOpt(jw, "bpmFirstPeak", f.BpmFirstPeak);
            WriteOpt(jw, "bpmFirstPeakWeight", f.BpmFirstPeakWeight);
            WriteOpt(jw, "bpmSecondPeak", f.BpmSecondPeak);
            WriteOpt(jw, "bpmSecondPeakWeight", f.BpmSecondPeakWeight);
            WriteOpt(jw, "bpmSecondPeakSpread", f.BpmSecondPeakSpread);
            if (!string.IsNullOrEmpty(f.ChordsKey)) jw.WriteString("chordsKey", f.ChordsKey);
            if (!string.IsNullOrEmpty(f.ChordsScale)) jw.WriteString("chordsScale", f.ChordsScale);
            if (f.ChordsHistogram != null)
            {
                jw.WritePropertyName("chordsHistogram");
                jw.WriteStartArray();
                foreach (var v in f.ChordsHistogram) jw.WriteNumberValue(Math.Round(v, 4));
                jw.WriteEndArray();
            }
            WriteOpt(jw, "chordsNumberRate", f.ChordsNumberRate);
            WriteOpt(jw, "tuningFrequency", f.TuningFrequency);
            WriteOpt(jw, "tuningEqualTemperedDeviation", f.TuningEqualTemperedDeviation);
            WriteOpt(jw, "tuningDiatonicStrength", f.TuningDiatonicStrength);
            WriteOpt(jw, "tuningNontemperedEnergyRatio", f.TuningNontemperedEnergyRatio);
            WriteOpt(jw, "averageLoudness", f.AverageLoudness);
            // Phase 2.5 — bottom-bit analysis; omit-when-null.
            if (f.BitUsage != null)
            {
                jw.WritePropertyName("bitUsage");
                jw.WriteStartObject();
                jw.WriteNumber("lowestNonZeroBit", f.BitUsage.LowestNonZeroBit);
                jw.WriteNumber("bottomBitActivity", f.BitUsage.BottomBitActivity);
                jw.WriteNumber("effectiveBits", f.BitUsage.EffectiveBits);
                jw.WriteNumber("samplesAnalyzed", f.BitUsage.SamplesAnalyzed);
                if (!string.IsNullOrEmpty(f.BitUsage.Method))
                    jw.WriteString("method", f.BitUsage.Method);
                jw.WriteEndObject();
            }
            // Phase 3 — HF energy ratio; omit-when-null.
            if (f.HfEnergyRatio.HasValue)
                jw.WriteNumber("hfEnergyRatio", Math.Round(f.HfEnergyRatio.Value, 6));
            if (!string.IsNullOrEmpty(f.HfEnergyMethod))
                jw.WriteString("hfEnergyMethod", f.HfEnergyMethod);
            // Phase 5 — HF spectral structure (FFT-derived); omit-when-null.
            if (f.HfSpectralStructure != null)
            {
                jw.WritePropertyName("hfSpectralStructure");
                jw.WriteStartObject();
                jw.WriteNumber("flatness", Math.Round(f.HfSpectralStructure.Flatness, 4));
                jw.WriteNumber("peakToMean", Math.Round(f.HfSpectralStructure.PeakToMean, 2));
                jw.WriteNumber("imagingSymmetry", Math.Round(f.HfSpectralStructure.ImagingSymmetry, 4));
                if (!string.IsNullOrEmpty(f.HfSpectralStructure.Method))
                    jw.WriteString("method", f.HfSpectralStructure.Method);
                jw.WriteEndObject();
            }
            // Sony SMFM (12-TONE) — omit-when-null (tracks MC has not yet analysed).
            static void WriteOptStr(Utf8JsonWriter w, string name, string? v) { if (v != null) w.WriteString(name, v); }
            if (f.SmfmScores != null)
            {
                jw.WritePropertyName("smfmScores");
                jw.WriteStartArray();
                foreach (var s in f.SmfmScores) jw.WriteNumberValue(s);
                jw.WriteEndArray();
                WriteOpt(jw, "smfmChannel", (double?)f.SmfmChannel);
                WriteOptStr(jw, "smfmChannelName", f.SmfmChannelName);
                if (f.SmfmBpm.HasValue) jw.WriteNumber("smfmBpm", Math.Round(f.SmfmBpm.Value, 3));
            }
            if (f.Mfcc != null)
            {
                jw.WritePropertyName("mfcc");
                jw.WriteStartArray();
                foreach (var v in f.Mfcc) jw.WriteNumberValue(v);
                jw.WriteEndArray();
            }
            jw.WriteString("lastModified", entry.LastModified.ToString("o"));
            if (entry.AnalysisDurationSecs.HasValue)
            {
                jw.WriteNumber("analysisDuration", Math.Round(entry.AnalysisDurationSecs.Value, 1));
            }
            if (!string.IsNullOrEmpty(entry.FileMd5))
                jw.WriteString("fileMd5", entry.FileMd5);
            if (!string.IsNullOrEmpty(entry.AudioStreamSha256))
            {
                jw.WriteString("audioStreamSha256", entry.AudioStreamSha256);
                // Emit the source signal for the non-default regions: "whole-file"
                // (lower-trust fallback) and "flac-frames" (frame-anchored FLAC hash,
                // 2026-07 algorithm — distinguishes new-style from legacy TagLib-region
                // values during the transition). "invariant" stays implicit.
                if (entry.AudioStreamSha256Source == "whole-file" || entry.AudioStreamSha256Source == "flac-frames")
                    jw.WriteString("audioStreamSha256Source", entry.AudioStreamSha256Source);
            }
            if (entry.FingerprintV1 != null)
                WriteFingerprintV1(jw, entry.FingerprintV1);

            // Phase 4 — TruedatVerdict, computed inline. Emit only when at least one
            // verdict produced a real yes/no decision. "unknown" without source signals
            // (legacy 24-bit FLAC entries lacking Phase 2.5/3 fields, applicability gate
            // passes but no votes -> ResolveVerdict returns "unknown") and "n/a" both
            // suppress the block — neither carries usable information for consumers.
            var verdict = ComputeTruedatVerdict(path, entry);
            bool hiresDecided = verdict.HiresGenuine == "yes" || verdict.HiresGenuine == "no";
            bool transcodeDecided = verdict.LossyTranscodeLikely == "yes" || verdict.LossyTranscodeLikely == "no";
            bool speechDecided = verdict.SpeechLikely == "yes" || verdict.SpeechLikely == "no";
            if (hiresDecided || transcodeDecided || speechDecided)
            {
                jw.WritePropertyName("truedat");
                jw.WriteStartObject();
                jw.WriteString("hiresGenuine", verdict.HiresGenuine);
                if (verdict.HiresConfidence.HasValue)
                    jw.WriteNumber("hiresConfidence", verdict.HiresConfidence.Value);
                jw.WriteString("lossyTranscodeLikely", verdict.LossyTranscodeLikely);
                if (verdict.LossyTranscodeConfidence.HasValue)
                    jw.WriteNumber("lossyTranscodeConfidence", verdict.LossyTranscodeConfidence.Value);
                jw.WriteString("speechLikely", verdict.SpeechLikely);
                if (verdict.SpeechConfidence.HasValue)
                    jw.WriteNumber("speechConfidence", verdict.SpeechConfidence.Value);
                jw.WriteString("speechMethod", verdict.SpeechMethod);
                jw.WriteString("method", verdict.Method);
                jw.WriteEndObject();
            }

            jw.WriteEndObject();
        }

        /// <summary>
        /// Load moods file using JsonDocument — compact read-only DOM, much more
        /// memory-efficient than Newtonsoft's JObject tree. All data is extracted
        /// into allTracks before the document is disposed.
        /// </summary>
        static int LoadExistingMoods(string path, ConcurrentDictionary<string, TrackEntry> allTracks)
        {
            if (!File.Exists(path)) return 0;
            try
            {
                var docOptions = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                using var doc = JsonDocument.Parse(fs, docOptions);
                var root = doc.RootElement;

                if (!root.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Object)
                    return 0;

                foreach (var prop in tracks.EnumerateObject())
                {
                    var filePath = PathHelper.NormalizeSeparators(prop.Name);
                    var track = prop.Value;

                    DateTime lastMod;
                    var lastModStr = GetStr(track, "lastModified");
                    if (string.IsNullOrEmpty(lastModStr))
                    {
                        try { lastMod = File.GetLastWriteTimeUtc(filePath); }
                        catch { continue; }
                    }
                    else if (!DateTime.TryParse(lastModStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out lastMod))
                        continue;

                    var features = ParseTrackFeaturesFromJson(track);
                    features.FilePath = filePath;
                    allTracks[filePath] = new TrackEntry
                    {
                        LastModified = lastMod,
                        Features = features,
                        AnalysisDurationSecs = GetNullableDbl(track, "analysisDuration"),
                        FileMd5 = GetStr(track, "fileMd5") is var md5Str && md5Str.Length > 0 ? md5Str : null,
                        AudioStreamSha256 = GetStr(track, "audioStreamSha256") is var shaStr && shaStr.Length > 0 ? shaStr : null,
                        AudioStreamSha256Source = GetStr(track, "audioStreamSha256Source") is var shaSrc && shaSrc.Length > 0 ? shaSrc : null,
                        FingerprintV1 = ParseFingerprintV1FromJson(track),
                    };
                }
                return allTracks.Count;
            }
            catch (JsonException ex)
            {
                var bakPath = path + $".corrupt.{DateTime.Now:yyyyMMdd.HHmmss}";
                try { File.Copy(path, bakPath); }
                catch (Exception bakEx)
                {
                    Console.WriteLine($"WARNING: Could not create backup: {bakEx.Message}");
                }
                Console.WriteLine();
                Console.WriteLine($"ERROR: Existing moods file is corrupt: {ex.Message}");
                Console.WriteLine($"Backup: {bakPath}");
                Console.WriteLine();
                Console.WriteLine("To start fresh, delete or rename the corrupt file and re-run:");
                Console.WriteLine($"  del \"{path}\"");
                Environment.Exit(1);
                return 0; // unreachable, satisfies compiler
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARNING: Could not load existing moods ({ex.Message})");
                return 0;
            }
        }

        /// <summary>
        /// Parse a per-track JsonElement (as written by WriteTrackEntry) into TrackFeatures.
        /// Mirror of WriteTrackEntry's field-by-field serialization. Used by
        /// LoadExistingMoods to round-trip mbxmoods.json across runs.
        /// </summary>
        static TrackFeatures ParseTrackFeaturesFromJson(JsonElement track)
        {
            // mfcc may appear as a JSON array (current WriteTrackEntry shape) or as
            // a stringified JSON array (legacy mbxmoods.json files written by older
            // builds). Accept both shapes for forward-compat.
            double[]? mfcc = null;
            if (track.TryGetProperty("mfcc", out var mfccEl))
            {
                if (mfccEl.ValueKind == JsonValueKind.Array)
                {
                    mfcc = new double[mfccEl.GetArrayLength()];
                    int idx = 0;
                    foreach (var v in mfccEl.EnumerateArray())
                        mfcc[idx++] = v.GetDouble();
                }
                else if (mfccEl.ValueKind == JsonValueKind.String)
                {
                    var raw = mfccEl.GetString();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        try
                        {
                            using var inner = JsonDocument.Parse(raw!);
                            if (inner.RootElement.ValueKind == JsonValueKind.Array)
                            {
                                mfcc = new double[inner.RootElement.GetArrayLength()];
                                int idx = 0;
                                foreach (var v in inner.RootElement.EnumerateArray())
                                    mfcc[idx++] = v.GetDouble();
                            }
                        }
                        catch { /* leave mfcc null on malformed string */ }
                    }
                }
            }

            return new TrackFeatures
            {
                TrackId = GetInt(track, "trackId"),
                Artist = GetStr(track, "artist"),
                Title = GetStr(track, "title"),
                Album = GetStr(track, "album"),
                Genre = GetStr(track, "genre"),
                Bpm = GetDbl(track, "bpm"),
                Key = GetStr(track, "key"),
                Mode = GetStr(track, "mode"),
                SpectralCentroid = GetDbl(track, "spectralCentroid"),
                SpectralFlux = GetDbl(track, "spectralFlux"),
                Loudness = GetDbl(track, "loudness"),
                Danceability = GetDbl(track, "danceability"),
                OnsetRate = GetDbl(track, "onsetRate"),
                ZeroCrossingRate = GetDbl(track, "zeroCrossingRate"),
                SpectralRms = GetDbl(track, "spectralRms"),
                SpectralFlatness = GetDbl(track, "spectralFlatness"),
                Dissonance = GetDbl(track, "dissonance"),
                PitchSalience = GetDbl(track, "pitchSalience"),
                ChordsChangesRate = GetDbl(track, "chordsChangesRate"),
                Mfcc = mfcc,
                DynamicRange = GetNullableDbl(track, "dynamicRange"),
                DynamicRangeSource = track.TryGetProperty("dynamicRangeSource", out var drs) && drs.ValueKind == JsonValueKind.String ? drs.GetString() : null,
                LoudnessMomentary = GetNullableDbl(track, "loudnessMomentary"),
                LoudnessShortTerm = GetNullableDbl(track, "loudnessShortTerm"),
                ReplayGain = GetNullableDbl(track, "replayGain"),
                SilenceRate20dB = GetNullableDbl(track, "silenceRate20dB"),
                SilenceRate30dB = GetNullableDbl(track, "silenceRate30dB"),
                SilenceRate60dB = GetNullableDbl(track, "silenceRate60dB"),
                SpectralRolloff = GetNullableDbl(track, "spectralRolloff"),
                SpectralComplexity = GetNullableDbl(track, "spectralComplexity"),
                SpectralEntropy = GetNullableDbl(track, "spectralEntropy"),
                SpectralKurtosis = GetNullableDbl(track, "spectralKurtosis"),
                SpectralSkewness = GetNullableDbl(track, "spectralSkewness"),
                SpectralSpread = GetNullableDbl(track, "spectralSpread"),
                SpectralStrongPeak = GetNullableDbl(track, "spectralStrongPeak"),
                SpectralDecrease = GetNullableDbl(track, "spectralDecrease"),
                SpectralEnergy = GetNullableDbl(track, "spectralEnergy"),
                SpectralEnergyLow = GetNullableDbl(track, "spectralEnergyLow"),
                SpectralEnergyMidLow = GetNullableDbl(track, "spectralEnergyMidLow"),
                SpectralEnergyMidHigh = GetNullableDbl(track, "spectralEnergyMidHigh"),
                SpectralEnergyHigh = GetNullableDbl(track, "spectralEnergyHigh"),
                Hfc = GetNullableDbl(track, "hfc"),
                BarkCrest = GetNullableDbl(track, "barkCrest"),
                BarkFlatness = GetNullableDbl(track, "barkFlatness"),
                BarkKurtosis = GetNullableDbl(track, "barkKurtosis"),
                BarkSkewness = GetNullableDbl(track, "barkSkewness"),
                BarkSpread = GetNullableDbl(track, "barkSpread"),
                ErbCrest = GetNullableDbl(track, "erbCrest"),
                ErbFlatness = GetNullableDbl(track, "erbFlatness"),
                ErbKurtosis = GetNullableDbl(track, "erbKurtosis"),
                ErbSkewness = GetNullableDbl(track, "erbSkewness"),
                ErbSpread = GetNullableDbl(track, "erbSpread"),
                MelCrest = GetNullableDbl(track, "melCrest"),
                MelFlatness = GetNullableDbl(track, "melFlatness"),
                MelKurtosis = GetNullableDbl(track, "melKurtosis"),
                MelSkewness = GetNullableDbl(track, "melSkewness"),
                MelSpread = GetNullableDbl(track, "melSpread"),
                BeatsLoudness = GetNullableDbl(track, "beatsLoudness"),
                ChordsStrength = GetNullableDbl(track, "chordsStrength"),
                HpcpCrest = GetNullableDbl(track, "hpcpCrest"),
                HpcpEntropy = GetNullableDbl(track, "hpcpEntropy"),
                // 2026-07-22 tonal/rhythm wave — nullable, absent on legacy entries.
                KeyVoteKrumhansl = ParseKeyVoteFromJson(track, "krumhansl"),
                KeyVoteTemperley = ParseKeyVoteFromJson(track, "temperley"),
                KeyVoteEdma = ParseKeyVoteFromJson(track, "edma"),
                BpmFirstPeak = GetNullableDbl(track, "bpmFirstPeak"),
                BpmFirstPeakWeight = GetNullableDbl(track, "bpmFirstPeakWeight"),
                BpmSecondPeak = GetNullableDbl(track, "bpmSecondPeak"),
                BpmSecondPeakWeight = GetNullableDbl(track, "bpmSecondPeakWeight"),
                BpmSecondPeakSpread = GetNullableDbl(track, "bpmSecondPeakSpread"),
                ChordsKey = GetStr(track, "chordsKey") is var chk && chk.Length > 0 ? chk : null,
                ChordsScale = GetStr(track, "chordsScale") is var chs && chs.Length > 0 ? chs : null,
                ChordsHistogram = track.TryGetProperty("chordsHistogram", out var chArr) && chArr.ValueKind == JsonValueKind.Array
                    ? chArr.EnumerateArray().Select(e => e.GetDouble()).ToArray()
                    : null,
                ChordsNumberRate = GetNullableDbl(track, "chordsNumberRate"),
                TuningFrequency = GetNullableDbl(track, "tuningFrequency"),
                TuningEqualTemperedDeviation = GetNullableDbl(track, "tuningEqualTemperedDeviation"),
                TuningDiatonicStrength = GetNullableDbl(track, "tuningDiatonicStrength"),
                TuningNontemperedEnergyRatio = GetNullableDbl(track, "tuningNontemperedEnergyRatio"),
                AverageLoudness = GetNullableDbl(track, "averageLoudness"),
                BitUsage = ParseBitUsageFromJson(track),
                HfEnergyRatio = GetNullableDbl(track, "hfEnergyRatio"),
                HfEnergyMethod = GetStr(track, "hfEnergyMethod") is var hem && hem.Length > 0 ? hem : null,
                HfSpectralStructure = ParseHfSpectralStructureFromJson(track),
                // Read new smfm* keys; fall back to legacy sensme* for not-yet-migrated libraries.
                SmfmScores = (track.TryGetProperty("smfmScores", out var ssEl) || track.TryGetProperty("sensmeScores", out ssEl)) && ssEl.ValueKind == JsonValueKind.Array
                    ? ssEl.EnumerateArray().Select(e => e.GetInt32()).ToArray()
                    : null,
                SmfmChannel     = GetNullableInt(track, "smfmChannel") ?? GetNullableInt(track, "sensmeChannel"),
                SmfmChannelName = GetStr(track, "smfmChannelName") is var scn && scn.Length > 0 ? scn
                                  : (GetStr(track, "sensmeChannelName") is var scnOld && scnOld.Length > 0 ? scnOld : null),
                SmfmBpm           = GetNullableDbl(track, "smfmBpm"),
            };
        }

        /// <summary>Read one profile's vote from the nested keyVotes block.
        /// Returns null when the block or profile is absent (legacy entries).</summary>
        static KeyVote? ParseKeyVoteFromJson(JsonElement track, string profile)
        {
            if (!track.TryGetProperty("keyVotes", out var kv) || kv.ValueKind != JsonValueKind.Object) return null;
            if (!kv.TryGetProperty(profile, out var v) || v.ValueKind != JsonValueKind.Object) return null;
            try
            {
                return new KeyVote { Key = GetStr(v, "key"), Scale = GetStr(v, "scale"), Strength = GetDbl(v, "strength") };
            }
            catch { return null; }
        }

        /// <summary>Phase 5 — read the nested hfSpectralStructure block back from
        /// mbxmoods.json. Returns null when absent (legacy entries) or malformed.
        /// Tolerant of missing fields within the block.</summary>
        static HfSpectralStructure? ParseHfSpectralStructureFromJson(JsonElement track)
        {
            if (!track.TryGetProperty("hfSpectralStructure", out var b) || b.ValueKind != JsonValueKind.Object) return null;
            try
            {
                return new HfSpectralStructure
                {
                    Flatness = GetDbl(b, "flatness"),
                    PeakToMean = GetDbl(b, "peakToMean"),
                    ImagingSymmetry = GetDbl(b, "imagingSymmetry"),
                    Method = GetStr(b, "method"),
                };
            }
            catch { return null; }
        }

        /// <summary>Phase 2.5 — read the nested bitUsage block back from mbxmoods.json.
        /// Returns null when the block is absent (legacy entries) or malformed. Tolerant
        /// of missing fields within the block — defaults are zero, never throws.</summary>
        static BitUsageSummary? ParseBitUsageFromJson(JsonElement track)
        {
            if (!track.TryGetProperty("bitUsage", out var b) || b.ValueKind != JsonValueKind.Object) return null;
            try
            {
                return new BitUsageSummary
                {
                    LowestNonZeroBit = GetInt(b, "lowestNonZeroBit"),
                    BottomBitActivity = GetDbl(b, "bottomBitActivity"),
                    EffectiveBits = GetDbl(b, "effectiveBits"),
                    SamplesAnalyzed = GetInt(b, "samplesAnalyzed"),
                    Method = GetStr(b, "method"),
                };
            }
            catch { return null; }
        }

        static string ExtractEssentiaError(string stderr, int exitCode)
        {
            if (string.IsNullOrWhiteSpace(stderr)) return $"Exit code {exitCode}";
            var lines = stderr.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var errorLines = new List<string>();
            foreach (var line in lines)
            {
                var trimmed = line.TrimStart();
                if (!trimmed.StartsWith("[   INFO   ]") &&
                    !trimmed.StartsWith("[  DEBUG  ]") &&
                    !trimmed.StartsWith("[  INFO  ]") &&
                    !string.IsNullOrWhiteSpace(line))
                {
                    errorLines.Add(line);
                }
            }

            if (errorLines.Count > 0)
            {
                int start = Math.Max(0, errorLines.Count - 3);
                return string.Join(" | ", errorLines.GetRange(start, errorLines.Count - start));
            }
            return $"Exit code {exitCode} (stderr: {lines.Last()})";
        }

        /// <summary>
        /// Run Essentia extractor on an audio file. Uses CPU activity monitoring
        /// instead of arbitrary timeouts. Creates hardlink for non-ASCII paths
        /// when 8.3 short names aren't available.
        /// </summary>
        static (TrackFeatures? Features, string? Error) AnalyzeWithEssentia(string essentiaExe, string audioPath, long fileSizeBytes, CancellationToken ct = default)
        {
            if (!File.Exists(audioPath))
                return (null, "File not found");

            var result = AnalyzeWithEssentiaCore(essentiaExe, audioPath, fileSizeBytes, ct);

            // Multi-channel files: downmix to stereo and retry
            if (result.Features == null && result.Error != null && result.Error.Contains("more than 2 channels"))
            {
                var stereoPath = DownmixToStereo(audioPath);
                if (stereoPath != null)
                {
                    try
                    {
                        Console.WriteLine($"  Downmixing to stereo (multi-channel detected)");
                        var stereoSize = new FileInfo(stereoPath).Length;
                        result = AnalyzeWithEssentiaCore(essentiaExe, stereoPath, stereoSize, ct);
                    }
                    finally
                    {
                        try { File.Delete(stereoPath); } catch { }
                    }
                }
                else if (_ffmpegPath.Value == null)
                {
                    result = (null, result.Error + " (install ffmpeg on PATH to auto-downmix)");
                }
            }

            // Unsupported codec (e.g. .opus — essentia's AudioLoader lacks libopus):
            // transcode to WAV via ffmpeg and retry. Same pattern as the multi-channel
            // branch; DownmixToStereo writes a clean stereo WAV which essentia can read.
            if (result.Features == null && result.Error != null && result.Error.Contains("Unsupported codec"))
            {
                var wavPath = DownmixToStereo(audioPath);
                if (wavPath != null)
                {
                    try
                    {
                        Console.WriteLine($"  Transcoding via ffmpeg (unsupported codec detected)");
                        var wavSize = new FileInfo(wavPath).Length;
                        result = AnalyzeWithEssentiaCore(essentiaExe, wavPath, wavSize, ct);
                    }
                    finally
                    {
                        try { File.Delete(wavPath); } catch { }
                    }
                }
                else if (_ffmpegPath.Value == null)
                {
                    result = (null, result.Error + " (install ffmpeg on PATH to auto-transcode)");
                }
            }

            return result;
        }

        static (TrackFeatures? Features, string? Error) AnalyzeWithEssentiaCore(string essentiaExe, string audioPath, long fileSizeBytes, CancellationToken ct = default)
        {
            string toolPath = SafePath(audioPath);
            string? tempLink = null;
            string pathMethod = toolPath == audioPath ? "original" : "8.3";

            // Hardlink fallback: needed when 8.3 still has non-ASCII, OR when 8.3
            // truncated the extension (e.g. .flac -> .FLA breaks Essentia format detection)
            if (HasNonAscii(toolPath) ||
                !string.Equals(Path.GetExtension(toolPath), Path.GetExtension(audioPath), StringComparison.OrdinalIgnoreCase))
            {
                var (link, method) = TryCreateHardlink(audioPath);
                pathMethod = method;
                if (link != null) { toolPath = link; tempLink = link; }
            }
            if (_audit && pathMethod != "original")
                Console.Error.WriteLine($"  DEBUG path: {pathMethod} -> {toolPath}");

            var tempJson = Path.GetTempFileName();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = essentiaExe,
                    Arguments = $"{PathHelper.QuoteArg(toolPath)} {PathHelper.QuoteArg(tempJson)}",
                    // Redirect (and drain) Essentia's stdout — without this, essentia's own
                    // progress prints inherit truedat's stdout and pollute the JSON channel
                    // in --analyze-file --json-output mode (Shell parses stdout as a single
                    // JSON document; essentia's trailing "Done" line breaks JsonDocument.Parse).
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var timer = Stopwatch.StartNew();
                using var proc = Process.Start(psi);
                if (proc == null) return (null, "Failed to start Essentia process");
                ApplyCpuLimit(proc);
                var pid = proc.Id;

                var stderrTask = proc.StandardError.ReadToEndAsync();
                // Drain stdout so a chatty essentia build can't deadlock on a full pipe
                // buffer. Result is discarded — essentia's per-file output is in tempJson.
                var stdoutDrainTask = proc.StandardOutput.ReadToEndAsync();

                // CPU activity monitoring — kill the subprocess if it stops burning CPU
                const int pollMs = 5000;
                const int maxIdlePolls = 12; // 60s of no CPU activity
                var lastCpu = TimeSpan.Zero;
                int idleCount = 0;

                while (!proc.WaitForExit(pollMs))
                {
                    if (ct.IsCancellationRequested)
                    {
                        try { proc.Kill(); proc.WaitForExit(3000); } catch { }
                        return (null, "Cancelled");
                    }
                    try
                    {
                        proc.Refresh();
                        var cpu = proc.TotalProcessorTime;
                        if (cpu > lastCpu)
                        {
                            lastCpu = cpu;
                            idleCount = 0;
                        }
                        else
                        {
                            idleCount++;
                            if (idleCount >= maxIdlePolls)
                            {
                                try { proc.Kill(); proc.WaitForExit(5000); } catch { }
                                timer.Stop();
                                var partialStderr = stderrTask.Wait(3000) ? stderrTask.Result : "";
                                var sizeMb = fileSizeBytes / (1024.0 * 1024.0);
                                Console.Error.WriteLine($"  DEBUG watchdog: killed stalled essentia after {timer.Elapsed.TotalSeconds:F0}s");
                                Console.Error.WriteLine($"    exe:    {Path.GetFileName(essentiaExe)}");
                                Console.Error.WriteLine($"    path:   {toolPath}");
                                Console.Error.WriteLine($"    method: {pathMethod}");
                                Console.Error.WriteLine($"    size:   {sizeMb:F1} MB");
                                Console.Error.WriteLine($"    cpu:    {lastCpu.TotalSeconds:F1}s total before stall");
                                Console.Error.WriteLine($"    stderr: {(partialStderr.Length > 0 ? $"[{partialStderr.Substring(0, Math.Min(300, partialStderr.Length))}]" : "(empty)")}");
                                var hint = !string.IsNullOrWhiteSpace(partialStderr) ? $" | {ExtractEssentiaError(partialStderr, -1)}" : "";
                                return (null, $"Process stalled after {timer.Elapsed.TotalSeconds:F0}s (no CPU for 60s, PID {pid}, {sizeMb:F0} MB){hint}");
                            }
                        }
                    }
                    catch { break; }
                }

                // Flush async stderr read buffer before reading the result.
                proc.WaitForExit();

                var stderr = stderrTask.Wait(5000) ? stderrTask.Result : "";
                timer.Stop();
                var exitCode = proc.ExitCode;

                if (exitCode != 0)
                {
                    var sizeMb = fileSizeBytes / (1024.0 * 1024.0);
                    var errorMsg = ExtractEssentiaError(stderr, exitCode);
                    Console.Error.WriteLine($"  DEBUG essentia: exit {exitCode}, method={pathMethod}, path={toolPath}");
                    if (stderr.Length > 0) Console.Error.WriteLine($"    stderr: [{stderr.Substring(0, Math.Min(300, stderr.Length))}]");
                    return (null, $"{errorMsg} (exit {exitCode}, PID {pid}, {sizeMb:F0} MB, {timer.Elapsed.TotalSeconds:F1}s)");
                }

                if (!File.Exists(tempJson) || new FileInfo(tempJson).Length == 0)
                {
                    Console.Error.WriteLine($"  DEBUG essentia: exit 0 but empty output, method={pathMethod}, path={toolPath}");
                    Console.Error.WriteLine($"    cpu:    {lastCpu.TotalSeconds:F1}s, wall: {timer.Elapsed.TotalSeconds:F1}s");
                    Console.Error.WriteLine($"    stderr: {(stderr.Length > 0 ? $"[{stderr.Substring(0, Math.Min(300, stderr.Length))}]" : "(empty)")}");
                    return (null, $"Empty output from Essentia ({ExtractEssentiaError(stderr, 0)})");
                }

                var json = File.ReadAllText(tempJson);
                var features = ParseEssentiaOutput(json);
                if (features != null) return (features, null);

                var jsonSize = new FileInfo(tempJson).Length;
                Console.Error.WriteLine($"  DEBUG essentia: exit 0, output unparseable ({jsonSize} bytes), method={pathMethod}, path={toolPath}");
                Console.Error.WriteLine($"    stderr: {(stderr.Length > 0 ? $"[{stderr.Substring(0, Math.Min(300, stderr.Length))}]" : "(empty)")}");
                var parseHint = !string.IsNullOrWhiteSpace(stderr) ? ExtractEssentiaError(stderr, 0) : $"output {jsonSize} bytes";
                return (null, $"Failed to parse Essentia output ({parseHint})");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  DEBUG essentia: exception, method={pathMethod}, path={toolPath}");
                Console.Error.WriteLine($"    error:  {ex.Message}");
                return (null, $"Exception: {ex.Message}");
            }
            finally
            {
                try { File.Delete(tempJson); } catch { }
                if (tempLink != null)
                {
                    try { RetryDelete(tempLink); }
                    catch (Exception ex) { Console.Error.WriteLine($"  WARNING: failed to delete hardlink {tempLink}: {ex.Message}"); }
                }
            }
        }

        static TrackFeatures? ParseEssentiaOutput(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var bpm = NavDbl(root, "rhythm.bpm");
                var key = NavStr(root, "tonal.key_edma.key");
                var keySource = "edma";
                if (key == "") { key = NavStr(root, "tonal.key_krumhansl.key"); keySource = "krumhansl"; }
                if (key == "") { key = NavStr(root, "tonal.chords_key"); keySource = "chords"; }
                if (key == "") keySource = "missing";
                var scale = NavStr(root, "tonal.key_edma.scale");
                var scaleSource = "edma";
                if (scale == "") { scale = NavStr(root, "tonal.key_krumhansl.scale"); scaleSource = "krumhansl"; }
                if (scale == "") { scale = NavStr(root, "tonal.chords_scale"); scaleSource = "chords"; }
                if (scale == "") scaleSource = "missing";
                var loudness = NavDbl(root, "lowlevel.loudness_ebu128.integrated", double.NaN);
                var loudnessSource = "ebu128";
                if (double.IsNaN(loudness)) { loudness = NavDbl(root, "lowlevel.average_loudness", -20); loudnessSource = "average fallback"; }
                var spectralCentroidMean = NavDbl(root, "lowlevel.spectral_centroid.mean", 2000);
                var spectralFluxMean = NavDbl(root, "lowlevel.spectral_flux.mean", 0.1);
                var danceability = NavDbl(root, "rhythm.danceability", 0.5);
                var onsetRate = NavDbl(root, "rhythm.onset_rate");
                var zeroCrossingRate = NavDbl(root, "lowlevel.zerocrossingrate.mean");
                var spectralRms = NavDbl(root, "lowlevel.spectral_rms.mean");
                var spectralFlatness = NavDbl(root, "lowlevel.spectral_flatness_db.mean");
                var dissonance = NavDbl(root, "lowlevel.dissonance.mean");
                var pitchSalience = NavDbl(root, "lowlevel.pitch_salience.mean");
                var chordsChangesRate = NavDbl(root, "tonal.chords_changes_rate");
                // EBU R128 Loudness Range (LRA in LU). Essentia's streaming extractor emits
                // this alongside `integrated` in the `lowlevel.loudness_ebu128` block when the
                // preset has it enabled. NaN signals "block absent"; plugin side falls back.
                var loudnessRange = NavDbl(root, "lowlevel.loudness_ebu128.loudness_range", double.NaN);

                // Extended features — all nullable, propagate NaN/missing as null so legacy
                // consumers don't see bogus zeros. Precision is picked per field: dB/LU values
                // at 2 dp, Hz at 1 dp, tiny spectral energies at 6 dp, everything else at 4 dp.
                static double? Opt(double v, int dp = 4) => double.IsNaN(v) ? (double?)null : Math.Round(v, dp);
                static double? OptN(JsonElement r, string p, int dp = 4) => Opt(NavDbl(r, p, double.NaN), dp);

                var loudnessMomentary = OptN(root, "lowlevel.loudness_ebu128.momentary.mean", 2);
                var loudnessShortTerm = OptN(root, "lowlevel.loudness_ebu128.short_term.mean", 2);
                var replayGain = OptN(root, "metadata.audio_properties.replay_gain", 2);
                var silenceRate20dB = OptN(root, "lowlevel.silence_rate_20dB.mean");
                var silenceRate30dB = OptN(root, "lowlevel.silence_rate_30dB.mean");
                var silenceRate60dB = OptN(root, "lowlevel.silence_rate_60dB.mean");
                var spectralRolloff = OptN(root, "lowlevel.spectral_rolloff.mean", 1);
                var spectralComplexity = OptN(root, "lowlevel.spectral_complexity.mean", 2);
                var spectralEntropy = OptN(root, "lowlevel.spectral_entropy.mean");
                var spectralKurtosis = OptN(root, "lowlevel.spectral_kurtosis.mean");
                var spectralSkewness = OptN(root, "lowlevel.spectral_skewness.mean");
                var spectralSpread = OptN(root, "lowlevel.spectral_spread.mean");
                var spectralStrongPeak = OptN(root, "lowlevel.spectral_strongpeak.mean");
                var spectralDecrease = OptN(root, "lowlevel.spectral_decrease.mean", 6);
                var spectralEnergy = OptN(root, "lowlevel.spectral_energy.mean", 6);
                var spectralEnergyLow = OptN(root, "lowlevel.spectral_energyband_low.mean", 6);
                var spectralEnergyMidLow = OptN(root, "lowlevel.spectral_energyband_middle_low.mean", 6);
                var spectralEnergyMidHigh = OptN(root, "lowlevel.spectral_energyband_middle_high.mean", 6);
                var spectralEnergyHigh = OptN(root, "lowlevel.spectral_energyband_high.mean", 6);
                var hfc = OptN(root, "lowlevel.hfc.mean", 2);
                var barkCrest = OptN(root, "lowlevel.barkbands_crest.mean");
                var barkFlatness = OptN(root, "lowlevel.barkbands_flatness_db.mean");
                var barkKurtosis = OptN(root, "lowlevel.barkbands_kurtosis.mean");
                var barkSkewness = OptN(root, "lowlevel.barkbands_skewness.mean");
                var barkSpread = OptN(root, "lowlevel.barkbands_spread.mean");
                var erbCrest = OptN(root, "lowlevel.erbbands_crest.mean");
                var erbFlatness = OptN(root, "lowlevel.erbbands_flatness_db.mean");
                var erbKurtosis = OptN(root, "lowlevel.erbbands_kurtosis.mean");
                var erbSkewness = OptN(root, "lowlevel.erbbands_skewness.mean");
                var erbSpread = OptN(root, "lowlevel.erbbands_spread.mean");
                var melCrest = OptN(root, "lowlevel.melbands_crest.mean");
                var melFlatness = OptN(root, "lowlevel.melbands_flatness_db.mean");
                var melKurtosis = OptN(root, "lowlevel.melbands_kurtosis.mean");
                var melSkewness = OptN(root, "lowlevel.melbands_skewness.mean");
                var melSpread = OptN(root, "lowlevel.melbands_spread.mean");
                var beatsLoudness = OptN(root, "rhythm.beats_loudness.mean", 2);
                var chordsStrength = OptN(root, "tonal.chords_strength.mean");
                var hpcpCrest = OptN(root, "tonal.hpcp_crest.mean", 2);
                var hpcpEntropy = OptN(root, "tonal.hpcp_entropy.mean");

                // 2026-07-22 wave — key votes (all three tonal profiles), bpm-histogram
                // peaks (half/double-time evidence), chord tonality, tuning, simple
                // loudness. All optional; omitted when the extractor didn't emit them.
                KeyVote? Vote(string profile)
                {
                    var vk = NavStr(root, $"tonal.{profile}.key");
                    var vs = NavStr(root, $"tonal.{profile}.scale");
                    var vst = NavDbl(root, $"tonal.{profile}.strength", double.NaN);
                    if (vk == "" || vs == "" || double.IsNaN(vst)) return null;
                    return new KeyVote { Key = vk, Scale = vs, Strength = Math.Round(vst, 4) };
                }
                var keyVoteKrumhansl = Vote("key_krumhansl");
                var keyVoteTemperley = Vote("key_temperley");
                var keyVoteEdma = Vote("key_edma");
                var bpmFirstPeak = OptN(root, "rhythm.bpm_histogram_first_peak_bpm", 1);
                var bpmFirstPeakWeight = OptN(root, "rhythm.bpm_histogram_first_peak_weight");
                var bpmSecondPeak = OptN(root, "rhythm.bpm_histogram_second_peak_bpm", 1);
                var bpmSecondPeakWeight = OptN(root, "rhythm.bpm_histogram_second_peak_weight");
                var bpmSecondPeakSpread = OptN(root, "rhythm.bpm_histogram_second_peak_spread");
                var chordsKeyStr = NavStr(root, "tonal.chords_key") is var ckv && ckv.Length > 0 ? ckv : null;
                var chordsScaleStr = NavStr(root, "tonal.chords_scale") is var csv && csv.Length > 0 ? csv : null;
                double[]? chordsHistogram = null;
                var chEl = NavigatePath(root, "tonal.chords_histogram");
                if (chEl.HasValue && chEl.Value.ValueKind == JsonValueKind.Array)
                {
                    chordsHistogram = chEl.Value.EnumerateArray().Select(v => Math.Round(v.GetDouble(), 4)).ToArray();
                    if (chordsHistogram.Length == 0) chordsHistogram = null;
                }
                var chordsNumberRate = OptN(root, "tonal.chords_number_rate");
                var tuningFrequency = OptN(root, "tonal.tuning_frequency", 2);
                var tuningEqualTemperedDeviation = OptN(root, "tonal.tuning_equal_tempered_deviation");
                var tuningDiatonicStrength = OptN(root, "tonal.tuning_diatonic_strength");
                var tuningNontemperedEnergyRatio = OptN(root, "tonal.tuning_nontempered_energy_ratio");
                var averageLoudness = OptN(root, "lowlevel.average_loudness");

                double[]? mfcc = null;
                var mfccEl = NavigatePath(root, "lowlevel.mfcc.mean");
                if (mfccEl.HasValue && mfccEl.Value.ValueKind == JsonValueKind.Array)
                {
                    var arr = mfccEl.Value;
                    mfcc = new double[arr.GetArrayLength()];
                    int idx = 0;
                    foreach (var v in arr.EnumerateArray())
                        mfcc[idx++] = v.GetDouble();
                }

                if (_audit)
                {
                    var notes = new List<string>();
                    if (keySource != "edma") notes.Add($"key: {keySource} fallback");
                    if (scaleSource != "edma") notes.Add($"scale: {scaleSource} fallback");
                    if (loudnessSource != "ebu128") notes.Add($"loudness: {loudnessSource}");
                    if (mfcc != null && mfcc.Length > 0) notes.Add($"mfcc: {mfcc.Length} coefficients");
                    else notes.Add("mfcc: missing");
                    var notesStr = notes.Count > 0 ? " " + string.Join(" ", notes.Select(n => $"[{n}]")) : "";
                    Console.WriteLine($"  DEBUG extract: bpm={Math.Round(bpm, 1)} key={key}{scale}{notesStr}");
                }

                return new TrackFeatures
                {
                    Bpm = Math.Round(bpm, 1), Key = key, Mode = scale,
                    SpectralCentroid = Math.Round(spectralCentroidMean, 1),
                    SpectralFlux = Math.Round(spectralFluxMean, 4),
                    Loudness = Math.Round(loudness, 2),
                    Danceability = Math.Round(danceability, 4),
                    OnsetRate = Math.Round(onsetRate, 2),
                    ZeroCrossingRate = Math.Round(zeroCrossingRate, 6),
                    SpectralRms = Math.Round(spectralRms, 6),
                    SpectralFlatness = Math.Round(spectralFlatness, 6),
                    Dissonance = Math.Round(dissonance, 4),
                    PitchSalience = Math.Round(pitchSalience, 4),
                    ChordsChangesRate = Math.Round(chordsChangesRate, 4),
                    Mfcc = mfcc?.Select(v => Math.Round(v, 4)).ToArray(),
                    DynamicRange = double.IsNaN(loudnessRange) ? (double?)null : Math.Round(loudnessRange, 2),
                    DynamicRangeSource = double.IsNaN(loudnessRange) ? null : "essentia-lra",
                    // Extended fields
                    LoudnessMomentary = loudnessMomentary,
                    LoudnessShortTerm = loudnessShortTerm,
                    ReplayGain = replayGain,
                    SilenceRate20dB = silenceRate20dB,
                    SilenceRate30dB = silenceRate30dB,
                    SilenceRate60dB = silenceRate60dB,
                    SpectralRolloff = spectralRolloff,
                    SpectralComplexity = spectralComplexity,
                    SpectralEntropy = spectralEntropy,
                    SpectralKurtosis = spectralKurtosis,
                    SpectralSkewness = spectralSkewness,
                    SpectralSpread = spectralSpread,
                    SpectralStrongPeak = spectralStrongPeak,
                    SpectralDecrease = spectralDecrease,
                    SpectralEnergy = spectralEnergy,
                    SpectralEnergyLow = spectralEnergyLow,
                    SpectralEnergyMidLow = spectralEnergyMidLow,
                    SpectralEnergyMidHigh = spectralEnergyMidHigh,
                    SpectralEnergyHigh = spectralEnergyHigh,
                    Hfc = hfc,
                    BarkCrest = barkCrest,
                    BarkFlatness = barkFlatness,
                    BarkKurtosis = barkKurtosis,
                    BarkSkewness = barkSkewness,
                    BarkSpread = barkSpread,
                    ErbCrest = erbCrest,
                    ErbFlatness = erbFlatness,
                    ErbKurtosis = erbKurtosis,
                    ErbSkewness = erbSkewness,
                    ErbSpread = erbSpread,
                    MelCrest = melCrest,
                    MelFlatness = melFlatness,
                    MelKurtosis = melKurtosis,
                    MelSkewness = melSkewness,
                    MelSpread = melSpread,
                    BeatsLoudness = beatsLoudness,
                    ChordsStrength = chordsStrength,
                    HpcpCrest = hpcpCrest,
                    HpcpEntropy = hpcpEntropy,
                    KeyVoteKrumhansl = keyVoteKrumhansl,
                    KeyVoteTemperley = keyVoteTemperley,
                    KeyVoteEdma = keyVoteEdma,
                    BpmFirstPeak = bpmFirstPeak,
                    BpmFirstPeakWeight = bpmFirstPeakWeight,
                    BpmSecondPeak = bpmSecondPeak,
                    BpmSecondPeakWeight = bpmSecondPeakWeight,
                    BpmSecondPeakSpread = bpmSecondPeakSpread,
                    ChordsKey = chordsKeyStr,
                    ChordsScale = chordsScaleStr,
                    ChordsHistogram = chordsHistogram,
                    ChordsNumberRate = chordsNumberRate,
                    TuningFrequency = tuningFrequency,
                    TuningEqualTemperedDeviation = tuningEqualTemperedDeviation,
                    TuningDiatonicStrength = tuningDiatonicStrength,
                    TuningNontemperedEnergyRatio = tuningNontemperedEnergyRatio,
                    AverageLoudness = averageLoudness
                };
            }
            catch (Exception ex)
            {
                if (_audit) Console.WriteLine($"  DEBUG extract: parse failed: {ex.Message}");
                return null;
            }
        }

        // -- JSON helpers -----------------------------------------------------

        /// <summary>Navigate a dot-separated path like "rhythm.bpm" through nested JSON objects.</summary>
        static JsonElement? NavigatePath(JsonElement root, string dottedPath)
        {
            var current = root;
            foreach (var part in dottedPath.Split('.'))
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out var next))
                    return null;
                current = next;
            }
            return current;
        }

        static double NavDbl(JsonElement root, string path, double def = 0)
        {
            var el = NavigatePath(root, path);
            return el.HasValue && el.Value.ValueKind == JsonValueKind.Number ? el.Value.GetDouble() : def;
        }

        static string NavStr(JsonElement root, string path)
        {
            var el = NavigatePath(root, path);
            return el.HasValue && el.Value.ValueKind == JsonValueKind.String ? el.Value.GetString() ?? "" : "";
        }

        static string GetStr(JsonElement el, string name)
        {
            return el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        }

        static double GetDbl(JsonElement el, string name, double def = 0)
        {
            return el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : def;
        }

        static int GetInt(JsonElement el, string name, int def = 0)
        {
            return el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : def;
        }

        static double? GetNullableDbl(JsonElement el, string name)
        {
            return el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : (double?)null;
        }

        static int? GetNullableInt(JsonElement el, string name)
        {
            return el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : (int?)null;
        }

        // -- Utility helpers --------------------------------------------------

        static string? ComputeFileMd5(string path)
        {
            try
            {
                using var md5 = MD5.Create();
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                var hash = md5.ComputeHash(fs);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
            catch { return null; }
        }

        /// <summary>Composite cheap fingerprint for a file. Sub-10ms per file on warm cache.</summary>
        internal sealed class FingerprintV1
        {
            public long FileSize;
            public string PathTail = "";
            public int DurationMs;
            public int SampleRate;
            public int Channels;
            public int BitDepth;          // TagLib BitsPerSample; 0 when unknown / lossy codec doesn't report
            public string Codec = "other";
            public string? CodecRaw;
            public int Bitrate;
            public string? Encoder;       // normalized: e.g. "LAME3.100", "libFLAC 1.3.3", "Lavf58.76.100"
            public string? EncoderRaw;    // unparsed tag value when Encoder normalization couldn't classify
            // MP3 LAME tag — populated only when codec == "mp3" AND the file contains a valid
            // Xing/Info+LAME header. Phase 2 plumbing for transcode detection.
            public string? Mp3LameVersion;    // "LAME3.100" etc.; differs from Encoder which may say "Lavc..."
            public int Mp3LowpassHz;          // 0 when unknown; typical 16000–20500
            public string? Mp3VbrMethod;      // "CBR" / "ABR" / "VBR (method N)" / "CBR 2-pass" etc.
            public byte Mp3VbrMethodCode;     // raw byte for downstream consumers
            public int Mp3EncoderDelay;       // samples
            public int Mp3EncoderPadding;     // samples
            public int Mp3MusicCrc;           // CRC-16 over the music data; useful as a re-encode tell
            public byte Mp3InfoTagRevision;   // LAME tag revision byte (high nibble of rev/vbr byte)
            public string AudioHead64kMd5 = "";
            public string AudioHead64kMd5Source = "invariant"; // "invariant" | "whole-file-start"
            public long InvariantStart;
            public long InvariantEnd;
        }

        /// <summary>One row in the identity-backfill registry. Each spec describes a
        /// single FingerprintV1 field that can be lifted cheaply from TagLib alone (no
        /// Essentia, no audio decode). Adding a future identity field that meets the
        /// "TagLib-only" bar means appending one entry to <see cref="IdentityFields"/>;
        /// no other touch-points in the backfill walker.</summary>
        internal sealed class IdentityFieldSpec
        {
            public string Name = "";                                       // diagnostic / verify CSV
            public Func<FingerprintV1, bool> IsPresent = _ => true;        // true when already populated
            public Action<TagLib.File, FingerprintV1> Populate = (_, __) => { };
        }

        /// <summary>Phase-1 identity fields backfilled from TagLib. Order is cosmetic
        /// (drives the CSV "backfilledFields" join order). To add a future identity
        /// field that's TagLib-readable, append a new spec here + add the field to
        /// FingerprintV1 + WriteFingerprintV1 + ParseFingerprintV1FromJson — same
        /// four-surfaces rule as any FingerprintV1 change.
        /// Phase-2 MP3 LAME tag fields don't use TagLib (raw file bytes) so they
        /// have their own populator path in ApplyMp3LameBackfill — kept off this
        /// list to preserve the "TagLib-only" semantics callers depend on.</summary>
        static readonly IdentityFieldSpec[] IdentityFields = new[]
        {
            new IdentityFieldSpec
            {
                Name = "bitDepth",
                // Lossy codecs don't carry a bit depth — they're "complete" by definition.
                // Without this guard, every backfill pass would retry the populate on MP3/AAC/Opus
                // entries forever (BitDepth stays 0; IsPresent returns false; spurious BACKFILLED report).
                IsPresent = fp => fp.BitDepth > 0 || CodecLacksBitDepth(fp.Codec),
                Populate  = (tf, fp) => fp.BitDepth = tf.Properties?.BitsPerSample ?? 0,
            },
            new IdentityFieldSpec
            {
                Name = "encoder",
                IsPresent = fp => !string.IsNullOrEmpty(fp.Encoder) || !string.IsNullOrEmpty(fp.EncoderRaw),
                Populate  = (tf, fp) =>
                {
                    var (e, er) = NormalizeEncoder(tf);
                    fp.Encoder = e;
                    fp.EncoderRaw = er;
                },
            },
        };

        /// <summary>Codecs where TagLib's BitsPerSample is always 0 because the format
        /// is bitstream-compressed and has no per-sample-bit concept. These count as
        /// "field complete" for backfill purposes — IsPresent returns true even at 0.</summary>
        static bool CodecLacksBitDepth(string? codec) => codec switch
        {
            "mp3" => true,
            "aac" => true,
            "opus" => true,
            "vorbis" => true,
            "ogg" => true,
            "wma" => true,
            "mpc" => true,
            _ => false,
        };

        /// <summary>Result of a successful Mp3LameTag parse. Defaults are "unknown"
        /// values so the caller can copy unconditionally; consumers omit-when-zero.</summary>
        internal sealed class Mp3LameTagInfo
        {
            public string? LameVersion;    // e.g. "LAME3.100"
            public byte InfoTagRevision;
            public byte VbrMethodCode;
            public string? VbrMethod;      // human-friendly classification of the byte
            public int LowpassHz;
            public int EncoderDelay;
            public int EncoderPadding;
            public int MusicCrc;
        }

        /// <summary>Pure-managed MP3 Xing/Info+LAME tag reader. Reads ~4 KB from the
        /// start of the file, skips any ID3v2 header, finds the first MPEG frame,
        /// parses the side-info length, locates the Xing/Info magic, walks the
        /// optional fields, and reads the LAME tag bytes appended after.
        ///
        /// Returns null when the file isn't MP3, has no Xing/Info header (raw CBR
        /// without info tag — uncommon for modern encoders), or fails any structural
        /// sanity check. Caller treats null as "no LAME info available" — not an
        /// error. Doesn't validate the LAME tag CRC (some buggy encoders write a
        /// wrong CRC over otherwise-good payload; we'd rather accept slightly-bad
        /// signal than reject it outright).</summary>
        internal static class Mp3LameTagParser
        {
            // ID3v2 footer flag (0x10 in the flags byte) means there's an extra 10-byte
            // footer copy at the end of the ID3 tag. We don't usually see this, but
            // honoring it is cheap.
            const int Id3v2FooterFlag = 0x10;

            public static Mp3LameTagInfo? TryParse(string filePath)
            {
                byte[] buf;
                try
                {
                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.SequentialScan);
                    buf = new byte[Math.Min(8192, fs.Length)];
                    int total = 0;
                    while (total < buf.Length)
                    {
                        int r = fs.Read(buf, total, buf.Length - total);
                        if (r <= 0) break;
                        total += r;
                    }
                    if (total < 64) return null;
                    if (total < buf.Length) Array.Resize(ref buf, total);
                }
                catch { return null; }

                int p = 0;
                // Skip ID3v2 tag if present at file start.
                if (buf.Length >= 10 && buf[0] == 'I' && buf[1] == 'D' && buf[2] == '3')
                {
                    int size = ((buf[6] & 0x7F) << 21) | ((buf[7] & 0x7F) << 14) | ((buf[8] & 0x7F) << 7) | (buf[9] & 0x7F);
                    int tagLen = 10 + size + (((buf[5] & Id3v2FooterFlag) != 0) ? 10 : 0);
                    p = tagLen;
                    if (p >= buf.Length) return null;  // ID3 tag larger than what we read; LAME tag (if any) is beyond our 8K window
                }

                // Scan forward for the first MPEG frame sync (11 set bits): byte[p]==0xFF AND (byte[p+1] & 0xE0) == 0xE0.
                int frame = -1;
                int scanEnd = Math.Min(p + 4096, buf.Length - 4);
                for (int i = p; i < scanEnd; i++)
                {
                    if (buf[i] == 0xFF && (buf[i + 1] & 0xE0) == 0xE0)
                    {
                        // Quick sanity: bitrate index != 1111, sample-rate index != 11.
                        int bri = (buf[i + 2] >> 4) & 0x0F;
                        int sri = (buf[i + 2] >> 2) & 0x03;
                        int layer = (buf[i + 1] >> 1) & 0x03;
                        if (bri == 0x0F || sri == 0x03 || layer == 0) continue;  // junk sync; keep scanning
                        frame = i;
                        break;
                    }
                }
                if (frame < 0) return null;

                // Parse the 4-byte header.
                int versionBits = (buf[frame + 1] >> 3) & 0x03;   // 11=MPEG1, 10=MPEG2, 00=MPEG2.5, 01=reserved
                if (versionBits == 0x01) return null;
                int layerBits = (buf[frame + 1] >> 1) & 0x03;     // 01=Layer III (= MP3)
                if (layerBits != 0x01) return null;
                bool protection = (buf[frame + 1] & 0x01) == 0;   // 0 = CRC follows
                int channelMode = (buf[frame + 3] >> 6) & 0x03;   // 11 = mono

                bool isMpeg1 = versionBits == 0x03;
                bool isMono = channelMode == 0x03;
                int sideInfoLen = isMpeg1 ? (isMono ? 17 : 32) : (isMono ? 9 : 17);

                int xingOffset = frame + 4 + (protection ? 2 : 0) + sideInfoLen;
                if (xingOffset + 8 > buf.Length) return null;

                // Magic must be "Xing" or "Info".
                bool isXing = buf[xingOffset] == 'X' && buf[xingOffset + 1] == 'i' && buf[xingOffset + 2] == 'n' && buf[xingOffset + 3] == 'g';
                bool isInfo = buf[xingOffset] == 'I' && buf[xingOffset + 1] == 'n' && buf[xingOffset + 2] == 'f' && buf[xingOffset + 3] == 'o';
                if (!isXing && !isInfo) return null;

                // Flags (BE uint32) tells us which optional Xing fields follow.
                uint flags = ((uint)buf[xingOffset + 4] << 24) | ((uint)buf[xingOffset + 5] << 16) | ((uint)buf[xingOffset + 6] << 8) | buf[xingOffset + 7];
                int q = xingOffset + 8;
                if ((flags & 0x1) != 0) q += 4;          // frames
                if ((flags & 0x2) != 0) q += 4;          // bytes
                if ((flags & 0x4) != 0) q += 100;        // TOC
                if ((flags & 0x8) != 0) q += 4;          // quality

                // LAME tag follows. Need at least 36 bytes for the full payload.
                if (q + 36 > buf.Length) return null;

                var info = new Mp3LameTagInfo();

                // Encoder ID — 9 ASCII bytes. Often "LAME3.100" / "LAME3.99r" but not always.
                int verLen = 0;
                while (verLen < 9 && buf[q + verLen] >= 0x20 && buf[q + verLen] <= 0x7E) verLen++;
                if (verLen > 0) info.LameVersion = Encoding.ASCII.GetString(buf, q, verLen);

                // Byte 9: info-tag-rev (high 4 bits) + vbr-method (low 4 bits).
                info.InfoTagRevision = (byte)((buf[q + 9] >> 4) & 0x0F);
                info.VbrMethodCode = (byte)(buf[q + 9] & 0x0F);
                info.VbrMethod = ClassifyVbrMethod(info.VbrMethodCode);

                // Byte 10: lowpass filter / 100 Hz.
                info.LowpassHz = buf[q + 10] * 100;

                // Bytes 21-23: encoder delay (12 bits) + encoder padding (12 bits) packed big-endian.
                int delayPad = (buf[q + 21] << 16) | (buf[q + 22] << 8) | buf[q + 23];
                info.EncoderDelay = (delayPad >> 12) & 0xFFF;
                info.EncoderPadding = delayPad & 0xFFF;

                // Bytes 32-33: music CRC (big-endian).
                info.MusicCrc = (buf[q + 32] << 8) | buf[q + 33];

                return info;
            }

            static string? ClassifyVbrMethod(byte code) => code switch
            {
                0 => "Unknown",
                1 => "CBR",
                2 => "ABR",
                3 => "VBR (method 1)",
                4 => "VBR (method 2)",
                5 => "VBR (method 3)",
                6 => "VBR (method 4)",
                8 => "CBR 2-pass",
                9 => "ABR 2-pass",
                _ => null,
            };
        }

        /// <summary>
        /// Normalize a path to a tail identity signal (last up-to-3 non-empty
        /// segments, lowercased). Used inside the cheap fingerprint composite.
        ///   replace '/' with '\', split on '\', take last up-to-3 non-empty parts,
        ///   join with '\', lowercase (invariant). Returns null for root / single-segment paths.
        /// </summary>
        static string? ComputePathTail(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var normalized = path!.Replace('/', '\\');
            if (string.IsNullOrEmpty(normalized)) return null;
            var parts = normalized.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;
            var take = Math.Min(3, parts.Length);
            return string.Join("\\", parts, parts.Length - take, take).ToLowerInvariant();
        }

        /// <summary>Canonical codec string from TagLibSharp MimeType. Falls back to 'other' + codecRaw.</summary>
        static (string Codec, string? CodecRaw) NormalizeCodec(TagLib.File file)
        {
            var raw = file.MimeType ?? "";
            switch (raw.ToLowerInvariant())
            {
                case "audio/mpeg":
                case "audio/mp3":
                case "taglib/mp3":
                    return ("mp3", null);
                case "audio/flac":
                case "audio/x-flac":
                case "taglib/flac":
                    return ("flac", null);
                case "audio/aac":
                case "taglib/aac":
                    return ("aac", null);
                case "audio/mp4":
                case "audio/x-m4a":
                case "taglib/m4a":
                case "taglib/mp4":
                    // TagLib doesn't cheaply distinguish ALAC vs AAC inside MP4.
                    // Inspect Properties.Description (e.g. "MPEG-4 Audio (alac)") when available.
                    var desc4 = file.Properties?.Description ?? "";
                    if (desc4.IndexOf("alac", StringComparison.OrdinalIgnoreCase) >= 0) return ("alac", null);
                    if (desc4.IndexOf("aac", StringComparison.OrdinalIgnoreCase) >= 0) return ("aac", null);
                    return ("m4a", string.IsNullOrEmpty(desc4) ? null : desc4);
                case "audio/opus":
                case "audio/ogg":
                case "taglib/opus":
                case "taglib/ogg":
                    var descOgg = file.Properties?.Description ?? "";
                    if (descOgg.IndexOf("opus", StringComparison.OrdinalIgnoreCase) >= 0) return ("opus", null);
                    if (descOgg.IndexOf("vorbis", StringComparison.OrdinalIgnoreCase) >= 0) return ("vorbis", null);
                    return ("ogg", string.IsNullOrEmpty(descOgg) ? null : descOgg);
                case "audio/wav":
                case "audio/wave":
                case "audio/x-wav":
                case "taglib/wav":
                    return ("wav", null);
                case "audio/x-ms-wma":
                case "audio/wma":
                case "taglib/wma":
                    return ("wma", null);
                case "audio/x-ape":
                case "taglib/ape":
                    return ("ape", null);
                case "audio/x-musepack":
                case "taglib/mpc":
                    return ("mpc", null);
                default:
                    return ("other", string.IsNullOrEmpty(raw) ? null : raw);
            }
        }

        /// <summary>File metadata lifted from TagLib tags for the non-XML scan paths
        /// (--file-list, --analyze-file). Mirrors the iTunes XML metadata MoodsMode uses
        /// so artist/title/album/genre populate when there is no XML source.</summary>
        internal sealed class FileTags
        {
            public string Artist = "";
            public string Title = "";
            public string Album = "";
            public string Genre = "";
            public int DurationMs;
        }

        /// <summary>Best-effort tag extraction. Defaults on failure — identity signals
        /// still flow even if tags are unreadable.</summary>
        static FileTags ExtractFileTags(string filePath)
        {
            var m = new FileTags();
            try
            {
                using var tfile = TagLib.File.Create(filePath);
                // (see ReadGenreForExclusion below for the genre-only variant used by the
                // pre-cache exclusion check on the per-file scan paths)
                var tag = tfile.Tag;
                if (tag != null)
                {
                    m.Artist = tag.FirstPerformer ?? tag.FirstAlbumArtist ?? "";
                    m.Title = tag.Title ?? "";
                    m.Album = tag.Album ?? "";
                    m.Genre = tag.FirstGenre ?? "";
                }
                if (tfile.Properties != null)
                    m.DurationMs = (int)tfile.Properties.Duration.TotalMilliseconds;
            }
            catch { /* best-effort */ }
            return m;
        }

        /// <summary>
        /// Genre only, for the exclusion check on the per-file scan paths (--file-list,
        /// --folder, --analyze-file). Those paths check exclusions BEFORE any cache tier and
        /// so have no parsed tags yet, which is why they passed a hard-coded null genre and
        /// made every `genre` rule silently INERT there — including on the plugin-driven
        /// autoscan path, which is where new files actually arrive. Header-only TagLib read,
        /// and callers gate it on <see cref="ExclusionSet.HasGenreRules"/> so it costs nothing
        /// unless the operator wrote a genre rule. Never throws: an unreadable file yields
        /// null, which cannot match a genre rule — the same outcome as before, but now by
        /// accident of the file rather than by construction.
        /// </summary>
        static string? ReadGenreForExclusion(string path)
        {
            try
            {
                using var tfile = TagLib.File.Create(path);
                var g = tfile.Tag?.FirstGenre;
                return string.IsNullOrWhiteSpace(g) ? null : g;
            }
            catch { return null; }
        }

        /// <summary>Best-effort encoder identification from codec-specific tag accessors.
        /// Tries Xiph ENCODER comment / vendor string (FLAC, Vorbis, Opus, Ogg),
        /// ID3v2 TSSE then TENC frames (MP3 / WAV / AIFF), and Apple ©too atom (MP4 / M4A).
        /// Returns (normalized, raw): normalized is the tag string trimmed; raw is reserved
        /// for future Phase 2 work that parses LAME tags / classifies multi-source strings.
        /// Returns (null, null) when no encoder tag is present.</summary>
        static (string? Encoder, string? EncoderRaw) NormalizeEncoder(TagLib.File tfile)
        {
            try
            {
                // 1. Xiph comment — FLAC, Vorbis, Opus, Ogg.
                var xiph = tfile.GetTag(TagLib.TagTypes.Xiph, false) as TagLib.Ogg.XiphComment;
                if (xiph != null)
                {
                    var enc = xiph.GetField("ENCODER");
                    if (enc != null && enc.Length > 0 && !string.IsNullOrWhiteSpace(enc[0]))
                        return (enc[0].Trim(), null);
                    if (!string.IsNullOrWhiteSpace(xiph.VendorId))
                        return (xiph.VendorId.Trim(), null);
                }

                // 2. ID3v2 — MP3, sometimes WAV/AIFF. Prefer TSSE (settings used for encoding)
                //    over TENC (person/organization). TSSE typically carries "LAME3.100", etc.
                var id3 = tfile.GetTag(TagLib.TagTypes.Id3v2, false) as TagLib.Id3v2.Tag;
                if (id3 != null)
                {
                    var tsse = id3.GetTextAsString("TSSE");
                    if (!string.IsNullOrWhiteSpace(tsse)) return (tsse.Trim(), null);
                    var tenc = id3.GetTextAsString("TENC");
                    if (!string.IsNullOrWhiteSpace(tenc)) return (tenc.Trim(), null);
                }

                // 3. Apple atoms — MP4, M4A, AAC, ALAC. ©too = "tool" used for encoding.
                var apple = tfile.GetTag(TagLib.TagTypes.Apple, false) as TagLib.Mpeg4.AppleTag;
                if (apple != null)
                {
                    var too = apple.GetText(new TagLib.ByteVector(new byte[] { 0xA9, (byte)'t', (byte)'o', (byte)'o' }));
                    if (too != null && too.Length > 0 && !string.IsNullOrWhiteSpace(too[0]))
                        return (too[0].Trim(), null);
                }

                return (null, null);
            }
            catch
            {
                // Tag access can throw on malformed files — fail closed, leave fields null.
                return (null, null);
            }
        }

        /// <summary>Compute fingerprint.v1 composite. Returns null + error string on failure.</summary>
        /// <summary>
        /// Tier-1.5 evidence check: proves an mtime-drifted same-path file is a
        /// tags-only change WITHOUT reading the audio body. True when the stored and
        /// freshly computed fingerprints agree on the 64 KB audio-region head hash
        /// (measured from InvariantStartPosition, so tag writes don't move it) and on
        /// the audio properties a re-encode/trim cannot preserve. FileSize, Bitrate
        /// and Encoder are deliberately NOT compared — all three legitimately drift
        /// with tag size. False on any doubt: the caller falls through to the full
        /// audioStreamSha256 tier, so a false negative only costs speed, never
        /// correctness. The accepted residual risk is an in-place audio edit beyond
        /// the first 64 KB that preserves duration and codec properties — deliberate
        /// tampering, which --verify (full SHA) still catches.
        /// </summary>
        static bool IsTagsOnlyChange(FingerprintV1 fresh, FingerprintV1 stored)
        {
            if (string.IsNullOrEmpty(fresh.AudioHead64kMd5) || string.IsNullOrEmpty(stored.AudioHead64kMd5))
                return false;
            // Trusted sources: "invariant" (tag-resilient region) and "flac-frames"
            // (frame-anchored FLAC region). "whole-file-start" is rejected — a tag
            // write moves those bytes. Sources must MATCH: an old-style FLAC head
            // ("invariant", pre-2026-07 build) measured different bytes than a
            // frame-anchored one, so cross-source equality would be coincidence.
            var freshSrc = fresh.AudioHead64kMd5Source ?? "invariant";
            var storedSrc = stored.AudioHead64kMd5Source ?? "invariant";
            if (freshSrc != storedSrc) return false;
            if (freshSrc != "invariant" && freshSrc != "flac-frames") return false;
            if (!string.Equals(fresh.AudioHead64kMd5, stored.AudioHead64kMd5, StringComparison.OrdinalIgnoreCase))
                return false;
            if (fresh.Codec != stored.Codec) return false;
            if (fresh.SampleRate != stored.SampleRate) return false;
            if (fresh.Channels != stored.Channels) return false;
            if (fresh.BitDepth != stored.BitDepth) return false;
            if (Math.Abs(fresh.DurationMs - stored.DurationMs) > 500) return false;
            return true;
        }

        /// <summary>
        /// Offset of the first FLAC audio frame, or -1 when the file isn't FLAC or
        /// the metadata chain is malformed. Header-only reads (a few KB + seeks).
        ///
        /// Why this exists: TagLibSharp's invariant region for FLAC INCLUDES the
        /// metadata blocks, so a Vorbis-comment write (MBXHub's embedded mood field,
        /// any MusicBee retag, Sony SMFM blocks) drifts the "invariant" hash even
        /// when the audio frames are byte-identical (proven 2026-07-21). Anchoring
        /// the hashed region at the frames makes FLAC identity genuinely
        /// tag-edit-resilient, matching what MP3/WMA already had.
        /// Handles an optional leading ID3v2 tag (seen in the wild on FLAC).
        /// </summary>
        static long GetFlacAudioStart(string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
                return GetFlacAudioStartCore(fs);
            }
            catch { return -1; }
        }

        /// <summary>Core of the FLAC frame-offset walk; stream-based so the
        /// self-test can drive synthetic chains.</summary>
        internal static long GetFlacAudioStartCore(Stream fs)
        {
            var hdr = new byte[10];
            if (fs.Read(hdr, 0, 4) != 4) return -1;
            long pos = 4;
            // Optional ID3v2 prefix: "ID3" + ver(2) + flags(1) + syncsafe-28 size(4)
            if (hdr[0] == (byte)'I' && hdr[1] == (byte)'D' && hdr[2] == (byte)'3')
            {
                if (fs.Read(hdr, 4, 6) != 6) return -1;
                long tagSize = ((long)(hdr[6] & 0x7F) << 21) | ((long)(hdr[7] & 0x7F) << 14)
                             | ((long)(hdr[8] & 0x7F) << 7) | (long)(hdr[9] & 0x7F);
                if ((hdr[5] & 0x10) != 0) tagSize += 10;   // footer-present flag
                pos = 10 + tagSize;
                if (pos + 4 > fs.Length) return -1;
                fs.Seek(pos, SeekOrigin.Begin);
                if (fs.Read(hdr, 0, 4) != 4) return -1;
                pos += 4;
            }
            if (hdr[0] != (byte)'f' || hdr[1] != (byte)'L' || hdr[2] != (byte)'a' || hdr[3] != (byte)'C') return -1;
            // Metadata block headers: 1 byte (last-flag<<7 | type) + 3-byte BE length
            var bh = new byte[4];
            while (true)
            {
                if (fs.Read(bh, 0, 4) != 4) return -1;
                pos += 4;
                bool last = (bh[0] & 0x80) != 0;
                long len = ((long)bh[1] << 16) | ((long)bh[2] << 8) | bh[3];
                pos += len;
                if (pos > fs.Length) return -1;
                if (last) return pos;
                fs.Seek(pos, SeekOrigin.Begin);
            }
        }

        static FingerprintV1? ComputeFingerprintV1(string filePath, long fileSize, out string? error)
        {
            error = null;
            try
            {
                using var tfile = TagLib.File.Create(filePath);
                var props = tfile.Properties;
                if (props == null)
                {
                    error = "no audio properties";
                    return null;
                }

                long invStart = tfile.InvariantStartPosition;
                long invEnd = tfile.InvariantEndPosition;
                // Some formats return 0/0 or negative — treat as "no invariant region known".
                string headSource = "invariant";
                if (invEnd <= invStart || invStart < 0 || invEnd > fileSize)
                {
                    invStart = 0;
                    invEnd = fileSize;
                    headSource = "whole-file-start";
                }

                // FLAC: anchor at the first audio frame — TagLib's region includes the
                // metadata blocks, so a tag write would drift the head hash (see
                // GetFlacAudioStart). Source "flac-frames" keeps old/new heads from
                // being compared across algorithms in IsTagsOnlyChange.
                long flacStart = GetFlacAudioStart(filePath);
                if (flacStart >= 0 && flacStart < fileSize)
                {
                    invStart = flacStart;
                    invEnd = fileSize;
                    headSource = "flac-frames";
                }

                var tail = ComputePathTail(filePath);
                if (tail == null)
                {
                    error = "path has too few segments for pathTail";
                    return null;
                }

                var (codec, codecRaw) = NormalizeCodec(tfile);
                var (encoder, encoderRaw) = NormalizeEncoder(tfile);

                // 64KB head MD5 from invariant start.
                string headMd5;
                int headLen = (int)Math.Min(65536L, invEnd - invStart);
                using (var md5 = MD5.Create())
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
                {
                    fs.Seek(invStart, SeekOrigin.Begin);
                    var buf = new byte[headLen];
                    int read = 0;
                    while (read < headLen)
                    {
                        int r = fs.Read(buf, read, headLen - read);
                        if (r <= 0) break;
                        read += r;
                    }
                    // If the file was shorter than expected, hash only the bytes we got.
                    var hash = md5.ComputeHash(buf, 0, read);
                    headMd5 = HexLower(hash);
                }

                var fp = new FingerprintV1
                {
                    FileSize = fileSize,
                    PathTail = tail,
                    DurationMs = (int)props.Duration.TotalMilliseconds,
                    SampleRate = props.AudioSampleRate,
                    Channels = props.AudioChannels,
                    BitDepth = props.BitsPerSample,
                    Codec = codec,
                    CodecRaw = codecRaw,
                    Bitrate = props.AudioBitrate,
                    Encoder = encoder,
                    EncoderRaw = encoderRaw,
                    AudioHead64kMd5 = headMd5,
                    AudioHead64kMd5Source = headSource,
                    InvariantStart = invStart,
                    InvariantEnd = invEnd,
                };

                // Phase 2 — MP3 LAME tag extraction. Only attempted for codec=mp3; failure is silent.
                if (codec == "mp3")
                    ApplyMp3LameTag(filePath, fp);

                return fp;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>Phase 2 — populate MP3 LAME-tag fields on an existing FingerprintV1.
        /// Called from ComputeFingerprintV1 (fresh extract) and from --verify --backfill
        /// (when the entry's codec=mp3 and LAME fields are missing). Safe to call on
        /// non-mp3 files (returns silently) and on mp3 files without a LAME tag (no-op).</summary>
        static void ApplyMp3LameTag(string filePath, FingerprintV1 fp)
        {
            var info = Mp3LameTagParser.TryParse(filePath);
            if (info == null) return;
            fp.Mp3LameVersion = info.LameVersion;
            fp.Mp3InfoTagRevision = info.InfoTagRevision;
            fp.Mp3VbrMethodCode = info.VbrMethodCode;
            fp.Mp3VbrMethod = info.VbrMethod;
            fp.Mp3LowpassHz = info.LowpassHz;
            fp.Mp3EncoderDelay = info.EncoderDelay;
            fp.Mp3EncoderPadding = info.EncoderPadding;
            fp.Mp3MusicCrc = info.MusicCrc;
        }

        /// <summary>
        /// Ride-along entry point: parse TagLib bounds, then SHA-256 the audio region.
        /// Kept separate from ComputeFingerprintV1 so the two run concurrently without
        /// one blocking the other on a shared result. Second TagLib open costs ~5ms.
        ///
        /// Returns the hash plus a "source" tag indicating coverage:
        ///   "invariant"  — hash covers [InvariantStartPosition, InvariantEndPosition)
        ///                  (audio region only, container metadata excluded). The common
        ///                  case; cross-machine stable across tag edits.
        ///   "whole-file" — invariant bounds were unavailable, hash covers the entire
        ///                  file including container metadata. Cross-machine stable
        ///                  only for byte-identical files; tag edits diverge the hash.
        /// MBXHub uses this signal to decide when an audioStreamSha256 is safe to
        /// promote to primary identity (Layer 4 contract — see spec §3.2).
        /// Mirrors the existing fingerprint.v1.audioHead64kMd5Source pattern.
        /// </summary>
        static (string? hash, string source) ComputeAudioStreamSha256FromFile(string filePath, long fileSize, out string? error)
            => ComputeAudioStreamSha256FromFile(filePath, fileSize, out error, out _);

        /// <summary>Overload that also returns the LEGACY-region sha for FLAC files
        /// (TagLib invariant region, which includes the metadata blocks — the value
        /// pre-2026-07 builds stored). Callers use it to recognize stored old-style
        /// hashes without re-analysis during the flac-frames transition; null for
        /// non-FLAC or when the legacy region is unavailable.</summary>
        static (string? hash, string source) ComputeAudioStreamSha256FromFile(string filePath, long fileSize, out string? error, out string? legacySha)
        {
            error = null;
            legacySha = null;
            try
            {
                // FLAC: frame-anchored region (see GetFlacAudioStart). The legacy
                // TagLib region is hashed in the same read for transition compares.
                long flacStart = GetFlacAudioStart(filePath);
                if (flacStart >= 0 && flacStart < fileSize)
                {
                    long legStart = -1, legEnd = -1;
                    try
                    {
                        using var tfile = TagLib.File.Create(filePath);
                        legStart = tfile.InvariantStartPosition;
                        legEnd = tfile.InvariantEndPosition;
                    }
                    catch { }
                    var (_, shaA, shaB) = ComputeMd5AndTwoShas(filePath, fileSize, false,
                        flacStart, fileSize, legStart, legEnd, out error);
                    legacySha = shaB;
                    return (shaA, shaA != null ? "flac-frames" : "");
                }

                long invStart, invEnd;
                using (var tfile2 = TagLib.File.Create(filePath))
                {
                    invStart = tfile2.InvariantStartPosition;
                    invEnd = tfile2.InvariantEndPosition;
                }
                string source = "invariant";
                if (invEnd <= invStart || invStart < 0 || invEnd > fileSize)
                {
                    invStart = 0;
                    invEnd = fileSize;
                    source = "whole-file";
                }
                var hash = ComputeAudioStreamSha256(filePath, invStart, invEnd, out error);
                return (hash, source);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return (null, "");
            }
        }

        /// <summary>
        /// One read pass over the whole file feeding two digests at once: whole-file
        /// MD5 and SHA-256 of the audio region [InvariantStartPosition,
        /// InvariantEndPosition). Replaces the back-to-back full reads
        /// (ComputeAudioStreamSha256FromFile + ComputeFileMd5) in the cache tier
        /// walks — same results, one read instead of two. Same fallback semantics as
        /// ComputeAudioStreamSha256FromFile: invalid invariant bounds → sha covers
        /// the whole file, source "whole-file".
        /// TagLib parse failure degrades to md5-only (sha null) — never loses the MD5.
        /// </summary>
        static (string? fileMd5, string? audioSha, string shaSource) ComputeFileMd5AndAudioSha(string filePath, long fileSize, out string? error)
            => ComputeFileMd5AndAudioSha(filePath, fileSize, out error, out _);

        /// <summary>Overload that also returns the LEGACY-region sha for FLAC (see the
        /// ComputeAudioStreamSha256FromFile overload) — computed in the SAME read pass.</summary>
        static (string? fileMd5, string? audioSha, string shaSource) ComputeFileMd5AndAudioSha(string filePath, long fileSize, out string? error, out string? legacySha)
        {
            error = null;
            legacySha = null;
            long invStart = 0, invEnd = 0;   // stays invalid on TagLib failure → md5-only
            string source = "invariant";
            long flacStart = GetFlacAudioStart(filePath);
            try
            {
                using (var tfile = TagLib.File.Create(filePath))
                {
                    invStart = tfile.InvariantStartPosition;
                    invEnd = tfile.InvariantEndPosition;
                }
                if (invEnd <= invStart || invStart < 0 || invEnd > fileSize)
                {
                    invStart = 0;
                    invEnd = fileSize;
                    source = "whole-file";
                }
            }
            catch (Exception ex)
            {
                // TagLib parse failure. Parity with the split helpers this replaced:
                // ComputeAudioStreamSha256FromFile returned a null sha here, while
                // ComputeFileMd5 (pure FileStream, no TagLib) still produced the MD5.
                // Leave the bounds invalid so Core hashes md5-only (non-FLAC).
                error = ex.Message;
                invStart = 0;
                invEnd = 0;
            }
            if (flacStart >= 0 && flacStart < fileSize)
            {
                // FLAC: primary sha = frame-anchored; legacy TagLib-region sha rides
                // the same pass so cache tiers can match stored old-style values.
                var (md5F, shaF, shaLeg) = ComputeMd5AndTwoShas(filePath, fileSize, true,
                    flacStart, fileSize, invStart, invEnd, out var flacErr);
                if (flacErr != null) error = flacErr;
                legacySha = shaLeg;
                return (md5F, shaF, shaF != null ? "flac-frames" : "");
            }
            var (md5Hex, shaHex) = ComputeFileMd5AndAudioShaCore(filePath, fileSize, invStart, invEnd, out var coreErr);
            if (coreErr != null) error = coreErr;
            return (md5Hex, shaHex, shaHex != null ? source : "");
        }

        /// <summary>Core of the single-pass dual hash; separated from the TagLib
        /// bounds lookup so the self-test can drive arbitrary regions.</summary>
        static (string? fileMd5, string? audioSha) ComputeFileMd5AndAudioShaCore(string filePath, long fileSize, long invariantStart, long invariantEnd, out string? error)
        {
            error = null;
            try
            {
                bool shaValid = invariantEnd > invariantStart && invariantStart >= 0 && invariantEnd <= fileSize;
                using var md5 = MD5.Create();
                // SHA256Cng: SHA-NI hardware accel; SHA256.Create() is managed-only on net48.
                using var sha = new SHA256Cng();
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
                var buf = new byte[81920];
                long pos = 0;
                int r;
                while ((r = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    md5.TransformBlock(buf, 0, r, null, 0);
                    if (shaValid)
                    {
                        // Overlap of [pos, pos+r) with [invariantStart, invariantEnd).
                        long s = Math.Max(pos, invariantStart);
                        long e = Math.Min(pos + r, invariantEnd);
                        if (e > s)
                            sha.TransformBlock(buf, (int)(s - pos), (int)(e - s), null, 0);
                    }
                    pos += r;
                }
                md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                string md5Hex = HexLower(md5.Hash!);
                string? shaHex = null;
                if (shaValid)
                {
                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    shaHex = HexLower(sha.Hash!);
                }
                return (md5Hex, shaHex);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return (null, null);
            }
        }

        /// <summary>
        /// One read pass feeding up to three digests: optional whole-file MD5,
        /// SHA-256 over region A (primary), SHA-256 over region B (legacy compare).
        /// Used for FLAC during the flac-frames transition — region A is the
        /// frame-anchored hash, region B the old TagLib-invariant hash, so a stored
        /// old-style value is recognizable without a second full read. An invalid
        /// region (end <= start etc.) yields a null sha for that slot.
        /// </summary>
        static (string? md5, string? shaA, string? shaB) ComputeMd5AndTwoShas(
            string filePath, long fileSize, bool wantMd5,
            long aStart, long aEnd, long bStart, long bEnd, out string? error)
        {
            error = null;
            try
            {
                bool aValid = aEnd > aStart && aStart >= 0 && aEnd <= fileSize;
                bool bValid = bEnd > bStart && bStart >= 0 && bEnd <= fileSize;
                using var md5 = wantMd5 ? MD5.Create() : null;
                using var shaA = aValid ? new SHA256Cng() : null;
                using var shaB = bValid ? new SHA256Cng() : null;
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
                var buf = new byte[81920];
                long pos = 0;
                int r;
                while ((r = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    md5?.TransformBlock(buf, 0, r, null, 0);
                    if (shaA != null)
                    {
                        long s = Math.Max(pos, aStart), e = Math.Min(pos + r, aEnd);
                        if (e > s) shaA.TransformBlock(buf, (int)(s - pos), (int)(e - s), null, 0);
                    }
                    if (shaB != null)
                    {
                        long s = Math.Max(pos, bStart), e = Math.Min(pos + r, bEnd);
                        if (e > s) shaB.TransformBlock(buf, (int)(s - pos), (int)(e - s), null, 0);
                    }
                    pos += r;
                }
                string? md5Hex = null;
                if (md5 != null)
                {
                    md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    md5Hex = HexLower(md5.Hash!);
                }
                string? aHex = null;
                if (shaA != null)
                {
                    shaA.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    aHex = HexLower(shaA.Hash!);
                }
                string? bHex = null;
                if (shaB != null)
                {
                    shaB.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    bHex = HexLower(shaB.Hash!);
                }
                return (md5Hex, aHex, bHex);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return (null, null, null);
            }
        }

        /// <summary>
        /// Streaming SHA-256 of the audio region [invariantStart, invariantEnd).
        /// Disk-bound; SHA-NI makes CPU cost negligible on modern hardware (SHA256Cng — the default SHA256.Create() on net48 is managed-only).
        /// </summary>
        static string? ComputeAudioStreamSha256(string filePath, long invariantStart, long invariantEnd, out string? error)
        {
            error = null;
            try
            {
                long length = invariantEnd - invariantStart;
                if (length <= 0)
                {
                    error = "empty audio region";
                    return null;
                }
                // SHA256Cng (CNG / bcrypt) uses SHA-NI on modern CPUs. SHA256.Create() on
                // .NET Framework resolves to SHA256Managed — no hardware accel, ~5-10x
                // slower, which made mass tag-edit rescans CPU-bound (observed 2026-07-04:
                // drive at 10%, CPU pegged). Same algorithm, same output.
                using var sha = new SHA256Cng();
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
                fs.Seek(invariantStart, SeekOrigin.Begin);
                var buf = new byte[81920];
                long remaining = length;
                while (remaining > 0)
                {
                    int want = (int)Math.Min(buf.Length, remaining);
                    int r = fs.Read(buf, 0, want);
                    if (r <= 0) break;
                    sha.TransformBlock(buf, 0, r, null, 0);
                    remaining -= r;
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return HexLower(sha.Hash!);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        static string HexLower(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }

        /// <summary>
        /// Build an identity envelope JSON object as the body of one NDJSON line
        /// in the --hash-only --output manifest (offline determinism rig path).
        /// </summary>
        static byte[] BuildIdentityOnlyEnvelope(string filePath, long fileSize,
            FingerprintV1 fp, string? audioStreamSha256, string level, string? audioStreamSha256Source)
        {
            var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
            using var ms = new MemoryStream();
            using (var jw = new Utf8JsonWriter(ms))
            {
                jw.WriteStartObject();
                jw.WriteString("path", filePath);

                // metadata (minimal — just what the cheap pass produced)
                jw.WritePropertyName("metadata");
                jw.WriteStartObject();
                jw.WriteNumber("duration", fp.DurationMs / 1000.0);
                jw.WriteNumber("fileSize", fileSize);
                jw.WriteEndObject();

                // identity
                jw.WritePropertyName("identity");
                jw.WriteStartObject();
                WriteFingerprintV1(jw, fp);
                if (!string.IsNullOrEmpty(audioStreamSha256))
                {
                    jw.WriteString("audioStreamSha256", audioStreamSha256);
                    if (audioStreamSha256Source == "whole-file")
                        jw.WriteString("audioStreamSha256Source", "whole-file");
                }
                jw.WriteEndObject();

                // provenance
                jw.WritePropertyName("provenance");
                jw.WriteStartObject();
                jw.WriteString("scannedBy", Environment.MachineName);
                jw.WriteString("tool", "truedat-hash");
                jw.WriteString("toolVersion", version);
                jw.WriteString("scannedAt", DateTime.UtcNow.ToString("o"));
                jw.WriteString("level", level);
                jw.WriteEndObject();

                jw.WriteEndObject();
            }
            return ms.ToArray();
        }

        /// <summary>Parse a FingerprintV1 back from mbxmoods.json round-trip. Returns null
        /// when the "fingerprint.v1" property is missing or malformed; LoadExistingMoods
        /// uses this so re-runs preserve identity fields without recomputing.</summary>
        static FingerprintV1? ParseFingerprintV1FromJson(JsonElement track)
        {
            if (!track.TryGetProperty("fingerprint.v1", out var fp) || fp.ValueKind != JsonValueKind.Object)
                return null;
            try
            {
                var head = GetStr(fp, "audioHead64kMd5");
                if (string.IsNullOrEmpty(head)) return null;  // primary key missing -> not a valid fingerprint
                var src  = GetStr(fp, "audioHead64kMd5Source");
                var codecRaw = GetStr(fp, "codecRaw");
                var encoder = GetStr(fp, "encoder");
                var encoderRaw = GetStr(fp, "encoderRaw");
                // MP3 LAME tag (Phase 2) — nested object; tolerant of absence.
                string? lameVersion = null, lameVbrMethod = null;
                int lameLowpass = 0, lameDelay = 0, lamePadding = 0, lameMusicCrc = 0;
                byte lameRev = 0, lameVbrCode = 0;
                if (fp.TryGetProperty("mp3LameTag", out var lameNode) && lameNode.ValueKind == JsonValueKind.Object)
                {
                    lameVersion   = GetStr(lameNode, "version");
                    lameVbrMethod = GetStr(lameNode, "vbrMethod");
                    lameLowpass   = GetInt(lameNode, "lowpassHz");
                    lameDelay     = GetInt(lameNode, "encoderDelay");
                    lamePadding   = GetInt(lameNode, "encoderPadding");
                    lameMusicCrc  = GetInt(lameNode, "musicCrc");
                    lameRev       = (byte)Math.Min(255, GetInt(lameNode, "infoTagRevision"));
                    lameVbrCode   = (byte)Math.Min(255, GetInt(lameNode, "vbrMethodCode"));
                }
                long fileSize = fp.TryGetProperty("fileSize", out var fs) && fs.ValueKind == JsonValueKind.Number ? fs.GetInt64() : 0L;
                long invStart = fp.TryGetProperty("invariantStart", out var iS) && iS.ValueKind == JsonValueKind.Number ? iS.GetInt64() : 0L;
                long invEnd   = fp.TryGetProperty("invariantEnd",   out var iE) && iE.ValueKind == JsonValueKind.Number ? iE.GetInt64() : 0L;
                return new FingerprintV1
                {
                    FileSize = fileSize,
                    PathTail = GetStr(fp, "pathTail"),
                    DurationMs = GetInt(fp, "durationMs"),
                    SampleRate = GetInt(fp, "sampleRate"),
                    Channels = GetInt(fp, "channels"),
                    BitDepth = GetInt(fp, "bitDepth"),                       // tolerant of older entries (returns 0)
                    Codec = string.IsNullOrEmpty(GetStr(fp, "codec")) ? "other" : GetStr(fp, "codec"),
                    CodecRaw = string.IsNullOrEmpty(codecRaw) ? null : codecRaw,
                    Bitrate = GetInt(fp, "bitrate"),
                    Encoder = string.IsNullOrEmpty(encoder) ? null : encoder,
                    EncoderRaw = string.IsNullOrEmpty(encoderRaw) ? null : encoderRaw,
                    Mp3LameVersion = string.IsNullOrEmpty(lameVersion) ? null : lameVersion,
                    Mp3InfoTagRevision = lameRev,
                    Mp3VbrMethodCode = lameVbrCode,
                    Mp3VbrMethod = string.IsNullOrEmpty(lameVbrMethod) ? null : lameVbrMethod,
                    Mp3LowpassHz = lameLowpass,
                    Mp3EncoderDelay = lameDelay,
                    Mp3EncoderPadding = lamePadding,
                    Mp3MusicCrc = lameMusicCrc,
                    AudioHead64kMd5 = head,
                    AudioHead64kMd5Source = string.IsNullOrEmpty(src) ? "invariant" : src,
                    InvariantStart = invStart,
                    InvariantEnd = invEnd
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Serialize a FingerprintV1 into an open identity object as identity["fingerprint.v1"].
        /// New optional fields (bitDepth, encoder, encoderRaw) are omitted when empty / zero,
        /// keeping older-schema files byte-identical when nothing is known.</summary>
        static void WriteFingerprintV1(Utf8JsonWriter jw, FingerprintV1 fp)
        {
            jw.WritePropertyName("fingerprint.v1");
            jw.WriteStartObject();
            jw.WriteNumber("fileSize", fp.FileSize);
            jw.WriteString("pathTail", fp.PathTail);
            jw.WriteNumber("durationMs", fp.DurationMs);
            jw.WriteNumber("sampleRate", fp.SampleRate);
            jw.WriteNumber("channels", fp.Channels);
            if (fp.BitDepth > 0)
                jw.WriteNumber("bitDepth", fp.BitDepth);
            jw.WriteString("codec", fp.Codec);
            if (!string.IsNullOrEmpty(fp.CodecRaw))
                jw.WriteString("codecRaw", fp.CodecRaw);
            jw.WriteNumber("bitrate", fp.Bitrate);
            if (!string.IsNullOrEmpty(fp.Encoder))
                jw.WriteString("encoder", fp.Encoder);
            if (!string.IsNullOrEmpty(fp.EncoderRaw))
                jw.WriteString("encoderRaw", fp.EncoderRaw);
            // Phase 2 — MP3 LAME tag block, only emitted when at least one field was extracted.
            if (!string.IsNullOrEmpty(fp.Mp3LameVersion) || fp.Mp3LowpassHz > 0 || fp.Mp3VbrMethodCode > 0)
            {
                jw.WritePropertyName("mp3LameTag");
                jw.WriteStartObject();
                if (!string.IsNullOrEmpty(fp.Mp3LameVersion))
                    jw.WriteString("version", fp.Mp3LameVersion);
                if (fp.Mp3InfoTagRevision > 0)
                    jw.WriteNumber("infoTagRevision", fp.Mp3InfoTagRevision);
                jw.WriteNumber("vbrMethodCode", fp.Mp3VbrMethodCode);
                if (!string.IsNullOrEmpty(fp.Mp3VbrMethod))
                    jw.WriteString("vbrMethod", fp.Mp3VbrMethod);
                if (fp.Mp3LowpassHz > 0)
                    jw.WriteNumber("lowpassHz", fp.Mp3LowpassHz);
                if (fp.Mp3EncoderDelay > 0)
                    jw.WriteNumber("encoderDelay", fp.Mp3EncoderDelay);
                if (fp.Mp3EncoderPadding > 0)
                    jw.WriteNumber("encoderPadding", fp.Mp3EncoderPadding);
                if (fp.Mp3MusicCrc > 0)
                    jw.WriteNumber("musicCrc", fp.Mp3MusicCrc);
                jw.WriteEndObject();
            }
            jw.WriteString("audioHead64kMd5", fp.AudioHead64kMd5);
            if (fp.AudioHead64kMd5Source != "invariant")
                jw.WriteString("audioHead64kMd5Source", fp.AudioHead64kMd5Source);
            jw.WriteEndObject();
        }

        static Dictionary<string, (T Entry, string OldKey)>? BuildHashIndex<T>(
            ConcurrentDictionary<string, T> cache,
            Func<T, string?> getHash) where T : class
        {
            Dictionary<string, (T, string)>? index = null;
            foreach (var kvp in cache)
            {
                var hash = getHash(kvp.Value);
                if (string.IsNullOrEmpty(hash)) continue;
                index ??= new Dictionary<string, (T, string)>(StringComparer.OrdinalIgnoreCase);
                if (!index.ContainsKey(hash!))
                    index[hash!] = (kvp.Value, kvp.Key);
            }
            return index;
        }

        /// <summary>
        /// Build a fresh TrackEntry that reuses cached Essentia features but takes
        /// fresh metadata from the iTunes XML track and (optionally) refreshes
        /// identity fields the caller has just recomputed off disk. Used by every
        /// cache-reuse path (path-mtime, sha-path, sha-cross). Centralized
        /// so adding a new TrackFeatures field doesn't have to be done four places.
        ///
        /// `refreshedFileMd5` / `refreshedFp`: pass non-null when the caller knows
        /// the cached values are stale (e.g. tag edits diverge fileMd5). Otherwise
        /// pass null and the source's existing values are kept.
        /// </summary>
        static TrackEntry RebuildCacheEntry(
            TrackEntry source,
            ITunesTrack t,
            DateTime newLastMod,
            string? refreshedFileMd5,
            FingerprintV1? refreshedFp,
            string? refreshedSha = null,
            string? refreshedShaSource = null)
        {
            return RebuildCacheEntryCore(source, t.TrackId, t.Artist, t.Name, t.Album, t.Genre, t.Location, newLastMod, refreshedFileMd5, refreshedFp, refreshedSha, refreshedShaSource);
        }

        /// <summary>
        /// Same shape as RebuildCacheEntry but takes metadata fields explicitly.
        /// Used by --file-list / --analyze-file where the metadata source is TagLib
        /// tags rather than an iTunes XML track. trackId carries through from the
        /// cached entry (zero if it was never an iTunes-XML-derived entry).
        /// </summary>
        static TrackEntry RebuildCacheEntryFromTags(
            TrackEntry source,
            string artist, string title, string album, string genre, string filePath,
            DateTime newLastMod,
            string? refreshedFileMd5,
            FingerprintV1? refreshedFp,
            string? refreshedSha = null,
            string? refreshedShaSource = null)
        {
            return RebuildCacheEntryCore(source, source.Features.TrackId, artist, title, album, genre, filePath, newLastMod, refreshedFileMd5, refreshedFp, refreshedSha, refreshedShaSource);
        }

        static TrackEntry RebuildCacheEntryCore(
            TrackEntry source,
            int trackId,
            string artist, string title, string album, string genre, string filePath,
            DateTime newLastMod,
            string? refreshedFileMd5,
            FingerprintV1? refreshedFp,
            string? refreshedSha = null,
            string? refreshedShaSource = null)
        {
            var sf = source.Features;
            return new TrackEntry
            {
                Features = new TrackFeatures
                {
                    TrackId = trackId, Artist = artist, Title = title,
                    Album = album, Genre = genre, FilePath = filePath,
                    Bpm = sf.Bpm, Key = sf.Key, Mode = sf.Mode,
                    SpectralCentroid = sf.SpectralCentroid, SpectralFlux = sf.SpectralFlux,
                    Loudness = sf.Loudness, Danceability = sf.Danceability,
                    OnsetRate = sf.OnsetRate, ZeroCrossingRate = sf.ZeroCrossingRate,
                    SpectralRms = sf.SpectralRms, SpectralFlatness = sf.SpectralFlatness,
                    Dissonance = sf.Dissonance, PitchSalience = sf.PitchSalience,
                    ChordsChangesRate = sf.ChordsChangesRate, Mfcc = sf.Mfcc,
                    DynamicRange = sf.DynamicRange,
                    DynamicRangeSource = sf.DynamicRangeSource,
                    LoudnessMomentary = sf.LoudnessMomentary,
                    LoudnessShortTerm = sf.LoudnessShortTerm,
                    ReplayGain = sf.ReplayGain,
                    SilenceRate20dB = sf.SilenceRate20dB,
                    SilenceRate30dB = sf.SilenceRate30dB,
                    SilenceRate60dB = sf.SilenceRate60dB,
                    SpectralRolloff = sf.SpectralRolloff,
                    SpectralComplexity = sf.SpectralComplexity,
                    SpectralEntropy = sf.SpectralEntropy,
                    SpectralKurtosis = sf.SpectralKurtosis,
                    SpectralSkewness = sf.SpectralSkewness,
                    SpectralSpread = sf.SpectralSpread,
                    SpectralStrongPeak = sf.SpectralStrongPeak,
                    SpectralDecrease = sf.SpectralDecrease,
                    SpectralEnergy = sf.SpectralEnergy,
                    SpectralEnergyLow = sf.SpectralEnergyLow,
                    SpectralEnergyMidLow = sf.SpectralEnergyMidLow,
                    SpectralEnergyMidHigh = sf.SpectralEnergyMidHigh,
                    SpectralEnergyHigh = sf.SpectralEnergyHigh,
                    Hfc = sf.Hfc,
                    BarkCrest = sf.BarkCrest,
                    BarkFlatness = sf.BarkFlatness,
                    BarkKurtosis = sf.BarkKurtosis,
                    BarkSkewness = sf.BarkSkewness,
                    BarkSpread = sf.BarkSpread,
                    ErbCrest = sf.ErbCrest,
                    ErbFlatness = sf.ErbFlatness,
                    ErbKurtosis = sf.ErbKurtosis,
                    ErbSkewness = sf.ErbSkewness,
                    ErbSpread = sf.ErbSpread,
                    MelCrest = sf.MelCrest,
                    MelFlatness = sf.MelFlatness,
                    MelKurtosis = sf.MelKurtosis,
                    MelSkewness = sf.MelSkewness,
                    MelSpread = sf.MelSpread,
                    BeatsLoudness = sf.BeatsLoudness,
                    ChordsStrength = sf.ChordsStrength,
                    HpcpCrest = sf.HpcpCrest,
                    HpcpEntropy = sf.HpcpEntropy,
                    // 2026-07-22 tonal/rhythm wave — preserve across cache hits
                    KeyVoteKrumhansl = sf.KeyVoteKrumhansl,
                    KeyVoteTemperley = sf.KeyVoteTemperley,
                    KeyVoteEdma = sf.KeyVoteEdma,
                    BpmFirstPeak = sf.BpmFirstPeak,
                    BpmFirstPeakWeight = sf.BpmFirstPeakWeight,
                    BpmSecondPeak = sf.BpmSecondPeak,
                    BpmSecondPeakWeight = sf.BpmSecondPeakWeight,
                    BpmSecondPeakSpread = sf.BpmSecondPeakSpread,
                    ChordsKey = sf.ChordsKey,
                    ChordsScale = sf.ChordsScale,
                    ChordsHistogram = sf.ChordsHistogram,
                    ChordsNumberRate = sf.ChordsNumberRate,
                    TuningFrequency = sf.TuningFrequency,
                    TuningEqualTemperedDeviation = sf.TuningEqualTemperedDeviation,
                    TuningDiatonicStrength = sf.TuningDiatonicStrength,
                    TuningNontemperedEnergyRatio = sf.TuningNontemperedEnergyRatio,
                    AverageLoudness = sf.AverageLoudness,
                    BitUsage = sf.BitUsage,    // Phase 2.5 — preserve across cache hits
                    HfEnergyRatio = sf.HfEnergyRatio,    // Phase 3 — preserve across cache hits
                    HfEnergyMethod = sf.HfEnergyMethod,
                    HfSpectralStructure = sf.HfSpectralStructure,    // Phase 5 — preserve across cache hits
                },
                LastModified = newLastMod,
                AnalysisDurationSecs = source.AnalysisDurationSecs,
                FileMd5 = refreshedFileMd5 ?? source.FileMd5,
                AudioStreamSha256 = refreshedSha ?? source.AudioStreamSha256,
                AudioStreamSha256Source = refreshedShaSource ?? source.AudioStreamSha256Source,
                FingerprintV1 = refreshedFp ?? source.FingerprintV1,
            };
        }

        static TimeSpan StopwatchTicksToTimeSpan(long stopwatchTicks)
        {
            return TimeSpan.FromSeconds((double)stopwatchTicks / Stopwatch.Frequency);
        }

        static DateTime TruncateToSeconds(DateTime dt)
        {
            return new DateTime(dt.Ticks - dt.Ticks % TimeSpan.TicksPerSecond, dt.Kind);
        }

        /// <summary>Wall-clock ETA — naturally accounts for parallelism, cache hits, failures, and varying track sizes.</summary>
        static string FormatEta(TimeSpan elapsed, int done, int total)
        {
            if (done < 10 || total <= done) return "";
            var etaSecs = ComputeEtaSecs(elapsed, done, total);
            // Per-track avg. Once anything has analyzed, show the ANALYZED-class
            // average (Essentia thread-time — the cost that actually matters); the
            // blended wall average is dominated by ~0ms cache hits and reads as
            // nonsense ("163ms/trk" while podcasts take minutes). The blend only
            // remains for pure-cache runs, where it genuinely is the per-track cost.
            string avgTag;
            int an = Volatile.Read(ref _analyzeCount);
            if (an > 0)
            {
                var aAvg = (double)Interlocked.Read(ref _analyzeTicksTotal) / Stopwatch.Frequency / an;
                avgTag = aAvg >= 60 ? $"{FormatTimeSpan(TimeSpan.FromSeconds(aAvg))}/analyzed" : $"{aAvg:F1}s/analyzed";
            }
            else
            {
                var avg = elapsed.TotalSeconds / done;
                avgTag = avg >= 1 ? $"{avg:F1}s/trk" : $"{avg * 1000:F0}ms/trk";
            }
            // Live analyzed throughput (trailing window) — size-normalized, so it's
            // comparable across libraries with different track lengths.
            var rate = CurrentRateMBps(elapsed.TotalSeconds);
            var rateTag = rate > 0 ? $" · {rate:F1} MB/s" : "";
            // Negative = no honest estimate yet; show the measured bits without an ETA.
            var etaTag = etaSecs < 0 ? "" : $"ETA {FormatTimeSpan(TimeSpan.FromSeconds(etaSecs))} · ";
            return $" {etaTag}{avgTag}{rateTag}";
        }

        /// <summary>Parse "M/N" or "MofN" chunk spec. Returns false on any malformed input.</summary>
        static bool TryParseChunk(string spec, out int index, out int total)
        {
            index = 0; total = 0;
            if (string.IsNullOrWhiteSpace(spec)) return false;
            var parts = spec.Split(new[] { '/', ':' }, 2);
            if (parts.Length != 2)
            {
                var of = spec.IndexOf("of", StringComparison.OrdinalIgnoreCase);
                if (of <= 0) return false;
                parts = new[] { spec.Substring(0, of), spec.Substring(of + 2) };
            }
            return int.TryParse(parts[0].Trim(), out index)
                && int.TryParse(parts[1].Trim(), out total)
                && total >= 1;
        }

        /// <summary>
        /// Insert a suffix before the file extension. Used when --chunk is set
        /// so two machines writing to the same library directory don't collide.
        ///   InsertSuffix("C:\foo\mbxmoods.json", "machineA")
        ///     -> "C:\foo\mbxmoods.machineA.json"
        /// </summary>
        static string InsertFilenameSuffix(string path, string suffix)
        {
            var dir = Path.GetDirectoryName(path) ?? "";
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            return Path.Combine(dir, $"{name}.{suffix}{ext}");
        }

        /// <summary>Strip filename-hostile characters from a hostname for use in a path.</summary>
        static string SanitizeForFilename(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "host";
            var bad = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(raw.Length);
            foreach (var c in raw)
                sb.Append(Array.IndexOf(bad, c) >= 0 || c == '.' ? '_' : c);
            var s = sb.ToString().Trim('_');
            return s.Length == 0 ? "host" : s;
        }

        static string FormatTimeSpan(TimeSpan ts)
        {
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h{ts.Minutes:D2}m";
            if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m{ts.Seconds:D2}s";
            return $"{ts.TotalSeconds:F1}s";
        }

        static void RetryDelete(string path)
        {
            for (int i = 0; i < 4; i++)
            {
                try { File.Delete(path); return; }
                catch (IOException) when (i < 3) { Thread.Sleep(50 * (i + 1)); }
                catch (UnauthorizedAccessException) when (i < 3) { Thread.Sleep(50 * (i + 1)); }
            }
        }

        static string CsvEscape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            value = value.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ");
            if (value.Contains(",") || value.Contains("\"")) return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        /// <summary>
        /// Atomic file replacement — uses File.Replace on Windows (ReplaceFile API) which
        /// ensures either the old or new file exists, never neither.
        /// </summary>
        internal static void AtomicReplace(string tmpPath, string targetPath)
        {
            if (File.Exists(targetPath))
                File.Replace(tmpPath, targetPath, null);
            else
                File.Move(tmpPath, targetPath);
        }

        /// <summary>Warn if the output drive has low free space before starting a long operation.</summary>
        static void WarnLowDiskSpace(string dir)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(dir));
                if (root != null)
                {
                    var drive = new DriveInfo(root);
                    if (drive.IsReady)
                    {
                        var freeMb = drive.AvailableFreeSpace / (1024.0 * 1024.0);
                        if (freeMb < 500)
                            Console.WriteLine($"WARNING: Low disk space ({freeMb:F0} MB free on {drive.Name})");
                    }
                }
            }
            catch { }
        }
    }
}
