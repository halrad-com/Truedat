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

        // ---------------------------------------------------------------- the fold
        /// <summary>A synthetic 240 s track: a 6 s fade-in from silence then steady -14 LUFS; a 4 s fade-out to silence at the end
        /// with 1 s of digital silence after it; beats every 0.5 s (120 bpm) throughout; C-major chroma in the head, A-minor in
        /// the tail; MFCC and spectral scalars constant per half so spreads are 0 and means are known.</summary>
        static string BuildSyntheticFrames(string dir, double durationSec = 240, bool withBeats = true, bool withHpcp = true)
        {
            int ll = (int)Math.Round(durationSec / EdgeFold.LowLevelHop), tn = (int)Math.Round(durationSec / EdgeFold.TonalHop), eb = (int)Math.Round(durationSec / EdgeFold.EbuHop);
            var momentary = new float[eb]; var shortTerm = new float[eb];
            for (int i = 0; i < eb; i++)
            {
                double t = i * EdgeFold.EbuHop;
                double lufs = -14;
                if (t < 6) lufs = -40 + (26.0 * t / 6.0);                   // fade-in: -40 -> -14 over 6 s
                else if (t >= durationSec - 1) lufs = -90;                     // trailing digital silence (1 s)
                else if (t >= durationSec - 5) lufs = -14 - 30.0 * (t - (durationSec - 5)) / 4.0;   // fade-out 4 s: -14 -> -44
                momentary[i] = (float)lufs; shortTerm[i] = (float)lufs;
            }
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
                ("lowlevel.loudness_ebu128.momentary", EspbType.F32Vector, 0u, (uint)eb, 1u, F32(momentary)),
                ("lowlevel.loudness_ebu128.short_term", EspbType.F32Vector, 0u, (uint)eb, 1u, F32(shortTerm)),
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
                assert(h.MomentaryCentiDb.Length == 300 && h.ShortTermCentiDb.Length == 300 && h.MomentaryCentiDb[299] == -1400 && h.MomentaryCentiDb[0] == -4000,
                    $"the EBU pair is stored at 10/s as int16 centi-dB: 300 values, first -4000, last -1400 ({h.MomentaryCentiDb.Length}, {h.MomentaryCentiDb[0]}, {h.MomentaryCentiDb[h.MomentaryCentiDb.Length - 1]})");
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
                assert(h.Loudness != null && h.Loudness.StartLufs < -35 && Math.Abs(h.Loudness.EndLufs!.Value - (-14)) < 0.01 && h.Loudness.FadeMs == 6000 && h.Loudness.SilenceMs == 0 && h.Loudness.SilenceSource == "ebu",
                    $"head loudness: start ~-39, end -14, a 6 s fade-in, no silence, source ebu ({h.Loudness?.StartLufs:F1}, {h.Loudness?.EndLufs:F1}, fade {h.Loudness?.FadeMs}, silence {h.Loudness?.SilenceMs})");
                assert(t.Loudness != null && Math.Abs(t.Loudness.StartLufs!.Value - (-14)) < 0.01 && t.Loudness.EndLufs < -80 && t.Loudness.FadeMs == 4000 && t.Loudness.SilenceMs == 1000,
                    $"tail loudness: start -14, end silent, a 4 s fade-out, 1,000 ms of trailing silence ({t.Loudness?.StartLufs:F1}, {t.Loudness?.EndLufs:F1}, fade {t.Loudness?.FadeMs}, silence {t.Loudness?.SilenceMs})");
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
