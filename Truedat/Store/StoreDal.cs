using System;
using System.Collections.Generic;

namespace Truedat.Store
{
    /// <summary>Data access for the identity core (machine, roots, entities, facets, locations), the
    /// provenance tables (run, check_result, work_item, conflict) and feature rows. Every method is one
    /// statement, prepared once per connection and reused; transactions belong to the caller.
    /// Every statement is RESET as soon as its result has been read: a statement left on a row holds a
    /// WAL read mark, and that is exactly what made the first real import's checkpoint fail with
    /// "database table is locked".</summary>
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

        /// <summary>Resets every cached statement. Call before a checkpoint or VACUUM.</summary>
        internal void ResetAll() { foreach (var st in _stmts.Values) st.Reset(); }

        // ---- helpers: bind, run, read, RESET.
        static void BindAll(SqliteStmt st, object?[] binds)
        {
            for (int i = 0; i < binds.Length; i++)
            {
                var v = binds[i]; int p = i + 1;
                switch (v)
                {
                    case null: st.BindNull(p); break;
                    case long l: st.Bind(p, l); break;
                    case int n: st.Bind(p, (long)n); break;
                    case double d: st.Bind(p, d); break;
                    case string s: st.Bind(p, s); break;
                    case byte[] b: st.Bind(p, b); break;
                    default: throw new ArgumentException($"unsupported bind type {v.GetType().Name}");
                }
            }
        }

        /// <summary>Runs a statement that yields no rows (or whose rows are not wanted).</summary>
        void Exec(string sql, params object?[] binds)
        {
            var st = Stmt(sql);
            BindAll(st, binds);
            try { while (st.Step()) { } } finally { st.Reset(); }
        }

        /// <summary>First row read through <paramref name="onRow"/>, or <paramref name="none"/>.</summary>
        T Row<T>(string sql, Func<SqliteStmt, T> onRow, T none, params object?[] binds)
        {
            var st = Stmt(sql);
            BindAll(st, binds);
            try { return st.Step() ? onRow(st) : none; } finally { st.Reset(); }
        }

        /// <summary>Parameterised statement for callers outside the DAL (the edge table, the verdict projection) — same cache, same binding.</summary>
        internal void ExecBound(string sql, params object?[] binds) => Exec(sql, binds);
        /// <summary>First column of the first row as a long (0 when no row or NULL).</summary>
        internal long ScalarLong(string sql, params object?[] binds) => Row(sql, s => s.IsNull(0) ? 0L : s.GetInt64(0), 0L, binds);

        // ---------------------------------------------------------------- store_meta
        internal string? MetaGet(string key) => Row("SELECT value FROM store_meta WHERE key = ?;", s => s.GetString(0), null, key);

        internal void MetaSet(string key, string value)
            => Exec("INSERT INTO store_meta(key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value;", key, value);

