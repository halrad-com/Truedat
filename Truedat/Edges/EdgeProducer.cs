using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Truedat.Edges
{
    /// <summary>S2 C4 — the <c>edges</c> producer's orchestration around one analysed track: the extractor profile that asks
    /// the .5 build for a curated binary frames file, then frames file → fold → PCM pass → one <see cref="EdgeFoldResult"/>.
    /// Pure with respect to the store (the sink writes it at the flush) and the catalog (nothing is written to mbxmoods.json).
    ///
    /// Staging rules (operator, 2026-10-12): the profile and the frames file live in the STAGE dir beside the extractor's temp
    /// JSON (ASCII by construction — the extractor's writers open output paths narrow, so a non-ASCII path lands under a
    /// mojibake name with exit 0), both are deleted in the caller's finally, and the startup sweep covers a killed scan. The
    /// PCM pass reads the path the extractor read (the staged copy), so a network source is still read once.</summary>
    internal static class EdgeProducer
    {
        /// <summary>The descriptors the fold reads, as <c>framesInclude</c> prefixes (exact name, or name + "." — the
        /// extractor's Pool namespace rule). metadata.audio_properties carries the decoded length the fold is anchored to.</summary>
        internal static readonly string[] FramesInclude =
        {
            "lowlevel.loudness_ebu128", "lowlevel.mfcc", "tonal.hpcp", "rhythm.beats_position", "rhythm.beats_loudness",
            "lowlevel.spectral_centroid", "lowlevel.spectral_flux", "lowlevel.spectral_rolloff", "lowlevel.spectral_entropy",
            "lowlevel.spectral_complexity", "lowlevel.dissonance", "lowlevel.pitch_salience", "lowlevel.zerocrossingrate",
            "lowlevel.spectral_rms", "lowlevel.spectral_energy", "lowlevel.spectral_strongpeak", "metadata.audio_properties",
        };

        /// <summary>The extractor builds that carry the binary frames writer (<c>--frames-bin</c> + <c>framesInclude</c>) the edges
        /// path reads — a capability set, not one exact string (plan B6 [c1]). Format compatibility lives here; measurement provenance
        /// (which edges an extractor change invalidates) is the edge producer version, never the build id.</summary>
        internal static readonly string[] SupportedBuilds = { "output-x64.5", "output-x64.6" };
        internal static string SupportedBuildsText => string.Join(" or ", SupportedBuilds);
        internal static bool SupportsFrames(string? build)
            => build != null && Array.Exists(SupportedBuilds, b => string.Equals(b, build, StringComparison.OrdinalIgnoreCase));

        /// <summary>The two-line profile plus the include list, as the extractor parses it.</summary>
        internal static string ProfileText()
        {
            var sb = new StringBuilder();
            sb.Append("outputFrames: 1\noutputFormat: json\nframesInclude: [");
            sb.Append(string.Join(", ", FramesInclude));
            sb.Append("]\n");
            return sb.ToString();
        }

        /// <summary>The SCAN profile (extractor .6 drop 2, 02d1115: <c>essentia-build/spike-x6/scan-profile.yaml</c> verbatim, comments
        /// dropped): the extractor computes and emits only the statistics truedat reads (<c>scan-allowlist.txt</c> — lowlevel .mean on
        /// every descriptor and .stdev on six, tonal .mean + hpcp_entropy.stdev, rhythm .mean, mfcc mean + cov, gfcc mean) and skips the
        /// 128-band mel branch and the per-frame band storage, which nothing reads. <c>x6.md5</c> stays ON: cutting it changes the edges
        /// frames file's descriptor set for a cost that did not measure. The output falls from 599 keys to 187 (94 read); the extractor's
        /// time by about 3 % (cpu-table.txt — the heavy branches all feed keys truedat reads). The 2-arg call stays the FULL extractor.</summary>
        internal const string ScanProfileKeys =
            "lowlevel.stats: [mean, stdev]\ntonal.stats: [mean, stdev]\nrhythm.stats: [mean]\nlowlevel.mfccStats: [mean, cov]\nlowlevel.gfccStats: [mean]\n" +
            "x6.melbands128: 0\nx6.bandsStorage: 0\nx6.md5: 1\n";

        /// <summary>What truedat hands the extractor as its one profile file: the frames block when edges are on, the scan keys when the
        /// build honours them (<see cref="Program.ScanProfileCapable"/>), both when both — the extractor takes a single profile.</summary>
        internal static string ProfileTextFor(bool edges, bool scan)
            => (edges ? ProfileText() : "") + (scan ? ScanProfileKeys : "");

        /// <summary>Writes the profile beside the extractor's temp JSON (same directory, so the same ASCII guarantee) and
        /// returns its path with the frames path the extractor is told to write (null when edges are off: no frames file is asked for).
        /// Null profile when neither edges nor the scan profile apply — the caller makes the plain 2-arg call.</summary>
        internal static (string? ProfilePath, string? FramesPath) PrepareFor(string tempJson, bool edges = true, bool scan = false)
        {
            if (!edges && !scan) return (null, null);
            string stem = Path.Combine(Path.GetDirectoryName(tempJson) ?? ".", Path.GetFileNameWithoutExtension(tempJson));
            string profile = stem + ".profile.yaml", frames = stem + ".frames.bin";
            File.WriteAllText(profile, ProfileTextFor(edges, scan), new UTF8Encoding(false));
            return (profile, edges ? frames : null);
        }

        /// <summary>Frames file → fold → PCM pass. Never throws: a missing or unreadable frames file is a failed result that
        /// names the reason, so the run row carries it and no partial row lands.</summary>
        internal static EdgeFoldResult Produce(string framesPath, string audioPath, double? decodedLengthSec, string? ffmpegExe, EdgeSettings settings)
        {
            var sw = Stopwatch.StartNew();
            EdgeFoldResult r;
            try
            {
                if (!File.Exists(framesPath)) return Failed(settings, decodedLengthSec, "the extractor wrote no frames file (is it output-x64.5 with outputFrames in the profile?)");
                using var f = EspbFile.Open(framesPath);
                double duration = decodedLengthSec ?? 0;
                if (f.TryGetF32("metadata.audio_properties.length", out var len, out _) && len.Length == 1 && len[0] > 0) duration = len[0];
                if (duration <= 0) return Failed(settings, decodedLengthSec, "no decoded length (neither the frames file nor the main output carries metadata.audio_properties.length)");
                r = EdgeFold.Fold(f, duration, settings);
            }
            catch (Exception ex) { return Failed(settings, decodedLengthSec, $"frames file unreadable: {ex.Message}"); }
            r.FoldMs = sw.ElapsedMilliseconds; sw.Restart();
            if (r.Status == "ok" && ffmpegExe != null)
            {
                foreach (var e in new[] { r.Head, r.Tail })
                {
                    double start = e.StartMs / 1000.0, len = (e.EndMs - e.StartMs) / 1000.0;
                    EdgeFold.ApplyPcm(e, EdgePcm.Analyze(audioPath, ffmpegExe, start, len, outerAtStart: e.Kind == "head"));
                }
            }
            r.PcmMs = sw.ElapsedMilliseconds;
            return r;
        }

        static EdgeFoldResult Failed(EdgeSettings s, double? durationSec, string why)
        {
            var r = new EdgeFoldResult { ProducerVersion = s.ProducerVersion, DurationMs = (int)Math.Round((durationSec ?? 0) * 1000) };
            r.Head.Status = r.Tail.Status = "failed"; r.Head.Error = r.Tail.Error = why;
            return r;
        }
    }
}
