using System;
using System.Collections.Generic;

namespace Truedat.Store
{
    /// <summary>Data access for the identity core (machine, roots, entities, facets, locations) and
    /// the few store_meta keys. Every method is one statement, prepared once per connection and
    /// reused; transactions belong to the caller. Feature rows are the mapper's business (C4).</summary>
    internal sealed class StoreDal : IDisposable
    {
        internal SqliteDb Db { get; }
        readonly Dictionary<string, SqliteStmt> _stmts = new Dictionary<string, SqliteStmt>(StringComparer.Ordinal);

        internal StoreDal(SqliteDb db) { Db = db; }

        /// <summary>A cached prepared statement, reset and ready to bind.</summary>
        internal SqliteStmt Stmt(string sql)
        {
            if (!_stmts.TryGetValue(sql, out var st)) { st = Db.Prepare(sql); _stmts[sql] = st; }
            else st.Reset();
            return st;
        }

        // ---------------------------------------------------------------- store_meta
        internal string? MetaGet(string key)
        {
            var st = Stmt("SELECT value FROM store_meta WHERE key = ?;");
            st.Bind(1, key);
            return st.Step() ? st.GetString(0) : null;
        }

        internal void MetaSet(string key, string value)
        {
            var st = Stmt("INSERT INTO store_meta(key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value;");
            st.Bind(1, key).Bind(2, value);
            st.Step();
        }

        // ---------------------------------------------------------------- machine, roots
        internal void InsertMachine(string id, string hostname, string nowUtc)
        {
            var st = Stmt("INSERT INTO machine(id, hostname, created_utc) VALUES (?, ?, ?);");
            st.Bind(1, id).Bind(2, hostname).Bind(3, nowUtc);
            st.Step();
        }

        internal long EnsureRoot(string machineId, string path, string role, string nowUtc)
        {
            var find = Stmt("SELECT id FROM library_root WHERE machine_id = ? AND path = ?;");
            find.Bind(1, machineId).Bind(2, path);
            if (find.Step()) return find.GetInt64(0);
            var ins = Stmt("INSERT INTO library_root(machine_id, path, role, created_utc) VALUES (?, ?, ?, ?);");
            ins.Bind(1, machineId).Bind(2, path).Bind(3, role).Bind(4, nowUtc);
            ins.Step();
            StoreLog.Info($"root registered: {path} ({role})");
            return Db.LastInsertRowId;
        }

        // ---------------------------------------------------------------- entities and facets
        internal string CreateEntity(string nowUtc)
        {
            var id = Uuid7.New();
            var st = Stmt("INSERT INTO file_entity(id, media_type, state, created_utc) VALUES (?, 'music', 'active', ?);");
            st.Bind(1, id).Bind(2, nowUtc);
            st.Step();
            return id;
        }

        internal string? FindEntityByFacet(string kind, string value)
        {
            var st = Stmt("SELECT entity_id FROM identity_facet WHERE kind = ? AND value = ? LIMIT 1;");
            st.Bind(1, kind).Bind(2, value);
            return st.Step() ? st.GetString(0) : null;
        }

        internal void UpsertFacet(string entityId, string kind, string value, bool isPrimary, double confidence, string source, string nowUtc)
        {
            var st = Stmt(
                "INSERT INTO identity_facet(entity_id, kind, value, is_primary, confidence, source, first_seen_utc, last_seen_utc) " +
                "VALUES (?, ?, ?, ?, ?, ?, ?, ?) " +
                "ON CONFLICT(entity_id, kind, value) DO UPDATE SET is_primary = excluded.is_primary, last_seen_utc = excluded.last_seen_utc;");
            st.Bind(1, entityId).Bind(2, kind).Bind(3, value).Bind(4, isPrimary ? 1L : 0L).Bind(5, confidence).Bind(6, source).Bind(7, nowUtc).Bind(8, nowUtc);
            st.Step();
        }

