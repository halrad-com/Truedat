using System;
using System.Collections.Generic;

namespace Truedat
{
    /// <summary>
    /// VAM S2.5 — the persisted `vocal.*` block on each TrackEntry. Mirrors
    /// the JSON shape documented in docs/plans/2026-05-16-vader-vam-roadmap.md
    /// §3 with the federation choice (per-backend nested) applied: every
    /// inference run lands under <see cref="ByBackend"/> keyed by backend
    /// tag, so multi-machine merges keep both numbers instead of clobbering.
    ///
    /// Shape (purely instrumental case — vocalCoverage = 0):
    ///   "vocal": { "vocalCoverage": 0, "vad": { ... } }
    ///
    /// Shape (vocal present, CPU EP ran):
    ///   "vocal": {
    ///     "vocalCoverage": 0.62,
    ///     "vad": { "method": "silero-v4", "modelVersion": "...", "threshold": 0.1, "inferenceMs": 12.3 },
    ///     "byBackend": {
    ///       "cpu": { "valence": 0.32, "arousal": 0.45, "dominance": 0.50,
    ///                "modelVersion": "wav2vec2-msp-v1.0-int8",
    ///                "inferenceMs": 87.2, "analyzedAt": "2026-..." }
    ///     }
    ///   }
    ///
    /// Skeleton period: VAD always reports vocalCoverage = 0, so every track
    /// lands in the instrumental branch. The vad metadata still gets written
    /// so consumers can audit which VAD method ran. ByBackend stays empty.
    /// </summary>
    internal sealed class VocalBlock
    {
        /// <summary>Fraction of audio classified as containing vocal, [0, 1].
        /// 0 = purely instrumental (V/A omitted under any backend).</summary>
        public double VocalCoverage;

        public VadInfo? Vad;

        /// <summary>Per-backend SER outputs. Key is the backend tag (e.g.
        /// "cpu", "gpu-directml", "npu-directml"). Empty when vocalCoverage = 0
        /// (instrumental) or when VAM hasn't run yet on this entry.</summary>
        public Dictionary<string, VocalBackendResult>? ByBackend;
    }

    /// <summary>VAD audit-trail. Always present when a VocalBlock exists so
    /// downstream consumers can tell what VAD produced the coverage number.</summary>
    internal sealed class VadInfo
    {
        public string Method = "";          // "silero-v4" | "skeleton-stub" | ...
        public string? ModelVersion;        // "silero-vad-v4" | "skeleton-stub"
        public double Threshold;            // gate threshold the coverage was compared against
        public double InferenceMs;
    }

    /// <summary>Per-backend SER output. One entry per backend tag in
    /// VocalBlock.ByBackend; multi-machine federation keeps every entry so the
    /// consumer can pick which one to trust at consumption time.</summary>
    internal sealed class VocalBackendResult
    {
        public double Valence;              // [0, 1]
        public double Arousal;              // [0, 1]
        public double Dominance;            // [0, 1] — optional (MSP-Podcast-style)
        public string ModelVersion = "";    // e.g. "wav2vec2-msp-v1.0-int8" or "skeleton-identity"
        public double InferenceMs;
        public DateTime AnalyzedAt;         // UTC
    }
}
