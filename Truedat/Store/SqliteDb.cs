using System;
using System.Runtime.InteropServices;

namespace Truedat.Store
{
    internal sealed class SqliteException : Exception
    {
        public int ResultCode { get; }
        public SqliteException(int rc, string message) : base(message) { ResultCode = rc; }
    }

    /// <summary>One connection. Opens with WAL, a 5 s busy timeout and foreign keys ON (the recovery
    /// review's first "do not repeat"). Everything is UTF-8 in and out, so a non-ASCII store path or
    /// value never needs translating.</summary>
    internal sealed class SqliteDb : IDisposable
    {
        IntPtr _db;
        readonly string _path;

        /// <summary>This process's open connections per store file. A close that leaves another of them open says nothing about
        /// the -wal: that connection checkpoints when it closes.</summary>
        static readonly System.Collections.Generic.Dictionary<string, int> s_open =
            new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        static string Key(string path) { try { return System.IO.Path.GetFullPath(path); } catch { return path; } }

        SqliteDb(IntPtr db, string path)
        {
            _db = db; _path = path;
            lock (s_open) s_open[Key(path)] = (s_open.TryGetValue(Key(path), out var n) ? n : 0) + 1;
        }

        /// <summary>Page size for a store created by this build. SQLite's default is 4096; a track_feature row
        /// is ~2 KB with its 149-column header, so two never fit a 4 KB leaf and every row took a page of its
        /// own (measured 2026-10-11: 368.9 MB for 131.7 MB of feature payload, 1.34x the JSON). At 16 KB
        /// about seven rows share a page and the same store is 261.8 MB, 0.95x the JSON. 32 KB gains 2 more
        /// points while doubling the bytes moved per point read and per small write — not worth it.</summary>
        internal const int DefaultPageSize = 16384;

        internal static SqliteDb Open(string path, bool create, int? pageSize = null, bool exclusiveLocking = false)
        {
            int flags = SqliteNative.SQLITE_OPEN_READWRITE | (create ? SqliteNative.SQLITE_OPEN_CREATE : 0);
            int rc = SqliteNative.sqlite3_open_v2(SqliteNative.Z(path), out var handle, flags, IntPtr.Zero);
            if (rc != SqliteNative.SQLITE_OK)
            {
                var msg = handle != IntPtr.Zero ? SqliteNative.Utf8(SqliteNative.sqlite3_errmsg(handle)) : null;
                if (handle != IntPtr.Zero) SqliteNative.sqlite3_close_v2(handle);
                throw new SqliteException(rc, $"cannot open {path}: {msg ?? ("sqlite rc " + rc)}");
            }
            var db = new SqliteDb(handle, path);
            try
            {
                SqliteNative.sqlite3_busy_timeout(handle, 5000);
                // Page size is only settable while the file is empty and before WAL is on; an existing
                // store keeps whatever it was built with (re-tune it with --store-vacuum --page-size).
                bool empty = db.ExecScalar("PRAGMA page_count;") == "0";
                if (empty) db.Exec($"PRAGMA page_size={pageSize ?? DefaultPageSize};");
                // A network store (explicit --store on a share) cannot use WAL's shared-memory file;
                // exclusive locking keeps WAL working for the one process truedat is by design.
                if (exclusiveLocking) db.Exec("PRAGMA locking_mode=EXCLUSIVE;");
                // The pragma does not throw when a filesystem refuses WAL; it answers with the mode in force. Say so.
                var mode = db.ExecScalar("PRAGMA journal_mode=WAL;");
                if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                    StoreLog.Warn($"{path}: WAL journal mode was refused; running with journal_mode={mode} (slower, still safe)");
                db.Exec("PRAGMA foreign_keys=ON;");
                StoreLog.Debug($"opened {path} (create={create}, wal, fk on" + (empty ? $", new file at page_size {pageSize ?? DefaultPageSize}" : "") + ")");
                return db;
            }
            catch { db.Dispose(); throw; }
        }

        internal string Path => _path;

