using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Truedat
{
    /// <summary>
    /// Working files beside the catalog (<see cref="WorkFolder"/>), and the move back from the folder the EV6.2 build put them
    /// in: which files are working files, that a reading run finds them in the old folder and moves nothing, that a writing run
    /// brings them home without writing over anything, and that a folder another library owns is never read or emptied.
    /// Plus the config file across <c>--clear</c>, <c>--reset</c> and <c>--restore</c>.
    /// </summary>
    internal static class WorkFolderSelfTest
    {
        internal static void Run(Action<bool, string> assert)
        {
            Console.WriteLine("Work folder self-test (working files beside the catalog; the move back from the EV6.2 folder)");
            var hook = WorkFolder.BeginOnFirstUse;
            var root = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "workfolder-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                WorkFolder.BeginOnFirstUse = null;
                Table(assert);
                BesideTheCatalog(assert, Path.Combine(root, "beside"));
                ExeInLibrary(assert, Path.Combine(root, "exe"));
                LibrariesById(assert, Path.Combine(root, "byid"));
                AnotherLibrary(assert, Path.Combine(root, "other"));
                Collision(assert, Path.Combine(root, "collide"));
                ConfigAcrossReset(assert, Path.Combine(root, "config"));
            }
            catch (Exception ex) { assert(false, "work folder self-test threw " + ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                WorkFolder.Configure(AppDomain.CurrentDomain.BaseDirectory);
                WorkFolder.BeginOnFirstUse = hook;
                ResetCommand.ConfigDirForTest = null;
                try { Directory.Delete(root, true); } catch { }
            }
        }

        static void Marker(string folder, string cat)
            => File.WriteAllText(Path.Combine(folder, WorkFolder.LegacyOwnerMarkerName),
                "{\"kind\":\"truedat-work-owner\",\"version\":1,\"catalogFolder\":" + System.Text.Json.JsonSerializer.Serialize(cat) + "}");

        static void Locator(string cat, string work)
            => File.WriteAllText(Path.Combine(cat, WorkFolder.LegacyLocatorFileName),
                "{\"kind\":\"truedat-work-folder\",\"version\":1,\"catalogFolder\":" + System.Text.Json.JsonSerializer.Serialize(cat)
                + ",\"workFolder\":" + System.Text.Json.JsonSerializer.Serialize(work) + ",\"libraryId\":\"x\"}");

        /// <summary>With no old folder, every working-file path is beside the catalog, and nothing is created anywhere else.</summary>
        static void BesideTheCatalog(Action<bool, string> assert, string root)
        {
            var lib = Path.Combine(root, "Library");
            var exe = Path.Combine(root, "elsewhere", "bin");
            Directory.CreateDirectory(lib); Directory.CreateDirectory(exe);
            WorkFolder.Configure(exe);
            var cat = Path.Combine(lib, "mbxmoods.json");
            assert(string.Equals(WorkFolder.For(lib), Path.GetFullPath(lib), StringComparison.OrdinalIgnoreCase), "work beside: a library's working files go beside its catalog");
            assert(WorkFolder.Write(lib, "mbxmoods-errors.csv") == Path.Combine(Path.GetFullPath(lib), "mbxmoods-errors.csv")
                   && WorkFolder.Read(lib, "mbxmoods-skipped.csv") == Path.Combine(Path.GetFullPath(lib), "mbxmoods-skipped.csv"),
                "work beside: a write and a read of a working file are beside the catalog");
            assert(WorkFolder.Folders(lib).Count == 1, "work beside: with no old folder there is one place to look");
            var r = WorkFolder.MoveBack(cat, null);
            assert(!r.Anything && WorkFolder.Describe(r) == null, "work beside: with no old folder the move back has nothing to say");
            assert(!Directory.Exists(Path.Combine(exe, WorkFolder.LegacyLibrariesDir)), "work beside: nothing is created under the exe");
        }

        /// <summary>The usual install: truedat in Library\truedat, where the EV6.2 build put the working files in the exe's folder.</summary>
        static void ExeInLibrary(Action<bool, string> assert, string root)
        {
            var lib = Path.Combine(root, "Library");
            var exe = Path.Combine(lib, "truedat");
            Directory.CreateDirectory(exe);
            WorkFolder.Configure(exe);
            var cat = Path.Combine(lib, "mbxmoods.json");
            File.WriteAllText(cat, "{}");
            File.WriteAllText(Path.Combine(exe, "truedat.exe"), "exe");
            File.WriteAllText(Path.Combine(exe, TruedatConfig.FileName), "{}");
            File.WriteAllText(Path.Combine(exe, "mbxmoods-errors.csv"), "errors");
            File.WriteAllText(Path.Combine(exe, "mbxmoods-scan-2026-11-21-1.txt"), "summary");
            File.WriteAllText(Path.Combine(exe, "mbxmoods.json.bak.20261121.010203.zip"), "backup");
            Marker(exe, Path.GetFullPath(lib));
            Locator(lib, Path.GetFullPath(exe));

            // A reading run: finds the old files where they are and moves nothing.
            assert(WorkFolder.Read(lib, "mbxmoods-errors.csv") == Path.Combine(Path.GetFullPath(exe), "mbxmoods-errors.csv"),
                "work exe: a reading run reads the error list where the EV6.2 build put it");
            assert(WorkFolder.Folders(lib).Count == 2 && WorkFolder.Files(lib, "mbxmoods-scan-*.txt").Count == 1,
                "work exe: the old folder is a second place to look, and its summaries are counted");
            assert(File.Exists(Path.Combine(exe, "mbxmoods-errors.csv")) && !File.Exists(Path.Combine(lib, "mbxmoods-errors.csv")),
                "work exe: and nothing moved");

            // A writing run: everything comes home; the exe, the config and nothing else stay; the marker and the locator go.
            var r = WorkFolder.MoveBack(cat, null);
            assert(r.Moved == 3 && r.Held.Count == 0, $"work exe: the move back brings the three working files home (moved {r.Moved}, held {r.Held.Count})");
            assert(File.ReadAllText(Path.Combine(lib, "mbxmoods-errors.csv")) == "errors"
                   && File.ReadAllText(Path.Combine(lib, "mbxmoods-scan-2026-11-21-1.txt")) == "summary"
                   && File.ReadAllText(Path.Combine(lib, "mbxmoods.json.bak.20261121.010203.zip")) == "backup",
                "work exe: byte for byte, beside the catalog");
            assert(File.Exists(Path.Combine(exe, "truedat.exe")) && File.Exists(Path.Combine(exe, TruedatConfig.FileName)),
                "work exe: the exe and the config file stay with the exe");
            assert(!File.Exists(Path.Combine(exe, WorkFolder.LegacyOwnerMarkerName)) && !File.Exists(Path.Combine(lib, WorkFolder.LegacyLocatorFileName)),
                "work exe: the owner marker and the locator are gone once nothing is left");
            assert(WorkFolder.Describe(r)!.Contains("back beside the catalog"), "work exe: the run says it moved them back");
            assert(WorkFolder.Folders(lib).Count == 1, "work exe: and after the move there is one place to look");
            assert(WorkFolder.OtherPlace(lib, Path.Combine(lib, "mbxmoods-errors.csv")) == Path.Combine(Path.GetFullPath(exe), "mbxmoods-errors.csv"),
                "work exe: a store keyed on the old ledger path still recognises it this run");
        }

        /// <summary>truedat installed elsewhere: the EV6.2 build used &lt;exe&gt;\libraries\&lt;id&gt;. Emptied, the folder goes.</summary>
        static void LibrariesById(Action<bool, string> assert, string root)
        {
            var lib = Path.Combine(root, "Library");
            var exe = Path.Combine(root, "tools");
            Directory.CreateDirectory(lib);
            WorkFolder.Configure(exe);
            var cat = Path.Combine(lib, "mbxmoods.json");
            File.WriteAllText(cat, "{}");
            var libs = Path.Combine(exe, WorkFolder.LegacyLibrariesDir);
            var old = Path.Combine(libs, global::Truedat.Store.StoreLocation.LibraryId(Path.GetFullPath(lib)));
            Directory.CreateDirectory(old);
            File.WriteAllText(Path.Combine(old, "mbxmoods-skipped.csv"), "skipped");
            File.WriteAllText(Path.Combine(old, "truedat.log"), "log");
            Marker(old, Path.GetFullPath(lib));
            var r = WorkFolder.MoveBack(cat, null);
            assert(r.Moved == 2 && File.ReadAllText(Path.Combine(lib, "mbxmoods-skipped.csv")) == "skipped" && File.Exists(Path.Combine(lib, "truedat.log")),
                $"work byid: the files under libraries\\<id> come back beside the catalog (moved {r.Moved})");
            assert(!Directory.Exists(old) && !Directory.Exists(libs), "work byid: the emptied folder, and the emptied libraries folder, are removed");
        }

        /// <summary>One exe serving two libraries: a folder whose marker names the other library is never read or emptied for this one.</summary>
        static void AnotherLibrary(Action<bool, string> assert, string root)
        {
            var lib = Path.Combine(root, "Library");
            var other = Path.Combine(root, "Other");
            var exe = Path.Combine(lib, "truedat");
            Directory.CreateDirectory(exe); Directory.CreateDirectory(other);
            WorkFolder.Configure(exe);
            var cat = Path.Combine(lib, "mbxmoods.json");
            File.WriteAllText(cat, "{}");
            File.WriteAllText(Path.Combine(exe, "mbxmoods-errors.csv"), "the other library's errors");
            Marker(exe, Path.GetFullPath(other));
            assert(WorkFolder.Read(lib, "mbxmoods-errors.csv") == Path.Combine(Path.GetFullPath(lib), "mbxmoods-errors.csv"),
                "work other: a folder another library owns is not read for this one");
            var r = WorkFolder.MoveBack(cat, null);
            assert(r.Moved == 0 && File.Exists(Path.Combine(exe, "mbxmoods-errors.csv")) && !File.Exists(Path.Combine(lib, "mbxmoods-errors.csv"))
                   && File.Exists(Path.Combine(exe, WorkFolder.LegacyOwnerMarkerName)),
                "work other: nor emptied: its files and its marker stay");
        }

        /// <summary>Both places hold a name: the newer is the live file, the older is kept beside it, nothing is written over.</summary>
        static void Collision(Action<bool, string> assert, string root)
        {
            var lib = Path.Combine(root, "Library");
            var exe = Path.Combine(lib, "truedat");
            Directory.CreateDirectory(exe);
            WorkFolder.Configure(exe);
            var cat = Path.Combine(lib, "mbxmoods.json");
            File.WriteAllText(cat, "{}");
            var beside = Path.Combine(lib, "mbxmoods-errors.csv");
            var away = Path.Combine(exe, "mbxmoods-errors.csv");
            File.WriteAllText(beside, "older, beside the catalog");
            File.WriteAllText(away, "newer, in the old folder");
            File.SetLastWriteTimeUtc(beside, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(away, DateTime.UtcNow.AddHours(-1));
            Marker(exe, Path.GetFullPath(lib));
            var r = WorkFolder.MoveBack(cat, null);
            var aside = Directory.GetFiles(lib, "mbxmoods-errors.csv" + WorkFolder.MovedSuffix + "*");
            assert(r.Moved == 1 && File.ReadAllText(beside) == "newer, in the old folder" && aside.Length == 1 && File.ReadAllText(aside[0]) == "older, beside the catalog",
                "work collide: the newer copy is live beside the catalog and the older is kept as <name>.moved-<time>");
            assert(!File.Exists(away), "work collide: and the old folder no longer holds it");
        }

        /// <summary>--reset takes the config file, --clear keeps it, --no-config leaves it, and --restore of a reset archive puts it back
        /// beside the exe — refusing while one is there.</summary>
        static void ConfigAcrossReset(Action<bool, string> assert, string root)
        {
            var exe = Path.Combine(root, "exe");
            var lib = Path.Combine(root, "Library");
            Directory.CreateDirectory(exe); Directory.CreateDirectory(lib);
            var config = Path.Combine(exe, TruedatConfig.FileName);
            File.WriteAllText(config, "{\"store\":true}");
            var cat = Path.Combine(lib, "mbxmoods.json");
            File.WriteAllText(cat, "{}");
            assert(!ResetCommand.Keeps(ResetCommand.Verb.Reset, TruedatConfig.FileName) && ResetCommand.Keeps(ResetCommand.Verb.Clear, TruedatConfig.FileName),
                "config: --reset does not keep the config file; --clear does");
            assert(ResetCommand.ConfigToTake(ResetCommand.Verb.Reset, false, exe) == config, "config: --reset takes the config file beside the exe");
            assert(ResetCommand.ConfigToTake(ResetCommand.Verb.Clear, false, exe) == null, "config: --clear takes none");
            assert(ResetCommand.ConfigToTake(ResetCommand.Verb.Reset, true, exe) == null, "config: --reset with --no-config leaves it");
            var files = ResetCommand.Collect(ResetCommand.Verb.Reset, cat, null, null, config);
            assert(files.Any(f => f.EntryName == ResetCommand.ConfigEntryName && f.Path == config), "config: the reset's file set holds it as " + ResetCommand.ConfigEntryName);
            var cleared = ResetCommand.Collect(ResetCommand.Verb.Clear, cat, null, null, config);
            assert(!cleared.Any(f => f.EntryName == ResetCommand.ConfigEntryName), "config: the clear's file set does not, even when handed one");

            var zip = Path.Combine(root, "mbxmoods-reset-20261122-010203.zip");
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                z.CreateEntryFromFile(cat, "mbxmoods.json");
                z.CreateEntryFromFile(config, ResetCommand.ConfigEntryName);
            }
            ResetCommand.ConfigDirForTest = exe;
            var entries = ResetCommand.RuleEntries(zip);
            assert(entries.Contains(ResetCommand.ConfigEntryName), "config: a reset archive's config entry is one a restore puts back");
            assert(ResetCommand.RuleConflicts(entries, lib).Contains(config), "config: a restore refuses while a config file is there, naming it");
            File.Delete(config);
            assert(ResetCommand.RuleConflicts(entries, lib).Count == 0 && ResetCommand.RestoreRules(zip, entries, lib) == 1
                   && File.ReadAllText(config) == "{\"store\":true}",
                "config: with none there, the restore puts it back beside the exe, byte for byte");
            ResetCommand.ConfigDirForTest = null;

            // The scan that follows a --reset runs without the config (it was just taken); --clear's scan uses it.
            assert(!Program.ConfigApplies(noConfig: false, resetVerb: true) && Program.ConfigApplies(noConfig: false, resetVerb: false)
                   && !Program.ConfigApplies(noConfig: true, resetVerb: false),
                "config: a --reset run reads no config file, so its scan runs on the defaults; any other run reads it unless --no-config");
        }

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
                "mbxmoods-speech.csv", "mbxmoods-smfm.csv", "mbxmoods-smfm-missing.csv", "mbxmoods-formats.csv",
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
                // A verify shard's receipt (review #16): --verify-coverage on any host reads it beside the shared catalog.
                "mbxmoods-verify.receipt.json", "mbxmoods-verify.host7.1of2.receipt.json",
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

    }
}
