using System;
using System.IO;

namespace Truedat.Store
{
    /// <summary>Thrown for store-level refusals that are not SQLite errors (schema newer than the exe, …).</summary>
    internal sealed class StoreException : Exception
    {
        /// <summary>Process exit code for this refusal: 1 generic, StoreExitCodes.StoreUnavailable (4) when SQLite itself is missing or too old.</summary>
        public int ExitCode { get; }
        public StoreException(string message, int exitCode = 1) : base(message) { ExitCode = exitCode; }
    }

    /// <summary>Names shared by the DDL (Schema/v1.sql), the drift test and the C4 mapper.</summary>
    internal static class StoreSchema
    {
        internal const int CurrentVersion = 3;   // v3 (T04): typed columns — scaled INTEGER, int32 vectors, sparse histogram

        /// <summary>Columns of track_content / track_copy that are not catalog JSON keys (v2 split).</summary>
        internal static readonly string[] TrackContentMetaColumns = { "entity_id", "content_key", "content_key_kind", "run_id", "updated_utc" };
        internal static readonly string[] TrackCopyMetaColumns = { "location_id", "run_id", "updated_utc" };

        /// <summary>The one flattening rule: nested blocks join with '_', and '.' in a key becomes '_'
        /// (<c>fingerprint.v1</c> → <c>fingerprint_v1</c>). Used by the drift test and the mapper so they
        /// cannot disagree.</summary>
        internal static string FlattenKey(string? prefix, string key)
            => (string.IsNullOrEmpty(prefix) ? key : prefix + "_" + key).Replace('.', '_');
    }

    /// <summary>Forward-only schema migrations from embedded <c>Schema/v{n}.sql</c> scripts. A store
    /// newer than this exe is refused, never guessed at (spec §3, settled core).</summary>
    internal static class SchemaMigrator
    {
        /// <summary>0 when the store has no schema_version table yet (a freshly created file).</summary>
        internal static int ReadVersion(SqliteDb db)
        {
            var has = db.ExecScalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='schema_version';");
            if (has != "1") return 0;
            var v = db.ExecScalar("SELECT max(version) FROM schema_version;");
            return string.IsNullOrEmpty(v) ? 0 : int.Parse(v!);
        }

        /// <summary><paramref name="targetVersion"/> is a test seam (build an older store to migrate from); production
        /// callers leave it null and always reach <see cref="StoreSchema.CurrentVersion"/>.</summary>
        internal static void Apply(SqliteDb db, string truedatVersion, int? targetVersion = null)
        {
            int target = targetVersion ?? StoreSchema.CurrentVersion;
            int current = ReadVersion(db);
            if (current > StoreSchema.CurrentVersion)
                throw new StoreException(
                    $"The store {db.Path} is at schema version {current}, which is newer than this truedat understands " +
                    $"(schema {StoreSchema.CurrentVersion}). Use the truedat that created it, or newer. Nothing was changed.");
            if (current >= target)
            {
                StoreLog.Debug($"schema at v{current}, nothing to apply");
                return;
            }

            // T04b: an EXISTING store is backed up before anything is changed — a consistent snapshot via VACUUM INTO (SQLite ≥ 3.27),
            // beside the store, named with the schema it holds — and the message names that path. A fresh file (current == 0)
            // has nothing to back up. The backup is the operator's "archive what I have" without a manual step.
            string? backup = null;
            if (current > 0)
            {
                var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                backup = $"{db.Path}.bak.v{current}.{stamp}";
                for (int n = 2; File.Exists(backup); n++) backup = $"{db.Path}.bak.v{current}.{stamp}-{n}";   // two migrations in one second (self-tests) never collide
                // The backup is a second copy of the whole store (a real store is hundreds of MB to a few GB); refuse up front, with the
                // numbers, rather than fail half way through VACUUM INTO on a full disk (review finding 7).
                try
                {
                    long need = new FileInfo(db.Path).Length;
                    var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(db.Path));
                    if (!string.IsNullOrEmpty(root))
                    {
                        long free = new DriveInfo(root!).AvailableFreeSpace;
                        if (free < need + need / 10)
                            throw new StoreException($"The store {db.Path} is schema v{current} and needs migrating to v{target}, which writes a backup copy first ({need / 1048576:N0} MB), but only {free / 1048576:N0} MB are free on {root}. Nothing was changed; free the space and open the store again.");
                    }
                }
                catch (StoreException) { throw; }
                catch (Exception ex) { StoreLog.Debug($"free-space check skipped: {ex.Message}"); }   // an exotic path (UNC without a drive) — VACUUM INTO's own error is the fallback
                db.Exec($"VACUUM INTO '{backup.Replace("'", "''")}';");
                StoreLog.Info($"store is schema v{current}; this build uses v{target}. Migrating now; backup first at {backup}");
                // A table rebuild (v3 re-stores every row into typed tables, then renames) follows SQLite's documented recipe: foreign-key
                // enforcement off for the rebuild, on again after. The rows are copied as they are; whatever referential state the old
                // store had, it keeps — the migration neither repairs nor judges it. (Must be set outside the transaction.)
                db.Exec("PRAGMA foreign_keys=OFF;");
            }

            try
            {
            using (var tx = db.BeginTransaction())
            {
                for (int v = current + 1; v <= target; v++)
                {
                    StoreLog.Info($"applying schema v{v} to {db.Path}");
                    db.Exec(LoadScript(v));
                    SchemaV2Migration.RunDataStep(db, v);
                    SchemaV3Migration.RunDataStep(db, v);
                    using (var st = db.Prepare("INSERT INTO schema_version(version, applied_utc, truedat_version) VALUES (?, ?, ?);"))
                    {
                        st.Bind(1, (long)v).Bind(2, DateTime.UtcNow.ToString("o")).Bind(3, truedatVersion);
                        st.Step();
                    }
                }
                tx.Complete();
            }
            }
            finally { if (current > 0) db.Exec("PRAGMA foreign_keys=ON;"); }
            StoreLog.Info($"schema now v{StoreSchema.CurrentVersion}" + (backup != null ? $" (backup of the previous store: {backup})" : ""));
        }

        static string LoadScript(int version)
        {
            var name = $"Truedat.Store.Schema.v{version}.sql";
            using var s = typeof(SchemaMigrator).Assembly.GetManifestResourceStream(name)
                ?? throw new StoreException($"schema script {name} is not embedded in this build");
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
    }
}
