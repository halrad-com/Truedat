using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Truedat.Store;

namespace Truedat.Edges
{
    /// <summary>S2 C4 — the ruled row in the store. Per track: 2 <c>segment</c> rows (head and tail, <c>set_key</c> = the
    /// producer version), ONE packed <c>value</c> row per edge (<c>edge.row</c>, scalars + series + the edge's summary + its
    /// three sub-window summaries), plus three whole-track rows (status, duration, head-vs-tail divergence). Keyed by
    /// content hash so a second copy of the same audio costs nothing.
    ///
    /// Why packed, not one row per field — measured 2026-10-12 in a vacuumed store: one value row per field (131 rows)
    /// cost 43 KB/track; one packed row per segment with 8 segments (sub-windows as rows) cost 11.5 KB; the plan's budget
    /// is 6.6 KB. Every row repeats the 64-char hash, the kind, the field and the version in the row and again in its
    /// primary-key index, ~270 bytes before any payload, so the row COUNT is the cost. The operator's ruling is a 6 KB edge
    /// row, so the sub-windows ride inside the edge blob (their bounds are the edge's thirds) and a track is 5 rows.
    ///
    /// Blob layout <c>edge-row v1</c>, little-endian, written by <see cref="PackEdge"/>:
    /// <code>
    ///   edge.row:     'E' 'D' 'G' 0x01, u8 status (1 ok, 2 short-context), 14 x f32 scalars (NaN = null):
    ///                 bpm.value, bpm.beats, bpm.regularity, loudness.startLufs, endLufs, meanLufs, fadeMs, silenceMs,
    ///                 silenceSource (0 ebu, 1 pcm), rms.crestDb, rms.transitionMs, divergence.loudness, texture, harmony;
    ///                 u16 n + n x int16 momentary centi-dB; u16 n + n x int16 short-term centi-dB;
    ///                 u16 n + n x int32 beat ms + n x f32 beat loudness;
    ///                 the edge's summary block; u8 subCount; subCount x (i32 startMs, i32 endMs, summary block).
    ///   summary block: 36 x f32 hpcp mean, 13 x f32 mfcc mean, 13 x f32 mfcc spread, 11 x f32 spectral mean,
    ///                 11 x f32 spectral spread, u8 keyPresent, [u8-len name, u8-len scale, u8-len code, f32 strength,
    ///                 u8 agreement, u8 votesPresent].
    /// </code>
    /// Rows are upserted: the same producer version rewrites itself, a different version (a settings change) coexists. An
    /// unavailable track writes no edge rows, only the whole-track status; a failed edge writes nothing at all (constraint
    /// 2 — no partial row) and the caller records the failed run.</summary>
    internal static class EdgeStore
    {
        internal const string ProducerKey = "edges";
        internal const string EdgeField = "edge.row";
        const long WholeTrack = 0;
        static readonly byte[] EdgeMagic = { (byte)'E', (byte)'D', (byte)'G', 1 };

        /// <summary>Returns the number of value rows written (0 for a failed result).</summary>
        internal static int Write(StoreDal dal, string contentKey, string contentKeyKind, long? runId, EdgeFoldResult r)
        {
            if (r.Status == "failed") return 0;
            dal.EnsureProducer(ProducerKey, r.ProducerVersion, "analysis", needsAudio: true);
            int n = 0;
            void Put(long seg, string field, double? num, string? txt, byte[]? vec, long? dims, string? unit)
            { dal.UpsertValue(contentKey, contentKeyKind, seg, field, ProducerKey, r.ProducerVersion, runId, num, txt, vec, dims, unit); n++; }
            Put(WholeTrack, "edges.status", null, r.Status, null, null, null);
            Put(WholeTrack, "edges.durationMs", r.DurationMs, null, null, null, null);
            if (!string.IsNullOrEmpty(r.Extractor)) Put(WholeTrack, "edges.extractor", null, r.Extractor, null, null, null);   // the frames depend on the build (review M4)
            if (r.Status == "unavailable") return n;
            if (r.HeadTail != null)
            {
                var d = r.HeadTail;
                Put(WholeTrack, "edges.headTail", null, null, F32Bytes(new[] { (float)(d.Loudness ?? double.NaN), (float)(d.Texture ?? double.NaN), (float)(d.Harmony ?? double.NaN) }), 3, "loudness,texture,harmony");
            }
            foreach (var e in new[] { r.Head, r.Tail })
            {
                long seg = dal.UpsertSegment(contentKey, contentKeyKind, r.ProducerVersion, e.Kind, 0, e.StartMs, e.EndMs);
                var blob = PackEdge(e);
                Put(seg, EdgeField, null, null, blob, blob.Length, "edge-row/v1");
            }
            return n;
        }

