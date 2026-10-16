using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Truedat.Store;

namespace Truedat.Edges
{
    /// <summary>S2 C4 / store-v3 T05 — the edge in the store (schema v4). Per track: 2 <c>segment</c> rows (head and tail, <c>set_key</c>
    /// = the producer version), ONE typed <c>edge</c> row per edge — the DEFAULT row is the scalars the readers use plus the edge's
    /// whole-summary HPCP (36 × float32); the opt-in capture (series, beats, sub-window summaries, MFCC/spectral vectors) rides in the
    /// row's <c>arrays</c> column as the edge-row/1 blob below — plus whole-track <c>value</c> rows (status, duration, extractor,
    /// head-vs-tail divergence). Keyed by content hash so a second copy of the same audio costs nothing. Capture is STICKY per track
    /// (operator, 2026-10-15: "sticky per track. keep what we captured"): a row that holds arrays keeps getting them on every later
    /// write whatever the flag says; only <see cref="DropArrays"/> (the explicit discard verb) clears them. The flag gates
    /// PERSISTENCE only — the fold computes the same arrays either way, because the scalars derive from them.
    ///
    /// (Before v4 the whole edge was one packed <c>value</c> row, <c>edge.row</c>; SchemaV4Migration moves each into this table.)
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

        /// <summary>The arrays blob's format name (edge.arrays_dtype).</summary>
        internal const string ArraysDtype = "edge-row/1";

        /// <summary>The capture flag in force for writes that do not say (the scan's `--edge-arrays` / config `edgeArrays`; default OFF —
        /// the config Default is the only default). Set once by the scan before any write.</summary>
        internal static bool CaptureArraysDefault;

        /// <summary>Returns the number of rows written — whole-track value rows plus edge rows (0 for a failed result).
        /// <paramref name="captureArrays"/> null = <see cref="CaptureArraysDefault"/>. A row that already holds arrays keeps them (sticky).</summary>
        internal static int Write(StoreDal dal, string contentKey, string contentKeyKind, long? runId, EdgeFoldResult r, bool? captureArrays = null)
        {
            if (r.Status == "failed") return 0;
            dal.EnsureProducer(ProducerKey, r.ProducerVersion, "analysis", needsAudio: true);
            int n = 0;
            void Put(long seg, string field, double? num, string? txt, byte[]? vec, long? dims, string? unit, string? dtype = null)
            { dal.UpsertValue(contentKey, contentKeyKind, seg, field, ProducerKey, r.ProducerVersion, runId, num, txt, vec, dims, unit, dtype); n++; }
            Put(WholeTrack, "edges.status", null, r.Status, null, null, null);
            Put(WholeTrack, "edges.durationMs", r.DurationMs, null, null, null, null);
            if (!string.IsNullOrEmpty(r.Extractor)) Put(WholeTrack, "edges.extractor", null, r.Extractor, null, null, null);   // the frames depend on the build (review M4)
            if (r.Status == "unavailable") return n;
            if (r.HeadTail != null)
            {
                var d = r.HeadTail;
                Put(WholeTrack, "edges.headTail", null, null, F32Bytes(new[] { (float)(d.Loudness ?? double.NaN), (float)(d.Texture ?? double.NaN), (float)(d.Harmony ?? double.NaN) }), 3, "loudness,texture,harmony", "f32");
            }
            bool capture = captureArrays ?? CaptureArraysDefault;
            // "sticky per track. keep what we captured" — per TRACK, not per producer version (keeper 019): a track whose rows hold arrays at ANY
            // version keeps getting them when it is re-folded under a new one, with the new fold's values.
            bool sticky = !capture && TrackHoldsArrays(dal, contentKey);
            var now = DateTime.UtcNow.ToString("o");
            foreach (var e in new[] { r.Head, r.Tail })
            {
                dal.UpsertSegment(contentKey, contentKeyKind, r.ProducerVersion, e.Kind, 0, e.StartMs, e.EndMs);
                UpsertEdgeRow(dal, contentKey, contentKeyKind, r.ProducerVersion, runId, e, capture || sticky ? PackEdge(e) : null, now);
                n++;
            }
            return n;
        }

        static float F(double? v) => (float)(v ?? double.NaN);
        static object? R(double? v) { if (!v.HasValue || double.IsNaN(v.Value) || double.IsInfinity(v.Value)) return null; return (double)(float)v.Value; }   // the fold's float32, widened

