using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Truedat.Store
{
    /// <summary>
    /// store-v3 T09 — snapshot / transfer / restore of the STORE itself, which is a different act from
    /// exporting a catalog: the catalog is derived (restoring it puts back the files the hub reads), the
    /// store is the authority (restoring it puts back what those files are derived FROM).
    ///
    /// A live store is never copied as a file. Under WAL the committed state lives across three files
    /// (<c>truedat.db</c>, <c>-wal</c>, <c>-shm</c>) and a plain copy of the first one is a torn read of
    /// whatever was in flight (G07). Every copy here goes through <c>VACUUM INTO</c>, which SQLite runs
    /// against a read snapshot and writes as one consistent, already-compacted database.
    ///
    /// Restore is a MUTATION, not a transfer of state the files already show: the caller publishes the
    /// restored store under a NEW generation, so a hub holding the old triple sees it move. The store
    /// being replaced is snapshotted first, so undoing a restore is the same command.
    /// </summary>
    internal static class StoreTransfer
    {
        /// <summary>The store's entry name inside a snapshot archive. An archive carrying it is a store
        /// snapshot; one that does not is a catalog archive and cannot restore a store.</summary>
        internal const string StoreEntryName = "truedat.db";

        internal readonly struct StoreCopy
        {
            internal StoreCopy(string path, long bytes, string sha256, int schema, long generation, int entries)
            { Path = path; Bytes = bytes; Sha256 = sha256; Schema = schema; Generation = generation; Entries = entries; }
            internal string Path { get; }
            internal long Bytes { get; }
            internal string Sha256 { get; }
            internal int Schema { get; }
            internal long Generation { get; }
            internal int Entries { get; }
        }

        /// <summary>Write a consistent copy of <paramref name="storePath"/> to <paramref name="destFile"/> via
        /// <c>VACUUM INTO</c> — never a file copy, so a live WAL store is safe to snapshot while a scan reads it.
        /// The destination must not exist: SQLite refuses to overwrite, and silently overwriting a store is the
        /// mistake this verb exists to make impossible. Returns the facts a manifest records.</summary>
        internal static StoreCopy VacuumInto(string storePath, string destFile)
        {
            var dest = Path.GetFullPath(destFile);
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir!);
            if (File.Exists(dest)) throw new StoreException($"the snapshot target already exists: {dest}");

            // The facts are read on the SAME connection that then copies, and NOT inside a transaction: VACUUM INTO
            // refuses to run in one, and opening the finished copy to interrogate it would put that file into WAL mode
            // and leave a handle on it — which is how the staged copy ends up undeletable. truedat is single-writer by
            // design, so between these reads and the copy nothing of ours commits; VACUUM INTO's own read snapshot is
            // what makes the copy itself consistent.
            int schema; long generation; int entries;
            using (var db = SqliteDb.Open(storePath, create: false))
            {
                schema = SchemaMigrator.ReadVersion(db);
                generation = ReadGeneration(db);
                entries = (int)(ToLong(db.ExecScalar("SELECT COUNT(*) FROM track_location WHERE state <> 'retired';")) ?? 0);
                db.Exec($"VACUUM INTO '{dest.Replace("'", "''")}';");
            }
            return new StoreCopy(dest, new FileInfo(dest).Length, Sha256File(dest), schema, generation, entries);
        }

        /// <summary>True when a ZIP archive carries a store. False for a gz or plain catalog, which cannot carry one.</summary>
        internal static bool ArchiveContainsStore(string archivePath)
        {
            try
            {
                if (CatalogArchive.SniffFile(archivePath) != CatalogArchive.CatalogFormat.Zip) return false;
                using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
                return FindStoreEntry(zip) != null;
            }
            catch (Exception) { return false; }
        }

        static ZipArchiveEntry? FindStoreEntry(ZipArchive zip)
            => zip.GetEntry(StoreEntryName)
               ?? zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".db", StringComparison.OrdinalIgnoreCase));

        /// <summary>Extract the store out of a snapshot archive to <paramref name="destFile"/> (streaming).</summary>
        internal static void ExtractStoreTo(string archivePath, string destFile)
        {
            using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            var entry = FindStoreEntry(zip)
                ?? throw new StoreException($"the archive carries no {StoreEntryName}: {archivePath}");
            using var es = entry.Open();
            using var dst = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
            es.CopyTo(dst);
        }

        /// <summary>What a candidate store file has to be: it opens, it passes SQLite's own integrity check, and its
        /// schema is one this build reads. Throws <see cref="StoreException"/> naming the defect otherwise — a restore
        /// refuses a damaged or future store BEFORE the live one is replaced.</summary>
        internal static StoreCopy Validate(string candidate)
        {
            int schema; long generation; int entries;
            try
            {
                using var db = SqliteDb.Open(candidate, create: false);
                var check = db.ExecScalar("PRAGMA quick_check;");
                if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new StoreException($"the store in the archive fails SQLite's integrity check ({check ?? "no answer"}); nothing was replaced.");
                schema = SchemaMigrator.ReadVersion(db);
                if (schema <= 0)
                    throw new StoreException("the file in the archive is a database but not a truedat store (no schema version); nothing was replaced.");
                if (schema > StoreSchema.CurrentVersion)
                    throw new StoreException($"the store in the archive is schema v{schema} and this build reads v{StoreSchema.CurrentVersion}; "
                        + "nothing was replaced. Restore it with the truedat that wrote it, or upgrade truedat.");
                generation = ReadGeneration(db);
                entries = (int)(ToLong(db.ExecScalar("SELECT COUNT(*) FROM track_location WHERE state <> 'retired';")) ?? 0);
            }
            catch (StoreException) { throw; }
            catch (Exception ex)
            {
                throw new StoreException($"the file in the archive is not a readable store ({ex.Message}); nothing was replaced.");
            }
            return new StoreCopy(candidate, new FileInfo(candidate).Length, Sha256File(candidate), schema, generation, entries);
        }

        internal readonly struct RestoreResult
        {
            internal RestoreResult(StoreCopy restored, string? backupPath, long generation)
            { Restored = restored; BackupPath = backupPath; Generation = generation; }
            internal StoreCopy Restored { get; }
            /// <summary>The snapshot of the store that was replaced — null when there was none to replace.</summary>
            internal string? BackupPath { get; }
            /// <summary>The generation the restored store now carries: advanced, because a restore is a change the
            /// consumer must see even when the restored bytes are older than what it cached.</summary>
            internal long Generation { get; }
        }

        /// <summary>Replace <paramref name="storePath"/> with the store inside <paramref name="archivePath"/>:
        /// extract beside the target, validate it, snapshot the store being replaced (<c>VACUUM INTO</c>, rotated),
        /// swap atomically, drop the replaced store's WAL pair, advance the generation. The files are NOT published
        /// here — the caller publishes from the restored store, so the triple still has one writer (T07).</summary>
        internal static RestoreResult RestoreStore(string archivePath, string storePath, int keepBackups)
        {
            var store = Path.GetFullPath(storePath);
            var dir = Path.GetDirectoryName(store);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);

            var staged = store + ".restore.tmp";
            try { File.Delete(staged); } catch { }
            StoreCopy candidate;
            try
            {
                ExtractStoreTo(archivePath, staged);
                candidate = Validate(staged);
            }
            catch (Exception)
            {
                try { File.Delete(staged); } catch { }
                throw;
            }

            // The store being replaced becomes a snapshot of its own, so this is reversible by the same command. Its
            // generation is kept: the restored store has to advance past it (see below), never merely past its own.
            string? backup = null;
            long replacedGeneration = 0;
            if (File.Exists(store))
            {
                backup = store + ".bak." + DateTime.Now.ToString("yyyyMMdd.HHmmss") + ".db";
                try
                {
                    replacedGeneration = VacuumInto(store, backup).Generation;
                    RotateStoreBackups(store, keepBackups);
                }
                catch (Exception ex)
                {
                    try { File.Delete(staged); } catch { }
                    throw new StoreException($"could not snapshot the store being replaced ({ex.Message}); nothing was replaced.");
                }
            }

            try { Program.AtomicReplace(staged, store); }
            catch (Exception ex)
            {
                try { File.Delete(staged); } catch { }
                throw new StoreException($"could not install the restored store ({ex.Message}); the store was left as it was"
                    + (backup != null ? $" and its snapshot is {backup}" : "") + ".");
            }
            // A WAL/-shm pair left from the replaced store describes a database that is gone, and SQLite would read it
            // as this file's, so they go with it.
            foreach (var side in new[] { store + "-wal", store + "-shm" })
                try { if (File.Exists(side)) File.Delete(side); } catch { }

            // The restored store's generation must exceed the one it REPLACED, not just its own. A snapshot taken at
            // generation 1 and restored over a store at generation 2 would otherwise republish generation 2 with
            // different content, and a consumer holding 2 would never look again — the one thing the generation
            // exists to prevent.
            long generation;
            using (var os = OpenStore.Open(store, create: false, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
            using (var tx = os.Db.BeginTransaction())
            {
                long replaced = replacedGeneration;
                generation = os.Dal.AdvanceGeneration();
                if (generation <= replaced)
                {
                    generation = replaced + 1;
                    os.Dal.MetaSet(StoreDal.GenerationKey, generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                tx.Complete();
            }
            return new RestoreResult(candidate, backup, generation);
        }

        /// <summary>Keep the newest <paramref name="keepN"/> store snapshots beside a store (names matching
        /// "&lt;store file name&gt;.bak."). Rotation, never truncation: keepN &lt;= 0 keeps everything — the ruling
        /// the catalog backups already follow.</summary>
        internal static int RotateStoreBackups(string storePath, int keepN)
        {
            if (keepN <= 0) return 0;
            var dir = Path.GetDirectoryName(Path.GetFullPath(storePath));
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return 0;
            var prefix = Path.GetFileName(storePath) + ".bak.";
            int deleted = 0;
            foreach (var old in Directory.GetFiles(dir!)
                         .Where(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                         .Skip(keepN))
                try { File.Delete(old); deleted++; } catch { }
            return deleted;
        }

        /// <summary>store-v3 T10: every live entry of another store, display path → entry, across EVERY root — a shard
        /// store minted on another machine names its own root, and a merge reading only the primary root would drop
        /// exactly the shard's share. Read in one snapshot; the source is opened read-only and never migrated.</summary>
        internal static Dictionary<string, TrackEntry> ReadEntries(string storePath)
        {
            var result = new Dictionary<string, TrackEntry>(PathComparer.Instance);
            using var db = SqliteDb.Open(storePath, create: false);
            var schema = SchemaMigrator.ReadVersion(db);
            if (schema <= 0) throw new StoreException($"not a truedat store: {storePath}");
            if (schema > StoreSchema.CurrentVersion)
                throw new StoreException($"{storePath} is schema v{schema} and this build reads v{StoreSchema.CurrentVersion}.");
            using var dal = new StoreDal(db);
            using var snap = db.BeginRead();
            foreach (var root in dal.ListRoots())
                foreach (var (display, entry) in dal.ReadEntries(root.Id))
                    result[display] = entry;
            snap.Complete();
            return result;
        }

        /// <summary>True when a file is a SQLite database by its own header magic — the one honest way to tell a merge
        /// source that is a store from one that is a catalog, since either may be named anything.</summary>
        internal static bool LooksLikeStore(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var head = new byte[16];
                int n = fs.Read(head, 0, head.Length);
                return n == 16 && Encoding.ASCII.GetString(head, 0, 15) == "SQLite format 3";
            }
            catch (Exception) { return false; }
        }

        /// <summary>The store's generation read with a plain scalar query rather than through <see cref="StoreDal"/>.
        /// The DAL caches prepared statements and holds them until it is disposed; an undisposed one leaves the
        /// connection with unfinalized statements, so <c>sqlite3_close_v2</c> defers and the FILE stays locked —
        /// which is exactly what stopped a validated copy from being moved into place.</summary>
        static long ReadGeneration(SqliteDb db)
            => ToLong(db.ExecScalar($"SELECT value FROM store_meta WHERE key = '{StoreDal.GenerationKey}';")) ?? 0;

        static long? ToLong(string? s) => long.TryParse(s, out var v) ? v : (long?)null;

        internal static string Sha256File(string path)
        {
            using var sha = SHA256.Create();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            var sb = new StringBuilder(64);
            foreach (var b in sha.ComputeHash(fs)) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
