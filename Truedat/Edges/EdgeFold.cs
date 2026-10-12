using System;
using System.Collections.Generic;

namespace Truedat.Edges
{
    /// <summary>Producer settings (operator, 2026-10-11): ride on the producer version so artifacts made under different
    /// settings coexist. Turning a knob DOWN is arithmetic on what is stored; turning it UP is a re-extraction.</summary>
    internal sealed class EdgeSettings
    {
        public int EdgeSeconds = 30;
        public int PoolHz = 10;
        /// <summary>Tracks shorter than this (seconds) get no edge rows at all: whole-track only (RS02).</summary>
        public int MinTrackSeconds = 20;
        public string ProducerVersion => $"edges@v1;e{EdgeSeconds};p{PoolHz}";
    }

    internal sealed class EdgeWindowSummary
    {
        public int StartMs, EndMs;
        public double[] HpcpMean = new double[36];
        public EdgeKeyVote? Key;
        public double[] MfccMean = new double[13], MfccSpread = new double[13];
        public double[] SpectralMean = new double[11], SpectralSpread = new double[11];
    }

    internal sealed class EdgeLoudness
    {
        public double? StartLufs, EndLufs, MeanLufs; public int? FadeMs; public int SilenceMs; public string SilenceSource = "ebu";
    }

    internal sealed class EdgeBpm { public double Value; public int Beats; public double Regularity; }

    internal sealed class EdgeDivergence { public double? Loudness, Texture, Harmony; }

    /// <summary>One edge of the ruled 6 KB row: status word, window, the stored series (EBU pair as centi-dB, beats), the
    /// summaries over the edge and its three sub-windows, and the hub-facing scalars.</summary>
    internal sealed class EdgeRow
    {
        public string Kind = "";                 // head | tail
        public string Status = "unavailable";    // ok | short-context | unavailable | failed
        public int StartMs, EndMs;
        public short[] MomentaryCentiDb = Array.Empty<short>();
        public short[] ShortTermCentiDb = Array.Empty<short>();
        public int[] BeatsMs = Array.Empty<int>();
        public float[] BeatsLoudness = Array.Empty<float>();
        public EdgeWindowSummary? Whole;         // over the edge
        public EdgeWindowSummary[] Sub = Array.Empty<EdgeWindowSummary>();   // three sub-windows
        public EdgeBpm? Bpm;
        public EdgeLoudness? Loudness;
        public EdgeDivergence? Divergence;       // this edge vs the whole track
        public string? Error;
    }

    internal sealed class EdgeFoldResult
    {
        public EdgeRow Head = new EdgeRow { Kind = "head" }, Tail = new EdgeRow { Kind = "tail" };
        public EdgeDivergence? HeadTail;         // head vs tail
        public int DurationMs;
        public string ProducerVersion = "";
    }

    /// <summary>S2 C4 — folds a frames file into the ruled row. Time-addressed only (constraint 1): low-level frames at
    /// 1024/44100 s, tonal frames at 2048/44100 s, EBU at 0.1 s hops, beats by their positions; never a frame index shared
    /// across arrays. Loudness from the EBU series only (constraint 4). Key per window through <see cref="EdgeKey"/>
    /// (constraint 5). Tempo from in-window beat intervals, sparse beats reported as sparse (constraint 6). A descriptor
    /// the fold needs but the file lacks fails the edge ("failed", with the name), never substitutes (constraint 2).
    /// Pure: reads the frames file and returns the row; nothing here touches the store or the audio.</summary>
    internal static class EdgeFold
    {
        internal const double LowLevelHop = 1024.0 / 44100.0;
        internal const double TonalHop = 2048.0 / 44100.0;
        internal const double EbuHop = 0.1;
        internal const double SilenceLufs = -60;       // the EBU fallback floor for silenceMs (the PCM pass uses -60 dBFS slice peaks)
        internal const double FadeMinLu = 10;          // a fade is a monotonic ramp of at least this much ...
        internal const int FadeMinMs = 1000;           // ... lasting at least this long; anything else is a cut (fadeMs null)
        internal static readonly string[] SpectralOrder =
        {
            "lowlevel.spectral_centroid", "lowlevel.spectral_flux", "lowlevel.spectral_rolloff", "lowlevel.spectral_entropy",
            "lowlevel.spectral_complexity", "lowlevel.dissonance", "lowlevel.pitch_salience", "lowlevel.zerocrossingrate",
            "lowlevel.spectral_rms", "lowlevel.spectral_energy", "lowlevel.spectral_strongpeak",
        };

