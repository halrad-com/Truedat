using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Truedat
{
    /// <summary>
    /// What a library scan's summary recommends for entries that lack the newest feature fields.
    ///
    /// The rule that sorts them (failed before, not listed, skipped as too short, genuinely behind)
    /// is pinned by unit asserts on hand-built sets. Those cannot see whether a SCAN hands the rule
    /// the error list and the skip list that are on disk — and a summary that sends someone to
    /// <c>--refresh</c> for a track a refresh can never reach reads as a tool that does not work.
    /// This runs the scan and reads its advice.
    ///
    /// Nothing is analyzed: both tracks are served without the extractor (one cached, one on the
    /// error list). The scan still refuses to start without the extractor beside the binary.
    /// </summary>
    internal static class SummaryAdviceSelfTest
    {
        internal static void Run(Action<bool, string> assert)
        {
            Console.WriteLine("Scan summary: the advice for entries without the newest fields");
            var exe = typeof(Program).Assembly.Location;
            var exeDir = Path.GetDirectoryName(exe) ?? ".";
            if (!File.Exists(Path.Combine(exeDir, "essentia_streaming_extractor_music.exe")))
            {
                Console.WriteLine("  NOT RUN  scan summary advice: a library scan refuses to start without essentia_streaming_extractor_music.exe beside the test binary (nothing here is analyzed)");
                return;
            }
            var root = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "advice-" + Guid.NewGuid().ToString("N"));
            bool keep = false;
            try
            {
                var lib = Path.Combine(root, "Library");
                var music = Path.Combine(root, "music");
                // <root>\AppData\MBXHub makes the review folder resolve INSIDE the fixture, never into a real instance.
                Directory.CreateDirectory(lib); Directory.CreateDirectory(music);
                Directory.CreateDirectory(Path.Combine(root, "AppData", "MBXHub", "review")); Directory.CreateDirectory(Path.Combine(root, "stage"));

                var stamp = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
                string Make(string name)
                {
                    var p = Path.Combine(music, name);
                    File.WriteAllText(p, "not audio; never opened");
                    File.SetLastWriteTimeUtc(p, stamp);
                    return p;
                }
                var failed = Make("failed-before.flac");
                var tooShort = Make("too-short.flac");

                // Both entries are current enough to be cache hits and both lack the newest fields.
                TrackEntry Old(string path, char sha) => new TrackEntry
                {
                    Features = new TrackFeatures
                    {
                        FilePath = path, Artist = "Fixture", Title = Path.GetFileNameWithoutExtension(path),
                        Bpm = 120, Mfcc = new[] { 1.5, -2.25 }, DynamicRange = 8, LoudnessMomentary = -14,
                    },
                    AudioStreamSha256 = new string(sha, 64), AudioStreamSha256Source = "flac-frames",
                    LastModified = stamp,
                };
                var catalog = Path.Combine(lib, Program.MoodsFileName);
                Program.SaveResultsForStore(catalog, new ConcurrentDictionary<string, TrackEntry>(new[]
                {
                    new KeyValuePair<string, TrackEntry>(failed, Old(failed, 'a')),
                    new KeyValuePair<string, TrackEntry>(tooShort, Old(tooShort, 'b')),
                }, PathComparer.Instance));

                string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
                File.WriteAllText(Path.Combine(lib, "mbxmoods-errors.csv"),
                    "Error,Artist,Title,FilePath,SizeMB,Duration,FailedComponents\r\n"
                    + Csv("exit 1") + "," + Csv("Fixture") + "," + Csv("failed-before") + "," + Csv(failed) + ",1.0,25,\r\n");
                File.WriteAllText(Path.Combine(lib, "mbxmoods-skipped.csv"),
                    "path,extension,reason,timestamp\r\n"
                    + Csv(tooShort) + ",.flac," + Csv("short file: 3 s < 5 s min (--enableshortfiles to include)") + ",2026-01-01T00:00:00Z\r\n");

                var xml = Path.Combine(lib, "iTunes Music Library.xml");
                string Listed(int id, string path) =>
                    "\t\t<key>" + id + "</key>\n\t\t<dict>\n\t\t\t<key>Track ID</key><integer>" + id + "</integer>\n"
                    + "\t\t\t<key>Name</key><string>" + Path.GetFileNameWithoutExtension(path) + "</string>\n"
                    + "\t\t\t<key>Artist</key><string>Fixture</string>\n\t\t\t<key>Total Time</key><integer>25000</integer>\n"
                    + "\t\t\t<key>Location</key><string>" + new Uri(path).AbsoluteUri + "</string>\n\t\t</dict>\n";
                File.WriteAllText(xml,
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<plist version=\"1.0\">\n<dict>\n\t<key>Tracks</key>\n\t<dict>\n"
                    + Listed(1, failed) + Listed(2, tooShort) + "\t</dict>\n</dict>\n</plist>\n");

                string Q(string v) => "\"" + v + "\"";
                var output = RunChild(exe, lib, "--no-config " + Q(xml) + " --no-store --stats-detail 1 --stage-dir " + Q(Path.Combine(root, "stage")) + " -p 1", out int exit);
                File.WriteAllText(Path.Combine(root, "scan.log"), output);
                void Check(bool cond, string msg) { if (!cond) keep = true; assert(cond, msg + (cond ? "" : "  [fixture kept: " + root + "]")); }

                Check(exit == 0, $"scan-advice: the scan exits 0 (got {exit})");
                Check(output.Contains("2 entries without the latest features"), "scan-advice: the summary counts both entries that lack the newest fields");
                Check(output.Contains("1 previously errored") && output.Contains("--retry-errors"), "scan-advice: the one on the error list is sent to --retry-errors");
                Check(output.Contains("1 skipped, not analyzable"), "scan-advice: the one a scan skips every time is named as that");
                Check(!output.Contains("just stale") && !output.Contains("truedat --refresh"), "scan-advice: and neither is recommended a --refresh, which could reach neither");

                // ---- a line that reads as a warning is for something that needs attention
                Check(global::Truedat.Store.ImportCommand.ReplacementIsExpected("import", "essentia"),
                    "replace-log: a description seeded from a catalog and then measured by a scan is the expected course, not a warning");
                Check(!global::Truedat.Store.ImportCommand.ReplacementIsExpected("essentia", "import"),
                    "replace-log: a catalog's values going over a scan's measurement is worth a warning");
                Check(!global::Truedat.Store.ImportCommand.ReplacementIsExpected("essentia", "essentia") && !global::Truedat.Store.ImportCommand.ReplacementIsExpected("import", "import"),
                    "replace-log: so is any other replacement");

                // ---- the store summary is made to be pasted: nothing but the block, and no path above it
                var storeDir = Path.Combine(root, "store"); Directory.CreateDirectory(storeDir);
                var store = Path.Combine(storeDir, "truedat.db");
                using (global::Truedat.Store.StoreLog.Quiet())
                using (global::Truedat.Store.OpenStore.Open(store, true, VersionInfo.Display, DateTime.UtcNow.ToString("o"))) { }
                var summary = RunChild(exe, storeDir, "--no-config --store-summary-min --store " + Q(store), out int summaryExit);
                File.WriteAllText(Path.Combine(root, "summary.log"), summary);
                string firstLine = "";
                foreach (var line in summary.Split(new[] { (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries)) { firstLine = line; break; }
                Check(summaryExit == 0 && firstLine.StartsWith("--- copy from here", StringComparison.Ordinal), $"store-summary: the first thing printed is the block's own opening line ('{firstLine}')");
                Check(!summary.Contains(storeDir), "store-summary: and the store's path is nowhere in the output");
            }
            catch (Exception ex) { keep = true; assert(false, "scan-advice: threw " + ex.GetType().Name + ": " + ex.Message + "  [fixture kept: " + root + "]"); }
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
            if (!child.WaitForExit(120000)) { try { child.Kill(); } catch { } throw new TimeoutException("the child did not exit within 120 seconds"); }
            exitCode = child.ExitCode;
            return stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        }
    }
}
