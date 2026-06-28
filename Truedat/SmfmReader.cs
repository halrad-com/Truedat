using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Truedat
{
    /// <summary>Reads Sony SMFM (12-TONE analysis) payloads embedded directly into audio files
    /// by Music Center. Supports FLAC (APPLICATION block), MP3 (GEOB frame), and WMA (ASF ECD).
    /// All methods are static and thread-safe. Never throws — returns null on any failure.</summary>
    internal static class SmfmReader
    {
        internal struct SmfmResult
        {
            public int[] Scores;   // STMO slot scores 0-255, length 10
            public int   Channel;  // argmax of Scores — dominant raw STMO slot index, NOT a mood channel
            public float Bpm;      // GBPM big-endian float32
        }

        // ── SMFM slot "channel" labels: REMOVED (device-refuted) ─────────────
        // An earlier hardcoded slot->name table ("Energetic", "Lounge", …) is
        // REFUTED and no longer emitted. A 2026-06-27 live Walkman device test
        // (52 tracks) proved the device's mood channels are REGIONS on a 2-D
        // arousal×valence canvas a track lands in SEVERAL of at once — they are
        // NOT 1:1 with STMO slots, and STMO argmax does NOT predict the device
        // channel. Specific refutations: ch2 ≠ "Emotional" (not a device channel
        // at all), ch6 ≠ "Extreme" (those were junk 254-valued LIVE recordings).
        // The labels were geometry/hand-audit guesses, never device-validated.
        //   Evidence (smfm repo): docs/2026-06-27-device-test-rederivation.md,
        //   docs/2026-06-27-canvas-rederivation-round2.md (+ the schema-v4 channel record).
        // The meaningful derived signal is (arousal, valence), computed DOWNSTREAM
        // (AutoQ / SmfmVaProjector), NOT here. truedat emits only the raw Scores +
        // Bpm + the dominant raw STMO slot index (SmfmResult.Channel).

        /// <summary>Slot "channel" names are device-refuted (slots ≠ mood channels) and no
        /// longer emitted — always returns null. Kept for call-site/back-compat; the property
        /// it feeds is omit-when-null. See the comment block above for the device-test finding.</summary>
        internal static string? ChannelName(int index) => null;

        /// <summary>Returns null if the file has no SMFM payload or parse fails.
        /// Header-only read — never decodes audio.</summary>
        internal static SmfmResult? TryRead(string path)
        {
            try
            {
                var ext = Path.GetExtension(path);
                if (string.IsNullOrEmpty(ext)) return null;

                byte[]? payload = null;
                switch (ext.ToLowerInvariant())
                {
                    case ".flac": payload = ReadFlac(path); break;
                    case ".mp3":  payload = ReadMp3(path);  break;
                    case ".wma":  payload = ReadWma(path);  break;
                    default:      return null;
                }
                if (payload == null) return null;

                var blocks = ParseSubBlocks(payload);

                if (!blocks.TryGetValue("GBPM", out var gbpmBytes)) return null;
                float bpm = DecodeGbpm(gbpmBytes);
                if (bpm <= 0) return null;

                if (!blocks.TryGetValue("STMO", out var stmoBytes)) return null;
                var scores = DecodeStmo(stmoBytes);
                if (scores == null) return null;

                int channel = 0;
                for (int i = 1; i < scores.Length; i++)
                    if (scores[i] > scores[channel]) channel = i;

                return new SmfmResult { Scores = scores, Channel = channel, Bpm = bpm };
            }
            catch
            {
                return null;
            }
        }

        // ── Container readers ─────────────────────────────────────────────────

        static byte[]? ReadFlac(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
            var buf = new byte[4];
            if (fs.Read(buf, 0, 4) < 4) return null;
            if (buf[0] != 'f' || buf[1] != 'L' || buf[2] != 'a' || buf[3] != 'C') return null;

            var hdr = new byte[4];
            while (true)
            {
                if (fs.Read(hdr, 0, 4) < 4) return null;
                bool last  = (hdr[0] & 0x80) != 0;
                int  btype = hdr[0] & 0x7F;
                int  blen  = (hdr[1] << 16) | (hdr[2] << 8) | hdr[3];
                var  data  = new byte[blen];
                if (blen > 0 && fs.Read(data, 0, blen) < blen) return null;
                if (btype == 2 && blen >= 4
                    && data[0] == 'S' && data[1] == 'M' && data[2] == 'F' && data[3] == 'M')
                {
                    var result = new byte[blen - 4];
                    Buffer.BlockCopy(data, 4, result, 0, result.Length);
                    return result;
                }
                if (last) return null;
            }
        }

        static byte[]? ReadMp3(string path)
        {
            byte[] tagData;
            bool syncsafeFrames;

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096))
            {
                var hdr = new byte[10];
                if (fs.Read(hdr, 0, 10) < 10) return null;
                if (hdr[0] != 'I' || hdr[1] != 'D' || hdr[2] != '3') return null;
                int version = hdr[3];
                if (version != 3 && version != 4) return null;
                syncsafeFrames = (version == 4);
                // Tag size is a syncsafe int28 (7 bits per byte)
                int tagSize = (hdr[6] << 21) | (hdr[7] << 14) | (hdr[8] << 7) | hdr[9];
                if (tagSize <= 0 || tagSize > 64 * 1024 * 1024) return null;
                tagData = new byte[tagSize];
                int read = 0;
                while (read < tagSize)
                {
                    int n = fs.Read(tagData, read, tagSize - read);
                    if (n <= 0) break;
                    read += n;
                }
            }

            int p = 0;
            while (p + 10 <= tagData.Length)
            {
                if (tagData[p] == 0) break; // padding
                int fsize = syncsafeFrames
                    ? (tagData[p + 4] << 21) | (tagData[p + 5] << 14) | (tagData[p + 6] << 7) | tagData[p + 7]
                    : (tagData[p + 4] << 24) | (tagData[p + 5] << 16) | (tagData[p + 6] << 8) | tagData[p + 7];
                if (fsize <= 0 || p + 10 + fsize > tagData.Length) break;

                if (tagData[p] == 'G' && tagData[p + 1] == 'E' && tagData[p + 2] == 'O' && tagData[p + 3] == 'B')
                {
                    var result = ExtractGeobObject(tagData, p + 10, fsize);
                    if (result != null) return result;
                }
                p += 10 + fsize;
            }
            return null;
        }

        static byte[]? ExtractGeobObject(byte[] arr, int start, int count)
        {
            int end = start + count;
            if (start + 1 >= end) return null;

            // Layout: encoding(1) + mime\0 + filename\0 + description\0 + object
            int pos = start + 1; // skip encoding byte

            int mimeEnd = IndexOf(arr, 0, pos, end);
            if (mimeEnd < 0) return null;
            // Verify mime == "application/SMFMF"
            if (!AsciiEquals(arr, pos, mimeEnd - pos, "application/SMFMF")) return null;
            pos = mimeEnd + 1;

            int fnEnd = IndexOf(arr, 0, pos, end);
            if (fnEnd < 0) return null;
            pos = fnEnd + 1;

            int descEnd = IndexOf(arr, 0, pos, end);
            if (descEnd < 0) return null;
            pos = descEnd + 1;

            if (pos >= end) return null;
            var result = new byte[end - pos];
            Buffer.BlockCopy(arr, pos, result, 0, result.Length);
            return result;
        }

        // ASF Header Object GUID: {75B22630-668E-11CF-A6D9-00AA0062CE6C} in bytes_le
        private static readonly byte[] _asfHeaderGuid = {
            0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
            0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C
        };
        // ASF Extended Content Description Object GUID: {D2D0A440-E307-11D2-97F0-00A0C95EA850} in bytes_le
        private static readonly byte[] _asfEcdGuid = {
            0x40, 0xA4, 0xD0, 0xD2, 0x07, 0xE3, 0xD2, 0x11,
            0x97, 0xF0, 0x00, 0xA0, 0xC9, 0x5E, 0xA8, 0x50
        };

        static byte[]? ReadWma(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
            var guid = new byte[16];
            if (fs.Read(guid, 0, 16) < 16 || !GuidEquals(guid, _asfHeaderGuid)) return null;

            var buf8 = new byte[8];
            var buf4 = new byte[4];
            var buf2 = new byte[2];
            if (fs.Read(buf8, 0, 8) < 8) return null; // header_size (unused — we walk by object)
            if (fs.Read(buf4, 0, 4) < 4) return null;
            int numObjects = BitConverter.ToInt32(buf4, 0);
            if (fs.Read(buf2, 0, 2) < 2) return null; // reserved

            var objGuid = new byte[16];
            for (int i = 0; i < numObjects; i++)
            {
                if (fs.Read(objGuid, 0, 16) < 16) return null;
                if (fs.Read(buf8, 0, 8) < 8) return null;
                long objSize  = BitConverter.ToInt64(buf8, 0);
                long dataSize = objSize - 24;
                if (dataSize < 0) return null;

                if (!GuidEquals(objGuid, _asfEcdGuid))
                {
                    fs.Seek(dataSize, SeekOrigin.Current);
                    continue;
                }
                if (dataSize > 10 * 1024 * 1024) return null; // sanity cap

                var data = new byte[dataSize];
                int read = 0;
                while (read < data.Length)
                {
                    int n = fs.Read(data, read, data.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                return ParseEcdObject(data);
            }
            return null;
        }

        static byte[]? ParseEcdObject(byte[] data)
        {
            if (data.Length < 2) return null;
            int p = 0;
            int count = (data[p + 1] << 8) | data[p]; p += 2; // LE uint16

            for (int i = 0; i < count; i++)
            {
                if (p + 2 > data.Length) return null;
                int nameLen = (data[p + 1] << 8) | data[p]; p += 2;
                if (p + nameLen > data.Length) return null;
                var name = Encoding.Unicode.GetString(data, p, nameLen).TrimEnd('\0');
                p += nameLen;
                if (p + 4 > data.Length) return null;
                int valType = (data[p + 1] << 8) | data[p]; p += 2;
                int valLen  = (data[p + 1] << 8) | data[p]; p += 2;
                if (p + valLen > data.Length) return null;
                if (name == "SMFMF" && valType == 1) // type 1 = byte array
                {
                    var result = new byte[valLen];
                    Buffer.BlockCopy(data, p, result, 0, valLen);
                    return result;
                }
                p += valLen;
            }
            return null;
        }

        // ── Sub-block parser ─────────────────────────────────────────────────

        // Both magic variants: PC firmware vs Walkman device firmware
        private static readonly byte[][] _knownMagics = {
            new byte[] { (byte)'S',(byte)'T',(byte)'A',(byte)'E',(byte)'M',(byte)'M',(byte)'L',(byte)'W' },
            new byte[] { (byte)'S',(byte)'T',(byte)'A',(byte)'E',(byte)'S',(byte)'M',(byte)'M',(byte)'L' },
        };

        static Dictionary<string, byte[]> ParseSubBlocks(byte[] payload)
        {
            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            int p = 0;
            while (p + 20 <= payload.Length)
            {
                var tag = Encoding.ASCII.GetString(payload, p, 4);

                bool validMagic = false;
                foreach (var m in _knownMagics)
                {
                    bool match = true;
                    for (int j = 0; j < 8; j++)
                        if (payload[p + 4 + j] != m[j]) { match = false; break; }
                    if (match) { validMagic = true; break; }
                }
                if (!validMagic) break;

                // Length at p+16, big-endian uint32
                int plen = (payload[p + 16] << 24) | (payload[p + 17] << 16)
                         | (payload[p + 18] << 8)  |  payload[p + 19];
                if (p + 20 + plen > payload.Length) break;

                var blockData = new byte[plen];
                Buffer.BlockCopy(payload, p + 20, blockData, 0, plen);
                result[tag] = blockData;
                p += 20 + plen;
            }
            return result;
        }

        // ── Sub-block decoders ────────────────────────────────────────────────

        static float DecodeGbpm(byte[] b)
        {
            if (b.Length != 4) return 0;
            // Big-endian float32 → reverse bytes for BitConverter (little-endian host)
            return BitConverter.ToSingle(new byte[] { b[3], b[2], b[1], b[0] }, 0);
        }

        static int[]? DecodeStmo(byte[] b)
        {
            if (b.Length != 160) return null;
            // 40 records × 4 bytes: [type, channel, sub_index, value]
            // Average the 4 sub-index values per channel
            var sums   = new int[10];
            var counts = new int[10];
            for (int i = 0; i < 160; i += 4)
            {
                int ch  = b[i + 1];
                int val = b[i + 3];
                if (ch < 10) { sums[ch] += val; counts[ch]++; }
            }
            var scores = new int[10];
            for (int ch = 0; ch < 10; ch++)
                if (counts[ch] > 0)
                    scores[ch] = (int)Math.Round((double)sums[ch] / counts[ch]);
            return scores;
        }

        // ── Utility ──────────────────────────────────────────────────────────

        static bool GuidEquals(byte[] a, byte[] b)
        {
            for (int i = 0; i < 16; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        static int IndexOf(byte[] arr, byte value, int start, int end)
        {
            for (int i = start; i < end; i++)
                if (arr[i] == value) return i;
            return -1;
        }

        static bool AsciiEquals(byte[] arr, int start, int len, string s)
        {
            if (len != s.Length) return false;
            for (int i = 0; i < len; i++)
                if (arr[start + i] != (byte)s[i]) return false;
            return true;
        }
    }
}
