using System;
using System.Collections.Generic;

namespace Truedat.Store
{
    /// <summary>Data step of the v3 → v4 migration (store v3 sprint, T05 + T19), run by <see cref="SchemaMigrator"/> right after v4.sql inside
    /// the same transaction (the v3 file was backed up with VACUUM INTO first). Every packed <c>edge.row</c> value row is unpacked and written
    /// into the typed <c>edge</c> table WITH its blob as the row's arrays — a v3 store captured the arrays, and capture is sticky per track, so
    /// nothing a store held is dropped by the upgrade. Then the packed rows are deleted and the head-tail vector rows gain their dtype. A blob
    /// that does not unpack throws: the transaction rolls back and the store stays v3 (refuse, never lose). The verdict table is derived right
    /// after the migration by <see cref="VerdictStore.EnsureCurrent"/> (store_meta carries no verdict tags yet).</summary>
    internal static class SchemaV4Migration
    {
        internal static long LastMovedEdgeRows;

        internal static void RunDataStep(SqliteDb db, int version)
        {
            if (version != 4) return;
            LastMovedEdgeRows = 0;
            var rows = new List<(string Hash, string HashKind, string Version, long? Run, string Kind, int Start, int End, byte[] Blob)>();
            using (var sel = db.Prepare("SELECT v.content_hash, v.content_hash_kind, v.producer_version, v.run_id, s.kind, s.start_ms, s.end_ms, v.vec " +
                                        "FROM value v JOIN segment s ON s.id = v.segment_id WHERE v.field = 'edge.row' AND v.vec IS NOT NULL;"))
                while (sel.Step())
                    rows.Add((sel.GetString(0)!, sel.GetString(1)!, sel.GetString(2)!, sel.IsNull(3) ? (long?)null : sel.GetInt64(3), sel.GetString(4)!,
                              (int)sel.GetInt64(5), (int)sel.GetInt64(6), sel.GetBlob(7)!));
            long packed = long.Parse(db.ExecScalar("SELECT count(*) FROM value WHERE field = 'edge.row';") ?? "0");
            if (packed != rows.Count)
                throw new StoreException($"v4 migration: {packed} packed edge rows but {rows.Count} could be joined to their segment; the store stays v3 (backup beside it).");
            var now = DateTime.UtcNow.ToString("o");
            using (var dal = new StoreDal(db))
            {
                foreach (var r in rows)
                {
                    var e = new global::Truedat.Edges.EdgeRow { Kind = r.Kind };
                    try { global::Truedat.Edges.EdgeStore.UnpackEdge(r.Blob, e); }
                    catch (Exception ex) { throw new StoreException($"v4 migration: the packed edge row of {r.Hash} ({r.Kind}, {r.Version}) does not unpack ({ex.Message}); the store stays v3 (backup beside it)."); }
                    e.StartMs = r.Start; e.EndMs = r.End;
                    global::Truedat.Edges.EdgeStore.UpsertEdgeRow(dal, r.Hash, r.HashKind, r.Version, r.Run, e, r.Blob, now);
                    LastMovedEdgeRows++;
                }
            }
            long moved = long.Parse(db.ExecScalar("SELECT count(*) FROM edge;") ?? "0");
            if (moved != rows.Count)
                throw new StoreException($"v4 migration: {rows.Count} packed edge rows but {moved} typed rows written; the store stays v3 (backup beside it).");
            db.Exec("DELETE FROM value WHERE field = 'edge.row';");
            db.Exec("UPDATE value SET dtype = 'f32' WHERE field = 'edges.headTail' AND vec IS NOT NULL;");
            StoreLog.Info($"v4 data step: {moved} edge rows moved into the typed table (arrays kept: captured in v3, sticky)");
        }
    }
}
