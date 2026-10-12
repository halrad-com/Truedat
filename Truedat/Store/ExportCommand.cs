using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Truedat.Store
{
    internal sealed class ExportOptions
    {
        public string? StorePath;
        public string? OutPath;
        public string? ExportRoot;
        public bool Audit;
    }

    internal sealed class ExportReport
    {
        public string Root = ""; public string OutPath = ""; public int Entries; public int KeyMoves; public string? KeyMovesPath; public long ElapsedMs;
    }

    internal sealed class RoundtripReport
    {
        public int CatalogEntries, StoreEntries, Same, OnlyInCatalog, OnlyInStore, Different;
        /// <summary>Differing entries whose location is a non-primary copy of a multi-copy media: by the 1:many:1
        /// ruling they export their media's single description, so a difference there is expected, not a defect.</summary>
        public int DifferentMerged;
        public string? FirstDifferentPath;
        public bool Equal => OnlyInCatalog == 0 && OnlyInStore == 0 && Different == 0 && CatalogEntries == StoreEntries;
    }

    internal sealed class KeyMove
    {
        public string EntityId = ""; public string OldKind = ""; public string OldHash = ""; public string NewKind = ""; public string NewHash = "";
        public string FirstSeenUtc = ""; public string SupersededUtc = "";
    }

    /// <summary>--store-export: the store → a catalog, through the UNCHANGED writer (SaveResults, with its
    /// parts, sidecar and atomic swap), so the hub reads it exactly as a scan's save. Beside it,
    /// key-moves.json whenever any entity carries a superseded content hash — the durable old→new
    /// mapping the 2026-10-11 FLAC-residuals finding showed was missing. --store-roundtrip is the
    /// S1 gate made a verb: per-entry byte equality of store against catalog, writer-vs-writer.</summary>
    internal static class ExportCommand
    {
        internal const string KeyMovesFileName = "key-moves.json";

        internal static int Run(ExportOptions o)
        {
            try
            {
                var r = Execute(o);
                Console.WriteLine($"Exported {r.Entries} entries of root {r.Root} to {r.OutPath} in {r.ElapsedMs / 1000.0:F1} s");
                if (r.KeyMovesPath != null) Console.WriteLine($"  {r.KeyMoves} content-hash key move(s) recorded in {r.KeyMovesPath}");
                return 0;
            }
            catch (StoreException ex) { Console.Error.WriteLine("Error: " + ex.Message); return ex.ExitCode; }
            catch (Exception ex) { Console.Error.WriteLine("Error: export failed: " + ex.Message); return 1; }
        }

        internal static ExportReport Execute(ExportOptions o)
        {
            var sw = Stopwatch.StartNew();
            StoreLog.SetVerbose(o.Audit);
            if (string.IsNullOrWhiteSpace(o.OutPath))
                throw new StoreException("No output path given. Pass --store-export <path\\mbxmoods.json>.");
            var outFull = Path.GetFullPath(o.OutPath!);
            var outDir = Path.GetDirectoryName(outFull);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir!);

            using var os = OpenStore.Open(o.StorePath, create: false, VersionInfo.Display, DateTime.UtcNow.ToString("o"));
            var root = PickRoot(os.Dal, o.ExportRoot);

            var dict = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
            foreach (var (display, entry) in os.Dal.ReadEntries(root.Id)) dict[display] = entry;
            StoreLog.Info($"export: {dict.Count} entries of root {root.Path} -> {outFull}");
            // The store's canonical precision (six significant figures, exact text) on the way out; the writer itself is unchanged.
            Program.StoreCanonicalNumbers = true;
            try { Program.SaveResultsForStore(outFull, dict); }
            finally { Program.StoreCanonicalNumbers = false; }

            var moves = ReadKeyMoves(os.Dal);
            string? movesPath = null;
            if (moves.Count > 0)
            {
                movesPath = Path.Combine(outDir ?? ".", KeyMovesFileName);
                WriteKeyMoves(movesPath, os.Path, moves);
                StoreLog.Info($"{moves.Count} key move(s) -> {movesPath}");
            }
            sw.Stop();
            return new ExportReport { Root = root.Path, OutPath = outFull, Entries = dict.Count, KeyMoves = moves.Count, KeyMovesPath = movesPath, ElapsedMs = sw.ElapsedMilliseconds };
        }

        internal static int RunRoundtrip(string? storePath, string catalogPath, string? exportRoot, bool audit)
        {
            try
            {
                StoreLog.SetVerbose(audit);
                var r = Roundtrip(storePath, catalogPath, exportRoot);
                Console.WriteLine($"Round-trip {storePath} vs {catalogPath}: catalog {r.CatalogEntries}, store {r.StoreEntries}, identical {r.Same}, different {r.Different}, only in catalog {r.OnlyInCatalog}, only in store {r.OnlyInStore}" +
                                  (r.DifferentMerged > 0 ? $", merged copies differing {r.DifferentMerged} (expected: a non-primary copy exports its media's single description)" : ""));
                if (r.FirstDifferentPath != null) Console.WriteLine($"  first difference: {r.FirstDifferentPath}");
                Console.WriteLine(r.Equal
                    ? (r.DifferentMerged == 0 ? "RESULT: EQUAL — every entry writes the same bytes from the store as from the catalog"
                                              : $"RESULT: EQUAL — every single-copy entry writes the same bytes; {r.DifferentMerged} extra copies of duplicate audio now carry their media's one description (1 : many : 1)")
                    : "RESULT: NOT EQUAL");
                return r.Equal ? 0 : 1;
            }
            catch (StoreException ex) { Console.Error.WriteLine("Error: " + ex.Message); return ex.ExitCode; }
            catch (Exception ex) { Console.Error.WriteLine("Error: round-trip failed: " + ex.Message); return 1; }
        }

        /// <summary>Where two renderings first part, with 60 characters of each around it — a NOT EQUAL that names only the path
        /// sends the reader to diff two catalogs by hand (C6a, 2026-10-12).</summary>
        internal static string FirstDifference(string a, string b)
        {
            int i = 0; while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
            int from = Math.Max(0, i - 60);
            string Win(string s) => s.Substring(from, Math.Min(120, Math.Max(0, s.Length - from)));
            return $"at char {i} (lengths {a.Length} vs {b.Length}): catalog '{Win(a)}' | store '{Win(b)}'";
        }

        /// <summary>Per-entry byte equality, writer-vs-writer: for every catalog key, the bytes the catalog
        /// writer produces for the loaded entry equal the bytes it produces for the store's entry. Order
        /// independent — whole-file equality is unsound because SaveResults writes dictionary order and a
        /// fresh generatedAt.</summary>
        internal static RoundtripReport Roundtrip(string? storePath, string catalogPath, string? exportRoot)
        {
            var r = new RoundtripReport();
            var catalog = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
            r.CatalogEntries = Program.LoadExistingMoodsForStore(Path.GetFullPath(catalogPath), catalog);
            using var os = OpenStore.Open(storePath, create: false, VersionInfo.Display, DateTime.UtcNow.ToString("o"));
            var root = PickRoot(os.Dal, exportRoot);
            // v2: a non-primary copy of a multi-copy media exports its media's single description (operator's
            // 1 : many : 1), so a difference there is expected and reported apart from unexplained ones.
            var merged = os.Dal.NonPrimaryCopyPaths(root.Id);
            var seen = new HashSet<string>(PathComparer.Instance);
            foreach (var (display, entry) in os.Dal.ReadEntries(root.Id))
            {
                r.StoreEntries++;
                seen.Add(display);
                if (!catalog.TryGetValue(display, out var orig)) { r.OnlyInStore++; continue; }
                string a = EntryJson(display, orig), b = EntryJson(display, entry);
                if (a == b) r.Same++;
                else if (merged.Contains(display)) { r.DifferentMerged++; StoreLog.Debug($"differs (merged copy, expected): {display}"); }
                else { r.Different++; r.FirstDifferentPath ??= display; StoreLog.Debug($"differs: {display}: {FirstDifference(a, b)}"); }
            }
            foreach (var key in catalog.Keys) if (!seen.Contains(key)) r.OnlyInCatalog++;
            return r;
        }

        /// <summary>The catalog writer's bytes for one entry at the store's canonical precision — the unit of the round-trip
        /// gate. Both sides (the loaded catalog entry and the store's) render through the same six-figure rule, so a one-bit
        /// difference between a parsed double and a computed one (C6a: "0.022454" vs "0.022454000000000002") is not a difference;
        /// a changed measurement still is.</summary>
        internal static string EntryJson(string path, TrackEntry entry)
        {
            using var ms = new MemoryStream();
            bool prior = Program.StoreCanonicalNumbers;
            Program.StoreCanonicalNumbers = true;
            try
            {
                using (var jw = new Utf8JsonWriter(ms))
                {
                    jw.WriteStartObject();
                    Program.WriteTrackEntryForStore(jw, path, entry);
                    jw.WriteEndObject();
                }
            }
            finally { Program.StoreCanonicalNumbers = prior; }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        static (long Id, string Path, string Role) PickRoot(StoreDal dal, string? exportRoot)
        {
            var roots = dal.ListRoots();
            if (roots.Count == 0) throw new StoreException("The store has no library roots; import a catalog first.");
            if (!string.IsNullOrWhiteSpace(exportRoot))
            {
                var want = Norm(exportRoot!);
                foreach (var r in roots) if (Norm(r.Path) == want) return r;
                throw new StoreException($"No root '{exportRoot}' in the store. Roots: {string.Join(", ", roots.Select(x => $"{x.Path} ({x.Role})"))}.");
            }
            var primaries = roots.Where(x => x.Role == "primary").ToList();
            if (primaries.Count == 1) return primaries[0];
            if (primaries.Count == 0) throw new StoreException($"The store has no primary root; pass --export-root. Roots: {string.Join(", ", roots.Select(x => $"{x.Path} ({x.Role})"))}.");
            throw new StoreException($"The store has {primaries.Count} primary roots; pass --export-root to pick one: {string.Join(", ", primaries.Select(x => x.Path))}.");
        }

        static string Norm(string p) => p.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

        internal static List<KeyMove> ReadKeyMoves(StoreDal dal)
        {
            var list = new List<KeyMove>();
            using var st = dal.Db.Prepare(
                "SELECT o.entity_id, o.kind, o.value, o.first_seen_utc, n.kind, n.value, n.first_seen_utc " +
                "FROM identity_facet o JOIN identity_facet n ON n.entity_id = o.entity_id AND n.is_primary = 1 AND substr(n.kind, 1, 13) = 'content_hash:' " +
                "WHERE o.is_primary = 0 AND substr(o.kind, 1, 13) = 'content_hash:' ORDER BY o.entity_id, o.id;");
            while (st.Step())
                list.Add(new KeyMove
                {
                    EntityId = st.GetString(0)!, OldKind = st.GetString(1)!, OldHash = st.GetString(2)!, FirstSeenUtc = st.GetString(3) ?? "",
                    NewKind = st.GetString(4)!, NewHash = st.GetString(5)!, SupersededUtc = st.GetString(6) ?? "",
                });
            return list;
        }

        static void WriteKeyMoves(string path, string store, List<KeyMove> moves)
        {
            var arr = new JsonArray();
            foreach (var m in moves)
                arr.Add(new JsonObject
                {
                    ["entity"] = m.EntityId, ["oldKind"] = m.OldKind, ["oldHash"] = m.OldHash, ["newKind"] = m.NewKind, ["newHash"] = m.NewHash,
                    ["firstSeen"] = m.FirstSeenUtc, ["supersededAt"] = m.SupersededUtc,
                });
            var j = new JsonObject { ["kind"] = "key-moves", ["schemaVersion"] = 1, ["generatedAt"] = DateTime.UtcNow.ToString("o"), ["store"] = store, ["count"] = moves.Count, ["moves"] = arr };
            File.WriteAllText(path, j.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
