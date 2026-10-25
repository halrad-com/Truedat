using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
            StoreDal? dal = null;
            try
            {
                db = SqliteDb.Open(full, create, exclusiveLocking: network);
                SchemaMigrator.Apply(db, truedatVersion);
                dal = new StoreDal(db);
                var machine = MachineIdentity.EnsureMinted(dal, Environment.MachineName, nowUtc);
                FoldNestedRoots(db, dal, nowUtc);   // a store left with nested primary roots is repaired before anything reads it
                ConvertSpectralFlatness(db, dal);   // rows an older build wrote with the three-band average get Essentia's value
                VerdictStore.EnsureCurrent(db, dal, nowUtc);   // T19: derive / re-derive the verdict projection when the build's tags differ
                StoreLog.Info($"store open: {full} (schema v{SchemaMigrator.ReadVersion(db)}, sqlite {probe.VersionText}, machine {machine.Id} on {machine.CreatedByHost})");
                return new OpenStore(db, dal, machine, probe, full);
            }
            catch (SqliteException ex)
            {
                // Not a database, locked, read-only share: one plain sentence, never an escaping stack trace (review #6).
                // The DAL's cached statements go first: a connection closed under live statements stays open, and the file
                // stayed locked — a refused restore could not delete its staging copy (codex 2026-10-23 F2 follow-up).
                dal?.Dispose(); db?.Dispose();
                throw new StoreException($"The store {full} could not be opened: {ex.Message}");
            }
            catch { dal?.Dispose(); db?.Dispose(); throw; }
        }

        /// <summary>The repair half of <see cref="StoreDal.FoldNestedRoots"/>: a store written before the fold ran at root minting
        /// (or by any path that minted a sub-folder root first) is folded the first time it is opened, so every reader — the
        /// export, the scan's load, --store-info, the hub — sees one root instead of each needing its own workaround. A mutation
        /// a consumer can see (the locations' roots), so it advances the generation. A healthy store's open stays a read.</summary>
        static void FoldNestedRoots(SqliteDb db, StoreDal dal, string nowUtc)
        {
            if (!dal.HasNestedRoots()) return;
            // Default safe (operator 2026-10-15): this repair DELETES rows — the losing duplicate location's copy and verdict — so the store
            // is copied beside itself first, as the schema migration does (review 2026-10-24 #5). VACUUM INTO runs outside the transaction.
            string backup = SchemaMigrator.BackupBeside(db, "roots");
            List<RootFold> folds;
            using (var tx = db.BeginTransaction())
            {
                folds = dal.FoldNestedRoots();
                // A merge dropped one location's verdict; the projection is a row iff the block, so it is derived again.
                if (folds.Any(f => f.Merged > 0)) VerdictStore.DeriveAll(dal, nowUtc);
                if (folds.Count > 0) dal.AdvanceGeneration();
                tx.Complete();
            }
            foreach (var f in folds) StoreLog.Warn("root " + f + $" — the store was backed up first: {backup}");
        }

        /// <summary>spectralFlatness is Essentia's spectral_flatness_db: FlatnessDB over the Bark bands, emitted by the music extractor as
        /// barkbands_flatness_db, so it equals barkFlatness. Rows written 2026-08-11 .. 2026-10-19 carry truedat's old three-band
        /// average instead; they are converted from the stored Bark value (same 4-dp scaled integer, so the copy is exact), no audio
        /// read. A consumer-visible change, so the generation advances. A store with nothing to convert opens as a read.</summary>
        static void ConvertSpectralFlatness(SqliteDb db, StoreDal dal)
        {
            const string Differs = "barkFlatness IS NOT NULL AND spectralFlatness IS NOT barkFlatness";
            if (dal.ScalarLong($"SELECT EXISTS(SELECT 1 FROM track_content WHERE {Differs});") == 0) return;
            int converted;
            using (var tx = db.BeginTransaction())
            {
                dal.ExecBound($"UPDATE track_content SET spectralFlatness = barkFlatness WHERE {Differs};");
                converted = db.Changes;
                dal.AdvanceGeneration();
                tx.Complete();
            }
            StoreLog.Info($"spectralFlatness is Essentia's Bark-band flatness: {converted:N0} descriptions converted from the old three-band average, from the stored Bark value");
        }

        public void Dispose() { Dal.Dispose(); Db.Dispose(); }
    }
}
