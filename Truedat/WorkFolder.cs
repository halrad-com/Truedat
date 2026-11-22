using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Truedat
{
    /// <summary>
    /// Where truedat's WORKING files go (operator 2026-11-21: "right now we are littering the catalog folder. i really
    /// think we need to move that to the folder with the exe by default").
    ///
    /// The catalog folder is the hub's folder: the catalog, its sidecar, the edges companion, the store and every
    /// <c>store-*.json</c> record are read from it BY LOCATION, and the operator's own rules live there too. Everything
    /// else truedat writes — the error and skip ledgers, scan summaries, the audit log, catalog and store backups,
    /// snapshots, reset archives, the reports a verb emits — is this process's working material, and it goes to ONE
    /// folder derived from the exe.
    ///
    /// <see cref="Resolve(string,string,string)"/> is that one rule; no site may spell one of those paths any other way.
    /// Three cases:
    ///   * <c>--work-dir</c> (or config <c>work-dir</c>) — used as given;
    ///   * the exe's folder IS the catalog folder or a direct child of it (the usual install,
    ///     <c>Library\truedat\truedat.exe</c>) — the exe's folder;
    ///   * anywhere else — <c>&lt;exe folder&gt;\libraries\&lt;library id&gt;\</c>, the same id the store uses to keep one
    ///     library's file apart from another's. One exe serving two libraries must never write two libraries' error
    ///     ledgers to one path.
    /// A folder that cannot be written falls back to the catalog folder, which is what every build before this did.
    ///
    /// Reading and writing differ on purpose. A WRITING run settles its work folder with <see cref="Begin"/> — created,
    /// and the files already beside the catalog moved into it (<see cref="MoveExisting"/>) before any of them is read —
    /// either explicitly at the scan's entry or on the first use of a working-file path (<see cref="BeginOnFirstUse"/>).
    /// A READING run creates nothing and moves nothing, and finds each file with <see cref="Read"/> — work folder first,
    /// catalog folder second — so an install that has not scanned since the upgrade answers exactly as before.
    /// </summary>
    internal static class WorkFolder
    {
        /// <summary>The folder under the exe that holds one sub-folder per library, when the exe is not installed in one.</summary>
        internal const string LibrariesDir = "libraries";
        /// <summary>The flag and the config key — one spelling.</summary>
        internal const string Flag = "work-dir";
        /// <summary>The suffix a file takes when it cannot be the live one at its name (both folders held one).</summary>
        internal const string MovedSuffix = ".moved-";
        /// <summary>The dedupe verdicts sidecar (restfulbee's intake-dedupe spec): written by a person or by
        /// <c>tools/init-dedupe-verdicts.ps1</c>, never by the exe.</summary>
        internal const string DedupeVerdictsFileName = "mbxmoods-duplicates.verdicts.json";
        /// <summary>Every file <c>--duplicates</c> writes, and the operator's verdicts keyed against them, start with this
        /// (architect ruling 2026-11-21: the family stays with the catalog, so a person running the dedupe scripts types one folder).</summary>
        internal const string DuplicatesPrefix = "mbxmoods-duplicate";

        /// <summary>Self-test seam: true makes the configured rule answer "the catalog folder", today's layout, so the in-process
        /// self-tests that write working files keep finding them where they look and write nothing under the build output.
        /// <see cref="WorkFolderSelfTest"/> turns it off for its own cases. False in production.</summary>
        internal static bool UseCatalogFolderForTest;

        /// <summary>Set by a WRITING run: called the first time a working-file path is asked for a catalog folder this run has
        /// not settled, so the work folder is created and the files beside the catalog are moved before anything reads or
        /// writes one. Null in a reading run — which is what keeps a reading run from moving or creating anything.</summary>
        internal static Action<string>? BeginOnFirstUse;

        static string? _option;
        static string _exeDir = AppDomain.CurrentDomain.BaseDirectory;
        static readonly object Gate = new object();
        static readonly Dictionary<string, string> Settled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> Created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> Beginning = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The work folder of the library this run is working on, once one has been settled; null before that.
        /// This is the value <c>store-result.json</c> reports as <c>workFolder</c> (hub contract), so the hub's utility never
        /// re-derives the rule. After a fallback it is the folder actually used: the catalog folder.</summary>
        internal static string? Current { get; private set; }

        /// <summary>The catalog folder <see cref="Current"/> was settled for; null before that.</summary>
        internal static string? CurrentCatalogDir { get; private set; }

        /// <summary>The operator's choice and the exe's folder, both known before any catalog is. Clears everything an earlier
        /// configuration settled, so one process can serve several (the self-tests).</summary>
        internal static void Configure(string? option, string exeDir)
        {
            lock (Gate)
            {
                _option = string.IsNullOrWhiteSpace(option) ? null : option;
                _exeDir = string.IsNullOrWhiteSpace(exeDir) ? AppDomain.CurrentDomain.BaseDirectory : exeDir;
                Settled.Clear(); Created.Clear(); Beginning.Clear();
                Current = null; CurrentCatalogDir = null;
            }
        }

        /// <summary>The rule, with nothing ambient and no I/O — what the self-test pins.</summary>
        internal static string Resolve(string catalogDir, string? option, string exeDir)
        {
            if (!string.IsNullOrWhiteSpace(option)) return Norm(option!);
            var exe = Norm(exeDir);
            var cat = Norm(catalogDir);
            if (Same(exe, cat)) return exe;                                              // the exe sits in the library folder itself
            var parent = Path.GetDirectoryName(exe);
            if (!string.IsNullOrEmpty(parent) && Same(Norm(parent!), cat)) return exe;   // Library\truedat\truedat.exe
            return Path.Combine(exe, LibrariesDir, global::Truedat.Store.StoreLocation.LibraryId(cat));
        }

        /// <summary>The rule against this run's configuration.</summary>
        internal static string Resolve(string catalogDir)
            => UseCatalogFolderForTest ? Norm(catalogDir) : Resolve(catalogDir, _option, _exeDir);

        /// <summary>Why the rule gave what it gave, for <c>--paths</c>.</summary>
        internal static string Reason(string catalogDir)
        {
            var cat = Norm(catalogDir);
            var dir = For(cat);
            if (Same(dir, cat)) return Same(Resolve(cat), cat) ? "the catalog folder" : "the catalog folder: the work folder could not be written";
            if (_option != null) return "--work-dir";
            return dir.StartsWith(Path.Combine(Norm(_exeDir), LibrariesDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? $"under the exe, library id {(global::Truedat.Store.StoreLocation.LibraryId(cat))}"
                : "the exe's folder (truedat is installed in the library)";
        }

        /// <summary>The work folder for a library, as this run has settled it, or as the rule gives it. Creates nothing.</summary>
        internal static string For(string catalogDir)
        {
            var cat = Norm(catalogDir);
            lock (Gate) { if (Settled.TryGetValue(cat, out var settled)) return settled; }
            return Resolve(cat);
        }

        /// <summary>A WRITING run settles its work folder: created, or the catalog folder when it cannot be
        /// (<paramref name="note"/> is then the one line that says so, naming both folders).</summary>
        internal static string Begin(string catalogDir, out string? note)
        {
            note = null;
            var cat = Norm(catalogDir);
            var want = Resolve(cat);
            // The catalog folder under another name (a junction to it, a subst drive): it IS the catalog folder, and is settled as that,
            // so nothing is moved into itself (review #14).
            if (!Same(want, cat) && SameEntry(want, cat)) want = cat;
            if (!Same(want, cat) && !Ensure(want))
            {
                note = $"Work folder: {want} cannot be written — truedat's working files stay beside the catalog in {cat}";
                want = cat;
            }
            lock (Gate) { Settled[cat] = want; Current = want; CurrentCatalogDir = cat; }
            return want;
        }

        /// <summary>True once this run has settled the work folder for <paramref name="catalogDir"/>.</summary>
        internal static bool IsSettled(string catalogDir)
        {
            var cat = Norm(catalogDir);
            lock (Gate) return Settled.ContainsKey(cat);
        }

        /// <summary>A writing run's first use of a catalog folder: settle it through <see cref="BeginOnFirstUse"/> before the path
        /// is handed out. Re-entry for the same folder (the begin itself asking for paths) is a no-op.</summary>
        static void FirstUse(string cat)
        {
            var hook = BeginOnFirstUse;
            if (hook == null) return;
            lock (Gate)
            {
                if (Settled.ContainsKey(cat) || !Beginning.Add(cat)) return;
            }
            try { hook(cat); }
            finally { lock (Gate) Beginning.Remove(cat); }
        }

        /// <summary>Where a working file is WRITTEN. The folder is created on the way, and falls back to the catalog folder
        /// rather than throw — a working file that cannot go to the work folder goes where it always went.</summary>
        internal static string Write(string catalogDir, string name)
        {
            var cat = Norm(catalogDir);
            FirstUse(cat);
            var dir = For(cat);
            if (!Same(dir, cat) && !Ensure(dir)) dir = cat;
            return Path.Combine(dir, name);
        }

        /// <summary>Where a working file is READ (or appended to): the work folder when it holds one, the catalog folder when
        /// it does (a file another program held when the move ran), and the work folder's path when neither does — that is
        /// where a write puts it.</summary>
        internal static string Read(string catalogDir, string name)
        {
            var cat = Norm(catalogDir);
            FirstUse(cat);
            var dir = For(cat);
            var work = Path.Combine(dir, name);
            if (Same(dir, cat)) return work;
            try { if (File.Exists(work)) return work; } catch { }
            var beside = Path.Combine(cat, name);
            try { if (File.Exists(beside)) return beside; } catch { }
            bool created; lock (Gate) created = Created.Contains(dir);
            if (created || BeginOnFirstUse != null) { if (!Ensure(dir)) return beside; }   // a writing run will append here
            return work;
        }

        /// <summary>The folders this library's truedat files can be in — the work folder first, the catalog folder second;
        /// one entry when they are the same. The size block, the reset's file set and every rotation read both.</summary>
        internal static List<string> Folders(string catalogDir)
        {
            var cat = Norm(catalogDir);
            FirstUse(cat);
            var work = For(cat);
            var list = new List<string> { work };
            if (!Same(work, cat)) list.Add(cat);
            return list;
        }

        /// <summary>Files matching <paramref name="pattern"/> across <see cref="Folders"/>, the work folder's copy winning
        /// a name held in both (a file that could not be moved is still found, and is never counted twice).</summary>
        internal static List<string> Files(string catalogDir, string pattern)
        {
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Folders(catalogDir))
            {
                string[] files;
                try { files = Directory.GetFiles(dir, pattern); } catch { continue; }
                foreach (var f in files) if (seen.Add(Path.GetFileName(f))) found.Add(f);
            }
            return found;
        }

        /// <summary>The same name in the other folder: beside the catalog for a work-folder path, in the work folder for a path
        /// beside the catalog; null when the two folders are one. For the dedupe keys a store holds by file path, so a ledger
        /// that moved is still recognised as the one already imported.</summary>
        internal static string? OtherPlace(string catalogDir, string path)
        {
            var cat = Norm(catalogDir);
            var work = For(cat);
            if (Same(work, cat)) return null;
            var name = Path.GetFileName(path);
            var here = Norm(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
            if (Same(here, work)) return Path.Combine(cat, name);
            if (Same(here, cat)) return Path.Combine(work, name);
            return null;
        }

        /// <summary>Where a backup of a STORE goes (the catalog's own backups use <see cref="Write"/> on the catalog folder). A store
        /// in a catalog folder this run has settled has its backups in that library's work folder; a store anywhere else — kept in
        /// its own folder — keeps them beside it, as before: that folder is not a catalog folder, and it is never settled or
        /// emptied from here. A store opened before its library was settled backs up beside itself, and the next writing run's
        /// move carries the backup over.</summary>
        internal static string BackupPath(string filePath, string backupName)
        {
            var dir = Norm(Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".");
            if (IsSettled(dir)) return Write(dir, backupName);
            return Path.Combine(dir, backupName);
        }

        /// <summary>The folders a backup named after <paramref name="filePath"/> can be in, for rotation: its backup folder first,
        /// then the file's own folder (backups written before the move). One entry when they are the same.</summary>
        internal static List<string> BackupFolders(string filePath)
        {
            var dir = Norm(Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".");
            var list = new List<string>();
            var backups = Norm(Path.GetDirectoryName(BackupPath(filePath, "x")) ?? dir);
            list.Add(backups);
            if (!Same(backups, dir)) list.Add(dir);
            return list;
        }

        // ---- what moves -------------------------------------------------------------------------------------

        /// <summary>
        /// Does this file truedat wrote belong in the work folder, or does it stay with the catalog?
        /// <see cref="Program.ClassifySideFile"/> stays the one definition of "a file truedat wrote"; this is the one
        /// definition of which of them are WORKING files. Everything the hub reads by location stays: the catalog and its
        /// numbered parts, the sidecar, the edges companion, the store with its write-ahead pair, every <c>store-*.json</c>
        /// record and everything truedat writes for the hub's review folder. So do the operator's rules, the duplicate
        /// reports with the verdicts keyed against them, and the field registry.
        /// </summary>
        internal static bool IsWorkingFile(string name, string catalogName, string sidecarName, string? companionName, string? storeName)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.StartsWith(".truedat-work-probe-", StringComparison.Ordinal)) return false;
            // A write in progress (a --restore's staged catalog, any save's temp file) belongs to the run writing it: moving it
            // out from under that run is what made a --restore by full path fail to install its catalog.
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return false;
            switch (Program.ClassifySideFile(name, catalogName, sidecarName, companionName, storeName))
            {
                case Program.SideFile.None: return false;              // not ours
                case Program.SideFile.AlreadyCounted: return false;    // the catalog, sidecar, companion, store, its WAL pair
                case Program.SideFile.Backup: return true;             // <catalog>.bak.<stamp>.zip
                default: return !StaysWithCatalog(name, catalogName, companionName);
            }
        }

        /// <summary>The files inside the Reports bucket that are the hub's or the operator's, not working material. A named
        /// rule, tested as a table in <see cref="WorkFolderSelfTest"/>.</summary>
        internal static bool StaysWithCatalog(string name, string catalogName, string? companionName)
        {
            bool Is(string other) => string.Equals(name, other, StringComparison.OrdinalIgnoreCase);
            bool Starts(string prefix) => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

            // The catalog's numbered parts (mbxmoods.json.1, .2, …) ARE the catalog.
            if (IsCatalogPart(name, catalogName)) return true;
            // The edges companion and anything named after it.
            var comp = string.IsNullOrEmpty(companionName) ? global::Truedat.Edges.EdgeCompanion.FileName : companionName!;
            if (Is(comp) || Starts(comp + ".")) return true;
            // The store's machine records, read beside the store by the hub (store-result.json and its siblings).
            if (Starts("store-") && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return true;
            // The operator's rules, their backups, and the exclusion playlist.
            if (Starts(Path.GetFileNameWithoutExtension(ExclusionStore.FileName))) return true;
            // The duplicate reports and the operator's verdicts on them: one folder for the pair.
            if (Starts(DuplicatesPrefix)) return true;
            // The field registry: a decision about what the catalog carries.
            if (Is("mbxmoods-schema.json")) return true;
            // What truedat writes for the hub to render: the review ledger and the preview page. Normally in the hub's
            // review folder; beside the catalog they are still the hub's. A --chunk shard's ledger carries the host suffix
            // (mbxmoods-review.<host>.json) and --merge-moods folds it from beside the shard's catalog, so it stays too.
            if (Starts(Path.GetFileNameWithoutExtension(ReviewLedger.FileName) + ".") || Starts("mbxmoods-preview.")) return true;
            return false;
        }

        /// <summary><c>&lt;catalog&gt;.N</c> — a numbered part of a split catalog, whatever the catalog is called.</summary>
        internal static bool IsCatalogPart(string name, string catalogName)
        {
            if (string.IsNullOrEmpty(catalogName) || name.Length <= catalogName.Length + 1) return false;
            if (!name.StartsWith(catalogName + ".", StringComparison.OrdinalIgnoreCase)) return false;
            var rest = name.Substring(catalogName.Length + 1);
            foreach (var c in rest) if (c < '0' || c > '9') return false;
            return rest.Length > 0;
        }

        // ---- the move ---------------------------------------------------------------------------------------

        /// <summary>What <see cref="MoveExisting"/> did, for the one line a run prints.</summary>
        internal sealed class MoveReport
        {
            public string Folder = "";
            /// <summary>Files moved out of the catalog folder.</summary>
            public int Moved;
            /// <summary>Files that could not be moved (another program holds them). They stay, and are still read.</summary>
            public List<string> Held = new List<string>();
            /// <summary>Names held in BOTH folders: the older copy was kept as <c>&lt;name&gt;.moved-&lt;time&gt;</c> beside the live one.</summary>
            public List<string> Renamed = new List<string>();
            /// <summary>Every name that moved, in the order it moved. Anything beside the catalog named <c>mbxmoods-*</c> is taken
            /// for truedat's, a file a person made included, so the run writes these down (<see cref="MovedNames"/>).</summary>
            public List<string> MovedNames = new List<string>();
            public bool Anything { get { return Moved > 0 || Held.Count > 0; } }
        }

        /// <summary>
        /// Move this library's working files out of the catalog folder into the work folder — a WRITING run's first act,
        /// before it reads any of them, so the error ledger it consults and the backups it rotates are the ones that were
        /// already there. Nothing is ever written over: when both folders hold a name, the NEWER file is the live one and
        /// the other is kept beside it as <c>&lt;name&gt;.moved-&lt;time&gt;</c>. A file another program holds stays where
        /// it is, is still read from there, and is reported. Never throws.
        /// </summary>
        internal static MoveReport MoveExisting(string catalogPath, string? storePath)
        {
            var report = new MoveReport();
            string cat;
            try { cat = Norm(Path.GetDirectoryName(Path.GetFullPath(catalogPath)) ?? "."); }
            catch { return report; }
            var work = For(cat);
            report.Folder = work;
            if (Same(work, cat) || !Directory.Exists(cat)) return report;
            if (SameEntry(work, cat)) return report;   // the same folder by another name: nothing to move (review #14)

            string catalogName = Path.GetFileName(catalogPath);
            string sidecarName = Path.GetFileName(Path.ChangeExtension(catalogPath, ".mbxs"));
            string companion = global::Truedat.Edges.EdgeCompanion.FileName;
            string storeName = global::Truedat.Store.StoreLocation.FileName;
            if (!string.IsNullOrEmpty(storePath))
            {
                try
                {
                    var sd = Path.GetDirectoryName(Path.GetFullPath(storePath!));
                    if (!string.IsNullOrEmpty(sd) && Same(Norm(sd!), cat)) storeName = Path.GetFileName(storePath!);
                }
                catch { }
            }

            string[] names;
            try { names = Directory.GetFiles(cat); } catch { return report; }
            Array.Sort(names, StringComparer.OrdinalIgnoreCase);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            foreach (var src in names)
            {
                var name = Path.GetFileName(src);
                if (!IsWorkingFile(name, catalogName, sidecarName, companion, storeName)) continue;
                if (!Ensure(work)) return report;
                var dest = Path.Combine(work, name);
                try
                {
                    if (File.Exists(dest) && SameEntry(src, dest)) continue;   // one file reached by two names: not a collision (review #14)
                    if (File.Exists(dest))
                    {
                        // Both folders hold this name. The newer one is the live file; the other keeps its content under a
                        // name nothing reads, beside the live one. Ties go to the one already in the work folder.
                        if (!CanTake(src)) { report.Held.Add(name); continue; }
                        bool incomingIsNewer = File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(dest);
                        var aside = Unique(Path.Combine(work, name + MovedSuffix + stamp));
                        if (incomingIsNewer)
                        {
                            File.Move(dest, aside);
                            if (MoveOne(src, dest)) { report.Moved++; report.Renamed.Add(name); report.MovedNames.Add(name + " (the older copy, already in the work folder, kept as " + Path.GetFileName(aside) + ")"); }
                            else { File.Move(aside, dest); report.Held.Add(name); }
                        }
                        else if (MoveOne(src, aside)) { report.Moved++; report.Renamed.Add(name); report.MovedNames.Add(name + " (older than the copy in the work folder, kept as " + Path.GetFileName(aside) + ")"); }
                        else report.Held.Add(name);
                    }
                    else if (MoveOne(src, dest)) { report.Moved++; report.MovedNames.Add(name); }
                    else report.Held.Add(name);
                }
                catch (Exception) { report.Held.Add(name); }
            }
            return report;
        }

        /// <summary>The one line a writing run prints about the move; null when nothing was there to move.</summary>
        internal static string? Describe(MoveReport r)
        {
            if (!r.Anything) return null;
            var line = $"Work folder: moved {r.Moved:N0} working file{(r.Moved == 1 ? "" : "s")} out of the catalog folder into {r.Folder}";
            if (r.Renamed.Count > 0) line += $"; {r.Renamed.Count:N0} name{(r.Renamed.Count == 1 ? " was" : "s were")} in both folders, the older copy kept as <name>{MovedSuffix}<time>";
            if (r.Held.Count > 0) line += $"; {r.Held.Count:N0} held by another program and left in the catalog folder, still read from there ({string.Join(", ", r.Held)})";
            return line;
        }

        /// <summary>The names that moved, one per line, under a heading that says where from and where to; empty when nothing moved.</summary>
        internal static List<string> MovedNames(MoveReport r, string catalogDir)
        {
            var lines = new List<string>();
            if (r.MovedNames.Count == 0) return lines;
            lines.Add($"Work folder: moved from {catalogDir} into {r.Folder}:");
            foreach (var n in r.MovedNames) lines.Add("    " + n);
            return lines;
        }

        /// <summary>Nothing else has the file open: it can be moved.</summary>
        static bool CanTake(string path)
        {
            try { using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } return true; }
            catch (Exception) { return false; }
        }

        /// <summary>Rename on one volume, copy-verify-publish-delete across two. False when the file cannot be moved at all —
        /// another program holds it, the copy did not read back as written, or another writer made the destination first; the
        /// original is then left untouched.</summary>
        /// <remarks>Review #13: the copy used to go straight to the destination, and when it failed because another writer had created
        /// that name after the existence check, the cleanup deleted the destination — the OTHER writer's file — because both files
        /// existed. Now the copy goes to a staging name in the destination folder that only this process could have chosen, is
        /// verified there (length and SHA-256), and is published with a move that never writes over a name; the source is deleted
        /// last. The only file this ever deletes on failure is its own staging file, so a collision keeps both writers' data: theirs
        /// at the name, ours where it was.</remarks>
        static bool MoveOne(string src, string dest)
        {
            if (!CanTake(src)) return false;
            if (SameVolume(src, dest))
            {
                try { File.Move(src, dest); return true; }
                catch (Exception) { return false; }
            }
            var staging = Path.Combine(Path.GetDirectoryName(dest) ?? ".",
                Path.GetFileName(dest) + ".truedat-move-" + Guid.NewGuid().ToString("N") + ".tmp");   // ".tmp": never moved or archived
            bool stagingIsOurs = false;
            try
            {
                File.Copy(src, staging, false);
                stagingIsOurs = true;
                File.SetLastWriteTimeUtc(staging, File.GetLastWriteTimeUtc(src));   // age decides rotation and the newer-wins rule
                if (new FileInfo(src).Length != new FileInfo(staging).Length || Sha256(src) != Sha256(staging))
                {
                    File.Delete(staging);
                    return false;
                }
                File.Move(staging, dest);    // no-overwrite: a destination another writer made meanwhile fails here and stays as it is
                stagingIsOurs = false;       // published: the staging name no longer exists
                File.Delete(src);
                return true;
            }
            catch (Exception)
            {
                if (stagingIsOurs) { try { File.Delete(staging); } catch { } }
                return false;
            }
        }

        static bool SameVolume(string a, string b)
        {
            try { return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
            {
                var hash = sha.ComputeHash(fs);
                var sb = new System.Text.StringBuilder(hash.Length * 2);
                foreach (var x in hash) sb.Append(x.ToString("x2"));
                return sb.ToString();
            }
        }

        static string Unique(string path)
        {
            var candidate = path;
            for (int n = 2; File.Exists(candidate); n++) candidate = path + "-" + n;
            return candidate;
        }

        /// <summary>The folder exists and a file can be written in it. Remembered once true.</summary>
        static bool Ensure(string dir)
        {
            lock (Gate) { if (Created.Contains(dir)) return true; }
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".truedat-work-probe-" + Guid.NewGuid().ToString("N"));
                File.WriteAllBytes(probe, new byte[] { 0 });
                File.Delete(probe);
            }
            catch (Exception) { return false; }
            lock (Gate) { Created.Add(dir); }
            return true;
        }

        /// <summary>A folder's full path without a trailing separator — except a drive root, which keeps its own (review #6): "C:" is
        /// not the root of C but the current directory OF drive C, so a trimmed root sent the move and every path built on it into
        /// whatever folder the process happened to stand in.</summary>
        static string Norm(string path)
        {
            if (string.IsNullOrEmpty(path)) path = ".";   // GetDirectoryName of a bare relative name: the current directory
            var seps = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
            string full;
            try { full = Path.GetFullPath(path); }
            catch { return (path ?? "").TrimEnd(seps); }
            var trimmed = full.TrimEnd(seps);
            string? root = null;
            try { root = Path.GetPathRoot(full); } catch { }
            // "C:\" would trim to "C:": keep the root. A UNC root ("\\server\share") carries no trailing separator and is whole as it is.
            if (!string.IsNullOrEmpty(root) && root!.Length > 0 && Array.IndexOf(seps, root[root.Length - 1]) >= 0
                && string.Equals(trimmed, root.TrimEnd(seps), StringComparison.OrdinalIgnoreCase))
                return root;
            return trimmed;
        }

        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // ---- identity (review #14) --------------------------------------------------------------------------

        /// <summary>The same folder or file, whatever it is called: equal names, or the same volume serial and file index (a junction,
        /// a subst drive, a mapped name and its target). Strings alone took a junction and its target for two folders, so the move
        /// collided every working file with itself and renamed the only copy aside.</summary>
        internal static bool SameEntry(string a, string b)
        {
            if (Same(Norm(a), Norm(b))) return true;
            return TryIdentity(a, out var x) && TryIdentity(b, out var y) && x.Volume == y.Volume && x.Index == y.Index;
        }

        /// <summary>Volume serial number and file index of a file or folder (the target's, through a junction). False when it cannot be
        /// opened — it does not exist, or the volume does not report one — and the caller then has only the name to go on.</summary>
        internal static bool TryIdentity(string path, out (uint Volume, ulong Index) id)
        {
            id = default;
            try
            {
                using var h = CreateFileW(Path.GetFullPath(path), 0, 0x7 /* share read, write, delete */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
                                          0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS: folders open too */, IntPtr.Zero);
                if (h.IsInvalid) return false;
                if (!GetFileInformationByHandle(h, out var info)) return false;
                if (info.VolumeSerialNumber == 0 && info.FileIndexHigh == 0 && info.FileIndexLow == 0) return false;   // nothing to compare
                id = (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
                return true;
            }
            catch (Exception) { return false; }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, out ByHandleFileInformation info);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }
    }
}