        /// <summary>store-v3 T07: the consumer-visible publication generation, a <c>store_meta</c> row. 0 until the first
        /// mutation advances it. Every successful consumer-visible mutation — scan flush, maintenance verb, merge, restore,
        /// import — calls <see cref="AdvanceGeneration"/> inside its transaction; the exports carry the value in their headers
        /// (<c>CatalogOrigin</c>), which is how a hub tells a current triple from one left behind (M8).</summary>
        internal const string GenerationKey = "generation";
        internal long Generation()
            => long.TryParse(MetaGet(GenerationKey), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var g) ? g : 0;
        internal long AdvanceGeneration()
        {
            var g = Generation() + 1;
            MetaSet(GenerationKey, g.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return g;
        }

        // ---------------------------------------------------------------- machine, roots
        internal void InsertMachine(string id, string hostname, string nowUtc)
            => Exec("INSERT INTO machine(id, hostname, created_utc) VALUES (?, ?, ?);", id, hostname, nowUtc);

        internal string? FindMachineByHost(string hostname)
            => Row("SELECT id FROM machine WHERE hostname = ? COLLATE NOCASE ORDER BY created_utc LIMIT 1;", s => s.GetString(0), null, hostname);

        internal long EnsureRoot(string machineId, string path, string role, string nowUtc)
        {
            var existing = Row("SELECT id FROM library_root WHERE machine_id = ? AND path = ?;", s => (long?)s.GetInt64(0), null, machineId, path);
            if (existing.HasValue) return existing.Value;
            Exec("INSERT INTO library_root(machine_id, path, role, created_utc) VALUES (?, ?, ?, ?);", machineId, path, role, nowUtc);
            StoreLog.Info($"root registered: {path} ({role})");
            return Db.LastInsertRowId;
        }

        internal List<(long Id, string Path, string Role)> ListRoots()
        {
            var list = new List<(long, string, string)>();
            var st = Stmt("SELECT id, path, role FROM library_root ORDER BY id;");
            try { while (st.Step()) list.Add((st.GetInt64(0), st.GetString(1) ?? "", st.GetString(2) ?? "")); }
            finally { st.Reset(); }
            return list;
        }

        // ---------------------------------------------------------------- entities and facets
        internal string CreateEntity(string nowUtc)
        {
            var id = Uuid7.New();
            Exec("INSERT INTO file_entity(id, media_type, state, created_utc) VALUES (?, 'music', 'active', ?);", id, nowUtc);
            return id;
        }

        internal string? FindEntityByFacet(string kind, string value)
            => Row("SELECT entity_id FROM identity_facet WHERE kind = ? AND value = ? LIMIT 1;", s => s.GetString(0), null, kind, value);

        internal void UpsertFacet(string entityId, string kind, string value, bool isPrimary, double confidence, string source, string nowUtc)
            => Exec("INSERT INTO identity_facet(entity_id, kind, value, is_primary, confidence, source, first_seen_utc, last_seen_utc) VALUES (?, ?, ?, ?, ?, ?, ?, ?) " +
                    "ON CONFLICT(entity_id, kind, value) DO UPDATE SET is_primary = excluded.is_primary, last_seen_utc = excluded.last_seen_utc;",
                    entityId, kind, value, isPrimary ? 1L : 0L, confidence, source, nowUtc, nowUtc);

        /// <summary>Refreshes last_seen only — a re-sighting must not change which facet is primary.</summary>
        internal void TouchFacet(string entityId, string kind, string value, string nowUtc)
            => Exec("UPDATE identity_facet SET last_seen_utc = ? WHERE entity_id = ? AND kind = ? AND value = ?;", nowUtc, entityId, kind, value);

        /// <summary>Clears is_primary on every facet of the entity whose kind starts with the prefix.</summary>
        internal void DemoteFacets(string entityId, string kindPrefix)
            => Exec("UPDATE identity_facet SET is_primary = 0 WHERE entity_id = ? AND substr(kind, 1, ?) = ?;", entityId, (long)kindPrefix.Length, kindPrefix);

        internal int CountFacets(string entityId)
            => (int)Row("SELECT count(*) FROM identity_facet WHERE entity_id = ?;", s => s.GetInt64(0), 0L, entityId);

        internal bool HasFacet(string entityId, string kind, string value, out bool isPrimary)
        {
            var r = Row("SELECT is_primary FROM identity_facet WHERE entity_id = ? AND kind = ? AND value = ?;", s => (long?)s.GetInt64(0), null, entityId, kind, value);
            isPrimary = r.HasValue && r.Value != 0;
            return r.HasValue;
        }

        internal bool HasFacetOfKind(string entityId, string kind)
            => Row("SELECT 1 FROM identity_facet WHERE entity_id = ? AND kind = ? LIMIT 1;", _ => true, false, entityId, kind);

        /// <summary>The kind of the entity's primary content-hash facet, or null when it has none.</summary>
        internal string? PrimaryContentHashKind(string entityId)
            => Row("SELECT kind FROM identity_facet WHERE entity_id = ? AND is_primary = 1 AND substr(kind, 1, 13) = 'content_hash:' LIMIT 1;", s => s.GetString(0), null, entityId);

        // ---------------------------------------------------------------- locations
        internal long? FindLocation(long rootId, string canonicalPath, out string? entityId)
        {
            var r = Row("SELECT id, entity_id FROM track_location WHERE root_id = ? AND canonical_path = ?;",
                s => ((long?)s.GetInt64(0), s.GetString(1)), ((long?)null, (string?)null), rootId, canonicalPath);
            entityId = r.Item2;
            return r.Item1;
        }

        /// <summary>A location of the canonical path under ANY root — the mirror case: a declared mirror
        /// root maps the same relative path to the same entity.</summary>
        internal long? FindLocationAnyRoot(string canonicalPath, out string? entityId)
        {
            var r = Row("SELECT id, entity_id FROM track_location WHERE canonical_path = ? ORDER BY id LIMIT 1;",
                s => ((long?)s.GetInt64(0), s.GetString(1)), ((long?)null, (string?)null), canonicalPath);
            entityId = r.Item2;
            return r.Item1;
        }

        internal long UpsertLocation(string entityId, long rootId, string relPath, string canonicalPath, string displayPath, string? lastModifiedUtc, string nowUtc)
        {
            // An arrival with no mtime (a ledger row) never erases a known mtime or respells a display path
            // (review #1: a skipped.csv row naming a cataloged path nulled lastModified and broke the gate).
            Exec("INSERT INTO track_location(entity_id, root_id, rel_path, canonical_path, display_path, last_modified_utc, last_seen_utc, state) " +
                 "VALUES (?, ?, ?, ?, ?, ?, ?, 'present') " +
                 "ON CONFLICT(root_id, canonical_path) DO UPDATE SET entity_id = excluded.entity_id, rel_path = excluded.rel_path, " +
                 "display_path = CASE WHEN excluded.last_modified_utc IS NULL THEN display_path ELSE excluded.display_path END, " +
                 "last_modified_utc = coalesce(excluded.last_modified_utc, last_modified_utc), last_seen_utc = excluded.last_seen_utc, state = 'present';",
                 entityId, rootId, relPath, canonicalPath, displayPath, lastModifiedUtc, nowUtc);
            return FindLocation(rootId, canonicalPath, out _) ?? throw new StoreException($"location vanished after upsert: {displayPath}");
        }

        /// <summary>T18 / T06 D1: retire a location (the file moved away or the entry was removed) — its media, description, copy row
        /// and history stay; the export and the hub read present locations only. Returns true when it was present.</summary>
        internal bool RetireLocation(long locationId, string nowUtc)
        {
            bool present = Row("SELECT state FROM track_location WHERE id = ?;", s => s.GetString(0), null, locationId) == "present";
            if (present) Exec("UPDATE track_location SET state = 'retired', last_seen_utc = ? WHERE id = ?;", nowUtc, locationId);
            return present;
        }

        internal string? LocationLastModified(long locationId)
            => Row("SELECT last_modified_utc FROM track_location WHERE id = ?;", s => s.GetString(0), null, locationId);

        /// <summary>T07: the columns of a location an export or a store reader can see (entity, display path, mtime, state) — read
        /// before and after an upsert to tell a visible change from a last-seen stamp. Four separate values, compared column by
        /// column (TDA 082: a joined string could collide on a path containing the separator).</summary>
        internal (string Entity, string Display, string? Modified, string State)? LocationVisible(long locationId)
            => Row("SELECT entity_id, display_path, last_modified_utc, state FROM track_location WHERE id = ?;",
                   s => ((string Entity, string Display, string? Modified, string State)?)(s.GetString(0)!, s.GetString(1)!, s.IsNull(2) ? null : s.GetString(2), s.GetString(3)!),   // NOT NULL columns but the mtime
                   null, locationId);

        /// <summary>Display paths under a root whose location is NOT its media's primary (lowest-id) copy: the copies
        /// that export their media's single description rather than the values they were imported with.</summary>
        internal HashSet<string> NonPrimaryCopyPaths(long rootId)
        {
            var set = new HashSet<string>(PathComparer.Instance);
            using var st = Db.Prepare(
                "SELECT l.display_path FROM track_copy p JOIN track_location l ON l.id = p.location_id " +
                "WHERE l.root_id = ? AND l.id <> (SELECT min(l2.id) FROM track_copy p2 JOIN track_location l2 ON l2.id = p2.location_id WHERE l2.entity_id = l.entity_id);");
            st.Bind(1, rootId);
            while (st.Step()) set.Add(st.GetString(0)!);
            return set;
        }

        /// <summary>The content key of the media a location belongs to (v2: on track_content, reached through the location's entity).</summary>
        internal string? FeatureContentKey(long locationId)
            => Row("SELECT c.content_key FROM track_location l JOIN track_content c ON c.entity_id = l.entity_id WHERE l.id = ?;", s => s.GetString(0), null, locationId);

        /// <summary>Audio props of an existing copy of the entity at some OTHER location, for the
        /// sha-collision check (v2: file facts live on track_copy).</summary>
        internal (long DurationMs, string Codec, string Display)? FeatureProps(string entityId, long exceptRootId, string exceptCanonical)
            => Row("SELECT p.fingerprint_v1_durationMs, p.fingerprint_v1_codec, l.display_path FROM track_copy p JOIN track_location l ON l.id = p.location_id " +
                   "WHERE l.entity_id = ? AND NOT (l.root_id = ? AND l.canonical_path = ?) AND p.fingerprint_v1_durationMs IS NOT NULL LIMIT 1;",
                   s => ((long, string, string)?)(s.GetInt64(0), s.GetString(1) ?? "", s.GetString(2) ?? ""), null, entityId, exceptRootId, exceptCanonical);

        // ---------------------------------------------------------------- features (v2: description per media + facts per copy)
        /// <summary>Writes one entry as the media's description (track_content, keyed by entity) plus the copy's own
        /// facts (track_copy, keyed by location). contentKeyKind is the facet kind of contentKey (FacetKinds.*),
        /// or "entity" when the entry has no hash and contentKey is 'entity:&lt;uuid&gt;'.</summary>
        internal void UpsertFeature(long locationId, string entityId, string contentKey, string contentKeyKind, long? runId, TrackEntry entry, string nowUtc)
        {
            var row = TrackEntryMapper.ToRow(entry);
            UpsertContent(entityId, contentKey, contentKeyKind, runId, row, nowUtc);
            UpsertCopy(locationId, runId, row, nowUtc);
        }

        internal void UpsertContent(string entityId, string contentKey, string contentKeyKind, long? runId, object?[] row, string nowUtc)
        {
            var sc = Stmt(TrackEntryMapper.UpsertContentSql);
            sc.Bind(1, entityId).Bind(2, contentKey).Bind(3, contentKeyKind);
            if (runId.HasValue) sc.Bind(4, runId.Value); else sc.BindNull(4);
            sc.Bind(5, nowUtc);
            TrackEntryMapper.BindContent(sc, TrackEntryMapper.UpsertContentFirstParam, row);
            try { while (sc.Step()) { } } finally { sc.Reset(); }
        }

        internal void UpsertCopy(long locationId, long? runId, object?[] row, string nowUtc)
        {
            var sp = Stmt(TrackEntryMapper.UpsertCopySql);
            sp.Bind(1, locationId);
            if (runId.HasValue) sp.Bind(2, runId.Value); else sp.BindNull(2);
            sp.Bind(3, nowUtc);
            TrackEntryMapper.BindCopy(sp, TrackEntryMapper.UpsertCopyFirstParam, row);
            try { while (sp.Step()) { } } finally { sp.Reset(); }
        }

        internal sealed class ContentRow
        {
            public object?[] Values = Array.Empty<object?>();
            public string ContentKey = "";
            public long? RunId; public string? ProducerKey; public string? ProducerVersion;
        }

        /// <summary>The stored description of a media with its content key and the producer that wrote it, or null when none (S2 C2).</summary>
        internal ContentRow? ReadContent(string entityId)
            => Row("SELECT c.run_id, r.producer_key, r.producer_version, c.content_key, " + TrackEntryMapper.ContentColumnList("c") +
                   " FROM track_content c LEFT JOIN run r ON r.id = c.run_id WHERE c.entity_id = ?;",
                   s => (ContentRow?)new ContentRow { RunId = s.IsNull(0) ? (long?)null : s.GetInt64(0), ProducerKey = s.GetString(1), ProducerVersion = s.GetString(2), ContentKey = s.GetString(3) ?? "", Values = TrackEntryMapper.ReadContentValues(s, 4) },
                   null, entityId);

        /// <summary>The stored copy facts of a location (copy-column order), or null when none (S2 C2).</summary>
        internal object?[]? ReadCopyValues(long locationId)
            => Row("SELECT " + TrackEntryMapper.CopyColumnList("p") + " FROM track_copy p WHERE p.location_id = ?;",
                   s => (object?[]?)TrackEntryMapper.ReadCopyValues(s, 0), null, locationId);

        /// <summary>A content value that differs beyond noise under the same producer: recorded, never written over (S2 C2).</summary>
        /// <summary>Idempotent: the same open drift (entity, field, both values) is recorded once, however many times the same
        /// catalog is re-imported — the 32 known drifts on the real store must not grow by 128 rows per pass.</summary>
        /// <para><paramref name="state"/> is <c>open</c> for an import's kept-stored drift and <c>resolved</c> for a fresh
        /// re-measurement that replaced the description (T01): the old/new pair is history there, not a question.</para>
        internal bool InsertDriftConflict(string entityId, string field, string? valueA, string? valueB, long? runA, long? runB, string detail, string nowUtc, string state = "open")
        {
            Exec("INSERT INTO conflict(entity_a, kind, field, value_a, value_b, run_a, run_b, detail, state, created_utc) " +
                 "SELECT ?, 'value-drift', ?, ?, ?, ?, ?, ?, ?, ? " +
                 "WHERE NOT EXISTS (SELECT 1 FROM conflict WHERE entity_a = ? AND kind = 'value-drift' AND state = ? AND field = ? AND value_a IS ? AND value_b IS ?);",
                 entityId, field, valueA, valueB, runA, runB, detail, state, nowUtc, entityId, state, field, valueA, valueB);
            return Db.Changes > 0;
        }

        /// <summary>Closes the OPEN value-drift rows for one field of a media once a fresh measurement has replaced the description
        /// (T01, review finding 5): the question "which value is right?" is answered by the remeasure, so the open row would be
        /// stale. Returns how many were closed.</summary>
        internal int CloseOpenDrift(string entityId, string field)
        {
            Exec("UPDATE conflict SET state = 'resolved' WHERE entity_a = ? AND kind = 'value-drift' AND state = 'open' AND field = ?;", entityId, field);
            return Db.Changes;
        }

        /// <summary>T19: every entry of one media (each of its copies), with the location id and display path — the verdict projection's
        /// input, rebuilt through the mapper from the rows AS STORED. Its own statement (callers write while iterating).</summary>
        internal List<(long LocationId, string DisplayPath, TrackEntry Entry)> ReadEntriesForEntity(string entityId)
            => ReadLocationEntries(" WHERE l.entity_id = ?", entityId);

        /// <summary>T19: every entry in the store with its location id (the re-derive pass).</summary>
        internal List<(long LocationId, string DisplayPath, TrackEntry Entry)> ReadAllLocationEntries() => ReadLocationEntries("", null);

        List<(long, string, TrackEntry)> ReadLocationEntries(string where, string? bind)
        {
            var list = new List<(long, string, TrackEntry)>();
            using var st = Db.Prepare(
                "SELECT l.id, l.display_path, l.last_modified_utc, " + TrackEntryMapper.SelectList +
                " FROM track_copy p JOIN track_location l ON l.id = p.location_id JOIN track_content c ON c.entity_id = l.entity_id" + where + " ORDER BY l.id;");
            if (bind != null) st.Bind(1, bind);
            while (st.Step())
            {
                var lm = st.GetString(2);
                var lastModified = lm == null ? default
                    : DateTime.ParseExact(lm, "o", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
                list.Add((st.GetInt64(0), st.GetString(1)!, TrackEntryMapper.FromRow(st, 3, lastModified)));
            }
            return list;
        }

        /// <summary>Location ids of one media (including copies with no stored description).</summary>
        internal List<long> LocationIdsOf(string entityId)
        {
            var list = new List<long>();
            using var st = Db.Prepare("SELECT id FROM track_location WHERE entity_id = ? ORDER BY id;");
            st.Bind(1, entityId);
            while (st.Step()) list.Add(st.GetInt64(0));
            return list;
        }

        /// <summary>Every entry under a root, with the display path the catalog keys it by: the copy's facts joined
        /// to its media's description. Its own statement (not the cache) because callers iterate it while
        /// running other statements.</summary>
        internal IEnumerable<(string DisplayPath, TrackEntry Entry)> ReadEntries(long rootId)
        {
            using var st = Db.Prepare(
                "SELECT l.display_path, l.last_modified_utc, " + TrackEntryMapper.SelectList +
                " FROM track_copy p JOIN track_location l ON l.id = p.location_id JOIN track_content c ON c.entity_id = l.entity_id WHERE l.root_id = ? AND l.state = 'present' ORDER BY l.id;");
            st.Bind(1, rootId);
            while (st.Step())
            {
                var display = st.GetString(0)!;
                var lm = st.GetString(1);
                var lastModified = lm == null ? default
                    : DateTime.ParseExact(lm, "o", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
                yield return (display, TrackEntryMapper.FromRow(st, 2, lastModified));
            }
        }

        // ---------------------------------------------------------------- runs, checks, work items
        internal long InsertRun(string producerKey, string producerVersion, string? entityId, string? contentHash, string? contentHashKind, long? locationId,
            string startedUtc, long? durationMs, string? host, string? toolVersion, string outcome, string? failedComponents, string? reason, string? errorText, string? source)
        {
            Exec("INSERT INTO run(producer_key, producer_version, entity_id, content_hash, content_hash_kind, location_id, started_utc, duration_ms, host, tool_version, outcome, failed_components, reason, error_text, source) " +
                 "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
                 producerKey, producerVersion, entityId, contentHash, contentHashKind, locationId, startedUtc, durationMs, host, toolVersion, outcome, failedComponents, reason, errorText, source);
            return Db.LastInsertRowId;
        }

        internal void UpdateRunOutcome(long runId, string outcome, long? durationMs, string? errorText)
            => Exec("UPDATE run SET outcome = ?, duration_ms = ?, error_text = ?, reason = NULL WHERE id = ?;", outcome, durationMs, errorText, runId);

        internal bool RunExists(string producerKey, string? entityId, string outcome, string? source, string startedUtc)
            => Row("SELECT 1 FROM run WHERE producer_key = ? AND entity_id IS ? AND outcome = ? AND source IS ? AND started_utc = ? LIMIT 1;", _ => true, false,
                   producerKey, entityId, outcome, source, startedUtc);

        /// <summary>Dedupe by content, not by file mtime: the errors CSV is rewritten on every scan (review #5).</summary>
        internal bool RunExistsContent(string producerKey, string? entityId, string outcome, string? source, string? errorText, string? failedComponents)
            => Row("SELECT 1 FROM run WHERE producer_key = ? AND entity_id IS ? AND outcome = ? AND source IS ? AND error_text IS ? AND failed_components IS ? LIMIT 1;", _ => true, false,
                   producerKey, entityId, outcome, source, errorText, failedComponents);

        /// <summary>Dedupe a file-shaped run (a verify receipt) on its path and content hash, carried in tool_version.</summary>
        internal bool RunExistsBySourceVersion(string producerKey, string? source, string? toolVersion)
            => Row("SELECT 1 FROM run WHERE producer_key = ? AND source IS ? AND tool_version IS ? LIMIT 1;", _ => true, false, producerKey, source, toolVersion);

        internal void InsertCheck(long? locationId, string? contentHash, string kind, string result, string? detail, string checkedUtc, string? source)
            => Exec("INSERT INTO check_result(location_id, content_hash, kind, result, detail, checked_utc, source) VALUES (?, ?, ?, ?, ?, ?, ?);",
                    locationId, contentHash, kind, result, detail, checkedUtc, source);

        internal bool CheckExists(long? locationId, string result, string? source)
            => Row("SELECT 1 FROM check_result WHERE location_id IS ? AND result = ? AND source IS ? LIMIT 1;", _ => true, false, locationId, result, source);

        internal void UpsertWorkItem(string entityId, string producerKey, string targetVersion, string state, int attempts, string? reviewState, string? reviewNote)
            => Exec("INSERT INTO work_item(entity_id, producer_key, target_version, state, attempts, review_state, review_note) VALUES (?, ?, ?, ?, ?, ?, ?) " +
                    "ON CONFLICT(entity_id, producer_key) DO UPDATE SET target_version = excluded.target_version, state = excluded.state, " +
                    "attempts = max(attempts, excluded.attempts), review_state = excluded.review_state, review_note = excluded.review_note;",
                    entityId, producerKey, targetVersion, state, (long)attempts, reviewState, reviewNote);

        /// <summary>A description replaced by a newer (or unorderable) producer: the trace of the pair, already resolved (review #5).</summary>
        internal void InsertReplacedConflict(string entityId, string oldProducer, string newProducer, long? runA, long? runB, string detail, string nowUtc)
            => Exec("INSERT INTO conflict(entity_a, kind, value_a, value_b, run_a, run_b, detail, state, created_utc) VALUES (?, 'content-replaced', ?, ?, ?, ?, ?, 'resolved', ?);",
                    entityId, oldProducer, newProducer, runA, runB, detail, nowUtc);

        /// <summary>Open conflicts per kind (review #6: the surface review focus 2 names).</summary>
        internal Dictionary<string, long> OpenConflictsByKind()
        {
            var d = new Dictionary<string, long>(StringComparer.Ordinal);
            using var st = Db.Prepare("SELECT kind, count(*) FROM conflict WHERE state = 'open' GROUP BY kind ORDER BY kind;");
            while (st.Step()) d[st.GetString(0) ?? ""] = st.GetInt64(1);
            return d;
        }

        // ---------------------------------------------------------------- artifacts (S2 C-A)
        /// <summary>Records a content-addressed artifact file; the same (content, kind, format) is recorded once.</summary>
        internal bool InsertArtifact(string contentHash, string contentHashKind, long? runId, string kind, string formatVersion, string relPath, long bytes, string sha256, string nowUtc)
        {
            Exec("INSERT OR IGNORE INTO artifact(content_hash, content_hash_kind, run_id, kind, format_version, path, bytes, sha256, created_utc) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?);",
                 contentHash, contentHashKind, runId, kind, formatVersion, relPath, bytes, sha256, nowUtc);
            return Db.Changes > 0;
        }

        // ---------------------------------------------------------------- conflicts, counts
        internal void InsertConflict(string entityA, string? entityB, string kind, string detail, string nowUtc)
            => Exec("INSERT INTO conflict(entity_a, entity_b, kind, detail, state, created_utc) VALUES (?, ?, ?, ?, 'open', ?);", entityA, entityB, kind, detail, nowUtc);

        /// <summary>Row count of a schema table. Internal callers only — the name is interpolated.</summary>
        internal long Count(string table) => Row($"SELECT count(*) FROM \"{table}\";", s => s.GetInt64(0), 0L);

        // ---------------------------------------------------------------- segments and values (S2 C4, the edges producer)
        internal void EnsureProducer(string key, string version, string kind, bool needsAudio)
            => Exec("INSERT INTO producer(key, version, kind, needs_audio) VALUES (?, ?, ?, ?) ON CONFLICT(key) DO UPDATE SET version = excluded.version;", key, version, kind, needsAudio ? 1L : 0L);
        internal string? ProducerVersion(string key) => Row("SELECT version FROM producer WHERE key = ?;", s => s.GetString(0), null, key);
        /// <summary>The producer version of the newest run row for a producer — for essentia, the extractor build that made the descriptions.</summary>
        internal string? LatestRunVersion(string producerKey) => Row("SELECT producer_version FROM run WHERE producer_key = ? ORDER BY id DESC LIMIT 1;", s => s.GetString(0), null, producerKey);

        internal long UpsertSegment(string contentHash, string contentHashKind, string setKey, string kind, int ordinal, int startMs, int endMs)
        {
            Exec("INSERT INTO segment(content_hash, content_hash_kind, set_key, kind, ordinal, start_ms, end_ms) VALUES (?, ?, ?, ?, ?, ?, ?) " +
                 "ON CONFLICT(content_hash, set_key, kind, ordinal) DO UPDATE SET start_ms = excluded.start_ms, end_ms = excluded.end_ms;",
                 contentHash, contentHashKind, setKey, kind, (long)ordinal, (long)startMs, (long)endMs);
            return Row("SELECT id FROM segment WHERE content_hash = ? AND set_key = ? AND kind = ? AND ordinal = ?;", s => s.GetInt64(0), 0L, contentHash, setKey, kind, (long)ordinal);
        }

        internal IEnumerable<(long Id, string Kind, int Ordinal, int StartMs, int EndMs)> ReadSegments(string contentHash, string setKey)
        {
            using var st = Db.Prepare("SELECT id, kind, ordinal, start_ms, end_ms FROM segment WHERE content_hash = ? AND set_key = ? ORDER BY kind, ordinal;");
            st.Bind(1, contentHash); st.Bind(2, setKey);
            while (st.Step()) yield return (st.GetInt64(0), st.GetString(1)!, (int)st.GetInt64(2), (int)st.GetInt64(3), (int)st.GetInt64(4));
        }

        internal void UpsertValue(string contentHash, string contentHashKind, long segmentId, string field, string producerKey, string producerVersion, long? runId,
            double? num, string? txt, byte[]? vec, long? dims, string? unit, string? dtype = null)
            => Exec("INSERT INTO value(content_hash, content_hash_kind, segment_id, field, producer_key, producer_version, run_id, num, txt, vec, dims, unit, dtype) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?) " +
                    "ON CONFLICT(content_hash, segment_id, field, producer_key, producer_version) DO UPDATE SET run_id = excluded.run_id, num = excluded.num, txt = excluded.txt, vec = excluded.vec, dims = excluded.dims, unit = excluded.unit, dtype = excluded.dtype;",
                    contentHash, contentHashKind, segmentId, field, producerKey, producerVersion, runId, num, txt, vec, dims, unit, dtype);

        internal sealed class StoredValue { public double? Num; public string? Txt; public byte[]? Vec; public long? Dims; public string? Unit; }

        internal Dictionary<string, StoredValue> ReadValues(string contentHash, long segmentId, string producerKey, string producerVersion)
        {
            var d = new Dictionary<string, StoredValue>(StringComparer.Ordinal);
            using var st = Db.Prepare("SELECT field, num, txt, vec, dims, unit FROM value WHERE content_hash = ? AND segment_id = ? AND producer_key = ? AND producer_version = ?;");
            st.Bind(1, contentHash); st.Bind(2, segmentId); st.Bind(3, producerKey); st.Bind(4, producerVersion);
            while (st.Step())
                d[st.GetString(0)!] = new StoredValue { Num = st.IsNull(1) ? (double?)null : st.GetDouble(1), Txt = st.GetString(2), Vec = st.GetBlob(3), Dims = st.IsNull(4) ? (long?)null : st.GetInt64(4), Unit = st.GetString(5) };
            return d;
        }

        /// <summary>The refresh gate's list (operator 2026-10-12: the edge backfill is the scan, not a second pipeline): sha-keyed content
        /// with a description and no edge row at <paramref name="version"/>. A failed fold at that version is a recorded failure and is
        /// left out unless <paramref name="includeFailed"/> (--retry-errors), the scan's errors-ledger rule: nothing is retried
        /// implicitly, so a file whose fold fails is not re-analysed on every --refresh. Entity-keyed content has no sha for a
        /// catalog entry to match, so it is never listed.</summary>
        /// <summary>The query behind <see cref="ContentKeysLackingEdges"/>; a field so the self-test can EXPLAIN exactly this SQL.</summary>
        internal const string LackingEdgesSql = "SELECT c.content_key FROM track_content c WHERE c.content_key_kind <> 'entity' " +
            "AND NOT EXISTS (SELECT 1 FROM value v WHERE v.content_hash = c.content_key AND v.producer_key = ?1 AND v.producer_version = ?2 AND v.segment_id = 0 AND v.field = 'edges.status') " +
            "AND (?3 = 1 OR NOT EXISTS (SELECT 1 FROM run r WHERE r.entity_id = c.entity_id AND r.producer_key = ?1 AND r.producer_version = ?2 AND +r.outcome = 'failed' AND coalesce(r.failed_components, '') <> 'spill-lost'));";   // a lost spill never parks a track (TDA N1)
        // `+r.outcome`: the unary plus keeps the planner off ix_run_outcome, so the failed-run test goes through ix_run_entity — the
        // track's few runs — instead of every failed run in the store for every candidate track (TDA 040; pinned by EdgeSelfTest).

        internal HashSet<string> ContentKeysLackingEdges(string producerKey, string version, bool includeFailed)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var st = Db.Prepare(LackingEdgesSql);
            st.Bind(1, producerKey); st.Bind(2, version); st.Bind(3, includeFailed ? 1L : 0L);
            while (st.Step()) { var k = st.GetString(0); if (!string.IsNullOrEmpty(k)) set.Add(k!); }
            return set;
        }

        /// <summary>Every hashed content key the store holds (the lacking-edges query's own <c>content_key_kind &lt;&gt; 'entity'</c> filter):
        /// a catalog entry whose sha is not in this set is invisible to <see cref="ContentKeysLackingEdges"/>, so --refresh cannot
        /// re-analyse it for edges (keeper 083 / TDA 062, N1 — the scan says so).</summary>
        internal HashSet<string> ContentKeysHeld()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var st = Db.Prepare("SELECT content_key FROM track_content WHERE content_key_kind <> 'entity';");
            while (st.Step()) { var k = st.GetString(0); if (!string.IsNullOrEmpty(k)) set.Add(k!); }
            return set;
        }

        /// <summary>The extractor build most of this version's edge rows record (review M4) — the companion header's source.</summary>
        internal string? EdgesExtractorBuild(string producerVersion)
            => Row("SELECT txt FROM value WHERE producer_key = 'edges' AND producer_version = ? AND segment_id = 0 AND field = 'edges.extractor' GROUP BY txt ORDER BY count(*) DESC LIMIT 1;", s => s.GetString(0), null, producerVersion);

        /// <summary>Every content key holding an edge row (the whole-track status word) at this producer version, in key order.</summary>
        /// <summary>hub:H02: whether the store holds an edge result of ANY version (an 'edges.status' whole-track row) — the stamp's gate.</summary>
        internal bool HasAnyEdgeResults()
            => ScalarLong("SELECT EXISTS (SELECT 1 FROM value WHERE producer_key = 'edges' AND segment_id = 0 AND field = 'edges.status');") == 1;

        internal List<string> ContentKeysWithEdges(string producerVersion)
        {
            var list = new List<string>();
            using var st = Db.Prepare("SELECT content_hash FROM value WHERE producer_key = 'edges' AND producer_version = ? AND segment_id = 0 AND field = 'edges.status' ORDER BY content_hash;");
            st.Bind(1, producerVersion);
            while (st.Step()) list.Add(st.GetString(0)!);
            return list;
        }

        internal long CountRuns(string producerKey, string outcome) => Row("SELECT count(*) FROM run WHERE producer_key = ? AND outcome = ?;", s => s.GetInt64(0), 0L, producerKey, outcome);
        internal string? RunErrorText(string producerKey, string outcome) => Row("SELECT error_text FROM run WHERE producer_key = ? AND outcome = ? ORDER BY id DESC LIMIT 1;", s => s.GetString(0), null, producerKey, outcome);
        internal long? RunDurationMs(string producerKey, string outcome) => Row("SELECT duration_ms FROM run WHERE producer_key = ? AND outcome = ? ORDER BY id DESC LIMIT 1;", s => s.IsNull(0) ? (long?)null : s.GetInt64(0), null, producerKey, outcome);

        internal long CountSegments(string contentHash, string setKey) => Row("SELECT count(*) FROM segment WHERE content_hash = ? AND set_key = ?;", s => s.GetInt64(0), 0L, contentHash, setKey);
        internal long CountValues(string contentHash, string producerKey, string producerVersion)
            => Row("SELECT count(*) FROM value WHERE content_hash = ? AND producer_key = ? AND producer_version = ?;", s => s.GetInt64(0), 0L, contentHash, producerKey, producerVersion);
        internal long SumVecBytes(string contentHash, string producerKey, string producerVersion)
            => Row("SELECT coalesce(sum(length(vec)), 0) FROM value WHERE content_hash = ? AND producer_key = ? AND producer_version = ?;", s => s.GetInt64(0), 0L, contentHash, producerKey, producerVersion);

        public void Dispose()
        {
            foreach (var st in _stmts.Values) st.Dispose();
            _stmts.Clear();
        }
    }
}
