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
    /// Active → FinalOwned → Settled. The save itself runs outside the gate; the gate holds only the state. A stop that joins or follows
    /// the final writes gets the scan's record SEALED before it is released (the process ends past Main's finally after it), and a second
    /// caller arriving while that seal is being written waits for it too (codex ConcurrentSeal acceptance).
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
        static string _finalResult = FinalSavedIt;               // what the final writes did, for every stop that joins or follows them
        static Exception? _finalError;                           // the final writes' failure, so a later seal says FAILED with its cause (C4)
        static ManualResetEventSlim? _sealEvent;                 // set when a seal started AFTER the final writes has been written
        /// <summary>Self-test seams: fired between EndFinal's two gate holds (the window codex recheck C3 names); seals attempted so far.</summary>
        internal static Action? BetweenFinalLocksForTest;
        internal static int SealsForTest;

        /// <summary>Arm for one scan. <paramref name="saveNow"/> takes the exit code the caller knows (130 from the scan's own Ctrl+C
        /// handler; null for a native close, which the OS ends) and returns what happened, for the console. <paramref name="handleCtrlC"/>
        /// is for a mode with no Ctrl+C handler of its own (the per-file modes): save, then let the runtime end the process as it did.
        /// The library scan keeps its pause prompt and calls <see cref="Fire"/> itself where it used to abort without saving.</summary>
        internal static void Arm(Func<int?, string?> saveNow, bool handleCtrlC)
        {
            lock (_gate)
            {
                _save = saveNow; _ctrlC = handleCtrlC; _state = State.Active; _result = null; _stopJoinedFinal = false; _joinedExit = null;
                _finalSettled = false; _sealed = false; _finalResult = FinalSavedIt; _finalError = null;
                _sealEvent?.Dispose(); _sealEvent = null;
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
        /// False = a stop already owns it (waited for until it settled): the stop saved, the final writes stand down and write nothing.
        /// A scan that never armed a session (a per-file run with no catalog to save) owns its own end: true.</summary>
        internal static bool BeginFinal()
        {
            ManualResetEventSlim? done; State s;
            lock (_gate)
            {
                if (_done == null) return true;   // never armed (codex Cli.Unarmed): no session, the caller owns its writes
                s = _state; done = _done;
                if (s == State.Active) { _state = State.FinalOwned; return true; }
            }
            if (s == State.StopOwned) { done?.Wait(); return false; }
            // FinalOwned again: true, because each scan mode calls BeginFinal exactly once (library and per-file), so a second call in one
            // session cannot come from a second writer — only from the same caller re-asking. Settled: nothing to redo (labs 026).
            return s == State.FinalOwned;
        }

        /// <summary>The final writes are done — or threw (<paramref name="error"/>): the session settles and a stop arriving now (or later)
        /// gets that answer. A stop that JOINED the final writes is about to end the process without Main's finally, so the scan's record
        /// is sealed here first — the final flush's facts and publication, interrupted (failed when the writes failed or the scan noted a
        /// failure), the exit code the stop knows — and only then is that caller released (codex R7 acceptance). The seal happens while
        /// the session is still FinalOwned, so a stop arriving mid-seal waits for it as well.</summary>
        internal static void EndFinal(Exception? error = null)
        {
            ManualResetEventSlim? done;
            lock (_gate)
            {
                if (_done == null) return;   // never armed
                if (_state != State.FinalOwned) { _save = null; return; }   // a stop owned it, or already ended
                _finalError = error;   // kept for a stop that arrives after the settle (codex recheck C4)
                _finalResult = error == null ? FinalSavedIt : $"the scan's final writes failed ({error.Message}); the periodic saves hold what they wrote";
            }
            BetweenFinalLocksForTest?.Invoke();
            // Seal for every stop that joined — including one that joins while a seal is being written — and only then settle. The
            // decision "nothing to seal" and the transition out of FinalOwned happen under ONE gate hold (codex recheck C3: a snapshot
            // taken earlier let a stop that joined in between be released without its record).
            while (true)
            {
                bool sealForStop; int? exit;
                lock (_gate)
                {
                    sealForStop = _stopJoinedFinal && !_sealed; exit = _joinedExit;
                    if (sealForStop) _sealed = true;
                    else { _state = State.Settled; _finalSettled = true; _result ??= _finalResult; done = _done; _save = null; break; }
                }
                SealAfterFinal(exit, error, "during");
            }
            done?.Set();
        }

        /// <summary>The scan's record sealed for a stop that joined (or followed) the final writes — before that caller is released, since
        /// the process ends without Main's finally after it. A failed final write seals FAILED with its cause. Never throws.</summary>
        static void SealAfterFinal(int? exitCode, Exception? error, string when)
        {
            Interlocked.Increment(ref SealsForTest);
            try
            {
                string reason;
                if (error != null)
                {
                    reason = $"the scan's final writes failed ({error.Message}); the process was stopped {when} them";
                    global::Truedat.Store.StoreResult.NoteScanOutcome(global::Truedat.Store.StoreResult.OutcomeFailed, reason);
                }
                else reason = FinalSavedIt + $"; the process was stopped {when} them";
                global::Truedat.Store.StoreResult.FinishStoppedAfterFinal(exitCode, reason);
            }
            catch (Exception ex) { Console.Error.WriteLine($"  note: the scan record was not sealed for the stop ({ex.Message})"); }
        }

        /// <summary>A stop signal. The first one in an Active session runs the armed save and settles the session; every later caller
        /// — a second Ctrl+C, a close during the save, a close during the final writes — WAITS for the owner and returns its answer.
        /// Returns null only when nothing was ever armed. <paramref name="exitCode"/> is what the caller knows (130 from the scan's
        /// own handler; null when the OS or the runtime decides).</summary>
        internal static string? Fire(int? exitCode)
        {
            Func<int?, string?>? save = null; ManualResetEventSlim? done; State s; bool sealNow = false; ManualResetEventSlim? sealWait = null;
            lock (_gate)
            {
                if (_done == null) return null;   // never armed
                s = _state; done = _done;
                if (s == State.Active) { _state = State.StopOwned; save = _save; }
                else if (s == State.FinalOwned) { _stopJoinedFinal = true; _joinedExit ??= exitCode; }   // the strongest known exit stays
                else if (s == State.Settled && _finalSettled)
                {
                    // A stop AFTER the final writes, before Main's finally: the first one seals the record; a second one arriving while
                    // that seal is being written waits for it (codex ConcurrentSeal.AfterFinal) — never returns ahead of the disk.
                    if (!_sealed) { _sealed = true; _sealEvent = new ManualResetEventSlim(false); sealNow = true; }
                    else sealWait = _sealEvent;
                }
            }
            if (s == State.Active)
            {
                string? r;
                try { r = save?.Invoke(exitCode) ?? "nothing to save"; }
                catch (Exception ex) { r = $"save failed: {ex.Message}"; }
                lock (_gate) { _result = r; _state = State.Settled; }
                done!.Set();
                return r;
            }
            if (sealNow)
            {
                // The process ends past Main's finally after this returns, so the record is sealed here with the final flush's facts and the
                // exit code the stop knows.
                Exception? finalError; lock (_gate) finalError = _finalError;
                SealAfterFinal(exitCode, finalError, "after");   // C4: a final write that threw seals FAILED with its cause, not "saved it"
                _sealEvent?.Set();
            }
            else if (sealWait != null) sealWait.Wait();
            if (s == State.StopOwned || s == State.FinalOwned) done!.Wait();
            lock (_gate) return _result ?? _finalResult;
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
