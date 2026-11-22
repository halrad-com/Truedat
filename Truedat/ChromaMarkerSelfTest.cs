using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Truedat
{
    /// <summary>
    /// <c>chromaFold</c>: which way a track's 12 chroma values (<c>hpcp12</c>, <c>thpcp12</c>) were
    /// grouped from the extractor's 36.
    ///
    /// The grouping was corrected once. A track analyzed before that holds values grouped the old
    /// way, one analyzed after holds the corrected ones, and nothing in an entry said which — so a
    /// library scanned across both could not be told apart and no scan had a reason to redo the
    /// older tracks. The marker says which, per track; <c>--refresh-chroma</c> re-analyzes the tracks
    /// that do not carry the current one. It is written for every analyzed track, chroma or not, so
    /// a silent track is not re-analyzed forever.
    /// </summary>
    internal static class ChromaMarkerSelfTest
    {
        internal static void Run(Action<bool, string> assert)
        {
            Console.WriteLine("Chroma marker: each track says how its chroma was grouped; --refresh-chroma redoes the rest");
            TestSurfaces(assert);
            TestGate(assert);
            TestStore(assert);
            TestCli(assert);
        }

        static TrackEntry Entry(int? fold) => new TrackEntry
        {
            Features = new TrackFeatures
            {
                Artist = "Fixture", Title = "t", Bpm = 120, Mfcc = new[] { 1.5, -2.25 }, DynamicRange = 8, LoudnessMomentary = -14,
                Hpcp12 = new[] { 1.0, 0, 0, 0, 0.5, 0, 0, 0.25, 0, 0, 0, 0 }, ChromaFold = fold,
            },
            AudioStreamSha256 = new string(fold.HasValue ? 'a' : 'b', 64), AudioStreamSha256Source = "flac-frames",
            LastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        static void TestSurfaces(Action<bool, string> assert)
        {
            assert(Program.ChromaFoldCurrent == 2, $"chroma-marker: the corrected grouping is fold 2 ({Program.ChromaFoldCurrent})");

            var parsed = Program.ParseEssentiaOutput("{}");
            assert(parsed != null && parsed.ChromaFold == Program.ChromaFoldCurrent,
                $"chroma-marker: every analysis carries the marker, even one with no chroma at all ({parsed?.ChromaFold?.ToString() ?? "none"})");

            var root = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "chroma-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                string marked = Path.Combine(root, "marked.flac"), unmarked = Path.Combine(root, "unmarked.flac");
                var catalog = Path.Combine(root, Program.MoodsFileName);
                Program.SaveResultsForStore(catalog, new ConcurrentDictionary<string, TrackEntry>(new[]
                {
                    new KeyValuePair<string, TrackEntry>(marked, Entry(Program.ChromaFoldCurrent)),
                    new KeyValuePair<string, TrackEntry>(unmarked, Entry(null)),
                }, PathComparer.Instance));
                string text = File.ReadAllText(catalog);
                int written = text.Split(new[] { "\"chromaFold\"" }, StringSplitOptions.None).Length - 1;
                assert(written == 1, $"chroma-marker: the catalog writes the marker for the track that has one and nothing for the one that does not ({written} written)");

                var back = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
                Program.LoadExistingMoodsForStore(catalog, back);
                assert(back.TryGetValue(marked, out var m) && m.Features.ChromaFold == Program.ChromaFoldCurrent, "chroma-marker: the catalog reads the marker back");
                assert(back.TryGetValue(unmarked, out var u) && u.Features.ChromaFold == null, "chroma-marker: a track written without one reads back without one, not as zero");

                var listed = new ITunesTrack { TrackId = 1, Artist = "A", Name = "N", Album = "", Genre = "", Location = marked };
                var hit = Program.RebuildCacheEntry(Entry(Program.ChromaFoldCurrent), listed, DateTime.UtcNow, null, null);
                assert(hit.Features.ChromaFold == Program.ChromaFoldCurrent, "chroma-marker: a cache hit keeps the marker with the values it describes");
                var hitOld = Program.RebuildCacheEntry(Entry(null), listed, DateTime.UtcNow, null, null);
                assert(hitOld.Features.ChromaFold == null, "chroma-marker: and a cache hit on an unmarked track does not gain one");
            }
            catch (Exception ex) { assert(false, "chroma-marker: threw " + ex.GetType().Name + ": " + ex.Message); }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        static void TestGate(Action<bool, string> assert)
        {
            bool saved = Program._refreshChroma;
            try
            {
                Program._refreshChroma = false;
                assert(Program.HasCurrentFeatures(Entry(null).Features), "chroma-gate: without --refresh-chroma an unmarked track is a cache hit, as before");
                Program._refreshChroma = true;
                assert(!Program.HasCurrentFeatures(Entry(null).Features), "chroma-gate: under --refresh-chroma an unmarked track is re-analyzed");
                assert(!Program.HasCurrentFeatures(Entry(1).Features), "chroma-gate: so is one marked with an older grouping");
                assert(Program.HasCurrentFeatures(Entry(Program.ChromaFoldCurrent).Features), "chroma-gate: one carrying the current marker is left alone, so a second pass does nothing");
            }
            finally { Program._refreshChroma = saved; }
        }

        static void TestStore(Action<bool, string> assert)
        {
            var root = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "chroma-store-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                using (global::Truedat.Store.StoreLog.Quiet())
                {
                    int schemaNow = global::Truedat.Store.StoreSchema.CurrentVersion;
                    assert(schemaNow == 5, $"chroma-store: the store's schema is version 5 ({schemaNow})");
                    assert(global::Truedat.Store.TrackEntryMapper.ContentColumnNames.Contains("chromaFold"), "chroma-store: the marker is a column of the audio description");

                    // through a store and out again
                    string music = Path.Combine(root, "music"); Directory.CreateDirectory(music);
                    string marked = Path.Combine(music, "marked.flac"), unmarked = Path.Combine(music, "unmarked.flac");
                    var store = Path.Combine(root, "lib", "truedat.db"); Directory.CreateDirectory(Path.Combine(root, "lib"));
                    var catalog = Path.Combine(root, "lib", Program.MoodsFileName);
                    global::Truedat.Store.ImportCommand.ExecuteEntries(store, new Dictionary<string, TrackEntry>(PathComparer.Instance)
                    {
                        [marked] = Entry(Program.ChromaFoldCurrent), [unmarked] = Entry(null),
                    }, music, "primary", true, "essentia", "chroma-selftest", "fixture", null, null);
                    var publishError = global::Truedat.Store.StoreScan.Publish(store, catalog, out _);
                    assert(publishError == null, $"chroma-store: the catalog publishes from the store ({publishError?.Message})");
                    var back = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
                    if (File.Exists(catalog)) Program.LoadExistingMoodsForStore(catalog, back);
                    assert(back.TryGetValue(marked, out var m) && m.Features.ChromaFold == Program.ChromaFoldCurrent, "chroma-store: a marker stored is a marker published");
                    assert(back.TryGetValue(unmarked, out var u) && u.Features.ChromaFold == null, "chroma-store: and an unmarked track comes back unmarked");

                    // The marker travels with the chroma it describes. A catalog from before the marker, imported over a
                    // marked description, replaces the chroma with its own (older) values — and must not leave the marker
                    // behind to vouch for them.
                    var held = Entry(Program.ChromaFoldCurrent);
                    var older = Entry(null);
                    older.AudioStreamSha256 = held.AudioStreamSha256;                                  // the same audio
                    older.Features.Hpcp12 = new[] { 0.6, 0.3, 0, 0, 0.9, 0.2, 0, 1.0, 0.1, 0, 0, 0.4 };  // grouped the old way: different values
                    var store2 = Path.Combine(root, "lib2", "truedat.db"); Directory.CreateDirectory(Path.Combine(root, "lib2"));
                    var catalog2 = Path.Combine(root, "lib2", Program.MoodsFileName);
                    global::Truedat.Store.ImportCommand.ExecuteEntries(store2, new Dictionary<string, TrackEntry>(PathComparer.Instance) { [marked] = held },
                        music, "primary", true, "essentia", "chroma-selftest", "fixture", null, null);
                    global::Truedat.Store.ImportCommand.ExecuteEntries(store2, new Dictionary<string, TrackEntry>(PathComparer.Instance) { [marked] = older },
                        music, "primary", true, "import", "1", "fixture", null, null);
                    global::Truedat.Store.StoreScan.Publish(store2, catalog2, out _);
                    var after = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
                    if (File.Exists(catalog2)) Program.LoadExistingMoodsForStore(catalog2, after);
                    after.TryGetValue(marked, out var replaced);
                    bool tookOlderChroma = replaced?.Features?.Hpcp12 != null && Math.Abs(replaced.Features.Hpcp12[0] - 0.6) < 1e-9;
                    assert(tookOlderChroma, $"chroma-store: (fixture) the imported chroma replaced the stored one ({replaced?.Features?.Hpcp12?[0]})");
                    assert(tookOlderChroma && replaced!.Features.ChromaFold == null,
                        $"chroma-store: chroma replaced by values that carry no marker is left unmarked, not vouched for by the old marker ({replaced?.Features?.ChromaFold?.ToString() ?? "none"})");

                    // a version 4 store opened by this build
                    var old = Path.Combine(root, "old", "truedat.db"); Directory.CreateDirectory(Path.Combine(root, "old"));
                    using (var db = global::Truedat.Store.SqliteDb.Open(old, create: true))
                    {
                        global::Truedat.Store.SchemaMigrator.Apply(db, "selftest", 4);
                        db.Exec("INSERT INTO store_meta(key, value) VALUES ('selftest.kept', 'yes');");
                    }
                    string kept, version, hasColumn;
                    using (var os = global::Truedat.Store.OpenStore.Open(old, false, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
                    {
                        kept = os.Dal.MetaGet("selftest.kept") ?? "";
                        version = os.Db.ExecScalar("SELECT max(version) FROM schema_version;") ?? "";
                        hasColumn = os.Db.ExecScalar("SELECT count(*) FROM pragma_table_info('track_content') WHERE name = 'chromaFold';") ?? "";
                    }
                    assert(version == "5" && hasColumn == "1", $"chroma-store: a version 4 store is brought to version 5 with the new column when opened (version {version}, column {hasColumn})");
                    assert(kept == "yes", "chroma-store: and what it held is still there");
                    assert(Directory.GetFiles(Path.Combine(root, "old"), "truedat.db.bak.v4.*").Length == 1, "chroma-store: with a backup of the version 4 store beside it first");
                }
            }
            catch (Exception ex) { assert(false, "chroma-store: threw " + ex.GetType().Name + ": " + ex.Message); }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>The flag through a real scan: an unmarked track is a cache hit on a plain scan, is analyzed once under
        /// --refresh-chroma, and is a cache hit again on the next --refresh-chroma.</summary>
        static void TestCli(Action<bool, string> assert)
        {
            var exe = typeof(Program).Assembly.Location;
            var exeDir = Path.GetDirectoryName(exe) ?? ".";
            var ffmpeg = Path.Combine(exeDir, "ffmpeg.exe");
            if (!File.Exists(Path.Combine(exeDir, "essentia_streaming_extractor_music.exe")) || !File.Exists(ffmpeg))
            {
                Console.WriteLine("  NOT RUN  --refresh-chroma through a scan: needs the extractor and ffmpeg.exe beside the test binary (a real track is analyzed)");
                return;
            }
            var root = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "chroma-cli-" + Guid.NewGuid().ToString("N"));
            bool keep = false;
            try
            {
                var lib = Path.Combine(root, "Library"); var music = Path.Combine(root, "music"); var stage = Path.Combine(root, "stage");
                Directory.CreateDirectory(lib); Directory.CreateDirectory(music); Directory.CreateDirectory(stage);
                Directory.CreateDirectory(Path.Combine(root, "AppData", "MBXHub", "review"));   // the review folder resolves inside the fixture
                var track = Path.Combine(music, "tone.flac");
                string Q(string v) => "\"" + v + "\"";
                RunChild(ffmpeg, music, "-v error -f lavfi -i sine=frequency=440:duration=25 -ac 2 -ar 44100 -y " + Q(track), out _);
                var xml = Path.Combine(lib, "iTunes Music Library.xml");
                File.WriteAllText(xml,
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<plist version=\"1.0\">\n<dict>\n\t<key>Tracks</key>\n\t<dict>\n"
                    + "\t\t<key>1</key>\n\t\t<dict>\n\t\t\t<key>Track ID</key><integer>1</integer>\n\t\t\t<key>Name</key><string>tone</string>\n"
                    + "\t\t\t<key>Artist</key><string>Fixture</string>\n\t\t\t<key>Total Time</key><integer>25000</integer>\n"
                    + "\t\t\t<key>Location</key><string>" + new Uri(track).AbsoluteUri + "</string>\n\t\t</dict>\n\t</dict>\n</dict>\n</plist>\n");
                var catalog = Path.Combine(lib, Program.MoodsFileName);
                string common = "--no-config " + Q(xml) + " --no-store --stats-detail 1 --stage-dir " + Q(stage) + " -p 1";
                void Check(bool cond, string msg) { if (!cond) keep = true; assert(cond, msg + (cond ? "" : "  [fixture kept: " + root + "]")); }
                int? FoldOnDisk()
                {
                    var held = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
                    Program.LoadExistingMoodsForStore(catalog, held);
                    return held.Values.FirstOrDefault()?.Features?.ChromaFold;
                }

                var first = RunChild(exe, lib, common, out int e1);
                File.WriteAllText(Path.Combine(root, "1-scan.log"), first);
                Check(e1 == 0 && FoldOnDisk() == Program.ChromaFoldCurrent, $"chroma-cli: a track analyzed by this build carries the current marker (exit {e1}, marker {FoldOnDisk()?.ToString() ?? "none"})");

                // The same catalog as an earlier build would have left it: the analysis is there, the marker is not.
                var before = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
                Program.LoadExistingMoodsForStore(catalog, before);
                foreach (var entry in before.Values) entry.Features.ChromaFold = null;
                Program.SaveResultsForStore(catalog, before);
                Check(FoldOnDisk() == null, "chroma-cli: the fixture now holds the track without a marker");

                var plain = RunChild(exe, lib, common, out int e2);
                File.WriteAllText(Path.Combine(root, "2-plain.log"), plain);
                Check(e2 == 0 && plain.Contains("(cached)") && FoldOnDisk() == null, "chroma-cli: a plain scan serves the unmarked track from the cache and leaves it unmarked");

                var refresh = RunChild(exe, lib, common + " --refresh-chroma", out int e3);
                File.WriteAllText(Path.Combine(root, "3-refresh.log"), refresh);
                Check(e3 == 0 && !refresh.Contains("(cached)") && FoldOnDisk() == Program.ChromaFoldCurrent, $"chroma-cli: --refresh-chroma analyzes it again and it then carries the marker (exit {e3}, marker {FoldOnDisk()?.ToString() ?? "none"})");

                var again = RunChild(exe, lib, common + " --refresh-chroma", out int e4);
                File.WriteAllText(Path.Combine(root, "4-again.log"), again);
                Check(e4 == 0 && again.Contains("(cached)"), "chroma-cli: a second --refresh-chroma finds nothing to do");
            }
            catch (Exception ex) { keep = true; assert(false, "chroma-cli: threw " + ex.GetType().Name + ": " + ex.Message + "  [fixture kept: " + root + "]"); }
            finally { if (!keep) { try { Directory.Delete(root, true); } catch { } } }
        }

        static string RunChild(string exe, string cwd, string args, out int exitCode)
        {
            var start = new ProcessStartInfo
            {
                FileName = exe, WorkingDirectory = cwd, Arguments = WorkFolderSelfTest.ChildArgs(exe, cwd, args), UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            };
            using var child = Process.Start(start) ?? throw new InvalidOperationException("could not start the child");
            child.StandardInput.Close();
            var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(180000)) { try { child.Kill(); } catch { } throw new TimeoutException("the child did not exit within 180 seconds"); }
            exitCode = child.ExitCode;
            return stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        }
    }
}
