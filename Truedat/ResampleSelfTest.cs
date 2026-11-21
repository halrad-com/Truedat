using System;
using System.Diagnostics;
using System.IO;

namespace Truedat
{
    /// <summary>
    /// The conversions ffmpeg does for a scan (resample to 44.1 kHz, downmix to stereo) and the
    /// two rules that decide them: a conversion runs for as long as it is making progress, with no
    /// wall-clock cap, and it is not started when its output would not fit in the staging folder.
    ///
    /// Before this a fixed 300 s cap sat on each of them. The work is proportional to the length
    /// of the track, so the cap fired on exactly the tracks where falling back cost the most, after
    /// spending the 300 s.
    /// </summary>
    internal static class ResampleSelfTest
    {
        internal static void Run(Action<bool, string> assert)
        {
            Console.WriteLine("Conversions: run while progressing, and only when the copy fits");
            TestRoom(assert);
            TestWait(assert);
            TestResampleCall(assert);
        }

        static void TestRoom(Action<bool, string> assert)
        {
            const long GiB = 1L << 30;
            long minute = Program.ResampledBytes(60, 2);
            assert(minute == 60L * 44100 * 2 * 4 + Program.WavHeaderAllowance, $"resample-room: a minute of stereo is its samples as 32-bit float plus the header ({minute:N0})");
            assert(Program.ResampledBytes(60, 1) * 2 - Program.WavHeaderAllowance == minute, "resample-room: mono is half of it");
            assert(Program.ResampledBytes(8 * 3600, 2) > 4 * GiB, $"resample-room: eight hours of stereo is past 4 GiB ({Program.ResampledBytes(8 * 3600, 2):N0})");
            assert(Program.ResampledBytes(0, 2) == 0 && Program.ResampledBytes(-1, 2) == 0 && Program.ResampledBytes(60, 0) == 0,
                "resample-room: an unknown length or channel count gives no estimate, not a small one");

            long need = 10 * GiB, head = Program.StageHeadroomBytes;
            assert(Program.ResampleFits(need, need + head, 0), "resample-room: exactly the copy plus the headroom fits");
            assert(!Program.ResampleFits(need, need + head - 1, 0), "resample-room: one byte short does not");
            assert(!Program.ResampleFits(need, need + head + GiB, 2 * GiB), "resample-room: room already promised to conversions in flight is not counted twice");
            assert(Program.ResampleFits(need, need + head + 2 * GiB, 2 * GiB), "resample-room: with their share set aside and enough left, it fits");
            assert(Program.ResampleFits(0, 1, 0), "resample-room: with no estimate there is nothing to refuse on (the conversion's own failure covers a full disk)");
            assert(Program.ResampleFits(need, -1, 0), "resample-room: free space that cannot be read is not evidence of a full disk");
        }

        /// <summary>The wait itself, against real processes: one that works, one that sits, one that is cancelled.</summary>
        static void TestWait(Action<bool, string> assert)
        {
            var cmd = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrEmpty(cmd) || !File.Exists(cmd)) { Console.WriteLine("  NOT RUN  conversion wait: cmd.exe not found"); return; }

            // Busy for a few seconds — far longer than the 1 s idle window it is given. A wall-clock cap of that size would stop it.
            var sw = Stopwatch.StartNew();
            using (var busy = Start(cmd!, "/c for /L %i in (1,1,60000) do @ver >nul"))
            {
                var outcome = SubprocessWait.WhileProgressing(busy, pollMs: 200, maxIdlePolls: 5);
                sw.Stop();
                if (!busy.HasExited) { try { busy.Kill(); } catch { } }
                assert(outcome == SubprocessWait.Outcome.Exited, $"conversion-wait: a process that keeps working is left to finish ({outcome} after {sw.Elapsed.TotalSeconds:F1} s)");
                assert(sw.Elapsed.TotalSeconds > 1.0, $"conversion-wait: and it ran past the idle window, so the window is not a cap on how long work may take ({sw.Elapsed.TotalSeconds:F1} s)");
            }

            // Waiting on input that never comes: no CPU, no bytes read or written.
            sw.Restart();
            using (var idle = Start(cmd!, "/c pause", holdInput: true))
            {
                var outcome = SubprocessWait.WhileProgressing(idle, pollMs: 200, maxIdlePolls: 5);
                sw.Stop();
                bool alive = !idle.HasExited;
                try { idle.Kill(); } catch { }
                assert(outcome == SubprocessWait.Outcome.Stalled, $"conversion-wait: a process doing nothing is called stalled ({outcome} after {sw.Elapsed.TotalSeconds:F1} s)");
                assert(alive, "conversion-wait: the wait reports the stall and leaves the stopping to its caller");
                assert(sw.Elapsed.TotalSeconds < 10, $"conversion-wait: within its window, not after some longer cap ({sw.Elapsed.TotalSeconds:F1} s)");
            }

