using System;

namespace Truedat.Store
{
    /// <summary>Data step of the v1 → v2 migration, run by <see cref="SchemaMigrator"/> right after v2.sql inside
    /// the same transaction: split track_feature into track_content (once per media) and track_copy (once per
    /// location), record every disagreement between a copy and its media's description as a conflict row, then
    /// drop the wide row. The description that wins is the entity's lowest location id — its first-imported copy.
    ///
    /// A disagreement is a difference BEYOND run-to-run noise (<see cref="TrackEntryMapper.ContentEqualWithinNoise"/>):
    /// on the real 72,477-track store 590 duplicate-sha copies differed only in the 4th decimal of spectral-shape
    /// statistics (the extractor is not bit-deterministic, constraint 3) and one differed in 91 fields (a real sha
    /// collision). Noise is counted and logged, never stored as a conflict; the real one is.</summary>
    internal static class SchemaV2Migration
    {
        internal const string ConflictKind = "content-disagree-at-split";

        internal static void RunDataStep(SqliteDb db, int version)
        {
            if (version != 2) return;
            if (db.ExecScalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='track_feature';") != "1")
            {
                StoreLog.Debug("v2 data step: no track_feature table (fresh store), nothing to move");
                return;
            }
            var now = DateTime.UtcNow.ToString("o");
            long rows = long.Parse(db.ExecScalar("SELECT count(*) FROM track_feature;") ?? "0");

            // 1. One content row per media, from its first-imported copy.
            db.Exec("INSERT INTO track_content(entity_id, content_key, content_key_kind, run_id, updated_utc, " + TrackEntryMapper.ContentColumnList() + ") " +
                    "SELECT l.entity_id, f.content_key, f.content_key_kind, f.imported_run_id, f.updated_utc, " + TrackEntryMapper.ContentColumnList("f") + " " +
                    "FROM track_feature f JOIN track_location l ON l.id = f.location_id " +
                    "WHERE f.location_id = (SELECT min(f2.location_id) FROM track_feature f2 JOIN track_location l2 ON l2.id = f2.location_id WHERE l2.entity_id = l.entity_id);");
            long content = long.Parse(db.ExecScalar("SELECT count(*) FROM track_content;") ?? "0");

            // 2. Every other copy of a multi-copy media, compared column by column with its media's description.
            long conflicts = 0, noiseDiffs = 0, copiesWithinNoise = 0, copiesInConflict = 0;
            using (var ins = db.Prepare("INSERT INTO conflict(entity_a, kind, field, value_a, value_b, detail, state, created_utc) VALUES (?, ?, ?, ?, ?, ?, 'open', ?);"))
            using (var sel = db.Prepare(
                "SELECT l.entity_id, f.location_id, l.display_path, " + TrackEntryMapper.ContentColumnList("f") + " " +
                "FROM track_feature f JOIN track_location l ON l.id = f.location_id " +
                "WHERE l.entity_id IN (SELECT l3.entity_id FROM track_feature f3 JOIN track_location l3 ON l3.id = f3.location_id GROUP BY l3.entity_id HAVING count(*) > 1) " +
                "ORDER BY l.entity_id, f.location_id;"))
            {
                string? currentEntity = null; object?[]? primary = null;
                while (sel.Step())
                {
                    var entity = sel.GetString(0)!;
                    var display = sel.GetString(2) ?? "";
                    var values = TrackEntryMapper.ReadContentValues(sel, 3);
                    if (entity != currentEntity) { currentEntity = entity; primary = values; continue; }   // lowest location id = the description
                    bool anyConflict = false, anyNoise = false;
                    for (int k = 0; k < values.Length; k++)
                    {
                        var a = primary![k]; var b = values[k];
                        if (TrackEntryMapper.ContentEqualWithinNoise(k, a, b))
                        {
                            if (!(a == null && b == null) && !Equals(TrackEntryMapper.FormatContent(k, a), TrackEntryMapper.FormatContent(k, b))) { noiseDiffs++; anyNoise = true; }
                            continue;
                        }
                        ins.Reset();
                        ins.Bind(1, entity).Bind(2, ConflictKind).Bind(3, TrackEntryMapper.ContentColumnName(k))
                           .Bind(4, TrackEntryMapper.FormatContent(k, a)).Bind(5, TrackEntryMapper.FormatContent(k, b)).Bind(6, display).Bind(7, now);
                        while (ins.Step()) { }
                        conflicts++; anyConflict = true;
                    }
                    if (anyConflict) copiesInConflict++; else if (anyNoise) copiesWithinNoise++;
                }
            }

            // 3. One copy row per location.
            db.Exec("INSERT INTO track_copy(location_id, run_id, updated_utc, " + TrackEntryMapper.CopyColumnList() + ") " +
                    "SELECT f.location_id, f.imported_run_id, f.updated_utc, " + TrackEntryMapper.CopyColumnList("f") + " FROM track_feature f;");
            long copies = long.Parse(db.ExecScalar("SELECT count(*) FROM track_copy;") ?? "0");
            if (copies != rows) throw new StoreException($"v2 migration: {rows} feature rows but {copies} copy rows after the split; nothing was committed.");

            // 4. The wide row is gone.
            db.Exec("DROP INDEX IF EXISTS ix_feature_content;");
            db.Exec("DROP TABLE track_feature;");

            StoreLog.Info($"v2 split: {rows:N0} feature rows -> {content:N0} descriptions (track_content) + {copies:N0} copies (track_copy); " +
                          $"{copies - content:N0} copies now carry their media's description: {copiesWithinNoise:N0} differed only within run-to-run noise ({noiseDiffs:N0} column values), " +
                          $"{copiesInConflict:N0} differed beyond it -> {conflicts:N0} '{ConflictKind}' conflict rows");
        }
    }
}
