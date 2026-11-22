using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Truedat
{
    /// <summary>
    /// <c>--clear</c> and <c>--reset</c>: put every file truedat wrote for a library into one zip, remove them, and let the
    /// scan that follows start from nothing. One implementation; the two verbs differ only in what they keep
    /// (<see cref="Keeps"/>, the one answer, tested as a table).
    ///
    /// A scan trusts what a catalog already holds — an entry with every field present is a cache hit, whatever build
    /// measured it. That is right for a normal scan and it means a field whose definition later changed stays as it was.
    /// These are the verbs for "measure all of it again": not a flag on the cache, a clean start, with the old state kept
    /// in one file.
    ///
    /// <c>--clear</c> (operator 2026-11-21: "clear keeps what you have but clears the truedat db") keeps the operator's own
    /// files — the exclusion rules, <c>apply-result.json</c>, the duplicate verdicts — and carries the review list's ignore
    /// decisions into a fresh list ("give me a clean slate but i dont want to resetup how its working now"). <c>--reset</c>
    /// ("reset is a full reset") takes all of that too. Both keep the config file, the field registry, snapshots and every
    /// earlier clear or reset archive.
    ///
    /// What makes them safe to offer: nothing is removed until the archive has been written and read back; a file another
    /// program holds stops the run before the archive is written; and the scan entry refuses to begin unless it can also
    /// finish (extractor present, library on this machine). <c>--restore</c> of either archive brings the catalog back,
    /// and of a reset archive the operator's files too.
    /// </summary>
    internal static class ResetCommand
    {
        internal enum Verb { Clear, Reset }

        internal const string Flag = "reset";
        internal const string ClearFlag = "clear";
        /// <summary>The confirmation for a run nobody is sitting at, for either verb. Not a config key, on purpose.</summary>
        internal const string ConfirmFlag = "imeanit";
        /// <summary>What a person types at the prompt. Upper case and exact: a reflex "y" or "reset" is not it.</summary>
        internal const string ConfirmWord = "RESET";
        internal const string ClearConfirmWord = "CLEAR";
        internal const string ArchivePrefix = "mbxmoods-reset-";
        internal const string ClearArchivePrefix = "mbxmoods-clear-";

        internal static string FlagOf(Verb v) => v == Verb.Clear ? ClearFlag : Flag;
        internal static string WordOf(Verb v) => v == Verb.Clear ? ClearConfirmWord : ConfirmWord;
        internal static string PrefixOf(Verb v) => v == Verb.Clear ? ClearArchivePrefix : ArchivePrefix;

        /// <summary>The flags that may ride with <c>--clear</c> or <c>--reset</c>: options of the library scan. Anything else
        /// on the line is another verb or another verb's option — the other of the two included — and the run is refused
        /// naming it: a reset that quietly did not happen, or happened beside something else, is worse than a refusal.
        /// Allow-list, so a flag added later is refused until someone decides it belongs here. Every config key must be on
        /// it (pinned by the self-test): a config key becomes an argument on every run.</summary>
        internal static readonly string[] ScanOptions =
        {
            ConfirmFlag,
            "analyze", "p", "parallel", "audit", "audit-verdicts", "pause", "allow-sleep", "background", "cpu-limit", "budget",
            "no-config", "no-stage", "stage-dir", "no-quick-cache", "no-dir-batch",
            "file-md5", "no-bitusage", "no-hf-analysis", "smfm", "no-smfm",
            "enableshortfiles", "max-duration", "long-track-mins",
            "exclusions", "no-exclusions", "exclude-playlist", "accept-flac-tag-drift",
            "retry-errors", "refresh", "refresh-features", "refresh-smfm", "refresh-authenticity", "refresh-chroma",
            "?", "h", "help", "version", "v",   // these print and exit before any scan, so nothing is reset beside them
            "store", "store-dir", "work-dir", "no-store", "edges", "no-edges", "edge-arrays", "no-edge-arrays",
            "publish", "no-publish", "split-mb", "keep-backups", "scan-summary", "fixup-after-scan",
        };

        /// <summary>The first token on the command line that is a flag <paramref name="verb"/> cannot be combined with, as
        /// typed; null when there is none. Read the way the parser reads it: any token whose canonical form is a known flag
        /// is that flag (a bare word included), and the token after a value-taking flag is its value.</summary>
        internal static string? ConflictingFlag(string[] args, Verb verb)
        {
            var known = new HashSet<string>(Program.KnownFlags, StringComparer.OrdinalIgnoreCase);
            var allowed = new HashSet<string>(ScanOptions, StringComparer.OrdinalIgnoreCase) { FlagOf(verb) };
            for (int i = 0; i < args.Length; i++)
            {
                var canonical = Program.CanonicalFlag(args[i]);
                if (!known.Contains(canonical)) continue;                 // a positional (the library path), or a value
                if (!allowed.Contains(canonical)) return args[i];
                if (Program.TakesValue(canonical)) i++;                   // its value is not a flag, whatever it spells
            }
            return null;
        }

        /// <summary>The operator's own files: decisions, not scan output — the exclusion rules with their backups and the
        /// exclusion playlist (everything named <c>mbxmoods-exclude*</c> but the lock), the result of applying them, and the
        /// duplicate verdicts. <c>--clear</c> keeps them; <c>--reset</c> archives and removes them.</summary>
        internal static bool IsOperatorFile(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (IsRulesLock(name)) return false;
            return name.StartsWith(Path.GetFileNameWithoutExtension(ExclusionStore.FileName), StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, ExclusionStore.ApplyResultFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, WorkFolder.DedupeVerdictsFileName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The exclusion file's lock: a zero-byte file the writers coordinate on and never delete. A mechanism, not a
        /// decision — kept by both verbs.</summary>
        static bool IsRulesLock(string name)
            => string.Equals(name, ExclusionStore.FileName + ExclusionStore.LockFileSuffix, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// THE keep-list: does <paramref name="verb"/> leave this file where it is? Both keep a snapshot somebody took on
        /// purpose, every earlier clear or reset archive (neither verb swallows a previous one), the config file and the
        /// field registry (they belong to the exe, which may serve several libraries, and say how the scan after runs), and
        /// the exclusion file's lock. <c>--clear</c> also keeps the operator's own files (<see cref="IsOperatorFile"/>).
        /// Everything else truedat wrote for the library is archived, then removed.
        /// </summary>
        internal static bool Keeps(Verb verb, string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            bool Starts(string prefix) => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            bool Is(string other) => string.Equals(name, other, StringComparison.OrdinalIgnoreCase);
            if (Starts("mbxmoods-snapshot-") || Starts(ArchivePrefix) || Starts(ClearArchivePrefix)) return true;
            if (Is("mbxmoods-schema.json") || Is(TruedatConfig.FileName) || IsRulesLock(name)) return true;
            return verb == Verb.Clear && IsOperatorFile(name);
        }

        internal enum Confirmation { Confirmed, Declined, NotAsked }

        /// <summary>Has the operator said yes. The flag is a yes. With no flag and nobody at the console there is no one to
        /// ask, so nothing is read and nothing is written (<see cref="Confirmation.NotAsked"/>, the caller refuses). At a
        /// console the answer must be the verb's own word.</summary>
        internal static Confirmation Confirm(Verb verb, bool flagGiven, bool inputRedirected, Func<string?> readLine, TextWriter prompt)
        {
            if (flagGiven) return Confirmation.Confirmed;
            if (inputRedirected) return Confirmation.NotAsked;
            prompt.Write($"  Type {WordOf(verb)} to continue (anything else leaves everything as it is): ");
            prompt.Flush();
            var answer = readLine();
            return string.Equals(answer?.Trim(), WordOf(verb), StringComparison.Ordinal) ? Confirmation.Confirmed : Confirmation.Declined;
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

        /// <summary>The file set: everything <paramref name="verb"/> takes for this library, as (path, name inside the archive).
        /// Beside the catalog it is whatever the scan summary's own size block counts (<see cref="Program.ClassifySideFile"/>,
        /// one definition of "ours") and, for <c>--reset</c>, the operator's own files (<c>apply-result.json</c> is not
        /// truedat's to the classifier) — less <see cref="Keeps"/>. The work folder's working files come under <c>work/</c>;
        /// a store kept in another folder brings that folder's store files under <c>store/</c>; the review ledger, when it
        /// lives in the hub's review folder, comes as <c>review/</c> — and only the ledger, because the other manifests there
        /// belong to other verbs.</summary>
        internal static List<(string Path, string EntryName)> Collect(Verb verb, string catalogPath, string? storePath, string? reviewLedgerPath)
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
                    if (Keeps(verb, name)) continue;
                    if (Program.ClassifySideFile(name, catalogName, sidecarName, companion, storeNameHere) != Program.SideFile.None
                        || IsOperatorFile(name))
                        Add(f, name);
                }

            // The exclusion playlist in the library's Playlists folder (review #10): the scan finds it there as well as beside the library
            // (PlaylistReader), so a reset that left it would rebuild with the rules it was asked to remove. Every name the scan would
            // try, not only the first: with that one gone the next would take its place. Under its relative name, so a restore puts it
            // back where it was. --clear keeps it, as it keeps the other rule files.
            foreach (var p in PlaylistReader.FolderCandidates(dir))
            {
                var name = Path.GetFileName(p);
                if (File.Exists(p) && !Keeps(verb, name)) Add(p, PlaylistReader.PlaylistsFolder + "/" + name);
            }

            // The work folder (WorkFolder): the working files truedat wrote for this library — the same classifier, narrowed to
            // what IS a working file, because the work folder can be the exe's own folder, where the exe, the config file and the
            // field registry sit. Entries under work/, after the catalog folder's, so a name held in both folders never collides
            // and the store a restore looks for (truedat.db) is the catalog folder's.
            var workDir = WorkFolder.For(dir);
            // By identity, not by name (review #14): a work folder that is the catalog folder through a junction would list every file twice.
            if (!WorkFolder.SameEntry(workDir, dir) && Directory.Exists(workDir))
                foreach (var f in Directory.GetFiles(workDir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(f);
                    if (Keeps(verb, name)) continue;
                    if (WorkFolder.IsWorkingFile(name, catalogName, sidecarName, companion, storeNameHere)) Add(f, "work/" + name);
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
            /// <summary>Files that were archived but could not then be removed. Empty on a clean run.</summary>
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
            // An archive that was being written when an earlier clear or reset was stopped: unfinished, never moved into
            // place, and named for that run's time, so nothing would ever write over it or remove it.
            try
            {
                var archiveDir = Path.GetDirectoryName(Path.GetFullPath(archivePath)) ?? ".";
                foreach (var prefix in new[] { ArchivePrefix, ClearArchivePrefix })
                    foreach (var stale in Directory.GetFiles(archiveDir, prefix + "*.zip.tmp"))
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

        /// <summary>The archive is a working file: the library's work folder (<see cref="WorkFolder"/>), named for its verb.</summary>
        internal static string ArchivePathFor(Verb verb, string catalogPath, DateTime nowLocal)
            => WorkFolder.Write(Path.GetDirectoryName(Path.GetFullPath(catalogPath)) ?? ".",
                                PrefixOf(verb) + nowLocal.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".zip");

        // ---- the review list across --clear ------------------------------------------------------------------------------

        /// <summary>The ledger <c>--clear</c> writes back after the archive, or null when there is nothing to write: only the
        /// records the operator marked ignore, unchanged. <c>--reset</c> carries nothing.</summary>
        internal static ReviewLedger? CarriedLedger(Verb verb, ReviewLedger before)
        {
            if (verb != Verb.Clear) return null;
            var carried = before.IgnoreOnly();
            return carried.Count > 0 ? carried : null;
        }

        // ---- the operator's files across --restore ----------------------------------------------------------------------

        /// <summary>The operator's files a reset archive holds: entries at its top level that <see cref="IsOperatorFile"/>
        /// names. A snapshot (it carries a manifest) also bundles the exclusion file, and restoring a snapshot has always
        /// left it in the archive — so a snapshot names none. A clear archive holds none by construction.</summary>
        internal static List<string> RuleEntries(string archivePath)
        {
            var names = new List<string>();
            try
            {
                using var zip = ZipFile.OpenRead(archivePath);
                if (zip.GetEntry(CatalogArchive.ManifestEntryName) != null) return names;
                foreach (var e in zip.Entries)
                    if ((IsLeafName(e.FullName) && IsOperatorFile(e.FullName)) || IsPlaylistsEntry(e.FullName)) names.Add(e.FullName);
            }
            catch (Exception) { /* not a zip, or unreadable: the restore's own read reports it */ }
            return names;
        }

        /// <summary>A plain file name: no folder in it, by either separator (Windows takes <c>\</c> as one too), no <c>..</c>, no drive, no
        /// root, no character a file name cannot hold. The only shape a rule file put back beside the catalog may have.</summary>
        internal static bool IsLeafName(string name)
        {
            if (string.IsNullOrEmpty(name) || name == "." || name == "..") return false;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;   // '\', '/', ':', '*', '?', '"', '<', '>', '|', control chars
            return string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) && !Path.IsPathRooted(name);
        }

        /// <summary>The entries of a zip whose names could steer a restore out of the folder it writes into: anything that is not a
        /// plain name or one of our own <c>folder/name</c> paths built from plain names (<c>work/</c>, <c>store/</c>, <c>review/</c>).
        /// truedat's own writers never make one; a name with a backslash, a <c>..</c>, a drive or a root is somebody else's archive,
        /// and the restore refuses the WHOLE archive before it writes anything (review #4). Empty for a gzip or plain file, and for a
        /// zip that cannot be read (the restore's own read reports that).</summary>
        internal static List<string> UnsafeEntries(string archivePath)
        {
            var bad = new List<string>();
            try
            {
                if (CatalogArchive.SniffFile(archivePath) != CatalogArchive.CatalogFormat.Zip) return bad;
                using var zip = ZipFile.OpenRead(archivePath);
                foreach (var e in zip.Entries)
                {
                    var segments = e.FullName.Split('/');
                    bool ok = segments.Length > 0;
                    for (int i = 0; ok && i < segments.Length; i++)
                        ok = IsLeafName(segments[i]) || (i == segments.Length - 1 && i > 0 && segments[i].Length == 0);   // "folder/" is a folder entry
                    if (!ok) bad.Add(e.FullName);
                }
            }
            catch (Exception) { }
            return bad;
        }

        /// <summary><c>Playlists/&lt;the exclusion playlist&gt;</c>: the one rule file that lives in a folder of its own (review #10).</summary>
        internal static bool IsPlaylistsEntry(string name)
        {
            var slash = name.IndexOf('/');
            if (slash <= 0 || !string.Equals(name.Substring(0, slash), PlaylistReader.PlaylistsFolder, StringComparison.OrdinalIgnoreCase)) return false;
            var leaf = name.Substring(slash + 1);
            return IsLeafName(leaf) && PlaylistReader.IsPlaylistPath(leaf)
                && leaf.StartsWith(PlaylistReader.ConventionName + ".", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Where a rule entry goes: <paramref name="catalogDir"/>\<paramref name="name"/> (or its Playlists folder for a
        /// <see cref="IsPlaylistsEntry"/>), and nowhere else — a name that is not a leaf, or whose full path's folder is not that folder,
        /// throws <see cref="InvalidDataException"/>.</summary>
        static string RuleDestination(string catalogDir, string name)
        {
            if (IsPlaylistsEntry(name))
            {
                var folder = Path.GetFullPath(Path.Combine(catalogDir, PlaylistReader.PlaylistsFolder));
                return RuleDestination(folder, name.Substring(name.IndexOf('/') + 1));
            }
            if (!IsLeafName(name)) throw new InvalidDataException($"{name} is not a plain file name; a rule file is only ever put back beside the catalog");
            var dir = Path.GetFullPath(catalogDir);
            var dest = Path.GetFullPath(Path.Combine(dir, name));
            var parent = Path.GetDirectoryName(dest);
            if (parent == null || !string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar), dir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{name} does not resolve to a file in {dir}");
            return dest;
        }

        /// <summary>The rule files a restore would put back that exist now, as full paths. A restore never writes over a
        /// decision the operator has made since the archive; it names the file and stops.</summary>
        internal static List<string> RuleConflicts(IEnumerable<string> entries, string catalogDir)
            => entries.Select(e => RuleDestination(catalogDir, e)).Where(File.Exists).ToList();

        /// <summary>Put the operator's files back beside the catalog, byte for byte, each written to a temp name and moved into
        /// place without writing over anything. Every name is checked before the first is written: one that is not a plain file name
        /// beside the catalog stops the step with nothing put back. Returns how many were put back; throws on the first that cannot be.</summary>
        internal static int RestoreRules(string archivePath, IEnumerable<string> entries, string catalogDir)
        {
            int put = 0;
            var names = entries.ToList();
            var dests = names.Select(n => RuleDestination(catalogDir, n)).ToList();
            using var zip = ZipFile.OpenRead(archivePath);
            for (int i = 0; i < names.Count; i++)
            {
                var name = names[i];
                var e = zip.GetEntry(name) ?? throw new IOException($"{name} is not in {archivePath}");
                var dest = dests[i];
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);   // a Playlists folder removed since the archive was made
                var tmp = dest + ".restore.tmp";
                try { File.Delete(tmp); } catch { }
                using (var src = e.Open())
                using (var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    src.CopyTo(dst);
                try { File.SetLastWriteTime(tmp, e.LastWriteTime.LocalDateTime); } catch { }
                File.Move(tmp, dest);   // not Replace: a file that appeared meanwhile fails here instead of being written over
                put++;
            }
            return put;
        }
    }
}
