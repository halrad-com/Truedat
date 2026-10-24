using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Truedat.Store
{
    /// <summary>Stop/save integration regressions: real store transactions, publication, and the caller's on-disk result.
    /// Registered through StoreScanSelfTest in --self-test; no audio, subprocesses, or native stop events.</summary>
    internal static class StoreStopSelfTest
    {
        const string A = @"D:\music\a.flac", B = @"D:\music\b.flac", MovedB = @"D:\music\moved\b.flac";
        const string Started = "2026-10-24T00:00:00.0000000Z";
        const int GuardMs = 5000;

        internal static void Run(Action<bool, string> assert)
        {
            Console.WriteLine("Store-stop integration self-test (cache-only durability, failure precedence, final-owned result)");
            try { TestCacheOnlyStopPersistsAndPublishes(assert); }
            catch (Exception ex) { assert(false, $"StoreStop.CacheOnly: threw {ex.GetType().Name}: {ex.Message}"); }
            try { TestPersistentCheckpointFailureRemainsFailed(assert); }
            catch (Exception ex) { assert(false, $"StoreStop.PersistentFailure: threw {ex.GetType().Name}: {ex.Message}"); }
            try { TestFinalOwnedStopReturnsAfterResult(assert); }
            catch (Exception ex) { assert(false, $"StoreStop.FinalOwnedResult: threw {ex.GetType().Name}: {ex.Message}"); }
            try { TestStopAfterFinalStillWritesResult(assert); }
            catch (Exception ex) { assert(false, $"StoreStop.AfterFinalResult: threw {ex.GetType().Name}: {ex.Message}"); }
            foreach (bool afterFinal in new[] { false, true })
            {
                try { TestSecondStopWaitsForResultSeal(assert, afterFinal); }
                catch (Exception ex) { assert(false, $"StoreStop.ConcurrentSeal({afterFinal}): threw {ex.GetType().Name}: {ex.Message}"); }
            }

        }

        static TrackEntry Entry(string title, char sha) => new TrackEntry
        {
            Features = new TrackFeatures { Artist = "A", Title = title, Bpm = 120, Mfcc = new[] { 1.5, -2.25 } },
            LastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), AudioStreamSha256 = new string(sha, 64),
        };

        sealed class Fixture : IDisposable
        {
            internal readonly string Root, Store, Catalog;
            readonly string? _previousStoreDirectory;
            readonly int _previousExitCode;
            internal ScanSinkReport? LastFlush;
            internal ExportReport? LastPublication;
            internal Exception? PublishError;
            internal int Checkpoints;

            internal Fixture()
            {
                Root = Path.Combine(Path.GetTempPath(), "truedat-storestop-selftest-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);
                Store = Path.Combine(Root, "truedat.db"); Catalog = Path.Combine(Root, "mbxmoods.json");
                _previousStoreDirectory = ScanSink.StoreDirectory;
                _previousExitCode = Environment.ExitCode;
                ScanSink.Reset(); StoreResult.ResetScanForTest(); ScanSink.StoreDirectory = Root;
                using (StoreLog.Quiet())
                    ImportCommand.ExecuteEntries(Store, new Dictionary<string, TrackEntry>(PathComparer.Instance)
                    { [A] = Entry("Original A", 'a'), [B] = Entry("Original B", 'b') },
                        @"D:\music", "primary", true, "essentia", "stop-selftest", "fixture", null, null);
                StoreResult.BeginScan(Store, Started);
            }

            internal long Generation()
            {
                using (StoreLog.Quiet())
                using (var os = OpenStore.Open(Store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
                    return os.Dal.Generation();
            }

            // Same store-first sequence as AuthoritativeCheckpoint / final scan publication, with explicit fixture paths.
            internal CheckpointOutcome Checkpoint(bool final = false)
            {
                Checkpoints++;
                using (StoreLog.Quiet())
                {
                    LastFlush = ScanSink.Flush(Store, "stop-selftest", null, final: final);
                    var failure = Program.CheckpointStoreOutcome(LastFlush);
                    if (failure != null) return failure;
                    PublishError = StoreScan.Publish(Store, Catalog, out var published);
                    LastPublication = published;
                    StoreResult.NoteScanExport(published, PublishError);
                    return CheckpointOutcome.Durable; // authority is committed even when publication fails
                }
            }

            internal void Seal(int? code, string reason)
                => StoreResult.FinishStoppedScan(Store, ScanSink.Total, false, null, code, reason);

            public void Dispose()
            {
                ScanStop.EndFinal();
                ScanSink.Reset(); StoreResult.ResetScanForTest(); ScanSink.StoreDirectory = _previousStoreDirectory;
                Environment.ExitCode = _previousExitCode;
                try { Directory.Delete(Root, true); } catch { }
            }
        }

        // Removing force:true from ScanStop.SaveNow loses these completed copy updates despite zero fresh analyses.
        static void TestCacheOnlyStopPersistsAndPublishes(Action<bool, string> assert)
        {
            using (var f = new Fixture())
            {
                long before = f.Generation();
                var refreshed = Entry("Updated cached title", 'a');
                refreshed.LastModified = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
                ScanSink.RecordCacheHit(A, refreshed);
                ScanSink.RecordCacheHit(MovedB, Entry("Original B", 'b'));
                ScanSink.RecordRetire(B);
                var periodic = new PeriodicSave(Stopwatch.StartNew(), 3600, _ => { });
                ScanStop.SaveNow(periodic, () => f.Checkpoint(), 130, f.Seal);

                var reopened = StoreTransfer.ReadEntries(f.Store);
                long committed = f.Generation();
                assert(periodic.SinceDurable == 0 && periodic.Attempts == 1 && f.LastFlush?.Ok == true
                       && f.LastFlush.CacheHitKeys == 2 && f.LastFlush.Retired == 1 && committed == before + 1,
                    $"StoreStop.CacheOnly: zero analyses still commit two cache updates and one retirement once (attempts {periodic.Attempts}, generation {before}->{committed})");
                assert(reopened.TryGetValue(A, out var actual) && actual.Features!.Title == "Updated cached title"
                       && actual.LastModified == refreshed.LastModified && reopened.ContainsKey(MovedB) && !reopened.ContainsKey(B),
                    "StoreStop.CacheOnly: close/reopen retains the refreshed tags/time and moved path, with the old path retired");

                var catalog = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
                Program.LoadExistingMoodsForStore(f.Catalog, catalog);
                bool hasResult = StoreResult.TryRead(StoreResult.PathFor(f.Store), out var record);
                assert(f.PublishError == null && catalog.TryGetValue(A, out var published) && published.Features!.Title == "Updated cached title"
                       && catalog.ContainsKey(MovedB) && !catalog.ContainsKey(B) && hasResult && record.ExitCode == 130
                       && record.Store.Generation == committed && record.Export.Performed && record.Export.Generation == committed
                       && record.Export.Catalog?.Sha256 == Publication.Sha256Hex(f.Catalog),
                    "StoreStop.CacheOnly: published paths/tags and the stopped result's generation/hash agree with reopened data");

                periodic.SaveNow(() => f.Checkpoint(), force: true);
                assert(periodic.Attempts == 2 && f.Generation() == committed && f.LastFlush?.Entries == 0,
                    "StoreStop.CacheOnly: a second empty forced checkpoint does not advance the committed generation");
            }
        }

        // A non-final rollback increments CheckpointFailures but intentionally leaves Total.Ok true. A stop must still report failure.
        static void TestPersistentCheckpointFailureRemainsFailed(Action<bool, string> assert)
        {
            using (var f = new Fixture())
            {
                long before = f.Generation();
                ScanSink.Record(A, Entry("Uncommitted title", 'a'));
                var periodic = new PeriodicSave(Stopwatch.StartNew(), 0, _ => { });
                bool previousFault = ImportCommand.FailRunOutcomeForTest;
                try
                {
                    ImportCommand.FailRunOutcomeForTest = true;
                    periodic.AfterAnalysis(() => f.Checkpoint());
                }
                finally { ImportCommand.FailRunOutcomeForTest = previousFault; }
                assert(periodic.PersistentFailure != null && f.LastFlush?.Ok == false && ScanSink.Total.Ok
                       && ScanSink.Total.CheckpointFailures == 1 && ScanSink.Pending == 1 && f.Generation() == before,
                    "StoreStop.PersistentFailure: real checkpoint rollback retains the pending entry and records a persistent error while Total.Ok stays true");

                ScanStop.Arm(code => ScanStop.SaveNow(periodic, () => f.Checkpoint(), code, f.Seal), handleCtrlC: false);
                ScanStop.Fire(130);
                ScanStop.Fire(null);
                bool hasResult = StoreResult.TryRead(StoreResult.PathFor(f.Store), out var record);
                assert(f.Checkpoints == 1 && periodic.Attempts == 1 && ScanSink.Pending == 1 && f.Generation() == before,
                    "StoreStop.PersistentFailure: stopping and repeated stopping never retry the persistently failed store transaction");
                assert(hasResult && record.ExitCode == 130 && record.Outcome == StoreResult.OutcomeFailed
                       && record.Reason != null && record.Reason.Contains("fail-run-outcome"),
                    $"StoreStop.PersistentFailure: stopped result preserves failed outcome and actual checkpoint cause ({record.Outcome}; {record.Reason})");
            }
        }

        // Setting Settled/sealed before the result write finishes must not release another terminating caller.
        static void TestSecondStopWaitsForResultSeal(Action<bool, string> assert, bool afterFinal)
        {
            using (var f = new Fixture())
            {
                string label = "StoreStop.ConcurrentSeal." + (afterFinal ? "AfterFinal" : "DuringFinal");
                var enteredSeal = new ManualResetEventSlim(false);
                var releaseSeal = new ManualResetEventSlim(false);
                var secondReturned = new ManualResetEventSlim(false);
                var firstReturned = new ManualResetEventSlim(false);
                var previousBarrier = StoreResult.BeforeStoppedSealForTest;
                Exception? ownerError = null, secondError = null, firstError = null;
                int emergencySaves = 0, sealCalls = 0;
                Thread? first = null, owner = null, second = null;
                ScanStop.Arm(_ => { Interlocked.Increment(ref emergencySaves); return "unexpected emergency save"; }, handleCtrlC: false);
                try
                {
                    if (!ScanStop.BeginFinal()) throw new InvalidOperationException("Could not acquire final persistence.");
                    ScanSink.RecordCacheHit(A, Entry("Sealed final title", 'a'));
                    if (!f.Checkpoint(final: true).IsDurable) throw new InvalidOperationException("Fixture final checkpoint failed.");
                    if (afterFinal) ScanStop.EndFinal();
                    else
                    {
                        first = new Thread(() =>
                        {
                            try { ScanStop.Fire(130); }
                            catch (Exception ex) { firstError = ex; }
                            finally { firstReturned.Set(); }
                        }) { IsBackground = true };
                        first.Start();
                        if (!ReachedWaitOrReturned(first, firstReturned) || firstReturned.IsSet)
                            throw new InvalidOperationException("First stop did not join final persistence.");
                    }
                    StoreResult.BeforeStoppedSealForTest = () =>
                    {
                        Interlocked.Increment(ref sealCalls);
                        enteredSeal.Set();
                        if (!releaseSeal.Wait(GuardMs * 3)) throw new TimeoutException("Test sealing barrier was not released.");
                    };
                    owner = new Thread(() =>
                    {
                        try { if (afterFinal) ScanStop.Fire(130); else ScanStop.EndFinal(); }
                        catch (Exception ex) { ownerError = ex; }
                    }) { IsBackground = true };
                    owner.Start();
                    if (!enteredSeal.Wait(GuardMs)) throw new InvalidOperationException("Real result sealing did not reach the barrier.");
                    second = new Thread(() =>
                    {
                        try { ScanStop.Fire(130); }
                        catch (Exception ex) { secondError = ex; }
                        finally { secondReturned.Set(); }
                    }) { IsBackground = true };
                    second.Start();
                    bool observed = ReachedWaitOrReturned(second, secondReturned);
                    assert(observed && !secondReturned.IsSet,
                        label + ": second terminating caller waits while the result write is held");
                    releaseSeal.Set();
                    bool joined = owner.Join(GuardMs) && second.Join(GuardMs) && (first == null || first.Join(GuardMs));
                    bool hasResult = StoreResult.TryRead(StoreResult.PathFor(f.Store), out var record);
                    assert(joined && ownerError == null && secondError == null && firstError == null && sealCalls == 1
                           && emergencySaves == 0 && f.Checkpoints == 1 && hasResult && record.ExitCode == 130
                           && record.Outcome == StoreResult.OutcomeInterrupted && record.Reason != StoreResult.ProvisionalReason && record.Export.Performed
                           && record.Store.Generation == f.Generation() && record.Export.Generation == record.Store.Generation
                           && record.Export.Catalog?.Sha256 == Publication.Sha256Hex(f.Catalog),
                        label + ": releasing the seal completes every caller with one result and no second data writer");
                }
                finally
                {
                    releaseSeal.Set();
                    bool joined = (owner == null || owner.Join(GuardMs)) & (second == null || second.Join(GuardMs));
                    ScanStop.EndFinal();
                    joined &= first == null || first.Join(GuardMs);
                    StoreResult.BeforeStoppedSealForTest = previousBarrier;
                    if (joined)
                    {
                        enteredSeal.Dispose(); releaseSeal.Dispose();
                        secondReturned.Dispose(); firstReturned.Dispose();
                    }
                }
            }
        }

        // Each worker has no wait before its production call: its wait state or return identifies the tested boundary.
        static bool ReachedWaitOrReturned(Thread worker, ManualResetEventSlim returned)
            => SpinWait.SpinUntil(() => returned.IsSet
                || (worker.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, GuardMs);

        // A stop can arrive after EndFinal but before Main.FinishScan seals the provisional result.
        static void TestStopAfterFinalStillWritesResult(Action<bool, string> assert)
        {
            using (var f = new Fixture())
            {
                int emergencySaves = 0;
                var periodic = new PeriodicSave(Stopwatch.StartNew(), 3600, _ => { });
                ScanStop.Arm(code => { emergencySaves++; return ScanStop.SaveNow(periodic, () => f.Checkpoint(), code, f.Seal); }, handleCtrlC: false);
                assert(ScanStop.BeginFinal(), "StoreStop.AfterFinalResult: normal final persistence owns the session");
                ScanSink.RecordCacheHit(A, Entry("Completed final cached title", 'a'));
                var final = f.Checkpoint(final: true);
                ScanStop.EndFinal(); // no stop was waiting when final persistence completed
                ScanStop.Fire(130); // Main.FinishScan has not run yet
                bool hasResult = StoreResult.TryRead(StoreResult.PathFor(f.Store), out var record);
                assert(final.IsDurable && f.PublishError == null && emergencySaves == 0 && f.Checkpoints == 1,
                    "StoreStop.AfterFinalResult: stopping after final publication does not start a second data writer");
                assert(hasResult && record.ExitCode == 130 && record.Outcome == StoreResult.OutcomeInterrupted
                       && record.Reason != StoreResult.ProvisionalReason && record.Export.Performed
                       && record.Store.Generation == f.Generation() && record.Export.Generation == f.LastPublication?.Generation
                       && record.Export.Catalog?.Sha256 == Publication.Sha256Hex(f.Catalog),
                    $"StoreStop.AfterFinalResult: before Main cleanup, disk has the stop exit and final publication facts (exit {record.ExitCode}, outcome {record.Outcome}, published {record.Export.Performed})");
            }
        }

        // Program ends final ownership before Main.FinishScan. A waiting terminating caller must see the result before it returns.
        static void TestFinalOwnedStopReturnsAfterResult(Action<bool, string> assert)
        {
            using (var f = new Fixture())
            {
                var stopReturned = new ManualResetEventSlim(false);
                int emergencySaves = 0;
                var periodic = new PeriodicSave(Stopwatch.StartNew(), 3600, _ => { });
                ScanStop.Arm(code => { emergencySaves++; return ScanStop.SaveNow(periodic, () => f.Checkpoint(), code, f.Seal); }, handleCtrlC: false);
                assert(ScanStop.BeginFinal(), "StoreStop.FinalOwnedResult: normal final persistence owns the session");
                StoreResultFile? atReturn = null;
                Exception? stopError = null;
                var stop = new Thread(() =>
                {
                    try
                    {
                        ScanStop.Fire(130);
                        if (StoreResult.TryRead(StoreResult.PathFor(f.Store), out var record)) atReturn = record;
                    }
                    catch (Exception ex) { stopError = ex; }
                    finally { stopReturned.Set(); }
                }) { IsBackground = true };
                stop.Start();
                try
                {
                    // This thread has no other wait: WaitSleepJoin proves it reached Fire's completion wait.
                    bool reachedWait = SpinWait.SpinUntil(() => stopReturned.IsSet
                        || (stop.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, GuardMs);
                    assert(reachedWait && !stopReturned.IsSet, "StoreStop.FinalOwnedResult: the stop joins the active final write before it returns");
                    ScanSink.RecordCacheHit(A, Entry("Final cached title", 'a'));
                    var final = f.Checkpoint(final: true); // final Flush writes provisional facts; publication updates the pending result
                    assert(final.IsDurable && f.PublishError == null, "StoreStop.FinalOwnedResult: final store commit and publication succeeded");
                    ScanStop.EndFinal(); // real Program ordering: Main.FinishScan has not run yet
                    bool returned = stopReturned.Wait(GuardMs);
                    assert(returned && stopError == null && emergencySaves == 0 && f.Checkpoints == 1,
                        $"StoreStop.FinalOwnedResult: the stop returns without another data writer (returned {returned}, error {stopError?.Message}, emergency saves {emergencySaves})");
                    assert(returned && atReturn != null && atReturn.ExitCode == 130 && atReturn.Outcome == StoreResult.OutcomeInterrupted
                           && atReturn.Reason != StoreResult.ProvisionalReason && atReturn.Export.Performed
                           && atReturn.Store.Generation == f.Generation() && atReturn.Export.Generation == f.LastPublication?.Generation
                           && atReturn.Export.Catalog?.Sha256 == Publication.Sha256Hex(f.Catalog),
                        $"StoreStop.FinalOwnedResult: before a terminating caller returns, disk has its exit and actual publication facts (exit {atReturn?.ExitCode}, outcome {atReturn?.Outcome}, published {atReturn?.Export.Performed})");
                }
                finally
                {
                    ScanStop.EndFinal();
                    if (stop.Join(GuardMs)) stopReturned.Dispose(); // a deadlock failure must not dispose an event a background waiter can still signal
                    StoreResult.FinishScan(0); // ordinary Main cleanup follows the stop-return snapshot above
                }
            }
        }
    }
}
