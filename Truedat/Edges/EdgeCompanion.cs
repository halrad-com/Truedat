using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Truedat.Store;

namespace Truedat.Edges
{
    internal sealed class EdgeCompanionReport { public int Tracks, OmittedNoSha; public long Bytes; public string Path = ""; }

    /// <summary>S2 C7 — <c>mbxedges.v1.json</c>, the hub-facing companion file beside <c>mbxmoods.json</c>: the agreed shape
    /// (restfulbee 2026-10-11, fixture S:/mbxedges.v1.fixture.json, RMS option D 2026-10-12), keyed by audioStreamSha256, scalars
    /// only — the 10/s series, beats, HPCP means and sub-window summaries stay in the store. Written from the store, streamed
    /// through Utf8JsonWriter (72k tracks ≈ 115 MB, never a DOM), to a .tmp beside the target and swapped in. Media without a
    /// sha have no key in this contract and are counted, not written.
    ///
    /// Rounding: LUFS and crest 1 dp, bpm 1 dp, strengths/regularity/divergence 2 dp, MFCC 1 dp, spectral 4 dp (spectralFlux
    /// and the energies live below 0.1). Null where the fold declined: key (estimator declined), bpm (under five beats), rms (no
    /// PCM pass), fadeMs (a cut); every per-edge block null when the edge is not ok / short-context; the track divergence null
    /// unless both edges folded.</summary>
    internal static class EdgeCompanion
    {
        internal const string FileName = "mbxedges.v1.json";
        internal const string Contract = "mbxedges.v1";
        static readonly string[] SpectralOrder =
        {
            "spectralCentroid", "spectralFlux", "spectralRolloff", "spectralEntropy", "spectralComplexity", "dissonance",
            "pitchSalience", "zeroCrossingRate", "spectralRms", "spectralEnergy", "spectralStrongPeak",
        };
        static readonly string[] StatusWords = { "ok", "short-context", "unavailable", "failed" };

        /// <summary>Write the companion to <paramref name="path"/> atomically (temp + swap). <paramref name="generation"/>, when
        /// given, is the store's publication generation (store-v3 T07): it lands as <c>origin.generation</c> in the root, which an
        /// upgraded hub matches against the catalog's and an old hub ignores (unknown root key). Null keeps the pre-T07 shape.</summary>
        internal static EdgeCompanionReport WriteFromStore(StoreDal dal, string path, string producerVersion, string extractor, long? generation = null)
        {
            var tmp = path + ".tmp";
            var rep = WriteTempFromStore(dal, tmp, producerVersion, extractor, generation);
            try { if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path); }
            catch { try { File.Delete(tmp); } catch { } throw; }
            rep.Path = path;
            rep.Bytes = new FileInfo(path).Length;
            return rep;
        }

