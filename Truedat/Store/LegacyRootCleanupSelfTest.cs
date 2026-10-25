using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Truedat.Store
{
    /// <summary>Independent regressions for stores that already contain root aliases and child rows.</summary>
    internal static class LegacyRootCleanupSelfTest
    {
        const string Old = "2025-01-01T00:00:00.0000000Z", New = "2099-01-01T00:00:00.0000000Z";
        const string Foreign = "00000000-0000-7000-8000-00000000c1ea";
        static readonly string Scores = BitConverter.ToString(Enumerable.Range(1, 10).SelectMany(BitConverter.GetBytes).ToArray()).Replace("-", "");

        internal static void Run(Action<bool, string> assert)
        {
            foreach (bool mirrorFirst in new[] { true, false })
                foreach (bool aliasNewer in new[] { true, false })
                {
                    string label = $"LegacyRootCleanup values/history ({(mirrorFirst ? "mirror" : "primary")} first, {(aliasNewer ? "alias" : "original")} newer)";
                    Block(assert, label, () => SharedEntity(assert, label, mirrorFirst, aliasNewer));
                }
            foreach (string role in new[] { "mirror", "endpoint" })
                Block(assert, "LegacyRootCleanup same-role " + role, () => SameRole(assert, role));
            foreach (bool sharedEntity in new[] { false, true })
                Block(assert, "LegacyRootCleanup different-content", () => DifferentContent(assert, sharedEntity));
        }

        static void SharedEntity(Action<bool, string> assert, string label, bool mirrorFirst, bool aliasNewer)
        {
            using var dir = new Fixture();
            using var quiet = StoreLog.Quiet();
            const string root = @"D:\Alias", path = root + @"\song.flac";
            Seed(dir.Store, root, mirrorFirst ? "mirror" : "primary", (path, 'a'));
            long primary, first, alias, keep, drop, foreignRoot, foreignLoc, generation;
            (long Run, long Check) kh, dh, fh;
            string contentBefore, historyBefore, foreignBefore, copyStamp, copyBefore;
            using (var os = OpenStore.Open(dir.Store, false, VersionInfo.Display, Now()))
            {
                var db = os.Db;
                first = Id(db, "SELECT id FROM library_root;");
                long original = Id(db, "SELECT id FROM track_location;");
                alias = AddRoot(db, os.Machine.Id, "d:/ALIAS/", mirrorFirst ? "primary" : "mirror");
                long duplicate = CloneLocation(db, original, alias);
                primary = mirrorFirst ? alias : first;
                keep = aliasNewer ? duplicate : original; drop = aliasNewer ? original : duplicate;
                db.Exec($"UPDATE track_copy SET updated_utc = {Q(Old)}, artist = 'donor artist', title = 'older title', trackId = 77, " +
                    $"smfmBpm = 123000000, smfmScores = X'{Scores}', genre = NULL, album = NULL WHERE location_id = {drop};" +
                    $"UPDATE track_copy SET updated_utc = {Q(New)}, artist = NULL, title = '', trackId = 0, smfmBpm = NULL, " +
                    $"smfmScores = NULL, genre = NULL, album = 'kept album' WHERE location_id = {keep};");
                StampCopyObservation(db, keep, New, "keeper copy");
                StampCopyObservation(db, drop, Old, "dropped copy");
                kh = AddHistory(db, keep, "keeper", 'a'); dh = AddHistory(db, drop, "dropped", 'a');
                string entity = db.ExecScalar($"SELECT entity_id FROM track_location WHERE id = {keep};")!;
                db.Exec($"INSERT INTO conflict(entity_a, kind, detail, state, created_utc, field, value_a, value_b, run_a, run_b) " +
                    $"VALUES ({Q(entity)}, 'value-drift', 'retain this history', 'open', {Q(Old)}, 'title', 'before', 'after', {dh.Run}, {kh.Run});");
                AddMachine(db);
                foreignRoot = AddRoot(db, Foreign, @"d:\ALIAS\", "mirror");
                foreignLoc = CloneLocation(db, original, foreignRoot);
                db.Exec($"UPDATE track_copy SET artist = 'foreign artist', title = 'foreign title', trackId = 88 WHERE location_id = {foreignLoc};");
                fh = AddHistory(db, foreignLoc, "foreign", 'a');
                contentBefore = Snapshot(db, "file_entity", "identity_facet", "track_content", "conflict");
                historyBefore = HistoryPayload(db);
                foreignBefore = ForeignState(db, foreignRoot, foreignLoc, fh);
                copyStamp = db.ExecScalar($"SELECT quote(run_id) || '|' || updated_utc FROM track_copy WHERE location_id = {keep};")!;
                copyBefore = Rows(db, "track_copy", $"location_id = {keep}");
                generation = os.Dal.Generation();
                assert(Id(db, "SELECT count(DISTINCT entity_id) FROM track_location;") == 1
                    && Id(db, "SELECT count(*) FROM track_location;") == 3
                    && Id(db, $"SELECT count(*) FROM run WHERE id IN ({kh.Run},{dh.Run}) AND location_id IN ({keep},{drop});") == 2
                    && Id(db, "SELECT count(*) FROM track_content WHERE bpmHistogram IS NOT NULL;") == 1,
                    label + ": fixture has shared content, two distinct histories, duplicate paths, and a foreign mirror");
            }

            string repaired;
            using (var os = OpenStore.Open(dir.Store, false, VersionInfo.Display, Now()))
            {
                var db = os.Db;
                long actual = Id(db, $"SELECT id FROM track_location WHERE root_id = {primary} AND canonical_path = 'song.flac';");
                assert(actual == keep && Id(db, "SELECT count(*) FROM library_root;") == 2
                    && Id(db, $"SELECT count(*) FROM library_root WHERE id = {primary} AND role = 'primary';") == 1
                    && Id(db, $"SELECT count(*) FROM track_location WHERE root_id = {primary};") == 1,
                    label + ": one existing location remains under the existing primary root");
                assert(Rows(db, "track_copy", $"location_id = {actual}") == copyBefore,
                    label + ": the entire retained copy is unchanged, including NULL text, integer, and blob fields");
                assert(db.ExecScalar($"SELECT quote(title) || '|' || trackId || '|' || album || '|' || (genre IS NULL) FROM track_copy WHERE location_id = {actual};")
                    == "''|0|kept album|1", label + ": supplied empty and zero survive; both-NULL stays NULL");
                assert(db.ExecScalar($"SELECT quote(run_id) || '|' || updated_utc FROM track_copy WHERE location_id = {actual};") == copyStamp,
                    label + ": the newer copy's observation stamp is preserved");
                assert(HistoryPayload(db) == historyBefore
                    && Id(db, $"SELECT count(*) FROM run WHERE id IN ({kh.Run},{dh.Run}) AND location_id = {actual};") == 2
                    && Id(db, $"SELECT count(*) FROM check_result WHERE id IN ({kh.Check},{dh.Check}) AND location_id = {actual};") == 2,
                    label + ": BOTH locations' run/check IDs and payloads survive, with dropped references rewired");
                assert(Snapshot(db, "file_entity", "identity_facet", "track_content", "conflict") == contentBefore,
                    label + ": content, facets, entities, and content-conflict history stay unchanged");
                assert(ForeignState(db, foreignRoot, foreignLoc, fh) == foreignBefore,
                    label + ": the other machine's root, location, copy values, and history stay unchanged");
                assert(os.Dal.Generation() == generation + 1 && db.ExecScalar("PRAGMA foreign_key_check;") == null,
                    label + ": cleanup advances generation once and preserves foreign keys");
                repaired = Snapshot(db);
            }
            using (var os = OpenStore.Open(dir.Store, false, VersionInfo.Display, Now()))
                assert(Snapshot(os.Db) == repaired, label + ": a second open changes no logical row in any table");
        }

        static void SameRole(Action<bool, string> assert, string role)
        {
            string label = "LegacyRootCleanup same-role " + role;
            using var dir = new Fixture();
            using var quiet = StoreLog.Quiet();
            const string root = @"\\nas\Music", path = root + @"\song.flac";
            Seed(dir.Store, root, role, (path, 'a'));
            long first, alias, original, moved, foreignRoot, foreignLoc, generation;
            (long Run, long Check) fh;
            string foreignBefore, contentBefore;
            using (var os = OpenStore.Open(dir.Store, false, VersionInfo.Display, Now()))
            {
                var db = os.Db;
                first = Id(db, "SELECT id FROM library_root;"); original = Id(db, "SELECT id FROM track_location;");
                alias = AddRoot(db, os.Machine.Id, "//NAS/music/", role);
                moved = CloneLocation(db, original, alias, "extra.flac", root + @"\extra.flac");
                AddMachine(db); foreignRoot = AddRoot(db, Foreign, @"\\NAS\music\", "mirror");
                foreignLoc = CloneLocation(db, original, foreignRoot);
                db.Exec($"UPDATE track_copy SET title = 'foreign UNC copy' WHERE location_id = {foreignLoc};");
                fh = AddHistory(db, foreignLoc, "foreign UNC", 'a');
                foreignBefore = ForeignState(db, foreignRoot, foreignLoc, fh);
                contentBefore = Snapshot(db, "file_entity", "identity_facet", "track_content");
                generation = os.Dal.Generation();
                assert(Id(db, "SELECT count(*) FROM library_root;") == 3 && Id(db, "SELECT count(*) FROM track_location;") == 3,
                    label + ": fixture contains two UNC aliases and a legitimate foreign mirror");
            }
            string repaired;
            using (var os = OpenStore.Open(dir.Store, false, VersionInfo.Display, Now()))
            {
                // Review 2026-10-24 #5: the fold deletes the losing duplicate location's copy and verdict rows — default safe means a copy of
                // the store beside it first, as the schema migration does.
                assert(System.IO.Directory.GetFiles(System.IO.Path.GetDirectoryName(dir.Store)!, System.IO.Path.GetFileName(dir.Store) + ".bak.roots.*").Length == 1,
                    label + ": the alias repair on open backed the store up first (.bak.roots.<stamp> beside it)");
                var db = os.Db;
                long survivor = Id(db, $"SELECT id FROM library_root WHERE machine_id <> {Q(Foreign)};");
                assert((survivor == first || survivor == alias) && Id(db, "SELECT count(*) FROM library_root;") == 2
                    && db.ExecScalar($"SELECT role FROM library_root WHERE id = {survivor};") == role && os.Dal.LibraryRoots().Count == 0,
                    label + ": aliases reuse an existing root without promotion or a primary publication");
                assert(Id(db, $"SELECT count(*) FROM track_location WHERE id IN ({original},{moved}) AND root_id = {survivor};") == 2
                    && Id(db, $"SELECT count(*) FROM track_copy WHERE location_id IN ({original},{moved});") == 2,
                    label + ": noncolliding locations retain their IDs and copy rows");
                assert(ForeignState(db, foreignRoot, foreignLoc, fh) == foreignBefore
                    && Snapshot(db, "file_entity", "identity_facet", "track_content") == contentBefore,
                    label + ": foreign mirror facts/history and shared content are untouched");
                assert(os.Dal.Generation() == generation + 1 && db.ExecScalar("PRAGMA foreign_key_check;") == null,
                    label + ": one cleanup mutation preserves foreign keys");
                repaired = Snapshot(db);
            }
            using (var os = OpenStore.Open(dir.Store, false, VersionInfo.Display, Now()))
                assert(Snapshot(os.Db) == repaired, label + ": repeat open is a full logical no-op");
        }

        static void DifferentContent(Action<bool, string> assert, bool sharedEntity)
        {
            string label = "LegacyRootCleanup different-content (" + (sharedEntity ? "same entity, different observations" : "different entities") + ")";
            const string root = @"D:\Different", target = root + @"\song.flac";
            using var dir = new Fixture();
            using var quiet = StoreLog.Quiet();
            Seed(dir.Store, root, "primary", (target, 'a'), (root + @"\source.flac", 'b'));
            string contentBefore, historyBefore;
            long primary, newerObservation;
            using (var os = OpenStore.Open(dir.Store, false, VersionInfo.Display, Now()))
            {
                var db = os.Db;
                primary = Id(db, "SELECT id FROM library_root;");
                long original = Id(db, "SELECT id FROM track_location WHERE canonical_path = 'song.flac';");
                long source = Id(db, "SELECT id FROM track_location WHERE canonical_path = 'source.flac';");
                long alias = AddRoot(db, os.Machine.Id, "d:/DIFFERENT/", "mirror");
                long newer = CloneLocation(db, source, alias, "song.flac", target);
                db.Exec($"UPDATE track_copy SET updated_utc = {Q(Old)}, fileMd5 = '11111111111111111111111111111111', fingerprint_v1_fileSize = 111, " +
                    $"smfmScores = X'{Scores}' WHERE location_id = {original};" +
                    $"UPDATE track_copy SET updated_utc = {Q(New)}, fileMd5 = NULL, fingerprint_v1_fileSize = NULL, smfmScores = NULL WHERE location_id = {newer};");
                // An entity can survive re-encoding. The current description is B, while the older copy's observation was A.
                if (sharedEntity)
                    db.Exec($"UPDATE track_location SET entity_id = (SELECT entity_id FROM track_location WHERE id = {newer}) WHERE id = {original};");
                StampCopyObservation(db, original, Old, "content A copy", new string('a', 64));
                StampCopyObservation(db, newer, New, "content B copy", new string('b', 64));
                newerObservation = Id(db, $"SELECT run_id FROM track_copy WHERE location_id = {newer};");
                AddHistory(db, original, "content A", 'a'); AddHistory(db, newer, "content B", 'b');
                assert(Id(db, $"SELECT count(DISTINCT l.entity_id) FROM track_location l WHERE l.display_path = {Q(target)};") == (sharedEntity ? 1 : 2)
                    && Id(db, $"SELECT count(DISTINCT r.content_hash) FROM track_copy p JOIN run r ON r.id = p.run_id WHERE p.location_id IN ({original},{newer});") == 2
                    && Id(db, "SELECT count(DISTINCT content_key) FROM track_content WHERE content_key_kind <> 'entity';") == 2
                    && Id(db, $"SELECT count(*) FROM track_copy WHERE location_id = {newer} AND fileMd5 IS NULL AND fingerprint_v1_fileSize IS NULL AND smfmScores IS NULL;") == 1,
                    label + ": fixture has overlapping paths with different known hashes and missing newer file facts");
                contentBefore = Snapshot(db, "file_entity", "identity_facet", "track_content");
                historyBefore = HistoryPayload(db);
            }
            using (var repaired = OpenStore.Open(dir.Store, false, VersionInfo.Display, Now()))
            {
                var db = repaired.Db;
                long borrowed = Id(db, "SELECT count(*) FROM track_copy p JOIN track_location l ON l.id = p.location_id " +
                    "JOIN track_content c ON c.entity_id = l.entity_id WHERE c.content_key = " + Q(new string('b', 64)) +
                    " AND p.run_id IN (SELECT id FROM run WHERE content_hash = " + Q(new string('b', 64)) + ")" +
                    " AND l.display_path = " + Q(target) + " AND (p.fileMd5 IS NOT NULL OR p.fingerprint_v1_fileSize IS NOT NULL OR p.smfmScores IS NOT NULL);");
                assert(borrowed == 0, label + $": newer content never borrows the old file's MD5, fingerprint, or SMFM (contaminated copies {borrowed})");
                assert(Id(db, $"SELECT count(*) FROM track_copy p JOIN track_location l ON l.id = p.location_id WHERE p.run_id = {newerObservation} AND l.display_path = {Q(target)};") == 1
                    && HistoryPayload(db) == historyBefore,
                    label + ": the newer target observation and all run/check IDs and payloads survive");
                assert(Snapshot(db, "file_entity", "identity_facet", "track_content") == contentBefore,
                    label + ": cleanup does not rewrite or delete either content identity or description");
                assert(Id(db, $"SELECT count(*) FROM library_root WHERE id = {primary} AND role = 'primary';") == 1
                    && Id(db, $"SELECT count(*) FROM track_location WHERE root_id = {primary} AND canonical_path = 'song.flac';") == 1,
                    label + ": the existing primary still publishes one valid location at the target path");
            }
        }

        static void Seed(string store, string root, string role, params (string Path, char Hash)[] entries)
        {
            var tracks = new Dictionary<string, TrackEntry>(PathComparer.Instance);
            foreach (var e in entries)
            {
                var histogram = new double[250]; histogram[30] = 1;
                tracks[e.Path] = new TrackEntry { AudioStreamSha256 = new string(e.Hash, 64), AudioStreamSha256Source = "flac-frames",
                    LastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                    Features = new TrackFeatures { Artist = "Artist", Title = "Original", Bpm = 120, BpmHistogram = histogram } };
            }
            ImportCommand.ExecuteEntries(store, tracks, root, role, true, "import", "1", "legacy-cleanup-fixture", null, null);
        }
        static long AddRoot(SqliteDb db, string machine, string path, string role)
        {
            db.Exec($"INSERT INTO library_root(machine_id,path,role,created_utc) VALUES ({Q(machine)},{Q(path)},{Q(role)},{Q(Old)});");
            return db.LastInsertRowId;
        }
        static void AddMachine(SqliteDb db) => db.Exec($"INSERT INTO machine(id,hostname,created_utc) VALUES ({Q(Foreign)},'LEGACY-OTHER',{Q(Old)});");
        static long CloneLocation(SqliteDb db, long source, long root, string? relative = null, string? display = null)
        {
            db.Exec("INSERT INTO track_location(entity_id,root_id,rel_path,canonical_path,display_path,last_modified_utc,last_seen_utc,state) " +
                $"SELECT entity_id,{root},{(relative == null ? "rel_path" : Q(relative))},{(relative == null ? "canonical_path" : Q(relative))}," +
                $"{(display == null ? "display_path" : Q(display))},last_modified_utc,last_seen_utc,state FROM track_location WHERE id = {source};");
            long id = db.LastInsertRowId;
            db.Exec($"CREATE TEMP TABLE cleanup_copy AS SELECT * FROM track_copy WHERE location_id = {source};" +
                $"UPDATE cleanup_copy SET location_id = {id}; INSERT INTO track_copy SELECT * FROM cleanup_copy; DROP TABLE cleanup_copy;");
            return id;
        }
        static (long Run, long Check) AddHistory(SqliteDb db, long location, string tag, char hash)
        {
            db.Exec("INSERT INTO run(producer_key,producer_version,entity_id,content_hash,content_hash_kind,location_id,started_utc,outcome,failed_components,reason,error_text,source) " +
                $"SELECT 'verify','legacy',entity_id,{Q(new string(hash, 64))},{Q(FacetKinds.ShaFrames)},id,{Q(Old)},'failed','sha',{Q(tag + " reason")},{Q(tag + " error")},{Q(tag + " source")} FROM track_location WHERE id = {location};");
            long run = db.LastInsertRowId;
            db.Exec("INSERT INTO check_result(location_id,content_hash,content_hash_kind,kind,result,detail,checked_utc,source) " +
                $"VALUES ({location},{Q(new string(hash, 64))},{Q(FacetKinds.ShaFrames)},'verify','ERROR',{Q(tag + " detail")},{Q(Old)},{Q(tag + " check source")});");
            return (run, db.LastInsertRowId);
        }
        static void StampCopyObservation(SqliteDb db, long location, string stamp, string source, string? observedHash = null)
        {
            db.Exec("INSERT INTO run(producer_key,producer_version,entity_id,content_hash,content_hash_kind,location_id,started_utc,outcome,source) " +
                $"SELECT 'import','1',l.entity_id,{(observedHash == null ? "c.content_key" : Q(observedHash))},c.content_key_kind,l.id,{Q(stamp)},'ok',{Q(source)} " +
                $"FROM track_location l JOIN track_content c ON c.entity_id = l.entity_id WHERE l.id = {location};");
            long run = db.LastInsertRowId;
            db.Exec($"UPDATE track_copy SET run_id = {run} WHERE location_id = {location};");
        }
        static string HistoryPayload(SqliteDb db)
            => Rows(db, "run", omit: "location_id") + Rows(db, "check_result", omit: "location_id");
        static string ForeignState(SqliteDb db, long root, long location, (long Run, long Check) h)
            => Rows(db, "library_root", "id = " + root) + Rows(db, "track_location", "id = " + location)
                + Rows(db, "track_copy", "location_id = " + location) + Rows(db, "run", "id = " + h.Run) + Rows(db, "check_result", "id = " + h.Check);
        static string Snapshot(SqliteDb db, params string[] tables)
        {
            if (tables.Length == 0)
            {
                var names = new List<string>();
                using var st = db.Prepare("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;");
                while (st.Step()) names.Add(st.GetString(0)!);
                tables = names.ToArray();
            }
            return string.Concat(tables.Select(t => Rows(db, t)));
        }
        static string Rows(SqliteDb db, string table, string? where = null, string? omit = null)
        {
            var columns = new List<string>();
            using (var info = db.Prepare("PRAGMA table_info(" + Q(table) + ");"))
                while (info.Step()) if (info.GetString(1) != omit) columns.Add(info.GetString(1)!);
            string field(string c) => "quote(\"" + c.Replace("\"", "\"\"") + "\")";
            var expression = string.Join(" || '|' || ", columns.Select(c => "length(" + field(c) + ") || ':' || " + field(c)));
            var result = new StringBuilder(table + "\n");
            using var rows = db.Prepare("SELECT " + expression + " FROM \"" + table + "\"" + (where == null ? "" : " WHERE " + where) + " ORDER BY 1;");
            while (rows.Step()) { string row = rows.GetString(0)!; result.Append(row.Length).Append(':').Append(row).Append('\n'); }
            return result.ToString();
        }
        static long Id(SqliteDb db, string sql) => long.Parse(db.ExecScalar(sql) ?? "-1", System.Globalization.CultureInfo.InvariantCulture);
        static string Q(string value) => "'" + value.Replace("'", "''") + "'";
        static string Now() => DateTime.UtcNow.ToString("o");
        static void Block(Action<bool, string> assert, string label, Action action)
        { try { action(); } catch (Exception ex) { assert(false, label + ": threw " + ex.GetType().Name + ": " + ex.Message); } }
        sealed class Fixture : IDisposable
        {
            readonly string directory = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "legacy-cleanup-" + Guid.NewGuid().ToString("N"));
            internal string Store => Path.Combine(directory, "truedat.db");
            internal Fixture() { Directory.CreateDirectory(directory); }
            public void Dispose() { try { Directory.Delete(directory, true); } catch { } }
        }
    }
}
