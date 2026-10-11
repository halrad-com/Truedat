using System;
using System.Security.Cryptography;

namespace Truedat.Store
{
    /// <summary>RFC 9562 version-7 UUIDs as canonical lowercase text. Time-ordered: the first 48 bits are
    /// unix milliseconds and the 12-bit rand_a field is a per-millisecond counter, so ids minted
    /// back-to-back sort in minting order as plain strings — which is what makes a UUID-ordered export
    /// append-mostly (spec §3). Formatted here rather than through System.Guid, whose ToString()
    /// byte-swaps the first three groups and would scramble that ordering.</summary>
    internal static class Uuid7
    {
        static readonly object Gate = new object();
        static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();
        static long _lastMs;
        static int _seq;

        internal static string New()
        {
            var b = new byte[16];
            long ms; int seq;
            lock (Gate)
            {
                Rng.GetBytes(b, 8, 8);
                ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (ms <= _lastMs)
                {
                    // Same (or earlier — clock step) millisecond: keep the last timestamp and count up.
                    ms = _lastMs;
                    if (++_seq > 0xFFF) { _seq = 0; ms = ++_lastMs; }
                }
                else
                {
                    _lastMs = ms;
                    _seq = b[8] & 0x7F;   // random start, with headroom for thousands per ms
                }
                seq = _seq;
            }
            b[0] = (byte)(ms >> 40); b[1] = (byte)(ms >> 32); b[2] = (byte)(ms >> 24);
            b[3] = (byte)(ms >> 16); b[4] = (byte)(ms >> 8); b[5] = (byte)ms;
            b[6] = (byte)(0x70 | ((seq >> 8) & 0x0F));
            b[7] = (byte)(seq & 0xFF);
            b[8] = (byte)(0x80 | (b[8] & 0x3F));   // variant 10xx

            var c = new char[36];
            int ci = 0;
            for (int i = 0; i < 16; i++)
            {
                if (i == 4 || i == 6 || i == 8 || i == 10) c[ci++] = '-';
                c[ci++] = Hex[b[i] >> 4];
                c[ci++] = Hex[b[i] & 0xF];
            }
            return new string(c);
        }

        static readonly char[] Hex = "0123456789abcdef".ToCharArray();
    }
}
