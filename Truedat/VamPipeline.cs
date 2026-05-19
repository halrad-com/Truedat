using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Truedat
{
    /// <summary>
    /// VAM S2.5 — scan-lifetime owner of the VAD + SER inference sessions.
    /// One instance per scan, shared across worker tasks. ORT's
    /// <see cref="Microsoft.ML.OnnxRuntime.InferenceSession"/> is internally
    /// thread-safe, so concurrent worker calls to <see cref="AnalyzeTrack"/>
    /// share the same session(s) without explicit locking.
    ///
    /// Skeleton period (both stages are Identity stubs):
    ///   - VAD reports vocalCoverage = 0 → every track emits the instrumental
    ///     branch (`vocal: { vocalCoverage: 0, vad: { method: skeleton-stub } }`)
    ///   - SER never runs because the gate suppresses it
    ///   - Schema lands on disk; per-backend block stays empty
    ///
    /// Real-models period (S2.2.5 + S2.3.5):
    ///   - VAD returns real frame probabilities
    ///   - When gate passes, SER runs on CPU EP for S2 baseline
    ///   - <see cref="VocalBlock.ByBackend"/> gets a "cpu" entry per track
    ///   - S3 swaps in DML EP, adds "gpu-directml" / "npu-directml" entries
    ///
    /// VAM is excluded from the cache re-extract canary on purpose — see
    /// CLAUDE.md "Re-extract gate": installs without ffmpeg / the real model
    /// files would otherwise loop forever, and the VocalBlock is enrichable
    /// after the fact via `--enrich vam --file-list`.
    /// </summary>
    internal sealed class VamPipeline : IDisposable
    {
        private readonly VadStage _vad;
        private readonly VocalAffectStage _vam;
        private readonly string _ffmpegPath;
        private readonly TextWriter _log;
        private bool _disposed;

        /// <summary>Backend tag for the per-backend nesting. CPU EP for S2;
        /// S3 will spawn additional pipelines tagged "gpu-directml" /
        /// "npu-directml" and merge their results into the same VocalBlock.</summary>
        public string BackendTag { get; }

        public bool BothStagesSkeleton => _vad.IsSkeleton && _vam.IsSkeleton;

        public VamPipeline(string vadModelPath, string vamModelPath, string ffmpegPath, TextWriter log)
        {
            if (string.IsNullOrEmpty(ffmpegPath))
                throw new ArgumentException("ffmpeg.exe path is required for VAM pipeline", nameof(ffmpegPath));
            _ffmpegPath = ffmpegPath;
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _vad = new VadStage(vadModelPath, log);
            _vam = new VocalAffectStage(vamModelPath, log);
            BackendTag = "cpu";  // S2 baseline; S3 introduces "gpu-directml" / "npu-directml"
        }

        /// <summary>Analyse one track end-to-end. Returns a VocalBlock matching
        /// the persisted schema shape, or null when decode failed (caller
        /// records nothing). Designed to be called concurrently from worker
        /// tasks — the underlying ORT sessions are thread-safe.
        ///
        /// Always emits the `vad` sub-block so consumers can audit which VAD
        /// method ran. Per-backend SER results land under
        /// <see cref="VocalBlock.ByBackend"/>[<see cref="BackendTag"/>] only
        /// when the VAD gate passes AND SER produces output (i.e. real-model
        /// path; skeleton path never lands a per-backend entry because the
        /// gate suppresses SER).</summary>
        public VocalBlock? AnalyzeTrack(string audioPath)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(VamPipeline));
            if (string.IsNullOrEmpty(audioPath) || !File.Exists(audioPath)) return null;

            // Reuse the same s16le → float32 16 kHz mono decode path the
            // single-track / batch smokes use. One ffmpeg subprocess per track
            // (extra cost on top of the existing Essentia / bitUsage /
            // hfAnalysis pipes — same per-track-subprocess pattern).
            var pcm = Program.DecodeMidTrackPcm16kMono(audioPath, _ffmpegPath, durationSec: 30);
            if (pcm == null || pcm.Length == 0) return null;

            var vr = _vad.Run(pcm);
            var block = new VocalBlock
            {
                VocalCoverage = Math.Round(vr.VocalCoverage, 4),
                Vad = new VadInfo
                {
                    Method = vr.Method,
                    ModelVersion = _vad.IsSkeleton ? "skeleton-stub" : "TBD",
                    Threshold = VadStage.DefaultGateThreshold,
                    InferenceMs = Math.Round(vr.InferenceMs, 1),
                },
            };

            if (vr.VocalCoverage < VadStage.DefaultGateThreshold)
            {
                // Instrumental — VAM skipped, byBackend stays null.
                return block;
            }

            // Gate passed. Run SER, write the per-backend entry.
            var sw = Stopwatch.StartNew();
            var output = _vam.Run(pcm);
            sw.Stop();

            // Skeleton SER returns the entire audio buffer (Identity output),
            // not V/A/D scalars. Map to zero values + a clear modelVersion
            // tag so the schema is honest. Real wav2vec2 swap (S2.2.5)
            // replaces this block with proper V/A/D parsing.
            double valence, arousal, dominance;
            string modelVersion;
            if (_vam.IsSkeleton)
            {
                valence = 0.0;
                arousal = 0.0;
                dominance = 0.0;
                modelVersion = "skeleton-identity";
            }
            else
            {
                // S2.2.5+ — wav2vec2-MSP-Podcast outputs [valence, arousal,
                // dominance] scalars. Real parsing lands when the real model
                // ships; for now, refuse to invent numbers if a non-skeleton
                // model somehow loaded against this skeleton-era code.
                throw new NotImplementedException(
                    "VamPipeline.AnalyzeTrack for a real SER model lands in S2.2.5 — " +
                    "the current build only supports skeleton-identity SER output.");
            }

            block.ByBackend = new Dictionary<string, VocalBackendResult>(StringComparer.OrdinalIgnoreCase)
            {
                [BackendTag] = new VocalBackendResult
                {
                    Valence = Math.Round(valence, 4),
                    Arousal = Math.Round(arousal, 4),
                    Dominance = Math.Round(dominance, 4),
                    ModelVersion = modelVersion,
                    InferenceMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                    AnalyzedAt = DateTime.UtcNow,
                },
            };
            return block;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _vad?.Dispose();
            _vam?.Dispose();
            _disposed = true;
        }
    }
}
