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

        SqliteDb(IntPtr db, string path) { _db = db; _path = path; }

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

        internal int Changes => SqliteNative.sqlite3_changes(_db);
        internal long LastInsertRowId => SqliteNative.sqlite3_last_insert_rowid(_db);

        internal string ErrMsg() => SqliteNative.Utf8(SqliteNative.sqlite3_errmsg(_db)) ?? "unknown sqlite error";

        static string Trim(string sql) => sql.Length <= 80 ? sql : sql.Substring(0, 77) + "...";

        public void Dispose()
        {
            if (_db == IntPtr.Zero) return;
            // Leave the .db self-contained: a store travels to mirror boxes as a file by the same sync
            // that carries the catalog, so no -wal may be left holding pages (S2 C0).
            try { Exec("PRAGMA wal_checkpoint(TRUNCATE);"); }
            catch (SqliteException ex) { StoreLog.Debug($"checkpoint on close skipped: {ex.Message}"); }
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