        sealed class Inputs
        {
            public float[] Momentary = Array.Empty<float>(), ShortTerm = Array.Empty<float>(), Hpcp = Array.Empty<float>(), Mfcc = Array.Empty<float>();
            public float[][] Spectral = Array.Empty<float[]>();
            public float[] BeatPos = Array.Empty<float>(), BeatLoud = Array.Empty<float>();
            public double? Integrated;
            public double[] WholeMfcc = new double[13], WholeHpcp = new double[36];
        }

        internal static EdgeFoldResult Fold(EspbFile frames, double durationSec, EdgeSettings settings)
        {
            var r = new EdgeFoldResult { DurationMs = (int)Math.Round(durationSec * 1000), ProducerVersion = settings.ProducerVersion };
            double half = durationSec / 2;
            if (durationSec < settings.MinTrackSeconds)
            {
                r.Head.StartMs = 0; r.Head.EndMs = (int)Math.Round(half * 1000); r.Tail.StartMs = r.Head.EndMs; r.Tail.EndMs = r.DurationMs;
                r.Head.Status = r.Tail.Status = "unavailable";
                return r;
            }
            double e = Math.Min(settings.EdgeSeconds, half); bool clipped = e < settings.EdgeSeconds;
            r.Head.StartMs = 0; r.Head.EndMs = (int)Math.Round(e * 1000);
            r.Tail.StartMs = (int)Math.Round((durationSec - e) * 1000); r.Tail.EndMs = r.DurationMs;

            var missing = new List<string>();
            var inp = Load(frames, missing);
            if (missing.Count > 0)
            {
                var msg = "frames file lacks a declared input: " + string.Join(", ", missing);
                r.Head.Status = r.Tail.Status = "failed"; r.Head.Error = r.Tail.Error = msg;
                return r;
            }

            FoldEdge(r.Head, 0, e, clipped, inp, settings, outerAtStart: true);
            FoldEdge(r.Tail, durationSec - e, durationSec, clipped, inp, settings, outerAtStart: false);
            if (r.Head.Whole != null && r.Tail.Whole != null)
                r.HeadTail = Diverge(r.Head.Whole.MfccMean, r.Tail.Whole.MfccMean, r.Head.Whole.HpcpMean, r.Tail.Whole.HpcpMean, r.Head.Loudness?.MeanLufs, r.Tail.Loudness?.MeanLufs);
            return r;
        }

