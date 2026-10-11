using System;
using System.Collections.Generic;
using System.IO;

namespace Truedat.Store
{
    /// <summary>Self-tests for the v2 store, driven from <c>--self-test</c> through the same
    /// Assert(bool, string) the rest of the suite uses. Each block is isolated: an exception inside
    /// one block is reported as a FAIL naming the block and the next block still runs.</summary>
    internal static class StoreSelfTest
    {
        internal static void Run(Action<bool, string> assert)
        {
            Console.WriteLine("Store self-test (C1: sqlite layer + open guard)");
            Block(assert, "StoreLog", () => TestStoreLog(assert));
            Block(assert, "SqliteProbe seam", () => TestProbeSeam(assert));
            Block(assert, "SqliteProbe real", () => TestProbeReal(assert));
            Block(assert, "SqliteDb", () => TestDb(assert));
            Console.WriteLine("Store self-test (C2: schema v1 + drift)");
            Block(assert, "Schema", () => TestSchema(assert));
        }

        // ---------------------------------------------------------------- schema v1
        static readonly string[] ExpectedTables =
        {
            "schema_version", "store_meta", "machine", "library_root", "file_entity", "identity_facet",
            "track_location", "producer", "run", "work_item", "check_result", "conflict",
            "value", "segment", "artifact", "track_feature",
        };

        static void TestSchema(Action<bool, string> assert)
        {
            assert(StoreSchema.FlattenKey(null, "bpm") == "bpm", "FlattenKey: top-level key unchanged");
            assert(StoreSchema.FlattenKey("keyVotes", "krumhansl") == "keyVotes_krumhansl", "FlattenKey: nested joins with '_'");
            assert(StoreSchema.FlattenKey(null, "fingerprint.v1") == "fingerprint_v1", "FlattenKey: '.' becomes '_'");
            assert(StoreSchema.FlattenKey("fingerprint_v1", "mp3LameTag") == "fingerprint_v1_mp3LameTag", "FlattenKey: two levels");

            var dir = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "schema-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                using (var db = SqliteDb.Open(Path.Combine(dir, "store.db"), create: true))
                {
                    SchemaMigrator.Apply(db, "selftest");
                    assert(SchemaMigrator.ReadVersion(db) == StoreSchema.CurrentVersion, $"schema_version == {StoreSchema.CurrentVersion} after Apply on an empty store");
                    foreach (var t in ExpectedTables)
                        assert(db.ExecScalar($"SELECT count(*) FROM sqlite_master WHERE type='table' AND name='{t}';") == "1", $"table {t} exists");
                    assert(db.ExecScalar("SELECT count(*) FROM producer;") == "3", "producer seeded with essentia/import/verify");
                    assert(db.ExecScalar("SELECT truedat_version FROM schema_version WHERE version = 1;") == "selftest", "schema_version records the truedat version that applied it");

                    SchemaMigrator.Apply(db, "selftest-again");
                    assert(SchemaMigrator.ReadVersion(db) == StoreSchema.CurrentVersion && db.ExecScalar("SELECT count(*) FROM schema_version;") == "1",
                        "Apply is idempotent on a current store (no second row, no error)");

                    // The drift test: every key the catalog writer emits has a column, and every column is an emitted key.
                    var emitted = EmittedCatalogKeys();
                    assert(emitted.Count > 100, $"writer emits a populated entry with >100 flattened keys (got {emitted.Count})");
                    var columns = new HashSet<string>(StringComparer.Ordinal);
                    using (var st = db.Prepare("PRAGMA table_info(track_feature);"))
                        while (st.Step()) columns.Add(st.GetString(1)!);
                    foreach (var meta in StoreSchema.TrackFeatureMetaColumns) columns.Remove(meta);
                    columns.Add("lastModified");   // lives on track_location.last_modified_utc by design
                    var missing = new List<string>();
                    foreach (var k in emitted) if (!columns.Contains(k)) missing.Add(k);
                    assert(missing.Count == 0, "DRIFT: every emitted catalog key has a track_feature column" + (missing.Count == 0 ? "" : " — missing: " + string.Join(", ", missing)));
                    var dead = new List<string>();
                    // A column the writer does not emit is dead UNLESS the emission registry (FieldPolicy) is what
                    // cuts it: trackId and bpmHistogram are TrackFeatures fields the store still captures.
                    foreach (var c in columns) if (!emitted.Contains(c) && !FieldPolicy.IsExcluded(c) && c != "lastModified") dead.Add(c);
                    assert(dead.Count == 0, "DRIFT: every track_feature column is an emitted catalog key" + (dead.Count == 0 ? "" : " — dead: " + string.Join(", ", dead)));

                    // Refuses a store newer than the exe.
                    db.Exec("INSERT INTO schema_version(version, applied_utc, truedat_version) VALUES (999, '2099-01-01T00:00:00Z', 'future');");
                    bool refused = false; string? msg = null;
                    try { SchemaMigrator.Apply(db, "selftest"); } catch (StoreException ex) { refused = true; msg = ex.Message; }
                    assert(refused && msg != null && msg.Contains("999") && msg.IndexOf("newer", StringComparison.OrdinalIgnoreCase) >= 0,
                        $"a store at schema 999 is refused with a sentence naming the versions ({msg})");
                }
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch { } }
        }