        /// <summary>The typed default row (plus the arrays blob when captured). The scalars are the fold's float32 values widened to REAL,
        /// exactly what the edge-row/1 blob holds for them, so a row reads back the same with or without its arrays.</summary>
        internal static void UpsertEdgeRow(StoreDal dal, string contentKey, string contentKeyKind, string producerVersion, long? runId, EdgeRow e, byte[]? arrays, string now)
        {
            var w = e.Whole; var k = w?.Key; var l = e.Loudness;
            byte[]? hpcp = null;
            if (w != null) { var f = new float[36]; for (int i = 0; i < 36; i++) f[i] = i < w.HpcpMean.Length ? (float)w.HpcpMean[i] : float.NaN; hpcp = F32Bytes(f); }
            dal.ExecBound("INSERT INTO edge(content_hash, content_hash_kind, producer_version, kind, run_id, status, start_ms, end_ms, " +
                "key_name, key_scale, key_code, key_strength, key_agreement, key_votes, bpm, bpm_beats, bpm_regularity, " +
                "start_lufs, end_lufs, mean_lufs, fade_ms, silence_ms, silence_source, crest_db, transition_ms, div_loudness, div_texture, div_harmony, " +
                "centroid, dissonance, hpcp, arrays, arrays_dtype, updated_utc) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?) " +
                "ON CONFLICT(content_hash, producer_version, kind) DO UPDATE SET content_hash_kind = excluded.content_hash_kind, run_id = excluded.run_id, status = excluded.status, " +
                "start_ms = excluded.start_ms, end_ms = excluded.end_ms, key_name = excluded.key_name, key_scale = excluded.key_scale, key_code = excluded.key_code, " +
                "key_strength = excluded.key_strength, key_agreement = excluded.key_agreement, key_votes = excluded.key_votes, bpm = excluded.bpm, bpm_beats = excluded.bpm_beats, " +
                "bpm_regularity = excluded.bpm_regularity, start_lufs = excluded.start_lufs, end_lufs = excluded.end_lufs, mean_lufs = excluded.mean_lufs, fade_ms = excluded.fade_ms, " +
                "silence_ms = excluded.silence_ms, silence_source = excluded.silence_source, crest_db = excluded.crest_db, transition_ms = excluded.transition_ms, " +
                "div_loudness = excluded.div_loudness, div_texture = excluded.div_texture, div_harmony = excluded.div_harmony, centroid = excluded.centroid, " +
                "dissonance = excluded.dissonance, hpcp = excluded.hpcp, arrays = excluded.arrays, arrays_dtype = excluded.arrays_dtype, updated_utc = excluded.updated_utc;",
                contentKey, contentKeyKind, producerVersion, e.Kind, runId, e.Status == "short-context" ? "short-context" : "ok", (long)e.StartMs, (long)e.EndMs,
                k?.Name, k?.Scale, k?.Code, k == null ? null : R(k.Strength), k == null ? null : (object)(long)k.Agreement, k == null ? null : (object)(long)k.VotesPresent,
                e.Bpm == null ? null : R(e.Bpm.Value), e.Bpm == null ? null : (object)(long)e.Bpm.Beats, e.Bpm == null ? null : R(e.Bpm.Regularity),
                l == null ? null : R(l.StartLufs), l == null ? null : R(l.EndLufs), l == null ? null : R(l.MeanLufs), l?.FadeMs == null ? null : (object)(long)l.FadeMs.Value,
                l == null ? null : (object)(long)l.SilenceMs, l?.SilenceSource,
                e.Rms == null ? null : R(e.Rms.CrestDb), e.Rms == null ? null : (object)(long)e.Rms.TransitionMs,
                e.Divergence == null ? null : R(e.Divergence.Loudness), e.Divergence == null ? null : R(e.Divergence.Texture), e.Divergence == null ? null : R(e.Divergence.Harmony),
                w == null || w.SpectralMean.Length < 1 ? null : R(w.SpectralMean[0]), w == null || w.SpectralMean.Length < 6 ? null : R(w.SpectralMean[5]),
                hpcp, arrays, arrays == null ? null : ArraysDtype, now);
        }

        /// <summary>True when any edge row of this content, at ANY producer version, holds the opt-in arrays — the sticky test (per track).</summary>
        internal static bool TrackHoldsArrays(StoreDal dal, string contentKey)
            => dal.ScalarLong("SELECT count(*) FROM edge WHERE content_hash = ? AND arrays IS NOT NULL;", contentKey) > 0;

        /// <summary>True when any edge row of this content at this producer version holds the opt-in arrays.</summary>
        internal static bool ArraysCaptured(StoreDal dal, string contentKey, string producerVersion)
            => dal.ScalarLong("SELECT count(*) FROM edge WHERE content_hash = ? AND producer_version = ? AND arrays IS NOT NULL;", contentKey, producerVersion) > 0;

        internal sealed class DropArraysResult { public long Rows, Tracks, Bytes; }

