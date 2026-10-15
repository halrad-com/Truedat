using System;

namespace Truedat.Store
{
    /// <summary>Data step of the v2 → v3 migration (store v3 sprint, T04b), run by <see cref="SchemaMigrator"/> right after
    /// v3.sql inside the same transaction: every track_content / track_copy row is read in the v2 representation (REAL,
    /// float64 blobs) and written in the v3 one (scaled INTEGER, int32 blobs, the sparse histogram) through the mapper, so
    /// the conversion is the one the scan itself uses; then the v2 tables are dropped and the typed tables renamed into
    /// place. Values are preserved exactly (V15): a v2 REAL that had already been rounded to the field's decimal places is
    /// the same decimal the scaled integer holds; a v2 value that still carried six-figure or float noise is canonicalised to
    /// the declared precision — NO: such a value makes the migration REFUSE and roll back, naming the columns (LastCanonicalisationNote); the
    /// governing text promises that no retained value is lost, so the store stays v2 until the operator decides (keeper, T04 gate). Stale measurements and
    /// Sig6-rounded values are NOT reconstructed here; only a fresh scan (T01) replaces them.
    ///
    /// Row counts are checked before the drop; a mismatch throws and the transaction rolls back whole, leaving the v2 store
    /// (and its backup, written before the transaction) untouched.</summary>
    internal static class SchemaV3Migration
    {
        /// <summary>Test seam: throw after the rows have moved, to prove the migration's transaction rolls back whole.</summary>
        internal static bool FailAfterMoveForTest;

        /// <summary>Why the last migration REFUSED, when it did: v2 values that carry more digits than the store's precision for
        /// their field, per column with counts. The typed column cannot hold them exactly, and no retained value may be lost, so
        /// the migration throws before dropping anything and the store stays v2 (keeper, T04 preservation gate). Null after a
        /// migration that preserved everything. Real v2 stores never trip it (see RunDataStep).</summary>
        internal static string? LastCanonicalisationNote;
        internal static long LastRefusedValues;

