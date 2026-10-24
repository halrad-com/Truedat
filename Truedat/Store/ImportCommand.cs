using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Truedat.Store
{
    internal sealed class ImportOptions
    {
        public string? StorePath;
        public string? CatalogPath;
        public string? LibraryRoot;
        public string RootRole = "primary";
        public bool MachineAdopt;
        public bool Audit;
        /// <summary>Entries per transaction. A kill leaves whole batches (review-focus 4).</summary>
        public int BatchSize = 5000;
        /// <summary>Test seam: throw after this many entries to prove batch atomicity. 0 = never.</summary>
        public int FailAfterEntries;
    }

    internal sealed class ImportReport
    {
        public string Catalog = ""; public string CatalogSha256 = ""; public long CatalogBytes;
        public List<string> Roots = new List<string>();
        public int Entries, Unrooted, EntitiesCreated, MatchedHash, MatchedPath, CreatedNoSha, Locations, LocationsNew, LegacySha, LegacyShaUnrecoverable, Conflicts;
        /// <summary>T18: store locations this write retired (a scan's moved files' old paths).</summary>
        public int Retired;
        public int ErrorsRows, SkippedRows, ReviewRows, VerifyRows, ReceiptRows;
        /// <summary>Ledger rows whose path is under no root (stream URLs, videos outside the library) — counted, not imported.</summary>
        public int LedgerUnrooted;
        /// <summary>Entries whose lastModified is older than the store's for the same content key — skipped, never regress a row.</summary>
        public int Stale;
        /// <summary>Entries that arrived with a content value differing beyond noise from the stored description under the SAME
        /// producer — recorded as value-drift conflicts, the stored description untouched (S2 C2).</summary>
        public int Drift;
        /// <summary>store-v3 T07 (keeper 099): entries whose write changed something an export or a store reader can see — a new or
        /// changed location (entity, display path, mtime, state), a description or copy fact written, a drift conflict, edge rows,
        /// an artifact row. Run rows, last-seen stamps and failure rows are bookkeeping and do not count. The batch loop advances
        /// the generation once inside each committed transaction this grew in.</summary>
        public int VisibleChanges;
        /// <summary>Entries that wrote nothing: description and copy facts already equal (within noise) — no row churn (S2 C2).</summary>
        public int Unchanged;
        /// <summary>Entries whose description was replaced because it arrived from a DIFFERENT producer/version (the old run stays as history).</summary>
        public int ContentReplaced;
        /// <summary>Entries whose media gained a new content hash (a key move): the description is re-keyed and rewritten from the arrival.</summary>
        public int ContentRekeyed;
        /// <summary>S2 C4: edge rows written (an 'edges' run row + the packed rows) / folds that failed (a failed run row, no rows).</summary>
        public int Edges, EdgesFailed;
        /// <summary>Keeper 099: edge rows written WITH arrays only because the track already held them while the scan's array flag was
        /// off ("sticky per track") — the store-result's capture.stickyRowsRefreshed.</summary>
        public int StickyRowsRefreshed;
        /// <summary>Operator 2026-10-22: edge/segment/value rows at a SUPERSEDED producer version removed because the current version now
        /// holds the track (EdgeStore.PruneSupersededVersions) — the store-result's detail.supersededPruned.</summary>
        public int SupersededPruned;
        /// <summary>Edge spills that could not be read back at write time — counted in EdgesFailed too, and separately because they are a
        /// store-side loss, not an audio condition.</summary>
        public int SpillsLost;
        /// <summary>Entries where a stored NULL content column gained a value under the same producer — written as a fill, never a conflict (review #4).</summary>
        public int Filled;
        /// <summary>Operator 2026-10-22 (accounting): the remaining buckets, so that every entry lands in exactly one. Described = the media's
        /// FIRST description; Edited = a verb's exact edit (<see cref="ExactArrival"/>) that changed the row; Confirmed = a fresh measurement from
        /// a DIFFERENT producer that agrees within noise (the row is re-stamped to the measuring run with the fresh values — the description
        /// is the scan's now, not the import's); CopyOnly = only copy facts (mtime, size, tags) were written; Unstorable = rejected before any
        /// mutation (one failed run). <see cref="Accounted"/> sums them with the older buckets and must equal <see cref="Entries"/>.</summary>
        public int Described, Edited, Confirmed, CopyOnly, Unstorable;
        /// <summary>Every entry the batch received, by what happened to it — exactly one bucket each. Equals <see cref="Entries"/> when the
        /// accounting is whole; the self-test pins it and the scan warns when it is not.</summary>
        public long Accounted => Unrooted + Unstorable + Stale + Described + ContentRekeyed + Edited + Remeasured + Confirmed + Drift + ContentReplaced + Filled + CopyOnly + Unchanged;
        /// <summary>The buckets as one line, for messages.</summary>
        public string Buckets => $"described {Described}, re-keyed {ContentRekeyed}, edited {Edited}, remeasured {Remeasured}, confirmed {Confirmed}, drift {Drift}, replaced {ContentReplaced}, filled {Filled}, copy-only {CopyOnly}, unchanged {Unchanged}, stale {Stale}, unrooted {Unrooted}, unstorable {Unstorable}";
        /// <summary>The non-zero buckets only, for the console (every entry still accounted: the zeros are simply not printed).</summary>
        public string BucketsCompact => CompactBuckets(Described, ContentRekeyed, Edited, Remeasured, Confirmed, Drift, ContentReplaced, Filled, CopyOnly, Unchanged, Stale, Unrooted, Unstorable);
        internal static string CompactBuckets(long described, long rekeyed, long edited, long remeasured, long confirmed, long drift, long replaced, long filled, long copyOnly, long unchanged, long stale, long unrooted, long unstorable)
        {
            var parts = new List<string>();
            void P(long n, string name) { if (n != 0) parts.Add($"{n} {name}"); }
            P(described, "described"); P(rekeyed, "re-keyed"); P(edited, "edited"); P(remeasured, "remeasured"); P(confirmed, "confirmed"); P(drift, "drift");
            P(replaced, "replaced"); P(filled, "filled"); P(copyOnly, "copy facts only"); P(unchanged, "unchanged"); P(stale, "stale, skipped"); P(unrooted, "under no root, skipped"); P(unstorable, "unstorable, failed");
            return parts.Count == 0 ? "none" : string.Join(", ", parts);
        }
        /// <summary>Entries whose description was replaced by a fresh measurement from the SAME producer/version (T01, F2): the
        /// store holds the newest observation and its measuring run; a beyond-noise old/new pair is kept as a resolved value-drift record.</summary>
        public int Remeasured;
        /// <summary>Aggregate artifacts filed this call (new content-addressed files), ones already present for their content, and the bytes of the new ones (S2 C-A).</summary>
        public int Artifacts, ArtifactsShared;
        public long ArtifactBytes;
        public long StoreBytesBefore, StoreBytesAfter, ElapsedMs;
        public string ResultPath = "";
        /// <summary>For the common store-result.json (T11): the store this import opened, its schema and the committed generation
        /// when it finished — filled in place as Execute proceeds, so a failure keeps what was established before it.</summary>
        public string StorePath = ""; public int StoreSchema; public long Generation;
    }

    /// <summary>--store-import: the catalog (via the UNCHANGED reader) and the ledgers beside it, into
    /// the store. Collects nothing new. Idempotent: re-importing an unchanged catalog creates nothing.
    /// Batched transactions, so a kill leaves whole batches; the import's own run row says 'failed'
    /// until the last batch commits.</summary>
    internal static class ImportCommand
    {
        internal const string ResultFileName = "store-import-result.json";

        internal static int Run(ImportOptions o)
        {
            // store-v3 T11: the common store-result.json on EVERY exit path (the hub reads it, never stdout) — the same record
            // every store verb writes; store-import-result.json beside it stays the verb's own detailed counts. The report is
            // filled IN PLACE as Execute proceeds, so a failure still records the store facts established before it.
            var result = StoreResult.Begin("store-import");
            var r = new ImportReport();
            try
            {
                Execute(o, r);
                Console.WriteLine($"Imported {r.Entries} entries from {r.Catalog} into {o.StorePath}");
                Console.WriteLine($"  roots: {string.Join(", ", r.Roots)}");
                Console.WriteLine($"  entities: {r.EntitiesCreated} created, {r.MatchedHash} matched by sha, {r.MatchedPath} matched by path, {r.CreatedNoSha} created without a sha");
                // Wording (operator's MB3 import, 2026-10-12): "3138 unrecoverable" read as damage. It counts FLAC entries that carry only
                // the current frame-anchored hash — i.e. a mirror catalog scanned before 2026-07-21 could not be matched to them. Normal.
                Console.WriteLine($"  locations: {r.Locations} ({r.LocationsNew} new); sha collisions (same hash, different audio): {r.Conflicts}; FLAC hashes: {r.LegacySha} pre-2026-07 (kept as a facet), {r.LegacyShaUnrecoverable} current-only (a pre-2026-07 mirror would not match them; normal for a library scanned since)");
                Console.WriteLine($"  entries: {r.BucketsCompact}" + (r.Accounted == r.Entries ? "" : $" — WARNING: {r.Accounted} of {r.Entries} accounted for"));
                Console.WriteLine($"  ledgers: {r.ErrorsRows} errors, {r.SkippedRows} skipped, {r.ReviewRows} review, {r.VerifyRows} verify rows, {r.ReceiptRows} receipts" +
                                  (r.LedgerUnrooted > 0 ? $"; {r.LedgerUnrooted} ledger rows under no root (stream URLs, files outside the library) not imported" : ""));
                Console.WriteLine($"  store: {r.StoreBytesBefore:N0} -> {r.StoreBytesAfter:N0} bytes; {r.ElapsedMs / 1000.0:F1} s; result: {r.ResultPath}");
                if (r.Unrooted > 0) Console.WriteLine($"  WARNING: {r.Unrooted} entries were under none of the roots and were not imported (pass --library-root)");
                result.Store = new StoreResultStore { Path = r.StorePath, SchemaVersion = r.StoreSchema, Generation = r.Generation, TruedatVersion = VersionInfo.Display };
                result.Export = new StoreResultExport { Performed = false };   // an import publishes nothing; --store-export does
                result.Detail = new Dictionary<string, long>
                {
                    ["entries"] = r.Entries, ["unrooted"] = r.Unrooted, ["entitiesCreated"] = r.EntitiesCreated, ["locations"] = r.Locations, ["locationsNew"] = r.LocationsNew,
                    ["unchanged"] = r.Unchanged, ["remeasured"] = r.Remeasured, ["drift"] = r.Drift, ["contentReplaced"] = r.ContentReplaced, ["contentRekeyed"] = r.ContentRekeyed,
                    ["conflicts"] = r.Conflicts, ["failures"] = r.ErrorsRows, ["elapsedMs"] = r.ElapsedMs,
                };
                StoreResult.Write(r.StorePath, result, 0, StoreResult.OutcomeCompleted);
                return 0;
            }
            catch (StoreException ex)
            {
                Console.Error.WriteLine("Error: " + ex.Message);
                WriteFailed(o, r, result, ex.ExitCode, ex.Message);
                return ex.ExitCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error: import failed: " + ex.Message);
                WriteFailed(o, r, result, 1, "import failed: " + ex.Message);
                return 1;
            }
        }

        /// <summary>The failure record: the store block names the store when a path was given (schema and generation when it
        /// opened); nothing is published by an import, so the export block is simply not performed.</summary>
        static void WriteFailed(ImportOptions o, ImportReport r, StoreResultFile result, int exitCode, string reason)
        {
            string? storePath = r.StorePath.Length > 0 ? r.StorePath : null;
            try { if (storePath == null && !string.IsNullOrWhiteSpace(o.StorePath)) storePath = Path.GetFullPath(o.StorePath!); } catch { }
            bool opened = r.StorePath.Length > 0;   // schema/generation are facts only once the store answered; null otherwise (TDA review P3b)
            result.Store = new StoreResultStore { Path = storePath ?? "", SchemaVersion = opened ? r.StoreSchema : (int?)null, Generation = opened ? r.Generation : (long?)null, TruedatVersion = VersionInfo.Display };
            result.Export = new StoreResultExport { Performed = false };
            StoreResult.Write(storePath, result, exitCode, StoreResult.OutcomeFailed, reason);
        }

        /// <summary>S2 C3 — the in-memory entry: the same resolution, upsert, drift and run rules as a catalog import, over
        /// entries the caller already holds (the scan's analysed set), so a scan never re-reads the catalog it just wrote.
        /// <paramref name="producerKey"/>/<paramref name="producerVersion"/> name the observer (essentia@&lt;build&gt; for a scan,
        /// import@1 for a catalog); a non-import producer gets one run row per entry carrying its analysisDuration.</summary>
        internal static ImportReport ExecuteEntries(string? storePath, IReadOnlyDictionary<string, TrackEntry> entries, string? libraryRoot, string role, bool machineAdopt,
            string producerKey, string producerVersion, string source, IReadOnlyList<ScanFailure>? failures, string? resultPath, IEnumerable<string>? rootHints = null,
            IReadOnlyDictionary<string, string>? pendingArtifacts = null, IReadOnlyDictionary<string, string>? pendingEdges = null, int batchSize = 5000,
            IReadOnlyCollection<string>? copyOnlyKeys = null, IReadOnlyCollection<string>? retirePaths = null)
        {
            var sw = Stopwatch.StartNew();
            var r = new ImportReport { Entries = entries.Count, Catalog = source };
            role = (role ?? "primary").ToLowerInvariant();
            if (role != "primary" && role != "mirror" && role != "endpoint")
                throw new StoreException($"root role must be primary, mirror or endpoint (got '{role}').");
            var now = DateTime.UtcNow.ToString("o");
            using var os = OpenStore.Open(storePath, create: true, VersionInfo.Display, now);
            var dal = os.Dal; var db = os.Db;
            string writerMachine;
            using (var tx = db.BeginTransaction()) { writerMachine = os.Machine.ForWrites(dal, machineAdopt, now); tx.Complete(); }
            r.StoreBytesBefore = new FileInfo(os.Path).Length;

            // Roots. Given -> that one. Else the store's EXISTING roots cover what they cover, and only paths under none
            // of them get a root inferred — from the root hints (the whole catalog the scan is saving) when there are
            // any, never from the analysed subset alone: two files under D:\Music\Tool must not mint D:\Music\Tool.
            var allPaths = new List<string>(entries.Keys);
            if (failures != null) foreach (var f in failures) allPaths.Add(f.Path);
            if (retirePaths != null) allPaths.AddRange(retirePaths);
            var rootIds = new List<(string Path, long Id)>();
            var roots = new List<string>();
            using (var tx = db.BeginTransaction())
            {
                if (libraryRoot != null)
                {
                    roots.Add(libraryRoot);
                    rootIds.Add((libraryRoot, dal.EnsureRoot(writerMachine, libraryRoot, role, now)));
                }
                else
                {
                    foreach (var ex in dal.ListRoots()) rootIds.Add((ex.Path, ex.Id));
                    var uncovered = allPaths.Where(p => RootFor(rootIds, p) == null).ToList();
                    if (uncovered.Count > 0)
                    {
                        var basis = new List<string>(uncovered);
                        if (rootHints != null) basis.AddRange(rootHints.Where(p => RootFor(rootIds, p) == null));
                        foreach (var root in CanonicalPath.InferRoots(basis))
                        {
                            roots.Add(root);
                            rootIds.Add((root, dal.EnsureRoot(writerMachine, root, role, now)));
                            StoreLog.Info($"root registered: {root} ({role}) [inferred from {basis.Count} paths]");
                        }
                    }
                    if (rootIds.Count == 0 && allPaths.Count > 0) throw new StoreException("No library root could be inferred from the paths; pass --library-root.");
                }
                rootIds = FoldNestedRoots(dal, rootIds, now);
                tx.Complete();
            }
            r.Roots.AddRange(rootIds.Select(x => x.Path));
            bool mirror = role == "mirror";
            bool runPerEntry = producerKey != "import";

            // A non-import producer (the scan) records one run per entry; an import records one for the batch.
            long sharedRun = 0;
            if (!runPerEntry)
                using (var tx = db.BeginTransaction())
                {
                    sharedRun = dal.InsertRun(producerKey, producerVersion, null, null, null, null, now, null, Environment.MachineName, VersionInfo.Display, "failed", null, null, "in progress", source);
                    tx.Complete();
                }
            // T18: a scan's CACHE-HIT refreshes (copy facts, a re-key) are written under the import producer's shared run — a cache hit
            // measured nothing, so it must never appear as an Essentia observation.
            long copyOnlyRun = 0;
            bool anyCopyOnly = runPerEntry && copyOnlyKeys != null && copyOnlyKeys.Count > 0;
            if (anyCopyOnly)
                using (var tx = db.BeginTransaction())
                {
                    copyOnlyRun = dal.InsertRun("import", "1", null, null, null, null, now, null, Environment.MachineName, VersionInfo.Display, "failed", null, null, "in progress", "scan-cache:" + source);
                    tx.Complete();
                }
            try
            {
                // TDA N2 on 3244dbe: the scan's failure rows commit in the SAME transaction as the entries. Written afterwards in a second
                // transaction, a failure there re-queued a flush whose entries had already committed (spills gone, artifacts moved) and the
                // retry recorded spurious lost folds.
                Action<StoreDal>? writeFailures = failures == null || failures.Count == 0 ? null : d =>
                {
                    foreach (var f in failures)
                    {
                        var e = EntityForPath(d, rootIds, mirror, f.Path, source, now, r);
                        if (e == null) continue;
                        if (d.RunExistsContent(producerKey, e, "failed", source, f.Error, f.Components)) continue;
                        d.InsertRun(producerKey, producerVersion, e, null, null, null, now, (long)(f.DurationSec * 1000), Environment.MachineName, VersionInfo.Display, "failed", f.Components, null, f.Error, source);
                        r.ErrorsRows++;
                    }
                    if (FailBeforeCommitForTest) throw new InvalidOperationException("fail-before-commit seam fired after the failure rows");
                };
                // T18: a moved file's old path is retired in the SAME transaction as the entries (never a store that holds the new path
                // committed and the old one still present, or the reverse).
                Action<StoreDal>? beforeCommit = writeFailures;
                if (retirePaths != null && retirePaths.Count > 0)
                    beforeCommit = d =>
                    {
                        writeFailures?.Invoke(d);
                        foreach (var path in retirePaths)
                        {
                            var rf = RootFor(rootIds, path);
                            if (rf == null) continue;
                            CanonicalPath.Split(rf.Value.Path, path, out _, out var canon);
                            var loc = d.FindLocation(rf.Value.Id, canon, out _);
                            if (loc != null && d.RetireLocation(loc.Value, now)) { r.Retired++; r.VisibleChanges++; }
                        }
                    };
                // codex 2026-10-23 F5: the shared run's and the cache-only run's outcomes are written in the LAST batch's transaction, with
                // the entries. Written in their own transactions after it, a failure there reached ScanSink.Flush as a failed flush, which
                // re-queued a batch that had committed and deleted its edge spills — false spill-loss failures and duplicate observation
                // runs on the retry. Inside the transaction, a failure rolls the whole batch back and the retry is a true retry.
                var finalize = beforeCommit;
                beforeCommit = d =>
                {
                    finalize?.Invoke(d);
                    if (FailRunOutcomeForTest) throw new InvalidOperationException("fail-run-outcome seam fired inside the last batch's transaction");
                    if (!runPerEntry) d.UpdateRunOutcome(sharedRun, "ok", sw.ElapsedMilliseconds, null);
                    if (anyCopyOnly) d.UpdateRunOutcome(copyOnlyRun, "ok", sw.ElapsedMilliseconds, null);
                };
                ImportEntriesCore(dal, db, batchSize, FailAfterEntriesForTest, rootIds, mirror, entries, sharedRun, producerKey, producerVersion, runPerEntry, source, now, r,
                    pendingArtifacts, Path.GetDirectoryName(os.Path) ?? ".", pendingEdges, beforeCommit, anyCopyOnly ? copyOnlyKeys : null, copyOnlyRun);
                sw.Stop();
            }
            catch (Exception ex)
            {
                if (!runPerEntry) try { using var tx = db.BeginTransaction(); dal.UpdateRunOutcome(sharedRun, "failed", sw.ElapsedMilliseconds, ex.Message); tx.Complete(); } catch { }
                if (anyCopyOnly) try { using var tx = db.BeginTransaction(); dal.UpdateRunOutcome(copyOnlyRun, "failed", sw.ElapsedMilliseconds, ex.Message); tx.Complete(); } catch { }
                StoreLog.Error($"{producerKey} write failed after {r.Locations} entries: {ex.Message}");
                throw;
            }
            r.ElapsedMs = sw.ElapsedMilliseconds;
            dal.ResetAll();
            try { db.Exec("PRAGMA wal_checkpoint(TRUNCATE);"); }
            catch (SqliteException ex) { StoreLog.Warn($"WAL checkpoint did not complete ({ex.Message}); the -wal file will fold on the next open"); }
            r.StoreBytesAfter = new FileInfo(os.Path).Length;
            if (resultPath != null) { r.ResultPath = resultPath; WriteResult(r, os.Path, role, producerKey + "@" + producerVersion); }
            StoreLog.Info($"{producerKey}@{producerVersion} wrote {r.Entries} entries: {r.EntitiesCreated} entities created, {r.LocationsNew} new locations; {r.Buckets}; {r.ErrorsRows} failures, {r.ElapsedMs} ms");
            return r;
        }

        /// <summary>Thrown by ImportEntry's pre-mutation preflight only: the entry holds a value the typed store cannot represent. It is
        /// the ONE failure the batch loop continues past, because nothing of the entry has been written when it is raised.</summary>
        internal sealed class UnstorableEntryException : StoreException
        {
            public UnstorableEntryException(string message) : base(message) { }
        }

        /// <summary>The batched entry loop shared by the file import and the in-memory entry.</summary>
        static void ImportEntriesCore(StoreDal dal, SqliteDb db, int batchSize, int failAfterEntries, List<(string Path, long Id)> rootIds, bool mirror,
            IReadOnlyDictionary<string, TrackEntry> tracks, long sharedRun, string producerKey, string producerVersion, bool runPerEntry, string source, string now, ImportReport r,
            IReadOnlyDictionary<string, string>? pendingArtifacts = null, string? storeDir = null, IReadOnlyDictionary<string, string>? pendingEdges = null,
            Action<StoreDal>? beforeCommit = null, IReadOnlyCollection<string>? copyOnlyKeys = null, long copyOnlyRun = 0)
        {
            var copyOnly = copyOnlyKeys == null ? null : new HashSet<string>(copyOnlyKeys, PathComparer.Instance);
            var keys = tracks.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            int processed = 0;
            if (keys.Count == 0 && beforeCommit != null)
            {
                // Failure rows and/or T18 retirements only: one transaction; a retirement is a consumer-visible change, so it advances (T07).
                using var tx0 = db.BeginTransaction(); int v0 = r.VisibleChanges; beforeCommit(dal);
                if (r.VisibleChanges > v0) dal.AdvanceGeneration();
                tx0.Complete(); r.Generation = dal.Generation(); return;
            }
            for (int b = 0; b < keys.Count; b += Math.Max(1, batchSize))
            {
                int end = (int)Math.Min(keys.Count, (long)b + Math.Max(1, batchSize));
                // Keeper 006 / TDA rule: NO file is moved or deleted until the transaction that references it has committed. The artifact
                // moves this batch wants are planned here and performed after tx.Complete(); the edge spills are deleted then too. A rollback
                // leaves every pending file where the sink put it, so a re-queued retry finds its whole payload.
                var deferred = new List<(string Pending, string Full)>();
                int visibleBefore = r.VisibleChanges;
                using var tx = db.BeginTransaction();
                for (int i = b; i < end; i++)
                {
                    string? pendingArtifact = null;
                    if (pendingArtifacts != null) pendingArtifacts.TryGetValue(keys[i], out pendingArtifact);
                    // The fold was spilled to disk by the sink (review I3): read it here, one entry at a time; the file stays until the commit.
                    global::Truedat.Edges.EdgeFoldResult? edges = null;
                    bool spillLost = false;
                    if (pendingEdges != null && pendingEdges.TryGetValue(keys[i], out var edgesFile))
                    {
                        edges = ScanSink.ReadEdges(edgesFile, out var lost);
                        if (edges == null) { edges = ScanSink.LostFold(lost ?? $"spilled edge fold missing ({edgesFile})"); r.SpillsLost++; spillLost = true; }   // a FAILED edges run, never "no fold"
                    }
                    try
                    {
                        if (copyOnly != null && copyOnly.Contains(keys[i]))
                            ImportEntry(dal, rootIds, mirror, keys[i], tracks[keys[i]], copyOnlyRun, "import", "1", false, "scan-cache:" + source, now, r, null, storeDir, null, deferred, false);
                        else
                            ImportEntry(dal, rootIds, mirror, keys[i], tracks[keys[i]], sharedRun, producerKey, producerVersion, runPerEntry, source, now, r, pendingArtifact, storeDir, edges, deferred, spillLost);
                    }
                    catch (UnstorableEntryException ex)
                    {
                        // T04 review finding 3 + keeper atomicity notes: ONLY the pre-mutation rejection continues the batch — it is thrown
                        // before the entry has touched anything, so recording one failed run and going on leaves the store exactly as it
                        // was for that entry (never a poison pill that aborts every re-run). Any OTHER StoreException after a write — an
                        // artifact refusal, a DAL error — propagates as before and the whole batch transaction rolls back: a half-written
                        // entry (new content beside an old copy, an ok run) must never survive.
                        dal.InsertRun(producerKey, producerVersion, null, null, null, null, now, null, Environment.MachineName, VersionInfo.Display, "failed", "store", "unrepresentable", $"{keys[i]}: {ex.Message}", source);
                        r.ErrorsRows++; r.Unstorable++;
                        StoreLog.Warn($"{keys[i]}: not stored — {ex.Message}");
                    }
                    processed++;
                    if (failAfterEntries > 0 && processed == failAfterEntries)
                        throw new InvalidOperationException($"fail-after seam fired at entry {processed}");
                }
                if (beforeCommit != null && end == keys.Count) beforeCommit(dal);   // the scan's failure rows, in this same transaction (TDA N2)
                // T07 (keeper 099): one advance per committed transaction that changed something a consumer can see, written in THAT
                // transaction — a rollback takes the advance with it, and a batch of unchanged entries or failure rows is no mutation.
                if (r.VisibleChanges > visibleBefore) dal.AdvanceGeneration();
                tx.Complete();
                // Keeper 104 / TDA 083: the report carries the COMMITTED generation after every batch, so a verb that fails in a later
                // batch records the generation its earlier batches left the store at — not the one read at open.
                r.Generation = dal.Generation();
                // Committed: now the files. Nothing here can undo the commit. A crash between the commit and these moves leaves a row whose
                // file is still a pending file — there is NO reconciliation for that today (TDA N3; artifacts have had no production producer
                // since b9b27ec): re-enabling artifact capture needs row/file reconciliation at open, or a temp-in-destination-then-rename
                // order, before it ships — ISSUES I15.
                ArtifactStore.Finish(deferred);
                // A spill whose entry was preflight-rejected (UnstorableEntryException above) is deleted here too, with no edges run for it — the
                // rejection run already names the entry's failure (TDA N4).
                if (pendingEdges != null) for (int i = b; i < end; i++) if (pendingEdges.TryGetValue(keys[i], out var spill)) ScanSink.DeleteSpill(spill);
                StoreLog.Debug($"batch committed: {processed}/{keys.Count}");
            }
        }

        internal static ImportReport Execute(ImportOptions o) { var r = new ImportReport(); Execute(o, r); return r; }

        /// <summary>T06: set by a catalog-changing verb around its store write — the arriving entries are the verb's EXACT result (content
        /// and copy facts, nulls included: a strip or a migrate clears). Never set by a scan or an import. Single-threaded verbs only.</summary>
        [ThreadStatic] internal static bool ExactArrival;

        /// <summary>Test seam (codex 2026-10-23 F5): fail inside the last batch's transaction, where the run outcomes are written.</summary>
        internal static bool FailRunOutcomeForTest;

        /// <summary>Self-test seam: runs right after the store opened (schema and generation known) and before any entry is written,
        /// so a test can fail the verb there and prove the record keeps the at-open facts and carries no counts. Null in production.</summary>
        internal static Action? AfterStoreOpened;

        /// <summary>Self-test seam for the in-memory entry path: throw after N entries were processed inside the batch transaction, so a test can
        /// prove a rollback leaves every pending file recoverable and no row-less artifact file behind. 0 in production.</summary>
        internal static int FailAfterEntriesForTest;

        /// <summary>Self-test seam: throw inside the entries' transaction right after the scan's failure rows were written (TDA N2). False in production.</summary>
        internal static bool FailBeforeCommitForTest;

        /// <summary>Fills <paramref name="r"/> as it goes (see <see cref="Run"/>); throws on failure with the facts so far in place.</summary>
        internal static void Execute(ImportOptions o, ImportReport r)
        {
            var sw = Stopwatch.StartNew();
            StoreLog.SetVerbose(o.Audit);
            if (string.IsNullOrWhiteSpace(o.StorePath))
                throw new StoreException("No store given. Pass --store <path\\truedat.db>. The store path is never guessed: a default beside a mirrored catalog would put SQLite on a network share.");
            if (string.IsNullOrWhiteSpace(o.CatalogPath))
                throw new StoreException("No catalog to import. Pass the mbxmoods.json path (or --moods <path>).");
            var role = (o.RootRole ?? "primary").ToLowerInvariant();
            if (role != "primary" && role != "mirror" && role != "endpoint")
                throw new StoreException($"--root-role must be primary, mirror or endpoint (got '{o.RootRole}').");

            // 1. The catalog, through the reader that every other mode uses.
            var catalog = Path.GetFullPath(o.CatalogPath!);
            var tracks = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
            int loaded = Program.LoadExistingMoodsForStore(catalog, tracks);
            if (loaded == 0 && !File.Exists(catalog) && !File.Exists(catalog + ".1"))
                throw new StoreException($"Catalog not found: {catalog} (neither the file nor .1 parts).");
            r.Entries = loaded; r.Catalog = catalog;
            (r.CatalogSha256, r.CatalogBytes) = HashCatalog(catalog);
            StoreLog.Info($"catalog {catalog}: {loaded} entries, {r.CatalogBytes:N0} bytes, sha256 {r.CatalogSha256.Substring(0, 12)}…");

            var now = DateTime.UtcNow.ToString("o");
            using var os = OpenStore.Open(o.StorePath, create: true, VersionInfo.Display, now);
            var dal = os.Dal; var db = os.Db;
            r.StorePath = os.Path; r.StoreSchema = SchemaMigrator.ReadVersion(db); r.Generation = dal.Generation();
            AfterStoreOpened?.Invoke();
            // The machine this import writes AS: the store's own on its minting host; on another host a
            // refusal unless --machine-adopt, which finds-or-mints a machine row for THIS host (review #2).
            string writerMachine;
            using (var tx = db.BeginTransaction()) { writerMachine = os.Machine.ForWrites(dal, o.MachineAdopt, now); tx.Complete(); }
            r.StoreBytesBefore = new FileInfo(os.Path).Length;

            // 2. Roots: given, else inferred and reported so a wrong inference is visible.
            var roots = o.LibraryRoot != null ? new List<string> { o.LibraryRoot } : CanonicalPath.InferRoots(tracks.Keys);
            if (roots.Count == 0) throw new StoreException("No library root could be inferred from the catalog keys; pass --library-root.");
            var rootIds = new List<(string Path, long Id)>();
            using (var tx = db.BeginTransaction())
            {
                foreach (var root in roots) rootIds.Add((root, dal.EnsureRoot(writerMachine, root, role, now)));
                // The whole catalog's root may contain a root the store already had (a scan's first checkpoint minted a sub-folder):
                // without the fold, every entry under the old root was written a SECOND time under the new one and both roots stayed.
                rootIds = FoldNestedRoots(dal, rootIds, now);
                tx.Complete();
            }
            r.Roots.AddRange(roots);
            StoreLog.Info($"roots ({role}): {string.Join(", ", roots)}" + (o.LibraryRoot == null ? " [inferred]" : ""));

            // 3. The import's own run row — 'failed' until the last batch commits, so a kill is visible.
            var source = $"import:{catalog}@{r.CatalogSha256}:{r.CatalogBytes}";
            long importRun;
            using (var tx = db.BeginTransaction())
            {
                importRun = dal.InsertRun("import", "1", null, null, null, null, now, null, Environment.MachineName, VersionInfo.Display, "failed", null, null, "in progress", source);
                tx.Complete();
            }

            try
            {
                // 4. Entries, in batches (the same core the scan's in-memory entry uses).
                bool mirror = role == "mirror";
                ImportEntriesCore(dal, db, o.BatchSize, o.FailAfterEntries, rootIds, mirror, tracks, importRun, "import", "1", runPerEntry: false, source, now, r);

                // 5. Ledgers beside the catalog.
                var dir = Path.GetDirectoryName(catalog) ?? ".";
                using (var tx = db.BeginTransaction())
                {
                    ImportLedgers(dal, rootIds, mirror, dir, now, r);
                    tx.Complete();
                }

                // 6. Rescan-3 R5: a catalog carrying another store state's origin at or past this store's generation has now been
                // absorbed, so the store supersedes it — set PAST the catalog's generation, never merely advanced (an import that changed
                // nothing visible advanced nothing, and the next scan refused the same catalog as catalog-ahead). The files are then
                // behind the store, which the scan's precondition accepts and republishes.
                if (CatalogOrigin.TryRead(catalog, out var origin) && origin.Generation >= dal.Generation())
                    using (var tx = db.BeginTransaction())
                    {
                        long g = origin.Generation + 1;
                        dal.MetaSet(StoreDal.GenerationKey, g.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        tx.Complete();
                        StoreLog.Info($"the catalog's origin was at generation {origin.Generation}, at or past this store's: the store now supersedes it (generation {g}); the next scan republishes the files from the store");
                    }

                sw.Stop();
                using (var tx = db.BeginTransaction())
                {
                    dal.UpdateRunOutcome(importRun, "ok", sw.ElapsedMilliseconds, null);
                    tx.Complete();
                }
            }
            catch (Exception ex)
            {
                try { using var tx = db.BeginTransaction(); dal.UpdateRunOutcome(importRun, "failed", sw.ElapsedMilliseconds, ex.Message); tx.Complete(); } catch { }
                StoreLog.Error($"import failed after {r.Locations} entries: {ex.Message}");
                throw;
            }

            r.ElapsedMs = sw.ElapsedMilliseconds;
            // Fold the WAL into the main file before measuring, so the size reported is the size on disk
            // and the operator is left with one file, not db + wal. Every cached statement must be reset
            // first (a statement left on a row holds a WAL read mark — the first real import failed here).
            // A checkpoint that still cannot complete is a warning, never a failed import: the rows are
            // committed either way.
            dal.ResetAll();
            try { db.Exec("PRAGMA wal_checkpoint(TRUNCATE);"); }
            catch (SqliteException ex) { StoreLog.Warn($"WAL checkpoint did not complete ({ex.Message}); the -wal file will fold on the next open"); }
            r.StoreBytesAfter = new FileInfo(os.Path).Length;
            var wal = os.Path + "-wal";
            if (File.Exists(wal)) r.StoreBytesAfter += new FileInfo(wal).Length;
            r.ResultPath = Path.Combine(Path.GetDirectoryName(os.Path) ?? ".", ResultFileName);
            WriteResult(r, os.Path, o.RootRole ?? "primary", "import@1");
            r.Generation = dal.Generation();   // the committed generation after this import (each visible batch advanced it, T07)
            StoreLog.Info($"import done: {r.Entries} entries, {r.EntitiesCreated} entities created, {r.LocationsNew} new locations, {r.Conflicts} sha collisions, {r.Drift} value drifts, {r.ElapsedMs} ms");
        }

        static void ImportEntry(StoreDal dal, List<(string Path, long Id)> roots, bool mirror, string key, TrackEntry entry, long sharedRun,
            string producerKey, string producerVersion, bool runPerEntry, string source, string now, ImportReport r, string? pendingArtifact = null, string? storeDir = null,
            global::Truedat.Edges.EdgeFoldResult? edges = null, List<(string Pending, string Full)>? deferredArtifacts = null, bool spillLost = false)
        {
            var rf = RootFor(roots, key);
            if (rf == null) { r.Unrooted++; StoreLog.Warn($"not under any root, skipped: {key}"); return; }
            // spectralFlatness is Essentia's Bark-band flatness: an entry from a catalog an older build wrote (the three-band average)
            // is stored with Essentia's value, so the store holds one definition whatever wrote the arriving entry.
            if (entry.Features != null) entry.Features.SpectralFlatness = Program.HealSpectralFlatness(entry.Features);
            // Keeper partial-entry defect (T04): every representation of this entry is validated BEFORE any mutation — entity, location,
            // content, copy — so a value the typed store cannot hold rejects the entry whole (one failed run, nothing changed), never a
            // changed content row beside an untouched copy row and an ok run. The throw is caught per entry by ImportEntriesCore.
            if (TrackEntryMapper.FirstUnstorable(TrackEntryMapper.ToRow(entry)) is string unstorable)
                throw new UnstorableEntryException(unstorable + "; the entry was not stored.");
            CanonicalPath.Split(rf.Value.Path, key, out var rel, out var canon);
            bool isFlac = key.EndsWith(".flac", StringComparison.OrdinalIgnoreCase);
            var sha = string.IsNullOrEmpty(entry.AudioStreamSha256) ? null : entry.AudioStreamSha256;
            var kind = FacetKinds.ForShaSource(entry.AudioStreamSha256Source, isFlac);

            long? existingLoc = dal.FindLocation(rf.Value.Id, canon, out _);
            bool isNewLocation = existingLoc == null;
            var res = EntityResolver.Resolve(dal, sha, kind, rf.Value.Id, canon, source, now, matchPathAcrossRoots: mirror);
            switch (res.How)
            {
                case ResolveHow.Created: r.EntitiesCreated++; break;
                case ResolveHow.CreatedNoSha: r.CreatedNoSha++; break;
                case ResolveHow.MatchedHash: r.MatchedHash++; break;
                case ResolveHow.MatchedPath: r.MatchedPath++; break;
            }
            if (sha != null && kind == FacetKinds.ShaTaglibRegion) r.LegacySha++;
            if (sha != null && isFlac && kind == FacetKinds.ShaFrames && !dal.HasFacetOfKind(res.EntityId, FacetKinds.ShaTaglibRegion)) r.LegacyShaUnrecoverable++;

            var contentKey = sha ?? ("entity:" + res.EntityId);

            // Recency (review #4b): an OLDER catalog imported over a newer store must not regress a row. Same
            // content key and an older lastModified than the store holds -> skip, count stale, touch nothing.
            if (existingLoc != null)
            {
                var storedLm = dal.LocationLastModified(existingLoc.Value);
                var storedKey = dal.FeatureContentKey(existingLoc.Value);
                if (storedLm != null && storedKey == contentKey
                    && DateTime.ParseExact(storedLm, "o", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime()
                       > entry.LastModified.ToUniversalTime())
                {
                    r.Stale++; r.Locations++;
                    StoreLog.Debug($"stale arrival skipped: {key} (store {storedLm} > catalog {entry.LastModified:o})");
                    return;
                }
            }

            // Review-focus 1: same sha, different audio props -> one entity, both locations, a conflict row.
            if (isNewLocation && res.How == ResolveHow.MatchedHash && entry.FingerprintV1 != null)
            {
                var other = dal.FeatureProps(res.EntityId, rf.Value.Id, canon);
                if (other != null && (Math.Abs(other.Value.DurationMs - entry.FingerprintV1.DurationMs) > 500
                                      || !string.Equals(other.Value.Codec, entry.FingerprintV1.Codec, StringComparison.OrdinalIgnoreCase)))
                {
                    dal.InsertConflict(res.EntityId, null, "sha-collision-props-differ",
                        $"{key}: durationMs {entry.FingerprintV1.DurationMs} codec {entry.FingerprintV1.Codec} vs existing {other.Value.Display}: durationMs {other.Value.DurationMs} codec {other.Value.Codec}", now);
                    r.Conflicts++;
                    StoreLog.Warn($"sha collision with differing props: {key} vs {other.Value.Display}");
                }
            }

            var locBefore = existingLoc != null ? dal.LocationVisible(existingLoc.Value) : null;
            long loc = dal.UpsertLocation(res.EntityId, rf.Value.Id, rel, canon, key, entry.LastModified.ToString("o"), now);
            r.Locations++; if (isNewLocation) r.LocationsNew++;
            bool locationChanged = isNewLocation || !Nullable.Equals(dal.LocationVisible(loc), locBefore);
            if (locationChanged) r.VisibleChanges++;   // T07: the location is part of what an export shows
            // A scan records one run per analysed entry (the observation: who measured, how long, on which copy);
            // an import shares one run for the whole catalog.
            long runId = runPerEntry
                ? dal.InsertRun(producerKey, producerVersion, res.EntityId, sha, sha != null ? kind : null, loc, now,
                    entry.AnalysisDurationSecs.HasValue ? (long?)(entry.AnalysisDurationSecs.Value * 1000) : null, Environment.MachineName, VersionInfo.Display, "ok", null, null, null, source)
                : sharedRun;
            WriteDescriptionAndCopy(dal, loc, res.EntityId, contentKey, sha != null ? kind : "entity", runId, producerKey, producerVersion, entry, key, now, r, locationChanged);
            // T19: the verdict projection, from the rows AS STORED, for every copy of this media (a content change re-derives them all).
            VerdictStore.DeriveForEntity(dal, res.EntityId, now);

            // S2 C-A: the extractor's aggregate output, kept once per content key as a compressed file beside the store.
            if (pendingArtifact != null && storeDir != null)
            {
                // The row is written in this transaction; the pending file is MOVED only after the commit (ArtifactStore.Finish, by the batch loop).
                var a = ArtifactStore.Plan(storeDir, pendingArtifact, contentKey, producerVersion);
                dal.InsertArtifact(contentKey, sha != null ? kind : "entity", runPerEntry ? runId : (long?)null, ArtifactStore.MainKind, producerVersion, a.RelPath, a.Bytes, a.Sha256, now);
                if (a.Created) { r.Artifacts++; r.ArtifactBytes += a.Bytes; } else r.ArtifactsShared++;
                r.VisibleChanges++;
                if (deferredArtifacts != null) deferredArtifacts.Add((pendingArtifact, Path.Combine(storeDir, a.RelPath)));
                else ArtifactStore.Finish(new List<(string, string)> { (pendingArtifact, Path.Combine(storeDir, a.RelPath)) });
            }

            // S2 C4: the edges producer's result for this entry — its own run row (who folded, how long: fold + PCM), and the
            // packed rows under the entry's content key when it folded. A failed fold is a failed run naming the reason and
            // nothing else (constraint 2: no partial row); the catalog entry above is unaffected either way.
            if (edges != null)
            {
                // A lost spill is a store-side loss: its failed run is marked 'spill-lost' so the refresh gate still lists the track (TDA N1).
                if (WriteEdges(dal, res.EntityId, contentKey, sha != null ? kind : "entity", sha, loc, edges, source, now, key, spillLost ? ScanSink.SpillLostComponent : "edges", r)) { r.Edges++; r.VisibleChanges++; } else r.EdgesFailed++;
            }
        }

        /// <summary>S2 C4/C5 — one edges result for one entity: the 'edges' run row (outcome, fold + PCM ms, the reason on failure)
        /// and, when it folded, the packed rows under the content key. Shared by the scan sink and the --work drain so the two
        /// cannot record the same event differently. Returns true when rows were written.</summary>
        internal static bool WriteEdges(StoreDal dal, string entityId, string contentKey, string contentKeyKind, string? sha, long? locationId,
            global::Truedat.Edges.EdgeFoldResult edges, string source, string now, string displayPath, string failedComponent = "edges", ImportReport? r = null)
        {
            bool failed = edges.Status == "failed";
            dal.EnsureProducer(global::Truedat.Edges.EdgeStore.ProducerKey, edges.ProducerVersion, "analysis", needsAudio: true);   // the run row references it, even for a failed fold
            long edgesRun = dal.InsertRun(global::Truedat.Edges.EdgeStore.ProducerKey, edges.ProducerVersion, entityId, sha, sha != null ? contentKeyKind : null, locationId, now,
                edges.FoldMs + edges.PcmMs, Environment.MachineName, VersionInfo.Display, failed ? "failed" : "ok", failed ? failedComponent : null, null, failed ? edges.Head.Error : null, source);
            if (failed) { StoreLog.Warn($"edges: fold failed for {displayPath}: {edges.Head.Error}"); return false; }
            // Sticky per MEDIA (TDA F8): a media that holds arrays under any of its content hashes keeps capturing at the current one, so a key
            // move does not drop to the default row. Otherwise the scan's flag decides (Write's own per-content sticky still applies).
            bool media = global::Truedat.Edges.EdgeStore.MediaHoldsArrays(dal, entityId);
            global::Truedat.Edges.EdgeStore.Write(dal, contentKey, contentKeyKind, edgesRun, edges, media ? true : (bool?)null, out int arrayRows, out int pruned);
            if (r != null) r.SupersededPruned += pruned;
            // Keeper 099/109: with the flag OFF, edge rows THIS write stored with arrays can only be the sticky rule's (the track already
            // held them) — the record's capture.stickyRowsRefreshed. Counted from what Write wrote, never from what the table holds: an
            // 'unavailable' fold writes no edge row and must count 0.
            if (r != null && !global::Truedat.Edges.EdgeStore.CaptureArraysDefault) r.StickyRowsRefreshed += arrayRows;
            return true;
        }

        /// <summary>S2 C2 — observation integrity. The media's description is written once; afterwards an arriving value that
        /// differs BEYOND run-to-run noise under the SAME producer/version is a <c>value-drift</c> conflict (field, both values,
        /// both runs) and the stored description is left alone — that disagreement is the only part of an observation history
        /// that ever carried information. A different producer/version replaces the description (the old run stays as history).
        /// Copy facts describe the file, so the newer observation wins, exactly — per column, for the columns the arrival observed
        /// (<see cref="TrackEntryMapper.MergeCopy"/>). Equal arrivals write nothing at all.</summary>
        internal static void WriteDescriptionAndCopy(StoreDal dal, long locationId, string entityId, string contentKey, string contentKeyKind,
            long runId, string producerKey, string producerVersion, TrackEntry entry, string displayPath, string now, ImportReport r, bool locationChanged = false)
        {
            var row = TrackEntryMapper.ToRow(entry);
            bool contentWritten = false, copyWritten = locationChanged, drifted = false;   // a new or changed location (mtime, state) is a copy fact too

            var existing = dal.ReadContent(entityId);
            bool legacyAlias = existing != null && contentKeyKind == FacetKinds.ShaTaglibRegion && existing.ContentKeyKind == FacetKinds.ShaFrames;
            if (legacyAlias)
            {
                // A superseded alias, not a key move: the store already describes this FLAC under its frame hash and the arrival carries
                // the pre-2026-07-21 TagLib-region hash — a KNOWN-OLDER observation of the same media (the frame hash replaced the region
                // hash on 2026-07-21, so whatever carries the old one predates whatever carries the new). The description and its run
                // stand untouched; only the copy facts below are merged. Letting the arrival into the producer rules was not enough:
                // against a scan's description the producers differ, nothing can order them, and the old values REPLACED the scan's
                // (EV3 pre-tag review, H2). Its identity columns are the stored ones, so the alias itself never reads as a changed value.
                contentKey = existing!.ContentKey; contentKeyKind = existing.ContentKeyKind;
                for (int k = 0; k < TrackEntryMapper.ContentColumnCount; k++)
                {
                    var name = TrackEntryMapper.ContentColumnName(k);
                    if (name == "audioStreamSha256" || name == "audioStreamSha256Source") row[TrackEntryMapper.RowIndexOf(name)] = existing.Values[k];
                }
                StoreLog.Debug($"legacy FLAC hash arrival: the frame-keyed description stands, copy facts merged: {displayPath}");
            }
            if (existing == null)
            {
                dal.UpsertContent(entityId, contentKey, contentKeyKind, runId, row, now);
                contentWritten = true; r.Described++;
            }
            else if (legacyAlias)
            {
                // nothing for the description: see above
            }
            else if (!string.Equals(existing.ContentKey, contentKey, StringComparison.Ordinal))
            {
                // A key move (the resolver already superseded the old hash facet): the same media now has a new content hash,
                // so the arriving entry IS the fresher observation of it — the description is re-keyed and rewritten, no drift.
                dal.UpsertContent(entityId, contentKey, contentKeyKind, runId, row, now);
                contentWritten = true; r.ContentRekeyed++;
                StoreLog.Info($"description re-keyed: {displayPath} ({existing.ContentKey} -> {contentKey})");
            }
            else if (ExactArrival)
            {
                // T06: a catalog-changing VERB's edit (strip, migrate, backfill, ...) is the new value of the description, exactly —
                // a NULL arriving is a clear, not "not observed", and no noise rule applies. Equal rows write nothing.
                bool anyDiffers = false;
                for (int k = 0; k < TrackEntryMapper.ContentColumnCount && !anyDiffers; k++)
                    if (!TrackEntryMapper.ContentEqualExact(k, existing.Values[k], TrackEntryMapper.ContentValue(row, k))) anyDiffers = true;
                if (anyDiffers) { dal.UpsertContent(entityId, contentKey, contentKeyKind, runId, row, now); contentWritten = true; r.Edited++; }
            }
            else
            {
                // Three classes per content column (review #4): a stored NULL gaining a value is a FILL (the signal was not
                // measured before — ffmpeg absent, --no-bitusage — and is now); a value arriving as NULL is NOT OBSERVED and
                // changes nothing; two values differing beyond noise are DRIFT.
                // T01 (F2, I01): the noise tolerance is IMPORT-ONLY. A fresh measurement from the SAME producer/version is the
                // newest observation of the media: it REPLACES the description — exact values, its own run — and a beyond-noise
                // old/new pair is kept as a RESOLVED value-drift record, so the history survives without the store ever holding
                // the older value under the newer run (MB3: 3,143 rows did, stamped with the 2026-10-12 refresh). An import (a
                // catalog re-read, not a measurement) keeps the rules below: within noise writes nothing, beyond noise under the
                // same producer is an OPEN drift with the stored description kept.
                bool isImport = string.Equals(producerKey, "import", StringComparison.Ordinal);
                bool sameProducer = string.Equals(existing.ProducerKey, producerKey, StringComparison.Ordinal)
                                 && string.Equals(existing.ProducerVersion, producerVersion, StringComparison.Ordinal);
                var drift = new List<int>(); var fills = new List<int>(); var changed = new List<int>();
                for (int k = 0; k < TrackEntryMapper.ContentColumnCount; k++)
                {
                    var a = existing.Values[k]; var b = TrackEntryMapper.ContentValue(row, k);
                    if (a == null && b != null) fills.Add(k);
                    else if (b == null) continue;                        // not observed this time: never a change, never a wipe
                    else
                    {
                        if (!TrackEntryMapper.ContentEqualWithinNoise(k, a, b)) drift.Add(k);
                        if (!TrackEntryMapper.ContentEqualExact(k, a, b)) changed.Add(k);
                    }
                }
                if (!isImport && sameProducer && (changed.Count > 0 || fills.Count > 0))
                {
                    // The row written is the arrival where it observed a value and the stored value where it did not (review
                    // finding 1: a scan without ffmpeg must not erase bitUsage/HF measured earlier). Those retained columns ride
                    // under the new run_id — provenance per column is not modelled; the resolved drift rows and the run table
                    // keep the history (finding 6, documented, not hidden).
                    dal.UpsertContent(entityId, contentKey, contentKeyKind, runId, TrackEntryMapper.WithStoredContentWhereUnobserved(row, existing.Values), now);
                    foreach (var k in drift)
                        dal.InsertDriftConflict(entityId, TrackEntryMapper.ContentColumnName(k),
                            TrackEntryMapper.FormatContent(k, existing.Values[k]), TrackEntryMapper.FormatContent(k, TrackEntryMapper.ContentValue(row, k)),
                            existing.RunId, runId, displayPath, now, state: "resolved");
                    foreach (var k in changed) dal.CloseOpenDrift(entityId, TrackEntryMapper.ContentColumnName(k));   // finding 5: the question is answered
                    contentWritten = true; r.Remeasured++;
                    StoreLog.Info($"description remeasured on {displayPath} by {producerKey}@{producerVersion}: fresh values and run stored" + (drift.Count > 0 ? $"; {drift.Count} field(s) beyond noise kept as resolved value-drift history ({TrackEntryMapper.ContentColumnName(drift[0])}{(drift.Count > 1 ? ", …" : "")})" : ""));
                }
                else if (drift.Count > 0)
                {
                    // Review #5: a replacement has a direction. An arrival from an OLDER build of the same producer is drift
                    // (the newer description stays); a newer or unorderable one replaces, and leaves a resolved trace.
                    bool olderArrival = !sameProducer && ArrivalIsOlder(existing.ProducerKey, existing.ProducerVersion, producerKey, producerVersion);
                    if (sameProducer || olderArrival)
                    {
                        foreach (var k in drift)
                            dal.InsertDriftConflict(entityId, TrackEntryMapper.ContentColumnName(k),
                                TrackEntryMapper.FormatContent(k, existing.Values[k]), TrackEntryMapper.FormatContent(k, TrackEntryMapper.ContentValue(row, k)),
                                existing.RunId, runId, displayPath, now);
                        drifted = true; r.Drift++;
                        StoreLog.Warn($"value drift on {displayPath} ({producerKey}@{producerVersion} vs stored {existing.ProducerKey}@{existing.ProducerVersion}): {drift.Count} field(s) differ beyond noise ({TrackEntryMapper.ContentColumnName(drift[0])}{(drift.Count > 1 ? ", …" : "")}); stored description kept, conflict recorded");
                    }
                    else
                    {
                        // codex 2026-10-23 F4: the replacement wrote the raw row, so a column the arrival did not observe (bitUsage / HF from a
                        // scan without ffmpeg) was wiped — while the same arrival agreeing within noise would have kept it. The unobserved
                        // rule is the remeasure's and the confirmation's; a replacement is no different.
                        dal.UpsertContent(entityId, contentKey, contentKeyKind, runId, TrackEntryMapper.WithStoredContentWhereUnobserved(row, existing.Values), now);
                        dal.InsertReplacedConflict(entityId, $"{existing.ProducerKey}@{existing.ProducerVersion}", $"{producerKey}@{producerVersion}", existing.RunId, runId, displayPath, now);
                        contentWritten = true; r.ContentReplaced++;
                        StoreLog.Warn($"description replaced on {displayPath}: {existing.ProducerKey}@{existing.ProducerVersion} -> {producerKey}@{producerVersion} ({drift.Count} field(s) changed; the pair is recorded as a resolved content-replaced conflict)");
                    }
                }
                else if (!isImport && !sameProducer && !ArrivalIsOlder(existing.ProducerKey, existing.ProducerVersion, producerKey, producerVersion))
                {
                    // Operator 2026-10-22 (provenance): a fresh measurement from a DIFFERENT producer that AGREES within noise CONFIRMS the
                    // description — the fresh values and the measuring run take the row, exactly as a remeasure does, so a description a
                    // catalog import seeded becomes the scan's once the scan has measured it (MB3: 6,147 of 7,538 still read import@1 after
                    // the EV3 pass had measured every one). Agreement is not information: no conflict row. Columns the arrival did not
                    // observe keep their stored values (the remeasure rule).
                    dal.UpsertContent(entityId, contentKey, contentKeyKind, runId, TrackEntryMapper.WithStoredContentWhereUnobserved(row, existing.Values), now);
                    foreach (var k in changed) dal.CloseOpenDrift(entityId, TrackEntryMapper.ContentColumnName(k));
                    contentWritten = true; r.Confirmed++;
                    StoreLog.Debug($"description confirmed on {displayPath} by {producerKey}@{producerVersion} (was {existing.ProducerKey}@{existing.ProducerVersion}, agreeing within noise): fresh values and run stored");
                }
                if (!contentWritten && fills.Count > 0)
                {
                    // T01: a fill never re-stamps the row — the retained columns keep the run that measured them (existing.RunId);
                    // the filling run exists as its own run row, so nothing is lost by not moving the stamp.
                    dal.UpsertContent(entityId, contentKey, contentKeyKind, existing.RunId, TrackEntryMapper.WithStoredContentExcept(row, existing.Values, fills), now);
                    contentWritten = true;
                    if (!drifted) r.Filled++;   // a drifting arrival that also fills is one entry, already counted as drift (review 2026-10-22 F1)
                    StoreLog.Info($"description filled on {displayPath}: {fills.Count} column(s) that were null now have values ({TrackEntryMapper.ContentColumnName(fills[0])}{(fills.Count > 1 ? ", …" : "")})");
                }
            }

            // Keeper 067: a merge, not a whole-row replace. A copy fact the arriving catalog did not carry keeps the stored value (a
            // catalog without trackId — the export cut — wrote 0 over every stored id), and a value equal to the stored one at the
            // writer's precision is the same observation (a 1-dp catalog duration downgraded the store's 6-dp one on any rewrite).
            var existingCopy = dal.ReadCopyValues(locationId);
            bool catalogImport = string.Equals(producerKey, "import", StringComparison.Ordinal);   // a scan's arrival is a fresh measurement (keeper 070)
            // D4 (execution review 2026-10-21): a legacy-hash alias is a KNOWN-OLDER observation — "newer wins" cannot apply to its copy
            // facts either. It may fill a copy fact the store has no value for; it never replaces one (the older catalog's title and a
            // fabricated head hash were going over the scan's).
            ulong? observedCopy = entry.CopyObserved;
            if (legacyAlias) observedCopy = (entry.CopyObserved ?? TrackEntryMapper.AllCopyObserved) & TrackEntryMapper.CopyMaskWhereNull(existingCopy);
            var copyRow = TrackEntryMapper.MergeCopy(existingCopy, row, observedCopy, catalogImport, out var copyDiff);
            if (copyRow != null)
            {
                StoreLog.Debug($"copy facts written for {displayPath}: {copyDiff}");
                dal.UpsertCopy(locationId, runId, copyRow, now);
                copyWritten = true;
            }

            if (!contentWritten && !copyWritten && !drifted) r.Unchanged++;
            else
            {
                r.VisibleChanges++;   // T07: a description, copy fact or drift record a reader can see
                if (!contentWritten && !drifted) r.CopyOnly++;   // only copy facts moved (mtime, size, tags): its own bucket, never unnamed
            }
        }

        /// <summary>True when both are the same producer and both versions are known extractor builds (<c>output-x64.N</c>) with
        /// the arrival's N lower. Anything unorderable (different producer keys, an import, an md5:/unknown build) is false:
        /// the arrival then replaces, with its trace, rather than the code guessing an order.</summary>
        internal static bool ArrivalIsOlder(string? storedKey, string? storedVersion, string arrivingKey, string arrivingVersion)
        {
            if (!string.Equals(storedKey, arrivingKey, StringComparison.Ordinal)) return false;
            return BuildNumber(storedVersion) is int s && BuildNumber(arrivingVersion) is int a && a < s;
        }
        static int? BuildNumber(string? version)
        {
            const string prefix = "output-x64.";
            if (version == null || !version.StartsWith(prefix, StringComparison.Ordinal)) return null;
            return int.TryParse(version.Substring(prefix.Length), out var n) ? n : (int?)null;
        }

        static void ImportLedgers(StoreDal dal, List<(string Path, long Id)> roots, bool mirror, string dir, string now, ImportReport r)
        {
            string Mtime(string f) => File.GetLastWriteTimeUtc(f).ToString("o");

            var errorsCsv = Path.Combine(dir, "mbxmoods-errors.csv");
            foreach (var row in LedgerImport.ReadErrors(errorsCsv))
            {
                var e = EntityForPath(dal, roots, mirror, row.Path, errorsCsv, now, r);
                if (e == null) continue;
                var started = Mtime(errorsCsv);
                // Keyed on content, not the CSV's mtime: every scan rewrites the file (review #5).
                if (dal.RunExistsContent("essentia", e, "failed", errorsCsv, row.Error, row.FailedComponents)) continue;
                dal.InsertRun("essentia", "unknown", e, null, null, null, started, null, null, null, "failed", row.FailedComponents, null, row.Error, errorsCsv);
                r.ErrorsRows++;
            }

            var skippedCsv = Path.Combine(dir, "mbxmoods-skipped.csv");
            foreach (var row in LedgerImport.ReadSkipped(skippedCsv))
            {
                var e = EntityForPath(dal, roots, mirror, row.Path, skippedCsv, now, r);
                if (e == null) continue;
                var started = row.Timestamp ?? Mtime(skippedCsv);
                if (dal.RunExists("essentia", e, "skipped", skippedCsv, started)) continue;
                dal.InsertRun("essentia", "unknown", e, null, null, null, started, null, null, null, "skipped", null, row.Reason, null, skippedCsv);
                r.SkippedRows++;
            }

            var reviewPath = Path.Combine(dir, ReviewLedger.FileName);
            if (File.Exists(reviewPath))
            {
                var ledger = ReviewLedger.Load(reviewPath, out var err);
                if (err != null) StoreLog.Warn($"review ledger unreadable, skipped: {err}");
                foreach (var rec in ledger.Records)
                {
                    var e = EntityForPath(dal, roots, mirror, rec.Path, reviewPath, now, r);
                    if (e == null) continue;
                    var note = rec.Reason + (rec.StateReason != null ? " | " + rec.StateReason : "");
                    dal.UpsertWorkItem(e, "essentia", "unknown", "parked", rec.Attempts, ReviewLedger.StateName(rec.State).ToLowerInvariant(), note);
                    r.ReviewRows++;
                }
            }

            foreach (var csv in Directory.GetFiles(dir, "mbxmoods-verify*.csv"))
            {
                var checkedUtc = Mtime(csv);
                foreach (var row in LedgerImport.ReadVerify(csv))
                {
                    long? loc = null;
                    var rf = RootFor(roots, row.Path);
                    if (rf != null) { CanonicalPath.Split(rf.Value.Path, row.Path, out _, out var canon); loc = dal.FindLocation(rf.Value.Id, canon, out _); }
                    var src = csv + "#" + row.Path;
                    if (dal.CheckExists(loc, row.Status, src)) continue;
                    dal.InsertCheck(loc, null, "verify", row.Status, row.Detail, checkedUtc, src);
                    r.VerifyRows++;
                }
            }

            foreach (var receipt in Directory.GetFiles(dir, "*-verify*.receipt.json"))
            {
                var started = Mtime(receipt);
                var contentSha = FileSha256(receipt);   // the receipt's identity is its content, carried in tool_version (review #5)
                if (dal.RunExistsBySourceVersion("verify", receipt, contentSha)) continue;
                dal.InsertRun("verify", "1", null, null, null, null, started, null, null, contentSha, "ok", null, null, null, receipt);
                r.ReceiptRows++;
            }
        }

        /// <summary>A ledger row's path becomes an entity (path facet only) so runs and work items have
        /// something to hang on; a path under none of the roots is logged and dropped.</summary>
        static string? EntityForPath(StoreDal dal, List<(string Path, long Id)> roots, bool mirror, string path, string source, string now, ImportReport r)
        {
            var rf = RootFor(roots, path);
            // Stream URLs and files outside the library land in the skip ledger by design; they are
            // counted here (and in the result JSON), not warned one by one — 1,980 of them on X:\Library.
            if (rf == null) { r.LedgerUnrooted++; StoreLog.Debug($"ledger path under no root, not imported: {path}"); return null; }
            CanonicalPath.Split(rf.Value.Path, path, out var rel, out var canon);
            // A path the catalog already holds keeps its location untouched (review #1: the ledger upsert
            // nulled a cataloged entry's lastModified and broke the round-trip gate).
            if (dal.FindLocation(rf.Value.Id, canon, out var existingEntity) != null) return existingEntity;
            var res = EntityResolver.Resolve(dal, null, FacetKinds.ShaFrames, rf.Value.Id, canon, source, now, matchPathAcrossRoots: mirror);
            dal.UpsertLocation(res.EntityId, rf.Value.Id, rel, canon, path, null, now);
            return res.EntityId;
        }

        static string FileSha256(string path)
        {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(path);
            return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>The prevention half of <see cref="StoreDal.FoldNestedRoots"/>, in the caller's transaction, right after a root is
        /// minted: a new root may contain one the store already had, or sit inside one. Folding there means no commit ever leaves
        /// nested primaries (they made every later publication refuse), and this import is routed through the surviving roots.</summary>
        static List<(string Path, long Id)> FoldNestedRoots(StoreDal dal, List<(string Path, long Id)> rootIds, string now)
        {
            var folds = dal.FoldNestedRoots();
            if (folds.Count == 0) return rootIds;
            if (folds.Any(f => f.Merged > 0)) VerdictStore.DeriveAll(dal, now);   // a merge dropped a verdict row
            dal.AdvanceGeneration();
            foreach (var f in folds) StoreLog.Info("root " + f);
            var into = folds.ToDictionary(f => f.InnerId);
            return rootIds.Select(x => into.TryGetValue(x.Id, out var f) ? (f.Outer, f.OuterId) : x).Distinct().ToList();
        }

        static (string Path, long Id)? RootFor(List<(string Path, long Id)> roots, string path)
        {
            var p = path.Replace('/', '\\');
            (string Path, long Id)? best = null;
            foreach (var root in roots)
            {
                var rt = root.Path.Replace('/', '\\').TrimEnd('\\');
                if (p.Length > rt.Length + 1 && p.StartsWith(rt, StringComparison.OrdinalIgnoreCase) && p[rt.Length] == '\\')
                    if (best == null || rt.Length > best.Value.Path.TrimEnd('\\').Length) best = root;
            }
            return best;
        }

        static (string Sha, long Bytes) HashCatalog(string catalog)
        {
            var files = new List<string>();
            if (File.Exists(catalog)) files.Add(catalog);
            else for (int i = 1; File.Exists(catalog + "." + i); i++) files.Add(catalog + "." + i);
            long bytes = 0;
            using var sha = SHA256.Create();
            foreach (var f in files)
            {
                using var fs = File.OpenRead(f);
                bytes += fs.Length;
                var buf = new byte[1 << 16]; int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0) sha.TransformBlock(buf, 0, n, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return (BitConverter.ToString(sha.Hash!).Replace("-", "").ToLowerInvariant(), bytes);
        }

        static void WriteResult(ImportReport r, string storePath, string rootRole, string producer)
        {
            var j = new JsonObject
            {
                ["kind"] = "store-import-result", ["schemaVersion"] = 1, ["generatedAt"] = DateTime.UtcNow.ToString("o"),
                ["store"] = storePath, ["producer"] = producer.Split('@')[0], ["producerVersion"] = producer.Contains("@") ? producer.Substring(producer.IndexOf('@') + 1) : "",
                ["catalog"] = r.Catalog, ["catalogSha256"] = r.CatalogSha256, ["catalogBytes"] = r.CatalogBytes,
                ["roots"] = new JsonArray(r.Roots.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()), ["rootRole"] = rootRole,
                ["entries"] = r.Entries, ["unrooted"] = r.Unrooted,
                ["entitiesCreated"] = r.EntitiesCreated, ["matchedBySha"] = r.MatchedHash, ["matchedByPath"] = r.MatchedPath, ["createdWithoutSha"] = r.CreatedNoSha,
                ["locations"] = r.Locations, ["locationsNew"] = r.LocationsNew, ["conflicts"] = r.Conflicts,
                ["unchanged"] = r.Unchanged, ["remeasured"] = r.Remeasured, ["drift"] = r.Drift, ["contentReplaced"] = r.ContentReplaced, ["contentRekeyed"] = r.ContentRekeyed, ["stale"] = r.Stale,
                ["artifacts"] = r.Artifacts, ["artifactsShared"] = r.ArtifactsShared, ["artifactBytes"] = r.ArtifactBytes,
                ["legacySha"] = r.LegacySha, ["legacyShaUnrecoverable"] = r.LegacyShaUnrecoverable,
                ["ledgers"] = new JsonObject { ["errors"] = r.ErrorsRows, ["skipped"] = r.SkippedRows, ["review"] = r.ReviewRows, ["verify"] = r.VerifyRows, ["receipts"] = r.ReceiptRows, ["unrooted"] = r.LedgerUnrooted },
                ["storeBytesBefore"] = r.StoreBytesBefore, ["storeBytesAfter"] = r.StoreBytesAfter, ["elapsedMs"] = r.ElapsedMs,
            };
            File.WriteAllText(r.ResultPath, j.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