        /// <summary>Refreshes last_seen only — a re-sighting must not change which facet is primary.</summary>
        internal void TouchFacet(string entityId, string kind, string value, string nowUtc)
        {
            var st = Stmt("UPDATE identity_facet SET last_seen_utc = ? WHERE entity_id = ? AND kind = ? AND value = ?;");
            st.Bind(1, nowUtc).Bind(2, entityId).Bind(3, kind).Bind(4, value);
            st.Step();
        }

        /// <summary>Clears is_primary on every facet of the entity whose kind starts with the prefix.</summary>
        internal void DemoteFacets(string entityId, string kindPrefix)
        {
            var st = Stmt("UPDATE identity_facet SET is_primary = 0 WHERE entity_id = ? AND substr(kind, 1, ?) = ?;");
            st.Bind(1, entityId).Bind(2, (long)kindPrefix.Length).Bind(3, kindPrefix);
            st.Step();
        }

        internal int CountFacets(string entityId)
        {
            var st = Stmt("SELECT count(*) FROM identity_facet WHERE entity_id = ?;");
            st.Bind(1, entityId);
            return st.Step() ? (int)st.GetInt64(0) : 0;
        }

        internal bool HasFacet(string entityId, string kind, string value, out bool isPrimary)
        {
            var st = Stmt("SELECT is_primary FROM identity_facet WHERE entity_id = ? AND kind = ? AND value = ?;");
            st.Bind(1, entityId).Bind(2, kind).Bind(3, value);
            if (!st.Step()) { isPrimary = false; return false; }
            isPrimary = st.GetInt64(0) != 0;
            return true;
        }

        // ---------------------------------------------------------------- locations
        internal long? FindLocation(long rootId, string canonicalPath, out string? entityId)
        {
            var st = Stmt("SELECT id, entity_id FROM track_location WHERE root_id = ? AND canonical_path = ?;");
            st.Bind(1, rootId).Bind(2, canonicalPath);
            if (!st.Step()) { entityId = null; return null; }
            entityId = st.GetString(1);
            return st.GetInt64(0);
        }

        internal long UpsertLocation(string entityId, long rootId, string relPath, string canonicalPath, string displayPath, string? lastModifiedUtc, string nowUtc)
        {
            var st = Stmt(
                "INSERT INTO track_location(entity_id, root_id, rel_path, canonical_path, display_path, last_modified_utc, last_seen_utc, state) " +
                "VALUES (?, ?, ?, ?, ?, ?, ?, 'present') " +
                "ON CONFLICT(root_id, canonical_path) DO UPDATE SET entity_id = excluded.entity_id, rel_path = excluded.rel_path, " +
                "display_path = excluded.display_path, last_modified_utc = excluded.last_modified_utc, last_seen_utc = excluded.last_seen_utc, state = 'present';");
            st.Bind(1, entityId).Bind(2, rootId).Bind(3, relPath).Bind(4, canonicalPath).Bind(5, displayPath).Bind(6, lastModifiedUtc).Bind(7, nowUtc);
            st.Step();
            return FindLocation(rootId, canonicalPath, out _) ?? throw new StoreException($"location vanished after upsert: {displayPath}");
        }

        // ---------------------------------------------------------------- conflicts, counts
        internal void InsertConflict(string entityA, string? entityB, string kind, string detail, string nowUtc)
        {
            var st = Stmt("INSERT INTO conflict(entity_a, entity_b, kind, detail, state, created_utc) VALUES (?, ?, ?, ?, 'open', ?);");
            st.Bind(1, entityA).Bind(2, entityB).Bind(3, kind).Bind(4, detail).Bind(5, nowUtc);
            st.Step();
        }

        /// <summary>Row count of a schema table. Internal callers only — the name is interpolated.</summary>
        internal long Count(string table)
        {
            var st = Stmt($"SELECT count(*) FROM \"{table}\";");
            return st.Step() ? st.GetInt64(0) : 0;
        }

        public void Dispose()
        {
            foreach (var st in _stmts.Values) st.Dispose();
            _stmts.Clear();
        }
    }
}
