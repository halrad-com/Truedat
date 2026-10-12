using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Truedat.Edges
{
    internal enum EspbType : uint { F32Scalar = 1, F32Vector = 2, F32Matrix = 3, String = 4, StringVector = 5 }

    internal sealed class EspbDescriptor
    {
        public string Name = ""; public EspbType Type; public uint Flags; public uint Count; public uint Dims; public long Offset; public long Length;
        public bool Single => (Flags & 1) != 0;
    }

    /// <summary>Reader for ESPB v1, the extractor .5's binary frames file (contract: essentia-build/FRAMES-BINARY-FORMAT.md,
    /// written by BinaryPoolOutput). Header, tag and descriptor table are read once and validated (magic, version, byte
    /// order, table ending exactly at payloadOffset); a payload is read on demand by name as one block copy. Everything is
    /// little-endian; Real is float32; strings are u32-length-prefixed UTF-8. A malformed file is an
    /// <see cref="InvalidOperationException"/> at open, never a silent partial read.</summary>
    internal sealed class EspbFile : IDisposable
    {
        readonly FileStream _fs;
        readonly Dictionary<string, EspbDescriptor> _table = new Dictionary<string, EspbDescriptor>(StringComparer.Ordinal);

        internal string Tag { get; private set; } = "";
        internal IReadOnlyDictionary<string, EspbDescriptor> Descriptors => _table;

        EspbFile(FileStream fs) { _fs = fs; }

        internal static EspbFile Open(string path)
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            var f = new EspbFile(fs);
            try { f.ReadHeader(); return f; }
            catch { fs.Dispose(); throw; }
        }

        void ReadHeader()
        {
            var r = new BinaryReader(_fs, Encoding.UTF8, leaveOpen: true);
            if (_fs.Length < 24) throw new InvalidOperationException("ESPB: file shorter than its header");
            var magic = r.ReadBytes(4);
            if (magic[0] != 'E' || magic[1] != 'S' || magic[2] != 'P' || magic[3] != 'B') throw new InvalidOperationException("ESPB: bad magic");
            uint version = r.ReadUInt32();
            if (version != 1) throw new InvalidOperationException($"ESPB: format version {version} is not 1");
            uint bom = r.ReadUInt32();
            if (bom != 0x04030201u) throw new InvalidOperationException($"ESPB: byte order mark 0x{bom:X8} is not little-endian");
            uint count = r.ReadUInt32();
            long payloadOffset = (long)r.ReadUInt64();
            Tag = ReadString(r);
            for (uint i = 0; i < count; i++)
            {
                var d = new EspbDescriptor
                {
                    Name = ReadString(r), Type = (EspbType)r.ReadUInt32(), Flags = r.ReadUInt32(), Count = r.ReadUInt32(), Dims = r.ReadUInt32(),
                    Offset = (long)r.ReadUInt64(), Length = (long)r.ReadUInt64(),
                };
                if (d.Offset < payloadOffset || d.Offset + d.Length > _fs.Length) throw new InvalidOperationException($"ESPB: descriptor '{d.Name}' points outside the file");
                _table[d.Name] = d;
            }
            if (_fs.Position != payloadOffset) throw new InvalidOperationException($"ESPB: descriptor table ends at {_fs.Position}, payloadOffset says {payloadOffset}");
        }

        static string ReadString(BinaryReader r)
        {
            uint len = r.ReadUInt32();
            if (len > 1 << 24) throw new InvalidOperationException("ESPB: string length is implausible");
            return len == 0 ? "" : Encoding.UTF8.GetString(r.ReadBytes((int)len));
        }

        byte[] Payload(EspbDescriptor d)
        {
            var buf = new byte[d.Length];
            _fs.Position = d.Offset;
            int got = 0;
            while (got < buf.Length) { int n = _fs.Read(buf, got, buf.Length - got); if (n <= 0) throw new InvalidOperationException($"ESPB: short read on '{d.Name}'"); got += n; }
            return buf;
        }

        /// <summary>A float32 descriptor's payload as one array (count × dims, row-major for a matrix), with its dims.
        /// False for an absent name or a non-float type — a fold declares what it reads and fails its check, never substitutes.</summary>
        internal bool TryGetF32(string name, out float[] values, out int dims)
        {
            values = Array.Empty<float>(); dims = 0;
            if (!_table.TryGetValue(name, out var d)) return false;
            if (d.Type != EspbType.F32Scalar && d.Type != EspbType.F32Vector && d.Type != EspbType.F32Matrix) return false;
            if (d.Length != 4L * d.Count * Math.Max(1u, d.Dims)) return false;
            var bytes = Payload(d);
            values = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            dims = (int)Math.Max(1u, d.Dims);
            return true;
        }

        internal bool TryGetStrings(string name, out string[] values)
        {
            values = Array.Empty<string>();
            if (!_table.TryGetValue(name, out var d)) return false;
            if (d.Type != EspbType.String && d.Type != EspbType.StringVector) return false;
            var bytes = Payload(d);
            var list = new List<string>((int)d.Count);
            int p = 0;
            while (p + 4 <= bytes.Length)
            {
                int len = BitConverter.ToInt32(bytes, p); p += 4;
                if (len < 0 || p + len > bytes.Length) return false;
                list.Add(Encoding.UTF8.GetString(bytes, p, len)); p += len;
            }
            values = list.ToArray();
            return true;
        }

        public void Dispose() => _fs.Dispose();
    }
}