        /// <summary>The explicit discard verb: clears the opt-in arrays (and their dtype) from every edge row that holds them, keeping the
        /// row and its scalars. <paramref name="dryRun"/> counts what it would clear and changes nothing. The caller owns the transaction.</summary>
        internal static DropArraysResult DropArrays(StoreDal dal, bool dryRun)
        {
            var r = new DropArraysResult
            {
                Rows = long.Parse(dal.Db.ExecScalar("SELECT count(*) FROM edge WHERE arrays IS NOT NULL;") ?? "0"),
                Tracks = long.Parse(dal.Db.ExecScalar("SELECT count(DISTINCT content_hash) FROM edge WHERE arrays IS NOT NULL;") ?? "0"),
                Bytes = long.Parse(dal.Db.ExecScalar("SELECT coalesce(sum(length(arrays)), 0) FROM edge;") ?? "0"),
            };
            if (!dryRun) dal.Db.Exec("UPDATE edge SET arrays = NULL, arrays_dtype = NULL WHERE arrays IS NOT NULL;");
            return r;
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
            using (var st = dal.Db.Prepare("SELECT kind, status, start_ms, end_ms, key_name, key_scale, key_code, key_strength, key_agreement, key_votes, bpm, bpm_beats, bpm_regularity, " +
                "start_lufs, end_lufs, mean_lufs, fade_ms, silence_ms, silence_source, crest_db, transition_ms, div_loudness, div_texture, div_harmony, centroid, dissonance, hpcp, arrays " +
                "FROM edge WHERE content_hash = ? AND producer_version = ?;"))
            {
                st.Bind(1, contentKey); st.Bind(2, producerVersion);
                while (st.Step())
                {
                    var kind = st.GetString(0);
                    var row = kind == "head" ? r.Head : kind == "tail" ? r.Tail : null;
                    if (row == null) continue;
                    int startMs = (int)st.GetInt64(2), endMs = (int)st.GetInt64(3);
                    var arrays = st.IsNull(27) ? null : st.GetBlob(27);
                    if (arrays != null) UnpackEdge(arrays, row);   // the opt-in capture: the whole edge, scalars included (identical to the columns)
                    else ReadDefaultRow(st, row);
                    row.Status = st.GetString(1) ?? "ok";
                    row.StartMs = startMs; row.EndMs = endMs;
                    if (row.Whole != null) { row.Whole.StartMs = startMs; row.Whole.EndMs = endMs; }
                }
            }
            return r;
        }

        /// <summary>The default row: every scalar and the whole-summary HPCP; series, beats and sub-windows empty; MFCC and the other nine
        /// spectral means/spreads NaN (not captured) — centroid and dissonance at their places in <c>SpectralMean</c> (0 and 5).</summary>
        static void ReadDefaultRow(Truedat.Store.SqliteStmt st, EdgeRow row)
        {
            double? D(int c) => st.IsNull(c) ? (double?)null : st.GetDouble(c);
            int? I(int c) => st.IsNull(c) ? (int?)null : (int)st.GetInt64(c);
            var w = new EdgeWindowSummary();
            for (int i = 0; i < w.MfccMean.Length; i++) { w.MfccMean[i] = double.NaN; w.MfccSpread[i] = double.NaN; }
            for (int i = 0; i < w.SpectralMean.Length; i++) { w.SpectralMean[i] = double.NaN; w.SpectralSpread[i] = double.NaN; }
            w.SpectralMean[0] = D(24) ?? double.NaN; w.SpectralMean[5] = D(25) ?? double.NaN;
            var hp = st.IsNull(26) ? null : st.GetBlob(26);
            if (hp != null && hp.Length == 36 * 4) { var f = F32s(hp); for (int i = 0; i < 36; i++) w.HpcpMean[i] = f[i]; }
            else for (int i = 0; i < 36; i++) w.HpcpMean[i] = double.NaN;
            if (!st.IsNull(4)) w.Key = new EdgeKeyVote { Name = st.GetString(4) ?? "", Scale = st.GetString(5) ?? "", Code = st.GetString(6) ?? "", Strength = D(7) ?? 0, Agreement = I(8) ?? 0, VotesPresent = I(9) ?? 0 };
            row.Whole = w;
            if (D(10) is double bpm) row.Bpm = new EdgeBpm { Value = bpm, Beats = I(11) ?? 0, Regularity = D(12) ?? 0 };
            if (I(17) is int silence) row.Loudness = new EdgeLoudness { StartLufs = D(13), EndLufs = D(14), MeanLufs = D(15), FadeMs = I(16), SilenceMs = silence, SilenceSource = st.GetString(18) ?? "ebu" };
            if (I(20) is int transition) row.Rms = new EdgeRms { CrestDb = D(19), TransitionMs = transition };
            if (!st.IsNull(21) || !st.IsNull(22) || !st.IsNull(23)) row.Divergence = new EdgeDivergence { Loudness = D(21), Texture = D(22), Harmony = D(23) };
            row.MomentaryCentiDb = Array.Empty<short>(); row.ShortTermCentiDb = Array.Empty<short>();
            row.BeatsMs = Array.Empty<int>(); row.BeatsLoudness = Array.Empty<float>(); row.Sub = Array.Empty<EdgeWindowSummary>();
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

        internal static void UnpackEdge(byte[] blob, EdgeRow row)
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