        internal void Exec(string sql)
        {
            int rc = SqliteNative.sqlite3_exec(_db, SqliteNative.Z(sql), IntPtr.Zero, IntPtr.Zero, out var err);
            if (rc != SqliteNative.SQLITE_OK)
            {
                var msg = SqliteNative.Utf8(err) ?? ErrMsg();
                if (err != IntPtr.Zero) SqliteNative.sqlite3_free(err);
                throw new SqliteException(rc, $"{msg} [{Trim(sql)}]");
            }
        }

        /// <summary>First column of the first row as text, or null when the statement yields no row.
        /// For PRAGMAs and counts.</summary>
        internal string? ExecScalar(string sql)
        {
            using (var st = Prepare(sql))
                return st.Step() ? st.GetString(0) : null;
        }

        internal SqliteStmt Prepare(string sql)
        {
            int rc = SqliteNative.sqlite3_prepare_v2(_db, SqliteNative.Z(sql), -1, out var stmt, IntPtr.Zero);
            if (rc != SqliteNative.SQLITE_OK) throw new SqliteException(rc, $"{ErrMsg()} [{Trim(sql)}]");
            return new SqliteStmt(this, stmt);
        }

        internal SqliteTransaction BeginTransaction()
        {
            Exec("BEGIN IMMEDIATE;");
            return new SqliteTransaction(this);
        }

        /// <summary>A READ snapshot (deferred BEGIN): every read until <see cref="SqliteTransaction.Complete"/> sees
        /// one consistent state of the store — under WAL a concurrent writer's commit becomes invisible to it. No
        /// write lock is taken, so a running scan's flush is not blocked. Used by the export (store-v3 T07, keeper
        /// review of c89d48a P1): generation, entries and edge rows must come from ONE snapshot or a commit between
        /// the reads mislabels the published set.</summary>
        internal SqliteTransaction BeginRead()
        {
            Exec("BEGIN;");
            return new SqliteTransaction(this);
        }

        /// <summary>What the closing checkpoint achieved: true = the .db holds every committed change and the -wal is empty, false =
        /// a reader held an older snapshot past the busy wait, so committed pages are still only in the -wal; null = not closed yet.</summary>
        internal bool? CheckpointedOnClose { get; private set; }

        /// <summary>Test seam: the busy wait the closing checkpoint spends on a reader (5 s in production).</summary>
        internal void SetBusyTimeout(int ms) => SqliteNative.sqlite3_busy_timeout(_db, ms);

        internal int Changes => SqliteNative.sqlite3_changes(_db);
        internal long LastInsertRowId => SqliteNative.sqlite3_last_insert_rowid(_db);

        internal string ErrMsg() => SqliteNative.Utf8(SqliteNative.sqlite3_errmsg(_db)) ?? "unknown sqlite error";

        static string Trim(string sql) => sql.Length <= 80 ? sql : sql.Substring(0, 77) + "...";

        public void Dispose()
        {
            if (_db == IntPtr.Zero) return;
            // Leave the .db self-contained: a store travels to mirror boxes as a file by the same sync
            // that carries the catalog, so no -wal may be left holding pages (S2 C0).
            // The checkpoint waits (the busy timeout) for a reader on an older snapshot, then gives up and SAYS so in its result row
            // (busy, wal frames, frames copied) — it does not throw. The hub reads the store through a read-only connection, which can
            // never checkpoint, so what this close leaves in the -wal stays until truedat's next run: say so when it is not empty.
            int others;
            lock (s_open)
            {
                others = s_open.TryGetValue(Key(_path), out var n) ? n - 1 : 0;
                if (others > 0) s_open[Key(_path)] = others; else s_open.Remove(Key(_path));
            }
            bool complete = false, reader = false;
            try
            {
                using var st = Prepare("PRAGMA wal_checkpoint(TRUNCATE);");
                complete = !st.Step() || (st.GetInt64(0) == 0 && (st.GetInt64(1) <= 0 || st.GetInt64(2) >= st.GetInt64(1)));
                reader = !complete;
            }
            catch (SqliteException ex)
            {
                reader = ex.ResultCode == 5 || ex.ResultCode == 6;   // SQLITE_BUSY / SQLITE_LOCKED: another connection; anything else is not a reader
                if (!reader) StoreLog.Debug($"checkpoint on close failed: {ex.Message}");
            }
            CheckpointedOnClose = complete;
            if (reader && others == 0)
                StoreLog.Warn($"another program was reading the store as truedat closed it, so its latest changes are still in {_path}-wal. " +
                              "The store is complete as it stands; to copy it, copy that -wal with it (the next truedat run folds it in).");
            int rc = SqliteNative.sqlite3_close_v2(_db);
            if (rc != SqliteNative.SQLITE_OK) StoreLog.Warn($"close of {_path} returned rc {rc} (unfinalized statements?)");
            _db = IntPtr.Zero;
        }
    }

