using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Truedat.Edges;

namespace Truedat.Store
{
    internal sealed class WorkOptions
    {
        public string? StorePath;
        public string Producer = EdgeStore.ProducerKey;
        /// <summary>Stop leasing new items once this much wall time has passed; null = until the queue is empty.</summary>
        public TimeSpan? Budget;
        public int MaxAttempts = 3;
        public TimeSpan Lease = TimeSpan.FromMinutes(10);
        /// <summary>A failed item is not retried before this has passed (test seam: zero).</summary>
        public TimeSpan RetryDelay = TimeSpan.FromHours(1);
        public bool Audit;
    }

    internal sealed class WorkReport
    {
        public string Producer = "", ProducerVersion = "", Store = "";
        public int Enqueued, Leased, Done, Failed, Parked, Unreachable, Remaining, QueueQueued, QueueFailed, QueueParked, QueueDone;
        public long ElapsedMs, BudgetMs, BytesWritten;
        public string Stopped = "";                 // 'empty' | 'budget' | 'error'
        public string? NextEligibleUtc, ResultPath, Error;
        public double ItemsPerHour => ElapsedMs > 0 ? Leased * 3600000.0 / ElapsedMs : 0;
    }

    /// <summary>The C4 producer path for one reachable copy: run the extractor with the frames profile, fold, PCM pass. Never throws
    /// by contract (a failure is a failed <see cref="EdgeFoldResult"/>). The scan wires the real one; tests inject a synthetic one.</summary>
    internal delegate EdgeFoldResult ProduceEdges(string audioPath, long sizeBytes);

    /// <summary>S2 C5 — <c>--work</c>: the drain. Enqueue the gap (entities with a description but no edge row at the current
    /// producer version that have a copy on one of THIS machine's roots), then lease one item at a time inside the budget, run it
    /// through the C4 path, write the rows and its run row, and move it to done / failed (retry after a delay, 3 attempts then
    /// parked). Leases expire, nothing is force-killed, outputs go to <c>work-result.json</c> beside the store. "Done" is a query:
    /// <c>--store-info</c> prints eligible / captured / missing.</summary>
    internal static class WorkCommand
    {
        internal const string ResultFileName = "work-result.json";

