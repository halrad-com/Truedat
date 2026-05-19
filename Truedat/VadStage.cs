using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
// Shares SkeletonOnnx with VocalAffectStage — both bootstrap when their real
// model file is missing. Helper goes away when real models land.

namespace Truedat
{
    /// <summary>
    /// VAM S2.3 voice-activity detection. Single <see cref="InferenceSession"/>
    /// (CPU EP) wrapping a Silero VAD ONNX model on disk; consumes mono 16 kHz
    /// float32 PCM (same input shape as <see cref="VocalAffectStage"/>) and
    /// returns a <see cref="VadResult"/> with per-frame speech probabilities +
    /// an aggregated vocalCoverage scalar.
    ///
    /// Skeleton-bootstrap: when the model file is absent the constructor writes
    /// a tiny Identity ONNX (input "audio" float32 [1, T] → output "audio_out"
    /// float32 [1, T]) at that path. The skeleton path makes session creation
    /// and tensor flow exercisable end-to-end; it does NOT produce useful VAD
    /// output, and Run() honestly reports vocalCoverage = 0 with method =
    /// "skeleton-stub". S2.3.5 swaps the model file for the real Silero VAD
    /// export (snakers4/silero-vad, BSD-3-Clause) and Run() gets a frame-by-frame
    /// + LSTM-state implementation matching the real model's contract.
    ///
    /// Energy thresholding (the roadmap's "first" baseline) is intentionally
    /// not implemented: it's the wrong signal for music (continuous full-band
    /// energy regardless of vocal presence). For podcast / audiobook content
    /// it would be the right tool; see BACKLOG.md "Energy-threshold VAD for
    /// podcasts / audiobooks (future)".
    ///
    /// Lifecycle: construct once per scan, hold across tracks, dispose at end.
    /// Threading: <see cref="InferenceSession.Run"/> is thread-safe internally;
    /// worker-pool wiring is its own dispatch (T-A3).
    /// </summary>
    internal sealed class VadStage : IDisposable
    {
        private readonly InferenceSession _session;
        private readonly string _modelPath;
        private readonly string _inputName;
        private readonly string _outputName;
        private readonly bool _isSkeleton;
        private bool _disposed;

        /// <summary>Coverage at or above this threshold gates VAM inference on
        /// in the smoke / production paths. Default mirrors Silero's commonly
        /// recommended speech-probability threshold; revisit when the real
        /// model lands and corpus tuning is done.</summary>
        public const double DefaultGateThreshold = 0.1;

        public string ModelPath => _modelPath;
        public bool IsSkeleton => _isSkeleton;

        public VadStage(string modelPath, TextWriter log)
        {
            if (modelPath == null) throw new ArgumentNullException(nameof(modelPath));
            if (log == null) throw new ArgumentNullException(nameof(log));
            _modelPath = modelPath;
            _isSkeleton = false;

            if (!File.Exists(modelPath))
            {
                var dir = Path.GetDirectoryName(modelPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                log.WriteLine(
                    $"[vad] model file not found at '{modelPath}' — writing development skeleton " +
                    $"(Identity op, NOT a real Silero VAD). Run() will report vocalCoverage = 0 " +
                    $"with method=skeleton-stub until S2.3.5 ships the real model.");
                File.WriteAllBytes(modelPath, SkeletonOnnx.BuildIdentityModel(
                    inputName: "audio", outputName: "audio_out", graphName: "vad_skeleton"));
                _isSkeleton = true;
            }

            var opts = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING };
            _session = new InferenceSession(modelPath, opts);

            _inputName  = _session.InputMetadata.Keys.First();
            _outputName = _session.OutputMetadata.Keys.First();

            // Skeleton-marker heuristic for files that exist on disk: a real
            // Silero VAD export will have multiple inputs (audio + state
            // tensors) and a different graph shape. A single-input Identity
            // graph IS the skeleton. We use this so that even a pre-committed
            // skeleton file (without going through the auto-create branch)
            // still gets the skeleton-stub Run() path.
            if (_session.InputMetadata.Count == 1
                && _session.OutputMetadata.Count == 1
                && _session.InputMetadata[_inputName].ElementType == typeof(float)
                && _session.OutputMetadata[_outputName].ElementType == typeof(float))
            {
                // Most reliable tell: skeleton echoes a [1, T] tensor end-to-end.
                // We can't introspect the op_type from ORT metadata, so this
                // weaker check (single float in, single float out) is a
                // pragmatic stand-in. False positives here just mean a tiny
                // real model gets the skeleton path — acceptable cost vs the
                // alternative (false negatives reporting bogus VAD numbers).
                _isSkeleton = true;
            }
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendLine("VadStage — loaded model:");
            sb.AppendLine($"  path: {_modelPath}");
            sb.AppendLine($"  skeleton: {_isSkeleton}");
            sb.AppendLine("  Inputs:");
            foreach (var kv in _session.InputMetadata)
                sb.AppendLine($"    {kv.Key}: {DescribeMetadata(kv.Value)}");
            sb.AppendLine("  Outputs:");
            foreach (var kv in _session.OutputMetadata)
                sb.AppendLine($"    {kv.Key}: {DescribeMetadata(kv.Value)}");
            return sb.ToString().TrimEnd();
        }