    /// <summary>A prepared statement. Bind indexes are 1-based (SQLite's), column indexes 0-based
    /// (SQLite's). Binds are TRANSIENT copies, so the caller's buffers are free the moment Bind returns.</summary>
    internal sealed class SqliteStmt : IDisposable
    {
        readonly SqliteDb _db;
        IntPtr _stmt;

        internal SqliteStmt(SqliteDb db, IntPtr stmt) { _db = db; _stmt = stmt; }

        internal SqliteStmt Bind(int index, long value) { Check(SqliteNative.sqlite3_bind_int64(_stmt, index, value)); return this; }
        internal SqliteStmt Bind(int index, double value) { Check(SqliteNative.sqlite3_bind_double(_stmt, index, value)); return this; }
        internal SqliteStmt Bind(int index, string? value)
        {
            if (value == null) return BindNull(index);
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            Check(SqliteNative.sqlite3_bind_text(_stmt, index, bytes, bytes.Length, SqliteNative.SQLITE_TRANSIENT));
            return this;
        }
        internal SqliteStmt Bind(int index, byte[]? value)
        {
            if (value == null) return BindNull(index);
            Check(SqliteNative.sqlite3_bind_blob(_stmt, index, value, value.Length, SqliteNative.SQLITE_TRANSIENT));
            return this;
        }
        internal SqliteStmt BindNull(int index) { Check(SqliteNative.sqlite3_bind_null(_stmt, index)); return this; }

        /// <summary>True when a row is available, false when the statement is done.</summary>
        internal bool Step()
        {
            int rc = SqliteNative.sqlite3_step(_stmt);
            if (rc == SqliteNative.SQLITE_ROW) return true;
            if (rc == SqliteNative.SQLITE_DONE) return false;
            throw new SqliteException(rc, _db.ErrMsg());
        }

        internal void Reset()
        {
            SqliteNative.sqlite3_reset(_stmt);
            SqliteNative.sqlite3_clear_bindings(_stmt);
        }

        internal bool IsNull(int col) => SqliteNative.sqlite3_column_type(_stmt, col) == SqliteNative.SQLITE_NULL;
        internal long GetInt64(int col) => SqliteNative.sqlite3_column_int64(_stmt, col);
        internal double GetDouble(int col) => SqliteNative.sqlite3_column_double(_stmt, col);
        internal string? GetString(int col) => IsNull(col) ? null : SqliteNative.Utf8(SqliteNative.sqlite3_column_text(_stmt, col));
        internal byte[]? GetBlob(int col)
        {
            if (IsNull(col)) return null;
            int n = SqliteNative.sqlite3_column_bytes(_stmt, col);
            var buf = new byte[n];
            if (n > 0) Marshal.Copy(SqliteNative.sqlite3_column_blob(_stmt, col), buf, 0, n);
            return buf;
        }

        void Check(int rc) { if (rc != SqliteNative.SQLITE_OK) throw new SqliteException(rc, _db.ErrMsg()); }

        public void Dispose()
        {
            if (_stmt == IntPtr.Zero) return;
            SqliteNative.sqlite3_finalize(_stmt);
            _stmt = IntPtr.Zero;
        }
    }

    /// <summary>BEGIN IMMEDIATE … COMMIT on <see cref="Complete"/>, ROLLBACK on dispose without it.</summary>
    internal sealed class SqliteTransaction : IDisposable
    {
        readonly SqliteDb _db;
        bool _done;

        internal SqliteTransaction(SqliteDb db) { _db = db; }

        internal void Complete()
        {
            if (_done) return;
            _db.Exec("COMMIT;");
            _done = true;
        }

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            try { _db.Exec("ROLLBACK;"); }
            catch (SqliteException ex) { StoreLog.Warn($"rollback failed: {ex.Message}"); }
        }
    }
}
