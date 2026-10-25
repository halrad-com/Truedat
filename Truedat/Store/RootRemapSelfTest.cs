using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Truedat.Store
{
    /// <summary>Real CLI regressions for explicit remaps and refusal before publishing over a mirror export.</summary>
    internal static class RootRemapSelfTest
    {
        internal static void Run(Action<bool, string> assert)
        {
            var cases = new[]
            {
                ("canonical", @"D:\Music", @"D:\Music\song.flac", @"D:\Music", @"E:\Remapped"),
                ("trailing", @"D:\Music\", @"D:\Music\song.flac", @"D:\Music\", @"E:\Remapped\"),
                ("slashes", "D:/Music/", @"D:\Music\song.flac", @"D:\Music\", @"E:\Remapped\"),
                ("Unicode case", @"D:\Äudio", @"D:\äUDIO\song.flac", @"D:\äUDIO", @"E:\Remapped"),
            };
            foreach (var c in cases)
                Block(assert, "RootRemap " + c.Item1, () => Remap(assert, c.Item1, c.Item2, c.Item3, c.Item4, c.Item5,
                    @"E:\Remapped\song.flac", false, true));
            foreach (bool hashed in new[] { true, false })
                Block(assert, "RootRemap subfolder", () => Remap(assert, "subfolder " + (hashed ? "hashed" : "hashless"),
                    @"D:\Music", @"D:\Music\sub\song.flac", @"D:\Music\sub", @"D:\Music\newsub",
                    @"D:\Music\newsub\song.flac", true, hashed));
            Block(assert, "RootRemap mirror refusal", () => MirrorRefusal(assert));
            Block(assert, "RootRemap collision", () => Collision(assert));
            foreach (bool rollback in new[] { false, true })
                Block(assert, "RootRemap overlap", () => Overlap(assert, rollback));
            foreach (string role in new[] { "mirror", "endpoint" })
                Block(assert, "RootRemap destination role", () => DestinationRole(assert, role, false));
            Block(assert, "RootRemap foreign-owner destination role", () => DestinationRole(assert, "mirror", true));
            Block(assert, "RootRemap retired outer destination", () => RetiredOuterDestination(assert));
        }

        static void Remap(Action<bool, string> assert, string name, string root, string oldPath, string oldPrefix,
            string newPrefix, string expectedPath, bool subfolder, bool hashed)
        {
            using var f = new Fixture(assert, name);
            using var quiet = StoreLog.Quiet();
            string untouched = subfolder ? @"D:\Music\stay.flac" : @"D:\Elsewhere\stay.flac";
            Seed(f.Store, root, "primary", oldPath, Entry("Moved", hashed ? 'a' : (char?)null));
            Seed(f.Store, subfolder ? root : @"D:\Elsewhere", "primary", untouched, Entry("Untouched", 'b'));
            long location, entityCount, generation;
            string entity, facts, history, untouchedBefore;
            using (var os = Open(f.Store))
            {
                location = os.Dal.ScalarLong("SELECT id FROM track_location WHERE display_path = ?;", oldPath);
                entity = os.Db.ExecScalar($"SELECT entity_id FROM track_location WHERE id = {location};")!;
                entityCount = os.Dal.ScalarLong("SELECT count(*) FROM file_entity;");
                // A path edit must preserve the complete observation, including an explicit NULL, zero, and empty string.
                os.Db.Exec($"UPDATE track_copy SET artist = NULL, album = '', smfmBpm = 0 WHERE location_id = {location};");
                os.Db.Exec($"INSERT INTO check_result(location_id,kind,result,detail,checked_utc,source) VALUES ({location},'verify','OK','keep history','2026-05-01T00:00:00.0000000Z','root-remap fixture');");
                facts = Snapshot(os.Db, "track_content", "track_copy");
                history = Snapshot(os.Db, "run", "check_result");
                untouchedBefore = Rows(os.Db, "track_location", "display_path = " + Sql(untouched));
                generation = os.Dal.Generation();
            }
            ExportCommand.Execute(new ExportOptions { StorePath = f.Store, OutPath = f.Catalog }, new ExportReport());
            if (!RunCli(f, "--fixup --remap " + Quote(oldPrefix + "=" + newPrefix), 0)) return;

            string after;
            using (var os = Open(f.Store))
            {
                f.Check(os.Dal.ScalarLong("SELECT count(*) FROM track_location WHERE state = 'present';") == 2,
                    "both entries remain present");
                f.Check(os.Db.ExecScalar($"SELECT display_path FROM track_location WHERE id = {location};") == expectedPath
                    && os.Db.ExecScalar($"SELECT state FROM track_location WHERE id = {location};") == "present",
                    "the original location has the literal destination path and stays present");
                f.Check(os.Db.ExecScalar($"SELECT entity_id FROM track_location WHERE id = {location};") == entity
                    && os.Dal.ScalarLong("SELECT count(*) FROM file_entity;") == entityCount,
                    "the explicit move preserves entity identity without making another entity");
                f.Check(Snapshot(os.Db, "track_content", "track_copy") == facts,
                    "every stored content and copy field survives, including policy-cut histogram and trackId");
                f.Check(Snapshot(os.Db, "run", "check_result") == history,
                    "run and check IDs, location references, and payloads survive unchanged");
                f.Check(Rows(os.Db, "track_location", "display_path = " + Sql(untouched)) == untouchedBefore,
                    "the unrelated location is unchanged");
                f.Check(os.Dal.Generation() == generation + 1, "one path change advances generation once");
                var entries = os.Dal.ReadEntries(os.Dal.LibraryRoots().Select(r => r.Id)).ToDictionary(e => e.DisplayPath, e => e.Entry);
                f.Check(entries.ContainsKey(expectedPath) && !entries.ContainsKey(oldPath), "reopened store reads the new key only");
                f.Check(entries.TryGetValue(expectedPath, out var moved) && moved.Features.BpmHistogram != null
                    && moved.Features.BpmHistogram.Length == 250 && moved.Features.BpmHistogram[120] == 0.75,
                    "the moved entry still has its stored histogram");
                after = Snapshot(os.Db, "library_root", "track_location", "track_content", "track_copy", "run", "check_result", "store_meta");
            }
            var catalog = new System.Collections.Concurrent.ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
            Program.LoadExistingMoodsForStore(f.Catalog, catalog);
            f.Check(catalog.Count == 2 && catalog.ContainsKey(expectedPath) && catalog.ContainsKey(untouched),
                "published catalog contains the exact moved and untouched paths");
            if (!RunCli(f, "--fixup --remap " + Quote(oldPrefix + "=" + newPrefix), 0)) return;
            using (var os = Open(f.Store))
                f.Check(Snapshot(os.Db, "library_root", "track_location", "track_content", "track_copy", "run", "check_result", "store_meta") == after,
                    "repeating the original remap does not change stored state or history");
        }

        static void MirrorRefusal(Action<bool, string> assert)
        {
            using var f = new Fixture(assert, "mirror refusal");
            using var quiet = StoreLog.Quiet();
            Seed(f.Store, @"D:\Primary", "primary", @"D:\Primary\song.flac", Entry("Primary", 'a'));
            Seed(f.Store, @"X:\Mirror", "mirror", @"X:\Mirror\song.flac", Entry("Mirror", 'a'));
            ExportCommand.Execute(new ExportOptions { StorePath = f.Store, OutPath = f.Catalog, ExportRoot = @"X:\Mirror" }, new ExportReport());
            var catalog = File.ReadAllBytes(f.Catalog);
            var sidecar = File.ReadAllBytes(Path.ChangeExtension(f.Catalog, ".mbxs"));
            string before;
            using (var os = Open(f.Store)) before = Snapshot(os.Db, "library_root", "track_location", "track_content", "track_copy", "run", "check_result", "store_meta");
            if (!RunCli(f, "--strip-smfm", 1)) return;
            f.Check(File.ReadAllBytes(f.Catalog).SequenceEqual(catalog), "the refused verb leaves the mirror catalog byte-for-byte unchanged");
            f.Check(File.ReadAllBytes(Path.ChangeExtension(f.Catalog, ".mbxs")).SequenceEqual(sidecar),
                "the refused verb leaves the mirror sidecar byte-for-byte unchanged");
            using (var os = Open(f.Store))
                f.Check(Snapshot(os.Db, "library_root", "track_location", "track_content", "track_copy", "run", "check_result", "store_meta") == before,
                    "the refusal does not change primary or mirror store facts, history, or generation");
        }

        static void Collision(Action<bool, string> assert)
        {
            using var f = new Fixture(assert, "collision");
            using var quiet = StoreLog.Quiet();
            const string source = @"D:\Music\old\song.flac", destination = @"D:\Music\new\song.flac";
            Seed(f.Store, @"D:\Music", "primary", source, Entry("Source", null));
            Seed(f.Store, @"D:\Music", "primary", destination, Entry("Destination", 'b'));
            var copies = new Dictionary<string, (long Id, string Copy)>();
            string history;
            using (var os = Open(f.Store))
            {
                foreach (var title in new[] { "Source", "Destination" })
                {
                    long id = os.Dal.ScalarLong("SELECT location_id FROM track_copy WHERE title = ?;", title);
                    copies[title] = (id, Rows(os.Db, "track_copy", "location_id = " + id));
                    os.Db.Exec($"INSERT INTO check_result(location_id,kind,result,detail,checked_utc) VALUES ({id},'verify','OK',{Sql(title)},'2026-05-01T00:00:00.0000000Z');");
                }
                history = Rows(os.Db, "run", omit: "location_id") + Rows(os.Db, "check_result", omit: "location_id");
            }
            ExportCommand.Execute(new ExportOptions { StorePath = f.Store, OutPath = f.Catalog }, new ExportReport());
            if (!RunCli(f, "--fixup --remap " + Quote(@"D:\Music\old=D:\Music\new"), 0)) return;
            // The backup is the exact catalog/order read by the verb. Both keys map to the literal destination, so the last
            // backed-up entry is the independently known winner, whatever order the publisher happened to use.
            string expectedTitle;
            using (var original = new MemoryStream())
            {
                CatalogArchive.ExtractCatalogTo(Directory.GetFiles(f.Directory, "mbxmoods.json.bak.*.zip").Single(), original);
                original.Position = 0;
                using var document = System.Text.Json.JsonDocument.Parse(original);
                expectedTitle = document.RootElement.GetProperty("tracks").EnumerateObject().Last().Value.GetProperty("title").GetString()!;
            }
            var catalog = new System.Collections.Concurrent.ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
            Program.LoadExistingMoodsForStore(f.Catalog, catalog);
            f.Check(catalog.Count == 1 && catalog.ContainsKey(destination), "one destination survives the collision");
            f.Check(catalog.TryGetValue(destination, out var winner) && winner.Features.Title == expectedTitle,
                "the later entry in the actual input catalog wins");
            var kept = copies[expectedTitle];
            using (var os = Open(f.Store))
            {
                f.Check(os.Dal.ScalarLong("SELECT id FROM track_location WHERE display_path = ?;", destination) == kept.Id,
                    "the later winner retains its original location ID");
                f.Check(Rows(os.Db, "track_copy", "location_id = " + kept.Id) == kept.Copy, "the entire winning copy stays unchanged");
                f.Check(Rows(os.Db, "run", omit: "location_id") + Rows(os.Db, "check_result", omit: "location_id") == history,
                    "all collision history IDs and payloads survive");
                f.Check(os.Dal.ScalarLong("SELECT count(*) FROM run WHERE location_id <> ?;", kept.Id) == 0
                    && os.Dal.ScalarLong("SELECT count(*) FROM check_result WHERE location_id <> ?;", kept.Id) == 0,
                    "both original copies' history references the retained location");
            }
        }

        static void DestinationRole(Action<bool, string> assert, string role, bool foreignOwner)
        {
            using var f = new Fixture(assert, "destination " + role + (foreignOwner ? " foreign owner" : ""));
            using var quiet = StoreLog.Quiet();
            Seed(f.Store, @"D:\Music", "primary", @"D:\Music\song.flac", Entry("Original primary", 'a'));
            // Canonically identical to the requested destination, but different SQL bytes: UNIQUE(machine_id,path) alone
            // cannot enforce the established same-machine role refusal for this spelling.
            Seed(f.Store, "X:/Mirror/", role, @"X:\Mirror\song.flac", Entry("Protected " + role, 'a'));
            using (var os = Open(f.Store))
            {
                if (foreignOwner)
                {
                    os.Db.Exec("INSERT INTO machine(id,hostname,created_utc) VALUES ('remap-owner','OTHER-HOST','2026-01-01T00:00:00.0000000Z');");
                    os.Db.Exec("UPDATE library_root SET machine_id = 'remap-owner';");
                }
                os.Db.Exec("UPDATE track_copy SET updated_utc = '2099-01-01T00:00:00.0000000Z' WHERE location_id IN "
                    + "(SELECT l.id FROM track_location l JOIN library_root r ON r.id = l.root_id WHERE r.role <> 'primary');");
                os.Db.Exec("INSERT INTO check_result(location_id,kind,result,detail,checked_utc) "
                    + "SELECT id,'verify','OK',display_path,'2026-05-01T00:00:00.0000000Z' FROM track_location;");
            }
            ExportCommand.Execute(new ExportOptions { StorePath = f.Store, OutPath = f.Catalog }, new ExportReport());
            string before;
            using (var os = Open(f.Store))
                before = Snapshot(os.Db, "machine", "library_root", "track_location", "file_entity", "identity_facet",
                    "track_content", "track_copy", "run", "check_result", "verdict", "store_meta");
            // Check preservation even when the expected refusal assertion fails, so the red result shows the lost copy/history too.
            RunCli(f, "--fixup --remap " + Quote(@"D:\Music=x:\mirror"), 1);
            using (var os = Open(f.Store))
                f.Check(Snapshot(os.Db, "machine", "library_root", "track_location", "file_entity", "identity_facet",
                    "track_content", "track_copy", "run", "check_result", "verdict", "store_meta") == before,
                    "the destination role refusal preserves both roots, every copy, all history, and generation");
        }

        static void RetiredOuterDestination(Action<bool, string> assert)
        {
            using var f = new Fixture(assert, "retired outer destination");
            using var quiet = StoreLog.Quiet();
            const string source = @"D:\Old\song.flac", destination = @"E:\Library\sub\song.flac", stay = @"E:\Library\stay.flac";
            Seed(f.Store, @"D:\Old", "primary", source, Entry("Explicit source", 'a'));
            Seed(f.Store, @"E:\Library", "primary", destination, Entry("Retired destination", 'b'));
            Seed(f.Store, @"E:\Library", "primary", stay, Entry("Untouched", 'c'));
            long sourceId, retiredId; string sourceCopy, content, history;
            using (var os = Open(f.Store))
            {
                sourceId = os.Dal.ScalarLong("SELECT id FROM track_location WHERE display_path = ?;", source);
                retiredId = os.Dal.ScalarLong("SELECT id FROM track_location WHERE display_path = ?;", destination);
                os.Db.Exec($"UPDATE track_location SET state = 'retired' WHERE id = {retiredId};");
                os.Db.Exec($"UPDATE track_copy SET updated_utc = '2099-01-01T00:00:00.0000000Z' WHERE location_id = {retiredId};");
                os.Db.Exec($"INSERT INTO check_result(location_id,kind,result,detail,checked_utc,source) "
                    + $"VALUES ({retiredId},'verify','OK','retain this history','2026-05-01T00:00:00.0000000Z','retired-target');");
                sourceCopy = Rows(os.Db, "track_copy", "location_id = " + sourceId);
                content = Snapshot(os.Db, "track_content");
                history = Rows(os.Db, "run", omit: "location_id") + Rows(os.Db, "check_result", omit: "location_id");
            }
            ExportCommand.Execute(new ExportOptions { StorePath = f.Store, OutPath = f.Catalog }, new ExportReport());
            if (!RunCli(f, "--fixup --remap " + Quote(@"D:\Old=E:\Library\sub"), 0)) return;
            using (var os = Open(f.Store))
            {
                f.Check(os.Dal.ScalarLong("SELECT id FROM track_location WHERE display_path = ? AND state = 'present';", destination) == sourceId,
                    "the explicit source stays present with its original ID under the surviving outer root");
                f.Check(Rows(os.Db, "track_copy", "location_id = " + sourceId) == sourceCopy,
                    "a newer retired destination never replaces or supplies the explicit source copy");
                f.Check(Snapshot(os.Db, "track_content") == content
                    && Rows(os.Db, "run", omit: "location_id") + Rows(os.Db, "check_result", omit: "location_id") == history,
                    "all content and history payloads survive the destination collision");
                f.Check(os.Dal.ScalarLong("SELECT location_id FROM check_result WHERE source = 'retired-target';") == sourceId
                    && os.Dal.ScalarLong("SELECT count(*) FROM run WHERE location_id = ?;", retiredId) == 0,
                    "the retired destination history is reparented to the explicit winner");
            }
            var catalog = new System.Collections.Concurrent.ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
            Program.LoadExistingMoodsForStore(f.Catalog, catalog);
            f.Check(catalog.Count == 2 && catalog.TryGetValue(destination, out var moved) && moved.Features.Title == "Explicit source"
                && catalog.ContainsKey(stay), "the explicit source and untouched entry remain published");
        }

        static void Overlap(Action<bool, string> assert, bool rollback)
        {
            using var f = new Fixture(assert, rollback ? "overlap rollback" : "source overlap");
            using var quiet = StoreLog.Quiet();
            const string first = @"D:\Music\sub\song.flac", second = @"D:\Music\sub\child\song.flac",
                firstAfter = @"D:\Music\sub\child\song.flac", secondAfter = @"D:\Music\sub\child\child\song.flac";
            Seed(f.Store, @"D:\Music", "primary", first, Entry("First", null));
            Seed(f.Store, @"D:\Music", "primary", second, Entry("Second", null));
            ExportCommand.Execute(new ExportOptions { StorePath = f.Store, OutPath = f.Catalog }, new ExportReport());
            long firstId, secondId; string before, facts;
            using (var os = Open(f.Store))
            {
                firstId = os.Dal.ScalarLong("SELECT id FROM track_location WHERE display_path = ?;", first);
                secondId = os.Dal.ScalarLong("SELECT id FROM track_location WHERE display_path = ?;", second);
                if (rollback)
                    os.Db.Exec("CREATE TRIGGER reject_remap BEFORE UPDATE OF display_path ON track_location WHEN "
                        + $"EXISTS(SELECT 1 FROM track_location WHERE (id = {firstId} AND display_path = {Sql(firstAfter)}) "
                        + $"OR (id = {secondId} AND display_path = {Sql(secondAfter)})) "
                        + "BEGIN SELECT RAISE(ABORT, 'fixture remap rollback'); END;");
                before = Snapshot(os.Db, "library_root", "track_location", "track_content", "track_copy", "run", "check_result", "store_meta");
                facts = Snapshot(os.Db, "track_content", "track_copy", "run", "check_result");
            }
            if (!RunCli(f, "--fixup --remap " + Quote(@"D:\Music\sub=D:\Music\sub\child"), rollback ? 1 : 0)) return;
            using (var os = Open(f.Store))
            {
                if (rollback)
                    f.Check(Snapshot(os.Db, "library_root", "track_location", "track_content", "track_copy", "run", "check_result", "store_meta") == before,
                        "failure after the first final path update rolls back the entire remap");
                else
                {
                    f.Check(os.Db.ExecScalar($"SELECT display_path FROM track_location WHERE id = {firstId};") == firstAfter
                        && os.Db.ExecScalar($"SELECT display_path FROM track_location WHERE id = {secondId};") == secondAfter,
                        "each source moves to its own destination without replacing the other source");
                    f.Check(Snapshot(os.Db, "track_content", "track_copy", "run", "check_result") == facts,
                        "overlapping moves keep both original copies, content, and history");
                }
            }
            var catalog = new System.Collections.Concurrent.ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
            Program.LoadExistingMoodsForStore(f.Catalog, catalog);
            f.Check(catalog.Count == 2 && catalog.ContainsKey(rollback ? first : firstAfter) && catalog.ContainsKey(rollback ? second : secondAfter),
                "the published paths agree with the committed or rolled-back store");
        }

        static TrackEntry Entry(string title, char? hash)
        {
            var histogram = new double[250]; histogram[120] = 0.75;
            return new TrackEntry
            {
                LastModified = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                AudioStreamSha256 = hash.HasValue ? new string(hash.Value, 64) : null,
                AudioStreamSha256Source = hash.HasValue ? "flac-frames" : null,
                FileMd5 = "0123456789abcdef0123456789abcdef", AnalysisDurationSecs = 1.25,
                Features = new TrackFeatures { Artist = "Artist", Title = title, TrackId = 42, Bpm = 120,
                    Key = "C", Mode = "major", BpmHistogram = histogram, SmfmScores = new[] { 1, 2, 3 } },
            };
        }

        static void Seed(string store, string root, string role, string path, TrackEntry entry)
            => ImportCommand.ExecuteEntries(store, new Dictionary<string, TrackEntry>(PathComparer.Instance) { [path] = entry },
                root, role, true, "essentia", "root-remap-selftest", "fixture:" + path, null, null);
        static OpenStore Open(string store) => OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o"));

        static bool RunCli(Fixture f, string verb, int expectedExit)
        {
            var start = new ProcessStartInfo
            {
                FileName = typeof(Program).Assembly.Location, WorkingDirectory = f.Directory,
                Arguments = "--no-config --store " + Quote(f.Store) + " --moods " + Quote(f.Catalog) + " " + verb,
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the remap test child.");
            var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(30000))
            {
                f.Keep = true; f.Check(false, "child did not exit within 30 seconds; process and fixture left intact"); return false;
            }
            var output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(f.Directory, "cli-" + (++f.Calls) + ".log"), output);
            f.Check(child.ExitCode == expectedExit, $"CLI exits {expectedExit} (got {child.ExitCode})");
            return child.ExitCode == expectedExit;
        }

        // Windows command-line quoting doubles trailing backslashes before the closing quote.
        static string Quote(string value) => "\"" + value + new string('\\', value.Length - value.TrimEnd('\\').Length) + "\"";
        static string Sql(string value) => "'" + value.Replace("'", "''") + "'";
        static string Snapshot(SqliteDb db, params string[] tables) => string.Concat(tables.Select(t => Rows(db, t)));
        static string Rows(SqliteDb db, string table, string? where = null, string? omit = null)
        {
            var columns = new List<string>();
            using (var info = db.Prepare("PRAGMA table_info(" + Sql(table) + ");"))
                while (info.Step()) if (info.GetString(1) != omit) columns.Add(info.GetString(1)!);
            string Field(string c) => "quote(\"" + c.Replace("\"", "\"\"") + "\")";
            var expression = string.Join(" || '|' || ", columns.Select(c => "length(" + Field(c) + ") || ':' || " + Field(c)));
            var result = new StringBuilder(table + "\n");
            using var rows = db.Prepare("SELECT " + expression + " FROM \"" + table + "\"" + (where == null ? "" : " WHERE " + where) + " ORDER BY 1;");
            while (rows.Step()) { string row = rows.GetString(0)!; result.Append(row.Length).Append(':').Append(row).Append('\n'); }
            return result.ToString();
        }

        static void Block(Action<bool, string> assert, string label, Action action)
        { try { action(); } catch (Exception ex) { assert(false, label + ": threw " + ex.GetType().Name + ": " + ex.Message); } }
        sealed class Fixture : IDisposable
        {
            readonly Action<bool, string> _assert; readonly string _name;
            internal readonly string Directory = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "root-remap-" + Guid.NewGuid().ToString("N"));
            internal bool Keep; internal int Calls;
            internal string Store => Path.Combine(Directory, "truedat.db");
            internal string Catalog => Path.Combine(Directory, "mbxmoods.json");
            internal Fixture(Action<bool, string> assert, string name)
            { _assert = assert; _name = name; System.IO.Directory.CreateDirectory(Directory); }
            internal void Check(bool condition, string message)
            { if (!condition) Keep = true; _assert(condition, "RootRemap " + _name + ": " + message + (condition ? "" : "; fixture: " + Directory)); }
            public void Dispose() { if (!Keep) try { System.IO.Directory.Delete(Directory, true); } catch { } }
        }
    }
}
