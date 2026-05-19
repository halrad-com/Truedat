using System;
using System.IO;
using System.Text;

namespace Truedat
{
    /// <summary>
    /// In-tree ONNX byte builder for development skeleton models.
    ///
    /// Used by VocalAffectStage (S2.2) and VadStage (S2.3) to bootstrap a
    /// loadable model file at first run when the real model file is missing.
    /// Emits a minimal Identity ModelProto (~130 B): one float32 input with
    /// shape [1, T] (T as a symbolic dim_param), one matching output. Adapted
    /// from tools/ort-spike/Program.cs TinyOnnx.
    ///
    /// This whole file goes away when the real SER and Silero VAD model files
    /// ship — there's no production reason for an in-tree ONNX builder once
    /// the bootstrap path is gone. Keep the surface small to make that
    /// deletion painless.
    /// </summary>
    internal static class SkeletonOnnx
    {
        /// <summary>Build a single-input single-output Identity ONNX. Input
        /// tensor shape is [1, T] float32; output is [1, T] float32 echoing
        /// the input. Caller supplies the I/O names so VAM and VAD skeletons
        /// can have distinct names matching their eventual real-model
        /// contracts.</summary>
        public static byte[] BuildIdentityModel(string inputName, string outputName, string graphName)
        {
            if (inputName == null) throw new ArgumentNullException(nameof(inputName));
            if (outputName == null) throw new ArgumentNullException(nameof(outputName));
            if (graphName == null) throw new ArgumentNullException(nameof(graphName));

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
            var vinIn  = makeValueInfo(inputName);
            var vinOut = makeValueInfo(outputName);

            // NodeProto: input[0]=inputName (field 1, repeated string),
            //            output[0]=outputName (field 2, repeated string),
            //            op_type="Identity" (field 4)
            var node = WriteMessage(w =>
            {
                WriteTag(w, 1, 2); WriteLengthDelimitedString(w, inputName);
                WriteTag(w, 2, 2); WriteLengthDelimitedString(w, outputName);
                WriteTag(w, 4, 2); WriteLengthDelimitedString(w, "Identity");
            });

            // GraphProto: node (1, repeated message), name (2, string),
            //             input (11, repeated message), output (12, repeated message)
            var graph = WriteMessage(w =>
            {
                WriteTag(w, 1, 2);  WriteLengthDelimitedBytes(w, node);
                WriteTag(w, 2, 2);  WriteLengthDelimitedString(w, graphName);
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
                WriteTag(w, 2, 2); WriteLengthDelimitedString(w, "truedat-" + graphName);
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
