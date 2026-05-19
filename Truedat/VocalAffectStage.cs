using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

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
        private bool _disposed;

        public string ModelPath => _modelPath;
        public string InputName => _inputName;
        public string OutputName => _outputName;

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
                File.WriteAllBytes(modelPath, SkeletonOnnx.BuildIdentityModel());
            }

            // CPU EP only for S2.2. DirectML EP (GPU/NPU) is S3 — explicitly
            // not appended here, so any silent-fallback risk is moot.
            var opts = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING };
            _session = new InferenceSession(modelPath, opts);

            _inputName  = _session.InputMetadata.Keys.First();
            _outputName = _session.OutputMetadata.Keys.First();
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

        // ─────────────────────────────────────────────────────────────────────
        // SkeletonOnnx — minimal in-tree ONNX byte builder.
        //
        // Adapted from tools/ort-spike/Program.cs TinyOnnx (the §5.0 verification
        // spike), with the second dim switched from a fixed int (dim_value) to
        // a symbolic name (dim_param "T") so the skeleton accepts any audio
        // length. ~150 bytes of ONNX, all literal.
        //
        // This whole class disappears when the real SER model lands in S2.2.5+.
        // ─────────────────────────────────────────────────────────────────────
        private static class SkeletonOnnx
        {
            public static byte[] BuildIdentityModel()
            {
                // TensorShapeProto.Dimension messages
                var dim1 = WriteMessage(w => WriteVarintField(w, fieldNumber: 1, value: 1));   // dim_value = 1
                var dimT = WriteMessage(w =>                                                    // dim_param = "T"
                {
                    WriteTag(w, fieldNumber: 2, wireType: 2);
                    WriteLengthDelimitedString(w, "T");
                });

                // TensorShapeProto: dim (repeated, field 1, message)
                var shape = WriteMessage(w =>
                {
                    WriteTag(w, 1, 2); WriteLengthDelimitedBytes(w, dim1);
                    WriteTag(w, 1, 2); WriteLengthDelimitedBytes(w, dimT);
                });

                // TypeProto.Tensor: elem_type=1 (FLOAT, field 1), shape (field 2, message)
                var tensorTypeInner = WriteMessage(w =>
                {
                    WriteVarintField(w, fieldNumber: 1, value: 1);
                    WriteTag(w, 2, 2); WriteLengthDelimitedBytes(w, shape);
                });

                // TypeProto: tensor_type (field 1, message)
                var typeProto = WriteMessage(w =>
                {
                    WriteTag(w, 1, 2); WriteLengthDelimitedBytes(w, tensorTypeInner);
                });

                byte[] makeValueInfo(string name) => WriteMessage(w =>
                {
                    WriteTag(w, 1, 2); WriteLengthDelimitedString(w, name);
                    WriteTag(w, 2, 2); WriteLengthDelimitedBytes(w, typeProto);
                });
                var vinIn  = makeValueInfo("audio");
                var vinOut = makeValueInfo("audio_out");

                // NodeProto: input[0]="audio" (field 1, repeated string),
                //            output[0]="audio_out" (field 2, repeated string),
                //            op_type="Identity" (field 4)
                var node = WriteMessage(w =>
                {
                    WriteTag(w, 1, 2); WriteLengthDelimitedString(w, "audio");
                    WriteTag(w, 2, 2); WriteLengthDelimitedString(w, "audio_out");
                    WriteTag(w, 4, 2); WriteLengthDelimitedString(w, "Identity");
                });

                // GraphProto: node (1, repeated message), name (2, string),
                //             input (11, repeated message), output (12, repeated message)
                var graph = WriteMessage(w =>
                {
                    WriteTag(w, 1, 2);  WriteLengthDelimitedBytes(w, node);
                    WriteTag(w, 2, 2);  WriteLengthDelimitedString(w, "vam_skeleton");
                    WriteTag(w, 11, 2); WriteLengthDelimitedBytes(w, vinIn);
                    WriteTag(w, 12, 2); WriteLengthDelimitedBytes(w, vinOut);
                });

                // OperatorSetIdProto: version=18 (field 2). Domain "" omitted (proto3 default).
                var opsetImport = WriteMessage(w =>
                {
                    WriteVarintField(w, fieldNumber: 2, value: 18);
                });

                // ModelProto: ir_version=8 (1), producer_name (2), graph (7), opset_import (8)
                var model = WriteMessage(w =>
                {
                    WriteVarintField(w, fieldNumber: 1, value: 8);
                    WriteTag(w, 2, 2); WriteLengthDelimitedString(w, "truedat-vam-skeleton");
                    WriteTag(w, 7, 2); WriteLengthDelimitedBytes(w, graph);
                    WriteTag(w, 8, 2); WriteLengthDelimitedBytes(w, opsetImport);
                });

                return model;
            }

            private static byte[] WriteMessage(Action<MemoryStream> body)
            {
                using var ms = new MemoryStream();
                body(ms);
                return ms.ToArray();
            }

            private static void WriteTag(Stream s, int fieldNumber, int wireType)
                => WriteVarintRaw(s, (ulong)((fieldNumber << 3) | wireType));

            private static void WriteVarintField(Stream s, int fieldNumber, long value)
            {
                WriteTag(s, fieldNumber, 0);     // wire type 0 = varint
                WriteVarintRaw(s, (ulong)value);
            }

            private static void WriteVarintRaw(Stream s, ulong value)
            {
                while (value >= 0x80)
                {
                    s.WriteByte((byte)(value | 0x80));
                    value >>= 7;
                }
                s.WriteByte((byte)value);
            }

            private static void WriteLengthDelimitedString(Stream s, string str)
            {
                var bytes = Encoding.UTF8.GetBytes(str);
                WriteVarintRaw(s, (ulong)bytes.Length);
                s.Write(bytes, 0, bytes.Length);
            }

            private static void WriteLengthDelimitedBytes(Stream s, byte[] bytes)
            {
                WriteVarintRaw(s, (ulong)bytes.Length);
                s.Write(bytes, 0, bytes.Length);
            }
        }
    }
}
