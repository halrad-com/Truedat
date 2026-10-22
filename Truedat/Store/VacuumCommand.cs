using System;
using System.Diagnostics;
using System.IO;

namespace Truedat.Store
{
    internal sealed class VacuumReport
    {
        public string Path = "";
        public long BytesBefore, BytesAfter;
        public int PageSizeBefore, PageSizeAfter;
        public long PagesBefore, PagesAfter;
        public double Seconds;
    }

    /// <summary>--store-vacuum [--page-size N]: rebuild an existing store, at a new page size when asked.
    /// Page size is per file and a page-size CHANGE needs a VACUUM outside WAL mode — so this verb is
    /// journal_mode=DELETE (which checkpoints), set the size, VACUUM, back to WAL; that mode switch needs the
    /// store closed everywhere, so the verb refuses ("database is locked") while the hub holds the store. A plain
    /// VACUUM at the SAME page size runs fine in WAL mode with readers open: that is <see cref="Compact"/>, the scan's
    /// routine end-of-run compaction. Nothing stored changes; the round-trip gate proves it (self-test).
    /// Reversible: run it again with another size.</summary>
    internal static class VacuumCommand
    {
        internal static int Run(string? storePath, string? pageSizeText, bool audit)
        {
            try
            {
                StoreLog.SetVerbose(audit);
                int? pageSize = null;
                if (pageSizeText != null)
                {
                    if (!int.TryParse(pageSizeText, out var n))
                        throw new StoreException($"--page-size {pageSizeText} is not a whole number. SQLite page sizes are powers of two from 512 to 65536 (default {SqliteDb.DefaultPageSize}). Nothing was changed.");
                    pageSize = n;
                }
                var r = Execute(storePath, pageSize);
                Console.WriteLine($"Store: {r.Path}");
                Console.WriteLine($"  before: {r.BytesBefore:N0} bytes = {r.PagesBefore:N0} pages × {r.PageSizeBefore}");
                double pct = r.BytesBefore > 0 ? 100.0 * (r.BytesAfter - r.BytesBefore) / r.BytesBefore : 0;
                Console.WriteLine($"  after:  {r.BytesAfter:N0} bytes = {r.PagesAfter:N0} pages × {r.PageSizeAfter}  ({pct:+0.0;-0.0;0.0}%)");
                Console.WriteLine($"  {r.Seconds:F1} s; the store is back in WAL. Run --store-roundtrip to prove the contents.");
                return 0;
            }
            catch (StoreException ex) { Console.Error.WriteLine("Error: " + ex.Message); return ex.ExitCode; }
            catch (Exception ex) { Console.Error.WriteLine("Error: store-vacuum failed: " + ex.Message); return 1; }
        }

        /// <summary>Operator 2026-10-22 ("can we do a vacuum at the end as routine maintenance"): the scan's end-of-run compaction. A plain
        /// VACUUM in WAL mode — NOT the verb's journal-mode switch, which needs the store closed everywhere and fails with "database is
        /// locked" while the hub holds it (a plain VACUUM with an idle reader open does not). Runs every time — no
        /// freelist gate: most reclaimable space is half-empty pages, which freelist_count cannot see, so a free-page
        /// threshold skipped stores that were mostly reclaimable. Skipped (null) with a warning only when the volume has
        /// less than twice the store free, because VACUUM in WAL mode writes the whole database into the WAL first. The WAL is
        /// checkpointed and truncated afterwards so the file IS the store again. Nothing stored changes (round-trip pinned).</summary>
        internal static VacuumReport? Compact(string storePath)
        {
            var r = new VacuumReport();
            var sw = Stopwatch.StartNew();
            using var os = OpenStore.Open(storePath, create: false, VersionInfo.Display, DateTime.UtcNow.ToString("o"));
            r.Path = os.Path;
            os.Dal.ResetAll();                                       // VACUUM refuses while any statement is mid-step
            r.PageSizeBefore = r.PageSizeAfter = int.Parse(os.Db.ExecScalar("PRAGMA page_size;") ?? "0");
            r.PagesBefore = long.Parse(os.Db.ExecScalar("PRAGMA page_count;") ?? "0");
            r.BytesBefore = r.PagesBefore * r.PageSizeBefore;
            if (r.PagesBefore <= 0) return null;
            long? avail = AvailableBytesOn(os.Path);
            if (avail.HasValue && avail.Value < r.BytesBefore * 2)
            {
                StoreLog.Warn($"compaction skipped: only {avail.Value:N0} bytes free on the volume (a VACUUM needs about twice the store, {r.BytesBefore * 2:N0})");
                return null;
            }
            os.Db.Exec("VACUUM;");
            os.Db.Exec("PRAGMA wal_checkpoint(TRUNCATE);");
            r.PagesAfter = long.Parse(os.Db.ExecScalar("PRAGMA page_count;") ?? "0");
            r.BytesAfter = r.PagesAfter * r.PageSizeAfter;
            r.Seconds = sw.Elapsed.TotalSeconds;
            return r;
        }

        /// <summary>Free bytes on the volume holding <paramref name="path"/>; null when it cannot be read (a UNC path, an odd root).</summary>
        static long? AvailableBytesOn(string path)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root) || root!.StartsWith(@"\\", StringComparison.Ordinal)) return null;
                return new DriveInfo(root).AvailableFreeSpace;
            }
            catch { return null; }
        }

        internal static VacuumReport Execute(string? storePath, int? pageSize)
        {
            int target = pageSize ?? SqliteDb.DefaultPageSize;
            if (target < 512 || target > 65536 || (target & (target - 1)) != 0)
                throw new StoreException($"--page-size {target} is not valid: SQLite page sizes are powers of two from 512 to 65536 (default {SqliteDb.DefaultPageSize}). Nothing was changed.");

            var r = new VacuumReport();
            var sw = Stopwatch.StartNew();
            using (var os = OpenStore.Open(storePath, create: false, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
            {
                r.Path = os.Path;
                os.Dal.ResetAll();                                   // VACUUM refuses while any statement is mid-step
                r.PageSizeBefore = int.Parse(os.Db.ExecScalar("PRAGMA page_size;") ?? "0");
                r.PagesBefore = long.Parse(os.Db.ExecScalar("PRAGMA page_count;") ?? "0");
                os.Db.Exec("PRAGMA journal_mode=DELETE;");           // leaves WAL and checkpoints, so the file length below is the whole store
                r.BytesBefore = new FileInfo(os.Path).Length;
                StoreLog.Info($"vacuum {os.Path}: {r.BytesBefore:N0} bytes at page_size {r.PageSizeBefore} -> page_size {target}");
                try
                {
                    os.Db.Exec($"PRAGMA page_size={target};");
                    os.Db.Exec("VACUUM;");
                }
                finally
                {
                    os.Db.Exec("PRAGMA journal_mode=WAL;");          // every other verb expects WAL; restore it even when VACUUM failed
                }
                r.PageSizeAfter = int.Parse(os.Db.ExecScalar("PRAGMA page_size;") ?? "0");
                r.PagesAfter = long.Parse(os.Db.ExecScalar("PRAGMA page_count;") ?? "0");
                r.BytesAfter = new FileInfo(os.Path).Length;
            }
            r.Seconds = sw.Elapsed.TotalSeconds;
            StoreLog.Info($"vacuum done: {r.BytesAfter:N0} bytes = {r.PagesAfter:N0} × {r.PageSizeAfter} in {r.Seconds:F1} s");
            return r;
        }
    }
}
