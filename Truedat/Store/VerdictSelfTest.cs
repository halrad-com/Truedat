using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Truedat.Store
{
    /// <summary>Self-tests for the store's verdict projection (store-v3 T19, schema v4): keyed per LOCATION (two copies of one audio keep
    /// distinct verdicts), a row iff the catalog writes the truedat block, export parity by construction, both algorithm tags stored, and
    /// re-derivation when either tag changes.</summary>
    internal static class VerdictSelfTest
    {
        internal static void Run(Action<bool, string> assert)
        {
            Console.WriteLine("Verdict self-test (store-v3 T19: per-location verdicts, row iff block, export parity, both tags, re-derive on a tag change)");
            try { TestVerdicts(assert); }
            catch (Exception ex) { assert(false, $"Verdict: threw {ex.GetType().Name}: {ex.Message}"); }
            finally { VerdictStore.MethodOverrideForTest = null; VerdictStore.SpeechMethodOverrideForTest = null; }
        }

        const string Sha = "5555555555555555555555555555555555555555555555555555555555555555";
        internal const string PathA = @"D:\music\verdict\a-lavc.mp3", PathB = @"D:\music\verdict\b-lame.mp3", PathC = @"D:\music\verdict\c-128k.mp3";

        /// <summary>Three copies of ONE audio (same audioStreamSha256): A an mp3 whose copy facts say transcode (Lavc encoder, 16 kHz lowpass, no
        /// LAME tag), B an mp3 whose copy facts say original (LAME 3.100, 20 kHz lowpass), C a 128 kbps mp3 (below the transcode gate) that
        /// decides no axis. All three are mp3 so their content hashes share one facet kind and resolve to ONE media (a FLAC's frame-anchored
        /// hash is a different kind).</summary>
        internal static Dictionary<string, TrackEntry> Fixture()
        {
            TrackEntry E(string codec, int bitrate, string encoder, int lowpass, string? lame, int bitDepth, int sampleRate) => new TrackEntry
            {
                Features = new TrackFeatures { Artist = "A", Title = "T", Bpm = 120, Key = "C", Mode = "major", Mfcc = new double[13] },
                LastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), AudioStreamSha256 = Sha,
                FingerprintV1 = new Program.FingerprintV1 { Codec = codec, Bitrate = bitrate, Encoder = encoder, Mp3LowpassHz = lowpass, Mp3LameVersion = lame, BitDepth = bitDepth, SampleRate = sampleRate, Channels = 2, DurationMs = 240000, FileSize = 1000 },
            };
            return new Dictionary<string, TrackEntry>(PathComparer.Instance)
            {
                [PathA] = E("mp3", 320, "Lavc58.91", 16000, null, 0, 44100),
                [PathB] = E("mp3", 320, "LAME3.100", 20000, "LAME3.100", 0, 44100),
                [PathC] = E("mp3", 128, "LAME3.100", 16000, "LAME3.100", 0, 44100),
            };
        }

        sealed class Row { public string Hires = "", Lossy = "", Speech = "", Method = "", SpeechMethod = "", Prov = "", Content = ""; public double? HiresConf, LossyConf, SpeechConf; }

        static Dictionary<string, Row> ReadRows(OpenStore os)
        {
            var d = new Dictionary<string, Row>(PathComparer.Instance);
            using var st = os.Db.Prepare("SELECT l.display_path, v.hires_genuine, v.hires_confidence, v.lossy_transcode_likely, v.lossy_confidence, v.speech_likely, v.speech_confidence, v.method, v.speech_method, v.prov, v.content " +
                                         "FROM verdict v JOIN track_location l ON l.id = v.location_id;");
            double? D(int c) => st.IsNull(c) ? (double?)null : st.GetDouble(c);
            while (st.Step())
                d[st.GetString(0)!] = new Row { Hires = st.GetString(1)!, HiresConf = D(2), Lossy = st.GetString(3)!, LossyConf = D(4), Speech = st.GetString(5)!, SpeechConf = D(6), Method = st.GetString(7)!, SpeechMethod = st.GetString(8)!, Prov = st.GetString(9) ?? "", Content = st.GetString(10) ?? "" };
            return d;
        }

        static string Str(JsonElement o, string n) => o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        static double? Num(JsonElement o, string n) => o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : (double?)null;

        static void TestVerdicts(Action<bool, string> assert)
        {
            var dir = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "verdict-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                var store = Path.Combine(dir, "truedat.db");
                ImportCommand.ExecuteEntries(store, Fixture(), @"D:\music", "primary", true, "import", "1", "fixture", null, null);

                Dictionary<string, Row> rows; long entities, locations;
                using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
                {
                    rows = ReadRows(os);
                    entities = os.Dal.Count("file_entity"); locations = os.Dal.Count("track_location");
                }
                assert(entities == 1 && locations == 3, $"fixture: ONE media, three copies ({entities} entities, {locations} locations)");

                // Per location: A and B are the same audio with different copy facts — distinct verdicts.
                rows.TryGetValue(PathA, out var a); rows.TryGetValue(PathB, out var b);
                assert(a != null && b != null && a.Lossy == "yes" && a.Prov == "tc" && b.Lossy == "no" && b.Prov == "",
                    $"per-location verdicts: same audio, copy A transcode yes/tc, copy B transcode no (A {a?.Lossy}/{a?.Prov}, B {b?.Lossy}/{b?.Prov})");
                assert(a != null && a.Method == VerdictStore.BuildMethod && a.SpeechMethod == VerdictStore.BuildSpeechMethod, $"both algorithm tags stored on the row ({a?.Method} / {a?.SpeechMethod})");

                // Export parity: for EVERY exported path, the catalog's truedat block == the verdict row; absent block == absent row.
                var outPath = Path.Combine(dir, "out", "mbxmoods.json"); Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                ExportCommand.Execute(new ExportOptions { StorePath = store, OutPath = outPath });
                int compared = 0, mismatches = 0, absentBoth = 0; string? firstMismatch = null;
                using (var doc = JsonDocument.Parse(File.ReadAllBytes(outPath)))
                {
                    foreach (var t in doc.RootElement.GetProperty("tracks").EnumerateObject())
                    {
                        compared++;
                        bool hasBlock = t.Value.TryGetProperty("truedat", out var blk);
                        bool hasRow = rows.TryGetValue(t.Name, out var r);
                        if (!hasBlock && !hasRow) { absentBoth++; continue; }
                        bool same = hasBlock && hasRow
                            && Str(blk, "hiresGenuine") == r!.Hires && Num(blk, "hiresConfidence") == r.HiresConf
                            && Str(blk, "lossyTranscodeLikely") == r.Lossy && Num(blk, "lossyTranscodeConfidence") == r.LossyConf
                            && Str(blk, "speechLikely") == r.Speech && Num(blk, "speechConfidence") == r.SpeechConf
                            && Str(blk, "method") == r.Method && Str(blk, "speechMethod") == r.SpeechMethod && Str(blk, "prov") == r.Prov && Str(blk, "content") == r.Content;
                        if (!same) { mismatches++; firstMismatch ??= $"{t.Name} (block {hasBlock}, row {hasRow})"; }
                    }
                }
                assert(compared == 3 && mismatches == 0, $"export parity: every exported truedat block equals its verdict row, absent == absent ({compared} paths, {mismatches} mismatches, first {firstMismatch})");
                assert(absentBoth >= 1 && !rows.ContainsKey(PathC), $"row iff block: the copy that decides no axis has no block AND no row ({absentBoth} absent-both; C row {rows.ContainsKey(PathC)})");

                // A content change re-derives EVERY copy: remeasure via copy A only (a different producer replaces the description) — B's row follows.
                using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
                {
                    long before = os.Dal.ScalarLong("SELECT count(*) FROM verdict;");
                    os.Db.Exec("UPDATE verdict SET updated_utc = 'stale';");
                    using (var tx = os.Db.BeginTransaction())
                    {
                        var ent = os.Dal.ScalarLong("SELECT count(*) FROM file_entity;");
                        var entityId = os.Db.ExecScalar("SELECT id FROM file_entity;")!;
                        VerdictStore.DeriveForEntity(os.Dal, entityId, "fresh");
                        tx.Complete();
                    }
                    assert(os.Dal.ScalarLong("SELECT count(*) FROM verdict WHERE updated_utc = 'fresh';") == before && before == 2,
                        $"DeriveForEntity re-derives every copy of the media ({os.Dal.ScalarLong("SELECT count(*) FROM verdict WHERE updated_utc = 'fresh';")} of {before})");
                }

                // Re-derive at open when EITHER tag differs from the build — first the hires/transcode method, then the speech method.
                VerdictStore.MethodOverrideForTest = "test-method-2026-10-16";
                using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
                {
                    var r2 = ReadRows(os);
                    assert(r2.Count == 2 && r2[PathA].Method == "test-method-2026-10-16" && os.Dal.MetaGet(VerdictStore.MethodKey) == "test-method-2026-10-16" && r2[PathA].Lossy == "yes",
                        $"a method-tag change re-derives every row at open, from the stored rows ({r2.Count} rows, method {(r2.ContainsKey(PathA) ? r2[PathA].Method : "-")}, meta {os.Dal.MetaGet(VerdictStore.MethodKey)})");
                    assert(!VerdictStore.EnsureCurrent(os.Db, os.Dal, "x"), "once current, a second check re-derives nothing");
                }
                VerdictStore.SpeechMethodOverrideForTest = "test-speech-2026-10-16";
                using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
                {
                    var r3 = ReadRows(os);
                    assert(r3.Count == 2 && r3[PathB].SpeechMethod == "test-speech-2026-10-16" && os.Dal.MetaGet(VerdictStore.SpeechMethodKey) == "test-speech-2026-10-16",
                        $"a speech-tag change alone also re-derives every row ({(r3.ContainsKey(PathB) ? r3[PathB].SpeechMethod : "-")})");
                }
                VerdictStore.MethodOverrideForTest = null; VerdictStore.SpeechMethodOverrideForTest = null;
                using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
                    assert(ReadRows(os)[PathA].Method == VerdictStore.BuildMethod && os.Dal.MetaGet(VerdictStore.MethodKey) == VerdictStore.BuildMethod, "back to the build's tags: re-derived again, the build's method on the rows");
            }
            finally { VerdictStore.MethodOverrideForTest = null; VerdictStore.SpeechMethodOverrideForTest = null; try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