            // Working, but past its ceiling: a process that never stops working must still be stopped by something.
            sw.Restart();
            using (var busy = Start(cmd!, "/c for /L %i in (1,1,60000) do @ver >nul"))
            {
                var outcome = SubprocessWait.WhileProgressing(busy, pollMs: 100, maxIdlePolls: 500, maxMs: 500);
                sw.Stop();
                bool alive = !busy.HasExited;
                try { busy.Kill(); } catch { }
                assert(outcome == SubprocessWait.Outcome.Overran && alive, $"conversion-wait: a process still working at its ceiling is reported, not waited on ({outcome} after {sw.Elapsed.TotalSeconds:F1} s)");
            }

            long five = 300 * 1000L;
            assert(Program.ConversionCeilingMs(60) == five, $"conversion-ceiling: a short track gets the floor ({Program.ConversionCeilingMs(60):N0} ms)");
            assert(Program.ConversionCeilingMs(300) == five && Program.ConversionCeilingMs(301) == 301 * 1000L, "conversion-ceiling: past the floor the ceiling is the track's own length");
            assert(Program.ConversionCeilingMs(8 * 3600) == 8 * 3600 * 1000L, $"conversion-ceiling: eight hours of audio may take eight hours ({Program.ConversionCeilingMs(8 * 3600):N0} ms)");
            assert(Program.ConversionCeilingMs(0) == 0 && Program.ConversionCeilingMs(-5) == 0, "conversion-ceiling: an unknown length sets no ceiling (the stall rule still applies)");

            using (var idle = Start(cmd!, "/c pause", holdInput: true))
            {
                var outcome = SubprocessWait.WhileProgressing(idle, pollMs: 200, maxIdlePolls: 500, cancelled: () => true);
                try { idle.Kill(); } catch { }
                assert(outcome == SubprocessWait.Outcome.Cancelled, $"conversion-wait: a stop request ends the wait at the next poll ({outcome})");
            }
        }

        /// <summary>The resample call itself, with real ffmpeg: it converts when there is room and does not start when there is not.</summary>
        static void TestResampleCall(Action<bool, string> assert)
        {
            var exeDir = Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? ".";
            var ffmpeg = Path.Combine(exeDir, "ffmpeg.exe");
            if (!File.Exists(ffmpeg)) { Console.WriteLine("  NOT RUN  resample call: needs ffmpeg.exe beside the test binary"); return; }
            var root = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "resample-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                var source = Path.Combine(root, "tone48k.flac");
                using (var make = Start(ffmpeg, "-v error -f lavfi -i sine=frequency=440:duration=4 -ac 2 -ar 48000 -y \"" + source + "\""))
                    make.WaitForExit(60000);
                if (!File.Exists(source)) { assert(false, "resample-call: the 48 kHz fixture could not be made"); return; }

                long need = Program.ResampledBytes(4, 2);
                var made = Program.ResampleForTest(source, 48000, 2, 4, root, freeBytes: need + Program.StageHeadroomBytes, out string roomy);
                assert(made != null && File.Exists(made), "resample-call: with room for the copy, the copy is made" + (made == null ? " (" + roomy.Trim() + ")" : ""));
                if (made != null)
                {
                    long len = new FileInfo(made).Length;
                    assert(Math.Abs(len - need) < 64 * 1024, $"resample-call: and it is the size the estimate said ({len:N0} against {need:N0})");
                    try { File.Delete(made); } catch { }
                }

                var skipped = Program.ResampleForTest(source, 48000, 2, 4, root, freeBytes: need + Program.StageHeadroomBytes - 1, out string tight);
                assert(skipped == null, "resample-call: one byte short of room, no copy is made");
                assert(tight.Contains("tone48k.flac") && tight.Contains("free"), $"resample-call: and the line says which file and why ({tight.Trim()})");
                assert(Directory.GetFiles(root, "truedat_44k_*").Length == 0, "resample-call: nothing is left in the staging folder either way");
                assert(Program.ConversionReservedBytes == 0, $"resample-call: and no staging room stays promised once the calls have returned ({Program.ConversionReservedBytes:N0} B)");
            }
            catch (Exception ex) { assert(false, "resample-call: threw " + ex.GetType().Name + ": " + ex.Message); }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        static Process Start(string exe, string args, bool holdInput = false)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe, Arguments = args, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            };
            var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + exe);
            p.StandardOutput.ReadToEndAsync(); p.StandardError.ReadToEndAsync();
            if (!holdInput) p.StandardInput.Close();
            return p;
        }
    }
}
