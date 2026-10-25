using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Truedat.Store
{
    /// <summary>Merge winners use source order; absent values belong only to the same or unknown content.</summary>
    internal static class MergeSafetySelfTest
    {
        const string Root = @"D:\MergeSafety", PathA = Root + @"\a.flac", PathB = Root + @"\b.flac";
        const string Old = "2026-05-01T00:00:00.0000000Z", New = "2026-06-01T00:00:00.0000000Z";
        static readonly string ShaA = new string('a', 64), ShaB = new string('b', 64), Md5 = new string('c', 32);

        internal static void Run(Action<bool, string> assert)
        {
            Block(assert, "older winner", () => OlderWinner(assert));
            foreach (bool knownEntity in new[] { false, true })
                foreach (bool differentKind in new[] { false, true })
                    Block(assert, $"replacement ({knownEntity}, {differentKind})", () => Replacement(assert, knownEntity, differentKind));
            foreach (bool storedHash in new[] { false, true })
                foreach (bool winningHash in new[] { false, true })
                    Block(assert, $"NULL preservation ({storedHash}, {winningHash})", () => PreserveAbsent(assert, storedHash, winningHash));
            Block(assert, "fresh scan NULL", () => FreshScanNull(assert));
            Block(assert, "legacy alias", () => LegacyAlias(assert));
        }

        static void OlderWinner(Action<bool, string> assert)
        {
            using var f = new Fixture();
            f.Seed((PathA, Entry(ShaA, "flac-frames", New, true)));
            var winner = Entry(ShaA, "flac-frames", Old, false);
            winner["title"] = "Explicit later winner"; winner["bpm"] = 130;
            string before = f.Values(PathA), history = f.History();
            long generation = f.Generation();

            // Ordinary import still rejects an older observation. Merge has the operator's explicit source order instead.
            var ordinary = f.Import(winner);
            assert(ordinary.Stale == 1 && f.Values(PathA) == before && f.Generation() == generation,
                "MergeSafety: an ordinary older import remains stale and leaves stored values/generation unchanged");
            int exit = f.Merge(winner);
            assert(exit == 0 && f.Cell("p.title || '|' || c.bpm || '|' || l.last_modified_utc", PathA) == "Explicit later winner|1300|" + Old,
                "MergeSafety: the explicit later merge source wins supplied title/BPM even with an older file mtime");
            assert(f.Cell("p.album || '|' || p.fileMd5", PathA) == "Stored album|" + Md5,
                "MergeSafety: choosing an older merge winner still preserves its omitted same-content copy facts");
            assert(f.Generation() > generation && f.HasHistory(history),
                "MergeSafety: the winning edit advances generation and keeps earlier run IDs and payloads");
            string once = f.Values(PathA); generation = f.Generation();
            exit = f.Merge(winner);
            assert(exit == 0 && f.Values(PathA) == once && f.Generation() == generation,
                "MergeSafety: repeating the explicit older winner changes neither stored values nor generation");
        }

        static void Replacement(Action<bool, string> assert, bool knownEntity, bool differentKind)
        {
            string label = $"MergeSafety replacement ({(knownEntity ? "existing destination entity" : "new hash")}, {(differentKind ? "different hash kind" : "different hash")})";
            using var f = new Fixture();
            // A kind transition goes from legacy region to frame hashing. The reverse has a separate legacy-alias contract.
            string? oldKind = differentKind ? null : "flac-frames";
            string nextSha = differentKind ? ShaA : ShaB;
            var original = Entry(ShaA, oldKind, Old, true);
            var other = Entry(nextSha, "flac-frames", New, true);
            other["title"] = "Other copy"; other["fileMd5"] = new string('d', 32);
            if (knownEntity) f.Seed((PathA, original), (PathB, other)); else f.Seed((PathA, original));
            string history = f.History(), otherBefore = knownEntity ? f.CopyValues(PathB) : "";
            var winner = Entry(nextSha, "flac-frames", New, false);
            winner["title"] = "Replacement";
            int exit = f.Merge(winner);
            assert(exit == 0 && f.Cell("c.audioStreamSha256 || '|' || c.audioStreamSha256Source || '|' || p.title", PathA) == nextSha + "|flac-frames|Replacement",
                label + ": supplied content identity and title reach the destination");
            assert(f.Cell("(p.album IS NULL) || '|' || (p.fileMd5 IS NULL) || '|' || (p.smfmBpm IS NULL) || '|' || (p.smfmScores IS NULL) || '|' || (p.fingerprint_v1_audioHead64kMd5 IS NULL)", PathA) == "1|1|1|1|1",
                label + ": the replacement never inherits album, MD5, SMFM, or fingerprint from the previous content");
            assert(f.HasHistory(history) && f.Sql("PRAGMA foreign_key_check;") == null,
                label + ": previous run IDs/payloads remain and foreign keys are valid");
            if (knownEntity)
                assert(f.CopyValues(PathB) == otherBefore && f.Cell("l.entity_id", PathA) == f.Cell("l.entity_id", PathB),
                    label + ": joining a known entity does not borrow or rewrite its other copy's facts");
            string once = f.Values(PathA); long generation = f.Generation();
            exit = f.Merge(winner);
            assert(exit == 0 && f.Values(PathA) == once && f.Generation() == generation,
                label + ": repeat merge preserves the result and generation");
        }

        static void PreserveAbsent(Action<bool, string> assert, bool storedHash, bool winningHash)
        {
            string label = $"MergeSafety NULL preservation (stored hash {storedHash}, winning hash {winningHash})";
            using var f = new Fixture();
            f.Seed((PathA, Entry(storedHash ? ShaA : null, "flac-frames", Old, true)));
            // Mix explicit NULL with omitted fields. Both mean unobserved; supplied zero and empty are real values.
            var winner = Entry(winningHash ? ShaA : null, "flac-frames", New, false);
            winner["album"] = null; winner["fileMd5"] = null; winner["smfmScores"] = null;
            winner["mfcc"] = null; winner["title"] = ""; winner["bpm"] = 0;
            int exit = f.Merge(winner);
            assert(exit == 0 && f.Cell("quote(p.title) || '|' || c.bpm", PathA) == "''|0",
                label + ": supplied empty title and zero BPM replace stored values");
            assert(f.Cell("p.album || '|' || p.fileMd5 || '|' || p.smfmBpm || '|' || (p.smfmScores IS NOT NULL) || '|' || p.fingerprint_v1_audioHead64kMd5 || '|' || (c.mfcc IS NOT NULL)", PathA)
                == "Stored album|" + Md5 + "|95000000|1|" + Md5 + "|1",
                label + ": explicit NULL and omitted content/copy fields keep the known stored values");
            string once = f.Values(PathA); long generation = f.Generation();
            exit = f.Merge(winner);
            assert(exit == 0 && f.Values(PathA) == once && f.Generation() == generation,
                label + ": repeated partial winner leaves values and generation unchanged");
        }

        static void LegacyAlias(Action<bool, string> assert)
        {
            using var f = new Fixture();
            f.Seed((PathA, Entry(ShaA, "flac-frames", New, true)));
            string content = f.Cell("c.content_key || '|' || c.content_key_kind || '|' || c.bpm || '|' || c.run_id", PathA)!;
            var alias = Entry(ShaB, null, Old, true);
            alias["title"] = "Legacy title"; alias["fileMd5"] = new string('e', 32); alias["genre"] = "Jazz"; alias["bpm"] = 77;
            var report = f.Import(alias);
            assert(report.ContentRekeyed == 0 && report.ContentReplaced == 0
                && f.Cell("c.content_key || '|' || c.content_key_kind || '|' || c.bpm || '|' || c.run_id", PathA) == content,
                "MergeSafety legacy alias: an ordinary old FLAC-region arrival does not replace the frame-keyed description or its run");
            assert(f.Cell("p.title || '|' || p.fileMd5 || '|' || p.genre", PathA) == "Stored title|" + Md5 + "|Jazz",
                "MergeSafety legacy alias: known older copy facts only fill missing fields; stored title and MD5 remain");
            int exit = f.Merge(alias);
            assert(exit == 0 && f.Cell("c.content_key || '|' || c.content_key_kind || '|' || c.bpm || '|' || c.run_id", PathA) == content
                && f.Cell("p.title || '|' || p.fileMd5", PathA) == "Stored title|" + Md5,
                "MergeSafety legacy alias: merge also preserves the existing legacy-alias contract");
        }

        static void FreshScanNull(Action<bool, string> assert)
        {
            using var f = new Fixture();
            f.Seed((PathA, Entry(ShaA, "flac-frames", Old, true)));
            var measured = new TrackEntry
            {
                LastModified = DateTime.Parse(New, null, System.Globalization.DateTimeStyles.RoundtripKind),
                AudioStreamSha256 = ShaA, AudioStreamSha256Source = "flac-frames",
                Features = new TrackFeatures { Title = "Freshly measured", Bpm = 130 },
                // A normal scan does not carry a catalog observation mask; optional unavailable values remain NULL.
            };
            foreach (ulong? mask in new ulong?[] { null, TrackEntryMapper.PerFileScanCopyObserved })
            {
                string mode = mask.HasValue ? "per-file mask" : "raw entry";
                measured.CopyObserved = mask; measured.Features.Title = "Freshly measured " + mode;
                ImportCommand.ExecuteEntries(f.Store, new Dictionary<string, TrackEntry>(PathComparer.Instance) { [PathA] = measured },
                    Root, "primary", true, "essentia", "merge-safety", "synthetic fresh scan", null, null);
                assert(f.Cell("p.title || '|' || c.bpm", PathA) == measured.Features.Title + "|1300",
                    "MergeSafety fresh scan (" + mode + "): the ordinary scan writer stores supplied title and BPM");
                assert(f.Cell("p.fileMd5 || '|' || p.smfmBpm || '|' || (p.smfmScores IS NOT NULL) || '|' || p.fingerprint_v1_audioHead64kMd5", PathA)
                    == Md5 + "|95000000|1|" + Md5,
                    "MergeSafety fresh scan (" + mode + "): unavailable optional MD5, SMFM, and fingerprint never erase known same-content values");
            }
            int exit = f.StripSmfm();
            assert(exit == 0 && f.Cell("(p.smfmBpm IS NULL) || '|' || (p.smfmScores IS NULL) || '|' || p.fileMd5", PathA) == "1|1|" + Md5,
                "MergeSafety fresh scan: an explicit strip still clears SMFM and retains unrelated MD5");
        }

        static Dictionary<string, object?> Entry(string? sha, string? kind, string modified, bool full)
        {
            var e = new Dictionary<string, object?>
            {
                ["lastModified"] = modified, ["title"] = "Stored title", ["bpm"] = 120,
            };
            if (sha != null) e["audioStreamSha256"] = sha;
            if (kind != null) e["audioStreamSha256Source"] = kind;
            if (full)
            {
                e["album"] = "Stored album"; e["fileMd5"] = Md5; e["smfmBpm"] = 95;
                e["smfmScores"] = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }; e["mfcc"] = new[] { 1.5, -2.25 };
                e["fingerprint.v1"] = new Dictionary<string, object?>
                {
                    ["fileSize"] = 100000, ["durationMs"] = 1000, ["sampleRate"] = 44100, ["channels"] = 2,
                    ["codec"] = "flac", ["pathTail"] = "a.flac", ["audioHead64kMd5"] = Md5,
                };
            }
            return e;
        }

        static void Block(Action<bool, string> assert, string label, Action test)
        {
            try { test(); }
            catch (Exception ex) { assert(false, "MergeSafety " + label + ": " + ex); }
        }

        sealed class Fixture : IDisposable
        {
            static readonly FieldInfo StoreField = typeof(Program).GetField("_storePath", BindingFlags.NonPublic | BindingFlags.Static)!;
            static readonly MethodInfo MergeMethod = typeof(Program).GetMethod("RunMergeMoods", BindingFlags.NonPublic | BindingFlags.Static)!;
            readonly string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), ".truedat-selftest", "merge-safety-" + Guid.NewGuid().ToString("N"));
            readonly object? savedStore = StoreField.GetValue(null);
            readonly IDisposable quiet = StoreLog.Quiet();
            readonly int savedExit = Environment.ExitCode;
            internal string Store => System.IO.Path.Combine(dir, "truedat.db");
            string Catalog => System.IO.Path.Combine(dir, "mbxmoods.json");
            internal Fixture() { Directory.CreateDirectory(dir); }

            internal void Seed(params (string Path, Dictionary<string, object?> Entry)[] entries)
            {
                string file = Write("seed.json", entries);
                ImportCommand.Execute(new ImportOptions { StorePath = Store, CatalogPath = file, LibraryRoot = Root });
            }
            internal ImportReport Import(Dictionary<string, object?> entry)
                => ImportCommand.Execute(new ImportOptions { StorePath = Store, CatalogPath = Write("import.json", (PathA, entry)), LibraryRoot = Root });
            internal int Merge(Dictionary<string, object?> entry)
            {
                string winner = Write("winner.json", (PathA, entry));
                return Verb("merge-moods", "--merge-moods",
                    () => (int)MergeMethod.Invoke(null, new object[] { new List<string> { Catalog, winner }, Catalog })!);
            }
            internal int StripSmfm() => Verb("strip", "--strip-smfm", () =>
            {
                typeof(Program).GetMethod("RunStripSmfm", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, new object[] { Catalog, false });
                return Environment.ExitCode;
            });
            int Verb(string verb, string flag, Func<int> run)
            {
                StoreField.SetValue(null, Store);
                var output = Console.Out;
                try
                {
                    Console.SetOut(TextWriter.Null);
                    return Program.RunStoreBackedVerb(verb, flag, Catalog, run);
                }
                finally { Console.SetOut(output); }
            }
            string Write(string name, params (string Path, Dictionary<string, object?> Entry)[] entries)
            {
                var tracks = new Dictionary<string, object?>();
                foreach (var e in entries) tracks[e.Path] = e.Entry;
                string file = System.IO.Path.Combine(dir, name);
                File.WriteAllText(file, JsonSerializer.Serialize(new Dictionary<string, object?> { ["tracks"] = tracks, ["trackCount"] = tracks.Count }));
                return file;
            }
            internal string? Sql(string sql)
            {
                using var os = OpenStore.Open(Store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o"));
                return os.Db.ExecScalar(sql);
            }
            internal string? Cell(string expression, string path)
                => Sql("SELECT " + expression + " FROM track_location l JOIN track_content c ON c.entity_id = l.entity_id JOIN track_copy p ON p.location_id = l.id WHERE l.display_path = '" + path.Replace("'", "''") + "';");
            internal long Generation() => long.Parse(Sql("SELECT value FROM store_meta WHERE key = 'generation';")!);
            internal string CopyValues(string path)
                => Cell("quote(p.title) || '|' || quote(p.album) || '|' || quote(p.fileMd5) || '|' || quote(p.smfmBpm) || '|' || quote(p.smfmScores) || '|' || quote(p.fingerprint_v1_audioHead64kMd5) || '|' || quote(p.run_id) || '|' || quote(p.updated_utc)", path)!;
            internal string Values(string path)
                => CopyValues(path) + "|" + Cell("quote(c.audioStreamSha256) || '|' || quote(c.audioStreamSha256Source) || '|' || quote(c.bpm) || '|' || quote(c.mfcc) || '|' || quote(c.run_id) || '|' || quote(l.last_modified_utc)", path);
            internal string History() => Sql("SELECT group_concat(row, char(10)) FROM (SELECT id || '|' || quote(producer_key) || '|' || quote(content_hash) || '|' || quote(location_id) || '|' || quote(source) AS row FROM run ORDER BY id);") ?? "";
            internal bool HasHistory(string before)
            {
                string after = "\n" + History() + "\n";
                foreach (string row in before.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    if (!after.Contains("\n" + row + "\n")) return false;
                return true;
            }
            public void Dispose()
            {
                StoreField.SetValue(null, savedStore); Environment.ExitCode = savedExit; quiet.Dispose();
                string ownedRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), ".truedat-selftest")) + System.IO.Path.DirectorySeparatorChar;
                if (System.IO.Path.GetFullPath(dir).StartsWith(ownedRoot, StringComparison.OrdinalIgnoreCase))
                    try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
