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
            // The same selection as the export (the whole library, or the one typed --export-root), so --stats and --duplicates
            // describe exactly what a publication writes.
            var roots = ExportCommand.PickRoots(os, exportRoot);
            var dict = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
            foreach (var (display, entry) in os.Dal.ReadEntries(roots.Select(x => x.Id))) dict[display] = entry;
            rootPath = ExportCommand.Describe(roots);   // "root X" or "N roots (X, Y)" — for the Source line only
            StoreLog.Info($"store dictionary: {dict.Count} entries of {ExportCommand.Describe(roots)}");
            return dict;
        }
    }
}
