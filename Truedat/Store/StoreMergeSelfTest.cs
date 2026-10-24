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
                string Q(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
                var start = new ProcessStartInfo
                {
                    FileName = typeof(Program).Assembly.Location, WorkingDirectory = dir,
                    Arguments = "--no-config --store " + Q(store) + " --merge-moods --merge-source " + Q(catalog)
                        + " --merge-source " + Q(winner) + " --merge-output " + Q(catalog),
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                };
                using (var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the merge test child."))
                {
                    var stdout = child.StandardOutput.ReadToEndAsync();
                    var stderr = child.StandardError.ReadToEndAsync();
                    if (!child.WaitForExit(30000))
                    {
                        keepFixture = true; // Do not terminate a process or remove a fixture it may still be using.
                        assert(false, $"merge content {shape}: child did not exit within 30 seconds; fixture kept at {dir}");
                        return;
                    }
                    string output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
                    assert(child.ExitCode == 0, $"merge content {shape}: CLI exits 0 (got {child.ExitCode})"
                        + (child.ExitCode == 0 ? "" : ": " + output.Substring(0, Math.Min(output.Length, 600))));
                    if (child.ExitCode != 0) return;
                }

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
    }
}
