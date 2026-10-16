using System;
using System.Collections.Generic;

namespace Truedat.Store
{
    /// <summary>store-v3 T19 — the catalog's <c>truedat</c> block, projected into the store (schema v4 <c>verdict</c> table) so a reader of
    /// the store sees exactly what the exported catalog says. The verdict is computed at catalog WRITE time and never persisted by the
    /// catalog (threshold changes ship without a rescan); here it is a DERIVED table, written by truedat's own
    /// <see cref="Program.ComputeTruedatVerdict"/> from the rows AS STORED (rebuilt through the mapper after the merge, never from an
    /// incoming entry), so export parity holds by construction on every write path.
    ///
    /// Keyed by location: the verdict reads the copy's facts as well as the media's description, so two copies of one audio can carry
    /// different verdicts — exactly as the catalog does per path. A row exists iff the catalog writer emits the block
    /// (<see cref="Program.TruedatBlockWritten"/>); absent row == absent block. The build's two algorithm tags (method, speechMethod) are
    /// recorded in store_meta; when either differs from this build at open, every row is re-derived in one transaction, no audio.</summary>
    internal static class VerdictStore
    {
        internal const string MethodKey = "verdict.method";
        internal const string SpeechMethodKey = "verdict.speechMethod";

        static readonly TruedatVerdict BuildTags = new TruedatVerdict();
        internal static string BuildMethod => BuildTags.Method;
        internal static string BuildSpeechMethod => BuildTags.SpeechMethod;

        /// <summary>Self-test seam: the tags this build stamps (null = the real constants), so a test can simulate a tag change.</summary>
        internal static string? MethodOverrideForTest, SpeechMethodOverrideForTest;
        static string CurrentMethod => MethodOverrideForTest ?? BuildMethod;
        static string CurrentSpeechMethod => SpeechMethodOverrideForTest ?? BuildSpeechMethod;

        /// <summary>Re-derive every copy of one media from what is stored — called inside the write transaction after its content/copy rows.</summary>
        internal static void DeriveForEntity(StoreDal dal, string entityId, string nowUtc)
        {
            var have = new HashSet<long>();
            foreach (var (loc, path, entry) in dal.ReadEntriesForEntity(entityId)) { Put(dal, loc, path, entry, nowUtc); have.Add(loc); }
            foreach (var loc in dal.LocationIdsOf(entityId)) if (!have.Contains(loc)) dal.ExecBound("DELETE FROM verdict WHERE location_id = ?;", loc);
        }

        /// <summary>The whole table from the stored rows, and the build's tags recorded. Caller owns the transaction. Returns rows written.</summary>
        internal static int DeriveAll(StoreDal dal, string nowUtc)
        {
            dal.ExecBound("DELETE FROM verdict;");
            int n = 0;
            foreach (var (loc, path, entry) in dal.ReadAllLocationEntries()) if (Put(dal, loc, path, entry, nowUtc)) n++;
            dal.MetaSet(MethodKey, CurrentMethod);
            dal.MetaSet(SpeechMethodKey, CurrentSpeechMethod);
            return n;
        }

        /// <summary>At open, after the schema is applied: re-derive when the recorded tags are missing (a fresh or just-migrated store) or
        /// either differs from this build's. True when it re-derived. A store older than v4 (a self-test seam) has no table: nothing to do.</summary>
        internal static bool EnsureCurrent(SqliteDb db, StoreDal dal, string nowUtc)
        {
            if (SchemaMigrator.ReadVersion(db) < 4) return false;
            string? m = dal.MetaGet(MethodKey), sm = dal.MetaGet(SpeechMethodKey);
            if (m == CurrentMethod && sm == CurrentSpeechMethod) return false;
            int rows;
            using (var tx = db.BeginTransaction()) { rows = DeriveAll(dal, nowUtc); tx.Complete(); }
            StoreLog.Info(m == null && sm == null
                ? $"verdicts derived for the store: {rows} rows (method {CurrentMethod}, speech {CurrentSpeechMethod})"
                : $"verdict algorithm changed (method {m} -> {CurrentMethod}, speech {sm} -> {CurrentSpeechMethod}): {rows} rows re-derived from the stored rows, no audio read");
            return true;
        }

        static bool Put(StoreDal dal, long locationId, string displayPath, TrackEntry entry, string nowUtc)
        {
            var v = Program.ComputeTruedatVerdict(displayPath, entry);
            if (!Program.TruedatBlockWritten(v)) { dal.ExecBound("DELETE FROM verdict WHERE location_id = ?;", locationId); return false; }
            dal.ExecBound("INSERT INTO verdict(location_id, hires_genuine, hires_confidence, lossy_transcode_likely, lossy_confidence, speech_likely, speech_confidence, method, speech_method, prov, content, updated_utc) " +
                "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?) ON CONFLICT(location_id) DO UPDATE SET hires_genuine = excluded.hires_genuine, hires_confidence = excluded.hires_confidence, " +
                "lossy_transcode_likely = excluded.lossy_transcode_likely, lossy_confidence = excluded.lossy_confidence, speech_likely = excluded.speech_likely, speech_confidence = excluded.speech_confidence, " +
                "method = excluded.method, speech_method = excluded.speech_method, prov = excluded.prov, content = excluded.content, updated_utc = excluded.updated_utc " +
                // TDA F3: an equal re-derive writes nothing — updated_utc means "last change", and a no-op import is no mutation (T06).
                "WHERE hires_genuine IS NOT excluded.hires_genuine OR hires_confidence IS NOT excluded.hires_confidence OR lossy_transcode_likely IS NOT excluded.lossy_transcode_likely " +
                "OR lossy_confidence IS NOT excluded.lossy_confidence OR speech_likely IS NOT excluded.speech_likely OR speech_confidence IS NOT excluded.speech_confidence " +
                "OR method IS NOT excluded.method OR speech_method IS NOT excluded.speech_method OR prov IS NOT excluded.prov OR content IS NOT excluded.content;",
                locationId, v.HiresGenuine, v.HiresConfidence, v.LossyTranscodeLikely, v.LossyTranscodeConfidence, v.SpeechLikely, v.SpeechConfidence,
                MethodOverrideForTest ?? v.Method, SpeechMethodOverrideForTest ?? v.SpeechMethod, v.Prov ?? "", v.Content ?? "", nowUtc);
            return true;
        }
    }
}
