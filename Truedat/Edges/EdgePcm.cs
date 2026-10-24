using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace Truedat.Edges
{
    /// <summary>The three RMS-option-D scalars for one edge (hub fixture <c>rmsDefinitions</c>).</summary>
    internal sealed class EdgePcmResult
    {
        public double? CrestDb;      // 20*log10(max 100 ms slice sample peak / mean 100 ms slice RMS); null when mean RMS is 0
        public int TransitionMs;     // outer end: ms from the last slice within 20 dB of the window mean RMS to the silence floor; <= 400 reads as a cut
        public int SilenceMs;        // outer-end run of 100 ms slices whose sample max is below -60 dBFS
        public int Slices;
    }

    /// <summary>S2 C4 — the truedat-side PCM pass over one edge: one ffmpeg pipe (mono f32le at 44.1 kHz, same machinery as
    /// bitUsage / HF), 100 ms slices, three scalars; the slice series is never stored. Null without ffmpeg (never the spectral
    /// pool approximation), null when the window is under 1 s or the decode fails. Reads whatever path it is given — the caller
    /// hands it the staged copy so a network source is read once.</summary>
    internal static class EdgePcm
    {
        internal const int SampleRate = 44100;
        internal const int SliceSamples = SampleRate / 10;           // 100 ms
        internal const double SilencePeak = 0.001;                   // -60 dBFS
        internal const double WithinDbOfMean = 20;
        const int MinSlices = 10;                                    // under 1 s the definitions say null

        /// <summary>Pure: the slice math over decoded mono samples. Only whole 100 ms slices count.</summary>
        internal static EdgePcmResult? FromSamples(float[] mono, bool outerAtStart)
        {
            int slices = mono.Length / SliceSamples;
            if (slices < MinSlices) return null;
            var rms = new double[slices]; var peak = new double[slices];
            double rmsSum = 0, maxPeak = 0;
            for (int s = 0; s < slices; s++)
            {
                double sq = 0, pk = 0; int o = s * SliceSamples;
                for (int i = 0; i < SliceSamples; i++) { double x = mono[o + i]; sq += x * x; double a = Math.Abs(x); if (a > pk) pk = a; }
                rms[s] = Math.Sqrt(sq / SliceSamples); peak[s] = pk; rmsSum += rms[s]; if (pk > maxPeak) maxPeak = pk;
            }
            double meanRms = rmsSum / slices;
            var r = new EdgePcmResult { Slices = slices };
            r.CrestDb = meanRms > 0 && maxPeak > 0 ? Math.Round(20 * Math.Log10(maxPeak / meanRms), 2) : (double?)null;
            // silence run at the outer end: slices whose sample max is under -60 dBFS
            int silence = 0;
            if (outerAtStart) { for (int s = 0; s < slices && peak[s] < SilencePeak; s++) silence++; }
            else { for (int s = slices - 1; s >= 0 && peak[s] < SilencePeak; s--) silence++; }
            r.SilenceMs = silence * 100;
            // transition: from the last slice within 20 dB of the window's mean RMS (walking toward the outer end) to the silence floor
            double floor = meanRms * Math.Pow(10, -WithinDbOfMean / 20);
            if (meanRms <= 0) { r.TransitionMs = 0; return r; }
            if (outerAtStart)
            {
                int first = silence;                                                   // first non-silent slice (the floor's inner edge)
                int j = first; while (j < slices && rms[j] < floor) j++;               // first slice back within 20 dB of the mean
                r.TransitionMs = j >= slices ? (slices - first) * 100 : (j - first) * 100;
            }
            else
            {
                int floorStart = slices - silence;                                     // first silent slice (slices when there is none)
                int j = floorStart - 1; while (j >= 0 && rms[j] < floor) j--;          // last slice within 20 dB of the mean
                r.TransitionMs = j < 0 ? floorStart * 100 : (floorStart - 1 - j) * 100;
            }
            return r;
        }

        /// <summary>One ffmpeg pipe over [startSec, startSec + lengthSec): mono f32le at 44.1 kHz, folded slice by slice; the
        /// decoded audio never touches disk. Null without ffmpeg, on a decode failure, or when under 1 s decodes.</summary>
        /// <summary>How long the pipe may go without reaching EOF before ffmpeg is killed and the window reported null. The read
        /// used to run to EOF with the 60 s WaitForExit after it, so an ffmpeg that stalled with stdout open (a NAS read that
        /// never returns) blocked the worker, the scan's end and a graceful Ctrl+C for good (codex review 2026-10-20 #9).
        /// Static, not const, so the self-test can shorten it.</summary>
        internal static int StallTimeoutMs = 60_000;

        /// <summary>The least of the requested window a decode must deliver to count as that window. A container that overstates its
        /// length by a second or two (a VBR MP3 with no Xing header) still decodes most of a 30 s tail; a decode that stopped well short
        /// did not measure the window it was asked for.</summary>
        internal const double MinWindowCoverage = 0.8;

        /// <summary>codex 2026-10-23 F7: a failed decode is not a measurement however many samples came out before it failed — accepted,
        /// the truncated prefix's end became the track's "tail" and plausible silence/transition values went to the store for the wrong
        /// ending. Likewise a decode that delivered materially less than the window. Either leaves the EBU answer standing (null).</summary>
        internal static bool Covers(int exitCode, int samples, double lengthSec)
            => exitCode == 0 && samples >= (long)(lengthSec * SampleRate * MinWindowCoverage);

        internal static EdgePcmResult? Analyze(string audioPath, string? ffmpegExe, double startSec, double lengthSec, bool outerAtStart)
        {
            if (string.IsNullOrEmpty(ffmpegExe) || lengthSec < 1) return null;
            var inv = CultureInfo.InvariantCulture;
            string ss = startSec > 0 ? $"-ss {startSec.ToString("F3", inv)} " : "";
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegExe!,
                Arguments = $"-v error {ss}-t {lengthSec.ToString("F3", inv)} -i {PathHelper.QuoteArg(audioPath)} -ac 1 -ar {SampleRate} -f f32le pipe:1",
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            try
            {
                using var proc = Process.Start(psi);
                if (proc == null) return null;
                Program.PrepareChildProcess(proc);
                var stderrTask = proc.StandardError.ReadToEndAsync();          // drain so a chatty ffmpeg cannot deadlock the pipe
                var samples = new List<float>((int)Math.Min(int.MaxValue / 8, (lengthSec + 1) * SampleRate));
                var buf = new byte[64 * 1024]; int residue = 0;
                var stream = proc.StandardOutput.BaseStream;
                // The drain runs under the deadline: a Read that never returns is ended by killing the child, which closes the pipe.
                var drain = System.Threading.Tasks.Task.Run(() =>
                {
                    while (true)
                    {
                        int got = stream.Read(buf, residue, buf.Length - residue);
                        if (got <= 0) break;
                        int total = residue + got, whole = total / 4;
                        for (int i = 0; i < whole; i++) samples.Add(BitConverter.ToSingle(buf, i * 4));
                        int used = whole * 4; residue = total - used;
                        if (residue > 0) Buffer.BlockCopy(buf, used, buf, 0, residue);
                    }
                });
                if (!drain.Wait(StallTimeoutMs))
                {
                    try { proc.Kill(); } catch { }
                    try { drain.Wait(5000); } catch { }
                    return null;
                }
                if (!proc.WaitForExit(StallTimeoutMs)) { try { proc.Kill(); } catch { } return null; }
                if (!Covers(proc.ExitCode, samples.Count, lengthSec)) return null;
                return FromSamples(samples.ToArray(), outerAtStart);
            }
            catch { return null; }
        }
    }
}
