using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Truedat.Store;

namespace Truedat
{
    /// <summary>
    /// The run record (<c>store-result.json</c>) for a scan with no store, and the <c>list</c> block of a list scan.
    ///
    /// A launcher that hands truedat a list of paths reads the record afterwards instead of the console. Two things
    /// had to become true for that: a scan with no store in force writes the record too (its <c>store</c> and
    /// <c>export</c> are null — there is none), and a list scan says what became of the paths it was given, every one
    /// of them in exactly one count.
    /// </summary>
    internal static class ListRecordSelfTest
    {
        internal static void Run(Action<bool, string> assert)
        {
            Console.WriteLine("Run record: written with no store, and a list scan accounts for every listed path");
            TestRoundTrip(assert);
            TestLifecycle(assert);
            TestCli(assert);
        }

        static JsonElement Raw(string path) => JsonDocument.Parse(File.ReadAllBytes(path)).RootElement.Clone();
        static bool IsNull(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Null;
        static long? Num(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : (long?)null;

        static void TestRoundTrip(Action<bool, string> assert)
        {
            var dir = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "listrec-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var rec = StoreResult.Begin(StoreResult.VerbScanList);
                rec.NoStore = true;
                rec.List = new StoreResultList { Path = "new.txt", Listed = 3 };
                var path = StoreResult.Write(Path.Combine(dir, "truedat.db"), rec, null, StoreResult.OutcomeInterrupted, StoreResult.ProvisionalReason);
                assert(path != null && File.Exists(path), "list-record: a record with no store is written where the store's would be");
                if (path == null) return;
                var raw = Raw(path);
                assert(IsNull(raw, "store") && IsNull(raw, "export"), "list-record: with no store in force the store and export blocks are null, not empty objects");
                assert(raw.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Object && Num(list, "listed") == 3,
                    "list-record: the list block carries how many paths were listed");
                assert(raw.TryGetProperty("list", out list) && IsNull(list, "analyzed") && IsNull(list, "current") && IsNull(list, "excluded") && IsNull(list, "failed")
                       && IsNull(list, "notFound") && IsNull(list, "skipped") && IsNull(list, "notExamined"),
                    "list-record: a count not known yet is null, never 0");
                bool read = StoreResult.TryRead(path, out var back);
                assert(read && back.NoStore && back.Verb == "scan-list" && back.List != null && back.List.Listed == 3 && back.List.Path == "new.txt" && back.List.Analyzed == null,
                    "list-record: the record reads back as written");

                var withStore = StoreResult.Begin("scan");
                withStore.Store = new StoreResultStore { Path = Path.Combine(dir, "truedat.db"), TruedatVersion = "t" };
                var p2 = StoreResult.Write(Path.Combine(dir, "truedat.db"), withStore, 0, StoreResult.OutcomeCompleted);
                var raw2 = Raw(p2!);
                assert(raw2.TryGetProperty("store", out var st) && st.ValueKind == JsonValueKind.Object && raw2.TryGetProperty("export", out var ex) && ex.ValueKind == JsonValueKind.Object
                       && !raw2.TryGetProperty("list", out _),
                    "list-record: a store-backed scan's record is unchanged — store and export objects, no list block");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        static void TestLifecycle(Action<bool, string> assert)
        {
            var dir = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "listrec-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var catalog = Path.Combine(dir, Program.MoodsFileName);
            var record = Path.Combine(dir, StoreResultFile.FileName);
            StoreResult.ResetScanForTest();
            try
            {
                // ---- no store: begun, the list read, finished at the process boundary
                StoreResult.BeginScanWithoutStore(catalog, null, "2026-11-21T00:00:00.0000000Z");
                bool begun = StoreResult.TryRead(record, out var r0);
                assert(begun && r0.NoStore && r0.Verb == "scan" && r0.ExitCode == null && r0.Outcome == StoreResult.OutcomeInterrupted && r0.List == null,
                    "list-record: a scan with no store begins a record beside the catalog — no outcome yet, no store");

                StoreResult.BeginList("new.txt", 6);
                bool listed = StoreResult.TryRead(record, out var r1);
                assert(listed && r1.Verb == "scan-list" && r1.List != null && r1.List.Listed == 6 && r1.List.Analyzed == null && r1.ExitCode == null,
                    "list-record: once the list is read the pending record says scan-list and how many were listed, and nothing else yet");

                var tally = StoreResult.List;
                assert(tally != null, "list-record: the list scan has a tally to count into");
                tally?.Analyzed(); tally?.Current(); tally?.Excluded(); tally?.Skipped();
                tally?.Failed(@"D:\Music\bad.mp3", "analysis incomplete: decode-12%");
                // one of the six is never reached (the source went away): it is counted as not examined, not lost
                var done = StoreResult.FinishScan(0);
                bool fin = StoreResult.TryRead(record, out var r2);
                assert(done != null && fin && r2.ExitCode == 0 && r2.Outcome == StoreResult.OutcomeCompleted && r2.FinishedUtc != null,
                    "list-record: the process boundary writes the outcome and the exit code");
                var l = r2.List;
                assert(l != null && l.Analyzed == 1 && l.Current == 1 && l.Excluded == 1 && l.Skipped == 1 && l.Failed == 1 && l.NotFound == 0 && l.NotExamined == 1,
                    $"list-record: every listed path is in exactly one count (analyzed {l?.Analyzed}, current {l?.Current}, excluded {l?.Excluded}, skipped {l?.Skipped}, failed {l?.Failed}, notFound {l?.NotFound}, notExamined {l?.NotExamined})");
                assert(l != null && l.Analyzed + l.Current + l.Excluded + l.Skipped + l.Failed + l.NotFound + l.NotExamined == l.Listed,
                    "list-record: and the counts add up to the number listed");
                assert(l != null && l.Outcomes.Count == 1 && l.Outcomes[0].Outcome == "failed" && l.Outcomes[0].Path == @"D:\Music\bad.mp3" && l.Outcomes[0].Reason.Contains("decode-12%"),
                    "list-record: a failed path is named with its reason, under the same word as its count");
                assert(StoreResult.FinishScan(0) == null && StoreResult.List == null, "list-record: the record is finished exactly once");

                // ---- a stop with no store seals the record as interrupted, with the counts so far
                StoreResult.BeginScanWithoutStore(catalog, null, "2026-11-21T00:00:00.0000000Z");
                StoreResult.BeginList("new.txt", 3);
                StoreResult.List?.NotFound(@"D:\Music\gone.flac", "file not found");
                StoreResult.FinishStoppedScan(null, new ScanSinkReport(), false, null, 130, "stopped by the operator");
                bool stopped = StoreResult.TryRead(record, out var r3);
                assert(stopped && r3.NoStore && r3.Outcome == StoreResult.OutcomeInterrupted && r3.ExitCode == 130 && r3.Reason == "stopped by the operator"
                       && r3.List != null && r3.List.NotFound == 1 && r3.List.NotExamined == 2 && r3.List.Outcomes.Count == 1 && r3.List.Outcomes[0].Outcome == "notFound",
                    $"list-record: a stopped list scan with no store is sealed as interrupted, with what it had counted (outcome {r3.Outcome}, exit {r3.ExitCode}, notFound {r3.List?.NotFound}, notExamined {r3.List?.NotExamined})");
                assert(StoreResult.FinishScan(0) == null, "list-record: and the boundary does not rewrite a sealed record as completed");

                // ---- the named outcomes are bounded; the counts are not
                StoreResult.BeginScanWithoutStore(catalog, null, "2026-11-21T00:00:00.0000000Z");
                StoreResult.BeginList("big.txt", StoreResult.MaxListOutcomes + 5);
                for (int i = 0; i < StoreResult.MaxListOutcomes + 5; i++) StoreResult.List?.NotFound(@"D:\Music\" + i + ".flac", "file not found");
                StoreResult.FinishScan(0);
                bool big = StoreResult.TryRead(record, out var r4);
                assert(big && r4.List != null && r4.List.NotFound == StoreResult.MaxListOutcomes + 5 && r4.List.Outcomes.Count == StoreResult.MaxListOutcomes && r4.List.OutcomesOmitted == 5,
                    $"list-record: a very long list of missing files is counted in full and named up to a limit, with the remainder stated ({r4.List?.Outcomes.Count} named, {r4.List?.OutcomesOmitted} omitted)");

                // ---- with a store in force the same list block rides the store's record
                var store = Path.Combine(dir, "truedat.db");
                StoreResult.BeginScan(store, "2026-11-21T00:00:00.0000000Z");
                StoreResult.BeginList("new.txt", 2);
                bool st = StoreResult.TryRead(record, out var r5);
                assert(st && !r5.NoStore && r5.Verb == "scan-list" && r5.Store.Path.Length > 0 && r5.List != null && r5.List.Listed == 2,
                    "list-record: with a store in force the record keeps its store block and gains the list block");
                StoreResult.ResetScanForTest();

                // ---- a store that comes into force after a no-store begin takes the record over
                StoreResult.BeginScanWithoutStore(catalog, null, "2026-11-21T00:00:00.0000000Z");
                StoreResult.BeginScan(store, "2026-11-21T00:00:00.0000000Z");
                bool over = StoreResult.TryRead(record, out var r6);
                assert(over && !r6.NoStore && r6.Store.Path.Length > 0, "list-record: a store made after the scan began takes the record over — it is the store's from then on");
                StoreResult.ResetScanForTest();

                // ---- no catalog, no record: there is nowhere a reader would look
                StoreResult.BeginScanWithoutStore("", null, "2026-11-21T00:00:00.0000000Z");
                assert(StoreResult.FinishScan(0) == null, "list-record: a run with no catalog target begins no record");
            }
            finally { StoreResult.ResetScanForTest(); try { Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>Through the command line: real list files, a real catalog, and a path that is not ASCII.</summary>
        static void TestCli(Action<bool, string> assert)
        {
            var exe = typeof(Program).Assembly.Location;
            var exeDir = Path.GetDirectoryName(exe) ?? ".";
            if (!File.Exists(Path.Combine(exeDir, "essentia_streaming_extractor_music.exe")))
            {
                Console.WriteLine("  NOT RUN  the run record through a list scan: a scan refuses to start without essentia_streaming_extractor_music.exe beside the test binary");
                return;
            }
            var root = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "listrec-" + Guid.NewGuid().ToString("N"));
            bool keep = false;
            try
            {
                // "música" and "Ñandú – ålder": letters outside ASCII, and a dash outside the Windows code page's ASCII half
                var music = Path.Combine(root, "m\u00FAsica"); Directory.CreateDirectory(music);
                var real = Path.Combine(music, "\u00D1and\u00FA \u2013 \u00E5lder.flac");
                var movie = Path.Combine(music, "clip.mkv"); File.WriteAllBytes(movie, new byte[] { 1, 2, 3 });
                var gone1 = Path.Combine(music, "gone one.flac"); var gone2 = Path.Combine(music, "gone tw\u00F6.flac");
                var listFile = Path.Combine(root, "new.txt");
                var catalog = Path.Combine(root, "mbxmoods.json");
                var record = Path.Combine(root, StoreResultFile.FileName);
                string Q(string v) => "\"" + v + "\"";
                void Check(bool cond, string msg) { if (!cond) keep = true; assert(cond, msg + (cond ? "" : "  [fixture kept: " + root + "]")); }

                var ffmpeg = Path.Combine(exeDir, "ffmpeg.exe");
                bool haveTrack = false;
                if (File.Exists(ffmpeg))
                {
                    RunChild(ffmpeg, music, "-v error -f lavfi -i sine=frequency=440:duration=25 -ac 2 -ar 44100 -y tone.flac", out _);
                    if (File.Exists(Path.Combine(music, "tone.flac"))) { File.Move(Path.Combine(music, "tone.flac"), real); haveTrack = true; }
                }
                if (!haveTrack) Console.WriteLine("  NOT RUN  a listed path outside ASCII is analyzed: needs ffmpeg.exe beside the test binary to make a track");

                var lines = new List<string> { gone1, movie, gone2, gone1 };
                if (haveTrack) { lines.Insert(0, real); lines.Add(real); }
                File.WriteAllLines(listFile, lines, new UTF8Encoding(false));
                int listed = haveTrack ? 4 : 3;

                // ---- no store
                var output = RunChild(exe, root, "--no-config --no-store --file-list " + Q(listFile) + " --moods " + Q(catalog) + " -p 1", out int exit);
                File.WriteAllText(Path.Combine(root, "run1.log"), output);
                bool have = StoreResult.TryRead(record, out var r);
                Check(exit == 0, $"list-record-cli: a list whose only problems are files that are not there exits 0 ({exit})");
                Check(have && r.NoStore && r.Verb == "scan-list", $"list-record-cli: a list scan with no store writes the record beside the catalog (found {have}, verb {(have ? r.Verb : "-")})");
                if (have)
                {
                    var l = r.List;
                    Check(r.ExitCode == 0 && r.Outcome == StoreResult.OutcomeCompleted, $"list-record-cli: and it is finished: completed, exit 0 ({r.Outcome}, {r.ExitCode})");
                    Check(l != null && l.Path == listFile && l.Listed == listed, $"list-record-cli: it names the list and counts each listed path once ({l?.Listed} of {listed})");
                    Check(l != null && l.NotFound == 2 && l.Skipped == 1 && l.Failed == 0 && l.Excluded == 0 && l.NotExamined == 0 && l.Analyzed == (haveTrack ? 1 : 0) && l.Current == 0,
                        $"list-record-cli: two files not there, one that is not audio{(haveTrack ? ", one analyzed" : "")} (analyzed {l?.Analyzed}, current {l?.Current}, notFound {l?.NotFound}, skipped {l?.Skipped}, failed {l?.Failed}, excluded {l?.Excluded}, notExamined {l?.NotExamined})");
                    Check(l != null && l.Outcomes.Count == 2 && l.Outcomes.All(o => o.Outcome == "notFound") && l.Outcomes.Any(o => o.Path == gone1) && l.Outcomes.Any(o => o.Path == gone2),
                        "list-record-cli: the files that were not there are named, non-ASCII name included");
                }
                if (haveTrack)
                {
                    var keys = File.Exists(catalog) ? CatalogKeys(catalog) : new List<string>();
                    Check(keys.Count == 1 && keys[0] == real, $"list-record-cli: the listed path outside ASCII is analyzed and recorded under that exact path ({keys.Count} entr{(keys.Count == 1 ? "y" : "ies")})");

                    // ---- again: the same list is now current
                    RunChild(exe, root, "--no-config --no-store --file-list " + Q(listFile) + " --moods " + Q(catalog) + " -p 1", out int exit2);
                    bool have2 = StoreResult.TryRead(record, out var r2);
                    Check(exit2 == 0 && have2 && r2.List != null && r2.List.Analyzed == 0 && r2.List.Current == 1 && r2.List.NotFound == 2 && r2.List.Skipped == 1,
                        $"list-record-cli: a second run finds the analyzed file current (analyzed {r2.List?.Analyzed}, current {r2.List?.Current})");

                    // ---- one file through --analyze-file: the plain scan verb, still a record
                    RunChild(exe, root, "--no-config --no-store --analyze-file " + Q(real) + " --moods " + Q(catalog), out int exit3);
                    bool have3 = StoreResult.TryRead(record, out var r3);
                    Check(exit3 == 0 && have3 && r3.NoStore && r3.Verb == "scan" && r3.List == null && r3.ExitCode == 0,
                        $"list-record-cli: a single-file scan with no store writes the record too, without a list block (verb {(have3 ? r3.Verb : "-")}, exit {exit3})");

                    // ---- with a store: the store's record carries the list block
                    RunChild(exe, root, "--no-config --store --no-edges --file-list " + Q(listFile) + " --moods " + Q(catalog) + " -p 1", out int exit4);
                    bool have4 = StoreResult.TryRead(record, out var r4);
                    Check(exit4 == 0 && have4 && !r4.NoStore && r4.Verb == "scan-list" && r4.Store.Generation != null && r4.List != null && r4.List.Listed == listed && r4.List.Current == 1,
                        $"list-record-cli: with a store in force the record has both the store's facts and the list's (exit {exit4}, store {(have4 ? (!r4.NoStore).ToString() : "-")}, listed {r4.List?.Listed}, current {r4.List?.Current})");
                }
            }
            catch (Exception ex) { keep = true; assert(false, "list-record-cli: the test itself failed: " + ex.Message + "  [fixture kept: " + root + "]"); }
            finally { if (!keep) { try { Directory.Delete(root, true); } catch { } } }
        }

        static List<string> CatalogKeys(string catalog)
        {
            var keys = new List<string>();
            using var doc = JsonDocument.Parse(File.ReadAllBytes(catalog));
            if (doc.RootElement.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Object)
                foreach (var t in tracks.EnumerateObject()) keys.Add(t.Name);
            return keys;
        }

        internal static string RunChild(string exe, string cwd, string args, out int exitCode)
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
