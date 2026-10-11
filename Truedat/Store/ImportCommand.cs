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
        public int ErrorsRows, SkippedRows, ReviewRows, VerifyRows, ReceiptRows;
        /// <summary>Ledger rows whose path is under no root (stream URLs, videos outside the library) — counted, not imported.</summary>
        public int LedgerUnrooted;
        /// <summary>Entries whose lastModified is older than the store's for the same content key — skipped, never regress a row.</summary>
        public int Stale;
        public long StoreBytesBefore, StoreBytesAfter, ElapsedMs;
        public string ResultPath = "";
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
            try
            {
                var r = Execute(o);
                Console.WriteLine($"Imported {r.Entries} entries from {r.Catalog} into {o.StorePath}");
                Console.WriteLine($"  roots: {string.Join(", ", r.Roots)}");
                Console.WriteLine($"  entities: {r.EntitiesCreated} created, {r.MatchedHash} matched by sha, {r.MatchedPath} matched by path, {r.CreatedNoSha} created without a sha");
                Console.WriteLine($"  locations: {r.Locations} ({r.LocationsNew} new); conflicts: {r.Conflicts}; legacy FLAC sha: {r.LegacySha} kept, {r.LegacyShaUnrecoverable} unrecoverable");
                Console.WriteLine($"  ledgers: {r.ErrorsRows} errors, {r.SkippedRows} skipped, {r.ReviewRows} review, {r.VerifyRows} verify rows, {r.ReceiptRows} receipts" +
                                  (r.LedgerUnrooted > 0 ? $"; {r.LedgerUnrooted} ledger rows under no root (stream URLs, files outside the library) not imported" : ""));
                Console.WriteLine($"  store: {r.StoreBytesBefore:N0} -> {r.StoreBytesAfter:N0} bytes; {r.ElapsedMs / 1000.0:F1} s; result: {r.ResultPath}");
                if (r.Unrooted > 0) Console.WriteLine($"  WARNING: {r.Unrooted} entries were under none of the roots and were not imported (pass --library-root)");
                return 0;
            }
            catch (StoreException ex)
            {
                Console.Error.WriteLine("Error: " + ex.Message);
                return ex.ExitCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error: import failed: " + ex.Message);
                return 1;
            }
        }

        internal static ImportReport Execute(ImportOptions o)
        {
            var sw = Stopwatch.StartNew();
            var r = new ImportReport();
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
                // 4. Entries, in batches.
                bool mirror = role == "mirror";
                var keys = tracks.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
                int processed = 0;
                for (int b = 0; b < keys.Count; b += Math.Max(1, o.BatchSize))
                {
                    using var tx = db.BeginTransaction();
                    for (int i = b; i < Math.Min(keys.Count, b + Math.Max(1, o.BatchSize)); i++)
                    {
                        ImportEntry(dal, rootIds, mirror, keys[i], tracks[keys[i]], importRun, source, now, r);
                        processed++;
                        if (o.FailAfterEntries > 0 && processed == o.FailAfterEntries)
                            throw new InvalidOperationException($"fail-after seam fired at entry {processed}");
                    }
                    tx.Complete();
                    StoreLog.Debug($"batch committed: {processed}/{keys.Count}");
                }

                // 5. Ledgers beside the catalog.
                var dir = Path.GetDirectoryName(catalog) ?? ".";
                using (var tx = db.BeginTransaction())
                {
                    ImportLedgers(dal, rootIds, mirror, dir, now, r);
                    tx.Complete();
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
            WriteResult(r, o);
            StoreLog.Info($"import done: {r.Entries} entries, {r.EntitiesCreated} entities created, {r.LocationsNew} new locations, {r.Conflicts} conflicts, {r.ElapsedMs} ms");
            return r;
        }

        static void ImportEntry(StoreDal dal, List<(string Path, long Id)> roots, bool mirror, string key, TrackEntry entry, long importRun, string source, string now, ImportReport r)
        {
            var rf = RootFor(roots, key);
            if (rf == null) { r.Unrooted++; StoreLog.Warn($"not under any root, skipped: {key}"); return; }
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

            long loc = dal.UpsertLocation(res.EntityId, rf.Value.Id, rel, canon, key, entry.LastModified.ToString("o"), now);
            r.Locations++; if (isNewLocation) r.LocationsNew++;
            dal.UpsertFeature(loc, contentKey, sha != null ? kind : "entity", importRun, entry, now);
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

        static void WriteResult(ImportReport r, ImportOptions o)
        {
            var j = new JsonObject
            {
                ["kind"] = "store-import-result", ["schemaVersion"] = 1, ["generatedAt"] = DateTime.UtcNow.ToString("o"),
                ["store"] = o.StorePath, ["catalog"] = r.Catalog, ["catalogSha256"] = r.CatalogSha256, ["catalogBytes"] = r.CatalogBytes,
                ["roots"] = new JsonArray(r.Roots.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()), ["rootRole"] = o.RootRole,
                ["entries"] = r.Entries, ["unrooted"] = r.Unrooted,
                ["entitiesCreated"] = r.EntitiesCreated, ["matchedBySha"] = r.MatchedHash, ["matchedByPath"] = r.MatchedPath, ["createdWithoutSha"] = r.CreatedNoSha,
                ["locations"] = r.Locations, ["locationsNew"] = r.LocationsNew, ["conflicts"] = r.Conflicts,
                ["legacySha"] = r.LegacySha, ["legacyShaUnrecoverable"] = r.LegacyShaUnrecoverable,
                ["ledgers"] = new JsonObject { ["errors"] = r.ErrorsRows, ["skipped"] = r.SkippedRows, ["review"] = r.ReviewRows, ["verify"] = r.VerifyRows, ["receipts"] = r.ReceiptRows, ["unrooted"] = r.LedgerUnrooted },
                ["storeBytesBefore"] = r.StoreBytesBefore, ["storeBytesAfter"] = r.StoreBytesAfter, ["elapsedMs"] = r.ElapsedMs,
            };
            File.WriteAllText(r.ResultPath, j.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
