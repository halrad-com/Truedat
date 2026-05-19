using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
// SkeletonOnnx (shared with VadStage) lives in SkeletonOnnx.cs — same namespace.

namespace Truedat
{
    /// <summary>
    /// VAM S2.2 skeleton — single <see cref="InferenceSession"/> wrapping a
    /// model file on disk, CPU EP only. Caller hands in a 16 kHz mono float
    /// PCM buffer; Run returns the model's first output as a flat float[].
    ///
    /// Lifecycle: construct once per scan, hold across tracks, dispose at end.
    /// Threading: <see cref="InferenceSession.Run"/> is thread-safe internally;
    /// the worker-pool wiring (T-A3 in the roadmap) lands in a follow-up
    /// dispatch so we don't bake assumptions in before the smoke proves the
    /// wiring end-to-end.
    ///
    /// Model bootstrap: if the model file is missing, the constructor writes a
    /// trivial Identity ONNX (input "audio" float32 [1, T] → output
    /// "audio_out" float32 [1, T]) at that path so S2.2 has something to load.
    /// The skeleton emits the input back unchanged; it proves the wiring but
    /// is NOT a real SER model. The byte-builder + auto-create path go away
    /// when wav2vec2-MSP-Podcast (or equivalent) lands in S2.2.5+.
    /// </summary>
    internal sealed class VocalAffectStage : IDisposable
    {
        private readonly InferenceSession _session;
        private readonly string _modelPath;
        private readonly string _inputName;
        private readonly string _outputName;
        private readonly bool _isSkeleton;
        private bool _disposed;

        public string ModelPath => _modelPath;
        public string InputName => _inputName;
        public string OutputName => _outputName;

        /// <summary>True when the loaded model is the development Identity
        /// stub (auto-materialised when the model file was missing, or
        /// detected on load via a single-float-in / single-float-out heuristic).
        /// The S2.4 batch smoke records this per-row so the spreadsheet
        /// distinguishes skeleton runs from real-model runs.</summary>
        public bool IsSkeleton => _isSkeleton;

        public VocalAffectStage(string modelPath, TextWriter log)
        {
            if (modelPath == null) throw new ArgumentNullException(nameof(modelPath));
            if (log == null) throw new ArgumentNullException(nameof(log));
            _modelPath = modelPath;

            if (!File.Exists(modelPath))
            {
                var dir = Path.GetDirectoryName(modelPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                log.WriteLine(
                    $"[vam] model file not found at '{modelPath}' — writing development skeleton " +
                    $"(Identity op, NOT a real SER model). Replace before S2.5 ship.");
                File.WriteAllBytes(modelPath, SkeletonOnnx.BuildIdentityModel(
                    inputName: "audio", outputName: "audio_out", graphName: "vam_skeleton"));
            }

            // CPU EP only for S2.2. DirectML EP (GPU/NPU) is S3 — explicitly
            // not appended here, so any silent-fallback risk is moot.
            var opts = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING };
            _session = new InferenceSession(modelPath, opts);

            _inputName  = _session.InputMetadata.Keys.First();
            _outputName = _session.OutputMetadata.Keys.First();

            // Skeleton-marker heuristic (same as VadStage): a real wav2vec2
            // SER export has either multiple outputs (V/A/D heads) or a
            // [1,3]-ish output shape, NOT a [1, T] Identity echo. A single
            // float input with a single float output that matches the input
            // shape symbolically IS the skeleton. False positives here just
            // mean a tiny real model gets tagged 'skeleton' in the rig CSV —
            // acceptable cost vs the alternative (false negatives reporting
            // skeleton numbers as real SER output).
            _isSkeleton = _session.InputMetadata.Count == 1
                && _session.OutputMetadata.Count == 1
                && _session.InputMetadata[_inputName].ElementType == typeof(float)
                && _session.OutputMetadata[_outputName].ElementType == typeof(float);
        }

        /// <summary>Human-readable dump of the loaded model's I/O contract.
        /// Used by the --vam-smoke CLI entrypoint to confirm what loaded and
        /// to surface real-model shape/type info when wav2vec2 lands.</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendLine("VocalAffectStage — loaded model:");
            sb.AppendLine($"  path: {_modelPath}");
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
            // ORT NodeMetadata.Dimensions holds resolved ints (-1 for symbolic);
            // SymbolicDimensions holds the param names when known.
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

        /// <summary>Run one inference. <paramref name="pcm16k"/> is mono 16 kHz
        /// float audio in [-1, +1]. Tensor shape is [1, pcm16k.Length] — matches
        /// both the skeleton's symbolic [1, T] and the wav2vec2 contract
        /// (input_values float32 [1, T] at 16 kHz). Returns the first output
        /// as a flat float array.</summary>
        public float[] Run(float[] pcm16k)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(VocalAffectStage));
            if (pcm16k == null) throw new ArgumentNullException(nameof(pcm16k));

            var tensor = new DenseTensor<float>(pcm16k, new[] { 1, pcm16k.Length });
            var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
            using var results = _session.Run(inputs, new[] { _outputName });
            return results.First().AsTensor<float>().ToArray();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _session?.Dispose();
            _disposed = true;
        }
    }
}
