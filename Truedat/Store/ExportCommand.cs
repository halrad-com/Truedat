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
        /// <summary>The scan's publisher only: an <see cref="ExportRoot"/> that is no longer a root because it was folded into the
        /// one primary root containing it (StoreDal.FoldNestedRoots) resolves to that root. An operator's typed --export-root never
        /// sets this — it means exactly the root named.</summary>
        public bool FollowFoldedRoot;
    }

    internal sealed class ExportReport
    {
        public string Root = ""; public string OutPath = ""; public int Entries; public int KeyMoves; public string? KeyMovesPath; public long ElapsedMs;
        /// <summary>Store-v3 T07: the generation the triple carries; Published = the ruled publication ran (false = split
        /// fallback, v1 header); Companion = mbxedges.v1.json was part of it (the store had edge rows).</summary>
        public long Generation; public bool Published; public bool Companion;
        /// <summary>For the store-result record (T11): the store opened, its schema, and where the sidecar/companion landed.</summary>
        public string StorePath = ""; public int StoreSchema; public string SidecarPath = ""; public string CompanionPath = "";
    }

    internal sealed class RoundtripReport
    {
        public int CatalogEntries, StoreEntries, Same, OnlyInCatalog, OnlyInStore, Different;
        /// <summary>Differing entries whose location is a non-primary copy of a multi-copy media and which differ ONLY in the shared
        /// content columns: by the 1:many:1 ruling they export their media's single description, so that difference is expected. A copy
        /// fact or lastModified difference on such a copy counts in <see cref="Different"/> (T13, gate d).</summary>
        public int DifferentMerged;
        /// <summary>Entries whose bytes differ but whose values agree at each field's declared precision: an older writer spelled a value
        /// one ulp off its decimal ("0.0028770000000000002" for 0.002877), and the store, holding the field at that precision, gives the
        /// decimal back. The store holds the catalog; only the spelling moved. (MB3, 2026-10-18: 47 of 7,641.)</summary>
        public int SamePrecision;
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

        /// <summary>Self-test seam: runs after the entries are read and before the generation/edge reads, so a test
        /// can commit to the same store in between and prove the exported set still comes from ONE snapshot
        /// (keeper review of c89d48a, P1). Null in production.</summary>
        internal static Action? AfterEntriesRead;

        /// <summary>Self-test seam (store-intent.md item 8): fires inside the export's read snapshot, before the root is picked, so a
        /// test can commit a second primary root in between and prove the published root comes from the snapshot. Null in production.</summary>
        internal static Action? BeforeRootPicked;

        /// <summary>Self-test seam: runs after the triple is published and before key-moves.json, so a test can fail the
        /// verb AFTER publication and prove the record keeps the facts (keeper review of 9706c5b, P2). Null in production.</summary>
        internal static Action? AfterPublished;

        internal static int Run(ExportOptions o)
        {
            // store-v3 T11: store-result.json on EVERY exit path — the hub reads it, never stdout. The report is filled IN
            // PLACE as Execute proceeds (store opened → schema/generation known → published → key moves), so a failure
            // at any step still records everything established before it (keeper review of 9706c5b, P2: a key-moves.json
            // failure after a generation-7 publication reported performed=false / schema 0 / generation 0).
            var result = StoreResult.Begin("store-export");
            var r = new ExportReport();
            try
            {
                Execute(o, r);
                Console.WriteLine($"Exported {r.Entries} entries of root {r.Root} to {r.OutPath} in {r.ElapsedMs / 1000.0:F1} s"
                    + (r.Published ? $" (generation {r.Generation}: catalog + sidecar{(r.Companion ? " + mbxedges.v1.json" : "")} published together)" : ""));
                if (r.KeyMovesPath != null) Console.WriteLine($"  {r.KeyMoves} content-hash key move(s) recorded in {r.KeyMovesPath}");
                result.Store = new StoreResultStore { Path = r.StorePath, SchemaVersion = r.StoreSchema, Generation = r.Generation, TruedatVersion = VersionInfo.Display };
                result.Export = new StoreResultExport
                {
                    Performed = r.Published, Generation = r.Published ? r.Generation : (long?)null,
                    Catalog = r.Published ? StoreResult.Describe(r.OutPath) : null,
                    Sidecar = r.Published ? StoreResult.Describe(r.SidecarPath) : null,
                    Companion = r.Published && r.Companion ? StoreResult.Describe(r.CompanionPath) : null,
                };
                result.Detail = new System.Collections.Generic.Dictionary<string, long> { ["entries"] = r.Entries, ["keyMoves"] = r.KeyMoves, ["elapsedMs"] = r.ElapsedMs };
                StoreResult.Write(r.StorePath, result, 0, StoreResult.OutcomeCompleted);
                return 0;
            }
            catch (StoreException ex)
            {
                Console.Error.WriteLine("Error: " + ex.Message);
                WriteFailed(o, r, result, ex.ExitCode, ex.Message);
                return ex.ExitCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error: export failed: " + ex.Message);
                WriteFailed(o, r, result, 1, "export failed: " + ex.Message);
                return 1;
            }
        }

        /// <summary>The failure record: everything the attempt established stays — the store block names the store when a
        /// path was given (schema and generation when it opened), and a publication that happened before the failing step
        /// is recorded as performed, at its generation, with the files it left on disk named by hash. Only the outcome says
        /// the VERB failed.</summary>
        static void WriteFailed(ExportOptions o, ExportReport r, StoreResultFile result, int exitCode, string reason)
        {
            string? storePath = r.StorePath.Length > 0 ? r.StorePath : null;
            try { if (storePath == null && !string.IsNullOrWhiteSpace(o.StorePath)) storePath = Path.GetFullPath(o.StorePath!); } catch { }
            bool opened = r.StorePath.Length > 0;   // schema/generation are facts only once the store answered; null otherwise (TDA review P3b)
            result.Store = new StoreResultStore { Path = storePath ?? "", SchemaVersion = opened ? r.StoreSchema : (int?)null, Generation = opened ? r.Generation : (long?)null, TruedatVersion = VersionInfo.Display };
            result.Export = new StoreResultExport
            {
                Performed = r.Published, Generation = r.Published ? r.Generation : (long?)null,
                Catalog = r.Published ? StoreResult.Describe(r.OutPath) : null,
                Sidecar = r.Published ? StoreResult.Describe(r.SidecarPath) : null,
                Companion = r.Published && r.Companion ? StoreResult.Describe(r.CompanionPath) : null,
            };
            StoreResult.Write(storePath, result, exitCode, StoreResult.OutcomeFailed, reason);
        }

        internal static ExportReport Execute(ExportOptions o) { var r = new ExportReport(); Execute(o, r); return r; }

        /// <summary>Fills <paramref name="r"/> as it goes (see <see cref="Run"/>); throws on failure with the facts so far in place.</summary>
        internal static void Execute(ExportOptions o, ExportReport r)
        {
            var sw = Stopwatch.StartNew();
            StoreLog.SetVerbose(o.Audit);
            if (string.IsNullOrWhiteSpace(o.OutPath))
                throw new StoreException("No output path given. Pass --store-export <path\\mbxmoods.json>.");
            var outFull = Path.GetFullPath(o.OutPath!);
            var outDir = Path.GetDirectoryName(outFull);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir!);

            using var os = OpenStore.Open(o.StorePath, create: false, VersionInfo.Display, DateTime.UtcNow.ToString("o"));
            // hub:H02: the selected edge version is stamped FIRST, in its own write transaction (advancing the generation only when it
            // changes), from the same value the companion below is built with — never inside the read snapshot that follows.
            var producerVersion = new global::Truedat.Edges.EdgeSettings().ProducerVersion;
            global::Truedat.Edges.EdgeStore.StampSelectedVersion(os.Dal, producerVersion);
            // ONE snapshot for everything the export reads — root, entries, generation, edge rows, key moves — so a
            // commit by another connection in between cannot label generation-N entries with N+1 (keeper review of
            // c89d48a, P1). A deferred read transaction: under WAL it blocks no writer and sees none.
            using var snapshot = os.Db.BeginRead();
            r.StorePath = os.Path; r.StoreSchema = SchemaMigrator.ReadVersion(os.Db); r.Generation = os.Dal.Generation();
            BeforeRootPicked?.Invoke();   // self-test seam: a concurrent commit lands here, inside the snapshot (item 8)
            var root = PickRoot(os.Dal, o.ExportRoot, o.FollowFoldedRoot);

            var dict = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
            foreach (var (display, entry) in os.Dal.ReadEntries(root.Id)) dict[display] = entry;
            AfterEntriesRead?.Invoke();   // self-test seam: a concurrent commit lands here
            StoreLog.Info($"export: {dict.Count} entries of root {root.Path} -> {outFull}");

            // Store-v3 T07: --store-export means the TRIPLE — catalog (v2 header: contract + origin, the sidecar bound
            // by hash), sidecar and companion — published in the ruled order. The generation is the store's current one;
            // an export re-emits it, it does not advance it (only mutations do — the selected-version restamp above is one,
            // committed before this snapshot).
            var origin = new CatalogOrigin
            {
                Kind = "export", MachineId = os.Machine.Id, StoreSchema = r.StoreSchema,
                Truedat = VersionInfo.Display, Generation = r.Generation,
                // store-intent.md item (8): the selected row exactly as stored, read in this snapshot — never the typed --export-root.
                RootPath = root.Path, RootRole = root.Role,
            };
            r.Root = root.Path; r.OutPath = outFull; r.Entries = dict.Count;
            r.SidecarPath = Path.ChangeExtension(outFull, ".mbxs");
            r.CompanionPath = Path.Combine(outDir ?? ".", global::Truedat.Edges.EdgeCompanion.FileName);
            bool hasEdges = os.Dal.ContentKeysWithEdges(producerVersion).Count > 0;
            var build = os.Dal.LatestRunVersion("essentia") ?? "unknown";
            Func<string, bool>? companion = !hasEdges ? null : tmp =>
            {
                global::Truedat.Edges.EdgeCompanion.WriteTempFromStore(os.Dal, tmp, producerVersion, "essentia_streaming_extractor_music " + build, origin.Generation);
                return true;
            };
            // T04: no store-side rendering rule any more — the store holds each field at its declared precision, so the ordinary catalog
            // writer produces the same bytes the scan would.
            PublicationResult? published = Program.SaveResultsPublished(outFull, dict, origin, companion);
            if (published != null) StoreLog.Info($"export: published generation {published.Generation} (sidecar {published.SidecarSha256.Substring(0, 12)}…, companion {(hasEdges ? "written" : "none — no edge rows at " + producerVersion)})");
            r.Published = published != null; r.Companion = hasEdges && published != null;
            AfterPublished?.Invoke();   // self-test seam: a failure after publication lands here

            var moves = ReadKeyMoves(os.Dal);
            string? movesPath = null;
            if (moves.Count > 0)
            {
                movesPath = Path.Combine(outDir ?? ".", KeyMovesFileName);
                WriteKeyMoves(movesPath, os.Path, moves);
                StoreLog.Info($"{moves.Count} key move(s) -> {movesPath}");
            }
            snapshot.Complete();
            sw.Stop();
            r.KeyMoves = moves.Count; r.KeyMovesPath = movesPath; r.ElapsedMs = sw.ElapsedMilliseconds;
        }

        internal static int RunRoundtrip(string? storePath, string catalogPath, string? exportRoot, bool audit)
        {
            try
            {
                StoreLog.SetVerbose(audit);
                var r = Roundtrip(storePath, catalogPath, exportRoot);
                Console.WriteLine($"Round-trip {storePath} vs {catalogPath}: catalog {r.CatalogEntries}, store {r.StoreEntries}, identical {r.Same}, different {r.Different}, only in catalog {r.OnlyInCatalog}, only in store {r.OnlyInStore}" +
                                  (r.SamePrecision > 0 ? $", same at declared precision {r.SamePrecision} (only the number spelling differs)" : "") +
                                  (r.DifferentMerged > 0 ? $", merged copies differing {r.DifferentMerged} (expected: a non-primary copy exports its media's single description)" : ""));
                if (r.FirstDifferentPath != null) Console.WriteLine($"  first difference: {r.FirstDifferentPath}");
                Console.WriteLine(r.Equal
                    ? (r.DifferentMerged == 0 && r.SamePrecision == 0 ? "RESULT: EQUAL — every entry writes the same bytes from the store as from the catalog"
                                              : "RESULT: EQUAL — the store holds every entry" +
                                                (r.SamePrecision > 0 ? $"; {r.SamePrecision} write their numbers as the decimal at the field's precision" : "") +
                                                (r.DifferentMerged > 0 ? $"; {r.DifferentMerged} extra copies of duplicate audio now carry their media's one description (1 : many : 1)" : ""))
                    : "RESULT: NOT EQUAL");
                return r.Equal ? 0 : 1;
            }
            catch (StoreException ex) { Console.Error.WriteLine("Error: " + ex.Message); return ex.ExitCode; }
            catch (Exception ex) { Console.Error.WriteLine("Error: round-trip failed: " + ex.Message); return 1; }
        }

        internal enum Agreement { Same, SamePrecision, MergedCopy, Different }

        /// <summary>How a catalog entry relates to the store's entry for the same path — the ONE rule, shared by this gate and the scan's
        /// no-origin precondition (StoreScan.HoldsSameFacts), so the two cannot disagree about whether the store holds the catalog.
        /// Same = identical bytes. SamePrecision = identical once both are taken to each field's declared precision: the store holds
        /// that precision, so an older writer's one-ulp spelling comes back as the decimal it stood for. MergedCopy = a non-primary copy
        /// of duplicate audio that differs only in the shared description (T13, gate d: put the store's content columns into the
        /// catalog's entry; whatever still differs is a copy fact or the location, stored per copy). Different = anything else.
        /// <paramref name="firstDifference"/> names where the renderings part, for the log.</summary>
        internal static Agreement Classify(string path, TrackEntry catalogEntry, TrackEntry storeEntry, bool nonPrimaryCopy, out string? firstDifference)
        {
            firstDifference = null;
            string a = EntryJson(path, catalogEntry), b = EntryJson(path, storeEntry);
            if (a == b) return Agreement.Same;
            string store = EntryJson(path, TrackEntryMapper.AtStorePrecision(storeEntry));
            if (EntryJson(path, TrackEntryMapper.AtStorePrecision(catalogEntry)) == store) { firstDifference = FirstDifference(a, b); return Agreement.SamePrecision; }
            if (nonPrimaryCopy)
            {
                var h = EntryJson(path, TrackEntryMapper.WithContentOf(catalogEntry, storeEntry));
                if (h == store) return Agreement.MergedCopy;
                firstDifference = FirstDifference(h, store);
                return Agreement.Different;
            }
            firstDifference = FirstDifference(a, b);
            return Agreement.Different;
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
                bool nonPrimary = merged.Contains(display);
                switch (Classify(display, orig, entry, nonPrimary, out var diff))
                {
                    case Agreement.Same: r.Same++; break;
                    case Agreement.SamePrecision: r.SamePrecision++; StoreLog.Debug($"differs only in how a value is spelled (same at its declared precision): {display}: {diff}"); break;
                    case Agreement.MergedCopy: r.DifferentMerged++; StoreLog.Debug($"differs in the shared description only (merged copy, expected): {display}"); break;
                    default:
                        r.Different++; r.FirstDifferentPath ??= display;
                        StoreLog.Debug(nonPrimary ? $"merged copy differs beyond the shared description: {display}: {diff}" : $"differs: {display}: {diff}");
                        break;
                }
            }
            foreach (var key in catalog.Keys) if (!seen.Contains(key)) r.OnlyInCatalog++;
            return r;
        }

        /// <summary>The catalog writer's bytes for one entry — the unit of the round-trip gate. T04: no rendering rule sits between
        /// the writer and the bytes any more. Both sides (the loaded catalog entry and the store's) go through the same writer; the
        /// store side holds each field at its declared precision (the double Math.Round(v, dp) produced), which is the same double the
        /// catalog's text parses to, so the C6a one-bit case ("0.022454" vs "0.022454000000000002") renders identically because the
        /// VALUES are identical, not because a rule hid the difference. A changed measurement still differs.</summary>
        internal static string EntryJson(string path, TrackEntry entry)
        {
            using var ms = new MemoryStream();
            using (var jw = new Utf8JsonWriter(ms))
            {
                jw.WriteStartObject();
                Program.WriteTrackEntryForStore(jw, path, entry);
                jw.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        static (long Id, string Path, string Role) PickRoot(StoreDal dal, string? exportRoot, bool followFold = false)
        {
            var roots = dal.ListRoots();
            if (roots.Count == 0) throw new StoreException("The store has no library roots; import a catalog first.");
            if (!string.IsNullOrWhiteSpace(exportRoot))
            {
                var want = Norm(exportRoot!);
                foreach (var r in roots) if (Norm(r.Path) == want) return r;
                if (followFold)
                {
                    var containing = roots.Where(x => x.Role == "primary" && CanonicalPath.Contains(x.Path, exportRoot!)).ToList();
                    if (containing.Count == 1) return containing[0];
                }
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
