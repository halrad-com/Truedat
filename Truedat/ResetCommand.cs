using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Truedat
{
    /// <summary>
    /// <c>--reset</c>: put every file truedat wrote for a library into one zip, remove them, and let
    /// the scan that follows start from nothing.
    ///
    /// A scan trusts what a catalog already holds — an entry with every field present is a cache
    /// hit, whatever build measured it. That is right for a normal scan and it means a field whose
    /// definition later changed stays as it was. This is the verb for "measure all of it again":
    /// not a flag on the cache, a clean start, with the old state kept in one file.
    ///
    /// What makes it safe to offer: nothing is removed until the archive has been written and read
    /// back; a file another program holds stops the run before the archive is written; and the scan
    /// entry refuses to begin unless it can also finish (extractor present, library on this machine).
    /// The exclusion rules and the config file are the operator's decisions, not scan output, and stay.
    /// </summary>
    internal static class ResetCommand
    {
        internal const string Flag = "reset";
        /// <summary>The confirmation for a run nobody is sitting at. Not a config key, on purpose.</summary>
        internal const string ConfirmFlag = "imeanit";
        /// <summary>What a person types at the prompt. Upper case and exact: a reflex "y" or "reset" is not it.</summary>
        internal const string ConfirmWord = "RESET";
        internal const string ArchivePrefix = "mbxmoods-reset-";

        /// <summary>The flags that may ride with <c>--reset</c>: options of the library scan. Anything else on the line is
        /// another verb or another verb's option, and the run is refused naming it — a reset that quietly did not happen,
        /// or happened beside something else, is worse than a refusal. Allow-list, so a flag added later is refused until
        /// someone decides it belongs here. Every config key must be on it (pinned by the self-test): a config key becomes
        /// an argument on every run.</summary>
        internal static readonly string[] ScanOptions =
        {
            Flag, ConfirmFlag,
            "analyze", "p", "parallel", "audit", "audit-verdicts", "pause", "allow-sleep", "background", "cpu-limit", "budget",
            "no-config", "no-stage", "stage-dir", "no-quick-cache", "no-dir-batch",
            "file-md5", "no-bitusage", "no-hf-analysis", "smfm", "no-smfm",
            "enableshortfiles", "max-duration", "long-track-mins",
            "exclusions", "no-exclusions", "exclude-playlist", "accept-flac-tag-drift",
            "retry-errors", "refresh", "refresh-features", "refresh-smfm", "refresh-authenticity", "refresh-chroma",
            "?", "h", "help", "version", "v",   // these print and exit before any scan, so nothing is reset beside them
            "store", "store-dir", "no-store", "edges", "no-edges", "edge-arrays", "no-edge-arrays",
            "publish", "no-publish", "split-mb", "keep-backups", "scan-summary", "fixup-after-scan",
        };

        /// <summary>The first token on the command line that is a flag <c>--reset</c> cannot be combined with, as typed;
        /// null when there is none. Read the way the parser reads it: any token whose canonical form is a known flag is
        /// that flag (a bare word included), and the token after a value-taking flag is its value.</summary>
        internal static string? ConflictingFlag(string[] args)
        {
            var known = new HashSet<string>(Program.KnownFlags, StringComparer.OrdinalIgnoreCase);
            var allowed = new HashSet<string>(ScanOptions, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                var canonical = Program.CanonicalFlag(args[i]);
                if (!known.Contains(canonical)) continue;                 // a positional (the library path), or a value
                if (!allowed.Contains(canonical)) return args[i];
                if (Program.TakesValue(canonical)) i++;                   // its value is not a flag, whatever it spells
            }
            return null;
        }

        /// <summary>Files that match truedat's naming but are not this reset's to take: the operator's exclusion rules and
        /// their backups, the field registry, a snapshot somebody took on purpose, and an earlier reset's archive (a reset
        /// never swallows the previous one).</summary>
        internal static bool IsKept(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            bool Starts(string prefix) => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            return Starts(Path.GetFileNameWithoutExtension(ExclusionStore.FileName))   // mbxmoods-exclude.json and its children
                || Starts("mbxmoods-snapshot-")
                || Starts(ArchivePrefix)
                || string.Equals(name, "mbxmoods-schema.json", StringComparison.OrdinalIgnoreCase);
        }

        internal enum Confirmation { Confirmed, Declined, NotAsked }

        /// <summary>Has the operator said yes. The flag is a yes. With no flag and nobody at the console there is no one to
        /// ask, so nothing is read and nothing is written (<see cref="Confirmation.NotAsked"/>, the caller refuses). At a
        /// console the answer must be the word itself.</summary>
        internal static Confirmation Confirm(bool flagGiven, bool inputRedirected, Func<string?> readLine, TextWriter prompt)
        {
            if (flagGiven) return Confirmation.Confirmed;
            if (inputRedirected) return Confirmation.NotAsked;
            prompt.Write($"  Type {ConfirmWord} to continue (anything else leaves everything as it is): ");
            prompt.Flush();
            var answer = readLine();
            return string.Equals(answer?.Trim(), ConfirmWord, StringComparison.Ordinal) ? Confirmation.Confirmed : Confirmation.Declined;
        }

        /// <summary>The reset is only worth doing where the library can be analyzed again. A catalog copied to a machine
        /// that does not hold the audio is the one place where removing it loses the analysis for good, so the entry
        /// samples the library's files first: fewer than half present means the audio is not here.</summary>
        internal static bool LibraryLooksAbsent(int sampled, int found) => sampled <= 0 || found * 2 < sampled;

        /// <summary>Up to <paramref name="max"/> indexes spread evenly over a list of <paramref name="count"/>, so the
        /// sample covers every volume the library spans and not only whatever sorts first.</summary>
        internal static List<int> SampleIndexes(int count, int max)
        {
            var picked = new List<int>();
            if (count <= 0 || max <= 0) return picked;
            if (count <= max) { for (int i = 0; i < count; i++) picked.Add(i); return picked; }
            for (int k = 0; k < max; k++) picked.Add((int)((long)k * (count - 1) / (max - 1)));
            return picked;
        }

        /// <summary>The file set: everything truedat wrote for this library, as (path, name inside the archive).
        /// Beside the catalog it is whatever the scan summary's own size block counts (<see cref="Program.ClassifySideFile"/>,
        /// one definition of "ours"), less <see cref="IsKept"/>. A store kept in another folder brings that folder's store
        /// files under <c>store/</c>; the review ledger, when it lives in the hub's review folder, comes as
        /// <c>review/</c> — and only the ledger, because the other manifests there belong to other verbs.</summary>
        internal static List<(string Path, string EntryName)> Collect(string catalogPath, string? storePath, string? reviewLedgerPath)
        {
            var files = new List<(string Path, string EntryName)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string path, string entry) { if (seen.Add(Path.GetFullPath(path))) files.Add((path, entry)); }

            string dir = Path.GetDirectoryName(Path.GetFullPath(catalogPath)) ?? ".";
            string catalogName = Path.GetFileName(catalogPath);
            string sidecarName = Path.GetFileName(Path.ChangeExtension(catalogPath, ".mbxs"));
            string companion = global::Truedat.Edges.EdgeCompanion.FileName;
            string? storeDir = storePath != null ? Path.GetDirectoryName(Path.GetFullPath(storePath)) : null;
            bool storeHere = storeDir != null && string.Equals(storeDir, dir, StringComparison.OrdinalIgnoreCase);
            // A store at the default name beside the catalog is this library's whether or not this run uses it.
            string storeNameHere = storeHere ? Path.GetFileName(storePath!) : global::Truedat.Store.StoreLocation.FileName;

            if (Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(f);
                    if (IsKept(name)) continue;
                    if (Program.ClassifySideFile(name, catalogName, sidecarName, companion, storeNameHere) != Program.SideFile.None) Add(f, name);
                }

            if (storeDir != null && !storeHere && Directory.Exists(storeDir))
            {
                string storeName = Path.GetFileName(storePath!);
                foreach (var f in Directory.GetFiles(storeDir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(f);
                    // This folder is not the library's: another library's lists, a log or an exported edges file may sit in it.
                    // Only the store, its write-ahead pair, what is named after it, and the store's own result files are ours here —
                    // decided by name, not by the classifier, which is right about a catalog's folder and too generous for this one.
                    bool ours = string.Equals(name, storeName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, storeName + "-wal", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, storeName + "-shm", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith(storeName + ".", StringComparison.OrdinalIgnoreCase)
                        || (name.StartsWith("store-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
                    if (ours) Add(f, "store/" + name);
                }
            }

            if (reviewLedgerPath != null && File.Exists(reviewLedgerPath))
                Add(reviewLedgerPath, "review/" + Path.GetFileName(reviewLedgerPath));   // already listed when it sits beside the catalog
            return files;
        }

        /// <summary>The first file something else has open, or that cannot be opened for writing; null when all are free.
        /// Asked BEFORE the archive is written: removing most of a library's files and then stopping on the one a running
        /// program holds would leave a state no scan was ever meant to start from.</summary>
        internal static string? FirstHeld(IReadOnlyList<(string Path, string EntryName)> files)
        {
            foreach (var f in files)
            {
                try { using (new FileStream(f.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (IOException) { return f.Path; }
                catch (UnauthorizedAccessException) { return f.Path; }
            }
            return null;
        }

        /// <summary>Which store file belongs to this library, and whether the scan after the reset uses one — the same answer
        /// the scan's own store setup would give, taken while the file still exists. A store already resolved for this run
        /// stays in force (re-derived to the real catalog's when it was resolved against a guess and not named). With none
        /// resolved, a store sitting at the derived place is still this library's file and is archived and removed either
        /// way; it comes back only if a plain scan would have adopted it, which --no-store and config store false refuse.
        /// Without this the reset removes the one thing that made the next scan use a store, and a library that had edges
        /// would need the whole pass a second time to get them back.</summary>
        internal static (string? Store, bool InForce) StoreAfterReset(string? resolvedStore, bool storeExplicit, string? derived, bool derivedExists, bool noStoreTyped, bool configStoreOff)
        {
            string? store = Program.ShouldRederiveStore(storeExplicit, resolvedStore, derived) ? derived : resolvedStore;
            if (store != null) return (store, true);
            if (derived != null && derivedExists)
                return (derived, Program.ShouldAdoptExistingStore(noStoreTyped, storeAsked: false, storeVerb: false, storeFileExists: true, configStoreOff));
            return (null, false);
        }

        /// <summary>Fold a store's write-ahead log into its main file, so the main file by itself is the whole store. A store
        /// closed normally is already like that; one whose scan was killed can hold its last committed changes in the log
        /// only. <c>--restore</c> takes the main file out of an archive, so without this those changes would sit in the zip
        /// and be missing from what is restored. Opening and closing the store is all it takes — the close checkpoints.
        /// False when there is no store or it will not open (a damaged store is then archived as it is).</summary>
        internal static bool FoldStoreLog(string storePath)
        {
            if (!File.Exists(storePath)) return false;
            try { using (global::Truedat.Store.SqliteDb.Open(storePath, create: false)) { } return true; }
            catch (Exception) { return false; }
        }

        internal sealed class Result
        {
            public int Archived;
            public long Bytes;
            /// <summary>Files that were archived but could not then be removed. Empty on a clean reset.</summary>
            public List<string> NotRemoved = new List<string>();
        }

        /// <summary>Archive, then remove. The archive is written to a temp name, read back entry by entry against the bytes
        /// read from disk, and moved into place without writing over anything (the same writer every catalog backup goes through);
        /// if any of that fails it throws and nothing has been removed. A damaged catalog is archived as it is — a reset may
        /// be the remedy for one. The store's files go first: with a store the catalog is derived from it, so a run cut
        /// short between the two leaves files that say less, never files that say more than what remains.</summary>
        internal static Result Execute(IReadOnlyList<(string Path, string EntryName)> files, string archivePath, string? storeName = null)
        {
            var result = new Result();
            // An archive that was being written when an earlier reset was stopped: unfinished, never moved into place, and
            // named for that run's time, so nothing would ever write over it or remove it.
            try
            {
                foreach (var stale in Directory.GetFiles(Path.GetDirectoryName(Path.GetFullPath(archivePath)) ?? ".", ArchivePrefix + "*.zip.tmp"))
                    try { File.Delete(stale); } catch { }
            }
            catch { }
            foreach (var f in files) result.Bytes += new FileInfo(f.Path).Length;
            CatalogArchive.WriteZipBackup(files, archivePath, requireWholeDocuments: false);
            result.Archived = files.Count;
            string storeFile = storeName ?? global::Truedat.Store.StoreLocation.FileName;
            bool IsStoreFile(string entry) => entry.StartsWith("store/", StringComparison.Ordinal)
                || entry.StartsWith(storeFile, StringComparison.OrdinalIgnoreCase);
            foreach (var f in files.OrderBy(f => IsStoreFile(f.EntryName) ? 0 : 1))
            {
                try { File.SetAttributes(f.Path, FileAttributes.Normal); File.Delete(f.Path); }
                catch (Exception) { result.NotRemoved.Add(f.Path); }
            }
            return result;
        }

        internal static string ArchivePathFor(string catalogPath, DateTime nowLocal)
            => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(catalogPath)) ?? ".",
                            ArchivePrefix + nowLocal.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".zip");
    }
}