        private static string DescribeMetadata(NodeMetadata m)
        {
            string dims;
            if (m.Dimensions != null && m.Dimensions.Length > 0)
            {
                dims = "[" + string.Join(",", m.Dimensions.Select((d, i) =>
                {
                    if (d > 0) return d.ToString();
                    if (m.SymbolicDimensions != null && i < m.SymbolicDimensions.Length && !string.IsNullOrEmpty(m.SymbolicDimensions[i]))
                        return m.SymbolicDimensions[i];
                    return "?";
                })) + "]";
            }
            else
            {
                dims = "[scalar]";
            }
            return $"{m.ElementType.Name} {dims}";
        }

        /// <summary>Run VAD on the supplied 16 kHz mono float PCM buffer.
        ///
        /// Skeleton path: feeds the buffer through the Identity model to
        /// exercise the session, then returns vocalCoverage = 0 with
        /// method="skeleton-stub". The inference latency we report is real;
        /// the coverage isn't, by design — see class header.
        ///
        /// Real Silero path (S2.3.5+): re-implements this body as
        /// 512-sample window inference with LSTM state propagation, aggregates
        /// frame probabilities into vocalCoverage.</summary>
        public VadResult Run(float[] pcm16k)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(VadStage));
            if (pcm16k == null) throw new ArgumentNullException(nameof(pcm16k));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var tensor = new DenseTensor<float>(pcm16k, new[] { 1, pcm16k.Length });
            var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
            using var results = _session.Run(inputs, new[] { _outputName });
            // Exercise the output to make sure ORT actually ran something.
            var _ = results.First().AsTensor<float>().Length;
            sw.Stop();

            if (_isSkeleton)
            {
                return new VadResult
                {
                    VocalCoverage = 0.0,
                    FrameProbabilities = Array.Empty<float>(),
                    InferenceMs = sw.Elapsed.TotalMilliseconds,
                    Method = "skeleton-stub",
                };
            }

            // Real-model path lands in S2.3.5 — Silero v4 with 512-sample
            // chunks, LSTM state propagation, per-frame speech probability,
            // aggregate over threshold.
            throw new NotImplementedException(
                "VadStage.Run for a real Silero VAD model lands in S2.3.5 — " +
                "the current build only supports the skeleton model.");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _session?.Dispose();
            _disposed = true;
        }
    }

    /// <summary>VAD output. Per-frame probabilities included for diagnostics
    /// when the real Silero model lands; skeleton path returns an empty array.</summary>
    internal struct VadResult
    {
        /// <summary>Fraction of audio classified as containing vocal, [0, 1].
        /// Production code uses this to gate VAM inference on.</summary>
        public double VocalCoverage;

        /// <summary>Per-frame speech probabilities, [0, 1] each. Empty when the
        /// underlying model is the skeleton stub.</summary>
        public float[] FrameProbabilities;

        public double InferenceMs;
        public string Method;
    }
}
