using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace Truedat.Store
{
    /// <summary>CLI regressions for a merge's content-presence rule, reached by the global --self-test through GenerationSelfTest.</summary>
    internal static class StoreMergeSelfTest
    {
        internal static void Run(Action<bool, string> assert)
        {
            foreach (var shape in new[] { "omitted", "null", "value" }) TestContentPresence(assert, shape);
            TestNoHashHistogram(assert);
        }

        static void TestContentPresence(Action<bool, string> assert, string shape)
        {
            var dir = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "merge-presence-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            bool keepFixture = false;
            try
            {
                const string path = @"D:\music\p.flac", otherPath = @"D:\music\untouched.flac";
                var store = Path.Combine(dir, "truedat.db");
                var catalog = Path.Combine(dir, "mbxmoods.json");
                var winner = Path.Combine(dir, "winner.json");
                var modified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
                TrackEntry Seed(char hash, string title) => new TrackEntry
                {
                    AudioStreamSha256 = new string(hash, 64), AudioStreamSha256Source = "flac-frames", LastModified = modified,
                    Features = new TrackFeatures { Artist = "Artist", Title = title, Bpm = 120, Key = "C", Mode = "major" },
                };
                ImportCommand.ExecuteEntries(store, new Dictionary<string, TrackEntry>(PathComparer.Instance)
                {
                    [path] = Seed('a', "Original"), [otherPath] = Seed('b', "Untouched"),
                }, @"D:\music", "primary", true, "essentia", "selftest-merge", "fixture", null, null);
                long generation;
                using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o"))) generation = os.Dal.Generation();

                // Raw JSON preserves absence and explicit null, which constructing TrackFeatures would turn into zero/empty first.
                var track = new JsonObject
                {
                    ["audioStreamSha256"] = new string('a', 64), ["audioStreamSha256Source"] = "flac-frames",
                    ["lastModified"] = modified.ToString("o"), ["title"] = "Retitled",
                };
                if (shape == "null") { track["bpm"] = null; track["key"] = null; track["mode"] = null; }
                if (shape == "value") { track["bpm"] = 0; track["key"] = ""; track["mode"] = ""; }
                File.WriteAllText(winner, new JsonObject { ["tracks"] = new JsonObject { [path] = track } }.ToJsonString());

                // The child invokes this build's real merge and wrapper, never --self-test recursively. The wrapper publishes the
                // seeded store before it reads the first source; no audio files or operator config are involved.
                if (!RunMerge(assert, "merge content " + shape, dir, store, catalog, ref keepFixture, winner)) return;

                using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
                {
                    var entries = os.Dal.ReadEntries(os.Dal.LibraryRoots().Select(r => r.Id)).ToDictionary(e => e.DisplayPath, e => e.Entry, PathComparer.Instance);
                    var actual = entries[path].Features;
                    bool supplied = shape == "value";
                    assert(actual.Bpm == (supplied ? 0 : 120) && actual.Key == (supplied ? "" : "C") && actual.Mode == (supplied ? "" : "major"),
                        $"merge content {shape}: {(supplied ? "supplied zero/empty values replace" : "unobserved values preserve")} bpm/key/mode after reopen (got {actual.Bpm}, '{actual.Key}', '{actual.Mode}')");
                    assert(actual.Title == "Retitled" && actual.Artist == "Artist", $"merge content {shape}: supplied title lands and omitted artist stays");
                    var untouched = entries[otherPath].Features;
                    assert(entries.Count == 2 && untouched.Title == "Untouched" && untouched.Bpm == 120 && untouched.Key == "C" && untouched.Mode == "major",
                        $"merge content {shape}: the unrelated destination entry stays unchanged");
                    assert(os.Dal.Generation() == generation + 1, $"merge content {shape}: one visible merge advances generation once ({generation} -> {os.Dal.Generation()})");
                }
            }
            finally { if (!keepFixture) try { Directory.Delete(dir, true); } catch { } }
        }

        static void TestNoHashHistogram(Action<bool, string> assert)
        {
            var dir = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "merge-no-hash-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            bool keepFixture = false;
            try
            {
                const string newPath = @"D:\music\new.flac", replacePath = @"D:\music\replace.flac", nullPath = @"D:\music\null.flac",
                    untouchedPath = @"D:\music\untouched.flac", losingPath = @"D:\music\losing.flac";
                var store = Path.Combine(dir, "truedat.db");
                var catalog = Path.Combine(dir, "mbxmoods.json");
                var winner = Path.Combine(dir, "winner.db");
                var loser = Path.Combine(dir, "loser.db");
                var modified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
                double[] H(int bin) { var histogram = new double[250]; histogram[bin] = 1; return histogram; }
                TrackEntry Entry(string title, double[]? histogram, string? sha = null) => new TrackEntry
                {
                    AudioStreamSha256 = sha, AudioStreamSha256Source = sha == null ? null : "flac-frames", LastModified = modified,
                    Features = new TrackFeatures { Artist = "Artist", Title = title, Bpm = 120, BpmHistogram = histogram },
                };
                void Seed(string destination, Dictionary<string, TrackEntry> entries)
                {
                    using (StoreLog.Quiet())
                        ImportCommand.ExecuteEntries(destination, entries, @"D:\music", "primary", true, "essentia", "selftest-merge", "fixture", null, null);
                }
                Seed(store, new Dictionary<string, TrackEntry>(PathComparer.Instance)
                {
                    [replacePath] = Entry("Original", H(20)), [nullPath] = Entry("Original", H(30)),
                    [untouchedPath] = Entry("Untouched", H(40)),
                });
                Seed(loser, new Dictionary<string, TrackEntry>(PathComparer.Instance)
                {
                    [losingPath] = Entry("Losing hashed source", H(50), new string('a', 64)),
                });
                Seed(winner, new Dictionary<string, TrackEntry>(PathComparer.Instance)
                {
                    [newPath] = Entry("New winner", H(10)), [replacePath] = Entry("Replacement winner", H(21)),
                    [nullPath] = Entry("Null winner", null), [losingPath] = Entry("No-hash winner", null),
                });
                // The winning store really carries the cut fields; its own entry is the authority for them even without a hash.
                var source = StoreTransfer.ReadEntries(winner);
                assert(source.Count == 4 && source.Values.All(e => string.IsNullOrEmpty(e.AudioStreamSha256))
                    && source[newPath].Features.BpmHistogram?.SequenceEqual(H(10)) == true
                    && source[replacePath].Features.BpmHistogram?.SequenceEqual(H(21)) == true
                    && source[nullPath].Features.BpmHistogram == null,
                    "merge no-hash histogram: source fixture retains supplied histograms and an unobserved NULL without inventing hashes");
                long generation;
                using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o"))) generation = os.Dal.Generation();

                if (!RunMerge(assert, "merge no-hash histogram", dir, store, catalog, ref keepFixture, loser, winner)) return;
                using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
                {
                    var entries = os.Dal.ReadEntries(os.Dal.LibraryRoots().Select(r => r.Id)).ToDictionary(e => e.DisplayPath, e => e.Entry, PathComparer.Instance);
                    assert(entries.Count == 5 && entries.Values.All(e => string.IsNullOrEmpty(e.AudioStreamSha256)),
                        "merge no-hash histogram: all five winning paths persist without borrowing the losing source's hash");
                    CheckHistogram(newPath, H(10), "the new winning store entry supplies its own histogram");
                    CheckHistogram(replacePath, H(21), "the winning store's supplied histogram replaces the destination value");
                    CheckHistogram(nullPath, H(30), "a winning store's NULL preserves the destination histogram");
                    CheckHistogram(untouchedPath, H(40), "an unrelated destination entry retains its histogram");
                    CheckHistogram(losingPath, null, "the no-hash winner never borrows the losing hashed source's histogram");
                    assert(entries[newPath].Features.Title == "New winner" && entries[replacePath].Features.Title == "Replacement winner"
                        && entries[nullPath].Features.Title == "Null winner" && entries[untouchedPath].Features.Title == "Untouched"
                        && entries[losingPath].Features.Title == "No-hash winner",
                        "merge no-hash histogram: supplied winning titles land while the unrelated title stays");
                    assert(os.Dal.Generation() == generation + 1,
                        $"merge no-hash histogram: one merge advances the destination generation once ({generation} -> {os.Dal.Generation()})");
                    generation = os.Dal.Generation();

                    void CheckHistogram(string path, double[]? expected, string message)
                    {
                        var actual = entries.TryGetValue(path, out var entry) ? entry.Features.BpmHistogram : null;
                        bool equal = entries.ContainsKey(path) && (expected == null ? actual == null : actual?.SequenceEqual(expected) == true);
                        string bins = actual == null ? "NULL" : string.Join(",", actual.Select((value, bin) => (value, bin)).Where(v => v.value != 0).Select(v => v.bin));
                        assert(equal, $"merge no-hash histogram: {message} (got {bins})");
                    }
                }
                if (!RunMerge(assert, "merge no-hash histogram repeat", dir, store, catalog, ref keepFixture, loser, winner)) return;
                using (var os = OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
                    assert(os.Dal.Generation() == generation,
                        $"merge no-hash histogram: repeating the same merge is a no-op ({generation} -> {os.Dal.Generation()})");
            }
            finally { if (!keepFixture) try { Directory.Delete(dir, true); } catch { } }
        }

        static bool RunMerge(Action<bool, string> assert, string label, string dir, string store, string catalog, ref bool keepFixture, params string[] sources)
        {
            string Q(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
            var start = new ProcessStartInfo
            {
                FileName = typeof(Program).Assembly.Location, WorkingDirectory = dir,
                Arguments = "--no-config --store " + Q(store) + " --merge-moods --merge-source " + Q(catalog)
                    + string.Concat(sources.Select(source => " --merge-source " + Q(source))) + " --merge-output " + Q(catalog),
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using (var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the merge test child."))
            {
                var stdout = child.StandardOutput.ReadToEndAsync();
                var stderr = child.StandardError.ReadToEndAsync();
                if (!child.WaitForExit(30000))
                {
                    keepFixture = true; // Do not terminate a process or remove a fixture it may still be using.
                    assert(false, $"{label}: child did not exit within 30 seconds; fixture kept at {dir}");
                    return false;
                }
                string output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
                assert(child.ExitCode == 0, $"{label}: CLI exits 0 (got {child.ExitCode})"
                    + (child.ExitCode == 0 ? "" : ": " + output.Substring(0, Math.Min(output.Length, 600))));
                return child.ExitCode == 0;
            }
        }
    }
}
