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
