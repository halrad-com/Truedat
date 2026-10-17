using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Truedat
{
    /// <summary>What one checkpoint attempt achieved. Durable only when BOTH halves succeeded — the catalog save and the store
    /// flush (keeper 131 / TDA 119: <c>FlushStoreSink</c> logs a store failure and returns, so a save callback returning proves
    /// nothing about the store).</summary>
    internal sealed class CheckpointOutcome
    {
        internal static readonly CheckpointOutcome Durable = new CheckpointOutcome(null, null, null);
        /// <summary>Null when durable; otherwise where it failed: <c>catalog</c>, <c>store</c> (contract: only after the SAME attempt's catalog half
        /// succeeded — keeper 136) or <c>checkpoint</c> (an exception from
        /// outside both halves).</summary>
        internal readonly string? Stage;
        internal readonly Exception? Error;
        internal readonly string? Detail;
        CheckpointOutcome(string? stage, Exception? error, string? detail) { Stage = stage; Error = error; Detail = detail; }
        internal bool IsDurable => Stage == null;
        internal static CheckpointOutcome Failed(string stage, Exception? error, string? detail = null) => new CheckpointOutcome(stage, error, detail);
    }

    /// <summary>Cause classification for a failed checkpoint (operator: one retry, and a retry is not a root-cause fix — the actual
    /// cause is identified and logged). Retryable = a NAMED class where waiting for the next checkpoint can change the outcome:
    /// SQLite BUSY (5) / LOCKED (6) — another connection holds the store — and a Windows sharing (32) or lock (33) violation — another
    /// process holds the file. Everything else (disk full, access denied, SQLite FULL / IOERR / CORRUPT, an unknown exception) is
    /// not retried: repeating it cannot change it.</summary>
    internal static class CheckpointCause
    {
        internal static bool IsRetryable(Exception? ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is Store.SqliteException s) { int primary = s.ResultCode & 0xFF; return primary == 5 || primary == 6; }
                if (e is IOException) { int win32 = e.HResult & 0xFFFF; if (win32 == 32 || win32 == 33) return true; }
            }
            return false;
        }

        /// <summary>Disk full (TDA 120 — the commonest real cause, and the action is the operator's, not a retry): Windows
        /// ERROR_DISK_FULL 112 / ERROR_HANDLE_DISK_FULL 39, SQLITE_FULL 13.</summary>
        internal static bool IsDiskFull(Exception? ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is Store.SqliteException s && (s.ResultCode & 0xFF) == 13) return true;
                int win32 = e.HResult & 0xFFFF;
                if ((e.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) && (win32 == 112 || win32 == 39)) return true;
            }
            return false;
        }

        /// <summary>"volume X:\ — N GB free of M GB" for the volume holding <paramref name="path"/>, or null when it cannot be read.</summary>
        internal static string? VolumeNote(string? path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return null;
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root) || root!.StartsWith(@"\\", StringComparison.Ordinal)) return root == null ? null : $"share {root} (free space not readable)";
                var d = new DriveInfo(root);
                return $"volume {root} has {d.AvailableFreeSpace / 1048576.0:N0} MB free of {d.TotalSize / 1048576.0:N0} MB";
            }
            catch { return null; }
        }

        /// <summary>The cause as one line: exception type, SQLite result code or Windows HResult, message — innermost first is not
        /// assumed; every level is named so a wrapped native error is never hidden behind its wrapper.</summary>
        internal static string Describe(Exception? ex)
        {
            if (ex == null) return "no exception (the store reported failure)";
            var parts = new System.Collections.Generic.List<string>();
            for (var e = ex; e != null; e = e.InnerException)
            {
                string code = e is Store.SqliteException s ? $"SQLite rc {s.ResultCode}" : $"HResult 0x{e.HResult:X8}";
                parts.Add($"{e.GetType().Name} ({code}): {e.Message}");
            }
            return string.Join(" <- ", parts);
        }
    }

    /// <summary>
    /// The existing rescan rule (operator: "we already have this rule 5 minutes max of rescan time"): a scan saves its catalog — and the
    /// store rides the same checkpoint — at most once per period (<c>Program.SavePeriodSecs</c>, 300 s), and only from the
    /// analysis-completion path, so the "something new was analysed since the last save" condition is implicit and a pure cache-hit
    /// stretch never rewrites anything. This is the CONFIGURED period, not a proven recovery bound: what a crash costs is every analysis
    /// completed since the last DURABLE checkpoint (up to the period + the save's own duration + the gap to the next completion, since
    /// the save fires on a completion and skips while one is in flight) PLUS every analysis in flight — measured on the real route
    /// (keeper 131 / TDA 119), not asserted here. One implementation for every scan path: MoodsMode had it inline; <c>--file-list</c> /
    /// <c>--folder</c> had none and could lose a whole run (T18 defect 1, TDA 112). TryEnter + skip, not lock (T1, 2026-07-30): a worker
    /// that finishes while a save is in flight goes back to analysis. The period is measured save-end to save-start.
    /// A failed checkpoint is classified (<see cref="CheckpointCause"/>) and logged with its stage and cause; a retryable cause gets ONE
    /// retry (the first completion at least <see cref="DefaultRetryDelaySecs"/> after the failure, no sleep loop); anything else, or a second consecutive failure, is PERSISTENT: no
    /// further checkpoint and no new analysis (the failed stage is not attempted again at the end; the other half writes once) and the
    /// state is reported through <see cref="PersistentFailure"/>.
    /// </summary>
    internal sealed class PeriodicSave
    {
        readonly Stopwatch _clock;
        readonly long _periodTicks;
        readonly Action<string> _log;
        readonly object _lock = new object();
        readonly long _retryDelayTicks;
        long _dueTicks;           // clock ticks before which no attempt is made: a period after the last durable save (or the scan's start),
                                  // or, after a retryable failure, the retry delay after that failure
        long _lastDurableUtcTicks;
        int _saves, _attempts, _consecutiveFailures, _sinceDurable, _sinceCatalogHalf;
        volatile CheckpointOutcome? _persistent;

        /// <summary>The minimum wait before the ONE retry (TDA 123): the next completion can come milliseconds after a failure — before a
        /// holder (an indexer, a reader, another connection) can let go — so the retry is the first completion at least this long after
        /// the failure. Still at a completion; never a sleep loop.</summary>
        internal const double DefaultRetryDelaySecs = 30;

        internal PeriodicSave(Stopwatch clock, double periodSecs, Action<string>? log = null, double retryDelaySecs = DefaultRetryDelaySecs)
        {
            _clock = clock;
            _periodTicks = (long)(periodSecs * Stopwatch.Frequency);
            _retryDelayTicks = (long)(retryDelaySecs * Stopwatch.Frequency);
            _dueTicks = _periodTicks;
            _log = log ?? (m => Console.Error.WriteLine(m));
            _lastDurableUtcTicks = DateTime.UtcNow.Ticks;
        }

        /// <summary>Durable checkpoints taken.</summary>
        internal int Saves => Volatile.Read(ref _saves);
        /// <summary>Checkpoint attempts, durable or not.</summary>
        internal int Attempts => Volatile.Read(ref _attempts);
        /// <summary>Completed analyses since the last durable checkpoint (what a crash now would cost, beside the in-flight ones).</summary>
        internal int SinceDurable => Volatile.Read(ref _sinceDurable);
        /// <summary>Completed analyses since the CATALOG half last succeeded (keeper 136): a checkpoint whose catalog save succeeded and whose
        /// store flush failed is not durable, but the catalog does hold those analyses. Never more than <see cref="SinceDurable"/>.</summary>
        internal int SinceCatalogHalf => Volatile.Read(ref _sinceCatalogHalf);
        /// <summary>Non-null once checkpoints have stopped on a persistent failure: the failure that stopped them.</summary>
        internal CheckpointOutcome? PersistentFailure => _persistent;

        /// <summary>Call after an analysis completes. Attempts a checkpoint when a period has passed since the last durable one and no
        /// other worker is saving; returns true when this call made a DURABLE checkpoint. Never throws: an exception from
        /// <paramref name="save"/> is a failed checkpoint (stage <c>checkpoint</c>), classified like any other.</summary>
        internal bool AfterAnalysis(Func<CheckpointOutcome> save)
        {
            Interlocked.Increment(ref _sinceDurable);
            Interlocked.Increment(ref _sinceCatalogHalf);
            if (_persistent != null) return false;
            if (_clock.ElapsedTicks < Interlocked.Read(ref _dueTicks)) return false;
            if (!Monitor.TryEnter(_lock)) return false;
            try
            {
                if (_persistent != null || _clock.ElapsedTicks < Interlocked.Read(ref _dueTicks)) return false;
                Interlocked.Increment(ref _attempts);
                int pending = Volatile.Read(ref _sinceDurable), pendingCatalog = Volatile.Read(ref _sinceCatalogHalf);
                CheckpointOutcome outcome;
                try { outcome = save() ?? CheckpointOutcome.Failed("checkpoint", null, "the save returned no outcome"); }
                catch (Exception ex) { outcome = CheckpointOutcome.Failed("checkpoint", ex); }
                if (outcome.IsDurable)
                {
                    _consecutiveFailures = 0;
                    Interlocked.Increment(ref _saves);
                    Interlocked.Add(ref _sinceDurable, -pending);
                    Interlocked.Add(ref _sinceCatalogHalf, -pendingCatalog);
                    Interlocked.Exchange(ref _lastDurableUtcTicks, DateTime.UtcNow.Ticks);
                    Interlocked.Exchange(ref _dueTicks, _clock.ElapsedTicks + _periodTicks);   // period measured save-end to save-start
                    return true;
                }
                // The callers run the catalog half first and return a "store" outcome only after it succeeded, so a store-stage failure
                // means the catalog DOES hold this checkpoint's analyses (keeper 136).
                if (outcome.Stage == "store") Interlocked.Add(ref _sinceCatalogHalf, -pendingCatalog);
                _consecutiveFailures++;
                bool retry = _consecutiveFailures == 1 && CheckpointCause.IsRetryable(outcome.Error);
                var since = new DateTime(Interlocked.Read(ref _lastDurableUtcTicks), DateTimeKind.Utc).ToString("o");
                var facts = $"stage {outcome.Stage}; cause {CheckpointCause.Describe(outcome.Error)}{(outcome.Detail != null ? "; " + outcome.Detail : "")}; " +
                            $"attempt {_consecutiveFailures}; last durable checkpoint {since}; {pending} analyses completed since it";
                if (retry)
                {
                    Interlocked.Exchange(ref _dueTicks, _clock.ElapsedTicks + _retryDelayTicks);
                    _log($"  WARNING: checkpoint failed ({facts}). Retryable cause: ONE retry at the first completed analysis at least {_retryDelayTicks / (double)Stopwatch.Frequency:0.#} s from now.");
                }
                else
                {
                    _persistent = outcome;
                    _log($"  ERROR: checkpoint failed ({facts}). " +
                         (_consecutiveFailures > 1 ? "Second consecutive failure" : "Not a retryable cause") +
                         ": no further checkpoints this run and no new analysis; the failed stage is not attempted again at the end.");
                }
                return false;
            }
            finally { Monitor.Exit(_lock); }
        }
    }
}
