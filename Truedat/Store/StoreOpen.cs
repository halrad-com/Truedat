using System;
using System.IO;

namespace Truedat.Store
{
    /// <summary>The one way every store verb opens a store: probe winsqlite3 (refuse, exit 4), open or
    /// create, apply the schema, read or mint the machine identity. Every refusal is a StoreException
    /// carrying the operator-facing sentence and the exit code.</summary>
    internal sealed class OpenStore : IDisposable
    {
        internal SqliteDb Db { get; }
        internal StoreDal Dal { get; }
        internal MachineIdentity Machine { get; }
        internal SqliteProbeResult Probe { get; }
        internal string Path { get; }

        OpenStore(SqliteDb db, StoreDal dal, MachineIdentity machine, SqliteProbeResult probe, string path)
        { Db = db; Dal = dal; Machine = machine; Probe = probe; Path = path; }

        internal static OpenStore Open(string? storePath, bool create, string truedatVersion, string nowUtc)
        {
            if (string.IsNullOrWhiteSpace(storePath))
                throw new StoreException("No store resolved. Pass --store <path\\truedat.db>, or run from a library so the default (truedat.db beside mbxmoods.json, or a local per-library path when the catalog is on a share) can be found.");
            var full = System.IO.Path.GetFullPath(storePath!);

            var probe = SqliteProbe.Probe();
            if (!probe.Ok)
            {
                StoreLog.Error(probe.Message ?? "SQLite unavailable");
                throw new StoreException(probe.Message ?? "SQLite unavailable", StoreExitCodes.StoreUnavailable);
            }

            if (!create && !File.Exists(full))
                throw new StoreException($"Store not found: {full}. Run --store-import first to create it.");
            if (create)
            {
                var dir = System.IO.Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);
            }

            bool network = StoreLocation.IsNetworkPath(full);
            if (network)
                StoreLog.Warn($"store on a network path: {full} — not recommended (every page read and write is a round trip); opening with exclusive locking, one process only");

            SqliteDb? db = null;
            try
            {
                db = SqliteDb.Open(full, create, exclusiveLocking: network);
                SchemaMigrator.Apply(db, truedatVersion);
                var dal = new StoreDal(db);
                var machine = MachineIdentity.EnsureMinted(dal, Environment.MachineName, nowUtc);
                VerdictStore.EnsureCurrent(db, dal, nowUtc);   // T19: derive / re-derive the verdict projection when the build's tags differ
                StoreLog.Info($"store open: {full} (schema v{SchemaMigrator.ReadVersion(db)}, sqlite {probe.VersionText}, machine {machine.Id} on {machine.CreatedByHost})");
                return new OpenStore(db, dal, machine, probe, full);
            }
            catch (SqliteException ex)
            {
                // Not a database, locked, read-only share: one plain sentence, never an escaping stack trace (review #6).
                db?.Dispose();
                throw new StoreException($"The store {full} could not be opened: {ex.Message}");
            }
            catch { db?.Dispose(); throw; }
        }

        public void Dispose() { Dal.Dispose(); Db.Dispose(); }
    }
}