        static Inputs Load(EspbFile f, List<string> missing)
        {
            var inp = new Inputs();
            if (f.TryGetF32("lowlevel.loudness_ebu128.momentary", out var mom, out _)) inp.Momentary = mom; else missing.Add("lowlevel.loudness_ebu128.momentary");
            if (f.TryGetF32("lowlevel.loudness_ebu128.short_term", out var st, out _)) inp.ShortTerm = st; else missing.Add("lowlevel.loudness_ebu128.short_term");
            if (f.TryGetF32("tonal.hpcp", out var hp, out var hd) && hd == 36) inp.Hpcp = hp; else missing.Add("tonal.hpcp");
            if (f.TryGetF32("lowlevel.mfcc", out var mf, out var md) && md == 13) inp.Mfcc = mf; else missing.Add("lowlevel.mfcc");
            inp.Spectral = new float[SpectralOrder.Length][];
            for (int k = 0; k < SpectralOrder.Length; k++)
            {
                if (f.TryGetF32(SpectralOrder[k], out var sv, out _)) inp.Spectral[k] = sv; else { inp.Spectral[k] = Array.Empty<float>(); missing.Add(SpectralOrder[k]); }
            }
            if (f.TryGetF32("rhythm.beats_position", out var bp, out _)) inp.BeatPos = bp;                         // optional: sparse beats are reported as sparse
            if (f.TryGetF32("rhythm.beats_loudness", out var bl, out _) && bl.Length == inp.BeatPos.Length) inp.BeatLoud = bl;
            if (f.TryGetF32("lowlevel.loudness_ebu128.integrated", out var ig, out _) && ig.Length == 1) inp.Integrated = ig[0];
            if (missing.Count > 0) return inp;
            // whole-track references for the divergence scalars
            int mfccFrames = inp.Mfcc.Length / 13;
            for (int i = 0; i < mfccFrames; i++) for (int c = 0; c < 13; c++) inp.WholeMfcc[c] += inp.Mfcc[i * 13 + c];
            if (mfccFrames > 0) for (int c = 0; c < 13; c++) inp.WholeMfcc[c] /= mfccFrames;
            int hpcpFrames = inp.Hpcp.Length / 36;
            for (int i = 0; i < hpcpFrames; i++) for (int b = 0; b < 36; b++) inp.WholeHpcp[b] += inp.Hpcp[i * 36 + b];
            if (hpcpFrames > 0) for (int b = 0; b < 36; b++) inp.WholeHpcp[b] /= hpcpFrames;
            return inp;
        }

        static void FoldEdge(EdgeRow row, double start, double end, bool clipped, Inputs inp, EdgeSettings s, bool outerAtStart)
        {
            row.Status = clipped ? "short-context" : "ok";
            // --- stored series: the EBU pair pooled at PoolHz as int16 centi-dB
            row.MomentaryCentiDb = PoolCentiDb(inp.Momentary, start, end, s.PoolHz);
            row.ShortTermCentiDb = PoolCentiDb(inp.ShortTerm, start, end, s.PoolHz);
            // --- beats inside the window, ms from the track start
            var beatsMs = new List<int>(); var beatsLoud = new List<float>();
            for (int i = 0; i < inp.BeatPos.Length; i++)
            {
                double p = inp.BeatPos[i];
                if (p >= start && p < end) { beatsMs.Add((int)Math.Round(p * 1000)); if (inp.BeatLoud.Length == inp.BeatPos.Length) beatsLoud.Add(inp.BeatLoud[i]); }
            }
            row.BeatsMs = beatsMs.ToArray(); row.BeatsLoudness = beatsLoud.ToArray();
            row.Bpm = BpmFrom(row.BeatsMs);
            // --- summaries over the edge and its three sub-windows
            row.Whole = Summarise(start, end, inp);
            double third = (end - start) / 3;
            row.Sub = new[] { Summarise(start, start + third, inp), Summarise(start + third, start + 2 * third, inp), Summarise(start + 2 * third, end, inp) };
            // --- loudness scalars from the momentary series (EBU, the fallback source; the PCM pass may replace silenceMs)
            row.Loudness = LoudnessFrom(inp.Momentary, start, end, outerAtStart);
            row.Divergence = Diverge(row.Whole.MfccMean, inp.WholeMfcc, row.Whole.HpcpMean, inp.WholeHpcp, row.Loudness.MeanLufs, inp.Integrated);
        }

