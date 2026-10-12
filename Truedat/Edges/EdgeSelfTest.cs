using System;

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