        /// <summary>Writes a fully populated TrackEntry through the real catalog writer and returns the
        /// flattened key set it emitted (the <c>truedat</c> verdict block excluded — it is write-time
        /// derived, never stored). Generated, never hand-listed.</summary>
        internal static HashSet<string> EmittedCatalogKeys()
        {
            var entry = FullyPopulatedEntry();
            using var ms = new MemoryStream();
            using (var jw = new System.Text.Json.Utf8JsonWriter(ms))
            {
                jw.WriteStartObject();
                Program.WriteTrackEntryForStore(jw, @"C:\m\x.flac", entry);
                jw.WriteEndObject();
            }
            ms.Position = 0;
            using var doc = System.Text.Json.JsonDocument.Parse(ms);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var top in doc.RootElement.EnumerateObject())
                Walk(top.Value, null, keys);
            return keys;

            static void Walk(System.Text.Json.JsonElement obj, string? prefix, HashSet<string> into)
            {
                foreach (var p in obj.EnumerateObject())
                {
                    if (prefix == null && p.Name == "truedat") continue;
                    var flat = StoreSchema.FlattenKey(prefix, p.Name);
                    if (p.Value.ValueKind == System.Text.Json.JsonValueKind.Object) Walk(p.Value, flat, into);
                    else into.Add(flat);
                }
            }
        }

        /// <summary>Every public field/property of TrackEntry, TrackFeatures and their nested blocks set
        /// to a non-default value, by reflection, so the writer emits every key it knows.</summary>
        internal static TrackEntry FullyPopulatedEntry()
        {
            var e = new TrackEntry { Features = new TrackFeatures(), LastModified = new DateTime(2026, 10, 11, 1, 2, 3, DateTimeKind.Utc) };
            Populate(e.Features);
            e.AnalysisDurationSecs = 12.5;
            e.FileMd5 = "0123456789abcdef0123456789abcdef";
            e.AudioStreamSha256 = "ab".PadRight(64, 'c');
            e.AudioStreamSha256Source = "flac-frames";
            var fp = new Program.FingerprintV1();
            Populate(fp);
            fp.AudioHead64kMd5Source = "flac-frames";   // "invariant" would be omitted by the writer
            e.FingerprintV1 = fp;
            return e;
        }

        static void Populate(object o)
        {
            var t = o.GetType();
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                f.SetValue(o, SampleFor(f.FieldType, f.Name));
            foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                if (p.CanWrite) p.SetValue(o, SampleFor(p.PropertyType, p.Name));
        }

        static object? SampleFor(Type type, string name)
        {
            var u = Nullable.GetUnderlyingType(type) ?? type;
            if (u == typeof(double)) return 1.5;
            if (u == typeof(int)) return 7;
            if (u == typeof(long)) return 9L;
            if (u == typeof(byte)) return (byte)3;
            if (u == typeof(bool)) return true;
            if (u == typeof(string)) return "x" + name;
            if (u == typeof(DateTime)) return new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            if (u == typeof(double[])) return new[] { 1.0, 2.0, 3.0 };
            if (u == typeof(int[])) return new[] { 1, 2, 3 };
            if (u.IsClass && u.GetConstructor(Type.EmptyTypes) != null)
            {
                var child = Activator.CreateInstance(u)!;
                Populate(child);
                return child;
            }
            return null;
        }

        static void Block(Action<bool, string> assert, string name, Action body)
        {
            try { body(); }
            catch (Exception ex) { assert(false, $"{name}: threw {ex.GetType().Name}: {ex.Message}"); }
        }

        // ---------------------------------------------------------------- StoreLog
        static void TestStoreLog(Action<bool, string> assert)
        {
            var lines = new List<string>();
            var oldSink = StoreLog.Sink; var oldThreshold = StoreLog.Threshold;
            try
            {
                StoreLog.Sink = lines.Add;
                StoreLog.SetVerbose(false);
                StoreLog.Debug("hidden"); StoreLog.Trace("hidden");
                StoreLog.Info("shown-info"); StoreLog.Warn("shown-warn"); StoreLog.Error("shown-error");
                assert(lines.Count == 3, $"non-verbose: Debug/Trace suppressed, Info/Warn/Error pass (got {lines.Count})");
                assert(lines.Count == 3 && lines[0].StartsWith("[store] INFO ") && lines[0].EndsWith("shown-info"),
                    "line format is '[store] LEVEL message'");
                lines.Clear();
                StoreLog.SetVerbose(true);
                StoreLog.Debug("d"); StoreLog.Trace("t");
                assert(lines.Count == 2, "--audit (verbose) lets Debug and Trace through");
            }
            finally { StoreLog.Sink = oldSink; StoreLog.Threshold = oldThreshold; }
        }

        // ---------------------------------------------------------------- probe, through the seam
        static void TestProbeSeam(Action<bool, string> assert)
        {
            Func<string> ver = () => "x";
            Func<string, bool> opt = _ => false;

            var r = SqliteProbe.Probe(() => throw new DllNotFoundException("winsqlite3.dll"), ver, opt);
            assert(!r.Ok && r.Failure == SqliteProbeFailure.DllNotFound, "DllNotFoundException -> DllNotFound");
            assert(r.Message != null && r.Message.Contains("winsqlite3.dll") && !r.Message.Contains("Exception"),
                "DllNotFound message names the DLL and is a sentence, not an exception dump");

            r = SqliteProbe.Probe(() => throw new BadImageFormatException("x86"), ver, opt);
            assert(!r.Ok && r.Failure == SqliteProbeFailure.BadImage, "BadImageFormatException -> BadImage");

            r = SqliteProbe.Probe(() => throw new EntryPointNotFoundException("sqlite3_x"), ver, opt);
            assert(!r.Ok && r.Failure == SqliteProbeFailure.EntryPointMissing, "EntryPointNotFoundException -> EntryPointMissing");

            r = SqliteProbe.Probe(() => SqliteProbe.MinimumVersionNumber - 1, () => "3.39.9", opt);
            assert(!r.Ok && r.Failure == SqliteProbeFailure.VersionBelowMinimum, "version 3039999 -> VersionBelowMinimum");
            assert(r.Message != null && r.Message.Contains("3.39.9") && r.Message.Contains(SqliteProbe.MinimumVersionText),
                "VersionBelowMinimum message states found and required versions");

            r = SqliteProbe.Probe(() => SqliteProbe.MinimumVersionNumber, () => "3.40.0", o => o == "ENABLE_RTREE");
            assert(r.Ok && r.VersionNumber == SqliteProbe.MinimumVersionNumber && r.VersionText == "3.40.0",
                "exact minimum version is accepted");
            assert(r.HasRtree && !r.HasFts5, "compile options are probed per name (RTREE yes, FTS5 no)");
        }

        // ---------------------------------------------------------------- probe, for real
        static void TestProbeReal(Action<bool, string> assert)
        {
            var r = SqliteProbe.Probe();
            assert(r.Ok, $"real winsqlite3.dll probe is Ok ({r.Failure}: {r.Message})");
            assert(r.VersionNumber >= SqliteProbe.MinimumVersionNumber, $"real version {r.VersionText} >= {SqliteProbe.MinimumVersionText}");
            assert(r.HasRtree, "RTREE compiled in (true on every Windows build seen: Win10 + Win11)");
        }

        // ---------------------------------------------------------------- db
        static void TestDb(Action<bool, string> assert)
        {
            // Non-ASCII on purpose — the path footgun that gave truedat its staging subsystem.
            var dir = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "späike-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "store.db");
            try
            {
                bool threw = false;
                try { SqliteDb.Open(path, create: false).Dispose(); } catch (SqliteException) { threw = true; }
                assert(threw, "Open(create:false) on a missing file throws SqliteException");

                using (var db = SqliteDb.Open(path, create: true))
                {
                    assert(string.Equals(db.ExecScalar("PRAGMA journal_mode;"), "wal", StringComparison.OrdinalIgnoreCase), "WAL is on after Open");
                    assert(db.ExecScalar("PRAGMA foreign_keys;") == "1", "foreign_keys is ON after Open (recovery review: do not repeat FK-off)");

                    db.Exec("CREATE TABLE t (id INTEGER PRIMARY KEY, i INTEGER, d REAL, s TEXT, b BLOB, n TEXT);");

                    var blob = new byte[] { 1, 2, 3, 0, 255 };
                    using (var ins = db.Prepare("INSERT INTO t(i, d, s, b, n) VALUES (?, ?, ?, ?, ?);"))
                    {
                        ins.Bind(1, 42L).Bind(2, 2.5).Bind(3, "héllo").Bind(4, blob).BindNull(5);
                        assert(!ins.Step(), "INSERT Step() returns false (DONE)");
                    }
                    assert(db.Changes == 1, "Changes == 1 after one insert");
                    assert(db.LastInsertRowId == 1, "LastInsertRowId == 1");

                    using (var sel = db.Prepare("SELECT i, d, s, b, n FROM t WHERE id = ?;"))
                    {
                        sel.Bind(1, 1L);
                        assert(sel.Step(), "SELECT Step() returns true on a row");
                        assert(sel.GetInt64(0) == 42 && sel.GetDouble(1) == 2.5 && sel.GetString(2) == "héllo", "int64/double/UTF-8 text round-trip");
                        var back = sel.GetBlob(3);
                        assert(back != null && back.Length == 5 && back[4] == 255 && back[3] == 0, "blob round-trips including a 0 byte");
                        assert(sel.IsNull(4) && sel.GetString(4) == null, "NULL reads as IsNull and null string");
                        assert(!sel.Step(), "no second row");
                        sel.Reset(); sel.Bind(1, 1L);
                        assert(sel.Step() && sel.GetInt64(0) == 42, "Reset() lets a statement run again");
                    }

                    using (var tx = db.BeginTransaction())
                    {
                        db.Exec("INSERT INTO t(i) VALUES (7);");
                        // no Complete(): dispose must roll back
                    }
                    assert(db.ExecScalar("SELECT count(*) FROM t;") == "1", "transaction disposed without Complete() rolls back");
                    using (var tx = db.BeginTransaction())
                    {
                        db.Exec("INSERT INTO t(i) VALUES (8);");
                        tx.Complete();
                    }
                    assert(db.ExecScalar("SELECT count(*) FROM t;") == "2", "transaction with Complete() commits");

                    bool bad = false; int rc = 0; string? msg = null;
                    try { db.Exec("SELEKT nonsense;"); } catch (SqliteException ex) { bad = true; rc = ex.ResultCode; msg = ex.Message; }
                    assert(bad && rc != 0 && msg != null && msg.IndexOf("syntax", StringComparison.OrdinalIgnoreCase) >= 0,
                        $"bad SQL throws SqliteException with rc != 0 and sqlite's own message (rc={rc}: {msg})");
                }
                assert(File.Exists(path), "database file exists at the non-ASCII path after close");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }
}
