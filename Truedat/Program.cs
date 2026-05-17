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
    }

    class TrackEntry
    {
        public TrackFeatures Features = null!;
        public DateTime LastModified;
        public double? AnalysisDurationSecs;
        public string? FileMd5;
        public string? AudioMd5;
        // Identity fields — already computed in the parallel scan task block but
        // historically dropped on the moods-file write path. Persisting them here
        // means every scanned track lands in mbxmoods.json with the full identity
        // signal set, no separate hash backfill needed.
        public string? AudioStreamSha256;
        public string? AudioStreamSha256Source;   // "whole-file" only when invariant bounds were unavailable
        public Program.FingerprintV1? FingerprintV1;
        public string? Chromaprint;
        public double? ChromaprintDuration;
    }

    public class TrackFingerprint
    {
        public int TrackId { get; set; }
        public string Artist { get; set; } = "";
        public string Title { get; set; } = "";
        public string Album { get; set; } = "";
        public string Genre { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string Chromaprint { get; set; } = "";
        public int Duration { get; set; }
        public string Md5 { get; set; } = "";
    }

    class FingerprintEntry
    {
        public TrackFingerprint Fp = null!;
        public DateTime LastModified;
        public string? FileMd5;
    }

    class AudioDetails
    {
        public int TrackId;
        public string Artist = "", Title = "";
        public string Codec = "", Format = "";
        public int Channels;
        public int SampleRate;
        public int BitRate;
        public int BitDepth;
        public double Duration;
        public double SizeMb;
        public DateTime LastProbed;
        public DateTime LastModified;
        public string? FileMd5;
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

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _file.Flush(); _file.Dispose(); }
            base.Dispose(disposing);
        }
    }

    class Program
    {
        static int _analyzeCount;
        static long _analyzeTicksTotal;
        static readonly Lazy<string?> _ffmpegPath = new Lazy<string?>(FindFfmpeg);
        static readonly Lazy<string?> _ffprobePath = new Lazy<string?>(FindFfprobe);
        static bool _audit;


        // Essentia streaming ChordsDetection buffer limit: 262144 elements (forMultipleFrames).
        // At 44100 Hz / 2048 hop size = ~21.53 frames/sec -> max ~12172s before buffer overflow.
        // Use 12000s (200 min) as safe limit with margin.
        const int MaxEssentiaDurationSecs = 12000;

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

        static void CleanupOrphanedFiles()
        {
            // Clean up orphaned hardlinks from .truedat-tmp directories on all drives
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady) continue;
                    var tmpDir = Path.Combine(drive.RootDirectory.FullName, ".truedat-tmp");
                    if (!Directory.Exists(tmpDir)) continue;
                    try
                    {
                        var files = Directory.GetFiles(tmpDir);
                        if (files.Length > 0)
                        {
                            Console.WriteLine($"  Cleaning {files.Length} orphaned hardlink(s) from {tmpDir}");
                            foreach (var f in files)
                                try { File.Delete(f); } catch { }
                        }
                        if (Directory.GetFiles(tmpDir).Length == 0)
                            try { Directory.Delete(tmpDir); } catch { }
                    }
                    catch { }
                }
            }
            catch { }

            // Clean up orphaned downmix WAV files from temp directory
            try
            {
                var tempDir = Path.GetTempPath();
                var orphans = Directory.GetFiles(tempDir, "truedat_stereo_*.wav");
                if (orphans.Length > 0)
                {
                    Console.WriteLine($"  Cleaning {orphans.Length} orphaned downmix file(s) from {tempDir}");
                    foreach (var f in orphans)
                        try { File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        /// <summary>Remove podcast episodes from a parsed track list and log the count.</summary>
        static List<ITunesTrack> FilterPodcasts(List<ITunesTrack> tracks)
        {
            int before = tracks.Count;
            var filtered = tracks.Where(t => !t.IsPodcast).ToList();
            int removed = before - filtered.Count;
            if (removed > 0)
                Console.WriteLine($"  Skipped {removed} podcast episode(s)");
            return filtered;
        }

        /// <summary>Video file extensions that should not be analyzed as audio.</summary>
        internal static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".m4v", ".mkv", ".avi", ".wmv", ".mov", ".webm", ".flv", ".mpg", ".mpeg", ".vob", ".ts"
        };

        /// <summary>Remove video files from a parsed track list and log the count.</summary>
        static List<ITunesTrack> FilterVideoFiles(List<ITunesTrack> tracks)
        {
            int before = tracks.Count;
            var filtered = tracks.Where(t =>
                string.IsNullOrEmpty(t.Location) ||
                !VideoExtensions.Contains(Path.GetExtension(t.Location))).ToList();
            int removed = before - filtered.Count;
            if (removed > 0)
                Console.WriteLine($"  Skipped {removed} video file(s)");
            return filtered;
        }

        /// <summary>Playlist / redirector file extensions that point at audio but
        /// aren't audio themselves. fpcalc + Essentia waste time on them and
        /// produce empty / nonsense output. Distinct from VideoExtensions so the
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
        /// Also skipped during --folder enumeration.
        /// </summary>
        static readonly HashSet<string> UnsupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".dsf",   // Sony DSD format
            ".dff"    // Philips DSDIFF format
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
        static List<ITunesTrack> FilterNonAudio(List<ITunesTrack> tracks)
        {
            int before = tracks.Count;
            var filtered = tracks.Where(t =>
                string.IsNullOrEmpty(t.Location) ||
                !NonAudioExtensions.Contains(Path.GetExtension(t.Location))).ToList();
            int removed = before - filtered.Count;
            if (removed > 0)
                Console.WriteLine($"  Skipped {removed} playlist / redirector file(s)");
            return filtered;
        }

        static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            var parallelism = Environment.ProcessorCount;
            string? xmlPath = null;
            bool fixupMode = false;
            bool verifyMode = false;
            bool retryErrors = false;
            bool migrateMode = false;
            bool fingerprintMode = false;
            bool chromaprintOnly = false;
            bool md5Only = false;
            bool auditLog = false;
            bool checkFilenames = false;
            bool duplicatesMode = false;
            bool quickFingerprintMode = false;
            bool detailsMode = false;
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
                else if (canonical == "verify") verifyMode = true;
                else if (canonical == "retry-errors") retryErrors = true;
                else if (canonical == "migrate") migrateMode = true;
                else if (canonical == "fingerprint") fingerprintMode = true;
                else if (canonical == "chromaprint-only") { fingerprintMode = true; chromaprintOnly = true; }
                else if (canonical == "md5-only") { fingerprintMode = true; md5Only = true; }
                else if (canonical == "details") { fingerprintMode = true; detailsMode = true; }
                else if (canonical == "analyze") analyzeMode = true;
                else if (canonical == "all") { fingerprintMode = true; detailsMode = true; analyzeMode = true; }
                else if (canonical == "audit") auditLog = true;
                else if (canonical == "check-filenames") checkFilenames = true;
                else if (canonical == "duplicates") duplicatesMode = true;
                else if (canonical == "quick-fingerprint") quickFingerprintMode = true;
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
                else if (canonical == "background") cpuLimit = 25;
                else if (canonical == "cpu-limit" && i + 1 < args.Length && int.TryParse(args[i + 1], out var cl) && cl >= 1 && cl <= 100) { cpuLimit = cl; i++; }
                else if (!arg.StartsWith("-") && !arg.StartsWith("/") && xmlPath == null) xmlPath = args[i];
            }

            _audit = auditLog;

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
            if (chunkTotal > 0 && (analyzeFileMode || fileListMode || migrateMode || fixupMode || verifyMode || mergeMode || synthesize || seedMoods || hashOnlyMode))
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
                if (analyzeFileMode || fileListMode || hashOnlyMode || migrateMode || fixupMode || verifyMode || mergeMode || synthesize || seedMoods || chunkTotal > 0)
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
                Console.WriteLine("Usage: truedat.exe <path-to-iTunes-Music-Library.xml> [options]");
                Console.WriteLine();
                Console.WriteLine("Options:");
                Console.WriteLine($"  -p, --parallel      Number of parallel threads (default: {Environment.ProcessorCount})");
                Console.WriteLine("  --fixup             Validate and remap paths in mbxmoods.json without re-analyzing");
                Console.WriteLine("  --verify            Recompute audioStreamSha256 for each entry, report drift / missing.");
                Console.WriteLine("                      Read-only. Use --moods <path> to verify a specific file.");
                Console.WriteLine("  --retry-errors      Re-attempt all previously failed files (clears error log)");
                Console.WriteLine("  --migrate           Clean up mbxmoods.json: strip legacy fields, remove podcast entries (creates backup)");
                Console.WriteLine("  --fingerprint       Run fingerprint mode (chromaprint + md5) -> mbxhub-fingerprints.json");
                Console.WriteLine("  --chromaprint-only  Fingerprint mode: only run chromaprint (skip md5)");
                Console.WriteLine("  --md5-only          Fingerprint mode: only run audio md5 (skip chromaprint)");
                Console.WriteLine("  --details           Probe audio files with ffprobe -> mbxhub-details.json (implies --fingerprint)");
                Console.WriteLine("  --analyze           Run analysis mode (Essentia -> mbxmoods.json), combinable with --fingerprint/--details");
                Console.WriteLine("  --all               Run all modes: fingerprint + details + analysis");
                Console.WriteLine("  --audit             Write all console output to truedat.log (for debugging)");
                Console.WriteLine("  --check-filenames   Scan paths for non-ASCII / problem chars + zero-byte / small files -> mbxhub-filenames.json");
                Console.WriteLine("  --duplicates        Find duplicate files from fingerprint data -> mbxhub-duplicates.json");
                Console.WriteLine("  --quick-fingerprint Use fpcalc to generate 30-second chromaprint -> mbxhub-quickfp.json");
                Console.WriteLine("  --analyze-file <f>  Analyze a single audio file with Essentia (no iTunes XML needed)");
                Console.WriteLine("  --file-list <path>  Analyze files listed in a text file (one path per line, UTF-8, # comments)");
                Console.WriteLine("                      Use '-' as <path> to read paths from stdin instead of a file");
                Console.WriteLine("                      Mutually exclusive with --analyze-file / --folder; -p sets parallelism");
                Console.WriteLine("  --folder <dir>      Walk <dir> recursively for audio files and analyze them");
                Console.WriteLine("                      Use --moods <path> to merge results into an existing mbxmoods.json");
                Console.WriteLine("  --output <path>     --hash-only mode: append identity envelopes as NDJSON to <path> (offline manifest)");
                Console.WriteLine("  --hash-only         Identity-only mode (no Essentia). Requires --level, --file-list, --output");
                Console.WriteLine("  --level <name>      With --hash-only: 'fingerprint' (cheap composite) or 'stream' (durable SHA-256)");
                Console.WriteLine("  --transcode <in>    Standalone: ffmpeg-transcode <in> to uncompressed FLAC. Requires --transcode-out.");
                Console.WriteLine("  --transcode-out <p> Output FLAC path for --transcode mode.");
                Console.WriteLine("  --sample-rate <hz>  With --transcode: override output sample rate (default: match source).");
                Console.WriteLine("  --bit-depth <16|24> With --transcode: override output bit depth (default: match source).");
                Console.WriteLine("  --background        Run child processes with 25% CPU cap (won't starve foreground apps)");
                Console.WriteLine("  --cpu-limit <n>     Cap child process CPU to n% (1-100, e.g. 20 for low-end machines)");
                Console.WriteLine("  --chunk M/N         Process shard M of N for two-machine same-library scans. Output");
                Console.WriteLine("                      auto-suffixed with hostname: mbxmoods.<host>.json. Combine via --merge-moods.");
                Console.WriteLine("  -?, --help          Show this help");
                Console.WriteLine();
                Console.WriteLine("Synthesize mode:");
                Console.WriteLine("  --synthesize        Generate a synthetic MusicBee library from a catalog");
                Console.WriteLine("  --catalog <path>    Path to catalog JSONL (.jsonl or .jsonl.gz)");
                Console.WriteLine("  --synth-output <dir> Output directory for synthesized library");
                Console.WriteLine("  --count <n>         Number of tracks to generate (default: 430000)");
                Console.WriteLine("  --album-ratio <r>   Fraction of tracks in albums vs singles (default: 0.5)");
                Console.WriteLine("  --synth-moods <path> Path to existing mbxmoods.json to merge into");
                Console.WriteLine("  --seed <n>          Random seed for reproducibility (default: 42)");
                Console.WriteLine("  --dry-run           Preview without writing files");
                Console.WriteLine();
                Console.WriteLine("Mood Seeding:");
                Console.WriteLine("  --seed-moods        Seed mbxmoods.json from AcousticBrainz catalog");
                Console.WriteLine("  --seed-catalog <path> Path to synthlib-catalog.jsonl.gz");
                Console.WriteLine("  --seed-target <path>  Target mbxmoods.json path (default: next to library XML)");
                Console.WriteLine("  <library.xml>       iTunes XML library file (positional)");
                Console.WriteLine();
                Console.WriteLine("Merge Moods:");
                Console.WriteLine("  --merge-moods       Merge multiple mbxmoods.json files into one");
                Console.WriteLine("  --merge-source <path> Source moods file (repeatable, at least 2)");
                Console.WriteLine("  --merge-output <path> Output moods file path");
                Console.WriteLine();
                Console.WriteLine("Optional: ffmpeg on PATH enables auto-downmix of multi-channel (5.1+) audio files.");
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
                Environment.ExitCode = RunVerify(verifyPath!, parallelism);
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
                if (string.IsNullOrEmpty(analyzeFilePath) || !File.Exists(analyzeFilePath))
                {
                    Console.Error.WriteLine($"Error: File not found: {analyzeFilePath}");
                    Environment.ExitCode = 1;
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
                var afKey = Path.GetFullPath(analyzeFilePath!);
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

                // Cache hierarchy — same tiers as MoodsMode and --file-list.
                if (afMoodsTracks != null)
                {
                    var afMoodMd5Index = BuildMd5Index(afMoodsTracks, e => e.FileMd5);
                    var afMoodShaIndex = BuildMd5Index(afMoodsTracks, e => e.AudioStreamSha256);

                    if (afMoodsTracks.TryGetValue(afKey, out var afEx)
                        && afEx.Features.DynamicRange.HasValue
                        && afEx.Features.LoudnessMomentary.HasValue)
                    {
                        // Tier 1: path-mtime
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
                        // Tier 2: path-sha (mtime drifted, audio bytes unchanged)
                        else if (!string.IsNullOrEmpty(afEx.AudioStreamSha256))
                        {
                            var (recompSha, _) = ComputeAudioStreamSha256FromFile(analyzeFilePath!, afFileSize, out _);
                            if (!string.IsNullOrEmpty(recompSha)
                                && string.Equals(recompSha, afEx.AudioStreamSha256, StringComparison.OrdinalIgnoreCase))
                            {
                                var refreshedMd5 = ComputeFileMd5(analyzeFilePath!);
                                var refreshedFp = ComputeFingerprintV1(analyzeFilePath!, afFileSize, out _);
                                var freshTags = ExtractFileTags(analyzeFilePath!);
                                trackEntry = RebuildCacheEntryFromTags(afEx, freshTags.Artist, freshTags.Title,
                                    freshTags.Album, freshTags.Genre, afKey, afCurrentLastMod, refreshedMd5, refreshedFp);
                                afHitTag = "cached·sha";
                                afFingerprintV1 = trackEntry.FingerprintV1;
                                afAudioStreamSha256 = trackEntry.AudioStreamSha256;
                                afAudioStreamSha256Source = trackEntry.AudioStreamSha256Source ?? "";
                            }
                        }
                    }

                    // Tier 3: cross-MD5
                    if (trackEntry == null && afMoodMd5Index != null)
                    {
                        var localMd5 = ComputeFileMd5(analyzeFilePath!);
                        if (!string.IsNullOrEmpty(localMd5)
                            && afMoodMd5Index.TryGetValue(localMd5!, out var xp)
                            && xp.Entry.Features.DynamicRange.HasValue
                            && xp.Entry.Features.LoudnessMomentary.HasValue)
                        {
                            var freshTags = ExtractFileTags(analyzeFilePath!);
                            trackEntry = RebuildCacheEntryFromTags(xp.Entry, freshTags.Artist, freshTags.Title,
                                freshTags.Album, freshTags.Genre, afKey, afCurrentLastMod, localMd5, null);
                            afMoodsTracks.TryRemove(xp.OldKey, out _);
                            afHitTag = "cached·md5";
                            afFingerprintV1 = trackEntry.FingerprintV1;
                            afAudioStreamSha256 = trackEntry.AudioStreamSha256;
                            afAudioStreamSha256Source = trackEntry.AudioStreamSha256Source ?? "";
                        }
                    }

                    // Tier 4: cross-SHA
                    if (trackEntry == null && afMoodShaIndex != null)
                    {
                        var (localSha, _) = ComputeAudioStreamSha256FromFile(analyzeFilePath!, afFileSize, out _);
                        if (!string.IsNullOrEmpty(localSha)
                            && afMoodShaIndex.TryGetValue(localSha!, out var xs)
                            && xs.Entry.Features.DynamicRange.HasValue
                            && xs.Entry.Features.LoudnessMomentary.HasValue)
                        {
                            var refreshedMd5 = ComputeFileMd5(analyzeFilePath!);
                            var refreshedFp = ComputeFingerprintV1(analyzeFilePath!, afFileSize, out _);
                            var freshTags = ExtractFileTags(analyzeFilePath!);
                            trackEntry = RebuildCacheEntryFromTags(xs.Entry, freshTags.Artist, freshTags.Title,
                                freshTags.Album, freshTags.Genre, afKey, afCurrentLastMod, refreshedMd5, refreshedFp);
                            afMoodsTracks.TryRemove(xs.OldKey, out _);
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
                    var afEssentiaTask = Task.Run(() => AnalyzeWithEssentia(afEssentiaExe, analyzeFilePath!, afFileSize, CancellationToken.None));
                    var afFileMd5Task = Task.Run(() => ComputeFileMd5(analyzeFilePath!));
                    var afFingerprintTask = Task.Run(() =>
                    {
                        var swFp = Stopwatch.StartNew();
                        var fp = ComputeFingerprintV1(analyzeFilePath!, afFileSize, out _);
                        swFp.Stop();
                        if (_audit)
                            Console.Error.WriteLine($"[AUDIT] taglibParseMs={swFp.ElapsedMilliseconds} file=\"{Path.GetFileName(analyzeFilePath)}\"");
                        return fp;
                    });
                    var afAudioStreamSha256Task = Task.Run(() =>
                    {
                        var swSha = Stopwatch.StartNew();
                        var result = ComputeAudioStreamSha256FromFile(analyzeFilePath!, afFileSize, out _);
                        swSha.Stop();
                        if (_audit)
                            Console.Error.WriteLine($"[AUDIT] audioStreamSha256Ms={swSha.ElapsedMilliseconds} file=\"{Path.GetFileName(analyzeFilePath)}\"");
                        return result;
                    });
                    var afTagsTask = Task.Run(() => ExtractFileTags(analyzeFilePath!));
                    Task.WaitAll(new Task[] { afEssentiaTask, afFileMd5Task, afFingerprintTask, afAudioStreamSha256Task, afTagsTask });

                    var (features, error) = afEssentiaTask.Result;
                    var afFileMd5 = afFileMd5Task.Result;
                    afFingerprintV1 = afFingerprintTask.Result;
                    var (sha, shaSrc) = afAudioStreamSha256Task.Result;
                    afAudioStreamSha256 = sha;
                    afAudioStreamSha256Source = shaSrc;
                    var afTags = afTagsTask.Result;

                    if (features == null)
                    {
                        Console.Error.WriteLine($"Error: {error}");
                        Environment.ExitCode = 3;
                        return;
                    }

                    features.Artist = afTags.Artist;
                    features.Title = afTags.Title;
                    features.Album = afTags.Album;
                    features.Genre = afTags.Genre;
                    features.FilePath = analyzeFilePath!;

                    trackEntry = new TrackEntry
                    {
                        Features = features,
                        LastModified = afCurrentLastMod == DateTime.MinValue ? File.GetLastWriteTimeUtc(analyzeFilePath) : afCurrentLastMod,
                        AnalysisDurationSecs = afSw.Elapsed.TotalSeconds,
                        FileMd5 = afFileMd5,
                        AudioStreamSha256 = string.IsNullOrEmpty(afAudioStreamSha256) ? null : afAudioStreamSha256,
                        AudioStreamSha256Source = afAudioStreamSha256Source,
                        FingerprintV1 = afFingerprintV1
                    };

                    // Carry forward legacy fields if entry already exists in the moods file.
                    if (afMoodsTracks != null && afMoodsTracks.TryGetValue(afKey, out var afPrior))
                    {
                        trackEntry.AudioMd5 ??= afPrior.AudioMd5;
                        trackEntry.Chromaprint ??= afPrior.Chromaprint;
                        trackEntry.ChromaprintDuration ??= afPrior.ChromaprintDuration;
                    }
                }

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
                }

                Console.Error.WriteLine($"Done ({afHitTag}) in {afSw.Elapsed.TotalSeconds:F1}s");
                Environment.ExitCode = 0;
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

                if (folderMode)
                {
                    Console.Error.WriteLine($"Walking folder: {folderPath}");
                    int unsupportedCount = 0;
                    var walked = new List<string>();
                    foreach (var p in Directory.EnumerateFiles(folderPath!, "*", SearchOption.AllDirectories))
                    {
                        var ext = Path.GetExtension(p);
                        if (string.IsNullOrEmpty(ext)) continue;
                        if (UnsupportedExtensions.Contains(ext)) { unsupportedCount++; continue; }
                        if (AudioExtensions.Contains(ext)) walked.Add(p);
                    }
                    filePaths = walked;
                    Console.Error.WriteLine($"  Found {filePaths.Count} audio file(s)" +
                        (unsupportedCount > 0 ? $" (skipped {unsupportedCount} unsupported DSD/DSF)" : ""));
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
                var flCachedByShaPath = 0;
                var flCachedByMd5Cross = 0;
                var flCachedByShaCross = 0;
                var flFailed = 0;
                var flErrors = new ConcurrentBag<string>();
                var flSw = System.Diagnostics.Stopwatch.StartNew();

                // Accumulate for moods file (optional)
                var flMoodsTracks = new ConcurrentDictionary<string, TrackEntry>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(analyzeFileMoods) && File.Exists(analyzeFileMoods))
                    LoadExistingMoods(analyzeFileMoods!, flMoodsTracks);

                // Cross-indexes for cache reuse — same shape as MoodsMode. Built once
                // before the worker pool spins up. Plugin-driven workflows where this
                // mode is invoked repeatedly (e.g. after MusicBee scans add new files)
                // get path-cache hits ~free; only genuinely new files run Essentia.
                Dictionary<string, (TrackEntry Entry, string OldKey)>? flMoodMd5Index =
                    BuildMd5Index(flMoodsTracks, e => e.FileMd5);
                Dictionary<string, (TrackEntry Entry, string OldKey)>? flMoodShaIndex =
                    BuildMd5Index(flMoodsTracks, e => e.AudioStreamSha256);
                if (flMoodsTracks.Count > 0)
                    Console.Error.WriteLine($"  Loaded {flMoodsTracks.Count} existing entries (md5={flMoodMd5Index?.Count ?? 0}, sha={flMoodShaIndex?.Count ?? 0})");

                Parallel.ForEach(filePaths, new ParallelOptions
                {
                    MaxDegreeOfParallelism = parallelism,
                    CancellationToken = CancellationToken.None
                }, filePath =>
                {
                    if (!File.Exists(filePath))
                    {
                        Interlocked.Increment(ref flFailed);
                        flErrors.Add($"{filePath}: file not found");
                        Console.Error.WriteLine($"[SKIP] {filePath}: file not found");
                        return;
                    }

                    try
                    {
                        var fullPath = Path.GetFullPath(filePath);
                        var hasMoods = !string.IsNullOrEmpty(analyzeFileMoods);

                        // Cache hierarchy — same tiers as MoodsMode. Plugin-driven
                        // workflows (re-invoke after MusicBee scan finds new files) get
                        // path-mtime hits ~free; only new / changed audio runs Essentia.
                        if (hasMoods)
                        {
                            // Tier 1: path-mtime hit
                            DateTime currentLastMod = DateTime.MinValue;
                            try { currentLastMod = File.GetLastWriteTimeUtc(filePath); } catch { }
                            if (flMoodsTracks.TryGetValue(fullPath, out var fEx)
                                && fEx.Features.DynamicRange.HasValue
                                && fEx.Features.LoudnessMomentary.HasValue)
                            {
                                if (TruncateToSeconds(currentLastMod) == TruncateToSeconds(fEx.LastModified))
                                {
                                    var freshTags = ExtractFileTags(filePath);
                                    flMoodsTracks[fullPath] = RebuildCacheEntryFromTags(
                                        fEx, freshTags.Artist, freshTags.Title, freshTags.Album,
                                        freshTags.Genre, fullPath, currentLastMod, null, null);
                                    Interlocked.Increment(ref flProcessed);
                                    Interlocked.Increment(ref flCachedByMtime);
                                    Console.Error.WriteLine($"[CACHED] {Path.GetFileName(filePath)}");
                                    return;
                                }

                                // Tier 2: path-sha hit (mtime drifted but audio bytes unchanged)
                                if (!string.IsNullOrEmpty(fEx.AudioStreamSha256))
                                {
                                    var fileSize2 = new FileInfo(filePath).Length;
                                    var (recomputedSha, _) = ComputeAudioStreamSha256FromFile(filePath, fileSize2, out _);
                                    if (!string.IsNullOrEmpty(recomputedSha)
                                        && string.Equals(recomputedSha, fEx.AudioStreamSha256, StringComparison.OrdinalIgnoreCase))
                                    {
                                        var refreshedMd5 = ComputeFileMd5(filePath);
                                        var refreshedFp = ComputeFingerprintV1(filePath, fileSize2, out _);
                                        var freshTags = ExtractFileTags(filePath);
                                        flMoodsTracks[fullPath] = RebuildCacheEntryFromTags(
                                            fEx, freshTags.Artist, freshTags.Title, freshTags.Album,
                                            freshTags.Genre, fullPath, currentLastMod, refreshedMd5, refreshedFp);
                                        Interlocked.Increment(ref flProcessed);
                                        Interlocked.Increment(ref flCachedByShaPath);
                                        Console.Error.WriteLine($"[CACHED·sha] {Path.GetFileName(filePath)}");
                                        return;
                                    }
                                }
                            }

                            // Tier 3: cross-MD5 hit (file at a different path, same bytes)
                            if (flMoodMd5Index != null)
                            {
                                var localMd5 = ComputeFileMd5(filePath);
                                if (!string.IsNullOrEmpty(localMd5)
                                    && flMoodMd5Index.TryGetValue(localMd5!, out var xp)
                                    && xp.Entry.Features.DynamicRange.HasValue
                                    && xp.Entry.Features.LoudnessMomentary.HasValue)
                                {
                                    var freshTags = ExtractFileTags(filePath);
                                    flMoodsTracks[fullPath] = RebuildCacheEntryFromTags(
                                        xp.Entry, freshTags.Artist, freshTags.Title, freshTags.Album,
                                        freshTags.Genre, fullPath, currentLastMod, localMd5, null);
                                    flMoodsTracks.TryRemove(xp.OldKey, out _);
                                    Interlocked.Increment(ref flProcessed);
                                    Interlocked.Increment(ref flCachedByMd5Cross);
                                    Console.Error.WriteLine($"[CACHED·md5] {Path.GetFileName(filePath)}");
                                    return;
                                }
                            }

                            // Tier 4: cross-SHA hit (file moved AND tag-edited)
                            if (flMoodShaIndex != null)
                            {
                                var fileSize3 = new FileInfo(filePath).Length;
                                var (localSha, _) = ComputeAudioStreamSha256FromFile(filePath, fileSize3, out _);
                                if (!string.IsNullOrEmpty(localSha)
                                    && flMoodShaIndex.TryGetValue(localSha!, out var xs)
                                    && xs.Entry.Features.DynamicRange.HasValue
                                    && xs.Entry.Features.LoudnessMomentary.HasValue)
                                {
                                    var refreshedMd5 = ComputeFileMd5(filePath);
                                    var refreshedFp = ComputeFingerprintV1(filePath, fileSize3, out _);
                                    var freshTags = ExtractFileTags(filePath);
                                    flMoodsTracks[fullPath] = RebuildCacheEntryFromTags(
                                        xs.Entry, freshTags.Artist, freshTags.Title, freshTags.Album,
                                        freshTags.Genre, fullPath, currentLastMod, refreshedMd5, refreshedFp);
                                    flMoodsTracks.TryRemove(xs.OldKey, out _);
                                    Interlocked.Increment(ref flProcessed);
                                    Interlocked.Increment(ref flCachedByShaCross);
                                    Console.Error.WriteLine($"[CACHED·sha] {Path.GetFileName(filePath)}");
                                    return;
                                }
                            }
                        }

                        var fileSize = new FileInfo(filePath).Length;

                        // Identity ride-along — same concurrency pattern as MoodsMode at :1497-1513.
                        // Without this, --file-list mode was POSTing identity:{} (features-only).
                        var essentiaTask = Task.Run(() => AnalyzeWithEssentia(flEssentiaExe, filePath, fileSize, CancellationToken.None));
                        var fileMd5Task = Task.Run(() => ComputeFileMd5(filePath));
                        var fingerprintTask = Task.Run(() =>
                        {
                            var swFp = Stopwatch.StartNew();
                            var fp = ComputeFingerprintV1(filePath, fileSize, out _);
                            swFp.Stop();
                            if (_audit)
                                Console.Error.WriteLine($"[AUDIT] taglibParseMs={swFp.ElapsedMilliseconds} file=\"{Path.GetFileName(filePath)}\"");
                            return fp;
                        });
                        var audioStreamSha256Task = Task.Run(() =>
                        {
                            var swSha = Stopwatch.StartNew();
                            var result = ComputeAudioStreamSha256FromFile(filePath, fileSize, out _);
                            swSha.Stop();
                            if (_audit)
                                Console.Error.WriteLine($"[AUDIT] audioStreamSha256Ms={swSha.ElapsedMilliseconds} file=\"{Path.GetFileName(filePath)}\"");
                            return result;
                        });
                        // Tags ride-along — --file-list has no iTunes XML source, so populate
                        // artist/title/album/genre/duration from TagLib tags. Without this,
                        // identity.metadataKey on the server is empty for every local scan.
                        var tagsTask = Task.Run(() => ExtractFileTags(filePath));
                        Task.WaitAll(new Task[] { essentiaTask, fileMd5Task, fingerprintTask, audioStreamSha256Task, tagsTask });

                        var (features, error) = essentiaTask.Result;
                        var fileMd5 = fileMd5Task.Result;
                        var fingerprintV1 = fingerprintTask.Result;
                        var (audioStreamSha256, audioStreamSha256Source) = audioStreamSha256Task.Result;
                        var tags = tagsTask.Result;

                        if (features == null)
                        {
                            Interlocked.Increment(ref flFailed);
                            flErrors.Add($"{filePath}: {error}");
                            Console.Error.WriteLine($"[FAIL] {Path.GetFileName(filePath)}: {error}");
                            return;
                        }

                        features.Artist = tags.Artist;
                        features.Title = tags.Title;
                        features.Album = tags.Album;
                        features.Genre = tags.Genre;
                        features.FilePath = filePath;

                        var trackEntry = new TrackEntry
                        {
                            Features = features,
                            LastModified = File.GetLastWriteTimeUtc(filePath),
                            AnalysisDurationSecs = 0, // individual timing not tracked in batch
                            FileMd5 = fileMd5,
                            AudioStreamSha256 = string.IsNullOrEmpty(audioStreamSha256) ? null : audioStreamSha256,
                            AudioStreamSha256Source = audioStreamSha256Source,
                            FingerprintV1 = fingerprintV1
                        };

                        // Accumulate for moods file (only saved if --moods is set).
                        if (!string.IsNullOrEmpty(analyzeFileMoods))
                        {
                            var flKey = Path.GetFullPath(filePath);
                            // Carry forward legacy fields that this mode doesn't compute,
                            // so a user with prior --fingerprint output doesn't silently lose them.
                            if (flMoodsTracks.TryGetValue(flKey, out var flPrior))
                            {
                                trackEntry.AudioMd5 ??= flPrior.AudioMd5;
                                trackEntry.Chromaprint ??= flPrior.Chromaprint;
                                trackEntry.ChromaprintDuration ??= flPrior.ChromaprintDuration;
                            }
                            flMoodsTracks[flKey] = trackEntry;
                        }

                        Interlocked.Increment(ref flProcessed);
                        Interlocked.Increment(ref flAnalyzed);
                        Console.Error.WriteLine($"[OK] {Path.GetFileName(filePath)}");
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref flFailed);
                        flErrors.Add($"{filePath}: {ex.Message}");
                        Console.Error.WriteLine($"[FAIL] {Path.GetFileName(filePath)}: {ex.Message}");
                    }
                });

                flSw.Stop();

                // Save moods file if --moods specified
                if (!string.IsNullOrEmpty(analyzeFileMoods) && flMoodsTracks.Count > 0)
                {
                    SaveResults(analyzeFileMoods!, flMoodsTracks);
                    Console.Error.WriteLine($"Saved {flMoodsTracks.Count} entries to: {analyzeFileMoods}");
                }

                int flCachedTotal = flCachedByMtime + flCachedByShaPath + flCachedByMd5Cross + flCachedByShaCross;

                // Summary JSON on stdout
                if (jsonOutput || flFailed > 0)
                {
                    var summary = new
                    {
                        processed = flProcessed,
                        analyzed = flAnalyzed,
                        cached = flCachedTotal,
                        cachedByMtime = flCachedByMtime,
                        cachedByShaPath = flCachedByShaPath,
                        cachedByMd5Cross = flCachedByMd5Cross,
                        cachedByShaCross = flCachedByShaCross,
                        failed = flFailed,
                        elapsed = flSw.Elapsed.TotalSeconds,
                        errors = flErrors.ToArray()
                    };
                    var summaryJson = System.Text.Json.JsonSerializer.Serialize(summary,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    Console.WriteLine(summaryJson);
                }

                Console.Error.WriteLine($"Done: {flProcessed} processed ({flCachedTotal} cached, {flAnalyzed} analyzed), {flFailed} failed in {flSw.Elapsed.TotalSeconds:F1}s");
                Environment.ExitCode = flFailed > 0 ? 1 : 0;
                return;
            }

            xmlPath = xmlPath ?? "iTunes Music Library.xml";

            if (!File.Exists(xmlPath))
            {
                Console.WriteLine($"iTunes library not found: {xmlPath}");
                Console.WriteLine("Usage: truedat.exe <path-to-iTunes-Music-Library.xml> [options]");
                Console.WriteLine();
                Console.WriteLine("Options:");
                Console.WriteLine($"  -p, --parallel      Number of parallel threads (default: {Environment.ProcessorCount})");
                Console.WriteLine("  --fixup             Validate and remap paths in mbxmoods.json without re-analyzing");
                Console.WriteLine("  --verify            Recompute audioStreamSha256 for each entry, report drift / missing.");
                Console.WriteLine("                      Read-only. Use --moods <path> to verify a specific file.");
                Console.WriteLine("  --retry-errors      Re-attempt all previously failed files (clears error log)");
                Console.WriteLine("  --migrate           Clean up mbxmoods.json: strip legacy fields, remove podcast entries (creates backup)");
                Console.WriteLine("  --fingerprint       Run fingerprint mode (chromaprint + md5) -> mbxhub-fingerprints.json");
                Console.WriteLine("  --chromaprint-only  Fingerprint mode: only run chromaprint (skip md5)");
                Console.WriteLine("  --md5-only          Fingerprint mode: only run audio md5 (skip chromaprint)");
                Console.WriteLine("  --details           Probe audio files with ffprobe -> mbxhub-details.json (implies --fingerprint)");
                Console.WriteLine("  --analyze           Run analysis mode (Essentia -> mbxmoods.json), combinable with --fingerprint/--details");
                Console.WriteLine("  --all               Run all modes: fingerprint + details + analysis");
                Console.WriteLine("  --audit             Write all console output to truedat.log (for debugging)");
                Console.WriteLine("  --check-filenames   Scan paths for non-ASCII / problem chars + zero-byte / small files -> mbxhub-filenames.json");
                Console.WriteLine("  --duplicates        Find duplicate files from fingerprint data -> mbxhub-duplicates.json");
                Console.WriteLine("  --quick-fingerprint Use fpcalc to generate 30-second chromaprint -> mbxhub-quickfp.json");
                Console.WriteLine("  --analyze-file <f>  Analyze a single audio file with Essentia (no iTunes XML needed)");
                Console.WriteLine("  --file-list <path>  Analyze files listed in a text file (one path per line, UTF-8, # comments)");
                Console.WriteLine("                      Use '-' as <path> to read paths from stdin instead of a file");
                Console.WriteLine("                      Mutually exclusive with --analyze-file / --folder; -p sets parallelism");
                Console.WriteLine("  --folder <dir>      Walk <dir> recursively for audio files and analyze them");
                Console.WriteLine("                      Use --moods <path> to merge results into an existing mbxmoods.json");
                Console.WriteLine("  --output <path>     --hash-only mode: append identity envelopes as NDJSON to <path> (offline manifest)");
                Console.WriteLine("  --hash-only         Identity-only mode (no Essentia). Requires --level, --file-list, --output");
                Console.WriteLine("  --level <name>      With --hash-only: 'fingerprint' (cheap composite) or 'stream' (durable SHA-256)");
                Console.WriteLine("  --transcode <in>    Standalone: ffmpeg-transcode <in> to uncompressed FLAC. Requires --transcode-out.");
                Console.WriteLine("  --transcode-out <p> Output FLAC path for --transcode mode.");
                Console.WriteLine("  --sample-rate <hz>  With --transcode: override output sample rate (default: match source).");
                Console.WriteLine("  --bit-depth <16|24> With --transcode: override output bit depth (default: match source).");
                Console.WriteLine("  --background        Run child processes with 25% CPU cap (won't starve foreground apps)");
                Console.WriteLine("  --cpu-limit <n>     Cap child process CPU to n% (1-100, e.g. 20 for low-end machines)");
                Console.WriteLine("  --chunk M/N         Process shard M of N for two-machine same-library scans. Output");
                Console.WriteLine("                      auto-suffixed with hostname: mbxmoods.<host>.json. Combine via --merge-moods.");
                Console.WriteLine("  -?, --help          Show this help");
                Console.WriteLine();
                Console.WriteLine("Synthesize mode:");
                Console.WriteLine("  --synthesize        Generate a synthetic MusicBee library from a catalog");
                Console.WriteLine("  --catalog <path>    Path to catalog JSONL (.jsonl or .jsonl.gz)");
                Console.WriteLine("  --synth-output <dir> Output directory for synthesized library");
                Console.WriteLine("  --count <n>         Number of tracks to generate (default: 430000)");
                Console.WriteLine("  --album-ratio <r>   Fraction of tracks in albums vs singles (default: 0.5)");
                Console.WriteLine("  --synth-moods <path> Path to existing mbxmoods.json to merge into");
                Console.WriteLine("  --seed <n>          Random seed for reproducibility (default: 42)");
                Console.WriteLine("  --dry-run           Preview without writing files");
                Console.WriteLine();
                Console.WriteLine("Mood Seeding:");
                Console.WriteLine("  --seed-moods        Seed mbxmoods.json from AcousticBrainz catalog");
                Console.WriteLine("  --seed-catalog <path> Path to synthlib-catalog.jsonl.gz");
                Console.WriteLine("  --seed-target <path>  Target mbxmoods.json path (default: next to library XML)");
                Console.WriteLine("  <library.xml>       iTunes XML library file (positional)");
                Console.WriteLine();
                Console.WriteLine("Merge Moods:");
                Console.WriteLine("  --merge-moods       Merge multiple mbxmoods.json files into one");
                Console.WriteLine("  --merge-source <path> Source moods file (repeatable, at least 2)");
                Console.WriteLine("  --merge-output <path> Output moods file path");
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
            }

            var modeList = new List<string>();
            if (checkFilenames) modeList.Add("check-filenames");
            if (duplicatesMode) modeList.Add("duplicates");
            if (migrateMode) modeList.Add("migrate");
            if (fixupMode) modeList.Add("fixup");
            if (fingerprintMode) modeList.Add(chromaprintOnly ? "chromaprint-only" : md5Only ? "md5-only" : "fingerprint");
            if (detailsMode) modeList.Add("details");
            if (analyzeMode || (!checkFilenames && !duplicatesMode && !migrateMode && !fixupMode && !fingerprintMode)) modeList.Add("analyze");
            Console.WriteLine($"  Modes: {string.Join("+", modeList)} | Parallelism: {parallelism}{(retryErrors ? " | RetryErrors" : "")}");

            // Clean up orphaned hardlinks from previous crashed runs
            CleanupOrphanedFiles();

            if (checkFilenames) { RunCheckFilenames(xmlPath, outputDir); if (auditLog) Console.WriteLine($"Log:    {logPath}"); tee?.Dispose(); return; }
            if (duplicatesMode) { RunDuplicates(outputDir); if (auditLog) Console.WriteLine($"Log:    {logPath}"); tee?.Dispose(); return; }
            if (migrateMode) { RunMigrate(moodsPath); if (auditLog) Console.WriteLine($"Log:    {logPath}"); tee?.Dispose(); return; }
            if (fixupMode) { RunFixup(xmlPath, moodsPath); if (auditLog) Console.WriteLine($"Log:    {logPath}"); tee?.Dispose(); return; }
            if (fingerprintMode)
            {
                RunFingerprint(xmlPath, outputDir, parallelism, retryErrors, chromaprintOnly, md5Only, detailsMode);
                if (!analyzeMode) { if (auditLog) Console.WriteLine($"Log:    {logPath}"); tee?.Dispose(); return; }
                Console.WriteLine();
            }

            if (quickFingerprintMode)
            {
                RunQuickFingerprint(xmlPath, outputDir, parallelism, retryErrors);
                if (!analyzeMode && !fingerprintMode) { if (auditLog) Console.WriteLine($"Log:    {logPath}"); tee?.Dispose(); return; }
                Console.WriteLine();
            }

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var essentiaExe = FindTool("essentia_streaming_extractor_music.exe", baseDir, outputDir, Environment.CurrentDirectory);
            var catalogPath = FindCatalog(baseDir, Environment.CurrentDirectory,
                Path.GetDirectoryName(Path.GetDirectoryName(baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))) ?? "");

            Console.WriteLine("=== Tool Check ===");
            Console.WriteLine($"  App dir:    {baseDir}");
            Console.WriteLine($"  Output dir: {outputDir}");
            Console.WriteLine($"  Essentia:   {essentiaExe ?? "NOT FOUND"}");
            Console.WriteLine($"  ffmpeg:     {_ffmpegPath.Value ?? "not found (multi-channel files will be skipped)"}");
            Console.WriteLine($"  Catalog:    {(catalogPath != null ? catalogPath : "not found (run: python src/catalog-prep.py --download --build)")}");
            Console.WriteLine();

            if (essentiaExe == null)
            {
                Console.WriteLine("Essentia extractor not found in any search directory.");
                Console.WriteLine("Download from: https://essentia.upf.edu/extractors/");
                tee?.Dispose();
                return;
            }

            var errorsPath = Path.Combine(outputDir, "mbxmoods-errors.csv");
            if (chunkHostSuffix != null)
                errorsPath = InsertFilenameSuffix(errorsPath, chunkHostSuffix);

            Console.WriteLine($"Loading iTunes library: {xmlPath}");
            var tracks = ITunesParser.Parse(xmlPath, out var xmlIssues);
            if (_audit && xmlIssues != null)
                foreach (var issue in xmlIssues) Console.WriteLine(issue);
            Console.WriteLine($"Found {tracks.Count} tracks");
            tracks = FilterPodcasts(tracks);
            tracks = FilterVideoFiles(tracks);
            tracks = FilterNonAudio(tracks);

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
                tracks = tracks
                    .Where(t => !string.IsNullOrEmpty(t.Location)
                        && (PathComparer.Instance.GetHashCode(t.Location) & 0x7FFFFFFF) % chunkTotal == bucket)
                    .ToList();
                Console.WriteLine($"Chunk {chunkIndex}/{chunkTotal} on {Environment.MachineName}: {tracks.Count} of {n} tracks (hash-mod, bucket {bucket})");
                Console.WriteLine($"Output: {moodsPath}");
            }

            // Single in-memory dataset — loaded from disk once, updated by workers, streamed on save.
            // Eliminates the old pattern of re-reading/re-parsing the entire JSON on every save.
            var allTracks = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
            int existingCount = LoadExistingMoods(moodsPath, allTracks);
            Console.WriteLine($"Existing moods: {existingCount}");
            Dictionary<string, (TrackEntry Entry, string OldKey)>? moodMd5Index = BuildMd5Index(allTracks, e => e.FileMd5);
            if (moodMd5Index != null)
                Console.WriteLine($"  MD5 index:  {moodMd5Index.Count} entries available for cross-machine matching");
            // Secondary cross-index keyed by audioStreamSha256 — invariant-region hash,
            // stable across tag edits. Catches "audio bytes unchanged but tags drifted"
            // (cross-MD5 misses these because fileMd5 covers the whole file including tags).
            Dictionary<string, (TrackEntry Entry, string OldKey)>? moodShaIndex = BuildMd5Index(allTracks, e => e.AudioStreamSha256);
            if (moodShaIndex != null)
                Console.WriteLine($"  SHA index:  {moodShaIndex.Count} entries available for tag-edit / cross-machine matching");
            int crossPathMoods = 0;
            int cachedByShaPath = 0;   // tier A: same path, mtime drifted, audio bytes unchanged
            int cachedByShaCross = 0;  // tier B: different path, audio bytes unchanged

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
                Parallel.ForEach(tracks, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cts.Token }, t =>
                {
                    if (cts.IsCancellationRequested) return;
                    var current = Interlocked.Increment(ref processed);

                    try
                    {
                        var pct = (current * 100) / total;
                        var eta = FormatEta(sw.Elapsed, current, total);

                        if (existingErrors.TryGetValue(t.Location, out var prevError))
                        {
                            Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name} (skip: {prevError})");
                            Interlocked.Increment(ref skipped);
                            return;
                        }

                        // Check if already in moods and file unchanged
                        if (allTracks.TryGetValue(t.Location, out var existing))
                        {
                            try
                            {
                                var currentLastMod = File.GetLastWriteTimeUtc(t.Location);
                                if (TruncateToSeconds(currentLastMod) == TruncateToSeconds(existing.LastModified))
                                {
                                    // Re-extract when DR or the extended-feature canary is missing.
                                    // Older entries from pre-LRA or pre-extended-feature builds need
                                    // a fresh Essentia pass to backfill the current schema.
                                    if (!existing.Features.DynamicRange.HasValue
                                        || !existing.Features.LoudnessMomentary.HasValue)
                                    {
                                        if (_audit) Console.WriteLine($"  DEBUG cache: re-extracting (DR / extended missing)");
                                    }
                                    else
                                    {
                                        // Path-cache hit: same path, same mtime, canary passed.
                                        // Backfill fileMd5 if older entry lacked it.
                                        var refreshedMd5 = existing.FileMd5 ?? ComputeFileMd5(t.Location);
                                        allTracks[t.Location] = RebuildCacheEntry(existing, t, currentLastMod, refreshedMd5, null);
                                        Interlocked.Increment(ref cachedCount);
                                        Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name} (cached)");
                                        return;
                                    }
                                }
                                else
                                {
                                    // Mtime mismatched. Try the audioStreamSha256 path-tier:
                                    // if the audio bytes are unchanged (only tags / container
                                    // metadata drifted), reuse Essentia features and refresh
                                    // identity fields the tag edit invalidated.
                                    if (!string.IsNullOrEmpty(existing.AudioStreamSha256)
                                        && existing.Features.DynamicRange.HasValue
                                        && existing.Features.LoudnessMomentary.HasValue)
                                    {
                                        long fileSize = 0;
                                        try { fileSize = new FileInfo(t.Location).Length; } catch { }
                                        if (fileSize > 0)
                                        {
                                            var (recomputedSha, _) = ComputeAudioStreamSha256FromFile(t.Location, fileSize, out _);
                                            if (!string.IsNullOrEmpty(recomputedSha)
                                                && string.Equals(recomputedSha, existing.AudioStreamSha256, StringComparison.OrdinalIgnoreCase))
                                            {
                                                // Audio bytes unchanged — tag edit only. Refresh
                                                // fileMd5 + fingerprint.v1 (both tag-affected),
                                                // reuse everything else.
                                                var refreshedMd5 = ComputeFileMd5(t.Location);
                                                var refreshedFp = ComputeFingerprintV1(t.Location, fileSize, out _);
                                                allTracks[t.Location] = RebuildCacheEntry(existing, t, currentLastMod, refreshedMd5, refreshedFp);
                                                Interlocked.Increment(ref cachedCount);
                                                Interlocked.Increment(ref cachedByShaPath);
                                                Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name} (cached·sha)");
                                                return;
                                            }
                                        }
                                    }
                                    if (_audit)
                                        Console.WriteLine($"  DEBUG cache: stale (file:{currentLastMod:o} != cached:{existing.LastModified:o})");
                                }
                            }
                            catch (Exception ex) { if (_audit) Console.WriteLine($"  DEBUG cache: lastmod error: {ex.Message}"); }
                        }

                        // Cross-machine MD5 fallback — same file at a different path
                        if (moodMd5Index != null)
                        {
                            var localMd5 = ComputeFileMd5(t.Location);
                            if (localMd5 != null && moodMd5Index.TryGetValue(localMd5, out var xp))
                            {
                                var xf = xp.Entry.Features;
                                // Same fall-through as the path-cache branch — missing DR or extended
                                // canary means we can't reuse the MD5-matched entry; fresh Essentia needed.
                                if (!xf.DynamicRange.HasValue
                                    || !xf.LoudnessMomentary.HasValue)
                                {
                                    if (_audit) Console.WriteLine($"  DEBUG cache-md5: re-extracting (DR / extended missing)");
                                }
                                else
                                {
                                    var currentLastMod = DateTime.MinValue;
                                    try { currentLastMod = File.GetLastWriteTimeUtc(t.Location); } catch { }
                                    allTracks[t.Location] = new TrackEntry
                                    {
                                        Features = new TrackFeatures
                                        {
                                            TrackId = t.TrackId, Artist = t.Artist, Title = t.Name,
                                            Album = t.Album, Genre = t.Genre, FilePath = t.Location,
                                            Bpm = xf.Bpm, Key = xf.Key, Mode = xf.Mode,
                                            SpectralCentroid = xf.SpectralCentroid, SpectralFlux = xf.SpectralFlux,
                                            Loudness = xf.Loudness, Danceability = xf.Danceability,
                                            OnsetRate = xf.OnsetRate, ZeroCrossingRate = xf.ZeroCrossingRate,
                                            SpectralRms = xf.SpectralRms, SpectralFlatness = xf.SpectralFlatness,
                                            Dissonance = xf.Dissonance, PitchSalience = xf.PitchSalience,
                                            ChordsChangesRate = xf.ChordsChangesRate, Mfcc = xf.Mfcc,
                                            DynamicRange = xf.DynamicRange,
                                            DynamicRangeSource = xf.DynamicRangeSource,
                                            // Extended features carried through the cross-MD5 cache copy.
                                            LoudnessMomentary = xf.LoudnessMomentary,
                                            LoudnessShortTerm = xf.LoudnessShortTerm,
                                            ReplayGain = xf.ReplayGain,
                                            SilenceRate20dB = xf.SilenceRate20dB,
                                            SilenceRate30dB = xf.SilenceRate30dB,
                                            SilenceRate60dB = xf.SilenceRate60dB,
                                            SpectralRolloff = xf.SpectralRolloff,
                                            SpectralComplexity = xf.SpectralComplexity,
                                            SpectralEntropy = xf.SpectralEntropy,
                                            SpectralKurtosis = xf.SpectralKurtosis,
                                            SpectralSkewness = xf.SpectralSkewness,
                                            SpectralSpread = xf.SpectralSpread,
                                            SpectralStrongPeak = xf.SpectralStrongPeak,
                                            SpectralDecrease = xf.SpectralDecrease,
                                            SpectralEnergy = xf.SpectralEnergy,
                                            SpectralEnergyLow = xf.SpectralEnergyLow,
                                            SpectralEnergyMidLow = xf.SpectralEnergyMidLow,
                                            SpectralEnergyMidHigh = xf.SpectralEnergyMidHigh,
                                            SpectralEnergyHigh = xf.SpectralEnergyHigh,
                                            Hfc = xf.Hfc,
                                            BarkCrest = xf.BarkCrest,
                                            BarkFlatness = xf.BarkFlatness,
                                            BarkKurtosis = xf.BarkKurtosis,
                                            BarkSkewness = xf.BarkSkewness,
                                            BarkSpread = xf.BarkSpread,
                                            ErbCrest = xf.ErbCrest,
                                            ErbFlatness = xf.ErbFlatness,
                                            ErbKurtosis = xf.ErbKurtosis,
                                            ErbSkewness = xf.ErbSkewness,
                                            ErbSpread = xf.ErbSpread,
                                            MelCrest = xf.MelCrest,
                                            MelFlatness = xf.MelFlatness,
                                            MelKurtosis = xf.MelKurtosis,
                                            MelSkewness = xf.MelSkewness,
                                            MelSpread = xf.MelSpread,
                                            BeatsLoudness = xf.BeatsLoudness,
                                            ChordsStrength = xf.ChordsStrength,
                                            HpcpCrest = xf.HpcpCrest,
                                            HpcpEntropy = xf.HpcpEntropy
                                        },
                                        LastModified = currentLastMod,
                                        AnalysisDurationSecs = xp.Entry.AnalysisDurationSecs,
                                        FileMd5 = localMd5,
                                        AudioMd5 = xp.Entry.AudioMd5,
                                        // Cross-machine MD5 means same audio bytes — identity hashes are
                                        // bit-for-bit reusable, no recompute needed.
                                        AudioStreamSha256 = xp.Entry.AudioStreamSha256,
                                        AudioStreamSha256Source = xp.Entry.AudioStreamSha256Source,
                                        FingerprintV1 = xp.Entry.FingerprintV1,
                                        Chromaprint = xp.Entry.Chromaprint,
                                        ChromaprintDuration = xp.Entry.ChromaprintDuration
                                    };
                                    allTracks.TryRemove(xp.OldKey, out _);
                                    Interlocked.Increment(ref crossPathMoods);
                                    Interlocked.Increment(ref cachedCount);
                                    Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name} (cached\u00b7md5)");
                                    return;
                                }
                            }
                        }

                        // Cross-machine SHA fallback \u2014 same audio bytes (invariant region) at
                        // a different path. Catches "moved + tag-edited" where cross-MD5 misses
                        // because fileMd5 covers the whole file. Cost is ~50ms managed SHA-NI
                        // per cache miss that gets here.
                        if (moodShaIndex != null)
                        {
                            long fileSizeForSha = 0;
                            try { fileSizeForSha = new FileInfo(t.Location).Length; } catch { }
                            if (fileSizeForSha > 0)
                            {
                                var (localSha, _) = ComputeAudioStreamSha256FromFile(t.Location, fileSizeForSha, out _);
                                if (!string.IsNullOrEmpty(localSha) && moodShaIndex.TryGetValue(localSha!, out var xs))
                                {
                                    var xsf = xs.Entry.Features;
                                    if (!xsf.DynamicRange.HasValue || !xsf.LoudnessMomentary.HasValue)
                                    {
                                        if (_audit) Console.WriteLine($"  DEBUG cache-sha: re-extracting (DR / extended missing)");
                                    }
                                    else
                                    {
                                        // Audio bytes match. fileMd5 likely differs (else cross-MD5
                                        // would have caught it); fingerprint.v1 is also tag-affected.
                                        // Recompute both, reuse Essentia features.
                                        var refreshedMd5 = ComputeFileMd5(t.Location);
                                        var refreshedFp = ComputeFingerprintV1(t.Location, fileSizeForSha, out _);
                                        var currentLastMod = DateTime.MinValue;
                                        try { currentLastMod = File.GetLastWriteTimeUtc(t.Location); } catch { }
                                        allTracks[t.Location] = RebuildCacheEntry(xs.Entry, t, currentLastMod, refreshedMd5, refreshedFp);
                                        allTracks.TryRemove(xs.OldKey, out _);
                                        Interlocked.Increment(ref crossPathMoods);
                                        Interlocked.Increment(ref cachedByShaCross);
                                        Interlocked.Increment(ref cachedCount);
                                        Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name} (cached\u00b7sha)");
                                        return;
                                    }
                                }
                            }
                        }

                        long fileSizeBytes = 0;
                        var sizeTag = "";
                        try
                        {
                            fileSizeBytes = new FileInfo(t.Location).Length;
                            var sizeMb = fileSizeBytes / (1024.0 * 1024.0);
                            if (sizeMb >= 100) sizeTag = $" [{sizeMb:F0} MB]";
                        }
                        catch { }

                        Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name}{sizeTag}");

                        // Pre-flight: skip files that exceed Essentia's ChordsDetection buffer
                        var trackDurationSecs = t.TotalTimeMs / 1000;
                        if (trackDurationSecs > MaxEssentiaDurationSecs)
                        {
                            var durationMin = trackDurationSecs / 60.0;
                            var limitMin = MaxEssentiaDurationSecs / 60.0;
                            var sizeMb = fileSizeBytes / (1024.0 * 1024.0);
                            var msg = $"Skipped: duration {durationMin:F0} min exceeds Essentia ChordsDetection buffer limit ({limitMin:F0} min)";
                            Console.WriteLine($"  WARNING: {msg}");
                            AppendError(errorsPath, t.Location, t.Artist, t.Name, msg, sizeMb, 0, saveLock);
                            Interlocked.Increment(ref failed);
                            return;
                        }

                        // Run Essentia analysis and the managed hashes concurrently — disk/CPU
                        // profiles overlap so wall-clock per track is roughly max(analysis, hash)
                        // rather than the sum. All hashes are pure-managed I/O; the audio MD5
                        // and chromaprint subprocesses (essentia_streaming_md5, fpcalc) are no
                        // longer in the default codepath — see --fingerprint mode for those.
                        var analyzeStart = Stopwatch.GetTimestamp();
                        var essentiaTask = Task.Run(() => AnalyzeWithEssentia(essentiaExe, t.Location, fileSizeBytes, cts.Token));
                        var fileMd5Task = Task.Run(() => ComputeFileMd5(t.Location));
                        // fingerprint.v1 ride-along — ~5ms TagLib parse + 64KB MD5.
                        // Phase 2 cheap identity signal; cost is negligible vs Essentia.
                        var fingerprintTask = Task.Run(() =>
                        {
                            var swFp = Stopwatch.StartNew();
                            var fp = ComputeFingerprintV1(t.Location, fileSizeBytes, out _);
                            swFp.Stop();
                            if (_audit)
                                Console.Error.WriteLine($"[AUDIT] taglibParseMs={swFp.ElapsedMilliseconds} file=\"{Path.GetFileName(t.Location)}\"");
                            return fp;
                        });
                        // audioStreamSha256 ride-along — ~100ms/file with SHA-NI over the audio
                        // region. Persisted on the TrackEntry alongside the other identity
                        // signals (fingerprint.v1, chromaprint, fileMd5, audioMd5) so a default
                        // `truedat <iTunes-XML>` produces max output in one pass. Runs
                        // concurrently so it overlaps Essentia's much longer decode.
                        var audioStreamSha256Task = Task.Run(() =>
                        {
                            var swSha = Stopwatch.StartNew();
                            var result = ComputeAudioStreamSha256FromFile(t.Location, fileSizeBytes, out _);
                            swSha.Stop();
                            if (_audit)
                                Console.Error.WriteLine($"[AUDIT] audioStreamSha256Ms={swSha.ElapsedMilliseconds} file=\"{Path.GetFileName(t.Location)}\"");
                            return result;
                        });
                        Task.WaitAll(new Task[] { essentiaTask, fileMd5Task, fingerprintTask, audioStreamSha256Task });
                        var analyzeTicks = Stopwatch.GetTimestamp() - analyzeStart;
                        var analyzeDuration = StopwatchTicksToTimeSpan(analyzeTicks);
                        Interlocked.Add(ref _analyzeTicksTotal, analyzeTicks);
                        Interlocked.Increment(ref _analyzeCount);

                        var (feat, errorReason) = essentiaTask.Result;
                        var fileMd5 = fileMd5Task.Result;
                        var fingerprintV1 = fingerprintTask.Result;
                        var (audioStreamSha256, audioStreamSha256Source) = audioStreamSha256Task.Result;

                        if (feat == null)
                        {
                            var err = errorReason ?? "Unknown error";
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

                        var lastMod = DateTime.MinValue;
                        try { lastMod = File.GetLastWriteTimeUtc(t.Location); } catch { }

                        // Carry forward legacy fields the default codepath doesn't compute
                        // (audioMd5, chromaprint), so a re-extract triggered by the cache
                        // canary doesn't silently wipe them on entries seeded by a prior
                        // --fingerprint run. The audio is unchanged from those tools' POV;
                        // only Essentia's features need refresh.
                        allTracks.TryGetValue(t.Location, out var priorEntry);
                        allTracks[t.Location] = new TrackEntry
                        {
                            Features = feat,
                            LastModified = lastMod,
                            AnalysisDurationSecs = analyzeDuration.TotalSeconds,
                            FileMd5 = fileMd5,
                            AudioMd5 = priorEntry?.AudioMd5,
                            AudioStreamSha256 = string.IsNullOrEmpty(audioStreamSha256) ? null : audioStreamSha256,
                            AudioStreamSha256Source = audioStreamSha256Source,
                            FingerprintV1 = fingerprintV1,
                            Chromaprint = priorEntry?.Chromaprint,
                            ChromaprintDuration = priorEntry?.ChromaprintDuration
                        };
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
                });
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
            if (crossPathMoods > 0)
                Console.WriteLine($"  Cross-MD5:  {crossPathMoods}  (of {cachedCount} cached)");
            if (cachedByShaPath > 0 || cachedByShaCross > 0)
                Console.WriteLine($"  Cross-SHA:  {cachedByShaPath + cachedByShaCross}  (of {cachedCount} cached: {cachedByShaPath} same-path tag-edits, {cachedByShaCross} cross-path)");
            Console.WriteLine($"  Analyzed:   {analyzed}");
            Console.WriteLine($"  Skipped:    {skipped}  (errors from previous run)");
            Console.WriteLine($"  Failed:     {failed}{(timedOut > 0 ? $"  ({timedOut} timed out)" : "")}");
            Console.WriteLine($"  --------    -----");
            Console.WriteLine($"  Processed:  {cachedCount + analyzed + skipped + failed}");
            Console.WriteLine($"  Output:     {allTracks.Count} tracks in moods file");
            if (analyzed > 0)
            {
                var avgAnalyze = StopwatchTicksToTimeSpan(_analyzeTicksTotal / analyzed);
                Console.WriteLine($"  Avg/track:  {avgAnalyze.TotalSeconds:F1}s (analysis only)");
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
            tracks = FilterPodcasts(tracks);
            tracks = FilterVideoFiles(tracks);
            tracks = FilterNonAudio(tracks);
            Console.WriteLine();

            var errors = new List<(ITunesTrack Track, List<char> Chars)>();
            var warnings = new List<(ITunesTrack Track, List<char> Chars, bool Has83)>();
            var zeroByteFiles = new List<ITunesTrack>();
            var smallFiles = new List<(ITunesTrack Track, long Bytes)>();
            var unsupportedFiles = new List<(ITunesTrack Track, string Ext)>();
            const long SmallFileThreshold = 50 * 1024; // 50 KB

            foreach (var t in tracks)
            {
                var pathToScan = t.Location;
                if (string.IsNullOrEmpty(pathToScan)) continue;

                // Unsupported format check is independent of the other categories —
                // a .dsf with a non-ASCII path AND zero bytes shows up in three buckets.
                var ext = Path.GetExtension(pathToScan);
                if (!string.IsNullOrEmpty(ext) && UnsupportedExtensions.Contains(ext))
                    unsupportedFiles.Add((t, ext));

                List<char>? errorList = null;
                List<char>? warnList = null;

                // Scan the full path (not just the filename) — non-ASCII chars in
                // any directory component trigger the same legacy-mode (RunTool)
                // path-escape failure as non-ASCII in the filename. ASCII chars
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

            // Summary
            Console.WriteLine("=== Summary ===");
            Console.WriteLine($"  Total tracks:     {tracks.Count}");
            Console.WriteLine($"  Errors:           {errors.Count}  (will break - rename these)");
            Console.WriteLine($"  Warnings (no 8.3):{warnsNo83.Count}  (may break - check these)");
            Console.WriteLine($"  Warnings (8.3 ok):{warnsOk.Count}  (safe - 8.3 short path available)");
            Console.WriteLine($"  Unsupported:      {unsupportedFiles.Count}  (DSD/DSF — Essentia cannot decode)");
            Console.WriteLine($"  Zero-byte files:  {zeroByteFiles.Count}  (length == 0)");
            Console.WriteLine($"  Suspect files:    {smallFiles.Count}  (over 0 and under {SmallFileThreshold / 1024} KB)");
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

        static void RunDuplicates(string outputDir)
        {
            Console.WriteLine("=== Duplicate Detection ===");
            Console.WriteLine();

            var fpPath = Path.Combine(outputDir, "mbxhub-fingerprints.json");
            if (!File.Exists(fpPath))
            {
                Console.WriteLine($"Fingerprints file not found: {fpPath}");
                Console.WriteLine("Run --fingerprint first to generate file hashes.");
                return;
            }

            Console.WriteLine($"Loading: {fpPath}");
            var docOptions = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            using var fs = new FileStream(fpPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            using var doc = JsonDocument.Parse(fs, docOptions);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Object)
            {
                Console.WriteLine("No tracks found in fingerprints file.");
                return;
            }

            // Group by fileMd5
            var hashGroups = new Dictionary<string, List<(string Path, int TrackId, string Artist, string Title, string Album)>>(StringComparer.Ordinal);
            int total = 0, withHash = 0, noHash = 0;

            foreach (var prop in tracks.EnumerateObject())
            {
                total++;
                var filePath = PathHelper.NormalizeSeparators(prop.Name);
                var track = prop.Value;
                var fileMd5 = GetStr(track, "fileMd5");
                if (string.IsNullOrEmpty(fileMd5)) { noHash++; continue; }
                withHash++;

                var entry = (
                    Path: filePath,
                    TrackId: GetInt(track, "trackId"),
                    Artist: GetStr(track, "artist"),
                    Title: GetStr(track, "title"),
                    Album: GetStr(track, "album")
                );

                if (!hashGroups.TryGetValue(fileMd5, out var list))
                {
                    list = new List<(string, int, string, string, string)>();
                    hashGroups[fileMd5] = list;
                }
                list.Add(entry);
            }

            Console.WriteLine($"  Tracks: {total}  (with fileMd5: {withHash}, missing: {noHash})");
            Console.WriteLine();

            var duplicates = hashGroups
                .Where(kv => kv.Value.Count > 1)
                .OrderByDescending(kv => kv.Value.Count)
                .ToList();
            int dupFileCount = duplicates.Sum(kv => kv.Value.Count);

            if (duplicates.Count > 0)
            {
                Console.WriteLine($"DUPLICATES: {duplicates.Count} set(s), {dupFileCount} files with identical content:");
                Console.WriteLine();
                foreach (var kv in duplicates)
                {
                    Console.WriteLine($"  MD5 {kv.Key}  ({kv.Value.Count} copies):");
                    foreach (var f in kv.Value)
                        Console.WriteLine($"    {f.Artist} - {f.Title}  |  {f.Path}");
                    Console.WriteLine();
                }
            }
            else
            {
                Console.WriteLine("No duplicates found.");
                Console.WriteLine();
            }

            Console.WriteLine("=== Summary ===");
            Console.WriteLine($"  Total tracks:     {total}");
            Console.WriteLine($"  With fileMd5:     {withHash}");
            Console.WriteLine($"  Missing fileMd5:  {noHash}");
            Console.WriteLine($"  Duplicate sets:   {duplicates.Count}");
            Console.WriteLine($"  Duplicate files:  {dupFileCount}");
            Console.WriteLine($"  Unique:           {hashGroups.Count(kv => kv.Value.Count == 1)}");

            // Write JSON report
            var reportPath = Path.Combine(outputDir, "mbxhub-duplicates.json");
            var tmpPath = reportPath + ".tmp";
            try { File.Delete(tmpPath); } catch { }

            using (var ofs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            using (var jw = new Utf8JsonWriter(ofs, new JsonWriterOptions { Indented = true }))
            {
                jw.WriteStartObject();
                jw.WriteString("version", "1.0");
                jw.WriteString("generatedAt", DateTime.UtcNow.ToString("o"));

                jw.WriteStartObject("summary");
                jw.WriteNumber("totalTracks", total);
                jw.WriteNumber("withFileMd5", withHash);
                jw.WriteNumber("missingFileMd5", noHash);
                jw.WriteNumber("duplicateSets", duplicates.Count);
                jw.WriteNumber("duplicateFiles", dupFileCount);
                jw.WriteNumber("unique", hashGroups.Count(kv => kv.Value.Count == 1));
                jw.WriteEndObject();

                if (duplicates.Count > 0)
                {
                    jw.WriteStartArray("duplicates");
                    foreach (var kv in duplicates)
                    {
                        jw.WriteStartObject();
                        jw.WriteString("md5", kv.Key);
                        jw.WriteNumber("count", kv.Value.Count);
                        jw.WriteStartArray("files");
                        foreach (var f in kv.Value)
                        {
                            jw.WriteStartObject();
                            jw.WriteNumber("trackId", f.TrackId);
                            jw.WriteString("artist", f.Artist);
                            jw.WriteString("title", f.Title);
                            jw.WriteString("album", f.Album);
                            jw.WriteString("path", f.Path);
                            jw.WriteEndObject();
                        }
                        jw.WriteEndArray();
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

            int unchanged = 0, remapped = 0, orphaned = 0;
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
                ITunesTrack? match = null;

                if (!string.IsNullOrEmpty(filename) && byFilename.TryGetValue(filename, out var candidates))
                {
                    var strictMatches = candidates.Where(c =>
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

            if (remapped > 0 || orphaned > 0)
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
        /// Read-only diagnostic. Walk a moods file and, for each entry, classify:
        ///   OK       — file present, audioStreamSha256 recomputes to the cached value
        ///   DRIFT    — file present, audioStreamSha256 differs (audio bytes changed)
        ///   MISSING  — path doesn't exist on disk
        ///   NO_HASH  — entry has no audioStreamSha256 to verify against (older cache)
        ///   ERROR    — TagLib parse / IO failure
        /// Prints summary; writes mbxmoods-verify.csv next to the moods file with
        /// per-entry detail. No writes to the moods file itself.
        /// </summary>
        static int RunVerify(string moodsPath, int parallelism)
        {
            Console.WriteLine("=== Verify Mode ===");
            Console.WriteLine($"Moods file: {moodsPath}");
            Console.WriteLine();

            var allTracks = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
            int loaded = LoadExistingMoods(moodsPath, allTracks);
            Console.WriteLine($"Loaded {loaded} entries");
            if (loaded == 0) return 0;

            int ok = 0, drift = 0, missing = 0, noHash = 0, errored = 0;
            var details = new ConcurrentBag<string>();
            var sw = Stopwatch.StartNew();
            int done = 0;

            Parallel.ForEach(allTracks, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, kvp =>
            {
                var path = kvp.Key;
                var entry = kvp.Value;
                string status;
                string detail = "";

                if (!File.Exists(path))
                {
                    status = "MISSING";
                    Interlocked.Increment(ref missing);
                }
                else if (string.IsNullOrEmpty(entry.AudioStreamSha256))
                {
                    status = "NO_HASH";
                    Interlocked.Increment(ref noHash);
                }
                else
                {
                    try
                    {
                        var fileSize = new FileInfo(path).Length;
                        var (recomputed, _) = ComputeAudioStreamSha256FromFile(path, fileSize, out var err);
                        if (string.IsNullOrEmpty(recomputed))
                        {
                            status = "ERROR";
                            detail = err ?? "hash compute failed";
                            Interlocked.Increment(ref errored);
                        }
                        else
                        {
                            var cached = entry.AudioStreamSha256!;
                            if (string.Equals(recomputed, cached, StringComparison.OrdinalIgnoreCase))
                            {
                                status = "OK";
                                Interlocked.Increment(ref ok);
                            }
                            else
                            {
                                status = "DRIFT";
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
                    details.Add($"{status}\t{path}\t{detail}");

                var n = Interlocked.Increment(ref done);
                if (n % 250 == 0)
                {
                    var pct = (n * 100) / loaded;
                    Console.WriteLine($"[{n}/{loaded} {pct}%{FormatEta(sw.Elapsed, n, loaded)}] verifying...");
                }
            });

            sw.Stop();

            var csvPath = Path.Combine(
                Path.GetDirectoryName(moodsPath) ?? ".",
                "mbxmoods-verify.csv");
            try
            {
                var lines = new List<string> { "status\tpath\tdetail" };
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
            Console.WriteLine($"  OK:        {ok}");
            Console.WriteLine($"  Drift:     {drift}");
            Console.WriteLine($"  Missing:   {missing}");
            Console.WriteLine($"  No hash:   {noHash}");
            Console.WriteLine($"  Errored:   {errored}");
            Console.WriteLine($"  Elapsed:   {FormatTimeSpan(sw.Elapsed)}");
            Console.WriteLine($"  Detail:    {csvPath}");
            return (drift > 0 || missing > 0 || errored > 0) ? 1 : 0;
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

        static void RunMigrate(string moodsPath)
        {
            Console.WriteLine("=== Migrate Mode ===");
            Console.WriteLine("Cleans up mbxmoods.json: strips legacy fields, removes podcast entries");
            Console.WriteLine();

            if (!File.Exists(moodsPath)) { Console.WriteLine($"No moods file found: {moodsPath}"); return; }

            Console.WriteLine($"Loading: {moodsPath}");
            var json = File.ReadAllText(moodsPath);
            var docOptions = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            var root = JsonNode.Parse(json, null, docOptions)?.AsObject();
            if (root == null) { Console.WriteLine("Invalid JSON in moods file."); return; }
            var tracks = root["tracks"]?.AsObject();
            if (tracks == null || tracks.Count == 0) { Console.WriteLine("No tracks in moods file."); return; }

            int stripped = 0, total = tracks.Count;
            foreach (var kv in tracks)
            {
                var trackData = kv.Value?.AsObject();
                if (trackData == null) continue;
                bool changed = false;
                if (trackData.Remove("valence")) changed = true;
                if (trackData.Remove("arousal")) changed = true;
                if (changed) stripped++;
            }

            // Remove podcast entries (identified by genre)
            var podcastKeys = tracks
                .Where(kv => string.Equals(kv.Value?["genre"]?.GetValue<string>(), "Podcast", StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in podcastKeys) tracks.Remove(key);

            Console.WriteLine($"Tracks: {total}");
            if (stripped > 0)
                Console.WriteLine($"Stripped valence/arousal from: {stripped}");
            if (podcastKeys.Count > 0)
                Console.WriteLine($"Removed podcast entries: {podcastKeys.Count}");

            if (stripped == 0 && podcastKeys.Count == 0)
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

        // -- Fingerprint mode ------------------------------------------------

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

        static void RunQuickFingerprint(string xmlPath, string outputDir, int parallelism, bool retryErrors)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var cwd = Environment.CurrentDirectory;
            var fpcalcExe = FindTool("fpcalc.exe", baseDir, outputDir, cwd);
            if (fpcalcExe == null)
            {
                Console.WriteLine("fpcalc not found. --quick-fingerprint requires fpcalc.exe next to truedat.exe or in the library directory.");
                return;
            }

            Console.WriteLine("=== Quick Fingerprint Mode (fpcalc, 30s) ===");
            Console.WriteLine();
            Console.WriteLine($"  fpcalc: {fpcalcExe}");
            Console.WriteLine();

            var quickFpPath = Path.Combine(outputDir, "mbxhub-quickfp.json");
            var errorsPath = Path.Combine(outputDir, "mbxhub-quickfp-errors.csv");

            Console.WriteLine($"Loading iTunes library: {xmlPath}");
            var tracks = ITunesParser.Parse(xmlPath, out var xmlIssues);
            if (_audit && xmlIssues != null)
                foreach (var issue in xmlIssues) Console.WriteLine(issue);
            Console.WriteLine($"Found {tracks.Count} tracks");
            tracks = FilterPodcasts(tracks);
            tracks = FilterVideoFiles(tracks);
            tracks = FilterNonAudio(tracks);

            var allFp = new ConcurrentDictionary<string, FingerprintEntry>(PathComparer.Instance);
            int existingCount = LoadExistingFingerprints(quickFpPath, allFp);
            Console.WriteLine($"Existing quick fingerprints: {existingCount}");

            Dictionary<string, string> existingErrors;
            if (!retryErrors)
            {
                existingErrors = LoadExistingErrors(errorsPath);
            }
            else
            {
                existingErrors = new Dictionary<string, string>(PathComparer.Instance);
                if (File.Exists(errorsPath)) { File.Delete(errorsPath); Console.WriteLine("Errors CSV cleared (--retry-errors)"); }
            }
            if (existingErrors.Count > 0)
                Console.WriteLine($"Previous errors: {existingErrors.Count} (use --retry-errors to re-attempt)");
            Console.WriteLine();

            var saveLock = new object();
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

            int total = tracks.Count;
            int processed = 0, fingerprinted = 0, cachedCount = 0, skipped = 0, failed = 0;
            var sw = Stopwatch.StartNew();

            WarnLowDiskSpace(outputDir);
            Console.WriteLine($"Started:     {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"Parallelism: {parallelism} threads");
            Console.WriteLine();
            try
            {
                Parallel.ForEach(tracks, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cts.Token }, t =>
                {
                    if (cts.IsCancellationRequested) return;
                    var current = Interlocked.Increment(ref processed);

                    try
                    {
                        var pct = (current * 100) / total;
                        var eta = FormatEta(sw.Elapsed, current, total);

                        if (existingErrors.TryGetValue(t.Location, out var prevError))
                        {
                            Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name} (skip: {prevError})");
                            Interlocked.Increment(ref skipped);
                            return;
                        }

                        // Cache check
                        if (allFp.TryGetValue(t.Location, out var existing))
                        {
                            try
                            {
                                var fileInfo = new FileInfo(t.Location);
                                if (fileInfo.Exists && Math.Abs((fileInfo.LastWriteTimeUtc - existing.LastModified).TotalSeconds) < 2
                                    && !string.IsNullOrEmpty(existing.Fp.Chromaprint))
                                {
                                    Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name} (cached)");
                                    Interlocked.Increment(ref cachedCount);
                                    return;
                                }
                            }
                            catch { }
                        }

                        if (!File.Exists(t.Location))
                        {
                            Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name} (file not found)");
                            Interlocked.Increment(ref skipped);
                            return;
                        }

                        var timer = Stopwatch.StartNew();
                        var (fingerprint, duration, error) = RunFpcalc(fpcalcExe, t.Location, cts.Token);
                        timer.Stop();

                        if (error != null)
                        {
                            Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name} FAILED: {error}");
                            AppendError(errorsPath, t.Location, t.Artist, t.Name, error, 0, 0, saveLock);
                            Interlocked.Increment(ref failed);
                            return;
                        }

                        var lastMod = DateTime.MinValue;
                        try { lastMod = new FileInfo(t.Location).LastWriteTimeUtc; } catch { }

                        allFp[t.Location] = new FingerprintEntry
                        {
                            LastModified = lastMod,
                            Fp = new TrackFingerprint
                            {
                                TrackId = t.TrackId,
                                Artist = t.Artist ?? "",
                                Title = t.Name ?? "",
                                Album = t.Album ?? "",
                                Genre = t.Genre ?? "",
                                FilePath = t.Location,
                                Chromaprint = fingerprint,
                                Duration = duration
                            }
                        };

                        Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name} ({timer.Elapsed.TotalSeconds:F1}s)");
                        var count = Interlocked.Increment(ref fingerprinted);

                        if (count % 200 == 0)
                            SaveFingerprints(quickFpPath, allFp);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{current}/{total}] {t.Artist} - {t.Name} ERROR: {ex.Message}");
                        Interlocked.Increment(ref failed);
                    }
                });
            }
            catch (OperationCanceledException) { Console.WriteLine("Cancelled."); }

            SaveFingerprints(quickFpPath, allFp);
            sw.Stop();

            Console.WriteLine();
            Console.WriteLine($"=== Quick Fingerprint Complete ===");
            Console.WriteLine($"  Total:        {total}");
            Console.WriteLine($"  Cached:       {cachedCount}");
            Console.WriteLine($"  Fingerprinted:{fingerprinted}");
            Console.WriteLine($"  Skipped:      {skipped}");
            Console.WriteLine($"  Failed:       {failed}");
            Console.WriteLine($"  Elapsed:      {sw.Elapsed:hh\\:mm\\:ss}");
            Console.WriteLine($"  Output:       {quickFpPath}");
            if (fingerprinted > 0)
                Console.WriteLine($"  Avg:          {sw.Elapsed.TotalSeconds / fingerprinted:F1}s per track");
        }

        static void RunFingerprint(string xmlPath, string outputDir, int parallelism, bool retryErrors, bool chromaprintOnly, bool md5Only, bool detailsMode)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var cwd = Environment.CurrentDirectory;
            var chromaprintExe = FindTool("essentia_standard_chromaprinter.exe", baseDir, outputDir, cwd);
            var md5Exe = FindTool("essentia_streaming_md5.exe", baseDir, outputDir, cwd);

            bool runChromaprint = !md5Only;
            bool runMd5 = !chromaprintOnly;

            Console.WriteLine("=== Tool Check ===");
            Console.WriteLine($"  App dir:      {baseDir}");
            Console.WriteLine($"  Library dir:  {outputDir}");
            Console.WriteLine($"  Chromaprint:  {chromaprintExe ?? "NOT FOUND"}");
            Console.WriteLine($"  MD5:          {md5Exe ?? "NOT FOUND"}");
            Console.WriteLine($"  ffmpeg:       {_ffmpegPath.Value ?? "not found (multi-channel files will be skipped)"}");
            if (detailsMode) Console.WriteLine($"  ffprobe:      {_ffprobePath.Value ?? "not found (--details will be skipped)"}");
            var fpcalcExe = FindTool("fpcalc.exe", baseDir, outputDir, cwd);
            Console.WriteLine($"  fpcalc:       {fpcalcExe ?? "not found (--quick-fingerprint unavailable)"}");
            Console.WriteLine();

            if (runChromaprint && chromaprintExe == null)
            {
                Console.WriteLine("Chromaprinter not found in any search directory.");
                if (!runMd5) { Console.WriteLine("No fingerprint tools available."); return; }
                Console.WriteLine("Falling back to MD5 only.");
                runChromaprint = false;
            }
            if (runMd5 && md5Exe == null)
            {
                Console.WriteLine("MD5 tool not found in any search directory.");
                if (!runChromaprint) { Console.WriteLine("No fingerprint tools available."); return; }
                Console.WriteLine("Falling back to chromaprint only.");
                runMd5 = false;
            }

            Console.WriteLine("=== Fingerprint Mode ===");
            Console.WriteLine();
            if (runChromaprint) Console.WriteLine($"  Chromaprint: {chromaprintExe}");
            if (runMd5) Console.WriteLine($"  MD5:         {md5Exe}");
            Console.WriteLine();

            string? ffprobePath = null;
            var detailsPath = "";
            if (detailsMode)
            {
                ffprobePath = _ffprobePath.Value;
                if (ffprobePath == null)
                {
                    Console.WriteLine("WARNING: ffprobe not found - --details will be skipped (fingerprinting continues)");
                    Console.WriteLine();
                    detailsMode = false;
                }
                else
                {
                    detailsPath = Path.Combine(outputDir, "mbxhub-details.json");
                }
            }

            var fingerprintsPath = Path.Combine(outputDir, "mbxhub-fingerprints.json");
            var errorsPath = Path.Combine(outputDir, "mbxhub-fingerprints-errors.csv");

            Console.WriteLine($"Loading iTunes library: {xmlPath}");
            var tracks = ITunesParser.Parse(xmlPath, out var fpXmlIssues);
            if (_audit && fpXmlIssues != null)
                foreach (var issue in fpXmlIssues) Console.WriteLine(issue);
            Console.WriteLine($"Found {tracks.Count} tracks");
            tracks = FilterPodcasts(tracks);
            tracks = FilterVideoFiles(tracks);
            tracks = FilterNonAudio(tracks);

            var allFp = new ConcurrentDictionary<string, FingerprintEntry>(PathComparer.Instance);
            int existingCount = LoadExistingFingerprints(fingerprintsPath, allFp);
            Console.WriteLine($"Existing fingerprints: {existingCount}");

            var allDetails = new ConcurrentDictionary<string, AudioDetails>(PathComparer.Instance);
            if (detailsMode)
            {
                int existingDetails = LoadExistingDetails(detailsPath, allDetails);
                Console.WriteLine($"Existing details: {existingDetails}");
            }

            var fpMd5Index = BuildMd5Index(allFp, e => e.FileMd5);
            if (fpMd5Index != null)
                Console.WriteLine($"  File MD5 index:  {fpMd5Index.Count} entries available for cross-machine matching");
            var audioMd5Index = BuildMd5Index(allFp, e => string.IsNullOrEmpty(e.Fp.Md5) ? null : e.Fp.Md5);
            if (audioMd5Index != null)
                Console.WriteLine($"  Audio MD5 index: {audioMd5Index.Count} entries (from fingerprint data)");
            var detMd5Index = detailsMode ? BuildMd5Index(allDetails, e => e.FileMd5) : null;
            if (detMd5Index != null)
                Console.WriteLine($"  Details MD5 index: {detMd5Index.Count} entries");
            int crossPathFp = 0;

            Dictionary<string, string> existingErrors;
            if (retryErrors)
            {
                existingErrors = new Dictionary<string, string>(PathComparer.Instance);
                if (File.Exists(errorsPath)) { File.Delete(errorsPath); Console.WriteLine("Errors CSV cleared (--retry-errors)"); }
            }
            else
            {
                existingErrors = LoadExistingErrors(errorsPath);
            }
            Console.WriteLine($"Existing errors: {existingErrors.Count}");

            int cachedCount = 0, fingerprinted = 0, skipped = 0, failed = 0, probed = 0;
            int processed = 0, total = tracks.Count;
            int lastSaveCount = 0, lastProbeSaveCount = 0;
            const int SaveInterval = 200;
            var saveLock = new object();
            long fpTicksTotal = 0;

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
                Parallel.ForEach(tracks, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cts.Token }, t =>
                {
                    if (cts.IsCancellationRequested) return;
                    var current = Interlocked.Increment(ref processed);
                    try
                    {
                        var pct = (current * 100) / total;
                        var eta = FormatEta(sw.Elapsed, current, total);

                        if (existingErrors.TryGetValue(t.Location, out var prevError))
                        {
                            Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name} (skip: {prevError})");
                            Interlocked.Increment(ref skipped);
                            return;
                        }

                        // Check cache — atomic entry replacement for thread safety
                        if (allFp.TryGetValue(t.Location, out var existing))
                        {
                            try
                            {
                                var currentLastMod = File.GetLastWriteTimeUtc(t.Location);
                                if (TruncateToSeconds(currentLastMod) == TruncateToSeconds(existing.LastModified))
                                {
                                    bool hasChromaprint = !string.IsNullOrEmpty(existing.Fp.Chromaprint);
                                    bool hasMd5 = !string.IsNullOrEmpty(existing.Fp.Md5);
                                    if ((!runChromaprint || hasChromaprint) && (!runMd5 || hasMd5))
                                    {
                                        allFp[t.Location] = new FingerprintEntry
                                        {
                                            LastModified = currentLastMod,
                                            Fp = new TrackFingerprint
                                            {
                                                TrackId = t.TrackId, Artist = t.Artist, Title = t.Name,
                                                Album = t.Album, Genre = t.Genre, FilePath = t.Location,
                                                Chromaprint = existing.Fp.Chromaprint, Duration = existing.Fp.Duration,
                                                Md5 = existing.Fp.Md5
                                            },
                                            FileMd5 = existing.FileMd5 ?? ComputeFileMd5(t.Location)
                                        };
                                        Interlocked.Increment(ref cachedCount);
                                        Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name} (cached)");
                                        if (detailsMode)
                                        {
                                            if (allDetails.TryGetValue(t.Location, out var existingDet) &&
                                                TruncateToSeconds(currentLastMod) == TruncateToSeconds(existingDet.LastModified))
                                            {
                                                existingDet.TrackId = t.TrackId;
                                                existingDet.Artist = t.Artist;
                                                existingDet.Title = t.Name;
                                                existingDet.FileMd5 ??= existing.FileMd5 ?? ComputeFileMd5(t.Location);
                                            }
                                            else
                                            {
                                                var det = ProbeAudio(ffprobePath!, t.Location);
                                                if (det != null)
                                                {
                                                    det.TrackId = t.TrackId;
                                                    det.Artist = t.Artist;
                                                    det.Title = t.Name;
                                                    det.LastModified = currentLastMod;
                                                    det.FileMd5 = existing.FileMd5 ?? ComputeFileMd5(t.Location);
                                                    allDetails[t.Location] = det;
                                                    var np = Interlocked.Increment(ref probed);
                                                    if (np - Volatile.Read(ref lastProbeSaveCount) >= SaveInterval)
                                                    {
                                                        lock (saveLock)
                                                        {
                                                            if (probed - lastProbeSaveCount >= SaveInterval)
                                                            {
                                                                lastProbeSaveCount = probed;
                                                                SaveDetails(detailsPath, allDetails);
                                                                Console.WriteLine($"  [Saved {allDetails.Count} details]");
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        return;
                                    }
                                    if (_audit)
                                    {
                                        var missing = new List<string>();
                                        if (runChromaprint && !hasChromaprint) missing.Add("chromaprint");
                                        if (runMd5 && !hasMd5) missing.Add("md5");
                                        Console.WriteLine($"  DEBUG cache: incomplete ({string.Join("+", missing)} missing)");
                                    }
                                }
                                else if (_audit)
                                {
                                    Console.WriteLine($"  DEBUG cache: stale (file:{currentLastMod:o} != cached:{existing.LastModified:o})");
                                }
                            }
                            catch (Exception ex) { if (_audit) Console.WriteLine($"  DEBUG cache: lastmod error: {ex.Message}"); }
                        }

                        // Cross-machine MD5 fallback — same file at a different path
                        if (fpMd5Index != null)
                        {
                            var localMd5 = ComputeFileMd5(t.Location);
                            if (localMd5 != null && fpMd5Index.TryGetValue(localMd5, out var xp))
                            {
                                var xFp = xp.Entry.Fp;
                                bool hasChromaprint = !string.IsNullOrEmpty(xFp.Chromaprint);
                                bool hasMd5 = !string.IsNullOrEmpty(xFp.Md5);
                                if ((!runChromaprint || hasChromaprint) && (!runMd5 || hasMd5))
                                {
                                    var currentLastMod = DateTime.MinValue;
                                    try { currentLastMod = File.GetLastWriteTimeUtc(t.Location); } catch { }
                                    allFp[t.Location] = new FingerprintEntry
                                    {
                                        LastModified = currentLastMod,
                                        Fp = new TrackFingerprint
                                        {
                                            TrackId = t.TrackId, Artist = t.Artist, Title = t.Name,
                                            Album = t.Album, Genre = t.Genre, FilePath = t.Location,
                                            Chromaprint = xFp.Chromaprint, Duration = xFp.Duration,
                                            Md5 = xFp.Md5
                                        },
                                        FileMd5 = localMd5
                                    };
                                    allFp.TryRemove(xp.OldKey, out _);
                                    Interlocked.Increment(ref crossPathFp);
                                    Interlocked.Increment(ref cachedCount);
                                    Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name} (cached\u00b7md5)");

                                    if (detailsMode)
                                    {
                                        if (detMd5Index != null && detMd5Index.TryGetValue(localMd5, out var xd))
                                        {
                                            var d = xd.Entry;
                                            allDetails[t.Location] = new AudioDetails
                                            {
                                                TrackId = t.TrackId, Artist = t.Artist, Title = d.Title,
                                                Codec = d.Codec, Format = d.Format, Channels = d.Channels,
                                                SampleRate = d.SampleRate, BitRate = d.BitRate, BitDepth = d.BitDepth,
                                                Duration = d.Duration, SizeMb = d.SizeMb,
                                                LastProbed = d.LastProbed, LastModified = currentLastMod,
                                                FileMd5 = localMd5
                                            };
                                            allDetails.TryRemove(xd.OldKey, out _);
                                        }
                                        else
                                        {
                                            var det = ProbeAudio(ffprobePath!, t.Location);
                                            if (det != null)
                                            {
                                                det.TrackId = t.TrackId;
                                                det.Artist = t.Artist;
                                                det.Title = t.Name;
                                                det.LastModified = currentLastMod;
                                                det.FileMd5 = localMd5;
                                                allDetails[t.Location] = det;
                                                Interlocked.Increment(ref probed);
                                            }
                                        }
                                    }
                                    return;
                                }
                            }
                        }

                        // Audio MD5 cross-machine fallback — match via existing fingerprint audio hash
                        string? earlyAudioMd5 = null;
                        bool earlyAudioMd5Attempted = false;
                        if (runMd5 && audioMd5Index != null)
                        {
                            var (md5Result, md5Err) = RunMd5(md5Exe!, t.Location, cts.Token);
                            earlyAudioMd5Attempted = true;
                            if (md5Err == null)
                            {
                                earlyAudioMd5 = md5Result;
                                if (audioMd5Index.TryGetValue(md5Result, out var xp))
                                {
                                    var xFp = xp.Entry.Fp;
                                    bool hasChromaprint = !string.IsNullOrEmpty(xFp.Chromaprint);
                                    if (!runChromaprint || hasChromaprint)
                                    {
                                        var currentLastMod = DateTime.MinValue;
                                        try { currentLastMod = File.GetLastWriteTimeUtc(t.Location); } catch { }
                                        var localFileMd5 = ComputeFileMd5(t.Location);
                                        allFp[t.Location] = new FingerprintEntry
                                        {
                                            LastModified = currentLastMod,
                                            Fp = new TrackFingerprint
                                            {
                                                TrackId = t.TrackId, Artist = t.Artist, Title = t.Name,
                                                Album = t.Album, Genre = t.Genre, FilePath = t.Location,
                                                Chromaprint = xFp.Chromaprint, Duration = xFp.Duration,
                                                Md5 = md5Result
                                            },
                                            FileMd5 = localFileMd5
                                        };
                                        allFp.TryRemove(xp.OldKey, out _);
                                        Interlocked.Increment(ref crossPathFp);
                                        Interlocked.Increment(ref cachedCount);
                                        Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name} (cached\u00b7fp)");

                                        if (detailsMode)
                                        {
                                            if (detMd5Index != null && localFileMd5 != null && detMd5Index.TryGetValue(localFileMd5, out var xd))
                                            {
                                                var d = xd.Entry;
                                                allDetails[t.Location] = new AudioDetails
                                                {
                                                    TrackId = t.TrackId, Artist = t.Artist, Title = d.Title,
                                                    Codec = d.Codec, Format = d.Format, Channels = d.Channels,
                                                    SampleRate = d.SampleRate, BitRate = d.BitRate, BitDepth = d.BitDepth,
                                                    Duration = d.Duration, SizeMb = d.SizeMb,
                                                    LastProbed = d.LastProbed, LastModified = currentLastMod,
                                                    FileMd5 = localFileMd5
                                                };
                                                allDetails.TryRemove(xd.OldKey, out _);
                                            }
                                            else
                                            {
                                                var det = ProbeAudio(ffprobePath!, t.Location);
                                                if (det != null)
                                                {
                                                    det.TrackId = t.TrackId;
                                                    det.Artist = t.Artist;
                                                    det.Title = t.Name;
                                                    det.LastModified = currentLastMod;
                                                    det.FileMd5 = localFileMd5;
                                                    allDetails[t.Location] = det;
                                                    Interlocked.Increment(ref probed);
                                                }
                                            }
                                        }
                                        return;
                                    }
                                }
                            }
                        }

                        long fileSizeBytes = 0;
                        try { fileSizeBytes = new FileInfo(t.Location).Length; }
                        catch
                        {
                            Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name} (file not found)");
                            AppendError(errorsPath, t.Location, t.Artist, t.Name, "File not found", 0, 0, saveLock);
                            Interlocked.Increment(ref failed);
                            return;
                        }

                        var sizeMb = fileSizeBytes / (1024.0 * 1024.0);
                        Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name}");

                        var fpStart = Stopwatch.GetTimestamp();
                        var fp = new TrackFingerprint
                        {
                            TrackId = t.TrackId, Artist = t.Artist, Title = t.Name,
                            Album = t.Album, Genre = t.Genre, FilePath = t.Location
                        };

                        // Preserve existing data when running subset mode
                        if (allFp.TryGetValue(t.Location, out var prev))
                        {
                            if (!runChromaprint && !string.IsNullOrEmpty(prev.Fp.Chromaprint))
                            {
                                fp.Chromaprint = prev.Fp.Chromaprint;
                                fp.Duration = prev.Fp.Duration;
                            }
                            if (!runMd5 && !string.IsNullOrEmpty(prev.Fp.Md5))
                                fp.Md5 = prev.Fp.Md5;
                        }

                        string? errorMsg = null;

                        if (runChromaprint)
                        {
                            var (chromaprint, duration, chromaErr) = RunChromaprinter(chromaprintExe!, t.Location, cts.Token);
                            if (chromaErr != null) errorMsg = $"chromaprint: {chromaErr}";
                            else { fp.Chromaprint = chromaprint; fp.Duration = duration; }
                        }

                        if (runMd5 && errorMsg == null)
                        {
                            if (earlyAudioMd5 != null)
                            {
                                fp.Md5 = earlyAudioMd5;
                            }
                            else if (!earlyAudioMd5Attempted)
                            {
                                var (md5, md5Err) = RunMd5(md5Exe!, t.Location, cts.Token);
                                if (md5Err != null) errorMsg = $"md5: {md5Err}";
                                else fp.Md5 = md5;
                            }
                            else
                            {
                                errorMsg = "md5: failed in earlier cross-machine check";
                            }
                        }

                        var fpTicks = Stopwatch.GetTimestamp() - fpStart;
                        Interlocked.Add(ref fpTicksTotal, fpTicks);

                        if (errorMsg != null)
                        {
                            var fpDuration = StopwatchTicksToTimeSpan(fpTicks).TotalSeconds;
                            AppendError(errorsPath, t.Location, t.Artist, t.Name, errorMsg, sizeMb, fpDuration, saveLock);
                            Console.WriteLine($"  FAILED: {errorMsg}");
                            Interlocked.Increment(ref failed);
                            return;
                        }

                        var lastMod = DateTime.MinValue;
                        try { lastMod = File.GetLastWriteTimeUtc(t.Location); } catch { }
                        var fileMd5 = ComputeFileMd5(t.Location);
                        allFp[t.Location] = new FingerprintEntry { Fp = fp, LastModified = lastMod, FileMd5 = fileMd5 };
                        if (detailsMode)
                        {
                            var det = ProbeAudio(ffprobePath!, t.Location);
                            if (det != null)
                            {
                                det.TrackId = t.TrackId;
                                det.Artist = t.Artist;
                                det.Title = t.Name;
                                det.LastModified = lastMod;
                                det.FileMd5 = fileMd5;
                                allDetails[t.Location] = det;
                                Interlocked.Increment(ref probed);
                            }
                        }
                        var newCount = Interlocked.Increment(ref fingerprinted);

                        if (newCount - Volatile.Read(ref lastSaveCount) >= SaveInterval)
                        {
                            lock (saveLock)
                            {
                                if (newCount - lastSaveCount >= SaveInterval)
                                {
                                    lastSaveCount = newCount;
                                    var saveSw = Stopwatch.StartNew();
                                    SaveFingerprints(fingerprintsPath, allFp);
                                    if (detailsMode) SaveDetails(detailsPath, allDetails);
                                    saveSw.Stop();
                                    Console.WriteLine($"  [Saved {allFp.Count} fingerprints{(detailsMode ? $" + {allDetails.Count} details" : "")} in {saveSw.Elapsed.TotalSeconds:F1}s]");
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
                });
            }
            catch (OperationCanceledException) { }

            sw.Stop();
            var endTime = DateTime.Now;
            var wasCancelled = Volatile.Read(ref cancelRequested) != 0;

            SaveFingerprints(fingerprintsPath, allFp);
            if (detailsMode) SaveDetails(detailsPath, allDetails);

            Console.WriteLine();
            if (wasCancelled) Console.WriteLine("=== Interrupted (Ctrl+C) - progress saved ===");
            Console.WriteLine($"Started:    {startTime:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"Finished:   {endTime:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"Elapsed:    {FormatTimeSpan(sw.Elapsed)}");
            Console.WriteLine();
            Console.WriteLine($"  Cached:        {cachedCount}");
            if (crossPathFp > 0)
                Console.WriteLine($"  Cross-MD5:     {crossPathFp}  (of {cachedCount} cached)");
            Console.WriteLine($"  Fingerprinted: {fingerprinted}");
            Console.WriteLine($"  Skipped:       {skipped}  (errors from previous run)");
            Console.WriteLine($"  Failed:        {failed}");
            Console.WriteLine($"  ----------     -----");
            Console.WriteLine($"  Processed:     {cachedCount + fingerprinted + skipped + failed}");
            Console.WriteLine($"  Output:        {allFp.Count} tracks in fingerprints file");
            if (detailsMode)
            {
                Console.WriteLine($"  Probed:        {probed}");
                Console.WriteLine($"  Details:       {allDetails.Count} tracks in details file");
            }
            if (fingerprinted > 0)
            {
                var avgFp = StopwatchTicksToTimeSpan(Volatile.Read(ref fpTicksTotal) / fingerprinted);
                Console.WriteLine($"  Avg/track:     {avgFp.TotalSeconds:F1}s");
            }
            try
            {
                using var currentProcess = Process.GetCurrentProcess();
                var peakMb = currentProcess.PeakWorkingSet64 / (1024.0 * 1024.0);
                Console.WriteLine($"  Peak mem:      {peakMb:F0} MB");
            }
            catch { }
            Console.WriteLine();
            Console.WriteLine($"Output: {fingerprintsPath}");
            if (detailsMode) Console.WriteLine($"Output: {detailsPath}");

            if (failed > 0)
                Environment.ExitCode = 1;
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
            string[] candidates = { Path.Combine(root, ".truedat-tmp"), Path.Combine(Path.GetTempPath(), ".truedat-tmp") };

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

        // -- External tool runner (shared by chromaprinter and md5) ----------

        /// <summary>
        /// Run an external tool and capture its output. Uses CPU activity monitoring
        /// instead of arbitrary timeouts: keeps waiting while the process consumes CPU,
        /// only kills after 60s of zero CPU activity (truly stuck).
        /// When SafePath can't produce an ASCII path (8.3 disabled), creates a temporary
        /// hardlink on the same volume so the C++ tool gets a clean ASCII path.
        /// </summary>
        static (string Stdout, string? Error) RunTool(string exe, string audioPath, CancellationToken ct = default)
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

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe, Arguments = PathHelper.QuoteArg(toolPath),
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true
                };

                using var proc = Process.Start(psi)!;
                ApplyCpuLimit(proc);
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();

                // Monitor by CPU activity instead of arbitrary timeout.
                // Poll every 5s. Only kill after 60s of zero CPU (truly stuck).
                const int pollMs = 5000;
                const int maxIdlePolls = 12; // 12 * 5s = 60s with no CPU activity
                var lastCpu = TimeSpan.Zero;
                int idleCount = 0;

                while (!proc.WaitForExit(pollMs))
                {
                    if (ct.IsCancellationRequested)
                    {
                        try { proc.Kill(); proc.WaitForExit(3000); } catch { }
                        return ("", "Cancelled");
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
                                var partialStderr = stderrTask.Wait(3000) ? stderrTask.Result : "(timeout reading stderr)";
                                var partialStdout = stdoutTask.Wait(3000) ? stdoutTask.Result : "(timeout reading stdout)";
                                Console.Error.WriteLine($"  DEBUG watchdog: killed stalled process after {maxIdlePolls * pollMs / 1000}s idle");
                                Console.Error.WriteLine($"    exe:    {Path.GetFileName(exe)}");
                                Console.Error.WriteLine($"    path:   {toolPath}");
                                Console.Error.WriteLine($"    method: {pathMethod}");
                                Console.Error.WriteLine($"    cpu:    {lastCpu.TotalSeconds:F1}s total before stall");
                                Console.Error.WriteLine($"    stdout: {partialStdout.Length} chars");
                                if (partialStderr.Length > 0)
                                    Console.Error.WriteLine($"    stderr: [{partialStderr.Substring(0, Math.Min(300, partialStderr.Length))}]");
                                return ("", $"Process stalled (no CPU activity for {maxIdlePolls * pollMs / 1000}s)");
                            }
                        }
                    }
                    catch { break; } // Process likely exited between WaitForExit and Refresh
                }

                // Flush async stdout/stderr read buffers.
                proc.WaitForExit();

                // Generous timeout on output capture — process already exited, just draining buffers.
                var stdout = stdoutTask.Wait(30000) ? stdoutTask.Result : "";
                var stderr = stderrTask.Wait(30000) ? stderrTask.Result : "";

                if (proc.ExitCode != 0)
                {
                    Console.Error.WriteLine($"  DEBUG RunTool: exit code {proc.ExitCode}, method={pathMethod}, path={toolPath}");
                    if (stderr.Length > 0) Console.Error.WriteLine($"    stderr: [{stderr.Substring(0, Math.Min(300, stderr.Length))}]");
                    var err = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim().Split('\n').Last().Trim() : $"Exit code {proc.ExitCode}";
                    return ("", err);
                }

                if (stdout.Length == 0)
                {
                    Console.Error.WriteLine($"  DEBUG RunTool: exit 0, stdout empty, method={pathMethod}, path={toolPath}");
                    if (stderr.Length > 0) Console.Error.WriteLine($"    stderr: [{stderr.Substring(0, Math.Min(300, stderr.Length))}]");
                    else Console.Error.WriteLine($"    stderr: (empty)");
                    Console.Error.WriteLine($"    cpu: {lastCpu.TotalSeconds:F1}s total");
                }

                return (stdout, null);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  DEBUG RunTool: exception, method={pathMethod}, path={toolPath}");
                Console.Error.WriteLine($"    error:  {ex.Message}");
                return ("", ex.Message);
            }
            finally
            {
                if (tempLink != null)
                {
                    try { RetryDelete(tempLink); }
                    catch (Exception ex) { Console.Error.WriteLine($"  WARNING: failed to delete hardlink {tempLink}: {ex.Message}"); }
                }
            }
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

        /// <summary>
        /// Downmix a multi-channel audio file to stereo using ffmpeg.
        /// Returns path to temp WAV file, or null on failure. Caller must delete the temp file.
        /// </summary>
        static string? DownmixToStereo(string audioPath)
        {
            var ffmpeg = _ffmpegPath.Value;
            if (ffmpeg == null) return null;

            string tempPath = Path.Combine(Path.GetTempPath(), $"truedat_stereo_{Guid.NewGuid():N}.wav");
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

        static (string Fingerprint, int Duration, string? Error) RunChromaprinter(string exe, string audioPath, CancellationToken ct = default)
        {
            var (stdout, error) = RunTool(exe, audioPath, ct);

            // Multi-channel files: downmix to stereo and retry
            if (error != null && error.Contains("more than 2 channels"))
            {
                var stereoPath = DownmixToStereo(audioPath);
                if (stereoPath != null)
                {
                    try
                    {
                        Console.WriteLine($"  Downmixing to stereo (multi-channel detected)");
                        (stdout, error) = RunTool(exe, stereoPath, ct);
                    }
                    finally
                    {
                        try { File.Delete(stereoPath); } catch { }
                    }
                }
                else if (_ffmpegPath.Value == null)
                {
                    error += " (install ffmpeg on PATH to auto-downmix)";
                }
            }

            if (error != null) return ("", 0, error);

            string fingerprint = "";
            int duration = 0;
            foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("DURATION=")) int.TryParse(line.Substring("DURATION=".Length).Trim(), out duration);
                else if (line.StartsWith("FINGERPRINT=")) fingerprint = line.Substring("FINGERPRINT=".Length).Trim();
            }

            if (string.IsNullOrEmpty(fingerprint))
            {
                Console.WriteLine($"  DEBUG chromaprint: exit 0 but no FINGERPRINT. stdout={stdout.Length} chars, first 200: [{stdout.Substring(0, Math.Min(200, stdout.Length))}]");
                return ("", 0, "No FINGERPRINT in output");
            }
            return (fingerprint, duration, null);
        }

        static (string Fingerprint, int Duration, string? Error) RunFpcalc(string fpcalcExe, string audioPath, CancellationToken ct = default)
        {
            string toolPath = SafePath(audioPath);
            string? tempLink = null;
            string pathMethod = toolPath == audioPath ? "original" : "8.3";

            if (HasNonAscii(toolPath) ||
                !string.Equals(Path.GetExtension(toolPath), Path.GetExtension(audioPath), StringComparison.OrdinalIgnoreCase))
            {
                var (link, method) = TryCreateHardlink(audioPath);
                pathMethod = method;
                if (link != null) { toolPath = link; tempLink = link; }
            }
            if (_audit && pathMethod != "original")
                Console.WriteLine($"  DEBUG path: {pathMethod} -> {toolPath}");

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fpcalcExe, Arguments = $"-length 30 {PathHelper.QuoteArg(toolPath)}",
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true
                };

                using var proc = Process.Start(psi)!;
                ApplyCpuLimit(proc);
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();

                if (!proc.WaitForExit(120000)) // 2 min timeout (30s of audio + overhead)
                {
                    try { proc.Kill(); proc.WaitForExit(5000); } catch { }
                    return ("", 0, "fpcalc timed out (120s)");
                }
                proc.WaitForExit();

                var stdout = stdoutTask.Wait(10000) ? stdoutTask.Result : "";
                var stderr = stderrTask.Wait(10000) ? stderrTask.Result : "";

                if (proc.ExitCode != 0)
                {
                    var err = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim().Split('\n').Last().Trim() : $"Exit code {proc.ExitCode}";
                    return ("", 0, err);
                }

                string fingerprint = "";
                int duration = 0;
                foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("DURATION=")) int.TryParse(line.Substring("DURATION=".Length).Trim(), out duration);
                    else if (line.StartsWith("FINGERPRINT=")) fingerprint = line.Substring("FINGERPRINT=".Length).Trim();
                }

                if (string.IsNullOrEmpty(fingerprint))
                {
                    if (_audit)
                    {
                        var stderrSnippet = string.IsNullOrWhiteSpace(stderr)
                            ? "(stderr empty)"
                            : stderr.Trim().Replace("\r", " ").Replace("\n", " | ");
                        if (stderrSnippet.Length > 200) stderrSnippet = stderrSnippet.Substring(0, 200) + "…";
                        Console.WriteLine($"  DEBUG fpcalc: exit 0 but no FINGERPRINT. stdout={stdout.Length} chars; stderr=\"{stderrSnippet}\"; file=\"{Path.GetFileName(audioPath)}\". Retrying via ffmpeg-transcoded WAV.");
                    }

                    // Same retry pattern as RunMd5 + AnalyzeWithEssentia: pipe the file
                    // through ffmpeg to a clean stereo WAV and re-run. fpcalc's silent-no-output
                    // mode hits encoder quirks, container oddities, or specific MP3 frame
                    // layouts; a re-encode normalizes those away.
                    var wavPath = DownmixToStereo(audioPath);
                    if (wavPath != null)
                    {
                        try
                        {
                            var wavPsi = new ProcessStartInfo
                            {
                                FileName = fpcalcExe, Arguments = $"-length 30 {PathHelper.QuoteArg(wavPath)}",
                                RedirectStandardOutput = true, RedirectStandardError = true,
                                UseShellExecute = false, CreateNoWindow = true
                            };
                            using var wavProc = Process.Start(wavPsi)!;
                            ApplyCpuLimit(wavProc);
                            var wavStdoutTask = wavProc.StandardOutput.ReadToEndAsync();
                            var wavStderrTask = wavProc.StandardError.ReadToEndAsync();
                            if (!wavProc.WaitForExit(120000))
                            {
                                try { wavProc.Kill(); wavProc.WaitForExit(5000); } catch { }
                                return ("", 0, "fpcalc retry timed out (120s)");
                            }
                            wavProc.WaitForExit();
                            var wavStdout = wavStdoutTask.Wait(10000) ? wavStdoutTask.Result : "";
                            wavStderrTask.Wait(10000);
                            if (wavProc.ExitCode == 0)
                            {
                                foreach (var line in wavStdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                                {
                                    if (line.StartsWith("DURATION=")) int.TryParse(line.Substring("DURATION=".Length).Trim(), out duration);
                                    else if (line.StartsWith("FINGERPRINT=")) fingerprint = line.Substring("FINGERPRINT=".Length).Trim();
                                }
                                if (!string.IsNullOrEmpty(fingerprint))
                                {
                                    if (_audit) Console.WriteLine($"  DEBUG fpcalc: retry via ffmpeg WAV succeeded.");
                                    return (fingerprint, duration, null);
                                }
                            }
                        }
                        finally
                        {
                            try { File.Delete(wavPath); } catch { }
                        }
                    }
                    return ("", 0, string.IsNullOrWhiteSpace(stderr) ? "No FINGERPRINT in fpcalc output (after ffmpeg retry)" : "fpcalc: " + stderr.Trim().Split('\n').Last().Trim());
                }
                return (fingerprint, duration, null);
            }
            catch (Exception ex) { return ("", 0, ex.Message); }
            finally
            {
                if (tempLink != null)
                    try { File.Delete(tempLink); }
                    catch (Exception ex) { Console.WriteLine($"  WARNING: failed to delete hardlink {tempLink}: {ex.Message}"); }
            }
        }

        static (string Md5, string? Error) RunMd5(string exe, string audioPath, CancellationToken ct = default)
        {
            var (stdout, error) = RunTool(exe, audioPath, ct);

            // Multi-channel files: downmix to stereo and retry
            if (error != null && error.Contains("more than 2 channels"))
            {
                var stereoPath = DownmixToStereo(audioPath);
                if (stereoPath != null)
                {
                    try
                    {
                        Console.WriteLine($"  Downmixing to stereo (multi-channel detected)");
                        (stdout, error) = RunTool(exe, stereoPath, ct);
                    }
                    finally
                    {
                        try { File.Delete(stereoPath); } catch { }
                    }
                }
                else if (_ffmpegPath.Value == null)
                {
                    error += " (install ffmpeg on PATH to auto-downmix)";
                }
            }

            if (error != null) return ("", error);

            foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("MD5:"))
                {
                    var md5 = line.Substring("MD5:".Length).Trim();
                    if (!string.IsNullOrEmpty(md5)) return (md5, null);
                }
            }

            Console.WriteLine($"  DEBUG md5: exit 0 but no MD5 line. stdout={stdout.Length} chars, first 200: [{stdout.Substring(0, Math.Min(200, stdout.Length))}]");
            return ("", "No MD5 in output");
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

        static void SaveFingerprints(string path, ConcurrentDictionary<string, FingerprintEntry> allFp)
        {
            var tmpPath = path + ".tmp";
            try { File.Delete(tmpPath); } catch { }

            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            using (var jw = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
            {
                jw.WriteStartObject();
                jw.WriteString("version", "1.0");
                jw.WriteString("generatedAt", DateTime.UtcNow.ToString("o"));
                jw.WriteNumber("trackCount", allFp.Count);
                jw.WritePropertyName("tracks");
                jw.WriteStartObject();
                foreach (var kvp in allFp)
                {
                    var fp = kvp.Value.Fp;
                    jw.WritePropertyName(kvp.Key);
                    jw.WriteStartObject();
                    jw.WriteNumber("trackId", fp.TrackId);
                    jw.WriteString("artist", fp.Artist);
                    jw.WriteString("title", fp.Title);
                    jw.WriteString("album", fp.Album);
                    jw.WriteString("genre", fp.Genre);
                    if (!string.IsNullOrEmpty(fp.Chromaprint))
                    {
                        jw.WriteString("chromaprint", fp.Chromaprint);
                        jw.WriteNumber("duration", fp.Duration);
                    }
                    if (!string.IsNullOrEmpty(fp.Md5))
                    {
                        jw.WriteString("md5", fp.Md5);
                    }
                    jw.WriteString("lastModified", kvp.Value.LastModified.ToString("o"));
                    if (!string.IsNullOrEmpty(kvp.Value.FileMd5))
                        jw.WriteString("fileMd5", kvp.Value.FileMd5);
                    jw.WriteEndObject();
                }
                jw.WriteEndObject();
                jw.WriteEndObject();
            }

            AtomicReplace(tmpPath, path);
            if (_audit) { try { Console.WriteLine($"  DEBUG save: {path} ({new FileInfo(path).Length / 1024} KB, {allFp.Count} tracks)"); } catch { } }
        }

        static int LoadExistingFingerprints(string path, ConcurrentDictionary<string, FingerprintEntry> allFp)
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

                    allFp[filePath] = new FingerprintEntry
                    {
                        LastModified = lastMod,
                        Fp = new TrackFingerprint
                        {
                            FilePath = filePath,
                            TrackId = GetInt(track, "trackId"),
                            Artist = GetStr(track, "artist"),
                            Title = GetStr(track, "title"),
                            Album = GetStr(track, "album"),
                            Genre = GetStr(track, "genre"),
                            Chromaprint = GetStr(track, "chromaprint"),
                            Duration = GetInt(track, "duration"),
                            Md5 = GetStr(track, "md5")
                        },
                        FileMd5 = GetStr(track, "fileMd5") is var md5Str && md5Str.Length > 0 ? md5Str : null
                    };
                }
                return allFp.Count;
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
                Console.WriteLine($"ERROR: Existing fingerprints file is corrupt: {ex.Message}");
                Console.WriteLine($"Backup: {bakPath}");
                Console.WriteLine();
                Console.WriteLine("To start fresh, delete or rename the corrupt file and re-run:");
                Console.WriteLine($"  del \"{path}\"");
                Environment.Exit(1);
                return 0; // unreachable, satisfies compiler
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARNING: Could not load existing fingerprints ({ex.Message})");
                return 0;
            }
        }

        static void SaveDetails(string path, ConcurrentDictionary<string, AudioDetails> allDetails)
        {
            var tmpPath = path + ".tmp";
            try { File.Delete(tmpPath); } catch { }

            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            using (var jw = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
            {
                jw.WriteStartObject();
                jw.WriteString("version", "1.0");
                jw.WriteString("generatedAt", DateTime.UtcNow.ToString("o"));
                jw.WriteNumber("trackCount", allDetails.Count);
                jw.WritePropertyName("tracks");
                jw.WriteStartObject();
                foreach (var kvp in allDetails)
                {
                    var d = kvp.Value;
                    jw.WritePropertyName(kvp.Key);
                    jw.WriteStartObject();
                    jw.WriteNumber("trackId", d.TrackId);
                    jw.WriteString("artist", d.Artist);
                    jw.WriteString("title", d.Title);
                    jw.WriteString("codec", d.Codec);
                    jw.WriteNumber("channels", d.Channels);
                    jw.WriteNumber("sampleRate", d.SampleRate);
                    jw.WriteNumber("bitRate", d.BitRate);
                    jw.WriteNumber("bitDepth", d.BitDepth);
                    jw.WriteNumber("duration", d.Duration);
                    jw.WriteString("format", d.Format);
                    jw.WriteNumber("sizeMb", d.SizeMb);
                    jw.WriteString("lastProbed", d.LastProbed.ToString("o"));
                    jw.WriteString("lastModified", d.LastModified.ToString("o"));
                    if (!string.IsNullOrEmpty(d.FileMd5))
                        jw.WriteString("fileMd5", d.FileMd5);
                    jw.WriteEndObject();
                }
                jw.WriteEndObject();
                jw.WriteEndObject();
            }

            AtomicReplace(tmpPath, path);
            if (_audit) { try { Console.WriteLine($"  DEBUG save: {path} ({new FileInfo(path).Length / 1024} KB, {allDetails.Count} tracks)"); } catch { } }
        }

        static int LoadExistingDetails(string path, ConcurrentDictionary<string, AudioDetails> allDetails)
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
                    var t = prop.Value;

                    DateTime lastMod = DateTime.MinValue;
                    var lastModStr = GetStr(t, "lastModified");
                    if (!string.IsNullOrEmpty(lastModStr))
                        DateTime.TryParse(lastModStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out lastMod);

                    DateTime lastProbed = DateTime.MinValue;
                    var lastProbedStr = GetStr(t, "lastProbed");
                    if (!string.IsNullOrEmpty(lastProbedStr))
                        DateTime.TryParse(lastProbedStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out lastProbed);

                    allDetails[filePath] = new AudioDetails
                    {
                        TrackId = GetInt(t, "trackId"),
                        Artist = GetStr(t, "artist"),
                        Title = GetStr(t, "title"),
                        Codec = GetStr(t, "codec"),
                        Channels = GetInt(t, "channels"),
                        SampleRate = GetInt(t, "sampleRate"),
                        BitRate = GetInt(t, "bitRate"),
                        BitDepth = GetInt(t, "bitDepth"),
                        Duration = GetDbl(t, "duration"),
                        Format = GetStr(t, "format"),
                        SizeMb = GetDbl(t, "sizeMb"),
                        LastProbed = lastProbed,
                        LastModified = lastMod,
                        FileMd5 = GetStr(t, "fileMd5") is var md5Str && md5Str.Length > 0 ? md5Str : null
                    };
                }
                return allDetails.Count;
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
                Console.WriteLine($"ERROR: Existing details file is corrupt: {ex.Message}");
                Console.WriteLine($"Backup: {bakPath}");
                Console.WriteLine();
                Console.WriteLine("To start fresh, delete or rename the corrupt file and re-run:");
                Console.WriteLine($"  del \"{path}\"");
                Environment.Exit(1);
                return 0; // unreachable, satisfies compiler
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARNING: Could not load existing details ({ex.Message})");
                return 0;
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
            if (!string.IsNullOrEmpty(entry.AudioMd5))
                jw.WriteString("audioMd5", entry.AudioMd5);
            if (!string.IsNullOrEmpty(entry.AudioStreamSha256))
            {
                jw.WriteString("audioStreamSha256", entry.AudioStreamSha256);
                // Emit the source signal only for the whole-file fallback path so
                // consumers can detect the lower-trust hash.
                if (entry.AudioStreamSha256Source == "whole-file")
                    jw.WriteString("audioStreamSha256Source", "whole-file");
            }
            if (entry.FingerprintV1 != null)
                WriteFingerprintV1(jw, entry.FingerprintV1);
            if (!string.IsNullOrEmpty(entry.Chromaprint))
            {
                jw.WriteString("chromaprint", entry.Chromaprint);
                if (entry.ChromaprintDuration.HasValue && entry.ChromaprintDuration.Value > 0)
                    jw.WriteNumber("chromaprintDuration", entry.ChromaprintDuration.Value);
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
                        AudioMd5 = GetStr(track, "audioMd5") is var amd5Str && amd5Str.Length > 0 ? amd5Str : null,
                        AudioStreamSha256 = GetStr(track, "audioStreamSha256") is var shaStr && shaStr.Length > 0 ? shaStr : null,
                        AudioStreamSha256Source = GetStr(track, "audioStreamSha256Source") is var shaSrc && shaSrc.Length > 0 ? shaSrc : null,
                        FingerprintV1 = ParseFingerprintV1FromJson(track),
                        Chromaprint = GetStr(track, "chromaprint") is var cpStr && cpStr.Length > 0 ? cpStr : null,
                        ChromaprintDuration = GetNullableDbl(track, "chromaprintDuration")
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
                HpcpEntropy = GetNullableDbl(track, "hpcpEntropy")
            };
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

                // CPU activity monitoring — same approach as RunTool
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

                // Flush async stderr read buffer (matches RunTool pattern).
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
                    HpcpEntropy = hpcpEntropy
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
            public string Codec = "other";
            public string? CodecRaw;
            public int Bitrate;
            public string AudioHead64kMd5 = "";
            public string AudioHead64kMd5Source = "invariant"; // "invariant" | "whole-file-start"
            public long InvariantStart;
            public long InvariantEnd;
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

        /// <summary>Compute fingerprint.v1 composite. Returns null + error string on failure.</summary>
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

                var tail = ComputePathTail(filePath);
                if (tail == null)
                {
                    error = "path has too few segments for pathTail";
                    return null;
                }

                var (codec, codecRaw) = NormalizeCodec(tfile);

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

                return new FingerprintV1
                {
                    FileSize = fileSize,
                    PathTail = tail,
                    DurationMs = (int)props.Duration.TotalMilliseconds,
                    SampleRate = props.AudioSampleRate,
                    Channels = props.AudioChannels,
                    Codec = codec,
                    CodecRaw = codecRaw,
                    Bitrate = props.AudioBitrate,
                    AudioHead64kMd5 = headMd5,
                    AudioHead64kMd5Source = headSource,
                    InvariantStart = invStart,
                    InvariantEnd = invEnd,
                };
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
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
        {
            error = null;
            try
            {
                long invStart, invEnd;
                using (var tfile = TagLib.File.Create(filePath))
                {
                    invStart = tfile.InvariantStartPosition;
                    invEnd = tfile.InvariantEndPosition;
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
        /// Streaming SHA-256 of the audio region [invariantStart, invariantEnd).
        /// Disk-bound; SHA-NI makes CPU cost negligible on modern hardware.
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
                using var sha = SHA256.Create();
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
                    Codec = string.IsNullOrEmpty(GetStr(fp, "codec")) ? "other" : GetStr(fp, "codec"),
                    CodecRaw = string.IsNullOrEmpty(codecRaw) ? null : codecRaw,
                    Bitrate = GetInt(fp, "bitrate"),
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

        /// <summary>Serialize a FingerprintV1 into an open identity object as identity["fingerprint.v1"].</summary>
        static void WriteFingerprintV1(Utf8JsonWriter jw, FingerprintV1 fp)
        {
            jw.WritePropertyName("fingerprint.v1");
            jw.WriteStartObject();
            jw.WriteNumber("fileSize", fp.FileSize);
            jw.WriteString("pathTail", fp.PathTail);
            jw.WriteNumber("durationMs", fp.DurationMs);
            jw.WriteNumber("sampleRate", fp.SampleRate);
            jw.WriteNumber("channels", fp.Channels);
            jw.WriteString("codec", fp.Codec);
            if (!string.IsNullOrEmpty(fp.CodecRaw))
                jw.WriteString("codecRaw", fp.CodecRaw);
            jw.WriteNumber("bitrate", fp.Bitrate);
            jw.WriteString("audioHead64kMd5", fp.AudioHead64kMd5);
            if (fp.AudioHead64kMd5Source != "invariant")
                jw.WriteString("audioHead64kMd5Source", fp.AudioHead64kMd5Source);
            jw.WriteEndObject();
        }

        static Dictionary<string, (T Entry, string OldKey)>? BuildMd5Index<T>(
            ConcurrentDictionary<string, T> cache,
            Func<T, string?> getMd5) where T : class
        {
            Dictionary<string, (T, string)>? index = null;
            foreach (var kvp in cache)
            {
                var md5 = getMd5(kvp.Value);
                if (string.IsNullOrEmpty(md5)) continue;
                index ??= new Dictionary<string, (T, string)>(StringComparer.OrdinalIgnoreCase);
                if (!index.ContainsKey(md5!))
                    index[md5!] = (kvp.Value, kvp.Key);
            }
            return index;
        }

        /// <summary>
        /// Build a fresh TrackEntry that reuses cached Essentia features but takes
        /// fresh metadata from the iTunes XML track and (optionally) refreshes
        /// identity fields the caller has just recomputed off disk. Used by every
        /// cache-reuse path (path-mtime, cross-MD5, sha-path, sha-cross). Centralized
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
            FingerprintV1? refreshedFp)
        {
            return RebuildCacheEntryCore(source, t.TrackId, t.Artist, t.Name, t.Album, t.Genre, t.Location, newLastMod, refreshedFileMd5, refreshedFp);
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
            FingerprintV1? refreshedFp)
        {
            return RebuildCacheEntryCore(source, source.Features.TrackId, artist, title, album, genre, filePath, newLastMod, refreshedFileMd5, refreshedFp);
        }

        static TrackEntry RebuildCacheEntryCore(
            TrackEntry source,
            int trackId,
            string artist, string title, string album, string genre, string filePath,
            DateTime newLastMod,
            string? refreshedFileMd5,
            FingerprintV1? refreshedFp)
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
                    HpcpEntropy = sf.HpcpEntropy
                },
                LastModified = newLastMod,
                AnalysisDurationSecs = source.AnalysisDurationSecs,
                FileMd5 = refreshedFileMd5 ?? source.FileMd5,
                AudioMd5 = source.AudioMd5,
                AudioStreamSha256 = source.AudioStreamSha256,
                AudioStreamSha256Source = source.AudioStreamSha256Source,
                FingerprintV1 = refreshedFp ?? source.FingerprintV1,
                Chromaprint = source.Chromaprint,
                ChromaprintDuration = source.ChromaprintDuration
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
            var remaining = total - done;
            return $" ETA {FormatTimeSpan(TimeSpan.FromSeconds(elapsed.TotalSeconds / done * remaining))}";
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
        static void AtomicReplace(string tmpPath, string targetPath)
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
