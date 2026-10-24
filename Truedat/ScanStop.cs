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
    /// </summary>
    internal static class ScanStop
    {
        delegate bool HandlerRoutine(int ctrlType);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetConsoleCtrlHandler(HandlerRoutine handler, bool add);

        const int CtrlC = 0, CtrlBreak = 1, CtrlClose = 2, CtrlLogoff = 5, CtrlShutdown = 6;

        static readonly object _gate = new object();
        static HandlerRoutine? _routine;   // kept alive: the delegate the OS calls back must not be collected
        static Func<string?>? _save;
        static bool _ctrlC;
        static int _fired;

        /// <summary>Arm for one scan. <paramref name="saveNow"/> returns what happened, for the console. <paramref name="handleCtrlC"/>
        /// is for a mode with no Ctrl+C handler of its own (the per-file modes): save, then let the runtime end the process as it did.
        /// The library scan keeps its pause prompt and calls <see cref="Fire"/> itself where it used to abort without saving.</summary>
        internal static void Arm(Func<string?> saveNow, bool handleCtrlC)
        {
            lock (_gate)
            {
                _save = saveNow; _ctrlC = handleCtrlC; _fired = 0;
                if (_routine != null) return;
                _routine = OnCtrl;
                try { SetConsoleCtrlHandler(_routine, true); }
                catch (Exception ex) { Console.Error.WriteLine($"  note: the console close / shutdown hook is unavailable ({ex.Message}); the periodic save still bounds a loss."); }
            }
        }

        /// <summary>The scan's loop is over and its final writes take it from here.</summary>
        internal static void Disarm() { lock (_gate) _save = null; }

        /// <summary>Run the armed save once; later calls do nothing. Returns the save's report, or null when nothing was armed or it already ran.</summary>
        internal static string? Fire()
        {
            Func<string?>? save;
            lock (_gate) save = _save;
            if (save == null || Interlocked.Exchange(ref _fired, 1) != 0) return null;
            try { return save(); }
            catch (Exception ex) { return $"save failed: {ex.Message}"; }
        }

        /// <summary>What a stop saves: the completed analyses the periodic checkpoint has not written yet, through the same checkpoint.</summary>
        internal static string? SaveNow(PeriodicSave p, Func<CheckpointOutcome> checkpoint)
        {
            int pending = p.SinceDurable;
            var outcome = p.SaveNow(checkpoint);
            if (outcome == null)
                return pending == 0 ? "nothing completed since the last save" : "not saved: checkpoints stopped on an earlier failure (see above)";
            return outcome.IsDurable
                ? $"saved {pending} completed analyses"
                : $"save failed at stage {outcome.Stage}: {CheckpointCause.Describe(outcome.Error)}";
        }

        static bool OnCtrl(int type)
        {
            bool close = type == CtrlClose || type == CtrlLogoff || type == CtrlShutdown;
            if (!close && !_ctrlC) return false;   // Ctrl+C / Break: the scan's own handler runs next
            var what = Fire();
            if (what != null) Console.Error.WriteLine($"  {Name(type)}: {what}");
            return close;   // close / logoff / shutdown: handled (the system ends the process when this returns); Ctrl+C: pass along
        }

        static string Name(int type) => type switch
        {
            CtrlC => "Ctrl+C", CtrlBreak => "Ctrl+Break", CtrlClose => "console closed", CtrlLogoff => "logoff", CtrlShutdown => "shutdown", _ => $"control event {type}",
        };
    }
}
