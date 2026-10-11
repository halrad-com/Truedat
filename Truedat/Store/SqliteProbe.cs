using System;

namespace Truedat.Store
{
    internal enum SqliteProbeFailure { None, DllNotFound, BadImage, EntryPointMissing, VersionBelowMinimum }

    internal sealed class SqliteProbeResult
    {
        public SqliteProbeFailure Failure;
        public bool Ok => Failure == SqliteProbeFailure.None;
        public int VersionNumber;
        public string? VersionText;
        public bool HasFts5;
        public bool HasRtree;
        /// <summary>The plain sentence the operator reads on refusal. Names the DLL, what was found, what is required.</summary>
        public string? Message;
    }

    internal static class StoreExitCodes
    {
        /// <summary>Store cannot be opened (SQLite missing / wrong bitness / too old). 1 = generic, 3 = unreachable root.</summary>
        internal const int StoreUnavailable = 4;
    }

    /// <summary>The open guard (operator, 2026-10-11: "handle if not found, just in case"). Every
    /// failure class is a distinct, plain refusal — never a crash, never a stack trace, never a
    /// silent fallback. Compile options are probed by name because the Windows build varies per OS
    /// (Win10 ships without FTS5) while the version number does not tell you that.</summary>
    internal static class SqliteProbe
    {
        internal const int MinimumVersionNumber = 3040000;
        internal const string MinimumVersionText = "3.40.0";

        internal static SqliteProbeResult Probe() => Probe(
            SqliteNative.sqlite3_libversion_number,
            () => SqliteNative.Utf8(SqliteNative.sqlite3_libversion()) ?? "?",
            name => SqliteNative.sqlite3_compileoption_used(SqliteNative.Z(name)) != 0);

        /// <summary>Seam: the three delegates stand in for the native calls so each failure class can be
        /// driven by a self-test without a broken DLL to hand.</summary>
        internal static SqliteProbeResult Probe(Func<int> versionNumber, Func<string> versionText, Func<string, bool> compileOptionUsed)
        {
            var r = new SqliteProbeResult();
            try
            {
                r.VersionNumber = versionNumber();
                r.VersionText = versionText();
                if (r.VersionNumber < MinimumVersionNumber)
                {
                    r.Failure = SqliteProbeFailure.VersionBelowMinimum;
                    r.Message = $"{SqliteNative.Lib} is version {r.VersionText}; truedat needs {MinimumVersionText} or newer. Update Windows, or point truedat at a newer SQLite.";
                    return r;
                }
                r.HasFts5 = compileOptionUsed("ENABLE_FTS5");
                r.HasRtree = compileOptionUsed("ENABLE_RTREE");
                r.Failure = SqliteProbeFailure.None;
                return r;
            }
            catch (DllNotFoundException)
            {
                r.Failure = SqliteProbeFailure.DllNotFound;
                r.Message = $"{SqliteNative.Lib} was not found. truedat's store uses the SQLite that ships with Windows 10 (1511+) and Windows 11; this Windows does not have it.";
                return r;
            }
            catch (BadImageFormatException)
            {
                r.Failure = SqliteProbeFailure.BadImage;
                r.Message = $"{SqliteNative.Lib} could not be loaded into this process (bitness mismatch: truedat is {(Environment.Is64BitProcess ? "x64" : "x86")}).";
                return r;
            }
            catch (EntryPointNotFoundException ex)
            {
                r.Failure = SqliteProbeFailure.EntryPointMissing;
                r.Message = $"{SqliteNative.Lib} is present but too old: it lacks {ex.Message}. truedat needs {MinimumVersionText} or newer.";
                return r;
            }
        }
    }
}
