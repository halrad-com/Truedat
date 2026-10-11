using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Truedat.Store
{
    /// <summary>The store as the dictionary every read-only verb already consumes: what --stats and
    /// --duplicates get instead of LoadExistingMoods when --store is given. Keyed by display path with
    /// PathComparer, exactly as the reader keys it, so the verbs cannot tell the difference — which is
    /// the point: the in-truedat consumer that satisfies the wiring gate.</summary>
    internal static class StoreDictionary
    {
        internal static ConcurrentDictionary<string, TrackEntry> Load(string? storePath, string? exportRoot, out string rootPath)
        {
            using var os = OpenStore.Open(storePath, create: false, VersionInfo.Display, DateTime.UtcNow.ToString("o"));
            var roots = os.Dal.ListRoots();
            if (roots.Count == 0) throw new StoreException("The store has no library roots; import a catalog first.");
            (long Id, string Path, string Role) root;
            if (!string.IsNullOrWhiteSpace(exportRoot))
            {
                var want = Norm(exportRoot!);
                root = roots.FirstOrDefault(r => Norm(r.Path) == want);
                if (root.Path == null)
                    throw new StoreException($"No root '{exportRoot}' in the store. Roots: {string.Join(", ", roots.Select(x => $"{x.Path} ({x.Role})"))}.");
            }
            else
            {
                var primaries = roots.Where(x => x.Role == "primary").ToList();
                if (primaries.Count != 1)
                    throw new StoreException(primaries.Count == 0
                        ? $"The store has no primary root; pass --export-root. Roots: {string.Join(", ", roots.Select(x => $"{x.Path} ({x.Role})"))}."
                        : $"The store has {primaries.Count} primary roots; pass --export-root to pick one: {string.Join(", ", primaries.Select(x => x.Path))}.");
                root = primaries[0];
            }
            var dict = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
            foreach (var (display, entry) in os.Dal.ReadEntries(root.Id)) dict[display] = entry;
            StoreLog.Info($"store dictionary: {dict.Count} entries of root {root.Path}");
            rootPath = root.Path;
            return dict;
        }

        static string Norm(string p) => p.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
    }
}
