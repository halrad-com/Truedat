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

        /// <summary>How many paths a refusal names before "and N more".</summary>
        const int NameAtMost = 10;

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
        /// </summary>
        internal static LoadResult LoadAuthoritative(string storePath, string catalogPath, ConcurrentDictionary<string, TrackEntry> target)
        {
            var res = new LoadResult();
            bool catalogExists = File.Exists(catalogPath) || File.Exists(catalogPath + ".1");
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
                var load = os.Dal.LibraryRoots(os.Machine.Id);
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
                                $"({string.Join(", ", load.Select(x => x.Path))}): the catalog came from another store. Re-publish with --store-export, or run without the store.");
                    }
                    else
                    {
                        // A catalog exported from one non-primary root (--store-export --export-root <mirror>): exactly that root, as before.
                        var match = roots.Where(x => string.Equals(x.Path, origin.RootPath, StringComparison.Ordinal) && string.Equals(x.Role, origin.RootRole, StringComparison.Ordinal)).ToList();
                        if (match.Count != 1)
                            return Refuse(res, "root-unresolved",
                                $"the catalog names root {origin.RootPath} ({origin.RootRole}) and the store {Path.GetFullPath(storePath)} has {match.Count} rows matching it; " +
                                "the scan cannot tell which root it is publishing. Export the root you mean with --store-export --export-root, or run without the store.");
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
                        return Refuse(res, "equal-generation-disagree",
                            $"the catalog and the store are both at generation {res.Generation} but disagree on the root's paths " +
                            $"({onlyCatalog.Count:N0} only in the catalog{Names(onlyCatalog)}; {onlyStore.Count:N0} only in the store{Names(onlyStore)}). " +
                            "Something changed one without the other; nothing was scanned. Re-publish the files from the store with --store-export, or move the catalog aside.");
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
                    $"the catalog says generation {origin.Generation} but the store {Path.GetFullPath(storePath)} is at {res.Generation}: the catalog came from another store state. Nothing was scanned.");
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
                return Refuse(res, "no-origin-disagree",
                    $"the catalog (no store origin: written before the store was authoritative) and the store {Path.GetFullPath(storePath)} disagree — " +
                    $"{onlyCatalog.Count:N0} paths only in the catalog{Names(onlyCatalog)}; {onlyStore.Count:N0} only in the store{Names(onlyStore)}; " +
                    $"{changed.Count:N0} with different values{Names(changed)}. A store-authoritative scan would replace the catalog's values with the store's. " +
                    $"If the catalog is the newer, seed or extend the store from it: truedat --store-import --moods \"{catalogPath}\" --store \"{storePath}\" " +
                    "(it never removes store entries); if the store is the newer, move the catalog aside and the scan re-derives it. Nothing was scanned.");
            res.Case = "no-origin-agree";
            return Fill(res, storeEntries, target);
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

        static string Names(List<string> paths)
            => paths.Count == 0 ? "" : ": " + string.Join(", ", paths.Take(NameAtMost)) + (paths.Count > NameAtMost ? $", and {paths.Count - NameAtMost:N0} more" : "");

        /// <summary>T18a-d: publish the catalog + sidecar + edges companion DERIVED from the store (the --store-export publisher: one read
        /// snapshot, generation-stamped, the ruled rename order). Returns null on success, else the failure (the store is unaffected: a
        /// publication failure leaves the files behind the store, which T07 accepts and the next publish repairs).</summary>
        internal static Exception? Publish(string storePath, string catalogPath, out ExportReport report)
        {
            report = new ExportReport();
            // The WHOLE library, always (StoreDal.LibraryRoots): a scan or a verb publishes every entry it holds, wherever the files are.
            // Passing the root the scan loaded made it publish one drive's tracks — or refuse, with the library under two roots.
            try { ExportCommand.Execute(new ExportOptions { StorePath = storePath, OutPath = catalogPath }, report); return null; }
            catch (Exception ex) { return ex; }
        }
    }
}