        /// <summary>Write the companion to <paramref name="tmpPath"/> and leave it there: the store-v3 publisher
        /// (<c>Publication</c>) performs the rename in the ruled order (companion aside, catalog, companion, sidecar LAST).
        /// Never leaves a half-written temp (deleted on failure).</summary>
        internal static EdgeCompanionReport WriteTempFromStore(StoreDal dal, string tmpPath, string producerVersion, string extractor, long? generation = null)
        {
            var rep = new EdgeCompanionReport { Path = tmpPath };
            var keys = dal.ContentKeysWithEdges(producerVersion);
            // The header names the extractor the ROWS record (review M4), falling back to what the caller resolved.
            var recorded = dal.EdgesExtractorBuild(producerVersion);
            if (!string.IsNullOrEmpty(recorded)) extractor = "essentia_streaming_extractor_music " + recorded;
            var tmp = tmpPath;
            try
            {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = false }))   // compact, like mbxmoods.json: indented measured 2x the bytes (3.3 KB/track vs the plan's ~1.6)
            {
                w.WriteStartObject();
                w.WriteString("contract", Contract);
                w.WriteString("producerVersion", producerVersion);
                w.WriteString("extractor", extractor);
                w.WriteString("generatedUtc", DateTime.UtcNow.ToString("o"));
                if (generation.HasValue)
                {
                    // In the head, before the tracks: the hub's recovery/binding read is bounded (CatalogOrigin.TryReadHead).
                    w.WriteStartObject("origin"); w.WriteNumber("generation", generation.Value); w.WriteEndObject();
                }
                w.WritePropertyName("spectralOrder"); w.WriteStartArray(); foreach (var s in SpectralOrder) w.WriteStringValue(s); w.WriteEndArray();
                w.WritePropertyName("statusWords"); w.WriteStartArray(); foreach (var s in StatusWords) w.WriteStringValue(s); w.WriteEndArray();
                int shaKeyed = 0; foreach (var k in keys) if (!k.StartsWith("entity:", StringComparison.Ordinal)) shaKeyed++;
                w.WriteNumber("trackCount", shaKeyed);
                w.WritePropertyName("tracks"); w.WriteStartObject();
                foreach (var key in keys)
                {
                    if (key.StartsWith("entity:", StringComparison.Ordinal)) { rep.OmittedNoSha++; continue; }
                    var r = EdgeStore.Read(dal, key, producerVersion);
                    if (r == null) continue;
                    w.WritePropertyName(key); WriteTrack(w, r); rep.Tracks++;
                }
                w.WriteEndObject();
                w.WritePropertyName("rmsDefinitions"); w.WriteStartObject();
                w.WriteString("crestDb", "20*log10(max 100 ms slice sample peak / mean 100 ms slice RMS) over the edge window, from a PCM pass; null when the window < 1 s or ffmpeg is absent");
                w.WriteString("transitionMs", "outer end: ms from the last 100 ms slice within 20 dB of the window mean RMS to the silence floor, in 100 ms steps; <= 400 reads as a cut; null without the PCM pass");
                w.WriteString("silenceMs", "outer-end run of 100 ms slices whose sample max is below -60 dBFS (silenceSource 'pcm'); without ffmpeg, the EBU-momentary run below -60 LUFS (silenceSource 'ebu')");
                w.WriteString("fadeMs", "departure from the edge's plateau (median non-silent momentary level) by more than 1 LU, measured to the silence boundary or the window's outer end; kept when >= 1 s and the boundary level is >= 10 LU under the plateau; null = a cut");
                w.WriteEndObject();
                w.WriteEndObject();
                w.Flush();
            }
            }
            catch { try { File.Delete(tmp); } catch { } throw; }   // never leave a half-written .tmp beside the catalog (review M6)
            rep.Bytes = new FileInfo(tmp).Length;
            return rep;
        }

        static void WriteTrack(Utf8JsonWriter w, EdgeFoldResult r)
        {
            w.WriteStartObject();
            w.WriteNumber("durationMs", r.DurationMs);
            w.WritePropertyName("head"); WriteEdge(w, r.Head);
            w.WritePropertyName("tail"); WriteEdge(w, r.Tail);
            w.WritePropertyName("divergence");
            bool both = (r.Head.Status == "ok" || r.Head.Status == "short-context") && (r.Tail.Status == "ok" || r.Tail.Status == "short-context");
            if (both && r.HeadTail != null) WriteDivergence(w, r.HeadTail); else w.WriteNullValue();
            w.WriteEndObject();
        }

        static void WriteEdge(Utf8JsonWriter w, EdgeRow e)
        {
            bool folded = e.Status == "ok" || e.Status == "short-context";
            w.WriteStartObject();
            w.WriteString("status", e.Status);
            w.WriteNumber("startMs", e.StartMs); w.WriteNumber("endMs", e.EndMs);
            w.WritePropertyName("key");
            if (folded && e.Whole?.Key != null)
            {
                var k = e.Whole.Key;
                w.WriteStartObject();
                w.WriteString("name", k.Name); w.WriteString("code", k.Code); w.WriteString("scale", k.Scale);
                w.WriteNumber("strength", Dec(k.Strength, Dp.KeyStrength)); w.WriteNumber("agreement", k.Agreement); w.WriteNumber("votesPresent", k.VotesPresent);
                w.WriteEndObject();
            }
            else w.WriteNullValue();
            w.WritePropertyName("bpm");
            if (folded && e.Bpm != null)
            {
                w.WriteStartObject(); w.WriteNumber("value", Dec(e.Bpm.Value, Dp.BpmValue)); w.WriteNumber("beats", e.Bpm.Beats); w.WriteNumber("regularity", Dec(e.Bpm.Regularity, Dp.BpmRegularity)); w.WriteEndObject();
            }
            else w.WriteNullValue();
            w.WritePropertyName("loudness");
            if (folded && e.Loudness != null)
            {
                var l = e.Loudness;
                w.WriteStartObject();
                Num(w, "startLufs", l.StartLufs, Dp.Lufs); Num(w, "endLufs", l.EndLufs, Dp.Lufs); Num(w, "meanLufs", l.MeanLufs, Dp.Lufs);
                w.WritePropertyName("fadeMs"); if (l.FadeMs.HasValue) w.WriteNumberValue(l.FadeMs.Value); else w.WriteNullValue();
                w.WriteNumber("silenceMs", l.SilenceMs); w.WriteString("silenceSource", l.SilenceSource);
                w.WriteEndObject();
            }
            else w.WriteNullValue();
            w.WritePropertyName("texture");
            // The texture block exists only when the arrays were captured (opt-in): the default row has no MFCC/spectral vectors, and a
            // block of nulls would be a present object the v1 reader drops the track for. Null texture is v1-valid (EdgeRow.cs:179).
            if (folded && e.Whole != null && HasTexture(e.Whole))
            {
                w.WriteStartObject();
                Arr(w, "mfccMean", e.Whole.MfccMean, Dp.Mfcc); Arr(w, "mfccSpread", e.Whole.MfccSpread, Dp.Mfcc);
                Arr(w, "spectralMean", e.Whole.SpectralMean, Dp.Spectral); Arr(w, "spectralSpread", e.Whole.SpectralSpread, Dp.Spectral);
                w.WriteEndObject();
            }
            else w.WriteNullValue();
            // A04 (d) — ruled by the operator 2026-10-16 ("yes to A04 (d)"): the window's spectral centroid and dissonance as DIRECT keys, on
            // every folded edge (default and opt-in capture alike), so a hub has the regional values without the texture block. Same summary
            // and 4 dp as texture.spectralMean[0] / [5]. The v1 reader ignores unknown keys, so the file stays v1-valid.
            bool hasSummary = folded && e.Whole != null && e.Whole.SpectralMean.Length >= 6;
            Num(w, "centroid", hasSummary ? e.Whole!.SpectralMean[0] : (double?)null, Dp.Centroid);
            Num(w, "dissonance", hasSummary ? e.Whole!.SpectralMean[5] : (double?)null, Dp.Dissonance);
            w.WritePropertyName("divergence");
            if (folded && e.Divergence != null) WriteDivergence(w, e.Divergence); else w.WriteNullValue();
            w.WritePropertyName("rms");
            if (folded && e.Rms != null)
            {
                w.WriteStartObject(); Num(w, "crestDb", e.Rms.CrestDb, Dp.CrestDb); w.WriteNumber("transitionMs", e.Rms.TransitionMs); w.WriteEndObject();
            }
            else w.WriteNullValue();
            w.WriteEndObject();
        }

        static bool HasTexture(EdgeWindowSummary s)
        {
            foreach (var x in s.MfccMean) if (!double.IsNaN(x)) return true;
            return false;
        }

        static void WriteDivergence(Utf8JsonWriter w, EdgeDivergence d)
        {
            w.WriteStartObject(); Num(w, "loudness", d.Loudness, Dp.Divergence); Num(w, "texture", d.Texture, Dp.Divergence); Num(w, "harmony", d.Harmony, Dp.Divergence); w.WriteEndObject();
        }

        /// <summary>TDA 086 (6): EVERY decimal-place count the companion writes, in one table — the companion's write-time rounding rule,
        /// the one place a store-backed reader (hub:H02) mirrors and cites, so its values equal the companion's exactly rather than
        /// within a tolerance. Each value goes through <see cref="Dec"/> (Math.Round, then a decimal of it); NaN/Infinity write JSON null.
        /// Integers (startMs, endMs, durationMs, fadeMs, silenceMs, transitionMs, beats, agreement, votesPresent) are written as stored.</summary>
        internal static class Dp
        {
            internal const int KeyStrength = 2;      // key.strength
            internal const int BpmValue = 1;         // bpm.value
            internal const int BpmRegularity = 2;    // bpm.regularity
            internal const int Lufs = 1;             // loudness.startLufs / endLufs / meanLufs
            internal const int Mfcc = 1;             // texture.mfccMean / mfccSpread (opt-in arrays only)
            internal const int Spectral = 4;         // texture.spectralMean / spectralSpread (opt-in arrays only)
            internal const int Centroid = 4;         // centroid (A04 (d) direct key)
            internal const int Dissonance = 4;       // dissonance (A04 (d) direct key)
            internal const int CrestDb = 2;          // rms.crestDb
            internal const int Divergence = 2;       // divergence.loudness / texture / harmony, edge and head<->tail
        }

        // net48 formats a double with round-trip noise ("0.77000000000000002"); a decimal of the rounded value prints exactly
        // "0.77". The companion is read by machines but sized by humans: the noise doubled the bytes per track.
        static void Num(Utf8JsonWriter w, string name, double? v, int dp)
        {
            w.WritePropertyName(name);
            if (v.HasValue && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value)) w.WriteNumberValue(Dec(v.Value, dp)); else w.WriteNullValue();
        }

        static void Arr(Utf8JsonWriter w, string name, double[] v, int dp)
        {
            w.WritePropertyName(name); w.WriteStartArray();
            foreach (var x in v) { if (double.IsNaN(x) || double.IsInfinity(x)) w.WriteNullValue(); else w.WriteNumberValue(Dec(x, dp)); }
            w.WriteEndArray();
        }

        internal static decimal Dec(double v, int dp)
        {
            double r = Math.Round(v, dp);
            if (r > 7.9e28 || r < -7.9e28) return (decimal)Math.Sign(r) * 79000000000000000000000000000m;
            return decimal.Round((decimal)r, dp);
        }
    }
}
