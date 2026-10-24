using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Truedat
{
    /// <summary>
    /// Operator 2026-10-23: "a pause and exit, even a terminate, still writes what it has — the rule is never lose more than 5 minutes".
    /// The periodic checkpoint keeps that rule for what no code can see coming (TerminateProcess, power loss): at most one period of
    /// completed analyses plus the tracks in flight. Everything that CAN be seen saves now instead: the console window closed (End task,
    /// the X button), logoff, shutdown, and a second Ctrl+C. One save, through the scan's own checkpoint (<see cref="PeriodicSave.SaveNow"/>),
    /// so a checkpoint already running is waited for and nothing is written twice. Windows gives a close handler about five seconds; with a
    /// store the authority commits first and the files republish from it on the next scan, so the catalog write finishing is not what the
    /// bound rests on.
    ///
    /// One SESSION per scan (codex rescan 2026-10-23 R4/R5): persistence at the end has exactly one owner — the first stop signal, or the
    /// scan's own final writes — and every other party waits for that owner and takes its answer. A `fired` latch used to let a second
    /// terminating signal return at once while the first was still saving (Ctrl+C then passed to termination; close/shutdown returned
    /// "handled" and the system ended the process under the save), and the loop's end disarmed the hook and started the final writes
    /// while a close handler could still be saving — two writers on the same temp paths. States: Active → StopOwned → Settled, or
    /// Active → FinalOwned → Settled. The save itself runs outside the gate; the gate holds only the state.
    /// </summary>
    internal static class ScanStop
    {
        delegate bool HandlerRoutine(int ctrlType);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetConsoleCtrlHandler(HandlerRoutine handler, bool add);

        const int CtrlC = 0, CtrlBreak = 1, CtrlClose = 2, CtrlLogoff = 5, CtrlShutdown = 6;
        const string FinalSavedIt = "the scan's final writes saved it";

        enum State { Active, StopOwned, FinalOwned, Settled }

        static readonly object _gate = new object();
        static HandlerRoutine? _routine;   // kept alive: the delegate the OS calls back must not be collected
        static Func<int?, string?>? _save;
        static bool _ctrlC;
        static State _state = State.Settled;
        static ManualResetEventSlim? _done;   // set once the session settles, whoever settled it
        static string? _result;
        static bool _stopJoinedFinal; static int? _joinedExit;   // a stop that arrived during FinalOwned, and the exit code it knows
        static bool _finalSettled, _sealed;                      // the final writes settled the session; the record was sealed for a stop

        /// <summary>Arm for one scan. <paramref name="saveNow"/> takes the exit code the caller knows (130 from the scan's own Ctrl+C
        /// handler; null for a native close, which the OS ends) and returns what happened, for the console. <paramref name="handleCtrlC"/>
        /// is for a mode with no Ctrl+C handler of its own (the per-file modes): save, then let the runtime end the process as it did.
        /// The library scan keeps its pause prompt and calls <see cref="Fire"/> itself where it used to abort without saving.</summary>
        internal static void Arm(Func<int?, string?> saveNow, bool handleCtrlC)
        {
            lock (_gate)
            {
                _save = saveNow; _ctrlC = handleCtrlC; _state = State.Active; _result = null; _stopJoinedFinal = false; _joinedExit = null; _finalSettled = false; _sealed = false;
                _done?.Dispose(); _done = new ManualResetEventSlim(false);
                if (_routine != null) return;
                _routine = OnCtrl;
                try
                {
                    // The registration's Boolean is a failure mode too, not only a throw (plan v1 Task 3).
                    if (!SetConsoleCtrlHandler(_routine, true))
                        Console.Error.WriteLine($"  note: the console close / shutdown hook could not be registered (error {Marshal.GetLastWin32Error()}); the periodic save still bounds a loss.");
                }
                catch (Exception ex) { Console.Error.WriteLine($"  note: the console close / shutdown hook is unavailable ({ex.Message}); the periodic save still bounds a loss."); }
            }
        }

        /// <summary>The scan's loop is over: the final writes ask to own persistence. True = they do, run them and call <see cref="EndFinal"/>.
        /// False = a stop already owns it (waited for until it settled): the stop saved, the final writes stand down and write nothing.</summary>
        internal static bool BeginFinal()
        {
            ManualResetEventSlim? done; State s;
            lock (_gate)
            {
                s = _state; done = _done;
                if (s == State.Active) { _state = State.FinalOwned; return true; }
            }
            if (s == State.StopOwned) { done?.Wait(); return false; }
            return s == State.FinalOwned;   // Settled: nothing to redo
        }

        /// <summary>The final writes are done: the session settles and a stop arriving now (or later) gets that answer. A stop that JOINED the
        /// final writes is about to end the process without Main's finally, so the scan's record is sealed here first — the final
        /// flush's facts and publication, interrupted, the exit code the stop knows — before that caller is released (codex R7
        /// acceptance: "before a terminating caller returns, disk has its exit and actual publication facts").</summary>
        internal static void EndFinal()
        {
            ManualResetEventSlim? done; bool sealForStop; int? exit;
            lock (_gate)
            {
                sealForStop = _state == State.FinalOwned && _stopJoinedFinal && !_sealed; exit = _joinedExit;
                if (sealForStop) _sealed = true;
                if (_state == State.FinalOwned) { _state = State.Settled; _finalSettled = true; _result ??= FinalSavedIt; }
                done = _done; _save = null;
            }
            if (sealForStop) SealAfterFinal(exit, "during");
            done?.Set();
        }

        /// <summary>The scan's record sealed for a stop that joined (or followed) the final writes — before that caller is released, since
        /// the process ends without Main's finally after it. Never throws.</summary>
        static void SealAfterFinal(int? exitCode, string when)
        {
            try { global::Truedat.Store.StoreResult.FinishStoppedAfterFinal(exitCode, FinalSavedIt + $"; the process was stopped {when} them"); }
            catch (Exception ex) { Console.Error.WriteLine($"  note: the scan record was not sealed for the stop ({ex.Message})"); }
        }

        /// <summary>A stop signal. The first one in an Active session runs the armed save and settles the session; every later caller
        /// — a second Ctrl+C, a close during the save, a close during the final writes — WAITS for the owner and returns its answer.
        /// Returns null only when nothing was ever armed. <paramref name="exitCode"/> is what the caller knows (130 from the scan's
        /// own handler; null when the OS or the runtime decides).</summary>
        internal static string? Fire(int? exitCode)
        {
            Func<int?, string?>? save = null; ManualResetEventSlim? done; State s; bool sealNow = false;
            lock (_gate)
            {
                if (_done == null) return null;   // never armed
                s = _state; done = _done;
                if (s == State.Active) { _state = State.StopOwned; save = _save; }
                else if (s == State.FinalOwned) { _stopJoinedFinal = true; _joinedExit ??= exitCode; }   // the strongest known exit stays
                else if (s == State.Settled && _finalSettled && !_sealed) { _sealed = true; sealNow = true; }   // a stop AFTER the final writes, before Main's finally
            }
            // The final writes settled the session and this is the first stop since: the process ends past Main's finally after this
            // returns, so the record is sealed here with the final flush's facts and the exit code the stop knows.
            if (sealNow) SealAfterFinal(exitCode, "after");
            if (s == State.Active)
            {
                string? r;
                try { r = save?.Invoke(exitCode) ?? "nothing to save"; }
                catch (Exception ex) { r = $"save failed: {ex.Message}"; }
                lock (_gate) { _result = r; _state = State.Settled; }
                done!.Set();
                return r;
            }
            if (s == State.StopOwned || s == State.FinalOwned) done!.Wait();
            lock (_gate) return _result ?? FinalSavedIt;
        }

        /// <summary>What a stop saves: ONE forced checkpoint — the completed analyses the periodic checkpoint has not written yet AND the
        /// cache-only updates no counter sees (codex rescan R6: "nothing completed since the last save" skipped a tag-refresh pass's
        /// changes). A checkpoint already running is waited for; a persistently failed stage is not retried. Then the scan's record is
        /// sealed with what happened (<paramref name="seal"/>, codex R7) before the process ends. The report is what THIS save did,
        /// read after the lock — never a count sampled before the wait (labs 2026-10-24 finding 3).</summary>
        internal static string? SaveNow(PeriodicSave p, Func<CheckpointOutcome> checkpoint, int? exitCode, Action<int?, string>? seal)
        {
            var outcome = p.SaveNow(checkpoint, force: true);
            string report;
            if (outcome == null)
            {
                // force: null only on a persistent failure. The record says FAILED with the checkpoint's own cause (codex StoreStop
                // acceptance): the pending work is not on disk, and "interrupted" would read as saved.
                var pf = p.PersistentFailure;
                report = "not saved: checkpoints stopped on an earlier failure" + (pf != null ? $" (stage {pf.Stage}: {CheckpointCause.Describe(pf.Error)})" : " (see above)");
                global::Truedat.Store.StoreResult.NoteScanOutcome(global::Truedat.Store.StoreResult.OutcomeFailed, report);
            }
            else if (outcome.IsDurable)
            {
                int n = p.LastAttemptPending;
                report = n > 0 ? $"saved {n} completed analyses" : "saved: completed cache updates (no new analyses since the last save)";
            }
            else
            {
                report = $"save failed at stage {outcome.Stage}: {CheckpointCause.Describe(outcome.Error)}";
                global::Truedat.Store.StoreResult.NoteScanOutcome(global::Truedat.Store.StoreResult.OutcomeFailed, report);
            }
            try { seal?.Invoke(exitCode, report); }
            catch (Exception ex) { report += $" (the scan record was not written: {ex.Message})"; }
            return report;
        }

        static bool OnCtrl(int type)
        {
            bool close = type == CtrlClose || type == CtrlLogoff || type == CtrlShutdown;
            if (!close && !_ctrlC) return false;   // Ctrl+C / Break: the scan's own handler runs next
            var what = Fire(null);   // the system or the runtime ends the process after this: the exit code is not ours
            if (what != null) Console.Error.WriteLine($"  {Name(type)}: {what}");
            return close;   // close / logoff / shutdown: handled (the system ends the process when this returns); Ctrl+C: pass along
        }

        static string Name(int type) => type switch
        {
            CtrlC => "Ctrl+C", CtrlBreak => "Ctrl+Break", CtrlClose => "console closed", CtrlLogoff => "logoff", CtrlShutdown => "shutdown", _ => $"control event {type}",
        };
    }
}
