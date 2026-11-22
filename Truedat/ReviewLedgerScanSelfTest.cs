using System;
using System.Diagnostics;
using System.IO;

namespace Truedat
{
    /// <summary>
    /// Drives a real library scan and a real <c>--fixup</c>, each in a child process, against a
    /// review ledger on disk.
    ///
    /// The ledger's own rules are pinned by unit asserts beside the other ledger tests. Those
    /// cannot pin where the scan CONSULTS the ledger, and that is exactly where this defect lived:
    /// the record check ran before the file-exists check, and nothing ever retired a record for a
    /// path the library had stopped listing. A unit assert cannot see either, so this test runs
    /// the verbs and reads the file they leave.
    ///
    /// No audio is analyzed: the one listed track is missing on purpose. The scan still refuses to
    /// start without the extractor beside the binary, so without it the test says NOT RUN rather
    /// than passing quietly.
    /// </summary>
    internal static class ReviewLedgerScanSelfTest
    {
        internal static void Run(Action<bool, string> assert)
        {
            Console.WriteLine("Review ledger: the scan and --fixup against a ledger on disk");
            var exe = typeof(Program).Assembly.Location;
            var exeDir = Path.GetDirectoryName(exe) ?? ".";
            if (!File.Exists(Path.Combine(exeDir, "essentia_streaming_extractor_music.exe")))
            {
                Console.WriteLine("  NOT RUN  review-ledger scan path: a library scan refuses to start without essentia_streaming_extractor_music.exe beside the test binary (nothing here is analyzed)");
                return;
            }

            var root = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "review-scan-" + Guid.NewGuid().ToString("N"));
            bool keep = false;
            try
            {
                var lib = Path.Combine(root, "Library");
                var music = Path.Combine(root, "music");
                // <root>\AppData\MBXHub makes the review folder resolve INSIDE the fixture. Without it the
                // resolver falls back to a running MusicBee, and a test must never write into a real one.
                var review = Path.Combine(root, "AppData", "MBXHub", "review");
                Directory.CreateDirectory(lib); Directory.CreateDirectory(music); Directory.CreateDirectory(review);
                Directory.CreateDirectory(Path.Combine(root, "stage"));

                var missing = Path.Combine(music, "missing.flac");       // listed by the library, not on disk
                var ghost = Path.Combine(music, "ghost.flac");           // not listed, not on disk
                var ghostIgnored = Path.Combine(music, "ignored.flac");  // not listed, not on disk, operator said ignore
                var xml = Path.Combine(lib, "iTunes Music Library.xml");
                File.WriteAllText(xml,
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<plist version=\"1.0\">\n<dict>\n\t<key>Tracks</key>\n\t<dict>\n"
                    + "\t\t<key>1</key>\n\t\t<dict>\n\t\t\t<key>Track ID</key><integer>1</integer>\n\t\t\t<key>Name</key><string>missing</string>\n"
                    + "\t\t\t<key>Artist</key><string>Fixture</string>\n\t\t\t<key>Total Time</key><integer>600000</integer>\n"
                    + "\t\t\t<key>Location</key><string>" + new Uri(missing).AbsoluteUri + "</string>\n\t\t</dict>\n"
                    + "\t</dict>\n</dict>\n</plist>\n");

                var t0 = new DateTime(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc);
                var seed = new ReviewLedger();
                seed.Upsert(new ReviewRecord { Path = missing, Disposition = ReviewDisposition.Failed, Reason = "essentia exit 1" }, t0, false);
                seed.Upsert(new ReviewRecord { Path = ghost, Disposition = ReviewDisposition.Skipped, Reason = "short file", State = ReviewState.Auto, StateReason = "short file" }, t0, false);
                seed.Upsert(new ReviewRecord { Path = ghostIgnored, Disposition = ReviewDisposition.Skipped, Reason = "short file" }, t0, false);
                seed.SetState(ghostIgnored, ReviewState.Ignore);
                var ledgerPath = Path.Combine(review, ReviewLedger.FileName);
                File.WriteAllText(ledgerPath, seed.ToJson("selftest", t0).ToJsonString());

                // ---- a library scan
                var scan = RunChild(exe, lib, "--no-config " + Q(xml) + " --no-store --stage-dir " + Q(Path.Combine(root, "stage")) + " -p 1", out int scanExit);
                File.WriteAllText(Path.Combine(root, "scan.log"), scan);
                bool ok = true;
                void Check(bool cond, string msg) { if (!cond) { ok = false; keep = true; } assert(cond, msg + (cond ? "" : "  [fixture kept: " + root + "]")); }

                Check(scanExit == 0, $"review-scan: the scan exits 0 (got {scanExit})");
                Check(scan.Contains("[skipped missing]"),
                    "review-scan: a listed file that no longer exists is reported missing, though it holds a failure record");
                Check(!scan.Contains("(skip: recorded"),
                    "review-scan: its old recorded reason is not reprinted — the file is looked at before its history");
                var afterScan = ReviewLedger.Load(ledgerPath, out var e1);
                Check(e1 == null, $"review-scan: the ledger the scan wrote is readable ({e1})");
                var m = afterScan.Find(missing);
                Check(m != null && m.Disposition == ReviewDisposition.Skipped && m.Reason.StartsWith("file not found", StringComparison.Ordinal),
                    $"review-scan: the record now says file not found ({m?.Disposition}: {m?.Reason})");
                Check(afterScan.Find(ghost) == null,
                    "review-scan: a record for a path the library no longer lists is retired at the end of the scan");
                Check(afterScan.Find(ghostIgnored) != null,
                    "review-scan: the operator's ignore is not retired on library membership alone");
                Check(scan.Contains("1 record(s) retired"),
                    "review-scan: the summary says a record was retired");

                // ---- --fixup: both remaining records name files that no longer exist
                var fix = RunChild(exe, lib, "--no-config --fixup " + Q(xml) + " --no-store", out int fixExit);
                File.WriteAllText(Path.Combine(root, "fixup.log"), fix);
                Check(fixExit == 0, $"review-fixup: --fixup exits 0 (got {fixExit})");
                var afterFix = ReviewLedger.Load(ledgerPath, out var e2);
                Check(e2 == null && afterFix.Count == 0,
                    $"review-fixup: --fixup retires every record whose file is gone, the ignored one included ({afterFix.Count} left; {e2})");
                Check(fix.Contains("2 record(s) retired"), "review-fixup: --fixup says how many records it retired");
                if (!ok) Console.WriteLine("    logs: " + root);
            }
            catch (Exception ex)
            {
                keep = true;
                assert(false, "review-scan: threw " + ex.GetType().Name + ": " + ex.Message + "  [fixture kept: " + root + "]");
            }
            finally
            {
                if (!keep) { try { Directory.Delete(root, true); } catch { } }
            }
        }

        static string RunChild(string exe, string cwd, string args, out int exitCode)
        {
            var start = new ProcessStartInfo
            {
                FileName = exe, WorkingDirectory = cwd, Arguments = WorkFolderSelfTest.ChildArgs(exe, cwd, args),
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var child = Process.Start(start) ?? throw new InvalidOperationException("could not start the child");
            var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(120000)) { try { child.Kill(); } catch { } throw new TimeoutException("the child did not exit within 120 seconds"); }
            exitCode = child.ExitCode;
            return stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        }

        // Windows command-line quoting doubles trailing backslashes before the closing quote.
        static string Q(string value) => "\"" + value + new string('\\', value.Length - value.TrimEnd('\\').Length) + "\"";
    }
}
