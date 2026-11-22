using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Truedat
{
    /// <summary>
    /// truedat's WORKING files — the error and skip lists, scan summaries, the audit log, catalog and store backups,
    /// snapshots, clear and reset archives, the reports a verb emits — live beside the catalog, as they always did.
    ///
    /// For one release (0.5.5.4-EV6.2) they were moved to a folder derived from the exe. That was removed (operator
    /// 2026-11-22: "if you have multiple libraries the files dont make sense in 1 location"). What is left here is the move
    /// BACK: a writing run takes the working files out of the folder that release used for this library and puts them beside
    /// the catalog again (<see cref="MoveBack"/>), and a reading run, which moves nothing, still finds them there
    /// (<see cref="Read"/>, <see cref="Folders"/>), so an install that has not scanned since answers as it did.
    ///
    /// The folder that release used is found from what it left behind, never guessed: the exe's own folder when truedat is
    /// installed in the library (<c>Library\truedat\truedat.exe</c>), <c>&lt;exe&gt;\libraries\&lt;library id&gt;</c>
    /// otherwise, and whatever its locator file beside the catalog names. A folder whose owner marker names ANOTHER library is
    /// never read or emptied for this one.
    /// </summary>
    internal static class WorkFolder
    {
        /// <summary>The suffix a file takes when it cannot be the live one at its name (both folders held one).</summary>
        internal const string MovedSuffix = ".moved-";
        /// <summary>The dedupe verdicts sidecar (restfulbee's intake-dedupe spec): written by a person or by
        /// <c>tools/init-dedupe-verdicts.ps1</c>, never by the exe.</summary>
        internal const string DedupeVerdictsFileName = "mbxmoods-duplicates.verdicts.json";
        /// <summary>Every file <c>--duplicates</c> writes, and the operator's verdicts keyed against them, start with this.</summary>
        internal const string DuplicatesPrefix = "mbxmoods-duplicate";

        // What the EV6.2 build left behind, read to find its folder.
        internal const string LegacyLibrariesDir = "libraries";
        internal const string LegacyLocatorFileName = "truedat-work-folder.json";
        internal const string LegacyOwnerMarkerName = ".truedat-work-owner.json";

        /// <summary>Set by a WRITING run: called the first time a working-file path is asked for a catalog folder this run has
        /// not settled, so the files the EV6.2 build moved away come back before anything reads or writes one. Null in a reading
        /// run — which is what keeps a reading run from moving anything.</summary>
        internal static Action<string>? BeginOnFirstUse;

        static string _exeDir = AppDomain.CurrentDomain.BaseDirectory;
        static readonly object Gate = new object();
        static readonly HashSet<string> Settled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> Beginning = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>The folders this run moved files back from, per catalog folder: a store keys the ledger rows it imported on the
        /// ledger's PATH, so <see cref="OtherPlace"/> still answers with the old path after the folder has been emptied.</summary>
        static readonly Dictionary<string, List<string>> MovedFrom = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Files a run was HANDED (a --restore's archive): never moved, whatever they are called (review #8).</summary>
        static readonly HashSet<string> Kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The move leaves this file where it is. For a run's own input, resolved to a full path before the folder is
        /// settled, so a --restore handed an archive in the old folder can still open the path it was given (review #8).</summary>
        internal static void Keep(string path)
        {
            try { var full = Path.GetFullPath(path); lock (Gate) Kept.Add(full); } catch { }
        }

        /// <summary>The catalog folder of the library this run settled; null before that. This is the value <c>store-result.json</c>
        /// reports as <c>workFolder</c> (hub contract, kept so a reader of the field keeps working): the catalog folder.</summary>
        internal static string? Current { get; private set; }

        /// <summary>The exe's folder (where the EV6.2 build put working files). Clears everything an earlier configuration settled,
        /// so one process can serve several (the self-tests).</summary>
        internal static void Configure(string exeDir)
        {
            lock (Gate)
            {
                _exeDir = string.IsNullOrWhiteSpace(exeDir) ? AppDomain.CurrentDomain.BaseDirectory : exeDir;
                Settled.Clear(); Beginning.Clear(); MovedFrom.Clear(); Kept.Clear();
                Current = null;
            }
        }

        /// <summary>Where a library's working files go: beside its catalog.</summary>
        internal static string For(string catalogDir) => Norm(catalogDir);

        /// <summary>True once this run has settled <paramref name="catalogDir"/>.</summary>
        internal static bool IsSettled(string catalogDir)
        {
            var cat = Norm(catalogDir);
            lock (Gate) return Settled.Contains(cat);
        }

        /// <summary>A WRITING run settles the library it works on. The move back is <see cref="MoveBack"/>, called by the run.</summary>
        internal static string Begin(string catalogDir)
        {
            var cat = Norm(catalogDir);
            lock (Gate) { Settled.Add(cat); Current = cat; }
            return cat;
        }

        /// <summary>A writing run's first use of a catalog folder: settle it through <see cref="BeginOnFirstUse"/> before the path
        /// is handed out. Re-entry for the same folder (the begin itself asking for paths) is a no-op.</summary>
        static void FirstUse(string cat)
        {
            var hook = BeginOnFirstUse;
            if (hook == null) return;
            lock (Gate)
            {
                if (Settled.Contains(cat) || !Beginning.Add(cat)) return;
            }
            try { hook(cat); }
            finally { lock (Gate) Beginning.Remove(cat); }
        }

        /// <summary>Where a working file is WRITTEN: beside the catalog.</summary>
        internal static string Write(string catalogDir, string name)
        {
            var cat = Norm(catalogDir);
            FirstUse(cat);
            return Path.Combine(cat, name);
        }

        /// <summary>Where a working file is READ (or appended to): beside the catalog; in the old folder when only it holds the file
        /// (a reading run on an install that has not scanned since, or a file another program held when the move ran).</summary>
        internal static string Read(string catalogDir, string name)
        {
            var cat = Norm(catalogDir);
            FirstUse(cat);
            var here = Path.Combine(cat, name);
            try { if (File.Exists(here)) return here; } catch { return here; }
            foreach (var old in LegacyFolders(cat))
            {
                var there = Path.Combine(old, name);
                try { if (File.Exists(there)) return there; } catch { }
            }
            return here;
        }

        /// <summary>The folders this library's truedat files can be in — the catalog folder first, then any old folder still holding
        /// some. The size block, the reset's file set and every rotation read all of them.</summary>
        internal static List<string> Folders(string catalogDir)
        {
            var cat = Norm(catalogDir);
            FirstUse(cat);
            var list = new List<string> { cat };
            foreach (var old in LegacyFolders(cat)) if (!list.Contains(old, StringComparer.OrdinalIgnoreCase)) list.Add(old);
            return list;
        }

        /// <summary>Files matching <paramref name="pattern"/> across <see cref="Folders"/>, the catalog folder's copy winning a name
        /// held in both (never counted twice).</summary>
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

        /// <summary>The same name in the other place: beside the catalog for a path in an old folder, in the old folder for a path
        /// beside the catalog; null when there is no old folder. For the dedupe keys a store holds by file path, so a ledger that
        /// moved is still recognised as the one already imported.</summary>
        internal static string? OtherPlace(string catalogDir, string path)
        {
            var cat = Norm(catalogDir);
            var name = Path.GetFileName(path);
            string here;
            try { here = Norm(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "."); } catch { return null; }
            var olds = new List<string>(LegacyFolders(cat));
            lock (Gate) { if (MovedFrom.TryGetValue(cat, out var gone)) foreach (var g in gone) if (!olds.Contains(g, StringComparer.OrdinalIgnoreCase)) olds.Add(g); }
            if (olds.Count == 0) return null;
            if (Same(here, cat)) return Path.Combine(olds[0], name);
            if (olds.Any(o => Same(o, here))) return Path.Combine(cat, name);
            return null;
        }

        /// <summary>Where a backup of a STORE goes: beside the store.</summary>
        internal static string BackupPath(string filePath, string backupName)
        {
            var dir = Norm(Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".");
            return Path.Combine(dir, backupName);
        }

        /// <summary>The folders a backup named after <paramref name="filePath"/> can be in, for rotation: the file's own folder, then
        /// any old folder of the library there (backups the EV6.2 build wrote).</summary>
        internal static List<string> BackupFolders(string filePath)
        {
            var dir = Norm(Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".");
            var list = new List<string> { dir };
            foreach (var old in LegacyFolders(dir)) if (!list.Contains(old, StringComparer.OrdinalIgnoreCase)) list.Add(old);
            return list;
        }

        // ---- the folder the EV6.2 build used --------------------------------------------------------------

        /// <summary>The folders the EV6.2 build may have moved this library's working files into, that exist and still hold one.
        /// Found from what that build left, never guessed: its locator beside the catalog, the exe's folder when the exe sits in the
        /// catalog folder's child, and <c>&lt;exe&gt;\libraries\&lt;library id&gt;</c>. A folder whose owner marker names another
        /// library is not this library's and is never returned. Creates nothing.</summary>
        internal static List<string> LegacyFolders(string catalogDir)
        {
            var cat = Norm(catalogDir);
            var found = new List<string>();
            foreach (var cand in LegacyCandidates(cat))
            {
                try
                {
                    if (found.Contains(cand, StringComparer.OrdinalIgnoreCase) || Same(cand, cat) || !Directory.Exists(cand)) continue;
                    if (SameEntry(cand, cat)) continue;                 // the catalog folder under another name (a junction, a subst drive)
                    if (OwnedByAnother(cand, cat)) continue;
                    if (!HoldsWorkingFiles(cand)) continue;
                    found.Add(cand);
                }
                catch { }
            }
            return found;
        }

        static IEnumerable<string> LegacyCandidates(string cat)
        {
            string exe;
            try { exe = Norm(_exeDir); } catch { yield break; }
            var located = ReadLocator(Path.Combine(cat, LegacyLocatorFileName), cat);
            if (located != null) yield return located;
            var parent = Path.GetDirectoryName(exe);
            if (!string.IsNullOrEmpty(parent) && Same(Norm(parent!), cat)) yield return exe;
            string? byId = null;
            try { byId = Norm(Path.Combine(exe, LegacyLibrariesDir, global::Truedat.Store.StoreLocation.LibraryId(cat))); } catch { }
            if (byId != null) yield return byId;
        }

        /// <summary>The work folder a locator names for this catalog folder, or null (no locator, unreadable, or another library's).</summary>
        static string? ReadLocator(string path, string cat)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var d = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
                if (d?["kind"]?.GetValue<string>() != "truedat-work-folder") return null;
                var owner = d?["catalogFolder"]?.GetValue<string>();
                var work = d?["workFolder"]?.GetValue<string>();
                if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(work) || !SameEntry(owner!, cat)) return null;
                return Norm(work!);
            }
            catch { return null; }
        }

        /// <summary>The folder carries an owner marker that names a different catalog folder (or cannot be read): not this library's.</summary>
        static bool OwnedByAnother(string folder, string cat)
        {
            var marker = Path.Combine(folder, LegacyOwnerMarkerName);
            if (!File.Exists(marker)) return false;
            try
            {
                var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(marker));
                var owner = doc?["catalogFolder"]?.GetValue<string>();
                return owner == null || !SameEntry(owner, cat);
            }
            catch { return true; }
        }

        /// <summary>The folder holds at least one file the EV6.2 build would have moved there.</summary>
        static bool HoldsWorkingFiles(string folder)
        {
            foreach (var f in Directory.GetFiles(folder))
                if (IsWorkingFile(Path.GetFileName(f), "mbxmoods.json", "mbxmoods.mbxs", null, global::Truedat.Store.StoreLocation.FileName)) return true;
            return false;
        }

        // ---- what moves -------------------------------------------------------------------------------------

        /// <summary>
        /// Is this file truedat wrote a WORKING file — one the EV6.2 build moved away, and the move back brings home?
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
            // A verify shard's coverage receipt (review #16): small, named per host and shard, and read by --verify-coverage on ANY
            // host — so it lives with the shared catalog.
            if (IsVerifyReceipt(name, catalogName)) return true;
            // What truedat writes for the hub to render: the review ledger and the preview page. Normally in the hub's
            // review folder; beside the catalog they are still the hub's. A --chunk shard's ledger carries the host suffix
            // (mbxmoods-review.<host>.json) and --merge-moods folds it from beside the shard's catalog, so it stays too.
            if (Starts(Path.GetFileNameWithoutExtension(ReviewLedger.FileName) + ".") || Starts("mbxmoods-preview.")) return true;
            return false;
        }

        /// <summary><c>&lt;catalog stem&gt;-verify*.receipt.json</c> — a verify shard's coverage receipt.</summary>
        internal static bool IsVerifyReceipt(string name, string catalogName)
        {
            var stem = Path.GetFileNameWithoutExtension(string.IsNullOrEmpty(catalogName) ? "mbxmoods.json" : catalogName);
            return name.StartsWith(stem + "-verify", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".receipt.json", StringComparison.OrdinalIgnoreCase);
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

        // ---- the move back --------------------------------------------------------------------------------

        /// <summary>What <see cref="MoveBack"/> did, for the one line a run prints.</summary>
        internal sealed class MoveReport
        {
            /// <summary>The folders files were moved back from.</summary>
            public List<string> From = new List<string>();
            /// <summary>Files moved back beside the catalog.</summary>
            public int Moved;
            /// <summary>Files that could not be moved (another program holds them). They stay, and are still read.</summary>
            public List<string> Held = new List<string>();
            /// <summary>Names held in BOTH places: the older copy was kept beside the live one as <c>&lt;name&gt;.moved-&lt;time&gt;</c>.</summary>
            public List<string> Renamed = new List<string>();
            /// <summary>Every name that moved, in the order it moved.</summary>
            public List<string> MovedNames = new List<string>();
            public bool Anything { get { return Moved > 0 || Held.Count > 0; } }
        }

        /// <summary>
        /// Put this library's working files back beside the catalog, from the folder the EV6.2 build moved them into — a WRITING
        /// run's first act, before it reads any of them, so the error list it consults and the backups it rotates are the ones that
        /// were already there. Nothing is ever written over: when both places hold a name, the NEWER file is the live one and the
        /// other is kept beside it as <c>&lt;name&gt;.moved-&lt;time&gt;</c>. A file another program holds stays where it is, is
        /// still read from there, and is reported. Once a folder holds none of this library's files its owner marker goes, and a
        /// <c>libraries\&lt;id&gt;</c> folder left empty is removed; the locator beside the catalog (and beside the store) goes once
        /// nothing is left. Never throws.
        /// </summary>
        internal static MoveReport MoveBack(string catalogPath, string? storePath)
        {
            var report = new MoveReport();
            string cat;
            try { cat = Norm(Path.GetDirectoryName(Path.GetFullPath(catalogPath)) ?? "."); }
            catch { return report; }
            if (!Directory.Exists(cat)) return report;

            string catalogName = Path.GetFileName(catalogPath);
            string sidecarName = Path.GetFileName(Path.ChangeExtension(catalogPath, ".mbxs"));
            string companion = global::Truedat.Edges.EdgeCompanion.FileName;
            string storeName = global::Truedat.Store.StoreLocation.FileName;
            if (!string.IsNullOrEmpty(storePath))
            {
                try { storeName = Path.GetFileName(storePath!); } catch { }
            }

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var folders = LegacyFolders(cat);
            foreach (var old in folders)
            {
                report.From.Add(old);
                lock (Gate)
                {
                    if (!MovedFrom.TryGetValue(cat, out var list)) MovedFrom[cat] = list = new List<string>();
                    if (!list.Contains(old, StringComparer.OrdinalIgnoreCase)) list.Add(old);
                }
                string[] names;
                try { names = Directory.GetFiles(old); } catch { continue; }
                Array.Sort(names, StringComparer.OrdinalIgnoreCase);
                bool left = false;
                foreach (var src in names)
                {
                    var name = Path.GetFileName(src);
                    if (!IsWorkingFile(name, catalogName, sidecarName, companion, storeName)) continue;
                    List<string> keptPaths; lock (Gate) keptPaths = Kept.ToList();
                    if (keptPaths.Any(k => SameEntry(k, src))) { left = true; continue; }   // a file this run was handed stays where it was given
                    var dest = Path.Combine(cat, name);
                    try
                    {
                        if (File.Exists(dest))
                        {
                            if (!CanTake(src)) { report.Held.Add(name); left = true; continue; }
                            bool incomingIsNewer = File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(dest);
                            var aside = Unique(Path.Combine(cat, name + MovedSuffix + stamp));
                            if (incomingIsNewer)
                            {
                                File.Move(dest, aside);
                                if (MoveOne(src, dest)) { report.Moved++; report.Renamed.Add(name); report.MovedNames.Add(name + " (the older copy beside the catalog kept as " + Path.GetFileName(aside) + ")"); }
                                else { File.Move(aside, dest); report.Held.Add(name); left = true; }
                            }
                            else if (MoveOne(src, aside)) { report.Moved++; report.Renamed.Add(name); report.MovedNames.Add(name + " (older than the copy beside the catalog, kept as " + Path.GetFileName(aside) + ")"); }
                            else { report.Held.Add(name); left = true; }
                        }
                        else if (MoveOne(src, dest)) { report.Moved++; report.MovedNames.Add(name); }
                        else { report.Held.Add(name); left = true; }
                    }
                    catch (Exception) { report.Held.Add(name); left = true; }
                }
                if (left) continue;
                // Nothing of this library's is left there: its marker goes, and a folder that existed only for it goes too.
                try { var marker = Path.Combine(old, LegacyOwnerMarkerName); if (File.Exists(marker)) File.Delete(marker); } catch { }
                try
                {
                    var exe = Norm(_exeDir);
                    var libs = Path.Combine(exe, LegacyLibrariesDir);
                    if (Same(Norm(Path.GetDirectoryName(old) ?? ""), Norm(libs)) && !Directory.EnumerateFileSystemEntries(old).Any())
                    {
                        Directory.Delete(old);
                        if (Directory.Exists(libs) && !Directory.EnumerateFileSystemEntries(libs).Any()) Directory.Delete(libs);
                    }
                }
                catch { }
            }

            if (report.Held.Count == 0)
            {
                // The locator only ever pointed at the folder this undoes. Beside the catalog, and beside a store kept elsewhere.
                var locators = new List<string> { Path.Combine(cat, LegacyLocatorFileName) };
                try { if (!string.IsNullOrEmpty(storePath)) locators.Add(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(storePath!)) ?? cat, LegacyLocatorFileName)); } catch { }
                foreach (var loc in locators)
                    try { if (File.Exists(loc) && ReadLocator(loc, cat) != null) File.Delete(loc); } catch { }
            }
            return report;
        }

        /// <summary>The one line a writing run prints about the move back; null when nothing was there to move.</summary>
        internal static string? Describe(MoveReport r)
        {
            if (!r.Anything) return null;
            var line = $"Working files: moved {r.Moved:N0} file{(r.Moved == 1 ? "" : "s")} back beside the catalog from {string.Join(", ", r.From)}";
            if (r.Renamed.Count > 0) line += $"; {r.Renamed.Count:N0} name{(r.Renamed.Count == 1 ? " was" : "s were")} in both places, the older copy kept as <name>{MovedSuffix}<time>";
            if (r.Held.Count > 0) line += $"; {r.Held.Count:N0} held by another program and left where they are, still read from there ({string.Join(", ", r.Held)})";
            return line;
        }

        /// <summary>The names that moved, one per line, under a heading that says where from and where to; empty when nothing moved.</summary>
        internal static List<string> MovedNames(MoveReport r, string catalogDir)
        {
            var lines = new List<string>();
            if (r.MovedNames.Count == 0) return lines;
            lines.Add($"Working files: moved back from {string.Join(", ", r.From)} into {catalogDir}:");
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
