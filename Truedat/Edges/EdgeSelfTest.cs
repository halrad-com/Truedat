using System;
using System.Collections.Generic;
using System.Linq;

namespace Truedat.Edges
{
    /// <summary>Self-tests for the edges producer (S2 C4). Called from Program.RunSelfTest.</summary>
    internal static class EdgeSelfTest
    {
        internal static void Run(Action<bool, string> assert)
        {
            Console.WriteLine("Edges self-test (S2 C4: per-edge key estimator ported from the extractor's Key algorithm)");
            try { TestEdgeKey(assert); }
            catch (Exception ex) { assert(false, $"EdgeKey: threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine("Edges self-test (S2 C4: ESPB binary frames reader, from essentia-build/FRAMES-BINARY-FORMAT.md)");
            try { TestEspb(assert); }
            catch (Exception ex) { assert(false, $"Espb: threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine("Edges self-test (S2 C4: the fold — frames file -> the ruled 6 KB row)");
            try { TestFold(assert); }
            catch (Exception ex) { assert(false, $"Fold: threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine("Edges self-test (S2 C4: the fold on real frames files from the .5 build spike)");
            try { TestFoldReal(assert); }
            catch (Exception ex) { assert(false, $"FoldReal: threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine("Edges self-test (S2 C4: the PCM pass — 100 ms slices to crest, transition and silence)");
            try { TestPcm(assert); }
            catch (Exception ex) { assert(false, $"Pcm: threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine("Edges self-test (S2 C4: the ruled row in the store — segments, values, idempotent, versions coexist)");
            try { TestStore(assert); }
            catch (Exception ex) { assert(false, $"Store: threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine("Edges self-test (store-v3 T05: a v3 store's packed edge rows move into the typed edge table on open, arrays kept)");
            try { TestV4Migration(assert); }
            catch (Exception ex) { assert(false, $"V4Migration: threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine("Edges self-test (store-v3 T05: sticky capture follows the MEDIA across a content-key move)");
            try { TestKeyMoveSticky(assert); }
            catch (Exception ex) { assert(false, $"KeyMoveSticky: threw {ex.GetType().Name}: {ex.Message}"); }
            finally { EdgeStore.CaptureArraysDefault = false; Truedat.Store.ScanSink.Reset(); }
            Console.WriteLine("Edges self-test (S2 C4: through the scan sink — stash by analysed path, tie at Record, write at the flush)");
            try { TestSink(assert); }
            catch (Exception ex) { assert(false, $"Sink: threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine("Edges self-test (the scan's periodic checkpoint flush files tied folds and leaves in-flight ones alone)");
            try { TestSinkCheckpoint(assert); }
            catch (Exception ex) { assert(false, $"SinkCheckpoint: threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine("Edges self-test (S2 C7: mbxedges.v1.json from the store — the agreed hub shape, scalars only, keyed by sha)");
            try { TestCompanion(assert); }
            catch (Exception ex) { assert(false, $"Companion: threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine("Edges self-test (S2 C4: the .5 extractor build is a known build)");
            try { TestExtractorBuild(assert); }
            catch (Exception ex) { assert(false, $"ExtractorBuild: threw {ex.GetType().Name}: {ex.Message}"); }
            try { TestScanProfile(assert); }
            catch (Exception ex) { assert(false, $"ScanProfile: threw {ex.GetType().Name}: {ex.Message}"); }
            try { TestSpoolCap(assert); }
            catch (Exception ex) { assert(false, $"SpoolCap: threw {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Every committed extractor exe must resolve to its build name, not an MD5 stub: the store's run rows and the
        /// extractor report both go through this table. The edges path needs the binary frames writer, which .5 introduced and .6
        /// (decoder fix, 2026-10-25) carries unchanged — a SET of supported builds keyed on that capability, not one exact string
        /// (plan B6 [c1]: registering .6 alone would have skipped default edges and refused --edges).</summary>
        static void TestExtractorBuild(Action<bool, string> assert)
        {
            foreach (var build in new[] { "output-x64.5", "output-x64.6", "output-x64.7" })
            {
                var exe = FindUpward(AppContext.BaseDirectory, System.IO.Path.Combine("essentia-build", build, "essentia_streaming_extractor_music.exe"));
                if (exe == null) { Console.WriteLine($"  SKIP  essentia-build/{build} exe not found from the test binary"); continue; }
                var id = Program.ExtractorBuildId(exe);
                assert(id == build, $"the committed {build} exe resolves to its build name (got {id})");
            }
            assert(string.Equals(Program.LatestExtractorMd5, "6af75d493dfb3b4949ec1fd433b4c58c", StringComparison.OrdinalIgnoreCase),
                $"the latest known extractor is .7 (9844483: fused HPCP + shared decode; got {Program.LatestExtractorMd5})");
            assert(!EdgeProducer.SupportsFrames("output-x64.4") && EdgeProducer.SupportsFrames("output-x64.5") && EdgeProducer.SupportsFrames("output-x64.6")
                   && EdgeProducer.SupportsFrames("output-x64.7") && !EdgeProducer.SupportsFrames(null),
                $"binary frames: .4 no, .5 / .6 / .7 yes, unknown no ({EdgeProducer.SupportedBuildsText})");
        }

        /// <summary>Extractor .7's RAM spool cap (x7.spoolMaxBytes), written by truedat so ALL workers together stay inside 60 % of RAM: the
        /// worst spooled track costs 106.6 + 0.4141 L + 0.3365 L MiB (the measured fit, essentia-build/spike-x6/mem-fit.txt) with L = cap /
        /// 0.3365, so cap = min(256, max(0, (0.6 RAM / N - 106.6) / (1 + 0.4141 / 0.3365))). Pinned against ram-tiers.txt; a box with
        /// no RAM reading gets 0 = no spool (today's four decodes, same output).</summary>
        static void TestSpoolCap(Action<bool, string> assert)
        {
            const long GiB = 1L << 30;
            double MiB(long bytes) => Math.Round(bytes / 1048576.0, 1);
            var got = new[] { MiB(Program.SpoolCapBytes(8 * GiB, 4)), MiB(Program.SpoolCapBytes(8 * GiB, 8)), MiB(Program.SpoolCapBytes(8 * GiB, 20)),
                              MiB(Program.SpoolCapBytes(16 * GiB, 20)), MiB(Program.SpoolCapBytes(32 * GiB, 20)), MiB(Program.SpoolCapBytes(0, 8)), MiB(Program.SpoolCapBytes(1 * GiB, 20)) };
            assert(got[0] == 256 && got[1] == 227.6 && got[2] == 62.4 && got[3] == 172.6 && got[4] == 256 && got[5] == 0 && got[6] == 0,
                $"spool cap MiB at 8 GiB x4/x8/x20, 16 GiB x20, 32 GiB x20, no RAM reading, 1 GiB x20: {string.Join(" / ", got)} (expected 256 / 227.6 / 62.4 / 172.6 / 256 / 0 / 0)");
            assert(EdgeProducer.ProfileTextFor(false, true, 123456789) == EdgeProducer.ScanProfileKeys + "x7.spoolMaxBytes: 123456789\n"
                   && EdgeProducer.ProfileTextFor(true, true, 0) == EdgeProducer.ProfileText() + EdgeProducer.ScanProfileKeys + "x7.spoolMaxBytes: 0\n"
                   && EdgeProducer.ProfileTextFor(false, true, null) == EdgeProducer.ScanProfileKeys,
                "profile text carries x7.spoolMaxBytes only when a cap is given (a .7-capable exe), after the scan keys");
            foreach (var (build, capable) in new[] { ("output-x64.5", false), ("output-x64.6", false), ("output-x64.7", true) })
            {
                var exe = FindUpward(AppContext.BaseDirectory, System.IO.Path.Combine("essentia-build", build, "essentia_streaming_extractor_music.exe"));
                if (exe == null) { Console.WriteLine($"  SKIP  spool capability of {build}: exe not found from the test binary"); continue; }
                assert(Program.SpoolCapable(exe) == capable && (build == "output-x64.5" ? !Program.ScanProfileCapable(exe) : Program.ScanProfileCapable(exe)),
                    $"{build}: spool-capable {Program.SpoolCapable(exe)} (expected {capable}), scan-profile-capable {Program.ScanProfileCapable(exe)}");
            }
        }

        /// <summary>Extractor .6 drop 2 (02d1115 / 296f87d): the SCAN profile makes the extractor compute only what truedat reads. The
        /// end-to-end gate the worker could not run: the committed .6 on a real clip, once with the full 2-arg call and once with the scan
        /// profile truedat writes, both fed through ParseEssentiaOutput — the TrackFeatures must be identical while the raw output is the
        /// smaller one. The capability is keyed on the exe's MD5 (a .5 or the drop-1 .6 refuses the merge), and the command line has
        /// exactly three shapes.</summary>
        static void TestScanProfile(Action<bool, string> assert)
        {
            string q(string s) => PathHelper.QuoteArg(s);
            assert(Program.ExtractorArguments("in.flac", "out.json", null, null) == $"{q("in.flac")} {q("out.json")}"
                   && Program.ExtractorArguments("in.flac", "out.json", "p.yaml", null) == $"{q("in.flac")} {q("out.json")} {q("p.yaml")}"
                   && Program.ExtractorArguments("in.flac", "out.json", "p.yaml", "f.bin") == $"{q("in.flac")} {q("out.json")} {q("p.yaml")} --frames-bin {q("f.bin")}",
                $"extractor command line: 2-arg, 3-arg (profile), 3-arg + --frames-bin — one builder, three shapes ({Program.ExtractorArguments("in.flac", "out.json", "p.yaml", "f.bin")})");
            assert(EdgeProducer.ProfileTextFor(false, true) == EdgeProducer.ScanProfileKeys
                   && EdgeProducer.ProfileTextFor(true, true) == EdgeProducer.ProfileText() + EdgeProducer.ScanProfileKeys
                   && EdgeProducer.ProfileTextFor(true, false) == EdgeProducer.ProfileText()
                   && EdgeProducer.ScanProfileKeys.Contains("x6.md5: 1\n"),
                "profile text: scan keys alone, frames + scan keys, frames alone; x6.md5 stays ON (the frames prefix needs md5_encoded)");
            // The CURRENT extractor (.7) with exactly what truedat writes for it: the scan keys + the RAM spool cap (here 256 MiB, so the
            // clip spools), parsed against the full 2-arg output of the same exe.
            var exe = FindUpward(AppContext.BaseDirectory, System.IO.Path.Combine("essentia-build", "output-x64.7", "essentia_streaming_extractor_music.exe"));
            var clip = FindUpward(AppContext.BaseDirectory, System.IO.Path.Combine("essentia-build", "spike-x6", "corpus", "flac16_44k_st_short.flac"));
            if (exe == null || clip == null) { Console.WriteLine("  SKIP  scan-profile end to end: output-x64.7 exe or spike-x6 corpus clip not found from the test binary"); return; }
            assert(Program.ScanProfileCapable(exe) && Program.SpoolCapable(exe), "the committed .7 honours the scan profile AND the spool cap — its MD5 is in both sets");
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "truedat-scanprofile-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                string fullJson = System.IO.Path.Combine(dir, "full.json"), scanJson = System.IO.Path.Combine(dir, "scan.json");
                var (profile, frames) = EdgeProducer.PrepareFor(scanJson, edges: false, scan: true, spoolMaxBytes: 256L << 20);
                assert(profile != null && frames == null && System.IO.File.ReadAllText(profile) == EdgeProducer.ScanProfileKeys + "x7.spoolMaxBytes: 268435456\n",
                    "PrepareFor(scan only, .7) writes the scan keys + the spool cap beside the output and asks for no frames file");
                int rcFull = RunExtractor(exe, Program.ExtractorArguments(clip, fullJson, null, null));
                int rcScan = RunExtractor(exe, Program.ExtractorArguments(clip, scanJson, profile, null));
                assert(rcFull == 0 && rcScan == 0 && System.IO.File.Exists(fullJson) && System.IO.File.Exists(scanJson),
                    $"the .7 extractor completes the clip under both calls (full rc {rcFull}, scan + spool rc {rcScan})");
                if (rcFull != 0 || rcScan != 0) return;
                string fullText = System.IO.File.ReadAllText(fullJson), scanText = System.IO.File.ReadAllText(scanJson);
                int fullLeaves = CountLeaves(fullText), scanLeaves = CountLeaves(scanText);
                assert(scanLeaves < fullLeaves / 2, $"the scan profile took effect: {scanLeaves} leaf values vs {fullLeaves} under the full call");
                var fullF = Program.ParseEssentiaOutput(fullText); var scanF = Program.ParseEssentiaOutput(scanText);
                string a = System.Text.Json.JsonSerializer.Serialize(fullF), b = System.Text.Json.JsonSerializer.Serialize(scanF);
                assert(fullF != null && scanF != null && a == b,
                    "ParseEssentiaOutput reads the SAME TrackFeatures from the scan-profile output as from the full one" + (a == b ? "" : " — first difference: " + FirstDifference(a, b)));
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        static int RunExtractor(string exe, string arguments)
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe, Arguments = arguments, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } return -1; }
            return p.ExitCode;
        }

        static int CountLeaves(string json)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            int n = 0;
            void Walk(System.Text.Json.JsonElement e)
            {
                switch (e.ValueKind)
                {
                    case System.Text.Json.JsonValueKind.Object: foreach (var pr in e.EnumerateObject()) Walk(pr.Value); break;
                    case System.Text.Json.JsonValueKind.Array: foreach (var it in e.EnumerateArray()) Walk(it); break;
                    default: n++; break;
                }
            }
            Walk(doc.RootElement);
            return n;
        }

        static string FirstDifference(string a, string b)
        {
            int i = 0; while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
            int from = Math.Max(0, i - 40);
            return $"@{i}: full ...{a.Substring(from, Math.Min(90, a.Length - from))}... | scan ...{b.Substring(from, Math.Min(90, b.Length - from))}...";
        }

        // ---------------------------------------------------------------- the store rows
        const string Sha1 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string ShaKind = "content_hash:audio-stream-sha256";

        internal static EdgeFoldResult SyntheticFold(string dir, double duration = 240, bool withPcm = true)
        {
            EdgeFoldResult r;
            using (var f = EspbFile.Open(BuildSyntheticFrames(dir, duration))) r = EdgeFold.Fold(f, duration, new EdgeSettings());
            if (withPcm && r.Status == "ok")
            {
                EdgeFold.ApplyPcm(r.Head, new EdgePcmResult { CrestDb = 4.25, TransitionMs = 300, SilenceMs = 0, Slices = 300 });
                EdgeFold.ApplyPcm(r.Tail, new EdgePcmResult { CrestDb = 3.61, TransitionMs = 0, SilenceMs = 1100, Slices = 300 });
            }
            r.FoldMs = 7; r.PcmMs = 210; r.Extractor = "output-x64.5";
            return r;
        }

        static void TestStore(Action<bool, string> assert)
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), ".truedat-selftest", "edgestore-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                var now = DateTime.UtcNow.ToString("o");
                using var os = Truedat.Store.OpenStore.Open(System.IO.Path.Combine(dir, "truedat.db"), true, VersionInfo.Display, now);
                var dal = os.Dal;
                // ApplyPcm: the PCM answer replaces the EBU silence run and adds rms; a null pass leaves 'ebu' standing.
                var r = SyntheticFold(dir, withPcm: false);
                assert(r.Tail.Loudness!.SilenceSource == "ebu" && r.Tail.Rms == null, "before the PCM pass: silence from EBU, rms null");
                EdgeFold.ApplyPcm(r.Tail, null);
                assert(r.Tail.Loudness.SilenceSource == "ebu" && r.Tail.Rms == null, "a null PCM pass changes nothing (never a substitute)");
                r = SyntheticFold(dir);
                assert(r.Tail.Loudness!.SilenceSource == "pcm" && r.Tail.Loudness.SilenceMs == 1100 && r.Tail.Rms != null && r.Tail.Rms.CrestDb == 3.61 && r.Tail.Rms.TransitionMs == 0,
                    $"ApplyPcm: silence 1,100 ms from 'pcm', rms crest 3.61 / transition 0 ({r.Tail.Loudness.SilenceSource}, {r.Tail.Loudness.SilenceMs}, {r.Tail.Rms?.CrestDb})");
                assert(r.Status == "ok", $"whole-track status ok ({r.Status})");

                // Write + read back.
                int values;
                // T05: the OPT-IN path — captureArrays: true writes the typed edge rows AND the full edge-row blob in `arrays`.
                using (var tx = os.Db.BeginTransaction()) { values = EdgeStore.Write(dal, Sha1, ShaKind, null, r, captureArrays: true); tx.Complete(); }
                long segs = dal.CountSegments(Sha1, r.ProducerVersion), vrows = dal.CountValues(Sha1, EdgeStore.ProducerKey, r.ProducerVersion);
                long edgeRows = long.Parse(os.Db.ExecScalar("SELECT count(*) FROM edge WHERE content_hash = '" + Sha1 + "';") ?? "0");
                long arrayBytes = long.Parse(os.Db.ExecScalar("SELECT coalesce(sum(length(arrays)), 0) FROM edge WHERE content_hash = '" + Sha1 + "';") ?? "0");
                assert(segs == 2, $"2 segment rows: head and tail; the sub-windows ride inside the edge row ({segs})");
                assert(values == 6 && vrows == 4 && edgeRows == 2, $"T05: two typed edge rows in `edge` plus four whole-track value rows (status, duration, extractor, head-tail divergence); no edge.row value rows ({edgeRows} edge rows, {vrows} value rows, {values} written)");
                assert(os.Db.ExecScalar("SELECT count(*) FROM value WHERE field = 'edge.row';") == "0", "T05: the packed edge.row value rows are gone — the edge table holds the edge");
                assert(arrayBytes >= 5000 && arrayBytes <= 7500, $"opt-in arrays are the plan's payload: per edge EBU pair 1.2 KB + beats 0.4 KB + four summaries 1.4 KB -> ~6 KB per track ({arrayBytes} bytes)");
                assert(os.Db.ExecScalar("SELECT typeof(bpm) || '/' || typeof(mean_lufs) || '/' || typeof(fade_ms) || '/' || length(hpcp) || '/' || arrays_dtype FROM edge WHERE content_hash = '" + Sha1 + "' AND kind = 'head';") == "real/real/integer/144/edge-row/1",
                    $"T05 typed default row: bpm/mean_lufs REAL (the fold's float32, exact), fade_ms INTEGER, hpcp 36 × f32 = 144 bytes, arrays_dtype names the blob format ({os.Db.ExecScalar("SELECT typeof(bpm) || '/' || typeof(mean_lufs) || '/' || typeof(fade_ms) || '/' || length(hpcp) || '/' || coalesce(arrays_dtype, 'null') FROM edge WHERE content_hash = '" + Sha1 + "' AND kind = 'head';")})");
                var back = EdgeStore.Read(dal, Sha1, r.ProducerVersion);
                assert(back != null && back.Status == "ok" && back.DurationMs == 240000 && back.ProducerVersion == r.ProducerVersion, $"read back: status, duration, version ({back?.Status}, {back?.DurationMs})");
                assert(back != null && back.Head.StartMs == 0 && back.Head.EndMs == 30000 && back.Tail.StartMs == 210000 && back.Tail.EndMs == 240000 && back.Head.Status == "ok", "read back: the edge windows");
                assert(back != null && back.Head.MomentaryCentiDb.Length == 300 && back.Head.MomentaryCentiDb[3] == -3827 && back.Head.MomentaryCentiDb[0] == short.MinValue && back.Tail.ShortTermCentiDb[299] == -30069,
                    $"read back: the EBU pair as int16 centi-dB, undefined bins intact ([3]={back?.Head.MomentaryCentiDb[3]}, [0]={back?.Head.MomentaryCentiDb[0]})");
                assert(back != null && back.Head.BeatsMs.Length == 60 && back.Head.BeatsMs[0] == 250 && back.Head.BeatsLoudness.Length == 60 && Math.Abs(back.Head.BeatsLoudness[0] - 0.3) < 1e-6, "read back: beats ms and loudness");
                assert(back != null && back.Head.Bpm != null && Math.Abs(back.Head.Bpm.Value - 120) < 0.01 && back.Head.Bpm.Beats == 60, "read back: bpm");
                assert(back != null && back.Head.Whole?.Key != null && back.Head.Whole.Key.Name == "C" && back.Head.Whole.Key.Scale == "major" && back.Head.Whole.Key.Code == "8B" && back.Head.Whole.Key.Agreement == r.Head.Whole!.Key!.Agreement,
                    $"read back: the key vote ({back?.Head.Whole?.Key?.Name} {back?.Head.Whole?.Key?.Scale} {back?.Head.Whole?.Key?.Code})");
                assert(back != null && back.Head.Sub.Length == 3 && back.Head.Sub[2].StartMs == 20000 && back.Head.Sub[1].Key?.Name == "C" && Math.Abs(back.Head.Sub[1].MfccMean[12] - (-380)) < 1e-3,
                    "read back: three sub-windows with their own summaries");
                assert(back != null && back.Head.Whole != null && Math.Abs(back.Head.Whole.HpcpMean[9] - r.Head.Whole!.HpcpMean[9]) < 1e-6 && Math.Abs(back.Head.Whole.SpectralMean[10] - 1100) < 1e-3 && back.Head.Whole.MfccSpread[0] == 0,
                    "read back: hpcp mean, spectral means, spreads");
                // Scalars are stored as float32: equal to ~1e-6 relative, so compare with a tolerance, never ==.
                assert(back != null && back.Tail.Loudness != null && back.Tail.Loudness.SilenceMs == 1100 && back.Tail.Loudness.SilenceSource == "pcm" && back.Tail.Loudness.FadeMs == r.Tail.Loudness!.FadeMs
                    && Math.Abs(back.Tail.Loudness.MeanLufs!.Value - r.Tail.Loudness.MeanLufs!.Value) < 1e-4 && back.Tail.Rms != null && Math.Abs(back.Tail.Rms.CrestDb!.Value - 3.61) < 1e-5,
                    $"read back: loudness scalars, silence source, rms ({back?.Tail.Loudness?.SilenceMs} {back?.Tail.Loudness?.SilenceSource} fade {back?.Tail.Loudness?.FadeMs} vs {r.Tail.Loudness!.FadeMs}; mean {back?.Tail.Loudness?.MeanLufs} vs {r.Tail.Loudness.MeanLufs}; crest {back?.Tail.Rms?.CrestDb})");
                assert(back != null && back.HeadTail != null && Math.Abs(back.HeadTail.Harmony!.Value - r.HeadTail!.Harmony!.Value) < 1e-5 && back.Head.Divergence != null && Math.Abs(back.Head.Divergence.Texture!.Value - r.Head.Divergence!.Texture!.Value) < 1e-5,
                    "read back: divergences");
                // T02 review finding 5: an all-silent tail's NULL scalars survive pack (NaN f32) and unpack (null); the Loudness
                // object itself survives on silenceMs; the loudness divergences come back null, never 1.00.
                EdgeFoldResult rs;
                using (var f = EspbFile.Open(BuildSyntheticFrames(dir, 240, silentTailSec: 31))) rs = EdgeFold.Fold(f, 240, new EdgeSettings());
                const string ShaSilent = "2222222222222222222222222222222222222222222222222222222222222222";
                using (var tx = os.Db.BeginTransaction()) { EdgeStore.Write(dal, ShaSilent, ShaKind, null, rs); tx.Complete(); }
                var backS = EdgeStore.Read(dal, ShaSilent, rs.ProducerVersion);
                assert(backS != null && backS.Tail.Loudness != null && backS.Tail.Loudness.MeanLufs == null && backS.Tail.Loudness.StartLufs == null && backS.Tail.Loudness.EndLufs == null
                    && backS.Tail.Loudness.SilenceMs == rs.Tail.Loudness!.SilenceMs && backS.HeadTail != null && backS.HeadTail.Loudness == null && backS.Tail.Divergence != null && backS.Tail.Divergence.Loudness == null,
                    $"read back: an all-silent tail keeps NULL loudness scalars, its silence run, and null loudness divergences (mean {backS?.Tail.Loudness?.MeanLufs}, silence {backS?.Tail.Loudness?.SilenceMs} vs {rs.Tail.Loudness?.SilenceMs}, head-tail {backS?.HeadTail?.Loudness}, edge {backS?.Tail.Divergence?.Loudness})");

                // ---- T05 (A03): the DEFAULT path — captureArrays: false writes the typed edge rows (scalars, divergences, 36 × f32 HPCP) and
                // NO arrays blob; Read gives back every scalar and the whole-edge HPCP, with empty series/beats/sub-windows.
                const string ShaDefault = "3333333333333333333333333333333333333333333333333333333333333333";
                using (var tx = os.Db.BeginTransaction()) { EdgeStore.Write(dal, ShaDefault, ShaKind, null, r, captureArrays: false); tx.Complete(); }
                assert(os.Db.ExecScalar("SELECT count(*) || '/' || count(arrays) || '/' || count(arrays_dtype) FROM edge WHERE content_hash = '" + ShaDefault + "';") == "2/0/0",
                    $"default capture: two edge rows, no arrays blob, no arrays dtype ({os.Db.ExecScalar("SELECT count(*) || '/' || count(arrays) || '/' || count(arrays_dtype) FROM edge WHERE content_hash = '" + ShaDefault + "';")})");
                var backD = EdgeStore.Read(dal, ShaDefault, r.ProducerVersion);
                assert(backD != null && backD.Status == "ok" && backD.Tail.Loudness != null && Math.Abs(backD.Tail.Loudness.MeanLufs!.Value - r.Tail.Loudness!.MeanLufs!.Value) < 1e-4
                    && backD.Tail.Bpm != null && Math.Abs(backD.Tail.Bpm.Value - r.Tail.Bpm!.Value) < 1e-4 && backD.Tail.Bpm.Beats == r.Tail.Bpm.Beats
                    && backD.Tail.Whole?.Key != null && backD.Tail.Whole.Key.Code == r.Tail.Whole!.Key!.Code && backD.Tail.Whole.Key.Agreement == r.Tail.Whole.Key.Agreement && backD.Tail.Whole.Key.VotesPresent == r.Tail.Whole.Key.VotesPresent
                    && Math.Abs(backD.Tail.Whole.SpectralMean[0] - r.Tail.Whole.SpectralMean[0]) < 1e-3 && Math.Abs(backD.Tail.Whole.SpectralMean[5] - r.Tail.Whole.SpectralMean[5]) < 1e-6
                    && backD.Tail.Rms != null && Math.Abs(backD.Tail.Rms.CrestDb!.Value - 3.61) < 1e-5 && backD.Tail.Loudness.SilenceMs == 1100 && backD.Tail.Loudness.FadeMs == r.Tail.Loudness.FadeMs
                    && backD.Tail.Divergence != null && Math.Abs(backD.Tail.Divergence.Texture!.Value - r.Tail.Divergence!.Texture!.Value) < 1e-5 && backD.HeadTail != null && Math.Abs(backD.HeadTail.Harmony!.Value - r.HeadTail!.Harmony!.Value) < 1e-5,
                    "default row read back: status, bpm (value + beats), key (code/agreement/votes), centroid (SpectralMean[0]) and dissonance (SpectralMean[5]), loudness block, rms pair, divergences, head-tail divergence");
                bool hpcpOk = backD?.Tail.Whole != null; if (hpcpOk) for (int i = 0; i < 36; i++) if (Math.Abs(backD!.Tail.Whole!.HpcpMean[i] - r.Tail.Whole!.HpcpMean[i]) > 1e-6) { hpcpOk = false; break; }
                assert(hpcpOk && backD!.Tail.MomentaryCentiDb.Length == 0 && backD.Tail.BeatsMs.Length == 0 && backD.Tail.Sub.Length == 0 && backD.Tail.Whole!.MfccMean.All(double.IsNaN),
                    $"default row read back: the 36-bin HPCP is there (f32 exact), the series / beats / sub-windows / MFCC are not (hpcp {hpcpOk}, series {backD?.Tail.MomentaryCentiDb.Length}, beats {backD?.Tail.BeatsMs.Length}, subs {backD?.Tail.Sub.Length})");

                // Sticky per track (operator: "keep what we captured"): once a track holds arrays, a flag-off re-write keeps and refreshes them.
                long run77, run78;
                using (var tx = os.Db.BeginTransaction())
                {
                    run77 = dal.InsertRun(EdgeStore.ProducerKey, r.ProducerVersion, null, ShaDefault, ShaKind, null, now, 1, "selftest", "selftest", "ok", null, null, null, "selftest");
                    run78 = dal.InsertRun(EdgeStore.ProducerKey, r.ProducerVersion, null, ShaDefault, ShaKind, null, now, 1, "selftest", "selftest", "ok", null, null, null, "selftest");
                    tx.Complete();
                }
                using (var tx = os.Db.BeginTransaction()) { EdgeStore.Write(dal, ShaDefault, ShaKind, run77, r, captureArrays: true); tx.Complete(); }
                using (var tx = os.Db.BeginTransaction()) { EdgeStore.Write(dal, ShaDefault, ShaKind, run78, r, captureArrays: false); tx.Complete(); }
                assert(os.Db.ExecScalar("SELECT count(arrays) || '/' || min(run_id) || '/' || max(run_id) FROM edge WHERE content_hash = '" + ShaDefault + "';") == "2/" + run78 + "/" + run78,
                    $"sticky: a flag-off re-write of a track that holds arrays keeps the arrays and carries the new run ({os.Db.ExecScalar("SELECT count(arrays) || '/' || min(run_id) || '/' || max(run_id) FROM edge WHERE content_hash = '" + ShaDefault + "';")})");
                assert(EdgeStore.ArraysCaptured(dal, ShaDefault, r.ProducerVersion) && !EdgeStore.ArraysCaptured(dal, ShaSilent, rs.ProducerVersion), "ArraysCaptured answers per track");

                // Keeper 019 on 6721aa9: sticky is PER TRACK, not per producer version ("sticky per track. keep what we captured"). A track whose
                // V1 row holds arrays, re-analysed under a NEW producer version with the flag off, must get its arrays at V2 — refreshed to V2's values,
                // not V1's, and without the old row being discarded as a substitute.
                const string ShaVer = "4444444444444444444444444444444444444444444444444444444444444444";
                using (var tx = os.Db.BeginTransaction()) { EdgeStore.Write(dal, ShaVer, ShaKind, null, r, captureArrays: true); tx.Complete(); }
                var rV2 = SyntheticFold(dir); rV2.ProducerVersion = "edges@v4;e30;p10"; rV2.Head.MomentaryCentiDb[3] = -1234;   // a hypothetical NEXT fold rule (one past the current), new values
                using (var tx = os.Db.BeginTransaction()) { EdgeStore.Write(dal, ShaVer, ShaKind, null, rV2, captureArrays: false); tx.Complete(); }
                var backV2 = EdgeStore.Read(dal, ShaVer, rV2.ProducerVersion);
                var backV1 = EdgeStore.Read(dal, ShaVer, r.ProducerVersion);
                assert(EdgeStore.ArraysCaptured(dal, ShaVer, rV2.ProducerVersion) && backV2 != null && backV2.Head.MomentaryCentiDb.Length == 300 && backV2.Head.MomentaryCentiDb[3] == -1234,
                    $"sticky across a producer-version change: the V2 default write keeps capturing, with V2's values (V2 arrays {EdgeStore.ArraysCaptured(dal, ShaVer, rV2.ProducerVersion)}, series {backV2?.Head.MomentaryCentiDb.Length}, [3] {(backV2 != null && backV2.Head.MomentaryCentiDb.Length > 3 ? backV2.Head.MomentaryCentiDb[3] : 0)} (-1234))");
                // Operator 2026-10-22 ("prune, we don't want to keep dead wood"): once the track holds the version just written, the rows at
                // every OTHER version are dead — no reader selects them — and the write removes them: edge, segment and value rows alike.
                // The run rows stay (history). The sticky decision above was read BEFORE the prune, so the capture still carried.
                assert(backV1 == null && dal.CountSegments(ShaVer, r.ProducerVersion) == 0 && dal.CountValues(ShaVer, EdgeStore.ProducerKey, r.ProducerVersion) == 0
                       && os.Db.ExecScalar("SELECT count(*) FROM edge WHERE content_hash = '" + ShaVer + "' AND producer_version <> '" + rV2.ProducerVersion + "';") == "0",
                    $"the superseded V1 rows are pruned once V2 holds the track (V1 read {(backV1 == null ? "null" : "a row")}, segments {dal.CountSegments(ShaVer, r.ProducerVersion)}, values {dal.CountValues(ShaVer, EdgeStore.ProducerKey, r.ProducerVersion)})");

                // The explicit discard verb: dry-run names what it would remove and changes nothing; the real run removes exactly those.
                long withArrays = long.Parse(os.Db.ExecScalar("SELECT count(arrays) FROM edge;") ?? "0");
                var dry = EdgeStore.DropArrays(dal, dryRun: true);
                assert(dry.Rows == withArrays && dry.Tracks == 3 && long.Parse(os.Db.ExecScalar("SELECT count(arrays) FROM edge;") ?? "0") == withArrays,
                    $"--edge-arrays-drop --dry-run: names {dry.Rows} rows on {dry.Tracks} tracks and removes nothing ({os.Db.ExecScalar("SELECT count(arrays) FROM edge;")} still held)");
                EdgeStore.DropArraysResult real;
                using (var tx = os.Db.BeginTransaction()) { real = EdgeStore.DropArrays(dal, dryRun: false); tx.Complete(); }
                assert(real.Rows == withArrays && os.Db.ExecScalar("SELECT count(arrays) || '/' || count(arrays_dtype) || '/' || count(*) FROM edge;") == "0/0/" + os.Db.ExecScalar("SELECT count(*) FROM edge;"),
                    $"--edge-arrays-drop: removes exactly the arrays (and their dtype), keeps every edge row and its scalars ({os.Db.ExecScalar("SELECT count(arrays) || '/' || count(arrays_dtype) || '/' || count(*) FROM edge;")})");
                var backAfter = EdgeStore.Read(dal, Sha1, r.ProducerVersion);
                assert(backAfter != null && backAfter.Tail.Loudness != null && backAfter.Tail.MomentaryCentiDb.Length == 0 && Math.Abs(backAfter.Tail.Bpm!.Value - r.Tail.Bpm!.Value) < 1e-4,
                    "after the drop the opt-in track reads back as a default row: scalars intact, series gone");

                // Idempotent: the same version rewrites itself; a different version coexists. (Re-captured explicitly: the drop above cleared
                // Sha1's arrays, and the default write would — correctly — leave it a default row.)
                using (var tx = os.Db.BeginTransaction()) { EdgeStore.Write(dal, Sha1, ShaKind, null, r, captureArrays: true); tx.Complete(); }
                assert(dal.CountSegments(Sha1, r.ProducerVersion) == 2 && dal.CountValues(Sha1, EdgeStore.ProducerKey, r.ProducerVersion) == vrows, "a second write of the same version adds no rows");
                var r2 = SyntheticFold(dir); r2.ProducerVersion = "edges@v1;e20;p10";
                long runsBefore = long.Parse(os.Db.ExecScalar("SELECT count(*) FROM run;") ?? "0");
                int pruned; using (var tx = os.Db.BeginTransaction()) { EdgeStore.Write(dal, Sha1, ShaKind, null, r2, null, out _, out pruned); tx.Complete(); }
                assert(dal.CountSegments(Sha1, r2.ProducerVersion) == 2 && dal.CountSegments(Sha1, r.ProducerVersion) == 0 && EdgeStore.Read(dal, Sha1, r.ProducerVersion) == null
                       && dal.CountValues(Sha1, EdgeStore.ProducerKey, r.ProducerVersion) == 0 && pruned == 2 + 2 + vrows
                       && long.Parse(os.Db.ExecScalar("SELECT count(*) FROM run;") ?? "0") == runsBefore,
                    $"a different producer version REPLACES the track's rows: its own 2 segments, the earlier version's edge/segment/value rows pruned and counted ({pruned} = 2 edges + 2 segments + {vrows} values), run rows untouched");
                assert(EdgeStore.Read(dal, Sha1, "edges@v9;e30;p10") == null, "an unknown version reads as null");

                // The store-wide sweep for stores that already carry superseded rows (every instance that ran the edges@v3 --refresh kept its
                // edges@v2 set — 27% of MB3's file): a track that holds the CURRENT version loses its other versions; a track that holds only
                // an old version keeps it (nothing current has replaced it yet; --refresh will). Legacy rows are planted by SQL at a version
                // no fold writes any more.
                const string ShaSweep = "5555555555555555555555555555555555555555555555555555555555555555";
                const string ShaLegacyOnly = "6666666666666666666666666666666666666666666666666666666666666666";
                const string Legacy = "edges@v2;e30;p10";
                using (var tx = os.Db.BeginTransaction()) { EdgeStore.Write(dal, ShaSweep, ShaKind, null, r, captureArrays: false); tx.Complete(); }
                using (var tx = os.Db.BeginTransaction())
                {
                    foreach (var sha in new[] { ShaSweep, ShaLegacyOnly })
                    {
                        dal.ExecBound("INSERT INTO value(content_hash, content_hash_kind, segment_id, field, producer_key, producer_version, txt) VALUES (?, ?, 0, 'edges.status', 'edges', ?, 'ok');", sha, ShaKind, Legacy);
                        dal.ExecBound("INSERT INTO segment(content_hash, content_hash_kind, set_key, kind, ordinal, start_ms, end_ms) VALUES (?, ?, ?, 'head', 0, 0, 30000);", sha, ShaKind, Legacy);
                        dal.ExecBound("INSERT INTO edge(content_hash, content_hash_kind, producer_version, kind, status, start_ms, end_ms, updated_utc) VALUES (?, ?, ?, 'head', 'ok', 0, 30000, '2026-10-21T00:00:00Z');", sha, ShaKind, Legacy);
                    }
                    tx.Complete();
                }
                long sweptRows; using (var tx = os.Db.BeginTransaction()) { sweptRows = EdgeStore.PruneSupersededVersions(dal, r.ProducerVersion); tx.Complete(); }
                string legacyLeft(string sha) => os.Db.ExecScalar("SELECT (SELECT count(*) FROM edge WHERE content_hash = '" + sha + "' AND producer_version = '" + Legacy + "') || '/' || " +
                    "(SELECT count(*) FROM segment WHERE content_hash = '" + sha + "' AND set_key = '" + Legacy + "') || '/' || " +
                    "(SELECT count(*) FROM value WHERE content_hash = '" + sha + "' AND producer_version = '" + Legacy + "');") ?? "?";
                assert(sweptRows == 3 && legacyLeft(ShaSweep) == "0/0/0" && legacyLeft(ShaLegacyOnly) == "1/1/1"
                       && dal.CountSegments(ShaSweep, r.ProducerVersion) == 2 && EdgeStore.Read(dal, ShaSweep, r.ProducerVersion)?.Status == "ok",
                    $"the sweep prunes superseded versions only where the current one holds the track ({sweptRows} rows; sweep-track legacy left {legacyLeft(ShaSweep)}, legacy-only track {legacyLeft(ShaLegacyOnly)}; current rows intact)");
                long sweptAgain; using (var tx = os.Db.BeginTransaction()) { sweptAgain = EdgeStore.PruneSupersededVersions(dal, r.ProducerVersion); tx.Complete(); }
                assert(sweptAgain == 0, $"a second sweep finds nothing ({sweptAgain})");

                // Unavailable: no edge rows, the whole-track word only. Failed: nothing at all.
                const string Sha2 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
                var shortR = SyntheticFold(dir, 15);
                using (var tx = os.Db.BeginTransaction()) { EdgeStore.Write(dal, Sha2, ShaKind, null, shortR); tx.Complete(); }
                var backShort = EdgeStore.Read(dal, Sha2, shortR.ProducerVersion);
                assert(dal.CountSegments(Sha2, shortR.ProducerVersion) == 0 && backShort != null && backShort.Status == "unavailable" && backShort.DurationMs == 15000 && backShort.Head.Status == "unavailable",
                    $"unavailable: no segment rows, the whole-track word reads back ({backShort?.Status})");
                const string Sha3 = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
                EdgeFoldResult failed;
                using (var f = EspbFile.Open(BuildSyntheticFrames(dir, 240, withHpcp: false))) failed = EdgeFold.Fold(f, 240, new EdgeSettings());
                int wrote; using (var tx = os.Db.BeginTransaction()) { wrote = EdgeStore.Write(dal, Sha3, ShaKind, null, failed); tx.Complete(); }
                assert(failed.Status == "failed" && wrote == 0 && dal.CountSegments(Sha3, failed.ProducerVersion) == 0 && dal.CountValues(Sha3, EdgeStore.ProducerKey, failed.ProducerVersion) == 0 && EdgeStore.Read(dal, Sha3, failed.ProducerVersion) == null,
                    "failed: no partial row lands — nothing written, nothing to read");
                assert(dal.ProducerVersion(EdgeStore.ProducerKey) == shortR.ProducerVersion, $"the producer row names the version last written ({dal.ProducerVersion(EdgeStore.ProducerKey)})");
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }

            // The real budget is file bytes per track, not vector bytes: every value row repeats the content hash, the field
            // name and the producer version in the row AND in its primary-key index. Measured over 20 distinct tracks in a
            // fresh store (vacuumed), printed, and held to the plan's ceiling (the ruled row: ~6.6 KB/track at 72k = 0.48 GB).
            var dir2 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), ".truedat-selftest", "edgesize-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            System.IO.Directory.CreateDirectory(dir2);
            try
            {
                var now = DateTime.UtcNow.ToString("o");
                var dbPath = System.IO.Path.Combine(dir2, "truedat.db");
                long before, mid, after, payloadOptIn;
                using (var os = Truedat.Store.OpenStore.Open(dbPath, true, VersionInfo.Display, now))
                {
                    os.Db.Exec("VACUUM;"); before = LogicalBytes(os.Db);
                    var r = SyntheticFold(dir2);
                    using (var tx = os.Db.BeginTransaction())
                    {
                        for (int i = 0; i < 20; i++) EdgeStore.Write(os.Dal, i.ToString("x2") + Sha1.Substring(2), ShaKind, null, r, captureArrays: false);
                        tx.Complete();
                    }
                    os.Dal.ResetAll(); os.Db.Exec("VACUUM;"); mid = LogicalBytes(os.Db);
                    using (var tx = os.Db.BeginTransaction())
                    {
                        for (int i = 20; i < 40; i++) EdgeStore.Write(os.Dal, i.ToString("x2") + Sha1.Substring(2), ShaKind, null, r, captureArrays: true);
                        tx.Complete();
                    }
                    payloadOptIn = os.Dal.ScalarLong("SELECT coalesce(sum(length(arrays)), 0) FROM edge WHERE content_hash = ?;", "20" + Sha1.Substring(2));
                    os.Dal.ResetAll(); os.Db.Exec("VACUUM;"); after = LogicalBytes(os.Db);
                }
                long perDefault = (mid - before) / 20, perOptIn = (after - mid) / 20;
                Console.WriteLine($"        store bytes per track with edges: default row {perDefault:N0}, opt-in arrays {perOptIn:N0} (20 tracks each, vacuumed, page_count x page_size; opt-in arrays payload {payloadOptIn:N0}/track)");
                assert(perDefault > 0 && perOptIn <= 12000 && perDefault < perOptIn, $"T05: the default row costs less than the opt-in capture, and the opt-in row stays under 12 KB/track ({perDefault:N0} vs {perOptIn:N0} bytes/track)");
            }
            finally { try { System.IO.Directory.Delete(dir2, true); } catch { } }
        }

        /// <summary>T05 / schema v4: a v3 store (built at v3 with the test seam, holding ONE track's packed edge.row value rows exactly as v3
        /// wrote them) opens as v4: each packed row is now a typed edge row WITH its blob as the arrays (sticky: v3 captured them), the packed
        /// value rows are gone, the head-tail vector row carries its dtype, the backup is the untouched v3 file, and the edge reads back whole.</summary>
        static void TestV4Migration(Action<bool, string> assert)
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), ".truedat-selftest", "edgev4-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                var store = System.IO.Path.Combine(dir, "truedat.db");
                var r = SyntheticFold(dir);
                var headBlob = EdgeStore.PackEdge(r.Head); var tailBlob = EdgeStore.PackEdge(r.Tail);
                using (var db = Truedat.Store.SqliteDb.Open(store, create: true))
                {
                    Truedat.Store.SchemaMigrator.Apply(db, "selftest-v3", targetVersion: 3);
                    // v3's own rows for one track (S2 C4 shape): producer, 2 segments, 4 whole-track values, 2 packed edge.row values.
                    db.Exec("INSERT INTO producer(key, version, kind, needs_audio) VALUES ('edges', '" + r.ProducerVersion + "', 'analysis', 1);");
                    db.Exec("INSERT INTO segment(id, content_hash, content_hash_kind, set_key, kind, ordinal, start_ms, end_ms) VALUES (1, '" + Sha1 + "', '" + ShaKind + "', '" + r.ProducerVersion + "', 'head', 0, 0, 30000);");
                    db.Exec("INSERT INTO segment(id, content_hash, content_hash_kind, set_key, kind, ordinal, start_ms, end_ms) VALUES (2, '" + Sha1 + "', '" + ShaKind + "', '" + r.ProducerVersion + "', 'tail', 0, 210000, 240000);");
                    void V(long seg, string field, double? num, string? txt, byte[]? vec, long? dims, string? unit)
                    {
                        using var st = db.Prepare("INSERT INTO value(content_hash, content_hash_kind, segment_id, field, producer_key, producer_version, run_id, num, txt, vec, dims, unit) VALUES (?, ?, ?, ?, 'edges', ?, NULL, ?, ?, ?, ?, ?);");
                        st.Bind(1, Sha1); st.Bind(2, ShaKind); st.Bind(3, seg); st.Bind(4, field); st.Bind(5, r.ProducerVersion);
                        if (num.HasValue) st.Bind(6, num.Value); else st.BindNull(6);
                        if (txt != null) st.Bind(7, txt); else st.BindNull(7);
                        if (vec != null) st.Bind(8, vec); else st.BindNull(8);
                        if (dims.HasValue) st.Bind(9, dims.Value); else st.BindNull(9);
                        if (unit != null) st.Bind(10, unit); else st.BindNull(10);
                        st.Step();
                    }
                    V(0, "edges.status", null, "ok", null, null, null);
                    V(0, "edges.durationMs", 240000, null, null, null, null);
                    V(0, "edges.extractor", null, "output-x64.5", null, null, null);
                    var ht = new float[] { (float)(r.HeadTail!.Loudness ?? double.NaN), (float)(r.HeadTail.Texture ?? double.NaN), (float)(r.HeadTail.Harmony ?? double.NaN) };
                    var htb = new byte[12]; Buffer.BlockCopy(ht, 0, htb, 0, 12);
                    V(0, "edges.headTail", null, null, htb, 3, "loudness,texture,harmony");
                    V(1, "edge.row", null, null, headBlob, headBlob.Length, "edge-row/v1");
                    V(2, "edge.row", null, null, tailBlob, tailBlob.Length, "edge-row/v1");
                    assert(Truedat.Store.SchemaMigrator.ReadVersion(db) == 3 && db.ExecScalar("SELECT count(*) FROM value WHERE field = 'edge.row';") == "2", "v3 fixture: schema 3, two packed edge.row values");
                }
                using (var os = Truedat.Store.OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
                {
                    var db = os.Db;
                    assert(Truedat.Store.SchemaMigrator.ReadVersion(db) == 4 && Truedat.Store.SchemaV4Migration.LastMovedEdgeRows == 2, $"v3 -> v4 on open: {Truedat.Store.SchemaV4Migration.LastMovedEdgeRows} packed rows moved");
                    assert(db.ExecScalar("SELECT count(*) FROM value WHERE field = 'edge.row';") == "0" && db.ExecScalar("SELECT count(*) || '/' || count(arrays) || '/' || min(arrays_dtype) FROM edge WHERE content_hash = '" + Sha1 + "';") == "2/2/" + EdgeStore.ArraysDtype,
                        $"the packed rows are gone; two typed edge rows, BOTH with their arrays kept ({db.ExecScalar("SELECT count(*) || '/' || count(arrays) || '/' || coalesce(min(arrays_dtype), '-') FROM edge;")})");
                    var keptHead = db.Prepare("SELECT arrays FROM edge WHERE kind = 'head';"); keptHead.Step(); var kept = keptHead.GetBlob(0); keptHead.Dispose();
                    bool same = kept != null && kept.Length == headBlob.Length; if (same) for (int i = 0; i < kept!.Length; i++) if (kept[i] != headBlob[i]) { same = false; break; }
                    assert(same, "the head's arrays are the v3 blob byte for byte");
                    assert(db.ExecScalar("SELECT dtype FROM value WHERE field = 'edges.headTail';") == "f32", "the head-tail vector row gains dtype f32");
                    var back = EdgeStore.Read(os.Dal, Sha1, r.ProducerVersion);
                    assert(back != null && back.Head.MomentaryCentiDb.Length == 300 && back.Head.Sub.Length == 3 && back.Head.Bpm != null && Math.Abs(back.Head.Bpm.Value - 120) < 0.01 && back.Tail.StartMs == 210000 && back.HeadTail != null,
                        "the migrated edge reads back whole: series, sub-windows, bpm, windows, head-tail divergence");
                    assert(Math.Abs(double.Parse(db.ExecScalar("SELECT bpm FROM edge WHERE kind = 'head';")!, System.Globalization.CultureInfo.InvariantCulture) - (double)(float)r.Head.Bpm!.Value) < 1e-9,
                        "the typed bpm column equals the blob's float32 value");
                    assert(os.Dal.MetaGet(Truedat.Store.VerdictStore.MethodKey) == Truedat.Store.VerdictStore.BuildMethod && os.Dal.MetaGet(Truedat.Store.VerdictStore.SpeechMethodKey) == Truedat.Store.VerdictStore.BuildSpeechMethod,
                        "the migrated store's verdict tags are recorded (the table derived at open)");
                }
                assert(System.IO.Directory.GetFiles(dir, "truedat.db.bak.v3.*").Length == 1, "the v3 file was backed up before the migration");
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>TDA F8 on 6721aa9 (a required test, not an operator question): "sticky per track" is per MEDIA. Capture arrays for a track at its
        /// content hash S1; the same media re-keys to S2 (a new hash at the same location — the store's same-entity key move); the next fold at S2
        /// is written with the flag OFF. The S2 edge must hold the arrays, with the new fold's values, and the companion must carry texture for it.</summary>
        static void TestKeyMoveSticky(Action<bool, string> assert)
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), ".truedat-selftest", "keymove-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            System.IO.Directory.CreateDirectory(dir);
            var prevDir = Truedat.Store.ScanSink.StoreDirectory;
            try
            {
                var store = System.IO.Path.Combine(dir, "truedat.db");
                Truedat.Store.ScanSink.Reset(); Truedat.Store.ScanSink.StoreDirectory = dir;
                const string path = @"D:\music\keymove\a.mp3";
                const string S1 = "1111111111111111111111111111111111111111111111111111111111111111", S2 = "2121212121212121212121212121212121212121212121212121212121212121";
                TrackEntry E(string sha) => new TrackEntry { Features = new TrackFeatures { Artist = "A", Title = "T", Bpm = 120, Key = "C", Mode = "major", Mfcc = new double[13] }, LastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), AudioStreamSha256 = sha };
                var settings = new EdgeSettings();
                // 1. captured at S1 (the flag ON for this scan)
                EdgeStore.CaptureArraysDefault = true;
                var f1 = SyntheticFold(dir);
                Truedat.Store.ScanSink.StashEdges(path, f1); Truedat.Store.ScanSink.Record(path, E(S1), path);
                var a = Truedat.Store.ScanSink.Flush(store, "output-x64.5", new[] { path }, final: true);
                // 2. the media re-keys to S2; the next scan runs with the flag OFF and folds new values
                EdgeStore.CaptureArraysDefault = false;
                var f2 = SyntheticFold(dir); f2.Head.MomentaryCentiDb[3] = -2468;
                Truedat.Store.ScanSink.StashEdges(path, f2); Truedat.Store.ScanSink.Record(path, E(S2), path);
                var b = Truedat.Store.ScanSink.Flush(store, "output-x64.5", new[] { path }, final: true);
                using (var os = Truedat.Store.OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
                {
                    long entities = os.Dal.Count("file_entity");
                    string? key = os.Db.ExecScalar("SELECT c.content_key FROM track_content c JOIN track_location l ON l.entity_id = c.entity_id WHERE l.display_path = '" + path.Replace("'", "''") + "';");
                    assert(a.Ok && b.Ok && entities == 1 && key == S2 && b.ContentRekeyed == 1, $"fixture: one media, re-keyed S1 -> S2 by the second scan ({entities} entities, key {key?.Substring(0, 8)}, rekeyed {b.ContentRekeyed})");
                    var back = EdgeStore.Read(os.Dal, S2, settings.ProducerVersion);
                    assert(EdgeStore.ArraysCaptured(os.Dal, S2, settings.ProducerVersion) && back != null && back.Head.MomentaryCentiDb.Length == 300 && back.Head.MomentaryCentiDb[3] == -2468,
                        $"sticky across a key move: the re-keyed media's flag-off fold at S2 keeps its arrays, with the new values (arrays {EdgeStore.ArraysCaptured(os.Dal, S2, settings.ProducerVersion)}, series {back?.Head.MomentaryCentiDb.Length}, [3] {(back != null && back.Head.MomentaryCentiDb.Length > 3 ? back.Head.MomentaryCentiDb[3] : 0)})");
                    var comp = System.IO.Path.Combine(dir, EdgeCompanion.FileName);
                    EdgeCompanion.WriteFromStore(os.Dal, comp, settings.ProducerVersion, "x");
                    using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllBytes(comp));
                    var tr = doc.RootElement.GetProperty("tracks");
                    assert(tr.TryGetProperty(S2, out var t2) && t2.GetProperty("head").GetProperty("texture").ValueKind == System.Text.Json.JsonValueKind.Object,
                        $"the companion carries the re-keyed track's texture at S2 ({(tr.TryGetProperty(S2, out var t2b) ? t2b.GetProperty("head").GetProperty("texture").ValueKind.ToString() : "S2 absent")})");
                }
            }
            finally { EdgeStore.CaptureArraysDefault = false; Truedat.Store.ScanSink.StoreDirectory = prevDir; Truedat.Store.ScanSink.Reset(); try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        static long LogicalBytes(Truedat.Store.SqliteDb db)
        {
            long pages, size;
            using (var st = db.Prepare("PRAGMA page_count;")) { st.Step(); pages = st.GetInt64(0); }
            using (var st = db.Prepare("PRAGMA page_size;")) { st.Step(); size = st.GetInt64(0); }
            return pages * size;
        }

        // ---------------------------------------------------------------- the companion file
        static void TestCompanion(Action<bool, string> assert)
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), ".truedat-selftest", "companion-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                var now = DateTime.UtcNow.ToString("o");
                const string ShaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", ShaC = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
                var version = new EdgeSettings().ProducerVersion;
                string path = System.IO.Path.Combine(dir, EdgeCompanion.FileName);
                EdgeCompanionReport rep;
                using (var os = Truedat.Store.OpenStore.Open(System.IO.Path.Combine(dir, "truedat.db"), true, VersionInfo.Display, now))
                {
                    var full = SyntheticFold(dir);                               // ok, with PCM
                    var noPcm = SyntheticFold(dir, withPcm: false);              // ok, EBU silence, rms null
                    var shortR = SyntheticFold(dir, 15);                         // unavailable
                    using (var tx = os.Db.BeginTransaction())
                    {
                        EdgeStore.Write(os.Dal, Sha1, ShaKind, null, full, captureArrays: true);   // the opt-in capture: the full texture block
                        EdgeStore.Write(os.Dal, ShaB, ShaKind, null, noPcm, captureArrays: false);   // the default row: no texture block
                        EdgeStore.Write(os.Dal, ShaC, ShaKind, null, shortR);
                        EdgeStore.Write(os.Dal, "entity:01a12e52-bf89-705c-9cf8-4c0133113724", "entity", null, full);   // no sha: no key in the contract
                        tx.Complete();
                    }
                    System.IO.File.WriteAllText(path, "{\"stale\": true}");       // an older file is replaced atomically
                    rep = EdgeCompanion.WriteFromStore(os.Dal, path, version, "essentia_streaming_extractor_music unknown");   // the rows record the build (review M4); the caller's fallback is overridden
                    // Store-v3 T07: with a generation the root gains origin{generation} (in the head); without, the pre-T07 shape.
                    var pathGen = System.IO.Path.Combine(dir, "with-generation.json");
                    EdgeCompanion.WriteFromStore(os.Dal, pathGen, version, "essentia_streaming_extractor_music unknown", 7);
                    assert(Truedat.Store.CatalogOrigin.TryReadHead(pathGen, EdgeCompanion.Contract, out var cg) && cg.Generation == 7,
                        "companion with a generation: origin.generation = 7 readable from the bounded head");
                    assert(!Truedat.Store.CatalogOrigin.TryReadHead(path, EdgeCompanion.Contract, out _), "companion without a generation: no origin block (pre-T07 shape kept)");
                    // Review I2: --store-info's coverage is the drain's gap — current version only, unavailable counted as captured.
                    var info = Truedat.Store.StoreInfoCommand.Collect(os.Path);
                    assert(info.EdgeTracks == 4 && info.EdgeUnavailable == 1 && info.EdgesVersion == version, $"store-info: captured 4 content keys at the current version (1 unavailable; the entity-keyed media counts, it is a description too) ({info.EdgeTracks}, {info.EdgeUnavailable}, {info.EdgesVersion})");
                }
                assert(rep.Tracks == 3 && rep.OmittedNoSha == 1 && rep.Bytes > 1000 && System.IO.File.Exists(path) && !System.IO.File.Exists(path + ".tmp"),
                    $"three sha-keyed media written, the entity-keyed one counted and omitted, no .tmp left ({rep.Tracks}, {rep.OmittedNoSha}, {rep.Bytes} B)");
                var text = System.IO.File.ReadAllText(path);
                assert(!text.Contains("00000000") && !text.Contains("99999999"), "numbers are written exactly as rounded (no net48 double round-trip noise like 0.77000000000000002)");
                assert(!text.Contains("\n"), "compact: one line, like mbxmoods.json");
                using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllBytes(path));
                var root = doc.RootElement;
                assert(root.GetProperty("contract").GetString() == "mbxedges.v1" && root.GetProperty("producerVersion").GetString() == version && root.GetProperty("extractor").GetString() == "essentia_streaming_extractor_music output-x64.5"
                    && root.GetProperty("trackCount").GetInt32() == 3 && root.TryGetProperty("generatedUtc", out _), $"header: contract, producerVersion, extractor FROM THE ROWS (not the caller's 'unknown'), generatedUtc, trackCount ({root.GetProperty("extractor").GetString()})");
                var order = root.GetProperty("spectralOrder");
                assert(order.GetArrayLength() == 11 && order[0].GetString() == "spectralCentroid" && order[10].GetString() == "spectralStrongPeak", "header: the fixed 11-name spectralOrder");
                var words = root.GetProperty("statusWords");
                assert(words.GetArrayLength() == 4 && words[0].GetString() == "ok" && words[3].GetString() == "failed", "header: the four status words");
                assert(root.GetProperty("rmsDefinitions").GetProperty("silenceMs").GetString()!.Contains("-60 dBFS"), "trailer: the rmsDefinitions text the hub reads");
                var tracks = root.GetProperty("tracks");
                var t1 = tracks.GetProperty(Sha1); var head = t1.GetProperty("head"); var tail = t1.GetProperty("tail");
                assert(t1.GetProperty("durationMs").GetInt32() == 240000 && head.GetProperty("status").GetString() == "ok" && head.GetProperty("startMs").GetInt32() == 0 && head.GetProperty("endMs").GetInt32() == 30000
                    && tail.GetProperty("startMs").GetInt32() == 210000, "a full track: duration and the two windows");
                var key = head.GetProperty("key");
                assert(key.GetProperty("name").GetString() == "C" && key.GetProperty("code").GetString() == "8B" && key.GetProperty("scale").GetString() == "major" && key.GetProperty("strength").GetDouble() > 0
                    && key.GetProperty("agreement").GetInt32() >= 1 && key.GetProperty("votesPresent").GetInt32() == 3, "key: name, code, scale, strength, agreement, votesPresent");
                var bpm = head.GetProperty("bpm");
                assert(Math.Abs(bpm.GetProperty("value").GetDouble() - 120) < 0.05 && bpm.GetProperty("beats").GetInt32() == 60 && bpm.GetProperty("regularity").GetDouble() > 0.99, "bpm: value, beats, regularity");
                var loud = tail.GetProperty("loudness");
                // T02: the tail's last second is digital silence, so endLufs is JSON null in the companion — the hub reads null as "no level".
                assert(Math.Abs(loud.GetProperty("startLufs").GetDouble() - (-14)) < 0.1 && loud.GetProperty("endLufs").ValueKind == System.Text.Json.JsonValueKind.Null && loud.GetProperty("fadeMs").GetInt32() > 3000
                    && loud.GetProperty("silenceMs").GetInt32() == 1100 && loud.GetProperty("silenceSource").GetString() == "pcm", $"loudness: start, end NULL (silence), fade, silence, source (end kind {loud.GetProperty("endLufs").ValueKind})");
                var tex = head.GetProperty("texture");
                assert(tex.GetProperty("mfccMean").GetArrayLength() == 13 && Math.Abs(tex.GetProperty("mfccMean")[0].GetDouble() - (-500)) < 0.1 && tex.GetProperty("mfccSpread").GetArrayLength() == 13
                    && tex.GetProperty("spectralMean").GetArrayLength() == 11 && Math.Abs(tex.GetProperty("spectralMean")[10].GetDouble() - 1100) < 0.1 && tex.GetProperty("spectralSpread").GetArrayLength() == 11,
                    "texture: mfccMean[13], mfccSpread[13], spectralMean[11], spectralSpread[11]");
                assert(tail.GetProperty("rms").GetProperty("crestDb").GetDouble() == 3.61 && tail.GetProperty("rms").GetProperty("transitionMs").GetInt32() == 0, "rms: crestDb, transitionMs");
                // T05 writer rule: a default-row track (no arrays) writes texture NULL — never a block of nulls the v1 reader would drop the track
                // for (EdgeRow.cs:187 requires 13/11 when present; null is accepted, :179) — and still carries key, bpm and loudness.
                var dHead = tracks.GetProperty(ShaB).GetProperty("head");
                assert(dHead.GetProperty("texture").ValueKind == System.Text.Json.JsonValueKind.Null && dHead.GetProperty("key").ValueKind == System.Text.Json.JsonValueKind.Object
                       && dHead.GetProperty("bpm").ValueKind == System.Text.Json.JsonValueKind.Object && dHead.GetProperty("loudness").ValueKind == System.Text.Json.JsonValueKind.Object,
                    $"default-row companion: texture null, key/bpm/loudness present (texture {dHead.GetProperty("texture").ValueKind})");
                // A04 (d), ruled by the operator 2026-10-16 ("yes to A04 (d)"): every folded edge window carries `centroid` and `dissonance` as DIRECT
                // keys — default and opt-in rows alike — so a hub gets the regional values without the texture block. Unknown keys are ignored by
                // the v1 reader, so the file stays v1-valid. Same 4 dp as texture.spectralMean, from the same summary (SpectralMean[0] / [5]).
                // ShaB's fold (no PCM pass) has the same frames as Sha1's, so its spectral means equal the opt-in track's texture.spectralMean.
                var refMean = tex.GetProperty("spectralMean");
                assert(dHead.TryGetProperty("centroid", out var dc) && dc.ValueKind == System.Text.Json.JsonValueKind.Number && dc.GetDouble() == refMean[0].GetDouble()
                       && dHead.TryGetProperty("dissonance", out var dd) && dd.GetDouble() == refMean[5].GetDouble(),
                    $"A04 (d): the default-row window carries centroid / dissonance directly ({(dHead.TryGetProperty("centroid", out var dc2) ? dc2.ToString() : "absent")} / {(dHead.TryGetProperty("dissonance", out var dd2) ? dd2.ToString() : "absent")}; expected {refMean[0]} / {refMean[5]})");
                assert(head.GetProperty("centroid").GetDouble() == tex.GetProperty("spectralMean")[0].GetDouble() && head.GetProperty("dissonance").GetDouble() == tex.GetProperty("spectralMean")[5].GetDouble(),
                    "A04 (d): an opt-in window carries the same two keys, equal to its texture.spectralMean[0] / [5]");
                var uHead = tracks.GetProperty(ShaC).GetProperty("head");
                assert(uHead.TryGetProperty("centroid", out var uc) && uc.ValueKind == System.Text.Json.JsonValueKind.Null && uHead.GetProperty("dissonance").ValueKind == System.Text.Json.JsonValueKind.Null,
                    "A04 (d): an edge that did not fold (unavailable) writes centroid / dissonance null, like its other blocks");
                assert(head.GetProperty("divergence").GetProperty("texture").ValueKind == System.Text.Json.JsonValueKind.Number && t1.GetProperty("divergence").GetProperty("harmony").GetDouble() > 0.3,
                    "divergence: per edge (vs the whole) and per track (head vs tail)");
                var tB = tracks.GetProperty(ShaB);
                assert(tB.GetProperty("tail").GetProperty("rms").ValueKind == System.Text.Json.JsonValueKind.Null && tB.GetProperty("tail").GetProperty("loudness").GetProperty("silenceSource").GetString() == "ebu",
                    "no PCM pass: rms null, silenceSource ebu");
                var tC = tracks.GetProperty(ShaC); var hC = tC.GetProperty("head");
                assert(tC.GetProperty("durationMs").GetInt32() == 15000 && hC.GetProperty("status").GetString() == "unavailable" && hC.GetProperty("endMs").GetInt32() == 7500
                    && hC.GetProperty("key").ValueKind == System.Text.Json.JsonValueKind.Null && hC.GetProperty("bpm").ValueKind == System.Text.Json.JsonValueKind.Null
                    && hC.GetProperty("loudness").ValueKind == System.Text.Json.JsonValueKind.Null && hC.GetProperty("texture").ValueKind == System.Text.Json.JsonValueKind.Null
                    && hC.GetProperty("rms").ValueKind == System.Text.Json.JsonValueKind.Null && tC.GetProperty("divergence").ValueKind == System.Text.Json.JsonValueKind.Null,
                    "unavailable: status and windows present, every other field null, track divergence null");
                assert(!tracks.TryGetProperty("entity:01a12e52-bf89-705c-9cf8-4c0133113724", out _), "the entity-keyed media is not in the file");
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }
        // ---------------------------------------------------------------- through the sink
        /// <summary>The same shape as the artifact stash: the extractor core stashes the fold under the path it analysed (the staged
        /// copy), Record ties it to the catalog key, the flush writes it under the entry's content key with an 'edges' run row.
        /// A failed fold leaves a failed run row and no rows; a Rekey follows a retry; a Discard drops it.</summary>
        static void TestSink(Action<bool, string> assert)
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), ".truedat-selftest", "edgesink-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                var store = System.IO.Path.Combine(dir, "truedat.db");
                Truedat.Store.ScanSink.Reset();
                Truedat.Store.ScanSink.StoreDirectory = dir;
                // The spill is transient working data and belongs with truedat's other transient working data, not in
                // an artifacts\ folder beside the operator's library (operator, 2026-10-18). A scan points WorkRoot at
                // the stage dir; here a temp stand-in plays that part, and the store folder must stay clean of it.
                var stage = System.IO.Path.Combine(dir, "stage");
                Truedat.Store.ArtifactStore.WorkRoot = stage;
                var entries = Truedat.Store.StoreSelfTest.FixtureEntries(@"D:\Music");
                var ok = SyntheticFold(dir);
                EdgeFoldResult failed;
                using (var f = EspbFile.Open(BuildSyntheticFrames(dir, 240, withHpcp: false))) failed = EdgeFold.Fold(f, 240, new EdgeSettings());
                // a.flac: folded under a retry's WAV path, re-keyed to the staged path the scan records with; b.mp3: failed fold; nosha.mp3: discarded.
                Truedat.Store.ScanSink.StashEdges(@"C:\stage\abc.wav", ok);
                Truedat.Store.ScanSink.Rekey(@"C:\stage\abc.wav", @"C:\stage\abc.flac");
                Truedat.Store.ScanSink.StashEdges(@"C:\stage\def.mp3", failed);
                Truedat.Store.ScanSink.StashEdges(@"C:\stage\nosha.mp3", ok);
                Truedat.Store.ScanSink.Discard(@"C:\stage\nosha.mp3");
                Truedat.Store.ScanSink.Record(@"D:\Music\Tool\a.flac", entries[@"D:\Music\Tool\a.flac"], @"C:\stage\abc.flac");
                Truedat.Store.ScanSink.Record(@"D:\Music\Tool\b.mp3", entries[@"D:\Music\Tool\b.mp3"], @"C:\stage\def.mp3");
                Truedat.Store.ScanSink.Record(@"D:\Music\odd\nosha.mp3", entries[@"D:\Music\odd\nosha.mp3"], @"C:\stage\nosha.mp3");
                assert(Truedat.Store.ScanSink.PendingEdges == 2, $"two folds tied to their entries (the re-keyed one and the failed one), the discarded one gone ({Truedat.Store.ScanSink.PendingEdges})");
                // Review I3: folds are spilled to disk, one packed file each, in the pending dir; nothing of them stays in memory.
                var pendingDir = Truedat.Store.ArtifactStore.PendingDir(dir);
                int spilled = System.IO.Directory.Exists(pendingDir) ? System.IO.Directory.GetFiles(pendingDir, "*.edges").Length : 0;
                assert(spilled == 2, $"the two tied folds are two .edges files in the pending dir, the discarded one deleted ({spilled})");
                assert(pendingDir.StartsWith(stage, StringComparison.OrdinalIgnoreCase),
                    $"the spill goes under the working root, not the store ({pendingDir})");
                assert(!System.IO.Directory.Exists(Truedat.Store.ArtifactStore.ArtifactsDir(dir)),
                    "the store folder gets no artifacts\\ directory from a scan that spills");
                var r = Truedat.Store.ScanSink.Flush(store, "output-x64.5", entries.Keys);
                assert(r.Ok && r.Edges == 1 && r.EdgesFailed == 1, $"the flush reports 1 edge row written and 1 failed fold ({r.Edges}, {r.EdgesFailed}, ok {r.Ok} {r.Error})");
                assert(System.IO.Directory.GetFiles(pendingDir, "*.edges").Length == 0, "the flush consumed every spilled fold");
                using (var os = Truedat.Store.OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                {
                    var sha = entries[@"D:\Music\Tool\a.flac"].AudioStreamSha256!;
                    assert(os.Dal.CountSegments(sha, ok.ProducerVersion) == 2, $"a.flac's edges are in the store under its content hash ({os.Dal.CountSegments(sha, ok.ProducerVersion)} segments)");
                    var back = EdgeStore.Read(os.Dal, sha, ok.ProducerVersion);
                    assert(back != null && back.Head.Whole?.Key?.Name == "C" && back.Tail.Rms != null, "and read back whole");
                    long okRuns = os.Dal.CountRuns(EdgeStore.ProducerKey, "ok"), failedRuns = os.Dal.CountRuns(EdgeStore.ProducerKey, "failed");
                    assert(okRuns == 1 && failedRuns == 1, $"one ok and one failed 'edges' run row ({okRuns}, {failedRuns})");
                    var shaB = entries[@"D:\Music\Tool\b.mp3"].AudioStreamSha256!;
                    assert(os.Dal.CountSegments(shaB, failed.ProducerVersion) == 0 && EdgeStore.Read(os.Dal, shaB, failed.ProducerVersion) == null, "the failed fold left no segment or value rows");
                    assert(os.Dal.RunErrorText(EdgeStore.ProducerKey, "failed")?.Contains("tonal.hpcp") == true, $"the failed run names the missing descriptor ({os.Dal.RunErrorText(EdgeStore.ProducerKey, "failed")})");
                    long ms = os.Dal.RunDurationMs(EdgeStore.ProducerKey, "ok") ?? -1;
                    assert(ms == ok.FoldMs + ok.PcmMs, $"the ok run row carries fold + PCM milliseconds ({ms} vs {ok.FoldMs + ok.PcmMs})");
                }
                assert(Truedat.Store.ScanSink.PendingEdges == 0, "the flush cleared the tied folds");
                // The refresh gate's list (operator 2026-10-12: the backfill is the scan): sha-keyed media with a description and no edge
                // row at the version. a.flac folded (not listed); b.mp3's fold failed (not listed: a recorded failure is not retried
                // implicitly, the scan's errors-ledger rule; listed again under --retry-errors); the entity-keyed media never (no sha).
                // legacy.flac is described by a second flush with no fold: the one track the list must name.
                Truedat.Store.ScanSink.Record(@"D:\Music\old\legacy.flac", entries[@"D:\Music\old\legacy.flac"], @"C:\stage\legacy.flac");
                var rL = Truedat.Store.ScanSink.Flush(store, "output-x64.5", entries.Keys);
                assert(rL.Ok && rL.Edges == 0, $"legacy.flac described without a fold ({rL.Ok} {rL.Error})");
                using (var os = Truedat.Store.OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                {
                    var shaA = entries[@"D:\Music\Tool\a.flac"].AudioStreamSha256!;
                    var shaB = entries[@"D:\Music\Tool\b.mp3"].AudioStreamSha256!;
                    var shaL = entries[@"D:\Music\old\legacy.flac"].AudioStreamSha256!;
                    var expect = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { shaL };
                    var stale = os.Dal.ContentKeysLackingEdges(EdgeStore.ProducerKey, ok.ProducerVersion, includeFailed: false);
                    assert(stale.SetEquals(expect) && expect.Count > 0, $"lacking edges: exactly the described track with no fold; not the folded one, not the failed one, not the undescribed ones ({stale.Count} listed, {expect.Count} expected: {string.Join(",", stale)})");
                    var retry = os.Dal.ContentKeysLackingEdges(EdgeStore.ProducerKey, ok.ProducerVersion, includeFailed: true);
                    assert(retry.Contains(shaB) && !retry.Contains(shaA) && retry.Count == expect.Count + 1, $"with --retry-errors the failed fold is listed again ({retry.Count})");
                    var newer = os.Dal.ContentKeysLackingEdges(EdgeStore.ProducerKey, "edges@v9;e30;p10", includeFailed: false);
                    assert(newer.Contains(shaA) && newer.Contains(shaB), "a newer producer version lists everything again, the old version's rows and failures included");
                    bool anyEntity = false; foreach (var k in retry) if (k.StartsWith("entity:", StringComparison.Ordinal)) anyEntity = true;
                    assert(!anyEntity, "no entity-keyed content is ever listed");
                    // TDA 040 (from the T14 plan check): the per-track failed-run test must find the TRACK's runs through
                    // ix_run_entity. Without sqlite_stat1 the planner preferred ix_run_outcome (outcome='failed'), so every candidate
                    // track walked every failed run in the store — tracks x failed runs on each --refresh. Pinned on truedat's own
                    // SQLite (winsqlite3), the plan the scan actually gets.
                    var plan = new List<string>();
                    using (var ep = os.Dal.Db.Prepare("EXPLAIN QUERY PLAN " + Truedat.Store.StoreDal.LackingEdgesSql))
                    {
                        ep.Bind(1, EdgeStore.ProducerKey); ep.Bind(2, ok.ProducerVersion); ep.Bind(3, 0L);
                        while (ep.Step()) plan.Add(ep.GetString(3) ?? "");
                    }
                    var runLine = plan.Find(p => p.Contains(" r ") || p.EndsWith(" r") || p.Contains("SEARCH r") || p.Contains("SCAN r")) ?? "(no run step)";
                    assert(runLine.Contains("ix_run_entity") && !runLine.Contains("ix_run_outcome"),
                        $"lacking-edges: the failed-run check searches the track's runs by ix_run_entity, never every failed run by ix_run_outcome (plan: {string.Join(" | ", plan)})");
                }
            }
            finally { Truedat.Store.ScanSink.Reset(); Truedat.Store.ScanSink.StoreDirectory = null; Truedat.Store.ArtifactStore.WorkRoot = null; try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>The scan's periodic checkpoint flush (beside its 5-minute catalog save, workers still running): files what is tied,
        /// leaves an in-flight track's untied fold alone, and the end-of-scan flush files that one once it is tied.</summary>
        static void TestSinkCheckpoint(Action<bool, string> assert)
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), ".truedat-selftest", "edgeckpt-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                var store = System.IO.Path.Combine(dir, "truedat.db");
                Truedat.Store.ScanSink.Reset();
                Truedat.Store.ScanSink.StoreDirectory = dir;
                var entries = Truedat.Store.StoreSelfTest.FixtureEntries(@"D:\Music");
                var ok = SyntheticFold(dir);
                Truedat.Store.ScanSink.StashEdges(@"C:\stage\a.flac", ok);
                Truedat.Store.ScanSink.Record(@"D:\Music\Tool\a.flac", entries[@"D:\Music\Tool\a.flac"], @"C:\stage\a.flac");
                Truedat.Store.ScanSink.StashEdges(@"C:\stage\inflight.mp3", ok);   // folded, its entry not recorded yet: a track still in flight
                var pendingDir = Truedat.Store.ArtifactStore.PendingDir(dir);
                var c = Truedat.Store.ScanSink.Flush(store, "output-x64.5", entries.Keys, final: false);
                int left = System.IO.Directory.GetFiles(pendingDir, "*.edges").Length;
                assert(c.Ok && c.Edges == 1 && left == 1, $"checkpoint: the tied fold is filed, the in-flight fold's file is left in pending ({c.Edges} filed, {left} left, ok {c.Ok} {c.Error})");
                Truedat.Store.ScanSink.Record(@"D:\Music\Tool\b.mp3", entries[@"D:\Music\Tool\b.mp3"], @"C:\stage\inflight.mp3");
                var f = Truedat.Store.ScanSink.Flush(store, "output-x64.5", entries.Keys);
                left = System.IO.Directory.GetFiles(pendingDir, "*.edges").Length;
                assert(f.Ok && f.Edges == 1 && left == 0, $"end of scan: the once-in-flight fold is filed, nothing left in pending ({f.Edges} filed, {left} left)");
                using (var os = Truedat.Store.OpenStore.Open(store, false, "selftest", DateTime.UtcNow.ToString("o")))
                {
                    var shaB = entries[@"D:\Music\Tool\b.mp3"].AudioStreamSha256!;
                    assert(os.Dal.CountSegments(shaB, ok.ProducerVersion) == 2, "and its edge rows are in the store under b.mp3's sha");
                }
                Truedat.Store.ScanSink.StashEdges(@"C:\stage\orphan.mp3", ok);   // a fold whose entry never came (failed health gate, killed worker)
                Truedat.Store.ScanSink.Flush(store, "output-x64.5", entries.Keys);
                assert(System.IO.Directory.GetFiles(pendingDir, "*.edges").Length == 0, "the end-of-scan flush drops an untied fold even when it has nothing to write");
            }
            finally { Truedat.Store.ScanSink.Reset(); Truedat.Store.ScanSink.StoreDirectory = null; try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        // ---------------------------------------------------------------- the PCM pass
        /// <summary>30 s of mono 44.1 kHz: a 0.5-amplitude sine (RMS 0.354), then either a 4 s linear fade to zero at 24-28 s or a
        /// hard cut at 28 s, then digital silence to 30 s. Mirrored for the head.</summary>
        static float[] SynthPcm(bool fade, bool outerAtStart, double amp = 0.5)
        {
            int n = 30 * EdgePcm.SampleRate; var s = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / EdgePcm.SampleRate;
                double tt = outerAtStart ? 30 - t : t;             // time measured from the inner end toward the outer end
                double g = tt < 24 ? 1 : tt < 28 ? (fade ? (28 - tt) / 4 : 1) : 0;
                s[i] = (float)(amp * g * Math.Sin(2 * Math.PI * 440 * t));
            }
            return s;
        }

        static void TestPcm(Action<bool, string> assert)
        {
            // Tail, fade: silence = the 20 slices from 28 s (the fade's last 0.008 s dips under -60 dBFS inside the slice ending at 28.0,
            // which still peaks above it); the transition is the steep end of the fade — from the last slice within 20 dB of the window's
            // mean RMS (~-20 dB = amplitude ~0.043, reached at 27.65 s) to the silence floor at 28.0 s: 300 ms.
            var r = EdgePcm.FromSamples(SynthPcm(fade: true, outerAtStart: false), outerAtStart: false);
            assert(r != null && r.Slices == 300, $"300 slices of 100 ms from 30 s ({r?.Slices})");
            assert(r != null && r.SilenceMs == 2000, $"tail fade: 2,000 ms of digital silence at the outer end ({r?.SilenceMs})");
            assert(r != null && r.TransitionMs >= 200 && r.TransitionMs <= 400, $"tail fade: the transition is the last 20 dB of the fade, ~300 ms ({r?.TransitionMs})");
            assert(r != null && r.CrestDb.HasValue && r.CrestDb > 3.5 && r.CrestDb < 5.0, $"crest: sine peak 0.5 over the window's mean slice RMS ~0.31 -> ~4.2 dB ({r?.CrestDb:F2})");
            // Tail, hard cut at 28 s: the last loud slice ends exactly where silence starts -> 0 ms (a cut), silence 2,000.
            r = EdgePcm.FromSamples(SynthPcm(fade: false, outerAtStart: false), outerAtStart: false);
            assert(r != null && r.TransitionMs == 0 && r.SilenceMs == 2000, $"tail cut: transition 0 ms, silence 2,000 ms ({r?.TransitionMs}, {r?.SilenceMs})");
            assert(r != null && r.CrestDb.HasValue && Math.Abs(r.CrestDb.Value - 20 * Math.Log10(0.5 / (0.5 / Math.Sqrt(2) * 28.0 / 30.0))) < 0.1,
                $"tail cut: crest is exactly peak over mean RMS, 28 loud slices of 30 ({r?.CrestDb:F3})");
            // Head, mirrored: silence first, then the fade-in.
            r = EdgePcm.FromSamples(SynthPcm(fade: true, outerAtStart: true), outerAtStart: true);
            assert(r != null && r.SilenceMs == 2000 && r.TransitionMs >= 200 && r.TransitionMs <= 400, $"head fade: 2,000 ms of leading silence, ~300 ms transition ({r?.SilenceMs}, {r?.TransitionMs})");
            // No sound at all: silence is the whole window, crest null (mean RMS 0), transition 0.
            r = EdgePcm.FromSamples(new float[30 * EdgePcm.SampleRate], outerAtStart: false);
            assert(r != null && r.CrestDb == null && r.SilenceMs == 30000 && r.TransitionMs == 0, $"all silent: crest null, silence 30,000 ms ({r?.CrestDb}, {r?.SilenceMs})");
            // Under 1 s: null, by the fixture's definition.
            assert(EdgePcm.FromSamples(new float[EdgePcm.SliceSamples * 9], false) == null, "under ten slices (1 s) is null");
            // Without ffmpeg: null, never a substitute.
            assert(EdgePcm.Analyze("nowhere.flac", null, 0, 30, false) == null, "no ffmpeg -> null");
            // codex 2026-10-23 F7: a decode that failed, or stopped well short of the window, is not that window's measurement.
            int full = 30 * EdgePcm.SampleRate;
            assert(EdgePcm.Covers(0, full, 30) && EdgePcm.Covers(0, (int)(full * 0.85), 30),
                "a clean decode of the window, or of most of it (a container overstating its length by seconds), is accepted");
            assert(!EdgePcm.Covers(1, full, 30), "a nonzero ffmpeg exit is refused even with a full window's samples before the failure");
            assert(!EdgePcm.Covers(1, 5 * EdgePcm.SampleRate, 30), "several seconds of a 30 s tail and then a failure: refused, the EBU answer stands");
            assert(!EdgePcm.Covers(0, (int)(full * 0.5), 30), "a clean exit that delivered half the window is refused");

            // The real pipe, on a WAV we write (found beside the test binary, as the scan finds it); skipped when ffmpeg is absent.
            var ffmpeg = Program.FindFfmpegForTests();
            if (ffmpeg == null) { Console.WriteLine("  SKIP  ffmpeg.exe not found beside the test binary"); return; }
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), ".truedat-selftest", "pcm-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                // 60 s WAV: 30 s of the head shape (silence then fade-in) followed by 30 s of the tail shape (fade-out then silence).
                var head = SynthPcm(fade: true, outerAtStart: true); var tail = SynthPcm(fade: true, outerAtStart: false);
                var all = new float[head.Length + tail.Length]; Array.Copy(head, all, head.Length); Array.Copy(tail, 0, all, head.Length, tail.Length);
                var wav = System.IO.Path.Combine(dir, "edges.wav"); WriteWav16(wav, all, EdgePcm.SampleRate);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var h = EdgePcm.Analyze(wav, ffmpeg, 0, 30, outerAtStart: true);
                var t = EdgePcm.Analyze(wav, ffmpeg, 30, 30, outerAtStart: false);
                sw.Stop();
                assert(h != null && h.Slices == 300 && h.SilenceMs == 2000 && h.TransitionMs >= 200 && h.TransitionMs <= 400, $"ffmpeg head window: 300 slices, 2,000 ms silence, ~300 ms transition ({h?.Slices}, {h?.SilenceMs}, {h?.TransitionMs})");
                assert(t != null && t.Slices == 300 && t.SilenceMs == 2000 && t.TransitionMs >= 200 && t.TransitionMs <= 400 && t.CrestDb > 3.5 && t.CrestDb < 5.0, $"ffmpeg tail window: 300 slices, 2,000 ms silence, ~300 ms transition, crest ~4.2 ({t?.Slices}, {t?.SilenceMs}, {t?.TransitionMs}, {t?.CrestDb:F2})");
                Console.WriteLine($"        two 30 s windows through ffmpeg in {sw.ElapsedMilliseconds} ms");
                assert(EdgePcm.Analyze(System.IO.Path.Combine(dir, "missing.wav"), ffmpeg, 0, 30, false) == null, "a file ffmpeg cannot decode -> null, no throw");

                // Codex review 2026-10-20 #9: an ffmpeg that holds its stdout open without producing — staged here with a named pipe
                // nothing ever writes to — must be killed at the deadline, never left to block the worker. Proven only when the stall
                // actually stages (ffmpeg opens the pipe and blocks); otherwise reported as a skip, not a pass.
                var pipeName = "truedat-stall-" + Guid.NewGuid().ToString("N");
                using (var server = new System.IO.Pipes.NamedPipeServerStream(pipeName, System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous))
                {
                    var accept = server.WaitForConnectionAsync();
                    int savedDeadline = EdgePcm.StallTimeoutMs; EdgePcm.StallTimeoutMs = 3000;
                    var stallSw = System.Diagnostics.Stopwatch.StartNew(); EdgePcmResult? stalled;
                    try { stalled = EdgePcm.Analyze(@"\\.\pipe\" + pipeName, ffmpeg, 0, 30, false); }
                    finally { EdgePcm.StallTimeoutMs = savedDeadline; }
                    stallSw.Stop();
                    if (!accept.IsCompleted || stallSw.ElapsedMilliseconds < 2500)
                        Console.WriteLine($"  SKIP  the ffmpeg stall could not be staged on this machine (connected {accept.IsCompleted}, {stallSw.ElapsedMilliseconds} ms)");
                    else
                        assert(stalled == null && stallSw.ElapsedMilliseconds < 20000, $"a stalled ffmpeg (stdout open, nothing decoded) is killed at the deadline and the window is null ({stallSw.ElapsedMilliseconds} ms)");
                }
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        static void WriteWav16(string path, float[] mono, int rate)
        {
            using var fs = System.IO.File.Create(path);
            using var w = new System.IO.BinaryWriter(fs);
            int dataBytes = mono.Length * 2;
            w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + dataBytes); w.Write(new[] { 'W', 'A', 'V', 'E' });
            w.Write(new[] { 'f', 'm', 't', ' ' }); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(dataBytes);
            foreach (var s in mono) w.Write((short)Math.Max(-32768, Math.Min(32767, Math.Round(s * 32767))));
        }

        // ---------------------------------------------------------------- the fold on real audio
        /// <summary>Every full frames file the build spike left under essentia-build/spike-x5/real (MP3, M4A, WMA, two FLACs; found
        /// upward, skipped when absent) folds to two ok edges whose TAIL series are fully defined — the tail is the splice point,
        /// and the EBU series are trailing windows that start late, so a fold that assumed frame i sits at 0.1·i would leave the
        /// last bins of every real tail empty. Keys, bpm and loudness must be present and plausible; the rows are printed for review.</summary>
        static void TestFoldReal(Action<bool, string> assert)
        {
            var realDir = FindUpwardDir(AppContext.BaseDirectory, System.IO.Path.Combine("essentia-build", "spike-x5", "real"));
            if (realDir == null) { Console.WriteLine("  SKIP  essentia-build/spike-x5/real not found from the test binary"); return; }
            var settings = new EdgeSettings(); int files = 0;
            foreach (var bin in System.IO.Directory.GetFiles(realDir, "*.x5fb.bin"))
            {
                var json = System.IO.Path.ChangeExtension(bin, ".json");
                if (!System.IO.File.Exists(json)) continue;
                double duration;
                using (var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllBytes(json)))
                    duration = doc.RootElement.GetProperty("metadata").GetProperty("audio_properties").GetProperty("length").GetDouble();
                EdgeFoldResult r;
                using (var f = EspbFile.Open(bin)) r = EdgeFold.Fold(f, duration, settings);
                files++;
                var name = System.IO.Path.GetFileName(bin).Replace(".x5fb.bin", "");
                var h = r.Head; var t = r.Tail;
                assert(h.Status == "ok" && t.Status == "ok", $"{name}: both edges ok ({h.Status} / {t.Status}{(h.Error != null ? ": " + h.Error : "")})");
                if (h.Status != "ok" || t.Status != "ok") continue;
                int tailMomUndef = CountUndefined(t.MomentaryCentiDb), tailStUndef = CountUndefined(t.ShortTermCentiDb);
                int headMomUndef = CountUndefined(h.MomentaryCentiDb), headStUndef = CountUndefined(h.ShortTermCentiDb);
                assert(t.MomentaryCentiDb.Length == 300 && tailMomUndef == 0 && t.ShortTermCentiDb.Length == 300 && tailStUndef == 0,
                    $"{name}: the tail's EBU series are 300 samples with no undefined bin (momentary {tailMomUndef} undefined, short-term {tailStUndef})");
                assert(headMomUndef >= 2 && headMomUndef <= 4 && headStUndef >= 28 && headStUndef <= 30,
                    $"{name}: the head's EBU series are undefined exactly before the first full window, 400 ms and 3 s (momentary {headMomUndef} ~3, short-term {headStUndef} ~29)");
                assert(h.Whole?.Key != null && t.Whole?.Key != null, $"{name}: a key on both edges ({h.Whole?.Key?.Name} {h.Whole?.Key?.Scale} / {t.Whole?.Key?.Name} {t.Whole?.Key?.Scale})");
                assert(h.Bpm != null && t.Bpm != null && h.Bpm.Value > 40 && h.Bpm.Value < 240 && t.Bpm.Value > 40 && t.Bpm.Value < 240,
                    $"{name}: a plausible bpm on both edges ({h.Bpm?.Value:F1} x{h.Bpm?.Beats} / {t.Bpm?.Value:F1} x{t.Bpm?.Beats})");
                assert(h.Loudness?.MeanLufs != null && t.Loudness?.MeanLufs != null && h.Loudness.MeanLufs > -60 && h.Loudness.MeanLufs < 0 && t.Loudness.MeanLufs > -60 && t.Loudness.MeanLufs < 0,
                    $"{name}: mean loudness inside -60..0 LUFS on both edges ({h.Loudness?.MeanLufs:F1} / {t.Loudness?.MeanLufs:F1})");
                Console.WriteLine($"        {name,-5} {duration,7:F1}s  head: {h.Whole?.Key?.Name}{(h.Whole?.Key?.Scale == "minor" ? "m" : "")} {h.Whole?.Key?.Code} agree {h.Whole?.Key?.Agreement}/{h.Whole?.Key?.VotesPresent}, {h.Bpm?.Value:F1} bpm reg {h.Bpm?.Regularity:F2}, {h.Loudness?.StartLufs:F1}->{h.Loudness?.EndLufs:F1} LUFS fade {h.Loudness?.FadeMs?.ToString() ?? "-"} sil {h.Loudness?.SilenceMs}");
                Console.WriteLine($"        {"",-5} {"",7}   tail: {t.Whole?.Key?.Name}{(t.Whole?.Key?.Scale == "minor" ? "m" : "")} {t.Whole?.Key?.Code} agree {t.Whole?.Key?.Agreement}/{t.Whole?.Key?.VotesPresent}, {t.Bpm?.Value:F1} bpm reg {t.Bpm?.Regularity:F2}, {t.Loudness?.StartLufs:F1}->{t.Loudness?.EndLufs:F1} LUFS fade {t.Loudness?.FadeMs?.ToString() ?? "-"} sil {t.Loudness?.SilenceMs}; head-tail div L {r.HeadTail?.Loudness:F2} T {r.HeadTail?.Texture:F2} H {r.HeadTail?.Harmony:F2}");
            }
            assert(files >= 1, $"at least one real frames file folded ({files})");
        }

        static int CountUndefined(short[] s) { int n = 0; foreach (var v in s) if (v == short.MinValue) n++; return n; }

        // ---------------------------------------------------------------- the fold
        /// <summary>A synthetic 240 s track: a 6 s fade-in from silence then steady -14 LUFS; a 4 s fade-out to silence at the end
        /// with 1 s of digital silence after it; beats every 0.5 s (120 bpm) throughout; C-major chroma in the head, A-minor in
        /// the tail; MFCC and spectral scalars constant per half so spreads are 0 and means are known.</summary>
        // Digital silence as the extractor actually emits it: the momentary operator clips power to 1e-30 before log10
        // (unaryoperator.cpp:83-84) and shifts by -0.691 (loudnessebur128.cpp:157-162) -> -300.691 LUFS. Momentary is NOT gated.
        internal const double ExtractorSilence = 10 * -30 - 0.691;
        static string BuildSyntheticFrames(string dir, double durationSec = 240, bool withBeats = true, bool withHpcp = true, double silentTailSec = 1, double silentLevel = ExtractorSilence)
        {
            int ll = (int)Math.Round(durationSec / EdgeFold.LowLevelHop), tn = (int)Math.Round(durationSec / EdgeFold.TonalHop), eb = (int)Math.Round(durationSec / EdgeFold.EbuHop);
            // EBU series as the extractor emits them: trailing windows every 0.1 s, so momentary has eb-3 frames (400 ms window) and
            // short-term eb-29 (3 s window); frame i's value is the level at the time its window ENDS. (A 60 s track: 597 / 571.)
            // silentTailSec > 1 stretches the trailing digital silence (T02: a whole edge of silence must fold to NULL loudness).
            Func<double, double> lufsAt = t =>
            {
                if (t < 6) return -40 + (26.0 * t / 6.0);                       // fade-in: -40 -> -14 over 6 s
                if (t >= durationSec - silentTailSec) return silentLevel;       // trailing silence (silentTailSec, default 1 s at -90 = digital; a -65 run is "quiet but measurable")
                if (t >= durationSec - silentTailSec - 4) return -14 - 30.0 * (t - (durationSec - silentTailSec - 4)) / 4.0;   // fade-out 4 s: -14 -> -44
                return -14;
            };
            var momentary = new float[Math.Max(0, eb - 3)]; var shortTerm = new float[Math.Max(0, eb - 29)];
            for (int i = 0; i < momentary.Length; i++) momentary[i] = (float)lufsAt(EdgeFold.EbuTimeOf(momentary.Length, durationSec, i));
            for (int i = 0; i < shortTerm.Length; i++) shortTerm[i] = (float)lufsAt(EdgeFold.EbuTimeOf(shortTerm.Length, durationSec, i));
            var hpcp = new float[tn * 36];
            for (int i = 0; i < tn; i++)
            {
                double t = i * EdgeFold.TonalHop; bool head = t < durationSec / 2;
                int[] notes = head ? new[] { 3, 7, 10 } : new[] { 0, 3, 7 };   // C-E-G | A-C-E (bin 0 = A)
                foreach (var s in notes) { hpcp[i * 36 + s * 3] = 1f; hpcp[i * 36 + s * 3 + 1] = 0.6f; hpcp[i * 36 + (s * 3 + 35) % 36] = 0.6f; }
            }
            var mfcc = new float[ll * 13]; var spectral = new float[EdgeFold.SpectralOrder.Length][];
            for (int k = 0; k < spectral.Length; k++) spectral[k] = new float[ll];
            for (int i = 0; i < ll; i++)
            {
                double t = i * EdgeFold.LowLevelHop; bool head = t < durationSec / 2;
                for (int c = 0; c < 13; c++) mfcc[i * 13 + c] = head ? (float)(-500 + 10 * c) : (float)(-600 + 10 * c);
                for (int k = 0; k < spectral.Length; k++) spectral[k][i] = head ? (float)(100 * (k + 1)) : (float)(200 * (k + 1));
            }
            var beats = new List<float>(); for (double t = 0.25; t < durationSec; t += 0.5) beats.Add((float)t);
            var beatsLoud = new float[beats.Count]; for (int i = 0; i < beatsLoud.Length; i++) beatsLoud[i] = 0.3f;

            var descs = new List<(string, EspbType, uint, uint, uint, byte[])>
            {
                ("lowlevel.loudness_ebu128.momentary", EspbType.F32Vector, 0u, (uint)momentary.Length, 1u, F32(momentary)),
                ("lowlevel.loudness_ebu128.short_term", EspbType.F32Vector, 0u, (uint)shortTerm.Length, 1u, F32(shortTerm)),
                ("lowlevel.loudness_ebu128.integrated", EspbType.F32Scalar, 1u, 1u, 1u, F32(-15f)),
                ("lowlevel.mfcc", EspbType.F32Matrix, 0u, (uint)ll, 13u, F32(mfcc)),
                ("metadata.audio_properties.length", EspbType.F32Scalar, 1u, 1u, 1u, F32((float)durationSec)),
            };
            if (withHpcp) descs.Add(("tonal.hpcp", EspbType.F32Matrix, 0u, (uint)tn, 36u, F32(hpcp)));
            if (withBeats) { descs.Add(("rhythm.beats_position", EspbType.F32Vector, 1u, (uint)beats.Count, 1u, F32(beats.ToArray()))); descs.Add(("rhythm.beats_loudness", EspbType.F32Vector, 0u, (uint)beatsLoud.Length, 1u, F32(beatsLoud))); }
            for (int k = 0; k < spectral.Length; k++) descs.Add((EdgeFold.SpectralOrder[k], EspbType.F32Vector, 0u, (uint)ll, 1u, F32(spectral[k])));
            descs.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
            var path = System.IO.Path.Combine(dir, Guid.NewGuid().ToString("N") + ".bin");
            System.IO.File.WriteAllBytes(path, BuildEspb("output-x64.5", descs.ToArray()));
            return path;
        }

        static void TestFold(Action<bool, string> assert)
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), ".truedat-selftest", "fold-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                var settings = new EdgeSettings();
                assert(settings.ProducerVersion == "edges@v3;e30;p10", $"the producer version carries the algorithm generation and the settings ({settings.ProducerVersion})");

                EdgeFoldResult r;
                using (var f = EspbFile.Open(BuildSyntheticFrames(dir))) r = EdgeFold.Fold(f, 240, settings);
                var h = r.Head; var t = r.Tail;
                assert(h.Status == "ok" && t.Status == "ok" && h.StartMs == 0 && h.EndMs == 30000 && t.StartMs == 210000 && t.EndMs == 240000, $"a 240 s track has two full 30 s edges, status ok ({h.Status} {h.StartMs}-{h.EndMs}; {t.Status} {t.StartMs}-{t.EndMs})");
                assert(h.MomentaryCentiDb.Length == 300 && h.ShortTermCentiDb.Length == 300 && h.MomentaryCentiDb[299] == -1400 && h.MomentaryCentiDb[3] == -3827
                    && h.MomentaryCentiDb[0] == short.MinValue && h.MomentaryCentiDb[2] == short.MinValue && h.ShortTermCentiDb[28] == short.MinValue && h.ShortTermCentiDb[29] != short.MinValue,
                    $"the EBU pair is stored at 10/s as int16 centi-dB: 300 values; bins before the first full window are undefined (3 momentary, 29 short-term), then -3827 at 0.4 s, -1400 at 30 s ({h.MomentaryCentiDb.Length}, [0]={h.MomentaryCentiDb[0]}, [3]={h.MomentaryCentiDb[3]}, [299]={h.MomentaryCentiDb[299]}, st[28]={h.ShortTermCentiDb[28]}, st[29]={h.ShortTermCentiDb[29]})");
                assert(t.MomentaryCentiDb.Length == 300 && t.MomentaryCentiDb[299] == -30069 && t.ShortTermCentiDb[299] == -30069 && t.MomentaryCentiDb[0] == -1400 && CountUndefined(t.MomentaryCentiDb) == 0 && CountUndefined(t.ShortTermCentiDb) == 0,
                    $"the tail's series are fully defined and end on the window that ends at the track end: -1400 first, -30069 last (the extractor's -300.691 silence floor, kept raw in the series) ([0]={t.MomentaryCentiDb[0]}, [299]={t.MomentaryCentiDb[299]}, undefined {CountUndefined(t.MomentaryCentiDb)}/{CountUndefined(t.ShortTermCentiDb)})");
                assert(h.BeatsMs.Length == 60 && h.BeatsMs[0] == 250 && h.BeatsMs[59] == 29750 && h.BeatsLoudness.Length == 60 && t.BeatsMs.Length == 60 && t.BeatsMs[0] == 210250,
                    $"beats inside each window, as ms from the track start ({h.BeatsMs.Length} head, first {(h.BeatsMs.Length > 0 ? h.BeatsMs[0] : -1)}; {t.BeatsMs.Length} tail)");
                assert(h.Bpm != null && Math.Abs(h.Bpm.Value - 120) < 0.01 && h.Bpm.Beats == 60 && h.Bpm.Regularity > 0.99, $"bpm from in-window intervals: 120, 60 beats, regularity ~1 ({h.Bpm?.Value:F2}, {h.Bpm?.Beats}, {h.Bpm?.Regularity:F3})");
                assert(h.Whole != null && h.Whole.Key != null && h.Whole.Key.Name == "C" && h.Whole.Key.Scale == "major" && h.Whole.Key.Code == "8B", $"head key from the window's HPCP mean: C major 8B ({h.Whole?.Key?.Name} {h.Whole?.Key?.Scale})");
                assert(t.Whole != null && t.Whole.Key != null && t.Whole.Key.Name == "A" && t.Whole.Key.Scale == "minor", $"tail key: A minor ({t.Whole?.Key?.Name} {t.Whole?.Key?.Scale})");
                assert(h.Sub.Length == 3 && h.Sub[0].StartMs == 0 && h.Sub[0].EndMs == 10000 && h.Sub[2].StartMs == 20000 && h.Sub[2].EndMs == 30000 && h.Sub[1].Key != null && h.Sub[1].Key!.Name == "C", "three 10 s sub-windows, each with its own summary and key");
                assert(h.Whole != null && h.Whole.MfccMean.Length == 13 && Math.Abs(h.Whole.MfccMean[0] - (-500)) < 1e-3 && Math.Abs(h.Whole.MfccMean[12] - (-380)) < 1e-3 && h.Whole.MfccSpread[0] < 1e-6,
                    $"MFCC mean and spread over the window ({h.Whole!.MfccMean[0]:F1}, spread {h.Whole.MfccSpread[0]:E1})");
                assert(h.Whole.SpectralMean.Length == 11 && Math.Abs(h.Whole.SpectralMean[0] - 100) < 1e-3 && Math.Abs(t.Whole!.SpectralMean[10] - 2200) < 1e-3 && h.Whole.SpectralSpread[3] < 1e-6,
                    $"the 11 spectral scalars in the declared order, mean and spread ({h.Whole.SpectralMean[0]:F1}, {t.Whole!.SpectralMean[10]:F1})");
                // Fade and silence are quantised to the 0.1 s window grid and the fade is "departure from the plateau by 1 LU", so a
                // 6 s linear ramp reads 5,800 ms (it reaches -15 at 5.77 s) and the 4 s ramp 3,900 ms; the digital silence from
                // 239 s is the 11 windows ending in [239, 240].
                assert(h.Loudness != null && h.Loudness.StartLufs < -35 && Math.Abs(h.Loudness.EndLufs!.Value - (-14)) < 0.01 && h.Loudness.FadeMs.HasValue && Math.Abs(h.Loudness.FadeMs.Value - 6000) <= 300 && h.Loudness.SilenceMs == 0 && h.Loudness.SilenceSource == "ebu",
                    $"head loudness: start ~-36, end -14, a ~6 s fade-in, no silence, source ebu ({h.Loudness?.StartLufs:F1}, {h.Loudness?.EndLufs:F1}, fade {h.Loudness?.FadeMs}, silence {h.Loudness?.SilenceMs})");
                // T02 (F6.4, I03): the last second of the tail is digital silence — every window below the silence floor — so its
                // loudness is NULL (no measurable level), never a sentinel like -90/-300 stored as if it were LUFS.
                assert(t.Loudness != null && Math.Abs(t.Loudness.StartLufs!.Value - (-14)) < 0.01 && t.Loudness.EndLufs == null && t.Loudness.FadeMs.HasValue && Math.Abs(t.Loudness.FadeMs.Value - 4000) <= 300 && t.Loudness.SilenceMs >= 1000 && t.Loudness.SilenceMs <= 1100,
                    $"tail loudness: start -14, end NULL (digital silence), a ~4 s fade-out, ~1,000 ms of trailing silence ({t.Loudness?.StartLufs:F1}, {(t.Loudness?.EndLufs.HasValue == true ? t.Loudness.EndLufs.Value.ToString("F1") : "null")}, fade {t.Loudness?.FadeMs}, silence {t.Loudness?.SilenceMs})");
                assert(h.Divergence != null && h.Divergence.Loudness.HasValue && h.Divergence.Texture.HasValue && h.Divergence.Harmony.HasValue
                    && r.HeadTail != null && r.HeadTail.Harmony > 0.3 && r.HeadTail.Texture > 0 && r.HeadTail.Loudness < 0.5,
                    $"divergence per edge vs the whole and head vs tail (head-tail harmony {r.HeadTail?.Harmony:F2}, texture {r.HeadTail?.Texture:F2}, loudness {r.HeadTail?.Loudness:F2})");
                assert(r.DurationMs == 240000 && r.ProducerVersion == "edges@v3;e30;p10", $"the result names the duration and the producer version ({r.ProducerVersion})");

                // T02 (F6.4, I03): a tail edge that is ENTIRELY digital silence has no measurable loudness: every LUFS scalar is NULL,
                // the loudness divergences are NULL (not 1.00 from a -300 sentinel), and the head is untouched. 240 s with a 31 s
                // silent tail: the 30 s tail edge holds nothing but -90 windows.
                using (var f = EspbFile.Open(BuildSyntheticFrames(dir, 240, silentTailSec: 31))) r = EdgeFold.Fold(f, 240, settings);
                {
                    var ts = r!.Tail; var hs = r.Head;
                    assert(ts.Loudness != null && ts.Loudness.MeanLufs == null && ts.Loudness.StartLufs == null && ts.Loudness.EndLufs == null,
                        $"an all-silent tail edge folds to NULL loudness scalars, never a sentinel (mean {(ts.Loudness?.MeanLufs.HasValue == true ? ts.Loudness.MeanLufs.Value.ToString("F1") : "null")}, start {(ts.Loudness?.StartLufs.HasValue == true ? ts.Loudness.StartLufs.Value.ToString("F1") : "null")}, end {(ts.Loudness?.EndLufs.HasValue == true ? ts.Loudness.EndLufs.Value.ToString("F1") : "null")})");
                    assert(ts.Loudness != null && ts.Loudness.SilenceMs >= 29000 && ts.Loudness.FadeMs == null,
                        $"the silent tail still reports its silence run and no fade (silence {ts.Loudness?.SilenceMs}, fade {ts.Loudness?.FadeMs})");
                    assert(ts.Divergence != null && ts.Divergence.Loudness == null && r.HeadTail != null && r.HeadTail.Loudness == null,
                        $"loudness divergence is NULL when one side has no level (edge-vs-whole {(ts.Divergence?.Loudness.HasValue == true ? ts.Divergence.Loudness.Value.ToString("F2") : "null")}, head-tail {(r.HeadTail?.Loudness.HasValue == true ? r.HeadTail.Loudness.Value.ToString("F2") : "null")})");
                    assert(hs.Loudness?.MeanLufs != null && hs.Loudness.MeanLufs > -60, $"the head is unaffected ({hs.Loudness?.MeanLufs:F1})");
                }

                // T02 review finding 1: a QUIET tail (-65 LUFS, below the -60 silenceMs floor) is measurable — its scalars stay finite
                // while silenceMs still reports the run. Nothing above the extractor's own silence floor (-300.691) is "no level";
                // there is no gate on momentary loudness (the -70 figure is the INTEGRATED gate and does not apply here).
                using (var f = EspbFile.Open(BuildSyntheticFrames(dir, 240, silentTailSec: 31, silentLevel: -65))) r = EdgeFold.Fold(f, 240, settings);
                {
                    var tq = r!.Tail;
                    assert(tq.Loudness?.MeanLufs != null && Math.Abs(tq.Loudness.MeanLufs.Value - (-65)) < 0.01 && tq.Loudness.EndLufs != null && tq.Loudness.SilenceMs >= 29000,
                        $"a quiet-but-measurable tail keeps finite LUFS (mean {(tq.Loudness?.MeanLufs.HasValue == true ? tq.Loudness.MeanLufs.Value.ToString("F1") : "null")}) with silence {tq.Loudness?.SilenceMs} ms — no gate on momentary loudness; the -60 figure is the silenceMs floor only");
                    assert(r.HeadTail?.Loudness != null && r.HeadTail.Loudness == 1.0, $"its head-tail loudness divergence is a real 1.00 (-14 vs -65 clamps), not a null ({r.HeadTail?.Loudness:F2})");
                }

                // Keeper P2 on 24020d1: momentary loudness is NOT gated (loudnessebur128.cpp description), so a genuine -75 LUFS tail —
                // below the -70 INTEGRATED-loudness gate — is a finite reading that must survive. Only the extractor's own silence floor
                // (-300.691, power clipped to 1e-30) means "no level".
                using (var f = EspbFile.Open(BuildSyntheticFrames(dir, 240, silentTailSec: 31, silentLevel: -75))) r = EdgeFold.Fold(f, 240, settings);
                {
                    var t75 = r!.Tail;
                    assert(t75.Loudness?.MeanLufs != null && Math.Abs(t75.Loudness.MeanLufs.Value - (-75)) < 0.01 && t75.Loudness.StartLufs != null && t75.Loudness.EndLufs != null,
                        $"a genuine -75 LUFS tail (below the integrated gate, above the extractor floor) keeps finite scalars (mean {(t75.Loudness?.MeanLufs.HasValue == true ? t75.Loudness.MeanLufs.Value.ToString("F1") : "null")})");
                    assert(r.HeadTail?.Loudness == 1.0, $"and a real head-tail divergence ({(r.HeadTail?.Loudness.HasValue == true ? r.HeadTail.Loudness.Value.ToString("F2") : "null")})");
                }
                // Keeper probe on dbd20a0: a representable non-zero power (1e-22, well above the 1e-30 clip) reads -220.691 — a reading, not the floor.
                // Only the clipped value itself (10*log10(1e-30) - 0.691) is "no level"; -200 was another blanket cutoff.
                using (var f = EspbFile.Open(BuildSyntheticFrames(dir, 240, silentTailSec: 31, silentLevel: 10 * -22 - 0.691))) r = EdgeFold.Fold(f, 240, settings);
                assert(r!.Tail.Loudness?.MeanLufs != null && Math.Abs(r.Tail.Loudness.MeanLufs.Value - (-220.691)) < 0.01,
                    $"a -220.691 LUFS tail (representable non-zero power, above the clipped floor) keeps its finite value ({(r.Tail.Loudness?.MeanLufs.HasValue == true ? r.Tail.Loudness.MeanLufs.Value.ToString("F3") : "null")})");
                assert(Math.Abs(EdgeFold.ExtractorSilenceLufs - (-300.691)) < 1e-9, $"the floor constant is derived from the source, 10*log10(1e-30) - 0.691 = -300.691 ({EdgeFold.ExtractorSilenceLufs:F6})");
                // Digital silence exactly at the extractor floor (-300.691): no level, null — the sentinel case T02 exists for.
                using (var f = EspbFile.Open(BuildSyntheticFrames(dir, 240, silentTailSec: 31, silentLevel: ExtractorSilence))) r = EdgeFold.Fold(f, 240, settings);
                assert(r!.Tail.Loudness?.MeanLufs == null && r.HeadTail?.Loudness == null, $"a tail at the extractor's silence floor is null, no divergence ({r.Tail.Loudness?.MeanLufs}, {r.HeadTail?.Loudness})");

                // Short tracks: clipped to half each -> short-context; under the floor -> unavailable.
                using (var f = EspbFile.Open(BuildSyntheticFrames(dir, 40))) r = EdgeFold.Fold(f, 40, settings);
                assert(r.Head.Status == "short-context" && r.Tail.Status == "short-context" && r.Head.EndMs == 20000 && r.Tail.StartMs == 20000 && r.Head.MomentaryCentiDb.Length == 200,
                    $"a 40 s track: edges clipped to 20 s each, short-context, no overlap ({r.Head.Status} {r.Head.StartMs}-{r.Head.EndMs}; {r.Tail.StartMs}-{r.Tail.EndMs})");
                using (var f = EspbFile.Open(BuildSyntheticFrames(dir, 15))) r = EdgeFold.Fold(f, 15, settings);
                assert(r.Head.Status == "unavailable" && r.Tail.Status == "unavailable" && r.Head.MomentaryCentiDb.Length == 0 && r.HeadTail == null, $"a 15 s track is whole-track only: both edges unavailable ({r.Head.Status})");

                // Missing arrays: no beats -> bpm null, beats empty, edge still ok; no HPCP -> the edge FAILS its check (declared input missing), never a substitute.
                using (var f = EspbFile.Open(BuildSyntheticFrames(dir, 240, withBeats: false))) r = EdgeFold.Fold(f, 240, settings);
                assert(r.Head.Status == "ok" && r.Head.Bpm == null && r.Head.BeatsMs.Length == 0, $"no beats array: bpm null (sparse), edge still ok ({r.Head.Status})");
                using (var f = EspbFile.Open(BuildSyntheticFrames(dir, 240, withHpcp: false))) r = EdgeFold.Fold(f, 240, settings);
                assert(r.Head.Status == "failed" && r.Head.Error != null && r.Head.Error.Contains("tonal.hpcp"), $"a missing declared input fails the edge and names it ({r.Head.Status}: {r.Head.Error})");
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        // ---------------------------------------------------------------- ESPB reader
        static byte[] BuildEspb(string tag, params (string Name, EspbType Type, uint Flags, uint Count, uint Dims, byte[] Payload)[] descs)
        {
            using var ms = new System.IO.MemoryStream();
            using var w = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            w.Write(new[] { (byte)'E', (byte)'S', (byte)'P', (byte)'B' });
            w.Write(1u); w.Write(0x04030201u); w.Write((uint)descs.Length);
            long payloadOffsetPos = ms.Position; w.Write(0UL);
            var tagBytes = System.Text.Encoding.UTF8.GetBytes(tag); w.Write((uint)tagBytes.Length); w.Write(tagBytes);
            long tableBytes = 0; foreach (var d in descs) tableBytes += 4 + System.Text.Encoding.UTF8.GetByteCount(d.Name) + 4 + 4 + 4 + 4 + 8 + 8;
            long payloadOffset = ms.Position + tableBytes; long off = payloadOffset;
            foreach (var d in descs)
            {
                var nb = System.Text.Encoding.UTF8.GetBytes(d.Name); w.Write((uint)nb.Length); w.Write(nb);
                w.Write((uint)d.Type); w.Write(d.Flags); w.Write(d.Count); w.Write(d.Dims); w.Write((ulong)off); w.Write((ulong)d.Payload.Length);
                off += d.Payload.Length;
            }
            foreach (var d in descs) w.Write(d.Payload);
            w.Flush();
            ms.Position = payloadOffsetPos; w.Write((ulong)payloadOffset); w.Flush();
            return ms.ToArray();
        }
        static byte[] F32(params float[] v) { var b = new byte[v.Length * 4]; Buffer.BlockCopy(v, 0, b, 0, b.Length); return b; }
        static byte[] Str(params string[] s)
        {
            using var ms = new System.IO.MemoryStream(); using var w = new System.IO.BinaryWriter(ms);
            foreach (var x in s) { var b = System.Text.Encoding.UTF8.GetBytes(x); w.Write((uint)b.Length); w.Write(b); }
            w.Flush(); return ms.ToArray();
        }

        static void TestEspb(Action<bool, string> assert)
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), ".truedat-selftest", "espb-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                var path = System.IO.Path.Combine(dir, "frames.bin");
                var bytes = BuildEspb("output-x64.5",
                    ("lowlevel.loudness_ebu128.momentary", EspbType.F32Vector, 0u, 4u, 1u, F32(-20f, -19.5f, -19f, -18.5f)),
                    ("lowlevel.mfcc", EspbType.F32Matrix, 0u, 2u, 3u, F32(1f, 2f, 3f, 4f, 5f, 6f)),
                    ("metadata.tags.title", EspbType.String, 1u, 1u, 0u, Str("Lateralus")),
                    ("rhythm.beats_position", EspbType.F32Vector, 0u, 3u, 1u, F32(0.5f, 1.0f, 1.5f)),
                    ("tonal.chords_key", EspbType.StringVector, 0u, 2u, 0u, Str("A", "C")),
                    ("tonal.tuning_frequency", EspbType.F32Scalar, 1u, 1u, 1u, F32(440.1f)));
                System.IO.File.WriteAllBytes(path, bytes);
                using (var f = EspbFile.Open(path))
                {
                    assert(f.Tag == "output-x64.5" && f.Descriptors.Count == 6, $"header and table read: tag {f.Tag}, {f.Descriptors.Count} descriptors");
                    assert(f.TryGetF32("lowlevel.mfcc", out var mfcc, out var dims) && dims == 3 && mfcc.Length == 6 && mfcc[3] == 4f && mfcc[5] == 6f, "a matrix reads row-major with its dims");
                    assert(f.TryGetF32("lowlevel.loudness_ebu128.momentary", out var ebu, out var d1) && d1 == 1 && ebu.Length == 4 && ebu[0] == -20f && ebu[3] == -18.5f, "a per-frame scalar series reads as a vector");
                    assert(f.TryGetF32("tonal.tuning_frequency", out var tf, out _) && tf.Length == 1 && tf[0] == 440.1f && f.Descriptors["tonal.tuning_frequency"].Single, "a single scalar reads with the SINGLE flag");
                    assert(f.TryGetStrings("tonal.chords_key", out var ck) && ck.Length == 2 && ck[1] == "C" && f.TryGetStrings("metadata.tags.title", out var tt) && tt[0] == "Lateralus", "strings and string vectors read");
                    assert(!f.TryGetF32("lowlevel.nothing", out _, out _) && !f.TryGetF32("tonal.chords_key", out _, out _) && !f.TryGetStrings("lowlevel.mfcc", out _), "a missing name or a type mismatch is false, never a throw");
                }
                // Refusals: bad magic, wrong version, a table that does not end at payloadOffset.
                var bad = (byte[])bytes.Clone(); bad[0] = (byte)'X';
                bool refused = false; try { EspbFile.Open(WriteTemp(dir, bad)).Dispose(); } catch (InvalidOperationException) { refused = true; }
                assert(refused, "a bad magic is refused");
                var badVer = (byte[])bytes.Clone(); badVer[4] = 2;
                refused = false; try { EspbFile.Open(WriteTemp(dir, badVer)).Dispose(); } catch (InvalidOperationException) { refused = true; }
                assert(refused, "an unknown format version is refused");
                var badOff = (byte[])bytes.Clone(); badOff[16] ^= 0x10;
                refused = false; try { EspbFile.Open(WriteTemp(dir, badOff)).Dispose(); } catch (InvalidOperationException) { refused = true; }
                assert(refused, "a table that does not end exactly at payloadOffset is refused");

                // The build spike's real file, when this checkout has it (found upward from the exe; never an absolute path): a smoke read, not a contract.
                var real = FindUpward(AppContext.BaseDirectory, System.IO.Path.Combine("essentia-build", "spike-x5", "sine_curated.bin"));
                if (real != null && System.IO.File.Exists(real))
                    using (var f = EspbFile.Open(real))
                    {
                        bool ok = f.TryGetF32("lowlevel.mfcc", out var m, out var md);
                        assert(ok && f.Tag == "output-x64.5" && md == 13 && m.Length % 13 == 0 && m.Length / 13 > 1000,
                            $"the .5 spike's curated frames file reads: tag {f.Tag}, mfcc {(ok ? m.Length / Math.Max(1, md) : 0)} frames x {md}");
                    }

                // CALIBRATION (the C4 gate): over a real track's whole-run HPCP mean, the ported estimator must reproduce the
                // extractor's own tonal.key_<profile> for every profile the frames file carries. Runs on every full frames
                // file the build spike left under essentia-build/spike-x5/real (found upward; skipped when absent).
                var realDir = FindUpwardDir(AppContext.BaseDirectory, System.IO.Path.Combine("essentia-build", "spike-x5", "real"));
                if (realDir != null)
                {
                    int files = 0, agreed = 0, compared = 0; var mismatches = new System.Collections.Generic.List<string>();
                    foreach (var bin in System.IO.Directory.GetFiles(realDir, "*.bin"))
                    {
                        using var f = EspbFile.Open(bin);
                        if (!f.TryGetF32("tonal.hpcp", out var hp, out var hd) || hd != 36 || hp.Length < 36) continue;
                        files++;
                        int frames = hp.Length / 36; var mean = new double[36];
                        for (int i = 0; i < frames; i++) for (int b = 0; b < 36; b++) mean[b] += hp[i * 36 + b];
                        for (int b = 0; b < 36; b++) mean[b] /= frames;
                        foreach (var (profile, tag) in new[] { (KeyProfile.Edma, "edma"), (KeyProfile.Krumhansl, "krumhansl"), (KeyProfile.Temperley, "temperley") })
                        {
                            if (!f.TryGetStrings($"tonal.key_{tag}.key", out var k) || !f.TryGetStrings($"tonal.key_{tag}.scale", out var s)) continue;
                            var est = EdgeKey.Estimate(mean, profile);
                            compared++;
                            if (est != null && est.Key == k[0] && est.Scale == s[0]) agreed++;
                            else mismatches.Add($"{System.IO.Path.GetFileName(bin)}/{tag}: extractor {k[0]} {s[0]} vs port {est?.Key} {est?.Scale}");
                        }
                    }
                    assert(files == 0 || (compared > 0 && agreed == compared),
                        $"calibration: the port reproduces the extractor's key on {agreed}/{compared} profile votes over {files} real frames file(s)" + (mismatches.Count > 0 ? " — " + string.Join("; ", mismatches) : ""));
                }
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }
        static string WriteTemp(string dir, byte[] bytes) { var p = System.IO.Path.Combine(dir, Guid.NewGuid().ToString("N") + ".bin"); System.IO.File.WriteAllBytes(p, bytes); return p; }

        static string? FindUpwardDir(string start, string relative)
        {
            var dir = new System.IO.DirectoryInfo(start);
            for (int i = 0; dir != null && i < 8; i++, dir = dir.Parent)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, relative);
                if (System.IO.Directory.Exists(candidate)) return candidate;
            }
            return null;
        }

        /// <summary>Walks up from <paramref name="start"/> looking for <paramref name="relative"/>; null when no ancestor has it.</summary>
        static string? FindUpward(string start, string relative)
        {
            var dir = new System.IO.DirectoryInfo(start);
            for (int i = 0; dir != null && i < 8; i++, dir = dir.Parent)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, relative);
                if (System.IO.File.Exists(candidate)) return candidate;
            }
            return null;
        }

        // HPCP bin order is the extractor's: bin 0 = A. 36 bins = 3 per semitone.
        static double[] Chroma36(params int[] semitones)
        {
            var v = new double[36];
            foreach (var s in semitones) { v[s * 3] = 1.0; v[s * 3 + 1] = 0.6; v[((s * 3) + 35) % 36] = 0.6; }   // a peak with its neighbours
            return v;
        }
        const int A = 0, Bb = 1, B = 2, C = 3, Cs = 4, D = 5, Eb = 6, E = 7, F = 8, Fs = 9, G = 10, Ab = 11;

        static void TestEdgeKey(Action<bool, string> assert)
        {
            // A C-major triad decides C major on all three profiles.
            var cMajor = Chroma36(C, E, G);
            foreach (KeyProfile p in Enum.GetValues(typeof(KeyProfile)))
            {
                var est = EdgeKey.Estimate(cMajor, p);
                assert(est != null && est.Key == "C" && est.Scale == "major", $"{p}: C-E-G chroma -> C major ({est?.Key} {est?.Scale})");
                assert(est != null && est.Strength > 0.3 && est.Strength <= 1.0 && est.FirstToSecondRelativeStrength >= 0 && est.FirstToSecondRelativeStrength < 1,
                    $"{p}: strength is the winning correlation in (0.3, 1], first-to-second in [0, 1) ({est?.Strength:F3}, {est?.FirstToSecondRelativeStrength:F3})");
            }
            // Transposed two semitones up: D major. The key names are the extractor's (A, Bb, B, C, C#, D, Eb, E, F, F#, G, Ab).
            var dMajor = Chroma36(D, Fs, A);
            var dEst = EdgeKey.Estimate(dMajor, KeyProfile.Edma);
            assert(dEst != null && dEst.Key == "D" && dEst.Scale == "major", $"D-F#-A chroma -> D major ({dEst?.Key} {dEst?.Scale})");
            var ebEst = EdgeKey.Estimate(Chroma36(Eb, G, Bb), KeyProfile.Edma);
            assert(ebEst != null && ebEst.Key == "Eb", $"flats are spelled the extractor's way: Eb, not D# ({ebEst?.Key})");

            // Codex review 2026-10-20 #10: the streaming Key the extractor votes with first normalises the mean HPCP, gates bins under
            // 0.2 and rotates the peak onto a tempered bin (pcpThreshold 0.2, averageDetuningCorrection true — the defaults, which
            // MusicTonalDescriptors leaves alone). Without that, a C-E-G triad a third of a semitone flat read as B major.
            var detuned = new double[36]; detuned[8] = 1.0; detuned[20] = 0.7; detuned[29] = 0.8;
            var dt = EdgeKey.Estimate(detuned, KeyProfile.Edma);
            assert(dt != null && dt.Key == "C" && dt.Scale == "major", $"a C-E-G triad one bin flat (36-bin 8/20/29) -> C major after the extractor's detuning correction ({dt?.Key} {dt?.Scale})");
            var gated = Chroma36(C, E, G); for (int i = 0; i < 36; i++) if (gated[i] == 0) gated[i] = 0.15;   // a noise floor under the 0.2 gate
            var gt = EdgeKey.Estimate(gated, KeyProfile.Edma);
            var clean = EdgeKey.Estimate(Chroma36(C, E, G), KeyProfile.Edma);
            assert(gt != null && clean != null && gt.Key == "C" && gt.Scale == "major" && Math.Abs(gt.Strength - clean.Strength) < 1e-9,
                $"bins under the 0.2 gate are zeroed before the correlation: a noise floor of 0.15 changes nothing ({gt?.Strength:F6} vs {clean?.Strength:F6})");

            // A minor triad decides A minor.
            var aMinor = Chroma36(A, C, E);
            var amEst = EdgeKey.Estimate(aMinor, KeyProfile.Edma);
            assert(amEst != null && amEst.Key == "A" && amEst.Scale == "minor", $"A-C-E chroma -> A minor on EDMA ({amEst?.Key} {amEst?.Scale})");

            // The vote: EDMA's answer, Camelot code, agreement over the three profiles.
            var vote = EdgeKey.Vote(cMajor);
            assert(vote != null && vote.Name == "C" && vote.Scale == "major" && vote.Code == "8B" && vote.Agreement == 3 && vote.VotesPresent == 3,
                $"vote on C major: name C, Camelot 8B, 3/3 agree ({vote?.Name} {vote?.Scale} {vote?.Code} {vote?.Agreement}/{vote?.VotesPresent})");
            var voteAm = EdgeKey.Vote(aMinor);
            assert(voteAm != null && voteAm.Code == "8A" && voteAm.Agreement >= 1, $"vote on A minor: Camelot 8A ({voteAm?.Code}, agreement {voteAm?.Agreement})");

            // Too little context: a flat chroma (silence, noise) decides nothing — null, never a guess.
            var flat = new double[36]; for (int i = 0; i < 36; i++) flat[i] = 0.25;
            assert(EdgeKey.Estimate(flat, KeyProfile.Edma) == null, "a flat chroma yields no estimate (the correlation is undefined)");
            assert(EdgeKey.Vote(flat) == null, "a flat chroma yields no vote");
            assert(EdgeKey.Vote(new double[36]) == null, "an all-zero chroma yields no vote");
            assert(EdgeKey.Vote(cMajor, minStrength: 0.999) == null, "a vote below the minimum strength is declined (null), not reported weakly");

            // Size rule is the extractor's: any positive multiple of 12; the profiles are interpolated to it.
            var c12 = new double[12]; c12[C] = 1; c12[E] = 1; c12[G] = 1;
            var e12 = EdgeKey.Estimate(c12, KeyProfile.Krumhansl);
            assert(e12 != null && e12.Key == "C" && e12.Scale == "major", $"a 12-bin chroma is accepted ({e12?.Key} {e12?.Scale})");
            assert(EdgeKey.Estimate(new double[35], KeyProfile.Edma) == null && EdgeKey.Estimate(new double[0], KeyProfile.Edma) == null, "a size that is not a positive multiple of 12 yields null");

            // Camelot wheel, the hub's CamelotCode spelling: minor = A, major = B.
            assert(EdgeKey.Camelot("A", "minor") == "8A" && EdgeKey.Camelot("C", "major") == "8B", "Camelot: A minor 8A, C major 8B");
            assert(EdgeKey.Camelot("Ab", "minor") == "1A" && EdgeKey.Camelot("B", "major") == "1B", "Camelot: Ab minor 1A, B major 1B");
            assert(EdgeKey.Camelot("Eb", "minor") == "2A" && EdgeKey.Camelot("F#", "major") == "2B", "Camelot: Eb minor 2A, F# major 2B");
            assert(EdgeKey.Camelot("C#", "minor") == "12A" && EdgeKey.Camelot("E", "major") == "12B", "Camelot: C# minor 12A, E major 12B");
            assert(EdgeKey.Camelot("H", "major") == null && EdgeKey.Camelot("A", "majmin") == null, "an unknown key or scale has no Camelot code");
        }
    }
}
