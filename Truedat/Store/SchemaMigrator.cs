using System;
using System.IO;

namespace Truedat.Store
{
    /// <summary>Thrown for store-level refusals that are not SQLite errors (schema newer than the exe, …).</summary>
    internal sealed class StoreException : Exception
    {
        public StoreException(string message) : base(message) { }
    }

    /// <summary>Names shared by the DDL (Schema/v1.sql), the drift test and the C4 mapper.</summary>
    internal static class StoreSchema
    {
        internal const int CurrentVersion = 1;

        /// <summary>Columns of track_feature that are not catalog JSON keys.</summary>
        internal static readonly string[] TrackFeatureMetaColumns = { "location_id", "content_key", "imported_run_id", "updated_utc" };

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

        internal static void Apply(SqliteDb db, string truedatVersion)
        {
            int current = ReadVersion(db);
            if (current > StoreSchema.CurrentVersion)
                throw new StoreException(
                    $"The store {db.Path} is at schema version {current}, which is newer than this truedat understands " +
                    $"(schema {StoreSchema.CurrentVersion}). Use the truedat that created it, or newer. Nothing was changed.");
            if (current == StoreSchema.CurrentVersion)
            {
                StoreLog.Debug($"schema at v{current}, nothing to apply");
                return;
            }

            using (var tx = db.BeginTransaction())
            {
                for (int v = current + 1; v <= StoreSchema.CurrentVersion; v++)
                {
                    StoreLog.Info($"applying schema v{v} to {db.Path}");
                    db.Exec(LoadScript(v));
                    using (var st = db.Prepare("INSERT INTO schema_version(version, applied_utc, truedat_version) VALUES (?, ?, ?);"))
                    {
                        st.Bind(1, (long)v).Bind(2, DateTime.UtcNow.ToString("o")).Bind(3, truedatVersion);
                        st.Step();
                    }
                }
                tx.Complete();
            }
            StoreLog.Info($"schema now v{StoreSchema.CurrentVersion}");
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
