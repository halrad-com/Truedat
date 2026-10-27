using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Truedat.Store
{
    /// <summary>
    /// store-v3 T18: a scan with a store in force is STORE-AUTHORITATIVE. It loads its entries from the store (T18i), records every catalog
    /// change into the store (fresh analyses, cache-hit refreshes, moved files' retirements), commits at each checkpoint and at the end, and
    /// publishes the catalog + sidecar + edges companion DERIVED from the store (T18a-d) — no JSON-first sink. Without a store the scan is
    /// unchanged.
    /// </summary>
    internal static class StoreScan
    {
        internal sealed class LoadResult
        {
            internal bool Refused; internal string? Refusal;
            internal int Loaded; internal long Generation; internal string? RootPath;
            /// <summary>Which row of the precondition table applied (TDA 106 / keeper 129).</summary>
            internal string Case = "";
            internal string? Note;
        }

        /// <summary>How many paths a refusal names inline before "and N more". Three, not ten: a refusal has to be READ, and the
        /// operator's whole reason for not acting on one is that the action was lost in the path list (2026-10-26). A disagreement
        /// that can run to tens of thousands of paths does not inline them at all — see <see cref="WriteDisagreeLedger"/>.</summary>
        const int NameAtMost = 3;

        /// <summary>The ledger a path-set disagreement writes beside the catalog, so the refusal can name a file instead of
        /// printing the paths. Same convention as mbxmoods-skipped.csv / mbxmoods-errors.csv.</summary>
        internal const string DisagreeLedgerName = "mbxmoods-store-disagree.csv";

        /// <summary>Whether <c>--store-import</c> can change the outcome of a disagreement. It can only ADD what the store is
        /// missing — it never overwrites a stored measurement (both value-adopting paths in <see cref="ImportCommand"/> are gated
        /// on <c>!isImport</c>: an import is a seed, not a measurement, so it carries no authority over one). So when nothing is
        /// only in the catalog, an import provably cannot move anything and must NOT be named as the remedy. Naming it anyway is a
        /// closed loop: the operator runs it, it succeeds, the same refusal comes back. Field-reported 2026-10-26 after a
        /// 72,447-track library spent three scans in it; the 2026-10-18 pass fixed the false-positive half of this refusal
        /// (<see cref="HoldsSameFacts"/>) and left the wrong-remedy half, which this closes.</summary>
        internal static bool ImportCanResolve(int onlyCatalogCount) => onlyCatalogCount > 0;

        static string LedgerPath(string catalogPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(catalogPath))!, DisagreeLedgerName);

        /// <summary>Write the disagreeing paths beside the catalog and return the file's FULL path for the refusal to cite (the
        /// operator is in the exe's folder, not the catalog's), or null when it could not be written — the refusal then names the
        /// paths inline, so it is never left with none.</summary>
        static string? WriteDisagreeLedger(string catalogPath, List<string> onlyCatalog, List<string> onlyStore, List<string>? changed)
        {
            try
            {
                var path = LedgerPath(catalogPath);
                using var w = new StreamWriter(path, false);
                w.WriteLine("side,path");
                foreach (var p in onlyCatalog) w.WriteLine($"only-in-catalog,{Program.CsvEscape(p)}");
                foreach (var p in onlyStore) w.WriteLine($"only-in-store,{Program.CsvEscape(p)}");
                if (changed != null) foreach (var p in changed) w.WriteLine($"different-values,{Program.CsvEscape(p)}");
                return path;
            }
            catch { return null; }
        }

        /// <summary>The last refusal's ledger, once the two agree again: left behind it would describe a refusal that no longer applies.</summary>
        static void ClearDisagreeLedger(string catalogPath)
        {
            try { var p = LedgerPath(catalogPath); if (File.Exists(p)) File.Delete(p); } catch { }
        }

        /// <summary>
        /// T18i: fill <paramref name="target"/> from the store, after the provenance precondition (TDA 106 table, keeper 129):
        /// <list type="bullet">
        /// <item>no store yet: no catalog = a fresh start; a catalog with entries = REFUSE naming <c>--store-import</c> (the store would
        /// hold only what this scan analyses — ISSUES I3, keeper 116 Q1: explicit import only);</item>
        /// <item>catalog ABSENT (the triple deleted): the store is authoritative — load it; the scan re-derives the files with zero
        /// re-analysis;</item>
        /// <item>catalog with <c>origin</c>, generation EQUAL to the store's: the two must agree on the root's path set — any difference is a
        /// defect and refuses, naming it;</item>
        /// <item>catalog with <c>origin</c>, generation BEHIND the store's: the store is legitimately ahead (a committed mutation whose
        /// publication did not land, T07) — the store wins and the scan republishes; the difference is reported, not refused;</item>
        /// <item>catalog with <c>origin</c>, generation AHEAD of the store's: the catalog came from another store state — refuse;</item>
        /// <item>catalog WITHOUT <c>origin</c> (a scan before T18, or a catalog-only verb run while the store was parked): ambiguous. If the
        /// path sets differ in either direction, or any shared path's entry differs (a re-analysis that reached the catalog but not the
        /// store), refuse naming the paths and the remedies; if they agree exactly, proceed.</item>
        /// </list>
        /// The root is the one the catalog's origin names (exactly one matching row, store-intent item 8), else the store's single primary.
        /// <paramref name="writeLedger"/>: only a SCAN (or a store-backed verb) writes <see cref="DisagreeLedgerName"/> — off by default,
        /// so a read verb asking the same question (--store-info) writes nothing (operator 2026-10-26: "danger").
        /// </summary>
        internal static LoadResult LoadAuthoritative(string storePath, string catalogPath, ConcurrentDictionary<string, TrackEntry> target, bool writeLedger = false)
        {
            var r = LoadAuthoritativeCore(storePath, catalogPath, target, writeLedger);
            if (writeLedger && !r.Refused) ClearDisagreeLedger(catalogPath);
            return r;
        }

        static LoadResult LoadAuthoritativeCore(string storePath, string catalogPath, ConcurrentDictionary<string, TrackEntry> target, bool writeLedger)
        {
            var res = new LoadResult();
            bool catalogExists = global::Truedat.CatalogArchive.CatalogFiles(catalogPath).Count > 0;
            var catalog = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
            if (catalogExists) Program.LoadExistingMoodsForStore(catalogPath, catalog);
            CatalogOrigin? origin = null;
            if (catalogExists && CatalogOrigin.TryRead(catalogPath, out var o)) origin = o;

            if (!File.Exists(storePath))
            {
                if (catalog.Count == 0) { res.Case = "fresh"; return res; }
                return Refuse(res, "no-store-yet",
                    $"the store {Path.GetFullPath(storePath)} does not exist yet, but the catalog {Path.GetFullPath(catalogPath)} holds {catalog.Count:N0} entries: " +
                    $"a store-authoritative scan would publish only what it analyses and drop the rest. Seed the store from the catalog first: " +
                    $"truedat --store-import --moods \"{catalogPath}\" --store \"{storePath}\" (or run without the store).");
            }

            using var os = OpenStore.Open(storePath, create: false, VersionInfo.Display, DateTime.UtcNow.ToString("o"));
            var storeEntries = new Dictionary<string, TrackEntry>(PathComparer.Instance);
            var mergedCopies = new HashSet<string>(PathComparer.Instance);
            using (var snapshot = os.Db.BeginRead())
            {
                res.Generation = os.Dal.Generation();
                var roots = os.Dal.ListRoots();
                // The scan works on the LIBRARY — every entry, wherever its file is (StoreDal.LibraryRoots). Loading only the root a
                // catalog names made every track under the library's other roots look new: re-analysed on every scan, and left out of
                // the catalog again (MTest, 2026-10-18: the 12.5-hour file analysed a second time).
                var load = os.Dal.LibraryRoots();
                if (origin?.RootPath != null)
                {
                    bool Same(string a, string b) => string.Equals(a.Replace('/', '\\').TrimEnd('\\'), b.Replace('/', '\\').TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                    if (origin.RootRole == "primary")
                    {
                        // A catalog naming a root must name one of THIS library's: a root it has, or one since folded into a root that
                        // contains it (StoreDal.FoldNestedRoots). Anything else came from another store.
                        if (!load.Any(x => Same(x.Path, origin.RootPath) || CanonicalPath.Contains(x.Path, origin.RootPath)))
                            return Refuse(res, "root-unresolved",
                                $"the catalog names root {origin.RootPath} and the store {Path.GetFullPath(storePath)} holds no such library root " +
                                $"({string.Join(", ", load.Select(x => x.Path))}): the catalog came from another store. Move it aside and scan (the store publishes the whole library), seed this store from it with --store-import if it is the catalog you mean, or run without the store.");
                    }
                    else
                    {
                        // A catalog exported from one non-primary root (--store-export --export-root <mirror>): exactly that root, as before.
                        var match = roots.Where(x => string.Equals(x.Path, origin.RootPath, StringComparison.Ordinal) && string.Equals(x.Role, origin.RootRole, StringComparison.Ordinal)).ToList();
                        if (match.Count != 1)
                            return Refuse(res, "root-unresolved",
                                $"the catalog names root {origin.RootPath} ({origin.RootRole}) and the store {Path.GetFullPath(storePath)} has {match.Count} rows matching it; " +
                                "the scan cannot tell which root it is publishing. Move the catalog aside and scan (the store publishes the whole library), or run without the store.");
                        load = match;
                    }
                }
                if (load.Count > 0)
                {
                    res.RootPath = load.Count == 1 ? load[0].Path : null;
                    foreach (var (display, entry) in os.Dal.ReadEntries(load.Select(x => x.Id))) storeEntries[display] = entry;
                    mergedCopies = os.Dal.NonPrimaryCopyPaths(load.Select(x => x.Id));
                }
            }

            if (!catalogExists)
            {
                res.Case = "rebuild";
                res.Note = $"no catalog beside the store: the store is authoritative ({storeEntries.Count:N0} entries, generation {res.Generation}); the scan re-derives the files";
                return Fill(res, storeEntries, target);
            }

            var onlyCatalog = catalog.Keys.Where(k => !storeEntries.ContainsKey(k)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            var onlyStore = storeEntries.Keys.Where(k => !catalog.ContainsKey(k)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

            if (origin != null)
            {
                if (origin.Generation == res.Generation)
                {
                    if (onlyCatalog.Count > 0 || onlyStore.Count > 0)
                        return Refuse(res, "equal-generation-disagree", Disagreement(
                            $"the catalog and the store are both at generation {res.Generation} but disagree",
                            catalogPath, storePath, onlyCatalog, onlyStore, null, writeLedger));
                    res.Case = "equal-generation";
                    return Fill(res, storeEntries, target);
                }
                if (origin.Generation < res.Generation)
                {
                    res.Case = "store-ahead";
                    res.Note = $"the store is ahead of the files (store generation {res.Generation}, files {origin.Generation}): the store wins and the scan republishes" +
                               (onlyCatalog.Count + onlyStore.Count > 0 ? $" ({onlyCatalog.Count:N0} paths only in the old catalog, {onlyStore.Count:N0} only in the store)" : "");
                    return Fill(res, storeEntries, target);
                }
                return Refuse(res, "catalog-ahead",
                    $"the catalog says generation {origin.Generation} but the store {Path.GetFullPath(storePath)} is at {res.Generation}: the catalog came from another store state. " +
                    $"Move it aside and scan (the store publishes the whole library), or seed this store from it — truedat --store-import --moods \"{catalogPath}\" --store \"{storePath}\" — " +
                    "and scan again (the store then supersedes the catalog and republishes it). Nothing was scanned.");
            }

            // No origin: a catalog written before T18, or by a catalog-only verb while the store was parked.
            if (storeEntries.Count == 0 && catalog.Count > 0)
                return Refuse(res, "no-origin-store-empty",
                    $"the store {Path.GetFullPath(storePath)} holds no entries for this library, but the catalog holds {catalog.Count:N0}: " +
                    $"a store-authoritative scan would publish only what it analyses. Seed the store from the catalog first: truedat --store-import --moods \"{catalogPath}\" --store \"{storePath}\".");
            var changed = new List<string>();
            foreach (var kv in catalog)
                if (storeEntries.TryGetValue(kv.Key, out var se) && !HoldsSameFacts(kv.Key, kv.Value, se, mergedCopies.Contains(kv.Key)))
                    changed.Add(kv.Key);
            changed.Sort(StringComparer.OrdinalIgnoreCase);
            if (onlyCatalog.Count > 0 || onlyStore.Count > 0 || changed.Count > 0)
                return Refuse(res, "no-origin-disagree", Disagreement(
                    "the catalog and the store disagree", catalogPath, storePath, onlyCatalog, onlyStore, changed, writeLedger));
            res.Case = "no-origin-agree";
            return Fill(res, storeEntries, target);
        }

        /// <summary>A path-set / value disagreement: the headline with its counts, one line per action that resolves it, and where the
        /// paths are. Operator 2026-10-26: "less is more if something needs to be done be sure its correct and say how to fix it".
        /// Every path is FULL — the operator runs truedat from the exe's folder, and a network library's store is in %LOCALAPPDATA%.
        /// Keeping the catalog means a fresh store, and the store is off by default, so that line says to scan WITH --store; a fresh
        /// store is seeded from the catalog, which carries no edges. An import is named only when it can change the outcome
        /// (<see cref="ImportCanResolve"/>); whatever it cannot change, the next scan's refusal names.</summary>
        static string Disagreement(string headline, string catalogPath, string storePath,
            List<string> onlyCatalog, List<string> onlyStore, List<string>? changed, bool writeLedger)
        {
            var nl = Environment.NewLine;
            int values = changed?.Count ?? 0;
            var counts = new List<string>();
            if (values > 0) counts.Add(values == 1 ? "1 value differs" : $"{values:N0} values differ");
            if (onlyCatalog.Count > 0) counts.Add($"{onlyCatalog.Count:N0} only in the catalog");
            if (onlyStore.Count > 0) counts.Add($"{onlyStore.Count:N0} only in the store");
            var cat = Path.GetFullPath(catalogPath);
            var db = Path.GetFullPath(storePath);
            var sb = new System.Text.StringBuilder($"{headline} ({string.Join(", ", counts)}). Nothing was scanned.");
            if (ImportCanResolve(onlyCatalog.Count))
                sb.Append($"{nl}  Fix: truedat --store-import --moods \"{cat}\" --store \"{db}\", then scan.");
            else
            {
                var parts = global::Truedat.CatalogArchive.CatalogFiles(catalogPath);
                var renameCatalog = parts.Count > 1
                    ? $"move {Path.GetFileName(catalogPath)}.1 to .{parts.Count} in {Path.GetDirectoryName(cat)} aside"
                    : $"ren \"{cat}\" \"{Path.GetFileName(cat)}.bak\"";
                sb.Append($"{nl}  Keep the store's values:   {renameCatalog}, then scan.");
                sb.Append($"{nl}  Keep the catalog's values: ren \"{db}\" \"{Path.GetFileName(db)}.bak\", then scan with --store \"{db}\" (edges are not carried over).");
            }
            var ledger = writeLedger ? WriteDisagreeLedger(catalogPath, onlyCatalog, onlyStore, changed) : null;
            if (ledger != null) sb.Append($"{nl}  Detail: {ledger}");
            else
            {
                var named = new List<string>();
                if (onlyCatalog.Count > 0) named.Add("only in the catalog" + Names(onlyCatalog));
                if (onlyStore.Count > 0) named.Add("only in the store" + Names(onlyStore));
                if (values > 0) named.Add("values differ" + Names(changed!));
                sb.Append($"{nl}  Paths: {string.Join("; ", named)}");
            }
            return sb.ToString();
        }

        /// <summary>Whether a catalog entry with no store origin and the store's entry for the same path hold the same facts — the
        /// question the no-origin precondition asks, which is not "same bytes". An import makes two differences by design, and the
        /// first real library hit both (MB3, 2026-10-18: 140 of 7,641 refused, with --store-import named as the remedy for a gap it
        /// had just made): the store holds each field at its declared precision, so an older writer's one-ulp spelling comes back
        /// as the decimal it stood for; and a non-primary copy of duplicate audio comes back carrying its media's one description
        /// (the excuse --store-roundtrip already makes, gate d). Anything else — a value beyond the precision, a copy fact, the
        /// location — is still a difference.</summary>
        internal static bool HoldsSameFacts(string path, TrackEntry catalogEntry, TrackEntry storeEntry, bool nonPrimaryCopy)
            => ExportCommand.Classify(path, catalogEntry, storeEntry, nonPrimaryCopy, out _) != ExportCommand.Agreement.Different;

        static LoadResult Fill(LoadResult res, Dictionary<string, TrackEntry> entries, ConcurrentDictionary<string, TrackEntry> target)
        {
            foreach (var kv in entries) target[kv.Key] = kv.Value;
            res.Loaded = entries.Count;
            StoreLog.Info($"scan loads from the store: {res.Loaded} entries, generation {res.Generation}, root {res.RootPath ?? "(none yet)"} [{res.Case}]");
            return res;
        }

        static LoadResult Refuse(LoadResult res, string why, string sentence)
        {
            res.Refused = true; res.Case = why; res.Refusal = "Error: " + sentence;
            return res;
        }

        /// <summary>The remedy for files behind the store, in every message that names one: a SCAN. Each scan publishes the catalog,
        /// sidecar and edges companion from the store at its checkpoints and at the end (<see cref="Publish"/>), analysed entries or
        /// not — so no message recommends <c>--store-export</c> or <c>--edges-export</c> (operator, 2026-10-20: "it happens
        /// automatically"). The verbs remain for a deliberate export elsewhere.</summary>
        internal const string ScanPublishes = "the next scan publishes them from the store";
        /// <summary>The same remedy as a command line, for <c>--store-info</c>'s <c>next:</c> line and its JSON.</summary>
        internal const string ScanPublishesCommand = "truedat   (a scan publishes the catalog, sidecar and edges file from the store)";

        static string Names(List<string> paths)
            => paths.Count == 0 ? "" : ": " + string.Join(", ", paths.Take(NameAtMost)) + (paths.Count > NameAtMost ? $", and {paths.Count - NameAtMost:N0} more" : "");

        /// <summary>T18a-d: publish the catalog + sidecar + edges companion DERIVED from the store (the --store-export publisher: one read
        /// snapshot, generation-stamped, the ruled rename order). Returns null on success, else the failure (the store is unaffected: a
        /// publication failure leaves the files behind the store, which T07 accepts and the next publish repairs).</summary>
        /// <summary>Config <c>publish false</c> / <c>--no-publish</c> (operator 2026-10-25: "i just want the store, i dont want to keep the
        /// legacy mood files around"): a scan, a store-backed verb and a restore write the STORE only — no catalog, sidecar or
        /// mbxedges.v1.json. Off by default (false), so nothing changes until it is set. Applies only with a store in force; a scan
        /// without one still writes its catalog (it would otherwise keep nothing). --store-export / --edges-export are deliberate
        /// exports and ignore it.</summary>
        internal static bool PublishOff;

        /// <summary>Config + command line -> <see cref="PublishOff"/>: a typed <c>--no-publish</c> always wins; a typed <c>--publish</c> beats a
        /// config <c>publish false</c>; otherwise the config decides.</summary>
        internal static bool ResolvePublishOff(bool noPublishTyped, bool publishTyped, bool configSaysOff)
            => noPublishTyped || (configSaysOff && !publishTyped);

        /// <summary>Remove the published files beside <paramref name="catalogPath"/> (the catalog or its parts, the sidecar, the companion).
        /// <paramref name="archive"/> true keeps them first as ONE zip, <c>&lt;catalog&gt;.retired.&lt;stamp&gt;.zip</c> (each file under its own
        /// name; never matched by backup rotation, restorable with --restore): the files a scan finds when publication was switched
        /// off are never silently deleted. False is for files this run wrote itself (a store-backed verb's working copy). Returns the
        /// zip's path, or null when nothing was there (or nothing was archived).</summary>
        internal static string? RetirePublishedFiles(string catalogPath, bool archive)
        {
            var files = global::Truedat.CatalogArchive.CatalogFiles(catalogPath);
            var sidecar = Path.ChangeExtension(catalogPath, ".mbxs");
            var companion = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(catalogPath)) ?? ".", global::Truedat.Edges.EdgeCompanion.FileName);
            if (File.Exists(sidecar)) files.Add(sidecar);
            if (File.Exists(companion)) files.Add(companion);
            if (files.Count == 0) return null;
            string? zip = null;
            if (archive)
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd.HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                zip = $"{catalogPath}.retired.{stamp}.zip";
                for (int n = 2; File.Exists(zip); n++) zip = $"{catalogPath}.retired.{stamp}-{n}.zip";
                global::Truedat.CatalogArchive.WriteZipBackup(files.Select(f => (f, Path.GetFileName(f))).ToList(), zip);
            }
            foreach (var f in files) File.Delete(f);
            return zip;
        }

        internal static Exception? Publish(string storePath, string catalogPath, out ExportReport report, bool force = false)
        {
            report = new ExportReport();
            if (PublishOff && !force)
            {
                // Publication off: the store is the only output. Files left from before are kept once in a zip and removed, so a later
                // scan's start-of-scan check never compares the store with a catalog nothing keeps current (T18i reads "rebuild").
                report.PublicationOff = true;
                try
                {
                    report.RetiredArchive = RetirePublishedFiles(catalogPath, archive: true);
                    if (report.RetiredArchive != null) StoreLog.Info($"publication off: the files beside the store were kept in {report.RetiredArchive} and removed");
                    return null;
                }
                catch (Exception ex) { return ex; }
            }
            // The WHOLE library, always (StoreDal.LibraryRoots): a scan or a verb publishes every entry it holds, wherever the files are.
            // Passing the root the scan loaded made it publish one drive's tracks — or refuse, with the library under two roots.
            try { ExportCommand.Execute(new ExportOptions { StorePath = storePath, OutPath = catalogPath }, report); return null; }
            catch (Exception ex) { return ex; }
        }
    }
}
