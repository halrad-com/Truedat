using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Truedat
{
    /// <summary>Self-tests for <see cref="PeriodicSave"/> — the existing rescan rule's checkpoint, shared by the library scan and
    /// <c>--file-list</c> / <c>--folder</c> (T18 defect 1), and its failure handling (keeper 131 / TDA 119-120): durable only when both
    /// halves succeed, the cause classified and logged, ONE retry for a named retryable class, anything else or a second failure
    /// persistent (no further attempts). A short period stands in for 300 s; the CLI receipts measure the real route.</summary>
    internal static class PeriodicSaveSelfTest
    {
        internal static void Run(Action<bool, string> assert)
        {
            Console.WriteLine("Periodic-save self-test (T18 defect 1: the rescan rule — once per period, TryEnter + skip; durable = both halves; one retry for a named cause, else persistent)");
            try
            {
                TestPeriod(assert); TestSkipWhileSaving(assert); TestClassification(assert);
                TestRetryableOnceThenDurable(assert); TestPersistentDiskFull(assert); TestRetryableTwiceIsPersistent(assert);
                TestStoreHalfFailedIsNotDurable(assert); TestThrowingSaveIsAFailure(assert);
                TestCatalogFailureLeavesLiveIntact(assert);
                TestFinalWritesFromActualOutcomes(assert);
                TestRetryWaitsForTheDelay(assert);
                TestHeldCatalogReleasedBeforeTheRetry(assert);
                TestPriorCatalogHalfSurvives(assert);
                TestCompletionDuringSaveIsNotLost(assert);
                TestInPlaceChangeIsCaught(assert);
                TestSaveNowOnStop(assert);
                TestStopHookFiresOnce(assert);
            }
            catch (Exception ex) { assert(false, $"PeriodicSave: threw {ex.GetType().Name}: {ex.Message}"); }
        }

        static void Wait(Stopwatch clock, double secs) { while (clock.Elapsed.TotalSeconds < secs) Thread.Sleep(5); }
        static IOException Win32(int code, string msg) { var e = new IOException(msg); SetHResult(e, unchecked((int)0x80070000) | code); return e; }
        static void SetHResult(Exception e, int hr) => typeof(Exception).GetProperty("HResult")!.GetSetMethod(true)!.Invoke(e, new object[] { hr });
        /// <summary>A save that is always due: period 0.</summary>
        static PeriodicSave Due(out System.Collections.Generic.List<string> log)
        {
            var l = new System.Collections.Generic.List<string>(); log = l;
            return new PeriodicSave(Stopwatch.StartNew(), 0, m => { lock (l) l.Add(m); }, retryDelaySecs: 0);
        }

        static void TestPeriod(Action<bool, string> assert)
        {
            var clock = Stopwatch.StartNew();
            var p = new PeriodicSave(clock, 0.2, _ => { });
            int n = 0;
            Func<CheckpointOutcome> save = () => { n++; return CheckpointOutcome.Durable; };
            assert(!p.AfterAnalysis(save) && n == 0, "before the first period: no save (a scan does not save at its first completion)");
            Wait(clock, 0.25);
            assert(p.AfterAnalysis(save) && n == 1 && p.Saves == 1 && p.SinceDurable == 0, "a completion after one period saves once; nothing is pending after it");
            assert(!p.AfterAnalysis(save) && n == 1 && p.SinceDurable == 1, "the next completion right after a save does not save again (the period restarts at the save's end); it is pending");
            var after = clock.Elapsed.TotalSeconds;
            Wait(clock, after + 0.25);
            assert(p.AfterAnalysis(save) && n == 2 && p.Saves == 2, "one more period later, the next completion saves again");
        }

        /// <summary>Operator 2026-10-23: "a pause and exit, even a terminate, still writes what it has — never lose more than 5 minutes".
        /// A stop (second Ctrl+C, console closed, logoff, shutdown) saves NOW: the period does not apply, a save already running is
        /// waited for rather than skipped, nothing is written twice, and a stage that failed persistently is not tried again.</summary>
        static void TestSaveNowOnStop(Action<bool, string> assert)
        {
            var clock = Stopwatch.StartNew();
            var p = new PeriodicSave(clock, 3600, _ => { });
            int n = 0;
            Func<CheckpointOutcome> save = () => { n++; return CheckpointOutcome.Durable; };
            p.AfterAnalysis(save); p.AfterAnalysis(save);
            var o = p.SaveNow(save);
            assert(n == 1 && o != null && o.IsDurable && p.Saves == 1 && p.SinceDurable == 0,
                $"a stop saves the completed analyses at once, an hour before the period is due (saves {n}, pending after {p.SinceDurable})");
            var again = p.SaveNow(save);
            assert(n == 1 && again == null, $"a stop with nothing completed since the last save writes nothing (saves {n})");

            var p2 = new PeriodicSave(clock, 0, _ => { });
            int m = 0;
            using (var inSave = new ManualResetEventSlim(false))
            using (var release = new ManualResetEventSlim(false))
            {
                var saver = Task.Run(() => p2.AfterAnalysis(() => { inSave.Set(); release.Wait(5000); m++; return CheckpointOutcome.Durable; }));
                inSave.Wait(5000);
                var stop = Task.Run(() => p2.SaveNow(() => { m++; return CheckpointOutcome.Durable; }));
                bool waited = !stop.Wait(150);
                release.Set();
                saver.Wait(5000); stop.Wait(5000);
                assert(waited && m == 1 && p2.Saves == 1,
                    $"a stop during a running checkpoint waits for it instead of skipping, and does not write the same analyses again (waited {waited}, saves {m})");
            }

            var p3 = new PeriodicSave(clock, 0, _ => { }, retryDelaySecs: 0);
            p3.AfterAnalysis(() => CheckpointOutcome.Failed("catalog", new IOException("disk full", unchecked((int)0x80070070))));
            int k = 0;
            var after = p3.SaveNow(() => { k++; return CheckpointOutcome.Durable; });
            assert(p3.PersistentFailure != null && k == 0 && after == null, $"after a persistent failure a stop does not try the failed save again (tried {k})");
        }

        static void TestStopHookFiresOnce(Action<bool, string> assert)
        {
            int n = 0;
            ScanStop.Arm(() => { n++; return "saved"; }, handleCtrlC: false);
            var first = ScanStop.Fire(); var second = ScanStop.Fire();
            assert(first == "saved" && second == null && n == 1, $"the stop hook saves once however many stop signals arrive (ran {n})");
            ScanStop.Disarm();
            ScanStop.Arm(() => { n++; return "saved"; }, handleCtrlC: false);
            ScanStop.Disarm();
            assert(ScanStop.Fire() == null && n == 1, "after the scan's loop disarms it, a stop signal saves nothing (the final writes own the rest)");
        }

        static void TestSkipWhileSaving(Action<bool, string> assert)
        {
            var clock = Stopwatch.StartNew();
            var p = new PeriodicSave(clock, 0.05, _ => { });
            Wait(clock, 0.1);
            using (var inSave = new ManualResetEventSlim(false))
            using (var release = new ManualResetEventSlim(false))
            {
                var saver = Task.Run(() => p.AfterAnalysis(() => { inSave.Set(); release.Wait(5000); return CheckpointOutcome.Durable; }));
                inSave.Wait(5000);
                var sw = Stopwatch.StartNew();
                bool second = p.AfterAnalysis(() => CheckpointOutcome.Durable);
                long waitedMs = sw.ElapsedMilliseconds;
                release.Set();
                bool first = saver.Result;
                assert(first && !second && waitedMs < 1000 && p.Saves == 1,
                    $"a completion while another worker is saving skips at once instead of queueing (first {first}, second {second}, waited {waitedMs} ms)");
            }
        }

        static void TestClassification(Action<bool, string> assert)
        {
            assert(CheckpointCause.IsRetryable(new Store.SqliteException(5, "database is locked")) && CheckpointCause.IsRetryable(new Store.SqliteException(6, "locked"))
                   && CheckpointCause.IsRetryable(new Store.SqliteException(517, "busy snapshot (extended 5)")),
                "retryable: SQLite BUSY 5 / LOCKED 6 (extended codes by their primary)");
            // Operator's NAS run 2026-10-22 (scan-1(10-19)_out.txt): WinRAR held truedat.db open over the share while the last checkpoint ran; SQLite's
            // Windows layer fell back to a read-only handle and the write said "attempt to write a readonly database" (rc 8). The checkpoint
            // classed it permanent and dropped 505 analyses. A READONLY from another process holding the file is as transient as a
            // sharing violation — the zipper finishes — so it gets the same one retry.
            assert(CheckpointCause.IsRetryable(new Store.SqliteException(8, "attempt to write a readonly database"))
                   && CheckpointCause.IsRetryable(new Store.SqliteException(1032, "readonly dbmoved (extended 8)"))
                   && CheckpointCause.IsRetryable(new InvalidOperationException("wrapper", new Store.SqliteException(8, "readonly"))),
                "retryable: SQLite READONLY 8 (another process holding the store open — a zipper, a copy, a backup — made this open read-only), also when wrapped");
            // Review 2026-10-22 F3: the hint names the cause the code actually means — a moved store is not "another process holding it".
            var hint8 = CheckpointCause.Hint(new Store.SqliteException(8, "readonly")) ?? "";
            var hintMoved = CheckpointCause.Hint(new Store.SqliteException(1032, "readonly dbmoved")) ?? "";
            var hintDir = CheckpointCause.Hint(new Store.SqliteException(1544, "readonly directory")) ?? "";
            assert(hint8.Contains("another process") && hintMoved.Contains("moved") && !hintMoved.Contains("another process")
                   && hintDir.Contains("folder") && !hintDir.Contains("another process"),
                $"the READONLY hint follows the extended code (8: '{hint8}'; 1032: '{hintMoved}'; 1544: '{hintDir}')");
            assert(CheckpointCause.IsRetryable(Win32(32, "sharing violation")) && CheckpointCause.IsRetryable(Win32(33, "lock violation"))
                   && CheckpointCause.IsRetryable(new InvalidOperationException("wrapper", Win32(32, "inner sharing violation"))),
                "retryable: Windows sharing 32 / lock 33, also when wrapped");
            assert(!CheckpointCause.IsRetryable(Win32(112, "disk full")) && !CheckpointCause.IsRetryable(new Store.SqliteException(13, "database or disk is full"))
                   && !CheckpointCause.IsRetryable(new UnauthorizedAccessException("denied")) && !CheckpointCause.IsRetryable(new Store.SqliteException(11, "corrupt"))
                   && !CheckpointCause.IsRetryable(new Store.SqliteException(10, "ioerr")) && !CheckpointCause.IsRetryable(new Exception("unknown")) && !CheckpointCause.IsRetryable(null),
                "not retryable: disk full (112 / SQLite 13), access denied, CORRUPT, IOERR, an unknown exception, no exception");
            assert(CheckpointCause.IsDiskFull(Win32(112, "There is not enough space on the disk.")) && CheckpointCause.IsDiskFull(new Store.SqliteException(13, "full"))
                   && CheckpointCause.IsDiskFull(Win32(39, "handle disk full")) && !CheckpointCause.IsDiskFull(Win32(32, "sharing")),
                "disk full is recognised (ERROR_DISK_FULL 112, ERROR_HANDLE_DISK_FULL 39, SQLITE_FULL 13) so its message can name the volume");
            var d = CheckpointCause.Describe(new InvalidOperationException("outer", new Store.SqliteException(13, "database or disk is full")));
            assert(d.Contains("InvalidOperationException") && d.Contains("SQLite rc 13") && d.Contains("database or disk is full"), $"Describe names every level and the native code ({d})");
        }

        static void TestRetryableOnceThenDurable(Action<bool, string> assert)
        {
            var p = Due(out var log);
            int calls = 0;
            bool first = p.AfterAnalysis(() => { calls++; return CheckpointOutcome.Failed("catalog", Win32(32, "the file is in use")); });
            bool second = p.AfterAnalysis(() => { calls++; return CheckpointOutcome.Durable; });
            assert(!first && second && calls == 2 && p.Saves == 1 && p.PersistentFailure == null && p.SinceDurable == 0,
                $"a sharing violation gets ONE retry at the next completion, which succeeds (calls {calls}, saves {p.Saves})");
            assert(log.Count == 1 && log[0].Contains("stage catalog") && log[0].Contains("HResult 0x80070020") && log[0].Contains("ONE retry"),
                $"the failure is logged with stage + native code + the retry decision ({string.Join(" | ", log)})");
        }

        static void TestPersistentDiskFull(Action<bool, string> assert)
        {
            var p = Due(out var log);
            int calls = 0;
            Func<CheckpointOutcome> fail = () => { calls++; return CheckpointOutcome.Failed("catalog", Win32(112, "There is not enough space on the disk."), "volume X:\\ 0 bytes free"); };
            for (int i = 0; i < 5; i++) p.AfterAnalysis(fail);
            assert(calls == 1 && p.Attempts == 1 && p.Saves == 0 && p.PersistentFailure != null && p.PersistentFailure.Stage == "catalog",
                $"a persistent disk-full failure: exactly ONE attempt, then no further checkpoints across later completions (calls {calls})");
            assert(p.SinceDurable == 5, $"the five completions since the last durable checkpoint are counted as at risk ({p.SinceDurable})");
            assert(log.Count == 1 && log[0].Contains("ERROR") && log[0].Contains("Not a retryable cause") && log[0].Contains("HResult 0x80070070") && log[0].Contains("0 bytes free"),
                $"logged once, as persistent, with the code and the volume note ({string.Join(" | ", log)})");
        }

        static void TestRetryableTwiceIsPersistent(Action<bool, string> assert)
        {
            var p = Due(out var log);
            int calls = 0;
            Func<CheckpointOutcome> busy = () => { calls++; return CheckpointOutcome.Failed("store", new Store.SqliteException(5, "database is locked")); };
            for (int i = 0; i < 6; i++) p.AfterAnalysis(busy);
            assert(calls == 2 && p.PersistentFailure != null && log.Count == 2 && log[1].Contains("Second consecutive failure"),
                $"a retryable cause that fails again: exactly TWO attempts (one retry), then persistent (calls {calls}; {string.Join(" | ", log)})");
        }

        static void TestStoreHalfFailedIsNotDurable(Action<bool, string> assert)
        {
            var p = Due(out var log);
            bool saved = p.AfterAnalysis(() => CheckpointOutcome.Failed("store", null, "store checkpoint not written: unable to open database file"));
            assert(!saved && p.Saves == 0 && p.SinceDurable == 1 && p.PersistentFailure != null,
                "the catalog half succeeded but the store half failed: NOT durable, not counted, the completion stays at risk (no exception = not a retryable class)");
        }

        /// <summary>REAL failure, not simulated (TDA 121: before any retry, prove the failed attempt left no partial result): another
        /// handle holds the live catalog exclusively while the checkpoint's catalog half runs. The live bytes must be unchanged and no
        /// temp file left; the actual exception, HResult and its classification are reported as observed, not assumed.</summary>
        static void TestCatalogFailureLeavesLiveIntact(Action<bool, string> assert)
        {
            var dir = Path.Combine(Path.GetTempPath(), "truedat-periodic-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var cat = Path.Combine(dir, "mbxmoods.json");
                var tracks = new System.Collections.Concurrent.ConcurrentDictionary<string, TrackEntry>(StringComparer.OrdinalIgnoreCase);
                tracks[@"D:\music\a.flac"] = new TrackEntry { Features = new TrackFeatures { Artist = "A", Title = "a", Bpm = 120, Mfcc = new[] { 1.5 } }, LastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc) };
                assert(Program.TrySaveCheckpointCatalog(cat, tracks) == null && File.Exists(cat), "fixture: the first checkpoint's catalog half saves");
                var before = File.ReadAllBytes(cat);
                var sidecar = Path.ChangeExtension(cat, ".mbxs");
                var sidecarBefore = File.Exists(sidecar) ? File.ReadAllBytes(sidecar) : new byte[0];
                tracks[@"D:\music\b.flac"] = new TrackEntry { Features = new TrackFeatures { Artist = "B", Title = "b", Bpm = 121, Mfcc = new[] { 2.5 } }, LastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc) };
                CheckpointOutcome? failed;
                using (new FileStream(cat, FileMode.Open, FileAccess.Read, FileShare.None))
                    failed = Program.TrySaveCheckpointCatalog(cat, tracks);
                var after = File.ReadAllBytes(cat);
                var leftovers = Directory.GetFiles(dir);
                assert(failed != null && failed.Stage == "catalog" && failed.Error != null,
                    $"a held live catalog fails the catalog half: observed {CheckpointCause.Describe(failed?.Error)}; retryable by the named classes: {CheckpointCause.IsRetryable(failed?.Error)}");
                assert(System.Linq.Enumerable.SequenceEqual(before, after), "the failed attempt left the live catalog byte-identical");
                var sidecarAfter = File.Exists(sidecar) ? File.ReadAllBytes(sidecar) : new byte[0];
                assert(System.Linq.Enumerable.SequenceEqual(sidecarBefore, sidecarAfter), "the failed attempt left the sidecar byte-identical (it is written only after a successful swap)");
                var names = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(leftovers, f => Path.GetFileName(f)));
                assert(!System.Linq.Enumerable.Any(names, n => n.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)),
                    $"the failed attempt left no temp or backup beside the catalog ({string.Join(", ", names)})");
                assert(Program.TrySaveCheckpointCatalog(cat, tracks) == null && File.ReadAllText(cat).Contains("b.flac"),
                    "once the holder lets go, the same save succeeds and carries both entries (the retry writes the whole catalog once)");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>Keeper 134 / TDA 122: the end-of-scan survivor facts come from what the final writes ACTUALLY did, never from the
        /// stage that failed first; keeper 132: the persistently failed stage is never invoked again. Simulated halves (delegates), not a
        /// process run — the CLI receipts carry the real-route cases.</summary>
        static void TestFinalWritesFromActualOutcomes(Action<bool, string> assert)
        {
            var savedErr = Console.Error; Console.SetError(new StringWriter());
            try
            {
                var catalogFailed = CheckpointOutcome.Failed("catalog", Win32(112, "There is not enough space on the disk."));
                var storeFailed = CheckpointOutcome.Failed("store", new Store.SqliteException(13, "database or disk is full"));
                Store.ScanSinkReport ok = new Store.ScanSinkReport { Ok = true }, bad = new Store.ScanSinkReport { Ok = false, Error = "unable to open database file", ErrorException = new Store.SqliteException(14, "unable to open database file") };
                int catalogCalls = 0, storeCalls = 0;
                Action save = () => catalogCalls++;
                Action saveThrows = () => { catalogCalls++; throw Win32(112, "There is not enough space on the disk."); };

                // Catalog failed persistently, THEN the final store flush fails too: nothing survives — the old code said "the store was written".
                var w1 = Program.RunFinalWrites(catalogFailed, true, save, () => { storeCalls++; return bad; });
                var s1 = Program.SurvivorFacts(w1, 7, 7);
                assert(catalogCalls == 0 && storeCalls == 1 && w1.Catalog == Program.FinalWrite.Skipped && w1.Store == Program.FinalWrite.Failed && s1.Contains("in NEITHER (lost): 7"),
                    $"catalog failed, then the final store flush failed: catalog never retried, store tried once, survivors = nothing, 7 lost ({s1})");
                // Control: catalog failed, store flush succeeds -> the store holds them.
                catalogCalls = storeCalls = 0;
                var w2 = Program.RunFinalWrites(catalogFailed, true, save, () => { storeCalls++; return ok; });
                assert(catalogCalls == 0 && storeCalls == 1 && w2.Store == Program.FinalWrite.Written && Program.SurvivorFacts(w2, 7, 7).Contains("the store was written at the end"),
                    "control: catalog failed, final store flush succeeds -> the store holds this run's analyses");
                // Store failed persistently, catalog final save succeeds -> the catalog holds them; the store keeps OLD values at re-analysed paths.
                catalogCalls = storeCalls = 0;
                var w3 = Program.RunFinalWrites(storeFailed, true, save, () => { storeCalls++; return ok; });
                var s3 = Program.SurvivorFacts(w3, 0, 4);
                assert(catalogCalls == 1 && storeCalls == 0 && w3.Store == Program.FinalWrite.Skipped && s3.StartsWith("the catalog was written") && s3.Contains("at most 4 analyses behind (absent, or still their OLD values)") && s3.Contains("(lost): 0") && !s3.Contains("--store-import"),
                    $"store failed persistently: store never retried, catalog written once; survivors name the stale same-path values and promise no recovery verb ({s3})");
                // Store failed persistently, and the final catalog save throws -> nothing survives; the exception is recorded, not thrown.
                catalogCalls = storeCalls = 0;
                bool threw = false; Program.FinalWrites? w4 = null;
                try { w4 = Program.RunFinalWrites(storeFailed, true, saveThrows, () => { storeCalls++; return ok; }); } catch { threw = true; }
                assert(!threw && w4 != null && catalogCalls == 1 && storeCalls == 0 && w4.Catalog == Program.FinalWrite.Failed && w4.CatalogError != null && Program.SurvivorFacts(w4, 4, 4).Contains("(lost): 4"),
                    "store failed persistently and the final catalog save throws: recorded (not thrown), nothing survives, 4 lost");
                // An unknown-stage failure skips both halves (no blind retry of an unidentified failure).
                catalogCalls = storeCalls = 0;
                var w5 = Program.RunFinalWrites(CheckpointOutcome.Failed("checkpoint", new Exception("?")), true, save, () => { storeCalls++; return ok; });
                assert(catalogCalls == 0 && storeCalls == 0 && w5.Catalog == Program.FinalWrite.Skipped && w5.Store == Program.FinalWrite.Skipped, "an unknown-stage failure: neither half is attempted again");
                // No checkpoint failure: the old sequence, both written once; a throwing catalog save still propagates as before.
                catalogCalls = storeCalls = 0;
                var w6 = Program.RunFinalWrites(null, true, save, () => { storeCalls++; return ok; });
                bool propagated = false;
                try { Program.RunFinalWrites(null, true, saveThrows, () => ok); } catch (IOException) { propagated = true; }
                assert(catalogCalls == 2 && storeCalls == 1 && w6.Catalog == Program.FinalWrite.Written && w6.Store == Program.FinalWrite.Written && propagated,
                    "no checkpoint failure: unchanged — both halves once, and a final catalog save that throws still propagates");
            }
            finally { Console.SetError(savedErr); }
        }

        /// <summary>TDA 123: the ONE retry is the first completion at least the retry delay after the failure — completions inside the delay
        /// make no attempt (simulated clock: a 0.3 s delay stands in for 30 s).</summary>
        static void TestRetryWaitsForTheDelay(Action<bool, string> assert)
        {
            var clock = Stopwatch.StartNew();
            var p = new PeriodicSave(clock, 0, _ => { }, retryDelaySecs: 0.3);
            int calls = 0;
            p.AfterAnalysis(() => { calls++; return CheckpointOutcome.Failed("catalog", Win32(32, "in use")); });
            var failedAt = clock.Elapsed.TotalSeconds;
            for (int i = 0; i < 5; i++) { p.AfterAnalysis(() => { calls++; return CheckpointOutcome.Durable; }); Thread.Sleep(10); }
            bool noneInsideDelay = calls == 1 && clock.Elapsed.TotalSeconds - failedAt < 0.3;
            Wait(clock, failedAt + 0.35);
            bool saved = p.AfterAnalysis(() => { calls++; return CheckpointOutcome.Durable; });
            assert(noneInsideDelay && saved && calls == 2 && p.Saves == 1, $"completions inside the retry delay make no attempt; the first one after it retries once and saves (calls {calls})");
            assert(PeriodicSave.DefaultRetryDelaySecs == 30, "the production retry delay is 30 s");
        }

        /// <summary>REAL held file (TDA 123's fixture): another handle holds the live catalog, the holder lets go after 0.4 s. With the retry
        /// delay (0.6 s) the one retry lands after the release and succeeds; with no delay the retry fires while the file is still held and the
        /// checkpoint goes persistent — the reason the delay exists.</summary>
        static void TestHeldCatalogReleasedBeforeTheRetry(Action<bool, string> assert)
        {
            foreach (var delay in new[] { 0.6, 0.0 })
            {
                var dir = Path.Combine(Path.GetTempPath(), "truedat-periodic-selftest-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    var cat = Path.Combine(dir, "mbxmoods.json");
                    var tracks = new System.Collections.Concurrent.ConcurrentDictionary<string, TrackEntry>(StringComparer.OrdinalIgnoreCase);
                    tracks[@"D:\music\a.flac"] = new TrackEntry { Features = new TrackFeatures { Artist = "A", Title = "a", Bpm = 120, Mfcc = new[] { 1.5 } }, LastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc) };
                    Program.TrySaveCheckpointCatalog(cat, tracks);
                    var clock = Stopwatch.StartNew();
                    var p = new PeriodicSave(clock, 0, _ => { }, retryDelaySecs: delay);
                    Func<CheckpointOutcome> save = () => Program.TrySaveCheckpointCatalog(cat, tracks) ?? CheckpointOutcome.Durable;
                    var holder = new FileStream(cat, FileMode.Open, FileAccess.Read, FileShare.None);
                    var release = Task.Run(() => { Thread.Sleep(400); holder.Dispose(); });
                    p.AfterAnalysis(save);                                   // fails: held
                    var until = Stopwatch.StartNew();
                    while (until.Elapsed.TotalSeconds < 1.2 && p.Saves == 0 && p.PersistentFailure == null) { p.AfterAnalysis(save); Thread.Sleep(20); }   // completions every ~20 ms
                    release.Wait();
                    if (delay > 0)
                        assert(p.Saves == 1 && p.Attempts == 2 && p.PersistentFailure == null,
                            $"held catalog released after 0.4 s, retry delay {delay} s: the one retry lands after the release and is durable (attempts {p.Attempts}, saves {p.Saves})");
                    else
                        assert(p.Saves == 0 && p.Attempts == 2 && p.PersistentFailure != null,
                            $"same fixture with NO retry delay: the retry fires while the file is still held and the checkpoint goes persistent (attempts {p.Attempts})");
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            }
        }

        /// <summary>Keeper 136's trigger, REAL catalog file (the store half is simulated): checkpoint 1 writes the catalog and its store flush
        /// fails BUSY; the one retry writes the catalog again (now with c) and the store fails again -> persistent. Then a completion re-analyses
        /// a.flac with a NEW value and adds d; at the end the catalog swap fails on a held file and the store is skipped. The catalog file holds
        /// a(old), b, c; the survivor text must say so (in neither: the 2 analyses after the last catalog half), not 4 as 3178448 would.</summary>
        static void TestPriorCatalogHalfSurvives(Action<bool, string> assert)
        {
            var dir = Path.Combine(Path.GetTempPath(), "truedat-periodic-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var savedErr = Console.Error; Console.SetError(new StringWriter());
            try
            {
                var cat = Path.Combine(dir, "mbxmoods.json");
                var tracks = new System.Collections.Concurrent.ConcurrentDictionary<string, TrackEntry>(StringComparer.OrdinalIgnoreCase);
                Func<string, double, TrackEntry> entry = (t, bpm) => new TrackEntry { Features = new TrackFeatures { Artist = "A", Title = t, Bpm = bpm, Mfcc = new[] { 1.5 } }, LastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc) };
                var busy = new Store.ScanSinkReport { Ok = false, Error = "database is locked", ErrorException = new Store.SqliteException(5, "database is locked") };
                Func<CheckpointOutcome> save = () => Program.TrySaveCheckpointCatalog(cat, tracks)
                    ?? CheckpointOutcome.Failed("store", busy.ErrorException, "simulated store half: " + busy.Error);
                var p = new PeriodicSave(Stopwatch.StartNew(), 0, _ => { }, retryDelaySecs: 0);
                tracks[@"D:\music\a.flac"] = entry("a", 100);                           // already in the catalog before this run (no analysis)
                tracks[@"D:\music\b.flac"] = entry("b", 101);                           // analysis 1 completes
                p.AfterAnalysis(save);                                                   // catalog half OK (a, b), store BUSY -> one retry
                tracks[@"D:\music\c.flac"] = entry("c", 102);                           // analysis 2
                p.AfterAnalysis(save);                                                   // catalog half OK (a, b, c), store BUSY again -> persistent
                tracks[@"D:\music\a.flac"] = entry("a", 200);                           // analysis 3: same path, NEW value
                p.AfterAnalysis(save);                                                   // persistent: no attempt
                tracks[@"D:\music\d.flac"] = entry("d", 103);                           // analysis 4: a new path
                p.AfterAnalysis(save);                                                   // persistent: no attempt
                assert(p.PersistentFailure != null && p.PersistentFailure.Stage == "store" && p.Attempts == 2 && p.SinceCatalogHalf == 2 && p.SinceDurable == 4,
                    $"store failed twice after successful catalog halves: persistent; catalog half 2 analyses behind, durable 4 behind ({p.SinceCatalogHalf}/{p.SinceDurable})");
                Program.FinalWrites w;
                using (new FileStream(cat, FileMode.Open, FileAccess.Read, FileShare.None))
                    w = Program.RunFinalWrites(p.PersistentFailure, true, () => { var f = Program.TrySaveCheckpointCatalog(cat, tracks); if (f != null) throw f.Error!; }, () => null);
                var text = File.ReadAllText(cat);
                assert(w.Catalog == Program.FinalWrite.Failed && w.Store == Program.FinalWrite.Skipped
                       && text.Contains("c.flac") && !text.Contains("d.flac") && text.Contains("\"bpm\":100") && !text.Contains("\"bpm\":200"),
                    "the real catalog after the run: a (OLD value 100), b, c from the last successful catalog half; d and a's new value absent");
                var missing = Program.NotInCatalogFile(cat, tracks);
                assert(missing.Count == 2 && missing[0].EndsWith("a.flac") && missing[1].EndsWith("d.flac"),
                    $"the exact shortfall, read from the catalog file: a.flac (new value not in it) and d.flac ({string.Join(", ", missing)})");
                var facts = Program.SurvivorFacts(w, missing.Count, p.SinceDurable);
                assert(facts.Contains("2 paths' latest analyses are not in it (absent, or still their OLD values)") && facts.Contains("(lost): 2") && !facts.Contains("(lost): 4"),
                    $"survivor facts count only the 2 analyses after the last catalog half as lost, and name the stale value ({facts})");
            }
            finally { Console.SetError(savedErr); try { Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>Keeper 139's interleaving, executed with a deterministic gate (REAL catalog file; store half simulated, failing SQLITE_FULL
        /// so it is persistent at once): the checkpoint's counters are read, the save is held at a gate BEFORE its snapshot, a second worker
        /// adds e.flac and completes, the gate opens, the snapshot (and the file) include e. Store fails; the final catalog swap fails on a held
        /// file. The counter still says 1 unsaved (it was read before the snapshot); the exact shortfall from the file is 0, and that is what
        /// is reported.</summary>
        static void TestCompletionDuringSaveIsNotLost(Action<bool, string> assert)
        {
            var dir = Path.Combine(Path.GetTempPath(), "truedat-periodic-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var savedErr = Console.Error; Console.SetError(new StringWriter());
            try
            {
                var cat = Path.Combine(dir, "mbxmoods.json");
                var tracks = new System.Collections.Concurrent.ConcurrentDictionary<string, TrackEntry>(StringComparer.OrdinalIgnoreCase);
                Func<string, double, TrackEntry> entry = (t, bpm) => new TrackEntry { Features = new TrackFeatures { Artist = "A", Title = t, Bpm = bpm, Mfcc = new[] { 1.5 } }, LastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc) };
                var full = new Store.SqliteException(13, "database or disk is full");
                var p = new PeriodicSave(Stopwatch.StartNew(), 0, _ => { }, retryDelaySecs: 0);
                using (var atGate = new ManualResetEventSlim(false))
                using (var open = new ManualResetEventSlim(false))
                {
                    Func<CheckpointOutcome> gatedSave = () =>
                    {
                        atGate.Set(); open.Wait(5000);                                  // held BEFORE the snapshot
                        return Program.TrySaveCheckpointCatalog(cat, tracks) ?? CheckpointOutcome.Failed("store", full, "simulated store half");
                    };
                    tracks[@"D:\music\d.flac"] = entry("d", 100);                                // worker 1's analysis
                    var worker1 = Task.Run(() => p.AfterAnalysis(gatedSave));
                    atGate.Wait(5000);
                    tracks[@"D:\music\e.flac"] = entry("e", 101);                                // worker 2 adds + completes while worker 1 is at the gate
                    bool w2 = p.AfterAnalysis(() => CheckpointOutcome.Durable);         // skips: worker 1 holds the checkpoint
                    open.Set();
                    worker1.Wait(5000);
                    assert(!w2 && p.PersistentFailure != null && p.SinceCatalogHalf == 1,
                        $"setup: worker 2 completed during worker 1's save; store failed persistently; the counter (read before the snapshot) still says {p.SinceCatalogHalf} unsaved");
                }
                Program.FinalWrites w;
                using (new FileStream(cat, FileMode.Open, FileAccess.Read, FileShare.None))
                    w = Program.RunFinalWrites(p.PersistentFailure, true, () => { var f = Program.TrySaveCheckpointCatalog(cat, tracks); if (f != null) throw f.Error!; }, () => null);
                var text = File.ReadAllText(cat);
                var missing = Program.NotInCatalogFile(cat, tracks);
                var facts = Program.SurvivorFacts(w, missing.Count, p.SinceDurable);
                assert(w.Catalog == Program.FinalWrite.Failed && text.Contains("e.flac") && missing.Count == 0 && facts.Contains("(lost): 0"),
                    $"e.flac IS in the catalog file; the exact shortfall is 0 and the report says 0 lost, where the counter would have said 1 ({facts})");
            }
            finally { Console.SetError(savedErr); try { Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>TDA 129: an entry changed IN PLACE after its catalog half was written (same instance, new value) must count as not in the
        /// catalog. Compared by the bytes the writer emits against the file on disk, so instance identity cannot hide it.</summary>
        static void TestInPlaceChangeIsCaught(Action<bool, string> assert)
        {
            var dir = Path.Combine(Path.GetTempPath(), "truedat-periodic-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var cat = Path.Combine(dir, "mbxmoods.json");
                var tracks = new System.Collections.Concurrent.ConcurrentDictionary<string, TrackEntry>(StringComparer.OrdinalIgnoreCase);
                var e = new TrackEntry { Features = new TrackFeatures { Artist = "A", Title = "f", Bpm = 100, Mfcc = new[] { 1.5 } }, LastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc) };
                tracks[@"D:\music\f.flac"] = e;
                tracks[@"D:\music\g.flac"] = new TrackEntry { Features = new TrackFeatures { Artist = "A", Title = "g", Bpm = 90, Mfcc = new[] { 1.5 } }, LastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc) };
                assert(Program.TrySaveCheckpointCatalog(cat, tracks) == null && Program.NotInCatalogFile(cat, tracks).Count == 0, "fixture: written; nothing missing");
                tracks[@"D:\music\g.flac"] = new TrackEntry { Features = new TrackFeatures { Artist = "A", Title = "g", Bpm = 90, Mfcc = new[] { 1.5 } }, LastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc) };
                e.Features.Bpm = 150;                                                   // same instance, new value
                var missing = Program.NotInCatalogFile(cat, tracks);
                assert(missing.Count == 1 && missing[0].EndsWith("f.flac"),
                    $"the in-place change counts as not in the catalog; an equal rebuilt entry (a cache hit) does not ({string.Join(", ", missing)})");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        static void TestThrowingSaveIsAFailure(Action<bool, string> assert)
        {
            var p = Due(out var log);
            bool threw = false, saved = true;
            try { saved = p.AfterAnalysis(() => throw new UnauthorizedAccessException("Access to the path is denied.")); } catch { threw = true; }
            assert(!threw && !saved && p.PersistentFailure != null && p.PersistentFailure.Stage == "checkpoint" && log.Count == 1 && log[0].Contains("UnauthorizedAccessException"),
                "a save that throws never escapes into the worker: a failed checkpoint (stage checkpoint), classified and logged");
        }
    }
}