        internal static EdgeFoldResult? Read(StoreDal dal, string contentKey, string producerVersion)
        {
            var whole = dal.ReadValues(contentKey, WholeTrack, ProducerKey, producerVersion);
            if (!whole.TryGetValue("edges.status", out var status) || status.Txt == null) return null;
            var r = new EdgeFoldResult { ProducerVersion = producerVersion, DurationMs = (int)(whole.TryGetValue("edges.durationMs", out var dm) && dm.Num.HasValue ? dm.Num.Value : 0) };
            if (whole.TryGetValue("edges.extractor", out var ex) && ex.Txt != null) r.Extractor = ex.Txt;
            if (whole.TryGetValue("edges.headTail", out var ht) && ht.Vec != null && ht.Vec.Length == 12)
            {
                var f = F32s(ht.Vec);
                r.HeadTail = new EdgeDivergence { Loudness = Opt(f[0]), Texture = Opt(f[1]), Harmony = Opt(f[2]) };
            }
            if (status.Txt == "unavailable")
            {
                r.Head.Status = r.Tail.Status = "unavailable"; r.Head.EndMs = r.Tail.StartMs = (int)Math.Round(r.DurationMs / 2.0); r.Tail.EndMs = r.DurationMs;
                return r;
            }
            foreach (var s in dal.ReadSegments(contentKey, producerVersion))
            {
                var row = s.Kind == "head" ? r.Head : s.Kind == "tail" ? r.Tail : null;
                if (row == null || s.Ordinal != 0) continue;
                var v = dal.ReadValues(contentKey, s.Id, ProducerKey, producerVersion);
                if (!v.TryGetValue(EdgeField, out var ev) || ev.Vec == null) continue;
                UnpackEdge(ev.Vec, row); row.StartMs = s.StartMs; row.EndMs = s.EndMs;
                if (row.Whole != null) { row.Whole.StartMs = s.StartMs; row.Whole.EndMs = s.EndMs; }
            }
            return r;
        }

        // ---------------------------------------------------------------- the whole result as bytes (the sink's spill, review I3)

        static readonly byte[] ResultMagic = { (byte)'E', (byte)'D', (byte)'R', 1 };

        /// <summary>A whole fold result as one blob: status words, duration, producer version, extractor, stopwatch, errors,
        /// head-vs-tail divergence and the two packed edges. The scan sink writes this to disk per analysed track instead of
        /// holding ~11 KB of objects per track until the flush (72k tracks would have been ~800 MB resident).</summary>
        internal static byte[] PackResult(EdgeFoldResult r)
        {
            var ms = new MemoryStream(16384); var w = new BinaryWriter(ms);
            w.Write(ResultMagic);
            w.Write(r.ProducerVersion ?? ""); w.Write(r.Extractor ?? ""); w.Write(r.DurationMs); w.Write(r.FoldMs); w.Write(r.PcmMs);
            w.Write(r.Head.Status ?? ""); w.Write(r.Tail.Status ?? ""); w.Write(r.Head.Error ?? ""); w.Write(r.Tail.Error ?? "");
            w.Write(r.Head.StartMs); w.Write(r.Head.EndMs); w.Write(r.Tail.StartMs); w.Write(r.Tail.EndMs);
            var d = r.HeadTail;
            w.Write((float)(d?.Loudness ?? double.NaN)); w.Write((float)(d?.Texture ?? double.NaN)); w.Write((float)(d?.Harmony ?? double.NaN));
            var h = PackEdge(r.Head); var t = PackEdge(r.Tail);
            w.Write(h.Length); w.Write(h); w.Write(t.Length); w.Write(t);
            w.Flush(); return ms.ToArray();
        }

