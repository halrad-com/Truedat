using System;
using System.Collections.Generic;

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
            Console.WriteLine("Edges self-test (S2 C4: through the scan sink — stash by analysed path, tie at Record, write at the flush)");
            try { TestSink(assert); }
            catch (Exception ex) { assert(false, $"Sink: threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine("Edges self-test (S2 C4: the .5 extractor build is a known build)");
            try { TestExtractorBuild(assert); }
            catch (Exception ex) { assert(false, $"ExtractorBuild: threw {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>The committed output-x64.5 exe must resolve to its build name, not an MD5 stub: the store's run rows and
        /// the extractor report both go through this table, and the edges path requires the .5 build by name.</summary>
        static void TestExtractorBuild(Action<bool, string> assert)
        {
            var exe = FindUpward(AppContext.BaseDirectory, System.IO.Path.Combine("essentia-build", "output-x64.5", "essentia_streaming_extractor_music.exe"));
            if (exe == null) { Console.WriteLine("  SKIP  essentia-build/output-x64.5 exe not found from the test binary"); return; }
            var id = Program.ExtractorBuildId(exe);
            assert(id == "output-x64.5", $"the .5 exe resolves to output-x64.5 (got {id})");
            assert(string.Equals(Program.LatestExtractorMd5, "6f47bdfcaadfd4f510c5e1621c07945d", StringComparison.OrdinalIgnoreCase), $"the latest known extractor is .5 (got {Program.LatestExtractorMd5})");
        }

        // ---------------------------------------------------------------- the store rows
        const string Sha1 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string ShaKind = "content_hash:audio-stream-sha256";

        static EdgeFoldResult SyntheticFold(string dir, double duration = 240, bool withPcm = true)
        {
            EdgeFoldResult r;
            using (var f = EspbFile.Open(BuildSyntheticFrames(dir, duration))) r = EdgeFold.Fold(f, duration, new EdgeSettings());
            if (withPcm && r.Status == "ok")
            {
                EdgeFold.ApplyPcm(r.Head, new EdgePcmResult { CrestDb = 4.25, TransitionMs = 300, SilenceMs = 0, Slices = 300 });
                EdgeFold.ApplyPcm(r.Tail, new EdgePcmResult { CrestDb = 3.61, TransitionMs = 0, SilenceMs = 1100, Slices = 300 });
            }
            r.FoldMs = 7; r.PcmMs = 210;
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
                using (var tx = os.Db.BeginTransaction()) { values = EdgeStore.Write(dal, Sha1, ShaKind, null, r); tx.Complete(); }
                long segs = dal.CountSegments(Sha1, r.ProducerVersion), vrows = dal.CountValues(Sha1, EdgeStore.ProducerKey, r.ProducerVersion); long vecBytes = dal.SumVecBytes(Sha1, EdgeStore.ProducerKey, r.ProducerVersion);
                assert(segs == 2, $"2 segment rows: head and tail; the sub-windows ride inside the edge row ({segs})");
                assert(values == vrows && vrows == 5, $"one packed row per edge plus three whole-track rows: 5 value rows ({vrows} rows, {values} written)");
                assert(vecBytes >= 5000 && vecBytes <= 7500, $"the packed rows are the plan's payload: per edge EBU pair 1.2 KB + beats 0.4 KB + four summaries 1.4 KB -> ~6 KB per track ({vecBytes} bytes)");
                var back = EdgeStore.Read(dal, Sha1, r.ProducerVersion);
                assert(back != null && back.Status == "ok" && back.DurationMs == 240000 && back.ProducerVersion == r.ProducerVersion, $"read back: status, duration, version ({back?.Status}, {back?.DurationMs})");
                assert(back != null && back.Head.StartMs == 0 && back.Head.EndMs == 30000 && back.Tail.StartMs == 210000 && back.Tail.EndMs == 240000 && back.Head.Status == "ok", "read back: the edge windows");
                assert(back != null && back.Head.MomentaryCentiDb.Length == 300 && back.Head.MomentaryCentiDb[3] == -3827 && back.Head.MomentaryCentiDb[0] == short.MinValue && back.Tail.ShortTermCentiDb[299] == -9000,
                    $"read back: the EBU pair as int16 centi-dB, undefined bins intact ([3]={back?.Head.MomentaryCentiDb[3]}, [0]={back?.Head.MomentaryCentiDb[0]})");
                assert(back != null && back.Head.BeatsMs.Length == 60 && back.Head.BeatsMs[0] == 250 && back.Head.BeatsLoudness.Length == 60 && Math.Abs(back.Head.BeatsLoudness[0] - 0.3) < 1e-6, "read back: beats ms and loudness");
                assert(back != null && back.Head.Bpm != null && Math.Abs(back.Head.Bpm.Value - 120) < 0.01 && back.Head.Bpm.Beats == 60, "read back: bpm");
                assert(back != null && back.Head.Whole?.Key != null && back.Head.Whole.Key.Name == "C" && back.Head.Whole.Key.Scale == "major" && back.Head.Whole.Key.Code == "8B" && back.Head.Whole.Key.Agreement == r.Head.Whole!.Key!.Agreement,
                    $"read back: the key vote ({back?.Head.Whole?.Key?.Name} {back?.Head.Whole?.Key?.Scale} {back?.Head.Whole?.Key?.Code})");
                assert(back != null && back.Head.Sub.Length == 3 && back.Head.Sub[2].StartMs == 20000 && back.Head.Sub[1].Key?.Name == "C" && Math.Abs(back.Head.Sub[1].MfccMean[12] - (-380)) < 1e-3,
                    "read back: three sub-windows with their own summaries");
                assert(back != null && Math.Abs(back.Head.Whole.HpcpMean[9] - r.Head.Whole.HpcpMean[9]) < 1e-6 && Math.Abs(back.Head.Whole.SpectralMean[10] - 1100) < 1e-3 && back.Head.Whole.MfccSpread[0] == 0,
                    "read back: hpcp mean, spectral means, spreads");
                // Scalars are stored as float32: equal to ~1e-6 relative, so compare with a tolerance, never ==.
                assert(back != null && back.Tail.Loudness != null && back.Tail.Loudness.SilenceMs == 1100 && back.Tail.Loudness.SilenceSource == "pcm" && back.Tail.Loudness.FadeMs == r.Tail.Loudness!.FadeMs
                    && Math.Abs(back.Tail.Loudness.MeanLufs!.Value - r.Tail.Loudness.MeanLufs!.Value) < 1e-4 && back.Tail.Rms != null && Math.Abs(back.Tail.Rms.CrestDb!.Value - 3.61) < 1e-5,
                    $"read back: loudness scalars, silence source, rms ({back?.Tail.Loudness?.SilenceMs} {back?.Tail.Loudness?.SilenceSource} fade {back?.Tail.Loudness?.FadeMs} vs {r.Tail.Loudness!.FadeMs}; mean {back?.Tail.Loudness?.MeanLufs} vs {r.Tail.Loudness.MeanLufs}; crest {back?.Tail.Rms?.CrestDb})");
                assert(back != null && back.HeadTail != null && Math.Abs(back.HeadTail.Harmony!.Value - r.HeadTail!.Harmony!.Value) < 1e-5 && back.Head.Divergence != null && Math.Abs(back.Head.Divergence.Texture!.Value - r.Head.Divergence!.Texture!.Value) < 1e-5,
                    "read back: divergences");

                // Idempotent: the same version rewrites itself; a different version coexists.
                using (var tx = os.Db.BeginTransaction()) { EdgeStore.Write(dal, Sha1, ShaKind, null, r); tx.Complete(); }
                assert(dal.CountSegments(Sha1, r.ProducerVersion) == 2 && dal.CountValues(Sha1, EdgeStore.ProducerKey, r.ProducerVersion) == vrows, "a second write of the same version adds no rows");
                var r2 = SyntheticFold(dir); r2.ProducerVersion = "edges@v1;e20;p10";
                using (var tx = os.Db.BeginTransaction()) { EdgeStore.Write(dal, Sha1, ShaKind, null, r2); tx.Complete(); }
                assert(dal.CountSegments(Sha1, r2.ProducerVersion) == 2 && dal.CountSegments(Sha1, r.ProducerVersion) == 2 && EdgeStore.Read(dal, Sha1, r.ProducerVersion)?.Head.MomentaryCentiDb.Length == 300,
                    "a different producer version coexists: its own 2 segments, the first version's rows untouched");
                assert(EdgeStore.Read(dal, Sha1, "edges@v9;e30;p10") == null, "an unknown version reads as null");

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
                long before, after, vecBytes;
                using (var os = Truedat.Store.OpenStore.Open(dbPath, true, VersionInfo.Display, now))
                {
                    os.Db.Exec("VACUUM;"); before = LogicalBytes(os.Db);
                    var r = SyntheticFold(dir2);
                    using (var tx = os.Db.BeginTransaction())
                    {
                        for (int i = 0; i < 20; i++) EdgeStore.Write(os.Dal, i.ToString("x2") + Sha1.Substring(2), ShaKind, null, r);
                        tx.Complete();
                    }
                    vecBytes = os.Dal.SumVecBytes("00" + Sha1.Substring(2), EdgeStore.ProducerKey, r.ProducerVersion);
                    os.Dal.ResetAll(); os.Db.Exec("VACUUM;"); after = LogicalBytes(os.Db);
                }
                long perTrack = (after - before) / 20;
                Console.WriteLine($"        store bytes per track with edges: {perTrack:N0} (20 tracks, vacuumed, page_count x page_size; vector payload {vecBytes:N0}/track)");
                assert(perTrack > 0 && perTrack <= 10000, $"the ruled row costs under 10 KB/track in the store, payload included ({perTrack:N0} bytes/track; plan ~6,600; per-field rows measured 43,417)");
            }
            finally { try { System.IO.Directory.Delete(dir2, true); } catch { } }
        }

        static long LogicalBytes(Truedat.Store.SqliteDb db)
        {
            long pages, size;
            using (var st = db.Prepare("PRAGMA page_count;")) { st.Step(); pages = st.GetInt64(0); }
            using (var st = db.Prepare("PRAGMA page_size;")) { st.Step(); size = st.GetInt64(0); }
            return pages * size;
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
                var r = Truedat.Store.ScanSink.Flush(store, "output-x64.5", entries.Keys);
                assert(r.Ok && r.Edges == 1 && r.EdgesFailed == 1, $"the flush reports 1 edge row written and 1 failed fold ({r.Edges}, {r.EdgesFailed}, ok {r.Ok} {r.Error})");
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
        static string BuildSyntheticFrames(string dir, double durationSec = 240, bool withBeats = true, bool withHpcp = true)
        {
            int ll = (int)Math.Round(durationSec / EdgeFold.LowLevelHop), tn = (int)Math.Round(durationSec / EdgeFold.TonalHop), eb = (int)Math.Round(durationSec / EdgeFold.EbuHop);
            // EBU series as the extractor emits them: trailing windows every 0.1 s, so momentary has eb-3 frames (400 ms window) and
            // short-term eb-29 (3 s window); frame i's value is the level at the time its window ENDS. (A 60 s track: 597 / 571.)
            Func<double, double> lufsAt = t =>
            {
                if (t < 6) return -40 + (26.0 * t / 6.0);                       // fade-in: -40 -> -14 over 6 s
                if (t >= durationSec - 1) return -90;                           // trailing digital silence (1 s)
                if (t >= durationSec - 5) return -14 - 30.0 * (t - (durationSec - 5)) / 4.0;   // fade-out 4 s: -14 -> -44
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
                assert(settings.ProducerVersion == "edges@v1;e30;p10", $"the producer version carries the settings ({settings.ProducerVersion})");

                EdgeFoldResult r;
                using (var f = EspbFile.Open(BuildSyntheticFrames(dir))) r = EdgeFold.Fold(f, 240, settings);
                var h = r.Head; var t = r.Tail;
                assert(h.Status == "ok" && t.Status == "ok" && h.StartMs == 0 && h.EndMs == 30000 && t.StartMs == 210000 && t.EndMs == 240000, $"a 240 s track has two full 30 s edges, status ok ({h.Status} {h.StartMs}-{h.EndMs}; {t.Status} {t.StartMs}-{t.EndMs})");
                assert(h.MomentaryCentiDb.Length == 300 && h.ShortTermCentiDb.Length == 300 && h.MomentaryCentiDb[299] == -1400 && h.MomentaryCentiDb[3] == -3827
                    && h.MomentaryCentiDb[0] == short.MinValue && h.MomentaryCentiDb[2] == short.MinValue && h.ShortTermCentiDb[28] == short.MinValue && h.ShortTermCentiDb[29] != short.MinValue,
                    $"the EBU pair is stored at 10/s as int16 centi-dB: 300 values; bins before the first full window are undefined (3 momentary, 29 short-term), then -3827 at 0.4 s, -1400 at 30 s ({h.MomentaryCentiDb.Length}, [0]={h.MomentaryCentiDb[0]}, [3]={h.MomentaryCentiDb[3]}, [299]={h.MomentaryCentiDb[299]}, st[28]={h.ShortTermCentiDb[28]}, st[29]={h.ShortTermCentiDb[29]})");
                assert(t.MomentaryCentiDb.Length == 300 && t.MomentaryCentiDb[299] == -9000 && t.ShortTermCentiDb[299] == -9000 && t.MomentaryCentiDb[0] == -1400 && CountUndefined(t.MomentaryCentiDb) == 0 && CountUndefined(t.ShortTermCentiDb) == 0,
                    $"the tail's series are fully defined and end on the window that ends at the track end: -1400 first, -9000 last ([0]={t.MomentaryCentiDb[0]}, [299]={t.MomentaryCentiDb[299]}, undefined {CountUndefined(t.MomentaryCentiDb)}/{CountUndefined(t.ShortTermCentiDb)})");
                assert(h.BeatsMs.Length == 60 && h.BeatsMs[0] == 250 && h.BeatsMs[59] == 29750 && h.BeatsLoudness.Length == 60 && t.BeatsMs.Length == 60 && t.BeatsMs[0] == 210250,
                    $"beats inside each window, as ms from the track start ({h.BeatsMs.Length} head, first {(h.BeatsMs.Length > 0 ? h.BeatsMs[0] : -1)}; {t.BeatsMs.Length} tail)");
                assert(h.Bpm != null && Math.Abs(h.Bpm.Value - 120) < 0.01 && h.Bpm.Beats == 60 && h.Bpm.Regularity > 0.99, $"bpm from in-window intervals: 120, 60 beats, regularity ~1 ({h.Bpm?.Value:F2}, {h.Bpm?.Beats}, {h.Bpm?.Regularity:F3})");
                assert(h.Whole != null && h.Whole.Key != null && h.Whole.Key.Name == "C" && h.Whole.Key.Scale == "major" && h.Whole.Key.Code == "8B", $"head key from the window's HPCP mean: C major 8B ({h.Whole?.Key?.Name} {h.Whole?.Key?.Scale})");
                assert(t.Whole != null && t.Whole.Key != null && t.Whole.Key.Name == "A" && t.Whole.Key.Scale == "minor", $"tail key: A minor ({t.Whole?.Key?.Name} {t.Whole?.Key?.Scale})");
                assert(h.Sub.Length == 3 && h.Sub[0].StartMs == 0 && h.Sub[0].EndMs == 10000 && h.Sub[2].StartMs == 20000 && h.Sub[2].EndMs == 30000 && h.Sub[1].Key != null && h.Sub[1].Key.Name == "C", "three 10 s sub-windows, each with its own summary and key");
                assert(h.Whole.MfccMean.Length == 13 && Math.Abs(h.Whole.MfccMean[0] - (-500)) < 1e-3 && Math.Abs(h.Whole.MfccMean[12] - (-380)) < 1e-3 && h.Whole.MfccSpread[0] < 1e-6,
                    $"MFCC mean and spread over the window ({h.Whole.MfccMean[0]:F1}, spread {h.Whole.MfccSpread[0]:E1})");
                assert(h.Whole.SpectralMean.Length == 11 && Math.Abs(h.Whole.SpectralMean[0] - 100) < 1e-3 && Math.Abs(t.Whole!.SpectralMean[10] - 2200) < 1e-3 && h.Whole.SpectralSpread[3] < 1e-6,
                    $"the 11 spectral scalars in the declared order, mean and spread ({h.Whole.SpectralMean[0]:F1}, {t.Whole!.SpectralMean[10]:F1})");
                // Fade and silence are quantised to the 0.1 s window grid and the fade is "departure from the plateau by 1 LU", so a
                // 6 s linear ramp reads 5,800 ms (it reaches -15 at 5.77 s) and the 4 s ramp 3,900 ms; the digital silence from
                // 239 s is the 11 windows ending in [239, 240].
                assert(h.Loudness != null && h.Loudness.StartLufs < -35 && Math.Abs(h.Loudness.EndLufs!.Value - (-14)) < 0.01 && h.Loudness.FadeMs.HasValue && Math.Abs(h.Loudness.FadeMs.Value - 6000) <= 300 && h.Loudness.SilenceMs == 0 && h.Loudness.SilenceSource == "ebu",
                    $"head loudness: start ~-36, end -14, a ~6 s fade-in, no silence, source ebu ({h.Loudness?.StartLufs:F1}, {h.Loudness?.EndLufs:F1}, fade {h.Loudness?.FadeMs}, silence {h.Loudness?.SilenceMs})");
                assert(t.Loudness != null && Math.Abs(t.Loudness.StartLufs!.Value - (-14)) < 0.01 && t.Loudness.EndLufs < -80 && t.Loudness.FadeMs.HasValue && Math.Abs(t.Loudness.FadeMs.Value - 4000) <= 300 && t.Loudness.SilenceMs >= 1000 && t.Loudness.SilenceMs <= 1100,
                    $"tail loudness: start -14, end silent, a ~4 s fade-out, ~1,000 ms of trailing silence ({t.Loudness?.StartLufs:F1}, {t.Loudness?.EndLufs:F1}, fade {t.Loudness?.FadeMs}, silence {t.Loudness?.SilenceMs})");
                assert(h.Divergence != null && h.Divergence.Loudness.HasValue && h.Divergence.Texture.HasValue && h.Divergence.Harmony.HasValue
                    && r.HeadTail != null && r.HeadTail.Harmony > 0.3 && r.HeadTail.Texture > 0 && r.HeadTail.Loudness < 0.5,
                    $"divergence per edge vs the whole and head vs tail (head-tail harmony {r.HeadTail?.Harmony:F2}, texture {r.HeadTail?.Texture:F2}, loudness {r.HeadTail?.Loudness:F2})");
                assert(r.DurationMs == 240000 && r.ProducerVersion == "edges@v1;e30;p10", "the result names the duration and the producer version");

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
