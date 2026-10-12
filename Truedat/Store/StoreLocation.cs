using System;
using System.IO;

namespace Truedat.Store
{
    /// <summary>Where a store lives when nobody names one (S2 C0, operator rulings 2026-10-11): the store
    /// file is LOCAL. Default = <c>truedat.db</c> beside the catalog when that folder is on a local volume;
    /// when the catalog folder is a network location, a local per-library path instead
    /// (<c>%LOCALAPPDATA%\truedat\&lt;library-id&gt;\truedat.db</c>); <c>--store-dir</c> (settable, a
    /// directory — a knob, not a target) relocates that per-library layout to another local directory.
    /// An explicit <c>--store</c> always wins, network or not; a network store is allowed, warned about
    /// at open, and opened with exclusive locking (see <see cref="OpenStore"/>).</summary>
    internal static class StoreLocation
    {
        internal const string FileName = "truedat.db";

        /// <summary>16 lowercase hex of FNV-1a 64 over the catalog directory, case-folded with the trailing
        /// separator trimmed — same shape as the exclusion ruleId: derived, never stored.</summary>
        internal static string LibraryId(string catalogDir)
        {
            var norm = catalogDir.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
            ulong h = 14695981039346656037UL;
            foreach (char c in norm) { h ^= c; h *= 1099511628211UL; }
            return h.ToString("x16");
        }

        /// <summary>UNC, or a drive letter Windows reports as a network drive. No I/O beyond DriveInfo.</summary>
        internal static bool IsNetworkPath(string path)
        {
            string full;
            try { full = Path.GetFullPath(path); } catch { return false; }
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return false;
            try { return new DriveInfo(root!).DriveType == DriveType.Network; }
            catch { return false; }
        }

        internal static string Resolve(string? explicitStore, string? storeDir, string catalogPath, out string reason, string? localAppData = null)
        {
            if (!string.IsNullOrWhiteSpace(explicitStore))
            {
                reason = "explicit --store";
                return Path.GetFullPath(explicitStore!);
            }
            var catalogDir = Path.GetDirectoryName(Path.GetFullPath(catalogPath)) ?? Environment.CurrentDirectory;
            var id = LibraryId(catalogDir);
            if (!string.IsNullOrWhiteSpace(storeDir))
            {
                reason = $"--store-dir {storeDir}, library id {id}";
                return Path.Combine(Path.GetFullPath(storeDir!), id, FileName);
            }
            if (IsNetworkPath(catalogDir))
            {
                var app = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                reason = $"catalog folder is a network location; the store file stays local (library id {id})";
                return Path.Combine(app, "truedat", id, FileName);
            }
            reason = "beside the catalog";
            return Path.Combine(catalogDir, FileName);
        }
    }
}