        internal static void RunDataStep(SqliteDb db, int version)
        {
            if (version != 3) return;
            bool hasV2 = db.ExecScalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='track_content';") == "1";
            if (!hasV2)
            {
                // A fresh store: v1.sql + v2.sql created and emptied the v2 tables earlier in this same Apply; v2's track_content exists
                // but is empty. (hasV2 false only if v2.sql was never applied, which Apply's loop makes impossible.)
                StoreLog.Debug("v3 data step: no v2 tables, nothing to move");
            }
            long contentRows = hasV2 ? long.Parse(db.ExecScalar("SELECT count(*) FROM track_content;") ?? "0") : 0;
            long copyRows = hasV2 ? long.Parse(db.ExecScalar("SELECT count(*) FROM track_copy;") ?? "0") : 0;
            long movedContent = 0, movedCopy = 0;
            var canon = new System.Collections.Generic.SortedDictionary<string, long>(StringComparer.Ordinal);
            LastCanonicalisationNote = null; LastRefusedValues = 0;

            if (hasV2)
            {
                // 1. descriptions: v2 reader -> v3 binder, one row at a time (the blobs change shape, so no SQL-only copy).
                using (var sel = db.Prepare("SELECT entity_id, content_key, content_key_kind, run_id, updated_utc, " + TrackEntryMapper.ContentColumnList() + " FROM track_content;"))
                using (var ins = db.Prepare("INSERT INTO track_content_v3(entity_id, content_key, content_key_kind, run_id, updated_utc, " + TrackEntryMapper.ContentColumnList() + ") VALUES (?, ?, ?, ?, ?" + Params(TrackEntryMapper.ContentColumnCount) + ");"))
                {
                    while (sel.Step())
                    {
                        var values = TrackEntryMapper.ReadContentValuesLegacyV2(sel, 5);
                        foreach (var col in TrackEntryMapper.ColumnsBeyondPrecision(values, content: true)) canon[col] = canon.TryGetValue(col, out var n) ? n + 1 : 1;
                        ins.Reset();
                        ins.Bind(1, sel.GetString(0)!).Bind(2, sel.GetString(1)!).Bind(3, sel.GetString(2)!);
                        if (sel.IsNull(3)) ins.BindNull(4); else ins.Bind(4, sel.GetInt64(3));
                        ins.Bind(5, sel.GetString(4)!);
                        TrackEntryMapper.BindContent(ins, 6, TrackEntryMapper.ContentRowFromValues(values));
                        while (ins.Step()) { }
                        movedContent++;
                    }
                }
                // 2. copies.
                using (var sel = db.Prepare("SELECT location_id, run_id, updated_utc, " + TrackEntryMapper.CopyColumnList() + " FROM track_copy;"))
                using (var ins = db.Prepare("INSERT INTO track_copy_v3(location_id, run_id, updated_utc, " + TrackEntryMapper.CopyColumnList() + ") VALUES (?, ?, ?" + Params(TrackEntryMapper.CopyColumnCount) + ");"))
                {
                    while (sel.Step())
                    {
                        var values = TrackEntryMapper.ReadCopyValuesLegacyV2(sel, 3);
                        foreach (var col in TrackEntryMapper.ColumnsBeyondPrecision(values, content: false)) canon[col] = canon.TryGetValue(col, out var n) ? n + 1 : 1;
                        ins.Reset();
                        ins.Bind(1, sel.GetInt64(0));
                        if (sel.IsNull(1)) ins.BindNull(2); else ins.Bind(2, sel.GetInt64(1));
                        ins.Bind(3, sel.GetString(2)!);
                        TrackEntryMapper.BindCopy(ins, 4, TrackEntryMapper.CopyRowFromValues(values));
                        while (ins.Step()) { }
                        movedCopy++;
                    }
                }
                if (movedContent != contentRows || movedCopy != copyRows)
                    throw new StoreException($"v3 migration: {contentRows} descriptions / {copyRows} copies in v2 but {movedContent} / {movedCopy} moved; nothing was committed.");
                // PRESERVATION GATE (keeper, T04; operator: no retained value is lost). A v2 value the typed column cannot hold exactly
                // means this migration would be lossy — it REFUSES, rolls back whole (the backup written before the transaction stays),
                // and names the columns and counts. Real v2 stores never trip it: every parser field was rounded at its precision on
                // parse and six-figure rounding only removes digits; the two unrounded writer-side fields hold six places in v3.
                if (canon.Count > 0)
                {
                    long total = 0; var parts = new System.Collections.Generic.List<string>();
                    foreach (var kv in canon) { total += kv.Value; parts.Add($"{kv.Key} {kv.Value:N0}"); }
                    LastRefusedValues = total;
                    LastCanonicalisationNote = $"{total:N0} value(s) in {canon.Count} column(s) carry more digits than the store's precision for that field and cannot be held exactly ({string.Join(", ", parts)})";
                    throw new StoreException($"The store {db.Path} was NOT migrated to schema v3: {LastCanonicalisationNote}. Migrating would round them, and no retained value may be lost. The store is unchanged at v2; a backup was written beside it. This needs an operator decision (which columns, what precision) before it can proceed.");
                }
                if (FailAfterMoveForTest) throw new InvalidOperationException("v3 migration test seam: failing after the rows moved");

                // 3. the v2 tables go; the typed tables take their names and the index.
                db.Exec("DROP INDEX IF EXISTS ix_content_key;");
                db.Exec("DROP TABLE track_content;");
                db.Exec("DROP TABLE track_copy;");
            }
            db.Exec("ALTER TABLE track_content_v3 RENAME TO track_content;");
            db.Exec("ALTER TABLE track_copy_v3 RENAME TO track_copy;");
            db.Exec("CREATE INDEX ix_content_key ON track_content(content_key);");

            if (hasV2 && (contentRows > 0 || copyRows > 0))
                StoreLog.Info($"v3 types: {movedContent:N0} descriptions and {movedCopy:N0} copies re-stored as scaled integers / int32 vectors, every value preserved exactly; the dropped tables' pages are free until --store-vacuum repacks the file (advised).");
        }

        static string Params(int n) { var sb = new System.Text.StringBuilder(); for (int i = 0; i < n; i++) sb.Append(", ?"); return sb.ToString(); }
    }
}
