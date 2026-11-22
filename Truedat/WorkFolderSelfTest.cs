using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Truedat
{
    /// <summary>
    /// The work folder (<see cref="WorkFolder"/>): the rule that names it, which files move and which stay with the catalog,
    /// what a writing run does to files already beside the catalog, what a reading run leaves alone, and every site that now
    /// writes or reads a working file there — the store-result record, the catalog and store backups, the import of the
    /// ledgers, the reset's file set, and <c>--paths</c>.
    /// </summary>
    internal static class WorkFolderSelfTest
    {
        /// <summary>The arguments for a self-test's child truedat: <c>--work-dir &lt;the folder it runs in&gt;</c> first, so the child keeps
        /// the layout the test checks (working files beside the catalog it built) and writes nothing under the build output — the
        /// default rule would put a child's working files in &lt;exe&gt;\libraries\&lt;id&gt;, beside the test binary, one folder per
        /// temp library, kept forever. Any other program (ffmpeg, the extractor) is started with its arguments untouched.</summary>
        internal static string ChildArgs(string exe, string cwd, string args)
            => string.Equals(Path.GetFileName(exe), "truedat.exe", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(cwd)
                ? "--" + WorkFolder.Flag + " \"" + cwd.TrimEnd('\\') + "\" " + args
                : args;

        internal static void Run(Action<bool, string> assert)
        {
            Console.WriteLine("Work folder self-test (truedat's working files leave the catalog folder)");
            bool seam = WorkFolder.UseCatalogFolderForTest;
            var hook = WorkFolder.BeginOnFirstUse;
            var root = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "workfolder-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                WorkFolder.UseCatalogFolderForTest = false;
                WorkFolder.BeginOnFirstUse = null;
                Rule(assert);
                Table(assert);
                Reading(assert, Path.Combine(root, "reading"));
                Writing(assert, Path.Combine(root, "writing"));
                Collisions(assert, Path.Combine(root, "collide"));
                Fallback(assert, Path.Combine(root, "fallback"));
                FirstUse(assert, Path.Combine(root, "firstuse"));
                Record(assert, Path.Combine(root, "record"));
                Backups(assert, Path.Combine(root, "backups"));
                StoreBackups(assert, Path.Combine(root, "storebak"));
                Reset(assert, Path.Combine(root, "reset"));
                Import(assert, Path.Combine(root, "import"));
                Paths(assert, Path.Combine(root, "paths"));
                Size(assert, Path.Combine(root, "size"));
                Names(assert, Path.Combine(root, "names"));
                DriveRoots(assert, Path.Combine(root, "roots"));
                CustomCatalog(assert, Path.Combine(root, "custom"));
                CrossVolumeRace(assert, Path.Combine(root, "race"));
                Alias(assert, Path.Combine(root, "alias"));
                ReadOnlyLater(assert, Path.Combine(root, "readonly"));
                Owner(assert, Path.Combine(root, "owner"));
                Settings(assert);
            }
            catch (Exception ex) { assert(false, $"work folder: threw {ex.GetType().Name}: {ex.Message}"); }
            finally
            {
                WorkFolder.UseCatalogFolderForTest = seam;
                WorkFolder.BeginOnFirstUse = hook;
                WorkFolder.Configure(null, AppDomain.CurrentDomain.BaseDirectory);
                global::Truedat.Store.StoreResult.ResetScanForTest();
                try { Directory.Delete(root, true); } catch { }
            }
        }

        /// <summary>The usual install, the exe in the library itself, the exe elsewhere for two libraries, and the operator's choice.</summary>
        static void Rule(Action<bool, string> assert)
        {
            const string lib = @"D:\Library", other = @"E:\Other Library", tools = @"C:\Tools\truedat";
            string Id(string dir) => global::Truedat.Store.StoreLocation.LibraryId(dir);
            assert(WorkFolder.Resolve(lib, null, @"D:\Library\truedat") == @"D:\Library\truedat",
                "work rule: the usual install (Library\\truedat\\truedat.exe) works in the exe's folder");
            assert(WorkFolder.Resolve(lib, null, lib) == lib, "work rule: an exe in the library folder itself works there");
            var a = WorkFolder.Resolve(lib, null, tools);
            var b = WorkFolder.Resolve(other, null, tools);
            assert(a == Path.Combine(tools, "libraries", Id(lib)), $"work rule: an exe elsewhere works in <exe>\\libraries\\<library id> ({a})");
            assert(a != b && b == Path.Combine(tools, "libraries", Id(other)), $"work rule: two libraries served by one exe get two folders ({a} / {b})");
            assert(WorkFolder.Resolve(lib, null, @"D:\Library\apps\truedat") == Path.Combine(@"D:\Library\apps\truedat", "libraries", Id(lib)),
                "work rule: only a DIRECT child of the catalog folder counts as installed in the library");
            assert(WorkFolder.Resolve(lib, @"F:\work", @"D:\Library\truedat") == @"F:\work", "work rule: --work-dir is used as given, even over the usual install");
            assert(WorkFolder.Resolve(@"d:\library\", null, @"D:\LIBRARY\truedat\") == @"D:\LIBRARY\truedat",
                "work rule: case and a trailing separator do not change the answer");
            assert(Id(@"D:\Library") == Id(@"d:\library\"), "work rule: the library id is the store's (case-folded, trailing separator trimmed)");
        }

        /// <summary>Every file family truedat writes, by name: the working files move, the hub's and the operator's stay.</summary>
        static void Table(Action<bool, string> assert)
        {
            bool Moves(string n) => WorkFolder.IsWorkingFile(n, "mbxmoods.json", "mbxmoods.mbxs", global::Truedat.Edges.EdgeCompanion.FileName, "truedat.db");
            foreach (var n in new[]
            {
                "mbxmoods-errors.csv", "mbxmoods-errors.log", "mbxmoods-errors.host7.csv", "mbxmoods-errors.host7.log", "mbxmoods-skipped.csv",
                "mbxmoods-skipped.host7.csv", "mbxmoods-scan-2026-11-21-1.txt", "truedat.log", "mbxmoods.json.bak.20261121.010203.zip",
                "mbxmoods.json.bak.20261121.010203-2.zip", "mbxmoods.json.imported.c9458a2dafbe6763.zip", "mbxmoods.json.retired.20261121.010203.zip",
                "mbxmoods.json.damaged.20261121.010203.zip", "truedat.db.bak.v4.20261121-010203", "truedat.db.bak.roots.20261121-010203",
                "truedat.db.bak.20261121.010203.db", "truedat.db.bak.remap.20261121-010203", "mbxmoods-verify.csv", "mbxmoods-verify.host7.1of2.csv",
                "mbxmoods-verify.receipt.json", "mbxmoods-speech.csv", "mbxmoods-smfm.csv", "mbxmoods-smfm-missing.csv", "mbxmoods-formats.csv",
                "mbxmoods-store-disagree.csv", "mbxmoods-snapshot-20261121.010203.zip", "mbxmoods-reset-20261121-010203.zip",
                "mbxmoods-errors.csv.moved-20261121-010203",
            })
                assert(Moves(n), $"work table: {n} is a working file and moves");
            foreach (var n in new[]
            {
                "mbxmoods.json", "mbxmoods.json.1", "mbxmoods.json.12", "mbxmoods.mbxs", "mbxedges.v1.json", "truedat.db", "truedat.db-wal", "truedat.db-shm",
                "store-result.json", "store-import-result.json", "store-info.json", "mbxmoods-exclude.json", "mbxmoods-exclude.json.bak.20261121.010203",
                "mbxmoods-exclude.m3u8", "apply-result.json", "mbxmoods-schema.json", "mbxmoods-review.json", "mbxmoods-preview.html",
                // A --chunk shard's review ledger: --merge-moods folds it from beside the shard's catalog.
                "mbxmoods-review.host7.json",
                // A --chunk shard's catalog and sidecar: --merge-moods sources, never another run's working files.
                "mbxmoods.host7.json", "mbxmoods.host7.mbxs",
                // A write in progress belongs to the run writing it: a --restore's staged catalog, a save's temp file.
                "mbxmoods.json.restore.tmp", "mbxmoods.json.tmp", "mbxmoods-errors.csv.tmp",
                "mbxmoods-duplicates.json", "mbxmoods-duplicates.csv", "mbxmoods-duplicates.html", "mbxmoods-duplicates.manifest.json",
                "mbxmoods-duplicates.verdicts.json", "mbxmoods-duplicate-losers.m3u8", "iTunes Music Library.xml", "truedat.exe",
                "truedat.config.json", "desktop.ini", "song.mp3",
            })
                assert(!Moves(n), $"work table: {n} stays with the catalog");
            assert(WorkFolder.IsCatalogPart("custom.json.3", "custom.json") && !WorkFolder.IsCatalogPart("custom.json.bak.x.zip", "custom.json")
                   && !WorkFolder.IsCatalogPart("custom.json.", "custom.json"),
                "work table: a numbered part is <catalog>.<digits> whatever the catalog is called, and nothing else");
        }

        /// <summary>A reading run moves nothing and creates no folder, and finds a file beside the catalog where it always was.</summary>
        static void Reading(Action<bool, string> assert, string root)
        {
            var lib = Path.Combine(root, "Library");
            var exe = Path.Combine(root, "Tools", "truedat");
            Directory.CreateDirectory(lib);
            File.WriteAllText(Path.Combine(lib, "mbxmoods.json"), "{\"tracks\":{}}");
            var beside = Path.Combine(lib, "mbxmoods-errors.csv");
            File.WriteAllText(beside, "FilePath,Error\r\n");
            WorkFolder.Configure(null, exe);
            WorkFolder.BeginOnFirstUse = null;
            var work = WorkFolder.For(lib);
            assert(WorkFolder.Read(lib, "mbxmoods-errors.csv") == beside, "work reading: a file only beside the catalog is read from there");
            assert(WorkFolder.Files(lib, "mbxmoods-errors*").SequenceEqual(new[] { beside }), "work reading: and is listed from there");
            assert(!Directory.Exists(work) && !Directory.Exists(exe), $"work reading: a reading run creates no folder ({work})");
            assert(File.Exists(beside), "work reading: and moves nothing");
            assert(WorkFolder.Current == null, "work reading: nothing is settled until a run settles it");
        }

        /// <summary>The first writing run on an upgraded install: the working files move, the hub's and the operator's stay,
        /// bytes and timestamps intact; a held file stays and is still read; a second run moves nothing.</summary>
        static void Writing(Action<bool, string> assert, string root)
        {
            var lib = Path.Combine(root, "Library");
            var exe = Path.Combine(lib, "truedat");
            Directory.CreateDirectory(exe);
            var stays = new[] { "mbxmoods.json", "mbxmoods.mbxs", "mbxedges.v1.json", "truedat.db", "store-result.json", "mbxmoods-exclude.json",
                                "apply-result.json", "mbxmoods-duplicates.json", "mbxmoods-duplicates.verdicts.json", "iTunes Music Library.xml" };
            var moves = new[] { "mbxmoods-errors.csv", "mbxmoods-errors.log", "mbxmoods-skipped.csv", "mbxmoods-scan-2026-11-20-1.txt",
                                "mbxmoods.json.bak.20261120.010203.zip", "truedat.db.bak.v4.20261120-010203" };
            var when = new DateTime(2026, 11, 20, 1, 2, 3, DateTimeKind.Utc);
            foreach (var n in stays.Concat(moves).Concat(new[] { "truedat.log" }))
            {
                File.WriteAllText(Path.Combine(lib, n), "content of " + n);
                File.SetLastWriteTimeUtc(Path.Combine(lib, n), when);
            }
            WorkFolder.Configure(null, exe);
            var work = WorkFolder.Begin(lib, out var note);
            assert(work == exe && note == null && WorkFolder.Current == exe, $"work move: the usual install settles the exe's folder ({work})");
            using (new FileStream(Path.Combine(lib, "truedat.log"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var r = WorkFolder.MoveExisting(Path.Combine(lib, "mbxmoods.json"), Path.Combine(lib, "truedat.db"));
                assert(r.Moved == moves.Length, $"work move: every working file moved ({r.Moved} of {moves.Length})");
                assert(r.Held.SequenceEqual(new[] { "truedat.log" }), $"work move: a file another program holds is left and named ({string.Join(",", r.Held)})");
                var line = WorkFolder.Describe(r) ?? "";
                assert(line.Contains(exe) && line.Contains("truedat.log") && line.Contains(moves.Length.ToString()), $"work move: one line says how many and where ({line})");
                assert(WorkFolder.Read(lib, "truedat.log") == Path.Combine(lib, "truedat.log"), "work move: the held file is still read from where it is");
            }
            foreach (var n in moves)
            {
                var moved = Path.Combine(exe, n);
                assert(File.Exists(moved) && !File.Exists(Path.Combine(lib, n)) && File.ReadAllText(moved) == "content of " + n
                       && File.GetLastWriteTimeUtc(moved) == when, $"work move: {n} is in the work folder, same bytes, same time, and gone from the catalog folder");
            }
            foreach (var n in stays)
                assert(File.Exists(Path.Combine(lib, n)) && !File.Exists(Path.Combine(exe, n)), $"work move: {n} stays beside the catalog");
            var again = WorkFolder.MoveExisting(Path.Combine(lib, "mbxmoods.json"), null);
            assert(again.Moved == 1 && again.Held.Count == 0 && File.Exists(Path.Combine(exe, "truedat.log")),
                $"work move: the next run carries over the file that was held, now that it is free ({again.Moved} moved, {again.Held.Count} held)");
            var third = WorkFolder.MoveExisting(Path.Combine(lib, "mbxmoods.json"), null);
            assert(third.Moved == 0 && third.Held.Count == 0 && WorkFolder.Describe(third) == null, $"work move: after that there is nothing to move and nothing to say ({third.Moved})");
            assert(WorkFolder.Read(lib, "mbxmoods-errors.csv") == Path.Combine(exe, "mbxmoods-errors.csv"), "work move: afterwards the ledger is read from the work folder");
        }

        /// <summary>A name in both folders: the newer file is the live one, the other is kept beside it, nothing is written over.</summary>
        static void Collisions(Action<bool, string> assert, string root)
        {
            foreach (bool catalogNewer in new[] { true, false })
            {
                var lib = Path.Combine(root, catalogNewer ? "a" : "b", "Library");
                var exe = Path.Combine(lib, "truedat");
                Directory.CreateDirectory(exe);
                File.WriteAllText(Path.Combine(lib, "mbxmoods.json"), "{}");
                var inCatalog = Path.Combine(lib, "mbxmoods-skipped.csv");
                var inWork = Path.Combine(exe, "mbxmoods-skipped.csv");
                File.WriteAllText(inCatalog, "catalog copy"); File.WriteAllText(inWork, "work copy");
                var older = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc); var newer = older.AddDays(1);
                File.SetLastWriteTimeUtc(inCatalog, catalogNewer ? newer : older); File.SetLastWriteTimeUtc(inWork, catalogNewer ? older : newer);
                WorkFolder.Configure(null, exe);
                WorkFolder.Begin(lib, out _);
                var r = WorkFolder.MoveExisting(Path.Combine(lib, "mbxmoods.json"), null);
                var aside = Directory.GetFiles(exe, "mbxmoods-skipped.csv" + WorkFolder.MovedSuffix + "*");
                string live = catalogNewer ? "catalog copy" : "work copy", kept = catalogNewer ? "work copy" : "catalog copy";
                assert(File.ReadAllText(inWork) == live, $"work collide ({(catalogNewer ? "catalog" : "work")} newer): the newer file is the live one");
                assert(aside.Length == 1 && File.ReadAllText(aside[0]) == kept, $"work collide ({(catalogNewer ? "catalog" : "work")} newer): the other is kept beside it as .moved-<time>");
                assert(!File.Exists(inCatalog) && r.Renamed.Count == 1 && (WorkFolder.Describe(r) ?? "").Contains("both folders"),
                    $"work collide ({(catalogNewer ? "catalog" : "work")} newer): the catalog folder is clear and the run says so");
            }
        }

        /// <summary>A work folder that cannot be written: the catalog folder, today's behaviour, and one line naming both.</summary>
        static void Fallback(Action<bool, string> assert, string root)
        {
            var lib = Path.Combine(root, "Library");
            Directory.CreateDirectory(lib);
            File.WriteAllText(Path.Combine(lib, "mbxmoods.json"), "{}");
            File.WriteAllText(Path.Combine(lib, "mbxmoods-errors.csv"), "x");
            var blocker = Path.Combine(root, "blocker");
            File.WriteAllText(blocker, "a file where the folder would be");
            var wanted = Path.Combine(blocker, "work");
            WorkFolder.Configure(wanted, Path.Combine(root, "Tools"));
            var used = WorkFolder.Begin(lib, out var note);
            assert(used == lib && WorkFolder.Current == lib, $"work fallback: an unwritable work folder falls back to the catalog folder ({used})");
            assert(note != null && note.Contains(wanted) && note.Contains(lib), $"work fallback: one line names both folders ({note})");
            var r = WorkFolder.MoveExisting(Path.Combine(lib, "mbxmoods.json"), null);
            assert(r.Moved == 0 && File.Exists(Path.Combine(lib, "mbxmoods-errors.csv")), "work fallback: nothing moves");
            assert(WorkFolder.Write(lib, "truedat.log") == Path.Combine(lib, "truedat.log"), "work fallback: a working file is written beside the catalog, as before");
        }

        /// <summary>A writing run settles on the first use of a working-file path; the hook runs once per catalog folder.</summary>
        static void FirstUse(Action<bool, string> assert, string root)
        {
            var lib = Path.Combine(root, "Library");
            var exe = Path.Combine(root, "Tools", "truedat");
            Directory.CreateDirectory(lib);
            File.WriteAllText(Path.Combine(lib, "mbxmoods.json"), "{}");
            File.WriteAllText(Path.Combine(lib, "mbxmoods-skipped.csv"), "x");
            WorkFolder.Configure(null, exe);
            int calls = 0;
            WorkFolder.BeginOnFirstUse = dir => { calls++; WorkFolder.Begin(dir, out _); WorkFolder.MoveExisting(Path.Combine(dir, "mbxmoods.json"), null); };
            try
            {
                var p = WorkFolder.Read(lib, "mbxmoods-skipped.csv");
                var work = WorkFolder.For(lib);
                assert(calls == 1 && p == Path.Combine(work, "mbxmoods-skipped.csv") && File.Exists(p), $"work first use: the first path asked for settles and moves first ({calls}, {p})");
                WorkFolder.Write(lib, "truedat.log"); WorkFolder.Files(lib, "*");
                assert(calls == 1, $"work first use: once per catalog folder ({calls})");
                var w = WorkFolder.Write(lib, "mbxmoods-scan-2026-11-21-1.txt");
                assert(Path.GetDirectoryName(w) == work && Directory.Exists(work), "work first use: a write lands in a folder that exists");
            }
            finally { WorkFolder.BeginOnFirstUse = null; }
        }

        /// <summary>store-result.json names the work folder (hub contract): store-backed, no store, a list scan, after a
        /// fallback; and leaves the field out when none was settled.</summary>
        static void Record(Action<bool, string> assert, string root)
        {
            var lib = Path.Combine(root, "Library");
            var exe = Path.Combine(lib, "truedat");
            Directory.CreateDirectory(exe);
            var catalog = Path.Combine(lib, "mbxmoods.json");
            File.WriteAllText(catalog, "{}");
            var resultPath = Path.Combine(lib, global::Truedat.Store.StoreResultFile.FileName);
            const string started = "2026-11-21T00:00:00.0000000Z";
            global::Truedat.Store.StoreResultFile Final()
            {
                global::Truedat.Store.StoreResult.FinishScan(0);
                global::Truedat.Store.StoreResult.TryRead(resultPath, out var rec);
                return rec;
            }

            WorkFolder.Configure(null, exe);
            global::Truedat.Store.StoreResult.ResetScanForTest();
            global::Truedat.Store.StoreResult.BeginScanWithoutStore(catalog, null, started);
            var unresolved = Final();
            assert(unresolved.WorkFolder == null && !File.ReadAllText(resultPath).Contains("\"workFolder\""),
                "work record: with no work folder settled the field is absent, not empty and not null");

            WorkFolder.Begin(lib, out _);
            global::Truedat.Store.StoreResult.BeginScan(Path.Combine(lib, "truedat.db"), started);
            assert(Final().WorkFolder == exe, "work record: a store-backed scan's record names the work folder");
            global::Truedat.Store.StoreResult.BeginScanWithoutStore(catalog, null, started);
            assert(Final().WorkFolder == exe, "work record: a scan with no store names it too");
            global::Truedat.Store.StoreResult.BeginScanWithoutStore(catalog, null, started);
            global::Truedat.Store.StoreResult.BeginList("-", 3);
            var list = Final();
            assert(list.WorkFolder == exe && list.List != null, "work record: and so does a list scan's");

            var blocker = Path.Combine(root, "blocker"); File.WriteAllText(blocker, "x");
            WorkFolder.Configure(Path.Combine(blocker, "work"), exe);
            WorkFolder.Begin(lib, out _);
            global::Truedat.Store.StoreResult.BeginScanWithoutStore(catalog, null, started);
            assert(Final().WorkFolder == lib, "work record: after a fallback it names the folder actually used, the catalog folder");
        }

        /// <summary>The catalog's backups, the pinned import, the retired publication: in the work folder; rotation counts both folders.</summary>
        static void Backups(Action<bool, string> assert, string root)
        {
            var lib = Path.Combine(root, "Library");
            var exe = Path.Combine(lib, "truedat");
            Directory.CreateDirectory(exe);
            var catalog = Path.Combine(lib, "mbxmoods.json");
            File.WriteAllText(catalog, "{\"tracks\":{}}");
            WorkFolder.Configure(null, exe);
            WorkFolder.Begin(lib, out _);

            var bak = Program.BackupCatalogCompressed(catalog);
            assert(Path.GetDirectoryName(bak) == exe, $"work backups: a catalog backup is written to the work folder ({bak})");
            var pin = CatalogArchive.PinImported(catalog, new string('a', 64));
            assert(pin != null && Path.GetDirectoryName(pin) == exe, $"work backups: the pinned import goes there too ({pin})");
            File.WriteAllText(Path.ChangeExtension(catalog, ".mbxs"), "sidecar");
            var retired = global::Truedat.Store.StoreScan.RetirePublishedFiles(catalog, archive: true);
            assert(retired != null && Path.GetDirectoryName(retired) == exe && !File.Exists(catalog), $"work backups: and the retired publication ({retired})");

            // Rotation across both folders: two older backups beside the catalog (written before the move), two in the work folder.
            File.WriteAllText(catalog, "{\"tracks\":{}}");
            foreach (var f in Directory.GetFiles(exe, "mbxmoods.json.bak.*")) File.Delete(f);
            var stamps = new[] { (lib, "20260101.000000"), (lib, "20260102.000000"), (exe, "20260103.000000"), (exe, "20260104.000000") };
            foreach (var (dir, stamp) in stamps) CatalogArchive.WriteZipBackup(catalog, Path.Combine(dir, $"mbxmoods.json.bak.{stamp}.zip"));
            int removed = CatalogArchive.RotateBackups(catalog, 2);
            var left = WorkFolder.Files(lib, "mbxmoods.json.bak.*.zip").Select(Path.GetFileName).OrderBy(n => n).ToArray();
            assert(removed == 2 && left.SequenceEqual(new[] { "mbxmoods.json.bak.20260103.000000.zip", "mbxmoods.json.bak.20260104.000000.zip" }),
                $"work backups: rotation counts the backups in both folders and keeps the newest (removed {removed}; left {string.Join(",", left)})");
        }

        /// <summary>A store beside the catalog backs up into the work folder; a store kept elsewhere backs up beside itself.</summary>
        static void StoreBackups(Action<bool, string> assert, string root)
        {
            var lib = Path.Combine(root, "Library");
            var exe = Path.Combine(lib, "truedat");
            var elsewhere = Path.Combine(root, "Stores");
            Directory.CreateDirectory(exe); Directory.CreateDirectory(elsewhere);
            File.WriteAllText(Path.Combine(lib, "mbxmoods.json"), "{}");
            WorkFolder.Configure(null, exe);
            WorkFolder.Begin(lib, out _);
            string Backup(string store)
            {
                using (var db = global::Truedat.Store.SqliteDb.Open(store, create: true))
                {
                    db.Exec("CREATE TABLE IF NOT EXISTS t(x);");
                    return global::Truedat.Store.SchemaMigrator.BackupBeside(db, "v4");
                }
            }
            var beside = Backup(Path.Combine(lib, "truedat.db"));
            assert(Path.GetDirectoryName(beside) == exe && File.Exists(beside), $"work store backups: a store beside the catalog backs up into the work folder ({beside})");
            var own = Backup(Path.Combine(elsewhere, "truedat.db"));
            assert(Path.GetDirectoryName(own) == elsewhere && !Directory.GetFiles(elsewhere, "*").Any(f => f.Contains(".truedat-work-probe")),
                $"work store backups: a store in its own folder keeps its backups beside it ({own})");
            var folders = WorkFolder.BackupFolders(Path.Combine(lib, "truedat.db"));
            assert(folders.SequenceEqual(new[] { exe, lib }), $"work store backups: rotation reads the work folder, then the store's own ({string.Join(" | ", folders)})");
        }

        /// <summary>The reset still archives every file truedat wrote for the library — now from two folders — and keeps what IsKept names.</summary>
        static void Reset(Action<bool, string> assert, string root)
        {
            var lib = Path.Combine(root, "Library");
            var exe = Path.Combine(lib, "truedat");
            Directory.CreateDirectory(exe);
            var catalog = Path.Combine(lib, "mbxmoods.json");
            File.WriteAllText(catalog, "{}");
            foreach (var n in new[] { "mbxmoods.mbxs", "store-result.json", "mbxmoods-exclude.json", "mbxmoods-duplicates.json" }) File.WriteAllText(Path.Combine(lib, n), n);
            foreach (var n in new[] { "mbxmoods-errors.csv", "truedat.log", "mbxmoods.json.bak.20260101.000000.zip", "mbxmoods-snapshot-20260101.000000.zip",
                                      "mbxmoods-reset-20250101-000000.zip", "mbxmoods-schema.json", "truedat.config.json", "truedat.exe" })
                File.WriteAllText(Path.Combine(exe, n), n);
            File.WriteAllText(Path.Combine(lib, "mbxmoods-skipped.csv"), "held over from before the move");
            WorkFolder.Configure(null, exe);
            WorkFolder.Begin(lib, out _);
            var files = ResetCommand.Collect(ResetCommand.Verb.Clear, catalog, null, null);
            var entries = files.Select(f => f.EntryName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            string[] want = { "mbxmoods-duplicates.json", "mbxmoods-skipped.csv", "mbxmoods.json", "mbxmoods.mbxs", "store-result.json",
                              "work/mbxmoods-errors.csv", "work/mbxmoods.json.bak.20260101.000000.zip", "work/truedat.log" };
            assert(entries.SequenceEqual(want), $"work reset: the file set spans both folders, the work folder's under work/ ({string.Join(", ", entries)})");
            assert(files.All(f => File.Exists(f.Path)), "work reset: every entry names a real file");
            var archive = ResetCommand.ArchivePathFor(ResetCommand.Verb.Reset, catalog, new DateTime(2026, 11, 21, 1, 2, 3));
            assert(Path.GetDirectoryName(archive) == exe, $"work reset: the archive is written to the work folder ({archive})");
        }

        /// <summary>--store-import reads the ledgers from the work folder, and a ledger that moved is not imported a second time.</summary>
        static void Import(Action<bool, string> assert, string root)
        {
            var lib = Path.Combine(root, "Library");
            var exe = Path.Combine(lib, "truedat");
            Directory.CreateDirectory(exe);
            var catalog = Path.Combine(lib, "mbxmoods.json");
            // One clean entry: ErrorsRows also counts catalog entries the store cannot hold, and this counts ledger rows only.
            global::Truedat.Store.StoreSelfTest.WriteCatalog(catalog,
                global::Truedat.Store.StoreSelfTest.FixtureEntries(@"D:\Music").Where(kv => kv.Key.EndsWith(@"\Tool\a.flac", StringComparison.OrdinalIgnoreCase)));
            string Errors(string dir) { var p = Path.Combine(dir, "mbxmoods-errors.csv"); File.WriteAllLines(p, new[] { "Error,Artist,Title,FilePath,SizeMB,Duration,FailedComponents", "bad header,A,X,D:\\Music\\bad\\x.flac,1.0,2.0,sha" }); return p; }
            string Skipped(string dir) { var p = Path.Combine(dir, "mbxmoods-skipped.csv"); File.WriteAllLines(p, new[] { "path,extension,reason,timestamp", "D:\\Music\\skip\\s.dsf,.dsf,unsupported codec: DSD,2026-10-01T00:00:00Z" }); return p; }
            global::Truedat.Store.ImportReport Run(string store) => global::Truedat.Store.ImportCommand.Execute(new global::Truedat.Store.ImportOptions { StorePath = store, CatalogPath = catalog, LibraryRoot = @"D:\Music" });

            // Ledgers that are already in the work folder.
            WorkFolder.Configure(null, exe);
            WorkFolder.Begin(lib, out _);
            Errors(exe); Skipped(exe);
            var first = Run(Path.Combine(root, "a.db"));
            assert(first.ErrorsRows == 1 && first.SkippedRows == 1, $"work import: the ledgers are read from the work folder (errors {first.ErrorsRows}, skipped {first.SkippedRows})");

            // Ledgers imported while they sat beside the catalog, then moved: the next import recognises them.
            File.Delete(Path.Combine(exe, "mbxmoods-errors.csv")); File.Delete(Path.Combine(exe, "mbxmoods-skipped.csv"));
            var store = Path.Combine(root, "b.db");
            WorkFolder.UseCatalogFolderForTest = true; WorkFolder.Configure(null, exe);
            try
            {
                Errors(lib); Skipped(lib);
                var r1 = Run(store);
                assert(r1.ErrorsRows == 1 && r1.SkippedRows == 1, "work import: (before the move) the ledgers beside the catalog import");
            }
            finally { WorkFolder.UseCatalogFolderForTest = false; }
            WorkFolder.Configure(null, exe);
            WorkFolder.Begin(lib, out _);
            WorkFolder.MoveExisting(catalog, null);
            var r2 = Run(store);
            assert(File.Exists(Path.Combine(exe, "mbxmoods-errors.csv")) && r2.ErrorsRows == 0 && r2.SkippedRows == 0,
                $"work import: after the move the same rows are recognised and not imported again (errors {r2.ErrorsRows}, skipped {r2.SkippedRows})");
        }

        /// <summary>--paths names the work folder, and the working files where they now are.</summary>
        static void Paths(Action<bool, string> assert, string root)
        {
            var lib = Path.Combine(root, "Library");
            var exe = Path.Combine(root, "Tools", "truedat");
            Directory.CreateDirectory(lib);
            var catalog = Path.Combine(lib, "mbxmoods.json");
            File.WriteAllText(catalog, "{}");
            WorkFolder.Configure(null, exe);
            WorkFolder.BeginOnFirstUse = null;
            var work = WorkFolder.For(lib);
            var rows = Program.CollectPaths(catalog, null);
            var row = rows.FirstOrDefault(r => r.Key == "workDir");
            assert(row != null && row.Path == work && !row.Present && (row.Note ?? "").Contains("library id"),
                $"work paths: --paths has a work folder row naming where the working files go and why ({row?.Path}, {row?.Note})");
            assert(!Directory.Exists(work), "work paths: and --paths creates nothing");
            var errors = rows.FirstOrDefault(r => r.Key == "errorsCsv");
            assert(errors != null && Path.GetDirectoryName(errors.Path) == work, $"work paths: the error ledger is shown in the work folder ({errors?.Path})");
            var dupes = rows.FirstOrDefault(r => r.Key == "duplicates");
            assert(dupes != null && Path.GetDirectoryName(dupes.Path) == lib, $"work paths: the duplicate report stays beside the catalog ({dupes?.Path})");
        }

        /// <summary>The size block names a group's folder only once something is in the work folder: before any writing
        /// run (nothing moved, no work folder yet) it reads exactly as before.</summary>
        static void Size(Action<bool, string> assert, string root)
        {
            var lib = Path.Combine(root, "Library");
            var exe = Path.Combine(root, "Tools", "truedat");
            Directory.CreateDirectory(lib);
            var catalog = Path.Combine(lib, "mbxmoods.json");
            var entries = global::Truedat.Store.StoreSelfTest.FixtureEntries(@"D:\Music");
            global::Truedat.Store.StoreSelfTest.WriteCatalog(catalog, entries);
            File.WriteAllText(Path.Combine(lib, "mbxmoods-errors.csv"), "Error,Artist,Title,FilePath,SizeMB,Duration,FailedComponents\n");
            File.WriteAllText(Path.Combine(lib, "store-result.json"), "{}");
            string Block()
            {
                var saved = Console.Out; var sw = new StringWriter(); Console.SetOut(sw);
                try { Program.ReportCatalog(catalog, entries.Values, 0); }
                finally { Console.SetOut(saved); }
                return sw.ToString();
            }
            WorkFolder.Configure(null, exe);
            WorkFolder.BeginOnFirstUse = null;
            var before = Block();
            assert(before.Contains("mbxmoods-errors.csv") && !before.Contains("beside the catalog") && !before.Contains("in the work folder"),
                "work size: before anything moved the size block reads as before, with no folder named");
            assert(!Directory.Exists(WorkFolder.For(lib)), "work size: and reading it creates no folder");

            WorkFolder.Begin(lib, out _);
            WorkFolder.MoveExisting(catalog, null);
            var after = Block();
            var work = WorkFolder.For(lib);
            assert(after.Contains("beside the catalog") && after.Contains("in the work folder") && after.Contains(work),
                "work size: once the work folder holds files each group names its folder");
        }

        /// <summary>The move line gives a count; the names go to truedat.log in the work folder, and to the console under
        /// --audit. Anything beside the catalog named mbxmoods-* is taken for truedat's and moves, a file a person made
        /// included, so nobody should have to hunt for one that moved.</summary>
        static void Names(Action<bool, string> assert, string root)
        {
            (string Console, string Log, string Work) Run(string name, bool audit)
            {
                var lib = Path.Combine(root, name, "Library");
                var exe = Path.Combine(lib, "truedat");
                Directory.CreateDirectory(exe);
                var catalog = Path.Combine(lib, "mbxmoods.json");
                File.WriteAllText(catalog, "{}");
                File.WriteAllText(Path.Combine(lib, "mbxmoods-errors.csv"), "e");
                File.WriteAllText(Path.Combine(lib, "mbxmoods-my-notes.txt"), "a file a person made");
                WorkFolder.Configure(null, exe);
                WorkFolder.BeginOnFirstUse = null;
                Program.ResetWorkForTest(audit);
                Program.BeginWork(catalog);
                var sw = new StringWriter();
                Program.ShowWork(sw);
                var log = Path.Combine(exe, "truedat.log");
                return (sw.ToString(), File.Exists(log) ? File.ReadAllText(log) : "", exe);
            }
            try
            {
                var quiet = Run("quiet", audit: false);
                assert(quiet.Log.Contains("mbxmoods-errors.csv") && quiet.Log.Contains("mbxmoods-my-notes.txt") && quiet.Log.Contains(quiet.Work),
                    "work names: every moved name is written to truedat.log in the work folder, with the folder it went to");
                assert(quiet.Console.Contains("moved 2 working files") && !quiet.Console.Contains("mbxmoods-my-notes.txt"),
                    "work names: without --audit the console keeps the one count line");
                var loud = Run("audit", audit: true);
                assert(loud.Console.Contains("mbxmoods-errors.csv") && loud.Console.Contains("mbxmoods-my-notes.txt"),
                    "work names: under --audit the console lists them too");
                assert(loud.Log.Contains("mbxmoods-my-notes.txt"), "work names: and truedat.log still has them");
            }
            finally { Program.ResetWorkForTest(false); }
        }

        /// <summary>A catalog folder or a work folder at a drive root stays the root (review #6). Trimming the separator off "C:\" left
        /// "C:", which Windows reads as the current directory OF drive C — so with the current directory anywhere but the root, the
        /// move enumerated and wrote in the wrong folder. Paths only: nothing here is written at a drive root.</summary>
        static void DriveRoots(Action<bool, string> assert, string root)
        {
            Directory.CreateDirectory(root);
            var saved = Environment.CurrentDirectory;
            var driveRoot = Path.GetPathRoot(Path.GetFullPath(root))!;   // the drive the fixture is on, e.g. C:\
            try
            {
                Environment.CurrentDirectory = root;                      // not the root of that drive
                var exe = Path.Combine(root, "Tools", "truedat");
                WorkFolder.Configure(null, exe);
                WorkFolder.BeginOnFirstUse = null;
                string Where(string p) => Path.GetFullPath(p);

                var resolved = WorkFolder.Resolve(driveRoot, driveRoot, exe);
                assert(resolved == driveRoot, $"work roots: a work folder at a drive root is that root ({resolved}, want {driveRoot})");
                assert(Where(Path.Combine(resolved, "mbxmoods-errors.csv")) == Path.Combine(driveRoot, "mbxmoods-errors.csv"),
                    $"work roots: a file in it is at the root, not in the current directory ({Where(Path.Combine(resolved, "mbxmoods-errors.csv"))})");

                var underExe = WorkFolder.Resolve(driveRoot, null, exe);
                assert(underExe == Path.Combine(exe, "libraries", global::Truedat.Store.StoreLocation.LibraryId(driveRoot)),
                    $"work roots: a catalog at a drive root gets its own folder under the exe ({underExe})");
                var forRoot = WorkFolder.For(driveRoot);
                var other = WorkFolder.OtherPlace(driveRoot, Path.Combine(forRoot, "mbxmoods-errors.csv"));
                assert(other != null && Where(other) == Path.Combine(driveRoot, "mbxmoods-errors.csv"),
                    $"work roots: the catalog-folder side of a root catalog is the root ({other})");
                var folders = WorkFolder.Folders(driveRoot);
                assert(folders.Count == 2 && Where(Path.Combine(folders[1], "x")) == Path.Combine(driveRoot, "x"),
                    $"work roots: the folders a root catalog's files can be in end with the root itself ({string.Join(" | ", folders)})");

                WorkFolder.Configure(driveRoot, exe);
                var asOption = WorkFolder.For(Path.Combine(root, "Library"));
                assert(Where(Path.Combine(asOption, "x")) == Path.Combine(driveRoot, "x"), $"work roots: --work-dir naming a drive root is that root ({asOption})");
            }
            finally { Environment.CurrentDirectory = saved; WorkFolder.Configure(null, AppDomain.CurrentDomain.BaseDirectory); }
        }

        /// <summary>A verb run on a catalog <c>--moods</c> named (review #2): the first working-file path settles the work folder, and the
        /// move must know the catalog by its REAL name. It rebuilt the default name, so <c>mbxmoods-alt.json</c> looked like one of
        /// truedat's <c>mbxmoods-*</c> reports and <c>--list-formats</c> moved the catalog it had just read into the work folder. In a
        /// child process, because the hook is set only by a real run's argument block.</summary>
        static void CustomCatalog(Action<bool, string> assert, string root)
        {
            var exe = typeof(Program).Assembly.Location;
            {
                var lib = Path.Combine(root, "single", "Library");
                var work = Path.Combine(root, "single", "elsewhere");
                Directory.CreateDirectory(lib);
                var catalog = Path.Combine(lib, "mbxmoods-alt.json");
                File.WriteAllText(catalog, "{\"version\":1,\"trackCount\":0,\"tracks\":{}}");
                var sidecar = Path.ChangeExtension(catalog, ".mbxs");
                File.WriteAllText(sidecar, "sidecar");
                var stays = new[] { catalog, sidecar };
                var errors = Path.Combine(lib, "mbxmoods-errors.csv");
                File.WriteAllText(errors, "Error,Artist,Title,FilePath,SizeMB,Duration,FailedComponents\r\n");

                var start = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe, WorkingDirectory = lib,
                    Arguments = "--no-config --moods \"" + catalog + "\" --list-formats --work-dir \"" + work + "\"",
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                };
                int exit;
                using (var child = System.Diagnostics.Process.Start(start)!)
                {
                    child.StandardInput.Close();
                    var so = child.StandardOutput.ReadToEndAsync(); var se = child.StandardError.ReadToEndAsync();
                    if (!child.WaitForExit(120000)) { try { child.Kill(); } catch { } }
                    exit = child.HasExited ? child.ExitCode : -1;
                    so.GetAwaiter().GetResult(); se.GetAwaiter().GetResult();
                }
                var gone = stays.Where(p => !File.Exists(p)).Select(Path.GetFileName).ToList();
                assert(gone.Count == 0, $"work custom catalog: --list-formats leaves the catalog and its sidecar beside the library (exit {exit}{(gone.Count > 0 ? "; moved: " + string.Join(", ", gone) : "")})");
                assert(exit == 0 && File.Exists(Path.Combine(work, "mbxmoods-formats.csv")), $"work custom catalog: and writes its report in the work folder (exit {exit})");
                assert(!File.Exists(errors) && File.Exists(Path.Combine(work, "mbxmoods-errors.csv")), "work custom catalog: the working files still move");
            }

            // A split custom catalog (parts only: no verb that settles a folder reads one, so no child run reaches this): the move,
            // given the catalog's real name, leaves every part and the sidecar where they are.
            {
                var lib = Path.Combine(root, "split", "Library");
                var work = Path.Combine(root, "split", "elsewhere");
                Directory.CreateDirectory(lib);
                var catalog = Path.Combine(lib, "mbxmoods-alt.json");
                var stays = new List<string>();
                foreach (var n in new[] { 1, 2, 3 }) { var p = catalog + "." + n; File.WriteAllText(p, "{\"tracks\":{}}"); stays.Add(p); }
                var sidecar = Path.ChangeExtension(catalog, ".mbxs"); File.WriteAllText(sidecar, "sidecar"); stays.Add(sidecar);
                File.WriteAllText(Path.Combine(lib, "mbxmoods-errors.csv"), "e");
                WorkFolder.Configure(work, Path.Combine(root, "Tools"));
                WorkFolder.Begin(lib, out _);
                var r = WorkFolder.MoveExisting(catalog, null);
                var gone = stays.Where(p => !File.Exists(p)).Select(Path.GetFileName).ToList();
                assert(gone.Count == 0 && r.Moved == 1, $"work custom catalog (split): the move keeps every numbered part and the sidecar by the catalog's real name ({r.Moved} moved{(gone.Count > 0 ? "; lost: " + string.Join(", ", gone) : "")})");
                WorkFolder.Configure(null, AppDomain.CurrentDomain.BaseDirectory);
            }
        }

        /// <summary>The cross-volume move when another writer creates the destination first (review #13). The copy refused to overwrite
        /// it, and the cleanup then deleted the destination because both files existed — the OTHER writer's file. A "\\?\" spelling of
        /// the same folder has a different path root, so the move takes its copy branch here without a second volume.</summary>
        static void CrossVolumeRace(Action<bool, string> assert, string root)
        {
            var src = Path.Combine(root, "catalog", "mbxmoods-errors.csv");
            var destDir = Path.Combine(root, "work");
            Directory.CreateDirectory(Path.GetDirectoryName(src)!); Directory.CreateDirectory(destDir);
            File.WriteAllText(src, "this run's ledger");
            var dest = Path.Combine(destDir, "mbxmoods-errors.csv");
            File.WriteAllText(dest, "the other writer's ledger");
            var destOtherRoot = @"\\?\" + dest;
            assert(!string.Equals(Path.GetPathRoot(src), Path.GetPathRoot(destOtherRoot), StringComparison.OrdinalIgnoreCase),
                $"work race: the fixture's destination has another path root, so the move copies ({Path.GetPathRoot(destOtherRoot)})");
            var moveOne = typeof(WorkFolder).GetMethod("MoveOne", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            bool moved = moveOne != null && (bool)moveOne.Invoke(null, new object[] { src, destOtherRoot })!;
            assert(File.Exists(dest) && File.ReadAllText(dest) == "the other writer's ledger",
                "work race: a destination another writer made first is never deleted or written over");
            assert(!moved && File.Exists(src) && File.ReadAllText(src) == "this run's ledger",
                $"work race: the move reports it could not move, and this run's file stays where it was - both writers' data kept (moved {moved})");
            var leftovers = Directory.GetFiles(destDir).Where(f => !string.Equals(f, dest, StringComparison.OrdinalIgnoreCase)).Select(Path.GetFileName).ToList();
            assert(leftovers.Count == 0, $"work race: no staging file is left behind ({string.Join(", ", leftovers)})");

            // The ordinary cross-volume move still works: copied whole, then the source removed.
            File.Delete(dest);
            moved = moveOne != null && (bool)moveOne.Invoke(null, new object[] { src, destOtherRoot })!;
            assert(moved && !File.Exists(src) && File.ReadAllText(dest) == "this run's ledger"
                   && Directory.GetFiles(destDir).Length == 1, $"work race: with nothing in the way the copy lands, verified, and the source goes (moved {moved})");
        }

        /// <summary>A work folder that is the catalog folder under another name — a junction to it (review #14). The names differ, so
        /// the move took them for two folders: every working file "collided" with itself, the timestamps tied, and the only copy of
        /// the ledger was renamed to .moved-&lt;time&gt;. Folders are compared by directory identity (volume serial + file index) now.
        /// The junction is made inside this test's own folder and removed by the test (never left for a recursive delete).</summary>
        static void Alias(Action<bool, string> assert, string root)
        {
            var lib = Path.Combine(root, "Library");
            var junction = Path.Combine(root, "LibraryAlias");
            Directory.CreateDirectory(lib);
            var catalog = Path.Combine(lib, "mbxmoods.json");
            File.WriteAllText(catalog, "{}");
            var ledger = Path.Combine(lib, "mbxmoods-errors.csv");
            File.WriteAllText(ledger, "the only copy");
            int mk;
            using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c mklink /J \"" + junction + "\" \"" + lib + "\"")
                   { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!)
            { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(); mk = p.ExitCode; }
            if (mk != 0 || !File.Exists(Path.Combine(junction, "mbxmoods-errors.csv")))
            {
                Console.WriteLine("  NOT RUN  work alias: could not make a junction here (mklink /J exit " + mk + ")");
                return;
            }
            try
            {
                WorkFolder.Configure(junction, Path.Combine(root, "Tools"));
                var set = ResetCommand.Collect(ResetCommand.Verb.Reset, catalog, null, null).Select(f => f.EntryName).ToList();
                assert(!set.Any(n => n.StartsWith("work/", StringComparison.Ordinal)) && set.Count(n => n == "mbxmoods-errors.csv") == 1,
                    $"work alias: the reset's file set lists each file once, not again through the alias ({string.Join(", ", set)})");
                WorkFolder.Begin(lib, out _);
                var r = WorkFolder.MoveExisting(catalog, null);
                assert(File.Exists(ledger) && File.ReadAllText(ledger) == "the only copy",
                    "work alias: the ledger is still at its name, untouched, when the work folder is the catalog folder by another name");
                var aside = Directory.GetFiles(lib, "*" + WorkFolder.MovedSuffix + "*");
                assert(aside.Length == 0 && r.Moved == 0 && r.Renamed.Count == 0,
                    $"work alias: an alias-to-itself move moves nothing and renames nothing ({r.Moved} moved, {r.Renamed.Count} renamed, {aside.Length} .moved- files)");
                assert(WorkFolder.Describe(r) == null, "work alias: and has nothing to say");
                WorkFolder.Configure(null, AppDomain.CurrentDomain.BaseDirectory);
            }
            finally { try { Directory.Delete(junction); } catch { } }   // non-recursive: removes the link, never the folder it points at
            assert(!Directory.Exists(junction) && File.Exists(ledger), "work alias: the test's junction is removed and the folder it pointed at is intact");
        }

        /// <summary>A work folder written once and later unwritable (review #15): writes fall back to the catalog folder, and the ledgers
        /// the earlier run moved there must still be consulted — otherwise the fallback reads an empty catalog-folder ledger, a file
        /// that failed before is tried again, and a new ledger starts. Write access is denied with an ACL this test removes again.</summary>
        static void ReadOnlyLater(Action<bool, string> assert, string root)
        {
            var lib = Path.Combine(root, "Library");
            var work = Path.Combine(root, "work");
            Directory.CreateDirectory(lib);
            var catalog = Path.Combine(lib, "mbxmoods.json");
            File.WriteAllText(catalog, "{}");
            const string failedRow = "decode failed,A,T,D:\\Music\\broken.flac,1.0,2.0,essentia";
            File.WriteAllText(Path.Combine(lib, "mbxmoods-errors.csv"), "Error,Artist,Title,FilePath,SizeMB,Duration,FailedComponents\r\n" + failedRow + "\r\n");

            // Run 1: the folder is writable, the ledger moves into it.
            WorkFolder.Configure(work, Path.Combine(root, "Tools"));
            WorkFolder.Begin(lib, out _);
            WorkFolder.MoveExisting(catalog, null);
            assert(File.Exists(Path.Combine(work, "mbxmoods-errors.csv")) && !File.Exists(Path.Combine(lib, "mbxmoods-errors.csv")),
                "work read-only later: the first run moves the ledger into the work folder");

            var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
            var deny = new System.Security.AccessControl.FileSystemAccessRule(me,
                System.Security.AccessControl.FileSystemRights.CreateFiles | System.Security.AccessControl.FileSystemRights.AppendData
                | System.Security.AccessControl.FileSystemRights.WriteData | System.Security.AccessControl.FileSystemRights.Delete
                | System.Security.AccessControl.FileSystemRights.DeleteSubdirectoriesAndFiles,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Deny);
            var acl = Directory.GetAccessControl(work);
            acl.AddAccessRule(deny);
            Directory.SetAccessControl(work, acl);
            try
            {
                // Run 2: the folder cannot be written now.
                WorkFolder.Configure(work, Path.Combine(root, "Tools"));
                var used = WorkFolder.Begin(lib, out var note);
                assert(used == lib && note != null, $"work read-only later: the next run falls back to the catalog folder for its writes ({used})");
                var ledger = WorkFolder.Read(lib, "mbxmoods-errors.csv");
                bool sees = File.Exists(ledger) && File.ReadAllText(ledger).Contains(failedRow);
                assert(sees, $"work read-only later: the ledger the earlier run moved is still consulted, so the failed file is still skipped ({ledger})");
                var appendTo = WorkFolder.Read(lib, "mbxmoods-errors.csv");
                bool appended = false;
                try { File.AppendAllText(appendTo, "new failure,A,U,D:\\Music\\new.flac,1.0,2.0,essentia\r\n"); appended = true; } catch { }
                assert(appended && Path.GetDirectoryName(appendTo) == lib, $"work read-only later: and what this run adds goes where it can be written, the catalog folder ({appendTo})");
                assert(WorkFolder.Folders(lib).Contains(work), "work read-only later: the work folder is still listed among the places this library's files are");
            }
            finally
            {
                var back = Directory.GetAccessControl(work);
                back.RemoveAccessRule(deny);
                Directory.SetAccessControl(work, back);
                WorkFolder.Configure(null, AppDomain.CurrentDomain.BaseDirectory);
            }
        }

        /// <summary>One configured work folder, two libraries (hub review P1, item A): a config work-dir is used as given for every
        /// catalog, so two libraries shared backup and report names, and a reset or a rotation of one picked up the other's. The first
        /// library to settle the folder writes .truedat-work-owner.json there; a second is refused, naming both folders, before
        /// anything moves.</summary>
        static void Owner(Action<bool, string> assert, string root)
        {
            var lib1 = Path.Combine(root, "One", "Library");
            var lib2 = Path.Combine(root, "Two", "Library");
            var shared = Path.Combine(root, "shared-work");
            foreach (var lib in new[] { lib1, lib2 })
            {
                Directory.CreateDirectory(lib);
                File.WriteAllText(Path.Combine(lib, "mbxmoods.json"), "{}");
                File.WriteAllText(Path.Combine(lib, "mbxmoods-errors.csv"), "ledger of " + lib);
            }
            var tools = Path.Combine(root, "Tools");

            WorkFolder.Configure(shared, tools);
            WorkFolder.Begin(lib1, out _);
            WorkFolder.MoveExisting(Path.Combine(lib1, "mbxmoods.json"), null);
            var marker = Path.Combine(shared, ".truedat-work-owner.json");
            var doc = File.Exists(marker) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(marker)) : null;
            assert(doc?["kind"]?.GetValue<string>() == "truedat-work-owner" && doc?["version"]?.GetValue<int>() == 1
                   && doc?["catalogFolder"]?.GetValue<string>() == lib1
                   && doc?["libraryId"]?.GetValue<string>() == global::Truedat.Store.StoreLocation.LibraryId(lib1),
                $"work owner: the first library to settle a work folder that is not its catalog folder marks it as its own ({(doc == null ? "no marker" : doc.ToJsonString())})");

            WorkFolder.Configure(shared, tools);   // the next run, the other library, the same configured folder
            string? refusal = null;
            try { WorkFolder.Begin(lib2, out _); } catch (Exception ex) { refusal = ex.Message; }
            assert(refusal != null && refusal.Contains(lib1) && refusal.Contains(lib2),
                $"work owner: a second library is refused, the refusal naming both folders ({refusal ?? "not refused"})");
            assert(File.Exists(Path.Combine(lib2, "mbxmoods-errors.csv")) && File.ReadAllText(Path.Combine(shared, "mbxmoods-errors.csv")) == "ledger of " + lib1,
                "work owner: and nothing of the second library moved into the first one's folder");
            assert(!WorkFolder.IsSettled(lib2), "work owner: the refused library has no work folder settled");

            WorkFolder.Configure(shared, tools);   // the owner itself, again: fine
            bool ownerOk = true;
            try { WorkFolder.Begin(lib1, out _); } catch { ownerOk = false; }
            assert(ownerOk && WorkFolder.Current == shared, "work owner: the library the marker names settles it as before");
            assert(!WorkFolder.IsWorkingFile(".truedat-work-owner.json", "mbxmoods.json", "mbxmoods.mbxs", null, "truedat.db"),
                "work owner: the marker is not a working file (never moved, archived or rotated)");
            WorkFolder.Configure(null, AppDomain.CurrentDomain.BaseDirectory);

            // Through a real verb: the second library's run stops with exit 2, says why, and writes nothing into the folder.
            var lib3 = Path.Combine(root, "Three", "Library");
            Directory.CreateDirectory(lib3);
            File.WriteAllText(Path.Combine(lib3, "mbxmoods.json"), "{\"version\":1,\"trackCount\":0,\"tracks\":{}}");
            File.WriteAllText(Path.Combine(lib3, "mbxmoods-errors.csv"), "ledger of three");
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = typeof(Program).Assembly.Location, WorkingDirectory = lib3,
                Arguments = "--no-config --moods \"" + Path.Combine(lib3, "mbxmoods.json") + "\" --list-formats --work-dir \"" + shared + "\"",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            };
            int exit; string output;
            using (var child = System.Diagnostics.Process.Start(start)!)
            {
                child.StandardInput.Close();
                var so = child.StandardOutput.ReadToEndAsync(); var se = child.StandardError.ReadToEndAsync();
                if (!child.WaitForExit(120000)) { try { child.Kill(); } catch { } }
                exit = child.HasExited ? child.ExitCode : -1;
                output = so.GetAwaiter().GetResult() + se.GetAwaiter().GetResult();
            }
            assert(exit == 2 && output.Contains(lib1) && output.Contains(lib3),
                $"work owner (cli): a run of another library against the claimed folder exits 2, naming both folders (exit {exit})");
            assert(File.Exists(Path.Combine(lib3, "mbxmoods-errors.csv")) && !File.Exists(Path.Combine(shared, "mbxmoods-formats.csv")),
                "work owner (cli): its working files stayed where they were and its report was not written into the other library's folder");
        }

        /// <summary>The flag and the config key are one spelling, and a reset may ride with it.</summary>
        static void Settings(Action<bool, string> assert)
        {
            var s = TruedatConfig.Find(WorkFolder.Flag);
            assert(s != null && s.Kind == TruedatConfig.Kind.Text, "work settings: work-dir is a config key that takes a path");
            assert(Program.KnownFlags.Contains(WorkFolder.Flag) && Program.TakesValue(WorkFolder.Flag), "work settings: --work-dir is a flag that takes a value");
            assert(ResetCommand.ScanOptions.Contains(WorkFolder.Flag), "work settings: --clear and --reset accept --work-dir (every config key rides with them)");
        }
    }
}