        internal static EdgeFoldResult UnpackResult(byte[] blob)
        {
            var rd = new BinaryReader(new MemoryStream(blob));
            Magic(rd, ResultMagic);
            var r = new EdgeFoldResult { ProducerVersion = rd.ReadString(), Extractor = rd.ReadString(), DurationMs = rd.ReadInt32(), FoldMs = rd.ReadInt64(), PcmMs = rd.ReadInt64() };
            string hs = rd.ReadString(), ts = rd.ReadString(), he = rd.ReadString(), te = rd.ReadString();
            int hStart = rd.ReadInt32(), hEnd = rd.ReadInt32(), tStart = rd.ReadInt32(), tEnd = rd.ReadInt32();
            float dl = rd.ReadSingle(), dt = rd.ReadSingle(), dh = rd.ReadSingle();
            if (!float.IsNaN(dl) || !float.IsNaN(dt) || !float.IsNaN(dh)) r.HeadTail = new EdgeDivergence { Loudness = Opt(dl), Texture = Opt(dt), Harmony = Opt(dh) };
            int hl = rd.ReadInt32(); var hb = rd.ReadBytes(hl); int tl = rd.ReadInt32(); var tb = rd.ReadBytes(tl);
            UnpackEdge(hb, r.Head); UnpackEdge(tb, r.Tail);
            // the status word and errors travel outside the edge blob (the blob only knows ok / short-context)
            r.Head.Status = hs; r.Tail.Status = ts; r.Head.Error = he.Length == 0 ? null : he; r.Tail.Error = te.Length == 0 ? null : te;
            r.Head.StartMs = hStart; r.Head.EndMs = hEnd; r.Tail.StartMs = tStart; r.Tail.EndMs = tEnd;
            if (r.Head.Whole != null) { r.Head.Whole.StartMs = hStart; r.Head.Whole.EndMs = hEnd; }
            if (r.Tail.Whole != null) { r.Tail.Whole.StartMs = tStart; r.Tail.Whole.EndMs = tEnd; }
            return r;
        }

        // ---------------------------------------------------------------- pack

        internal static byte[] PackEdge(EdgeRow e)
        {
            var ms = new MemoryStream(8192); var w = new BinaryWriter(ms);
            w.Write(EdgeMagic);
            w.Write((byte)(e.Status == "short-context" ? 2 : 1));
            var l = e.Loudness;
            foreach (var x in new[]
            {
                e.Bpm?.Value, e.Bpm?.Beats, e.Bpm?.Regularity,
                l?.StartLufs, l?.EndLufs, l?.MeanLufs, l?.FadeMs, l == null ? (double?)null : l.SilenceMs, l == null ? (double?)null : (l.SilenceSource == "pcm" ? 1 : 0),
                e.Rms?.CrestDb, e.Rms == null ? (double?)null : e.Rms.TransitionMs,
                e.Divergence?.Loudness, e.Divergence?.Texture, e.Divergence?.Harmony,
            }) w.Write((float)(x ?? double.NaN));
            w.Write((ushort)e.MomentaryCentiDb.Length); foreach (var s in e.MomentaryCentiDb) w.Write(s);
            w.Write((ushort)e.ShortTermCentiDb.Length); foreach (var s in e.ShortTermCentiDb) w.Write(s);
            int nb = Math.Min(e.BeatsMs.Length, e.BeatsLoudness.Length == e.BeatsMs.Length ? e.BeatsMs.Length : e.BeatsMs.Length);
            w.Write((ushort)nb); for (int i = 0; i < nb; i++) w.Write(e.BeatsMs[i]); for (int i = 0; i < nb; i++) w.Write(i < e.BeatsLoudness.Length ? e.BeatsLoudness[i] : float.NaN);
            WriteSummary(w, e.Whole ?? new EdgeWindowSummary());
            w.Write((byte)Math.Min(255, e.Sub.Length));
            for (int i = 0; i < Math.Min(255, e.Sub.Length); i++) { w.Write(e.Sub[i].StartMs); w.Write(e.Sub[i].EndMs); WriteSummary(w, e.Sub[i]); }
            w.Flush(); return ms.ToArray();
        }

        static void WriteSummary(BinaryWriter w, EdgeWindowSummary s)
        {
            Vec(w, s.HpcpMean, 36); Vec(w, s.MfccMean, 13); Vec(w, s.MfccSpread, 13); Vec(w, s.SpectralMean, 11); Vec(w, s.SpectralSpread, 11);
            if (s.Key == null) { w.Write((byte)0); return; }
            w.Write((byte)1); Str(w, s.Key.Name); Str(w, s.Key.Scale); Str(w, s.Key.Code); w.Write((float)s.Key.Strength); w.Write((byte)s.Key.Agreement); w.Write((byte)s.Key.VotesPresent);
        }

