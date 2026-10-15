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
        /// <summary>The algorithm generation precedes the settings: v1 = the first fold; v2 (2026-10-12) = texture divergence over
        /// MFCC 1..12 and tempo from the interquartile mean interval. A fold-rule change is a new version so stored rows are never
        /// mistaken for current ones, and --work re-queues the done items for it.</summary>
        public string ProducerVersion => $"edges@v2;e{EdgeSeconds};p{PoolHz}";
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

    /// <summary>RMS option D from the PCM pass; null on the edge when the pass did not run.</summary>
    internal sealed class EdgeRms { public double? CrestDb; public int TransitionMs; }

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
        public EdgeRms? Rms;                     // the PCM pass (null without ffmpeg)
        public string? Error;
    }

    internal sealed class EdgeFoldResult
    {
        public EdgeRow Head = new EdgeRow { Kind = "head" }, Tail = new EdgeRow { Kind = "tail" };
        public EdgeDivergence? HeadTail;         // head vs tail
        public int DurationMs;
        public string ProducerVersion = "";
        public long FoldMs, PcmMs;               // the per-component stopwatch on the run row
        /// <summary>The extractor build the frames came from (e.g. output-x64.5); stored with the rows because the frames depend on it.</summary>
        public string Extractor = "";
        /// <summary>The whole-track word: ok when both edges folded (either status), unavailable under the floor, failed otherwise.</summary>
        public string Status => Head.Status == "failed" || Tail.Status == "failed" ? "failed" : Head.Status == "unavailable" ? "unavailable" : "ok";
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
        // T02: "no level" is the extractor's OWN silence floor, exactly, not a gate. The momentary and short-term series are NOT
        // gated (loudnessebur128.cpp description); a digitally silent window has its power clipped to 1e-30 before log10
        // (unaryoperator.cpp:83-84 / unaryoperatorstream.cpp:83) and is shifted by -0.691 (loudnessebur128.cpp:157-162):
        // 10*log10(1e-30) - 0.691 = -300.691 LUFS. Any representable non-zero power is a reading (1e-22 -> -220.691 stays finite;
        // keeper, dbd20a0 probe). The -70 absolute gate applies to INTEGRATED loudness only and must not erase a genuine -75
        // momentary reading (keeper P2 on 24020d1). The series carry the floor as float32, hence the tolerance.
        internal static readonly double ExtractorSilenceLufs = 10 * Math.Log10(1e-30) - 0.691;
        internal const double ExtractorSilenceToleranceLu = 1e-3;   // float32 ulp at 300 is ~3e-5; 1e-3 covers it with margin
        internal const double FadeMinLu = 10;          // a fade is a departure from the edge's plateau of at least this much ...
        internal const int FadeMinMs = 1000;           // ... lasting at least this long; anything else is a cut (fadeMs null)
        internal const double FadePlateauLu = 1;       // "on the plateau" = within this of the median non-silent level

        /// <summary>EBU frames are TRAILING windows (400 ms momentary, 3 s short-term) emitted every 0.1 s, so a series has
        /// fewer frames than duration/0.1 and its first frame does not sit at t = 0 (a 60 s track: 597 and 571 frames). Frame i
        /// is therefore addressed by the time its window ENDS, anchored to the decoded length: the last frame ends at the end of
        /// the audio, which is the splice point. Measured on the .5 spike's five real files; a frame-i-at-0.1·i rule left the
        /// last 3 and 29 bins of every real tail undefined.</summary>
        internal static double EbuTimeOf(int n, double durationSec, int i) => durationSec - (n - 1 - i) * EbuHop;

        /// <summary>Frames whose window end lies in (a, b] — half-open on the LEFT, so the bin [x, x+0.1) is described by the
        /// window that ends at x+0.1, and the tail's last bin holds the window ending exactly at the track end.</summary>
        internal static void EbuRange(int n, double durationSec, double a, double b, out int i0, out int i1)
        {
            const double eps = 1e-6;
            i0 = (int)Math.Floor(n - 1 - (durationSec - a) / EbuHop + eps) + 1;
            i1 = (int)Math.Floor(n - 1 - (durationSec - b) / EbuHop + eps) + 1;
            i0 = Math.Max(0, Math.Min(n, i0)); i1 = Math.Max(i0, Math.Min(n, i1));
        }
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

            FoldEdge(r.Head, 0, e, clipped, inp, settings, durationSec, outerAtStart: true);
            FoldEdge(r.Tail, durationSec - e, durationSec, clipped, inp, settings, durationSec, outerAtStart: false);
            if (r.Head.Whole != null && r.Tail.Whole != null)
                r.HeadTail = Diverge(r.Head.Whole.MfccMean, r.Tail.Whole.MfccMean, r.Head.Whole.HpcpMean, r.Tail.Whole.HpcpMean, r.Head.Loudness?.MeanLufs, r.Tail.Loudness?.MeanLufs);
            return r;
        }

        /// <summary>The PCM pass replaces the EBU silence run (source 'pcm') and adds the rms block; a null pass leaves the
        /// EBU answer standing, tagged 'ebu', and rms null — never a substitute.</summary>
        internal static void ApplyPcm(EdgeRow row, EdgePcmResult? pcm)
        {
            if (pcm == null || row.Loudness == null) return;
            row.Loudness.SilenceMs = pcm.SilenceMs; row.Loudness.SilenceSource = "pcm";
            row.Rms = new EdgeRms { CrestDb = pcm.CrestDb, TransitionMs = pcm.TransitionMs };
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

        static void FoldEdge(EdgeRow row, double start, double end, bool clipped, Inputs inp, EdgeSettings s, double durationSec, bool outerAtStart)
        {
            row.Status = clipped ? "short-context" : "ok";
            // --- stored series: the EBU pair pooled at PoolHz as int16 centi-dB
            row.MomentaryCentiDb = PoolCentiDb(inp.Momentary, durationSec, start, end, s.PoolHz);
            row.ShortTermCentiDb = PoolCentiDb(inp.ShortTerm, durationSec, start, end, s.PoolHz);
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
            row.Loudness = LoudnessFrom(inp.Momentary, durationSec, start, end, outerAtStart);
            row.Divergence = Diverge(row.Whole.MfccMean, inp.WholeMfcc, row.Whole.HpcpMean, inp.WholeHpcp, row.Loudness.MeanLufs, inp.Integrated);
        }

        /// <summary>EBU frames whose window ends inside each 1/poolHz bin of [start, end), energy-averaged per bin, as centi-dB.
        /// A bin no window ends in (the first 0.3 s / 2.9 s of a track) is <see cref="short.MinValue"/>: undefined, never 0.</summary>
        static short[] PoolCentiDb(float[] series, double durationSec, double start, double end, int poolHz)
        {
            int bins = Math.Max(0, (int)Math.Round((end - start) * poolHz));
            var outp = new short[bins];
            double binLen = 1.0 / poolHz;
            for (int b = 0; b < bins; b++)
            {
                double bs = start + b * binLen, be = bs + binLen;
                EbuRange(series.Length, durationSec, bs, be, out int i0, out int i1);
                double v = EnergyMean(series, i0, i1);
                outp[b] = double.IsNaN(v) ? short.MinValue : (short)Math.Max(short.MinValue + 1, Math.Min(short.MaxValue, Math.Round(v * 100)));
            }
            return outp;
        }

        /// <summary>Tempo from the in-window beat intervals. The beat tracker places beats on a 512-sample hop (11.6 ms at 44.1 kHz),
        /// so a MEDIAN interval is quantised to a hop multiple and unrelated tracks collide on one value (restfulbee, 2026-10-12:
        /// 101.4 bpm = 51 hops on four edges of three tracks). The interquartile mean (intervals between the 25th and 75th
        /// percentile) keeps the median's outlier resistance and averages the quantisation away.</summary>
        static EdgeBpm? BpmFrom(int[] beatsMs)
        {
            if (beatsMs.Length < 5) return null;          // under 4 intervals: sparse, say so with null (constraint 6)
            var iv = new double[beatsMs.Length - 1];
            for (int i = 1; i < beatsMs.Length; i++) iv[i - 1] = (beatsMs[i] - beatsMs[i - 1]) / 1000.0;
            var sorted = (double[])iv.Clone(); Array.Sort(sorted);
            int lo = sorted.Length / 4, hi = sorted.Length - sorted.Length / 4;          // [25%, 75%)
            if (hi <= lo) { lo = 0; hi = sorted.Length; }
            double iqm = 0; for (int i = lo; i < hi; i++) iqm += sorted[i]; iqm /= (hi - lo);
            if (iqm <= 0) return null;
            double mean = 0; foreach (var x in iv) mean += x; mean /= iv.Length;
            double var = 0; foreach (var x in iv) var += (x - mean) * (x - mean); var /= iv.Length;
            double regularity = mean > 0 ? Math.Max(0, Math.Min(1, 1 - Math.Sqrt(var) / mean)) : 0;
            return new EdgeBpm { Value = 60.0 / iqm, Beats = beatsMs.Length, Regularity = regularity };
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

        /// <summary>From the momentary windows ending inside (start, end]: energy-mean level over the outer and inner second and
        /// the whole edge, the outer-end silence run (windows under -60 LUFS; the EBU fallback source), and the fade. A fade is a
        /// departure from the edge's PLATEAU — the median non-silent level — by more than <see cref="FadePlateauLu"/>, measured to
        /// the silence boundary (or the window's outer end when there is no silence), kept when it spans ≥ FadeMinMs and the
        /// level at the boundary sits ≥ FadeMinLu under the plateau. Real momentary loudness wobbles frame to frame, so a
        /// monotonic-ramp rule found no fade on tracks that audibly fade; this one is measured against the .5 spike's real files.</summary>
        static EdgeLoudness LoudnessFrom(float[] momentary, double durationSec, double start, double end, bool outerAtStart)
        {
            EbuRange(momentary.Length, durationSec, start, end, out int i0, out int i1);
            var l = new EdgeLoudness();
            if (i1 - i0 <= 0) return l;
            int perSec = (int)Math.Round(1 / EbuHop);
            l.MeanLufs = LevelOrNull(momentary, i0, i1);
            l.StartLufs = LevelOrNull(momentary, i0, Math.Min(i1, i0 + perSec));
            l.EndLufs = LevelOrNull(momentary, Math.Max(i0, i1 - perSec), i1);
            // silence run at the outer end
            int silence = 0;
            if (outerAtStart) { for (int i = i0; i < i1 && momentary[i] < SilenceLufs; i++) silence++; }
            else { for (int i = i1 - 1; i >= i0 && momentary[i] < SilenceLufs; i--) silence++; }
            l.SilenceMs = (int)Math.Round(silence * EbuHop * 1000);
            // plateau: median of the non-silent windows in the edge
            var loud = new List<float>(i1 - i0);
            for (int i = i0; i < i1; i++) if (momentary[i] >= SilenceLufs) loud.Add(momentary[i]);
            if (loud.Count == 0) return l;
            loud.Sort(); double plateau = loud[loud.Count / 2];
            double n = momentary.Length;
            if (outerAtStart)
            {
                int b = i0 + silence;                                   // first non-silent window
                if (b >= i1) return l;
                int j = b; while (j < i1 && momentary[j] < plateau - FadePlateauLu) j++;   // first window on the plateau
                if (j >= i1) return l;
                double from = silence > 0 ? EbuTimeOf(momentary.Length, durationSec, b - 1) : start;   // the silence boundary, or the window's outer end
                int ms = (int)Math.Round((EbuTimeOf(momentary.Length, durationSec, j) - from) * 1000);
                double rise = plateau - momentary[b];
                l.FadeMs = rise >= FadeMinLu && ms >= FadeMinMs ? ms : (int?)null;
            }
            else
            {
                int b = i1 - silence;                                   // first silent window (i1 when there is no silence)
                if (b <= i0) return l;
                int j = b - 1; while (j >= i0 && momentary[j] < plateau - FadePlateauLu) j--;   // last window on the plateau
                if (j < i0) return l;
                double to = silence > 0 ? EbuTimeOf(momentary.Length, durationSec, b - 1) : end;   // the silence boundary, or the window's outer end
                int ms = (int)Math.Round((to - EbuTimeOf(momentary.Length, durationSec, j)) * 1000);
                double drop = plateau - momentary[b - 1];
                l.FadeMs = drop >= FadeMinLu && ms >= FadeMinMs ? ms : (int?)null;
            }
            return l;
        }

        /// <summary>Loudness combines in the energy domain: 10·log10 of the mean of 10^(L/10). An arithmetic mean of LUFS values
        /// over a window holding digital silence (Essentia emits about -300 for it) says -68 for a tail that is -16 where there is
        /// sound; the energy mean says what a listener hears.</summary>
        static double EnergyMean(float[] v, int from, int to)
        {
            double s = 0; int n = 0;
            for (int i = from; i < to; i++) { s += Math.Pow(10, v[i] / 10.0); n++; }
            if (n == 0) return double.NaN;
            double m = s / n;
            return m > 0 ? 10 * Math.Log10(m) : -300;
        }

        /// <summary>The loudness SCALAR of a range (T02, F6.4): NULL when every window in it sits at the extractor's silence floor
        /// (<see cref="ExtractorSilenceLufs"/>) — digital silence has no measurable level, and the -300.691 sentinel stored as if it
        /// were LUFS put a +276 LU head-to-tail step and a 1.00 loudness divergence on 23.5 % of one library's tracks. Otherwise the
        /// ungated energy mean: a -75 LUFS tail is a genuine finite reading (momentary is not gated — the -70 absolute gate belongs
        /// to integrated loudness; keeper P2), a -65 one is "quiet but measurable", and one sounding window among silent ones lifts
        /// the mean only as far as the energy says. The int16 series keep their raw centi-dB readings — a silent bin there is a
        /// reading, not a level claim.</summary>
        static double? LevelOrNull(float[] v, int from, int to)
        {
            if (to - from <= 0) return null;
            for (int i = from; i < to; i++) if (v[i] > ExtractorSilenceLufs + ExtractorSilenceToleranceLu) return EnergyMean(v, from, to);
            return null;
        }

        /// <summary>Loudness: |Δ| over 20 LU clamped to 0..1. Harmony: cosine distance of the HPCP means. Texture: cosine distance
        /// of MFCC coefficients 1..12 — coefficient 0 is the frame energy (about -800 on real audio) and swamps the rest, so a
        /// cosine over all 13 read 0.00 on every real edge (restfulbee, 2026-10-12); the spectral-shape coefficients are what
        /// "texture" means.</summary>
        static EdgeDivergence Diverge(double[] mfccA, double[] mfccB, double[] hpcpA, double[] hpcpB, double? lufsA, double? lufsB)
        {
            var d = new EdgeDivergence();
            if (lufsA.HasValue && lufsB.HasValue && !double.IsNaN(lufsA.Value) && !double.IsNaN(lufsB.Value) && !double.IsInfinity(lufsA.Value) && !double.IsInfinity(lufsB.Value))
                d.Loudness = Math.Max(0, Math.Min(1, Math.Abs(lufsA.Value - lufsB.Value) / 20.0));   // no level on either side = no divergence, never 1.00 (T02)
            d.Texture = CosineDistance(mfccA, mfccB, from: 1);
            d.Harmony = CosineDistance(hpcpA, hpcpB);
            return d;
        }

        static double? CosineDistance(double[] a, double[] b, int from = 0)
        {
            if (a.Length != b.Length || from >= a.Length) return null;
            double dot = 0, na = 0, nb = 0;
            for (int i = from; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
            if (na == 0 || nb == 0) return null;
            return Math.Max(0, Math.Min(1, 1 - dot / Math.Sqrt(na * nb)));
        }
    }
}
