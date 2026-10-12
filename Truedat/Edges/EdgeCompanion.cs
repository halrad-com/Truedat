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

        internal static EdgeCompanionReport WriteFromStore(StoreDal dal, string path, string producerVersion, string extractor)
        {
            var rep = new EdgeCompanionReport { Path = path };
            var keys = dal.ContentKeysWithEdges(producerVersion);
            // The header names the extractor the ROWS record (review M4), falling back to what the caller resolved.
            var recorded = dal.EdgesExtractorBuild(producerVersion);
            if (!string.IsNullOrEmpty(recorded)) extractor = "essentia_streaming_extractor_music " + recorded;
            var tmp = path + ".tmp";
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
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
            }
            catch { try { File.Delete(tmp); } catch { } throw; }   // never leave a half-written .tmp beside the catalog (review M6)
            rep.Bytes = new FileInfo(path).Length;
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
                w.WriteNumber("strength", Dec(k.Strength, 2)); w.WriteNumber("agreement", k.Agreement); w.WriteNumber("votesPresent", k.VotesPresent);
                w.WriteEndObject();
            }
            else w.WriteNullValue();
            w.WritePropertyName("bpm");
            if (folded && e.Bpm != null)
            {
                w.WriteStartObject(); w.WriteNumber("value", Dec(e.Bpm.Value, 1)); w.WriteNumber("beats", e.Bpm.Beats); w.WriteNumber("regularity", Dec(e.Bpm.Regularity, 2)); w.WriteEndObject();
            }
            else w.WriteNullValue();
            w.WritePropertyName("loudness");
            if (folded && e.Loudness != null)
            {
                var l = e.Loudness;
                w.WriteStartObject();
                Num(w, "startLufs", l.StartLufs, 1); Num(w, "endLufs", l.EndLufs, 1); Num(w, "meanLufs", l.MeanLufs, 1);
                w.WritePropertyName("fadeMs"); if (l.FadeMs.HasValue) w.WriteNumberValue(l.FadeMs.Value); else w.WriteNullValue();
                w.WriteNumber("silenceMs", l.SilenceMs); w.WriteString("silenceSource", l.SilenceSource);
                w.WriteEndObject();
            }
            else w.WriteNullValue();
            w.WritePropertyName("texture");
            if (folded && e.Whole != null)
            {
                w.WriteStartObject();
                Arr(w, "mfccMean", e.Whole.MfccMean, 1); Arr(w, "mfccSpread", e.Whole.MfccSpread, 1);
                Arr(w, "spectralMean", e.Whole.SpectralMean, 4); Arr(w, "spectralSpread", e.Whole.SpectralSpread, 4);
                w.WriteEndObject();
            }
            else w.WriteNullValue();
            w.WritePropertyName("divergence");
            if (folded && e.Divergence != null) WriteDivergence(w, e.Divergence); else w.WriteNullValue();
            w.WritePropertyName("rms");
            if (folded && e.Rms != null)
            {
                w.WriteStartObject(); Num(w, "crestDb", e.Rms.CrestDb, 2); w.WriteNumber("transitionMs", e.Rms.TransitionMs); w.WriteEndObject();
            }
            else w.WriteNullValue();
            w.WriteEndObject();
        }

        static void WriteDivergence(Utf8JsonWriter w, EdgeDivergence d)
        {
            w.WriteStartObject(); Num(w, "loudness", d.Loudness, 2); Num(w, "texture", d.Texture, 2); Num(w, "harmony", d.Harmony, 2); w.WriteEndObject();
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

        static decimal Dec(double v, int dp)
        {
            double r = Math.Round(v, dp);
            if (r > 7.9e28 || r < -7.9e28) return (decimal)Math.Sign(r) * 79000000000000000000000000000m;
            return decimal.Round((decimal)r, dp);
        }
    }
}