        static void Vec(BinaryWriter w, double[] v, int n) { for (int i = 0; i < n; i++) w.Write((float)(i < v.Length ? v[i] : double.NaN)); }
        static void Str(BinaryWriter w, string s) { var b = Encoding.ASCII.GetBytes(s ?? ""); w.Write((byte)Math.Min(255, b.Length)); w.Write(b, 0, Math.Min(255, b.Length)); }

        // ---------------------------------------------------------------- unpack

        static void UnpackEdge(byte[] blob, EdgeRow row)
        {
            var r = new BinaryReader(new MemoryStream(blob));
            Magic(r, EdgeMagic);
            row.Status = r.ReadByte() == 2 ? "short-context" : "ok";
            var f = new float[14]; for (int i = 0; i < 14; i++) f[i] = r.ReadSingle();
            if (Opt(f[0]) is double bpm) row.Bpm = new EdgeBpm { Value = bpm, Beats = (int)(Opt(f[1]) ?? 0), Regularity = Opt(f[2]) ?? 0 };
            if (!float.IsNaN(f[7]))
                row.Loudness = new EdgeLoudness { StartLufs = Opt(f[3]), EndLufs = Opt(f[4]), MeanLufs = Opt(f[5]), FadeMs = Opt(f[6]) is double fd ? (int?)Math.Round(fd) : null, SilenceMs = (int)Math.Round(f[7]), SilenceSource = f[8] >= 0.5f ? "pcm" : "ebu" };
            if (!float.IsNaN(f[10])) row.Rms = new EdgeRms { CrestDb = Opt(f[9]), TransitionMs = (int)Math.Round(f[10]) };
            if (!float.IsNaN(f[11]) || !float.IsNaN(f[12]) || !float.IsNaN(f[13])) row.Divergence = new EdgeDivergence { Loudness = Opt(f[11]), Texture = Opt(f[12]), Harmony = Opt(f[13]) };
            int n = r.ReadUInt16(); row.MomentaryCentiDb = new short[n]; for (int i = 0; i < n; i++) row.MomentaryCentiDb[i] = r.ReadInt16();
            n = r.ReadUInt16(); row.ShortTermCentiDb = new short[n]; for (int i = 0; i < n; i++) row.ShortTermCentiDb[i] = r.ReadInt16();
            n = r.ReadUInt16(); row.BeatsMs = new int[n]; for (int i = 0; i < n; i++) row.BeatsMs[i] = r.ReadInt32();
            row.BeatsLoudness = new float[n]; for (int i = 0; i < n; i++) row.BeatsLoudness[i] = r.ReadSingle();
            row.Whole = UnpackSummary(r);
            int subs = r.ReadByte(); row.Sub = new EdgeWindowSummary[subs];
            for (int i = 0; i < subs; i++) { int a = r.ReadInt32(), b = r.ReadInt32(); var s = UnpackSummary(r); s.StartMs = a; s.EndMs = b; row.Sub[i] = s; }
        }

        static EdgeWindowSummary UnpackSummary(BinaryReader r)
        {
            var s = new EdgeWindowSummary();
            ReadVec(r, s.HpcpMean); ReadVec(r, s.MfccMean); ReadVec(r, s.MfccSpread); ReadVec(r, s.SpectralMean); ReadVec(r, s.SpectralSpread);
            if (r.ReadByte() == 1)
                s.Key = new EdgeKeyVote { Name = ReadStr(r), Scale = ReadStr(r), Code = ReadStr(r), Strength = r.ReadSingle(), Agreement = r.ReadByte(), VotesPresent = r.ReadByte() };
            return s;
        }

        static void Magic(BinaryReader r, byte[] expect)
        {
            var got = r.ReadBytes(4);
            for (int i = 0; i < 4; i++) if (got.Length < 4 || got[i] != expect[i]) throw new InvalidDataException("edge-row: unknown blob layout");
        }
        static void ReadVec(BinaryReader r, double[] into) { for (int i = 0; i < into.Length; i++) into[i] = r.ReadSingle(); }
        static string ReadStr(BinaryReader r) { int n = r.ReadByte(); return Encoding.ASCII.GetString(r.ReadBytes(n)); }
        static double? Opt(float f) => float.IsNaN(f) ? (double?)null : f;
        static byte[] F32Bytes(float[] a) { var b = new byte[a.Length * 4]; Buffer.BlockCopy(a, 0, b, 0, b.Length); return b; }
        static float[] F32s(byte[] b) { var a = new float[b.Length / 4]; Buffer.BlockCopy(b, 0, a, 0, a.Length * 4); return a; }
    }
}