        /// <summary>EBU frames (0.1 s hop, frame i at t = 0.1 i) inside [start, end), averaged per 1/poolHz bin, as centi-dB.</summary>
        static short[] PoolCentiDb(float[] series, double start, double end, int poolHz)
        {
            int bins = Math.Max(0, (int)Math.Round((end - start) * poolHz));
            var outp = new short[bins];
            double binLen = 1.0 / poolHz;
            for (int b = 0; b < bins; b++)
            {
                double bs = start + b * binLen, be = bs + binLen;
                int i0 = (int)Math.Ceiling(bs / EbuHop - 1e-9), i1 = (int)Math.Ceiling(be / EbuHop - 1e-9);   // frames with t in [bs, be)
                double sum = 0; int n = 0;
                for (int i = Math.Max(0, i0); i < Math.Min(series.Length, i1); i++) { sum += series[i]; n++; }
                double v = n > 0 ? sum / n : double.NaN;
                outp[b] = double.IsNaN(v) ? short.MinValue : (short)Math.Max(short.MinValue + 1, Math.Min(short.MaxValue, Math.Round(v * 100)));
            }
            return outp;
        }

        static EdgeBpm? BpmFrom(int[] beatsMs)
        {
            if (beatsMs.Length < 5) return null;          // under 4 intervals: sparse, say so with null (constraint 6)
            var iv = new double[beatsMs.Length - 1];
            for (int i = 1; i < beatsMs.Length; i++) iv[i - 1] = (beatsMs[i] - beatsMs[i - 1]) / 1000.0;
            var sorted = (double[])iv.Clone(); Array.Sort(sorted);
            double median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
            if (median <= 0) return null;
            double mean = 0; foreach (var x in iv) mean += x; mean /= iv.Length;
            double var = 0; foreach (var x in iv) var += (x - mean) * (x - mean); var /= iv.Length;
            double regularity = mean > 0 ? Math.Max(0, Math.Min(1, 1 - Math.Sqrt(var) / mean)) : 0;
            return new EdgeBpm { Value = 60.0 / median, Beats = beatsMs.Length, Regularity = regularity };
        }

        static EdgeWindowSummary Summarise(double start, double end, Inputs inp)
        {
            var w = new EdgeWindowSummary { StartMs = (int)Math.Round(start * 1000), EndMs = (int)Math.Round(end * 1000) };
            // tonal frames: t_i = i * TonalHop
            int t0 = (int)Math.Ceiling(start / TonalHop - 1e-9), t1 = (int)Math.Ceiling(end / TonalHop - 1e-9), tonalFrames = inp.Hpcp.Length / 36, nt = 0;
            for (int i = Math.Max(0, t0); i < Math.Min(tonalFrames, t1); i++, nt++) for (int b = 0; b < 36; b++) w.HpcpMean[b] += inp.Hpcp[i * 36 + b];
            if (nt > 0) for (int b = 0; b < 36; b++) w.HpcpMean[b] /= nt;
            w.Key = nt > 0 ? EdgeKey.Vote(w.HpcpMean) : null;
            // low-level frames: t_i = i * LowLevelHop
            int l0 = (int)Math.Ceiling(start / LowLevelHop - 1e-9), l1 = (int)Math.Ceiling(end / LowLevelHop - 1e-9), llFrames = inp.Mfcc.Length / 13;
            int from = Math.Max(0, l0), to = Math.Min(llFrames, l1), n = Math.Max(0, to - from);
            if (n > 0)
            {
                for (int c = 0; c < 13; c++)
                {
                    double m = 0; for (int i = from; i < to; i++) m += inp.Mfcc[i * 13 + c]; m /= n;
                    double v = 0; for (int i = from; i < to; i++) { double d = inp.Mfcc[i * 13 + c] - m; v += d * d; }
                    w.MfccMean[c] = m; w.MfccSpread[c] = Math.Sqrt(v / n);
                }
                for (int k = 0; k < SpectralOrder.Length; k++)
                {
                    var sv = inp.Spectral[k]; int sto = Math.Min(sv.Length, l1), sn = Math.Max(0, sto - from);
                    if (sn == 0) continue;
                    double m = 0; for (int i = from; i < sto; i++) m += sv[i]; m /= sn;
                    double v = 0; for (int i = from; i < sto; i++) { double d = sv[i] - m; v += d * d; }
                    w.SpectralMean[k] = m; w.SpectralSpread[k] = Math.Sqrt(v / sn);
                }
            }
            return w;
        }