        internal static WorkReport Run(WorkOptions o, ProduceEdges produce, string producerVersion)
        {
            var sw = Stopwatch.StartNew();
            var r = new WorkReport { Producer = o.Producer, ProducerVersion = producerVersion, BudgetMs = (long)(o.Budget?.TotalMilliseconds ?? 0) };
            StoreLog.SetVerbose(o.Audit);
            if (o.Producer != EdgeStore.ProducerKey) { r.Stopped = "error"; r.Error = $"--work knows one producer in S2: {EdgeStore.ProducerKey} (got '{o.Producer}')."; return r; }
            try
            {
                var now = DateTime.UtcNow.ToString("o");
                using var os = OpenStore.Open(o.StorePath, create: false, VersionInfo.Display, now);
                var dal = os.Dal; var db = os.Db;
                r.Store = os.Path;
                string machineId = os.Machine.Id;
                string source = $"work:{Environment.MachineName}@{DateTime.UtcNow:o}";

                using (var tx = db.BeginTransaction())
                {
                    dal.EnsureProducer(EdgeStore.ProducerKey, producerVersion, "analysis", needsAudio: true);
                    r.Enqueued = dal.EnqueueMissingEdges(o.Producer, producerVersion, machineId);
                    tx.Complete();
                }
                StoreLog.Info($"work: {r.Enqueued} item(s) enqueued for {producerVersion} on this machine");

                // One attempt per item per run: the lease query's cutoff is the run start, so a failure (or lease) recorded during
                // this run is not eligible again until the next run, whatever the retry delay.
                string runStart = DateTime.UtcNow.ToString("o");
                while (true)
                {
                    if (o.Budget.HasValue && sw.Elapsed >= o.Budget.Value) { r.Stopped = "budget"; break; }
                    string leaseUntil = DateTime.UtcNow.Add(o.Lease).ToString("o");
                    (long Id, string EntityId, int Attempts)? item;
                    using (var tx = db.BeginTransaction()) { item = dal.LeaseNextWork(o.Producer, runStart, leaseUntil, o.MaxAttempts); tx.Complete(); }
                    if (item == null) { r.Stopped = "empty"; break; }
                    r.Leased++;

                    // The copy to read: the first present location on this machine's roots whose file is actually there.
                    string? path = null; long? loc = null; long size = 0;
                    foreach (var c in dal.CopiesOnMachine(item.Value.EntityId, machineId))
                    {
                        try { var fi = new FileInfo(c.DisplayPath); if (fi.Exists) { path = c.DisplayPath; loc = c.LocationId; size = fi.Length; break; } } catch { }
                    }
                    var ck = dal.ContentKeyOf(item.Value.EntityId);
                    string done = DateTime.UtcNow.ToString("o");
                    if (path == null || ck == null)
                    {
                        string why = ck == null ? "no description (track_content row) for this entity" : "no reachable copy of this entity on this machine";
                        r.Unreachable++;
                        Finish(dal, db, item.Value, o, r, false, why, done);
                        continue;
                    }

                    EdgeFoldResult edges;
                    try { edges = produce(path, size); }
                    catch (Exception ex)
                    {
                        edges = new EdgeFoldResult { ProducerVersion = producerVersion };
                        edges.Head.Status = edges.Tail.Status = "failed"; edges.Head.Error = edges.Tail.Error = $"producer threw: {ex.Message}";
                    }
                    edges.ProducerVersion = producerVersion;
                    bool ok;
                    using (var tx = db.BeginTransaction())
                    {
                        string? sha = ck.Value.Kind == "entity" ? null : ck.Value.ContentKey;
                        ok = ImportCommand.WriteEdges(dal, item.Value.EntityId, ck.Value.ContentKey, ck.Value.Kind, sha, loc, edges, source, done, path);
                        if (ok) r.BytesWritten += dal.SumVecBytes(ck.Value.ContentKey, EdgeStore.ProducerKey, producerVersion);
                        tx.Complete();
                    }
                    Finish(dal, db, item.Value, o, r, ok, ok ? null : edges.Head.Error, done);
                    StoreLog.Debug($"work: {(ok ? "done" : "failed")} {path} (attempt {item.Value.Attempts}, fold {edges.FoldMs} ms, pcm {edges.PcmMs} ms)");
                }

                var counts = dal.WorkCounts(o.Producer);
                r.QueueQueued = (int)(counts.TryGetValue("queued", out var q) ? q : 0);
                r.QueueFailed = (int)(counts.TryGetValue("failed", out var f) ? f : 0);
                r.QueueParked = (int)(counts.TryGetValue("parked", out var p) ? p : 0);
                r.QueueDone = (int)(counts.TryGetValue("done", out var d) ? d : 0);
                r.Remaining = r.QueueQueued + r.QueueFailed + (int)(counts.TryGetValue("leased", out var l) ? l : 0);
                r.NextEligibleUtc = dal.NextEligibleWork(o.Producer);
                dal.ResetAll();
                try { db.Exec("PRAGMA wal_checkpoint(TRUNCATE);"); } catch (SqliteException ex) { StoreLog.Warn($"WAL checkpoint did not complete ({ex.Message})"); }
                r.ElapsedMs = sw.ElapsedMilliseconds;
                r.ResultPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(os.Path)) ?? ".", ResultFileName);
                WriteResult(r);
                StoreLog.Info($"work: {r.Leased} leased, {r.Done} done, {r.Failed} failed ({r.Unreachable} unreachable), {r.Parked} parked, {r.Remaining} remaining; stopped: {r.Stopped}; {r.ElapsedMs} ms" +
                              (r.Leased > 0 ? $" ({r.ItemsPerHour:F0} items/h)" : ""));
            }
            catch (Exception ex)
            {
                r.Stopped = "error"; r.Error = ex.Message; r.ElapsedMs = sw.ElapsedMilliseconds;
                StoreLog.Error($"work: {ex.Message}");
            }
            return r;
        }

        static void Finish(StoreDal dal, SqliteDb db, (long Id, string EntityId, int Attempts) item, WorkOptions o, WorkReport r, bool ok, string? why, string nowUtc)
        {
            using var tx = db.BeginTransaction();
            if (ok) { dal.FinishWork(item.Id, "done", null, null); r.Done++; }
            else if (item.Attempts >= o.MaxAttempts) { dal.FinishWork(item.Id, "parked", why, null); r.Parked++; r.Failed++; }
            else { dal.FinishWork(item.Id, "failed", why, DateTime.UtcNow.Add(o.RetryDelay).ToString("o")); r.Failed++; }
            tx.Complete();
        }

        static void WriteResult(WorkReport r)
        {
            var j = new JsonObject
            {
                ["kind"] = "work-result", ["schemaVersion"] = 1, ["generatedAt"] = DateTime.UtcNow.ToString("o"),
                ["store"] = r.Store, ["producer"] = r.Producer, ["producerVersion"] = r.ProducerVersion,
                ["budgetMs"] = r.BudgetMs, ["elapsedMs"] = r.ElapsedMs, ["stopped"] = r.Stopped,
                ["enqueued"] = r.Enqueued, ["leased"] = r.Leased, ["done"] = r.Done, ["failed"] = r.Failed, ["unreachable"] = r.Unreachable, ["parked"] = r.Parked,
                ["remaining"] = r.Remaining, ["queue"] = new JsonObject { ["queued"] = r.QueueQueued, ["failed"] = r.QueueFailed, ["parked"] = r.QueueParked, ["done"] = r.QueueDone },
                ["nextEligibleUtc"] = r.NextEligibleUtc, ["itemsPerHour"] = (decimal)Math.Round(r.ItemsPerHour, 1), ["bytesWritten"] = r.BytesWritten,   // decimal: net48 prints a double with round-trip noise
                ["bytesPerItem"] = r.Done > 0 ? r.BytesWritten / r.Done : 0,
            };
            if (r.ResultPath == null) return;
            var tmp = r.ResultPath + ".tmp";
            File.WriteAllText(tmp, j.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(r.ResultPath)) File.Replace(tmp, r.ResultPath, null); else File.Move(tmp, r.ResultPath);
        }
    }
}
