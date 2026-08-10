using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Truedat
{
    /// <summary>
    /// Writes the <c>.mbxs</c> binary catalog sidecar beside <c>mbxmoods.json</c> on every save.
    /// It is a pre-compiled projection of the AutoQ scoring hot-path fields: MBXHub's MoodCache
    /// loads it ~orders of magnitude faster than parsing the JSON (labs measured ~9 ms vs ~877 ms
    /// on a 238 MB catalog, ~0.10 µs random lookup), version-checks it, and falls back to the JSON
    /// when it is absent or stale. The JSON stays the canonical, human-readable source of truth;
    /// the sidecar is a rebuildable derived cache. truedat owns the WRITER; MBXHub owns the reader.
    ///
    /// ============================ .mbxs FORMAT v1 (little-endian) ============================
    /// Header (16 bytes):
    ///   char[4]  magic       = "MBXS"
    ///   u32      version     = 1
    ///   u32      trackCount  = N
    ///   u32      chromaFloats= 24            (hpcp12[12] ++ thpcp12[12])
    ///
    /// Rec[N]  — fixed 22-byte stride, SORTED ASCENDING by PathHash (enables the reader's
    ///           O(log N) binary-search random access without parsing anything):
    ///   i64  PathHash    FNV-1a 64-bit over the UTF-16 chars of the catalog key (the file path,
    ///                    exactly the string used as the JSON "tracks" key). See FnvPathHash.
    ///   f32  Bpm         rhythm.bpm            (NaN if absent)
    ///   f32  DynCx       dynamicComplexity     (NaN if absent)
    ///   i32  KeyCode     stable key+mode code, 0..23 = pitchClass(0..11)*2 + (minor?1:0); -1 when
    ///                    key/mode is absent or unrecognized. (This replaces the labs prototype's
    ///                    string.GetHashCode(), which is NOT portable across .NET Framework/.NET
    ///                    Core/process — a hash is neither stable nor decodable. This code is both.)
    ///   u8   HasHpcp     1 if this row's hpcp12 chroma is real, 0 if the 12 floats are NaN filler
    ///   u8   HasThpcp    1 if this row's thpcp12 chroma is real, 0 otherwise
    ///
    /// Chroma  — f32[N * 24], row i (same sorted order as Rec[i]) = hpcp12[0..11] ++ thpcp12[0..11].
    ///           Missing vectors are written as 12 NaN floats and flagged via HasHpcp/HasThpcp.
    ///
    /// Path table — (u16 byteLen + UTF-8 bytes)[N], same sorted order, for display/reconstruction.
    ///           byteLen is clamped to 65535 (paths are never that long in practice).
    /// ========================================================================================
    ///
    /// Widen Rec deliberately (bump <c>version</c>) if MoodCache needs more scoring fields — the
    /// v1 set is the picker-critical subset. Any layout change is a cross-repo contract change
    /// with MBXHub; coordinate before shipping it.
    /// </summary>
    internal static class CatalogSidecar
    {
        internal const int ChromaFloats = 24;   // hpcp12[12] + thpcp12[12]
        internal const uint FormatVersion = 1;

        /// <summary>FNV-1a 64-bit over the string's UTF-16 chars. Matches the reader; used both as
        /// the Rec sort key and the random-access lookup key. Char-based (not byte-based) on purpose
        /// so writer and reader agree without a shared encoding assumption.</summary>
        internal static long FnvPathHash(string s)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
                return (long)h;
            }
        }

        // Sharp and flat spellings -> pitch class 0..11 (C=0). Essentia's key profiles emit sharps;
        // flats are accepted too so a re-tag or a different profile can't silently fall to -1.
        private static readonly Dictionary<string, int> PitchClasses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            {"C",0},{"C#",1},{"Db",1},{"D",2},{"D#",3},{"Eb",3},{"E",4},{"F",5},
            {"F#",6},{"Gb",6},{"G",7},{"G#",8},{"Ab",8},{"A",9},{"A#",10},{"Bb",10},{"B",11},
        };

        /// <summary>Stable, decodable key code: pitchClass(0..11)*2 + (minor?1:0) -> 0..23; -1 when
        /// the key is absent/unrecognized. Never uses GetHashCode (non-portable across runtimes).</summary>
        internal static int KeyCode(string key, string mode)
        {
            if (string.IsNullOrEmpty(key) || !PitchClasses.TryGetValue(key.Trim(), out var pc)) return -1;
            bool minor = string.Equals(mode?.Trim(), "minor", StringComparison.OrdinalIgnoreCase);
            return pc * 2 + (minor ? 1 : 0);
        }

        /// <summary>Build and atomically write the sidecar for the whole catalog. Best-effort:
        /// throws only to its caller's try/catch — a sidecar failure must never fail a JSON save,
        /// because the sidecar is a rebuildable cache and the reader falls back to JSON.</summary>
        internal static void Write(string sidecarPath, ICollection<KeyValuePair<string, TrackEntry>> tracks)
        {
            int n = tracks.Count;
            var pathHash = new long[n];
            var bpm = new float[n];
            var dyncx = new float[n];
            var keyCode = new int[n];
            var hasHpcp = new byte[n];
            var hasThpcp = new byte[n];
            var chroma = new float[n * ChromaFloats];
            var paths = new string[n];

            int idx = 0;
            foreach (var kv in tracks)
            {
                var f = kv.Value?.Features;
                paths[idx] = kv.Key;
                pathHash[idx] = FnvPathHash(kv.Key);
                bpm[idx] = f != null ? (float)f.Bpm : float.NaN;
                dyncx[idx] = f?.DynamicComplexity is double d ? (float)d : float.NaN;
                keyCode[idx] = f != null ? KeyCode(f.Key, f.Mode) : -1;
                int baseOff = idx * ChromaFloats;
                FillChroma(chroma, baseOff, f?.Hpcp12, out bool h);
                FillChroma(chroma, baseOff + 12, f?.Thpcp12, out bool th);
                hasHpcp[idx] = (byte)(h ? 1 : 0);
                hasThpcp[idx] = (byte)(th ? 1 : 0);
                idx++;
            }

            // Sort every column in lockstep by PathHash ascending (binary-search random access).
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, (a, b) => pathHash[a].CompareTo(pathHash[b]));

            var tmp = sidecarPath + ".tmp";
            try { File.Delete(tmp); } catch { }
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(Encoding.ASCII.GetBytes("MBXS"));
                bw.Write(FormatVersion);
                bw.Write((uint)n);
                bw.Write((uint)ChromaFloats);
                foreach (var o in order)
                {
                    bw.Write(pathHash[o]);
                    bw.Write(bpm[o]);
                    bw.Write(dyncx[o]);
                    bw.Write(keyCode[o]);
                    bw.Write(hasHpcp[o]);
                    bw.Write(hasThpcp[o]);
                }
                foreach (var o in order)
                {
                    int b = o * ChromaFloats;
                    for (int j = 0; j < ChromaFloats; j++) bw.Write(chroma[b + j]);
                }
                foreach (var o in order)
                {
                    var pb = Encoding.UTF8.GetBytes(paths[o]);
                    int len = Math.Min(pb.Length, 65535);
                    bw.Write((ushort)len);
                    bw.Write(pb, 0, len);
                }
                bw.Flush();
                fs.Flush(true);   // fsync: the sidecar is rebuildable, but don't leave a torn file
            }
            Program.AtomicReplace(tmp, sidecarPath, null);
        }

        private static void FillChroma(float[] dst, int off, double[]? vec, out bool present)
        {
            if (vec != null && vec.Length == 12)
            {
                for (int i = 0; i < 12; i++) dst[off + i] = (float)vec[i];
                present = true;
            }
            else
            {
                for (int i = 0; i < 12; i++) dst[off + i] = float.NaN;
                present = false;
            }
        }

        /// <summary>Parsed sidecar, in stored (PathHash-sorted) order. Reference reader for the
        /// self-test and for any truedat-side diagnostics; MBXHub ships its own MoodCache reader.</summary>
        internal sealed class SidecarData
        {
            public int Version, TrackCount;
            public long[] PathHash = Array.Empty<long>();
            public float[] Bpm = Array.Empty<float>();
            public float[] DynCx = Array.Empty<float>();
            public int[] KeyCode = Array.Empty<int>();
            public byte[] HasHpcp = Array.Empty<byte>();
            public byte[] HasThpcp = Array.Empty<byte>();
            public float[] Chroma = Array.Empty<float>();   // TrackCount * 24
            public string[] Paths = Array.Empty<string>();

            /// <summary>O(log N) lookup by path -> row index, or -1. Mirrors the reader's hot path.</summary>
            public int IndexOf(string path)
            {
                long target = FnvPathHash(path);
                int lo = 0, hi = TrackCount - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    long v = PathHash[mid];
                    if (v == target) return mid;
                    if (v < target) lo = mid + 1; else hi = mid - 1;
                }
                return -1;
            }
        }

        internal static SidecarData Read(string sidecarPath)
        {
            using (var fs = new FileStream(sidecarPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var br = new BinaryReader(fs))
            {
                var magic = Encoding.ASCII.GetString(br.ReadBytes(4));
                if (magic != "MBXS") throw new InvalidDataException($"not a .mbxs file (magic '{magic}')");
                var d = new SidecarData { Version = (int)br.ReadUInt32() };
                int n = (int)br.ReadUInt32();
                int chromaFloats = (int)br.ReadUInt32();
                if (chromaFloats != ChromaFloats) throw new InvalidDataException($"unexpected chromaFloats {chromaFloats}");
                d.TrackCount = n;
                d.PathHash = new long[n]; d.Bpm = new float[n]; d.DynCx = new float[n];
                d.KeyCode = new int[n]; d.HasHpcp = new byte[n]; d.HasThpcp = new byte[n];
                for (int i = 0; i < n; i++)
                {
                    d.PathHash[i] = br.ReadInt64();
                    d.Bpm[i] = br.ReadSingle();
                    d.DynCx[i] = br.ReadSingle();
                    d.KeyCode[i] = br.ReadInt32();
                    d.HasHpcp[i] = br.ReadByte();
                    d.HasThpcp[i] = br.ReadByte();
                }
                d.Chroma = new float[n * ChromaFloats];
                for (int i = 0; i < d.Chroma.Length; i++) d.Chroma[i] = br.ReadSingle();
                d.Paths = new string[n];
                for (int i = 0; i < n; i++)
                {
                    int len = br.ReadUInt16();
                    d.Paths[i] = Encoding.UTF8.GetString(br.ReadBytes(len));
                }
                return d;
            }
        }
    }
}