        /// <summary>From the momentary series inside [start, end): level at the outer and inner second, mean, the outer-end
        /// silence run (frames under -60 LUFS) and the fade: the span from the outer boundary of the non-silent region to the
        /// frame where the level stops changing monotonically, kept only when it spans ≥ FadeMinMs and ≥ FadeMinLu.</summary>
        static EdgeLoudness LoudnessFrom(float[] momentary, double start, double end, bool outerAtStart)
        {
            int i0 = Math.Max(0, (int)Math.Ceiling(start / EbuHop - 1e-9)), i1 = Math.Min(momentary.Length, (int)Math.Ceiling(end / EbuHop - 1e-9));
            var l = new EdgeLoudness();
            if (i1 - i0 <= 0) return l;
            int perSec = (int)Math.Round(1 / EbuHop);
            l.MeanLufs = Mean(momentary, i0, i1);
            l.StartLufs = Mean(momentary, i0, Math.Min(i1, i0 + perSec));
            l.EndLufs = Mean(momentary, Math.Max(i0, i1 - perSec), i1);
            // silence run at the outer end
            int silence = 0;
            if (outerAtStart) { for (int i = i0; i < i1 && momentary[i] < SilenceLufs; i++) silence++; }
            else { for (int i = i1 - 1; i >= i0 && momentary[i] < SilenceLufs; i--) silence++; }
            l.SilenceMs = (int)Math.Round(silence * EbuHop * 1000);
            // fade: head = a rising ramp from the first non-silent frame; tail = a falling ramp into the silence run
            const double tol = 0.01;
            if (outerAtStart)
            {
                int b = i0 + silence; int j = b;
                while (j + 1 < i1 && momentary[j + 1] - momentary[j] > tol) j++;
                double rise = j > b ? momentary[j] - momentary[b] : 0; int ms = (int)Math.Round((j - b) * EbuHop * 1000);
                l.FadeMs = rise >= FadeMinLu && ms >= FadeMinMs ? ms : (int?)null;
            }
            else
            {
                int b = i1 - silence;                       // first silent frame (or i1 when there is no silence)
                int j = b - 1;                              // last non-silent frame
                while (j - 1 >= i0 && momentary[j - 1] - momentary[j] > tol) j--;
                double drop = b - 1 > j ? momentary[j] - momentary[b - 1] : 0; int ms = (int)Math.Round((b - j) * EbuHop * 1000);
                l.FadeMs = drop >= FadeMinLu && ms >= FadeMinMs ? ms : (int?)null;
            }
            return l;
        }

        static double Mean(float[] v, int from, int to) { double s = 0; int n = 0; for (int i = from; i < to; i++) { s += v[i]; n++; } return n > 0 ? s / n : double.NaN; }

        /// <summary>Loudness: |Δ| over 20 LU clamped to 0..1. Texture and harmony: cosine distance of the mean vectors.</summary>
        static EdgeDivergence Diverge(double[] mfccA, double[] mfccB, double[] hpcpA, double[] hpcpB, double? lufsA, double? lufsB)
        {
            var d = new EdgeDivergence();
            if (lufsA.HasValue && lufsB.HasValue && !double.IsNaN(lufsA.Value) && !double.IsNaN(lufsB.Value)) d.Loudness = Math.Max(0, Math.Min(1, Math.Abs(lufsA.Value - lufsB.Value) / 20.0));
            d.Texture = CosineDistance(mfccA, mfccB);
            d.Harmony = CosineDistance(hpcpA, hpcpB);
            return d;
        }

        static double? CosineDistance(double[] a, double[] b)
        {
            if (a.Length != b.Length) return null;
            double dot = 0, na = 0, nb = 0;
            for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
            if (na == 0 || nb == 0) return null;
            return Math.Max(0, Math.Min(1, 1 - dot / Math.Sqrt(na * nb)));
        }
    }
}
