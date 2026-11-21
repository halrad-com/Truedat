using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Truedat
{
    /// <summary>
    /// <c>--reset</c>: archive this library's truedat files into one zip, remove them, scan from nothing.
    ///
    /// The rules are pinned as functions here (which files, which are kept, what counts as a
    /// confirmation, archive-before-remove). The last block runs the real verb in a child process,
    /// because none of the functions can pin where the scan calls them.
    /// </summary>
    internal static class ResetSelfTest
    {
        internal static void Run(Action<bool, string> assert)
        {
            Console.WriteLine("Reset: archive the library's truedat files, remove them, scan from nothing");
            TestFlags(assert);
            TestKept(assert);
            TestConfirm(assert);
            TestAbsentLibrary(assert);
            TestStoreAfterReset(assert);
            TestStoreIsWholeBeforeArchiving(assert);
            TestCollectAndExecute(assert);
            TestCli(assert);
        }

        static void TestFlags(Action<bool, string> assert)
        {
            string? C(params string[] args) => ResetCommand.ConflictingFlag(args);
            assert(C("--reset", "--imeanit", "-p", "8", "--store", "--edges", "--audit") == null,
                "reset-flags: scan options ride with --reset");
            assert(C("--reset", "--fixup") == "--fixup", $"reset-flags: another verb is named ({C("--reset", "--fixup")})");
            assert(C("--reset", "--analyze-file", "a.flac") == "--analyze-file",
                $"reset-flags: a per-file mode is named ({C("--reset", "--analyze-file", "a.flac")})");
            assert(C("--reset", "--chunk", "1/2") == "--chunk", "reset-flags: --chunk is named");
            assert(C("--reset", "--moods", "x.json") == "--moods", "reset-flags: --moods is named (the library scan's catalog is beside the library)");
            assert(C("--reset", "--stage-dir", "stats") == null, "reset-flags: a value that spells a flag name is a value, not a flag");
            assert(C("reset", "stats") == "stats", "reset-flags: a bare-word verb is caught as the parser would read it");
            assert(C("--reset", Path.Combine("C:", "lib", "iTunes Music Library.xml")) == null,
                "reset-flags: the positional library path is not a flag");
            assert(C("/reset", "/imeanit", "/no-store") == null, "reset-flags: slash spellings are read the same way");
            assert(C("--reset", "--analyze") == null, $"reset-flags: --analyze names the library scan itself ({C("--reset", "--analyze")})");
            assert(C("--reset", "--audit-verdicts") == null, $"reset-flags: --audit-verdicts is --audit with more detail ({C("--reset", "--audit-verdicts")})");

            var known = new HashSet<string>(Program.KnownFlags, StringComparer.OrdinalIgnoreCase);
            var unknown = ResetCommand.ScanOptions.Where(f => !known.Contains(f)).ToList();
            assert(unknown.Count == 0, "reset-flags: every allowed option is a real flag" + (unknown.Count > 0 ? " (not: " + string.Join(", ", unknown) + ")" : ""));
            assert(known.Contains(ResetCommand.Flag) && known.Contains(ResetCommand.ConfirmFlag), "reset-flags: --reset and --imeanit are known flags");

            // A config key becomes an argument on every run. One that --reset refused would make the
            // verb unusable on that instance, with a message blaming a flag nobody typed.
            var allowed = new HashSet<string>(ResetCommand.ScanOptions, StringComparer.OrdinalIgnoreCase);
            var blocked = TruedatConfig.Settings.Select(s => s.Key).Where(k => !allowed.Contains(k)).ToList();
            assert(blocked.Count == 0, "reset-flags: every config key may ride with --reset" + (blocked.Count > 0 ? " (refused: " + string.Join(", ", blocked) + ")" : ""));
            assert(!TruedatConfig.Settings.Any(s => string.Equals(s.Key, ResetCommand.Flag, StringComparison.OrdinalIgnoreCase)
                                          || string.Equals(s.Key, ResetCommand.ConfirmFlag, StringComparison.OrdinalIgnoreCase)),
                "reset-flags: neither --reset nor its confirmation can be set in the config file");
        }

        static void TestKept(Action<bool, string> assert)
        {
            assert(ResetCommand.IsKept(ExclusionStore.FileName), "reset-kept: the exclusion rules are the operator's, not scan output");
            assert(ResetCommand.IsKept(ExclusionStore.FileName + ".bak.20260101-000000.zip"), "reset-kept: so are the exclusion file's own backups");
            assert(ResetCommand.IsKept("mbxmoods-snapshot-20260101-000000.zip"), "reset-kept: a snapshot the operator took");
            assert(ResetCommand.IsKept("mbxmoods-reset-20260101-000000.zip"), "reset-kept: an earlier reset's archive");
            assert(ResetCommand.IsKept("MBXMOODS-RESET-20260101-000000.ZIP"), "reset-kept: whatever the case");
            assert(ResetCommand.IsKept("mbxmoods-schema.json"), "reset-kept: the field registry");
            assert(!ResetCommand.IsKept("mbxmoods-errors.csv"), "reset-kept: the error list goes");
            assert(!ResetCommand.IsKept("mbxmoods-skipped.csv"), "reset-kept: the skip list goes");
            assert(!ResetCommand.IsKept(ReviewLedger.FileName), "reset-kept: the review records go");
            assert(!ResetCommand.IsKept(Program.MoodsFileName), "reset-kept: the catalog goes");
        }

        static void TestConfirm(Action<bool, string> assert)
        {
            var sink = new StringWriter();
            string? Never() => throw new InvalidOperationException("the prompt was read");
            assert(ResetCommand.Confirm(true, false, Never, sink) == ResetCommand.Confirmation.Confirmed, "reset-confirm: --imeanit confirms without asking");
            assert(ResetCommand.Confirm(true, true, Never, sink) == ResetCommand.Confirmation.Confirmed, "reset-confirm: --imeanit confirms when nobody is at the console");
            assert(ResetCommand.Confirm(false, true, Never, sink) == ResetCommand.Confirmation.NotAsked,
                "reset-confirm: with no console and no flag nothing is read and nothing is confirmed");
            assert(sink.ToString().Length == 0, "reset-confirm: no prompt is written when nobody can answer it");

            ResetCommand.Confirmation Typed(string? line) { var w = new StringWriter(); return ResetCommand.Confirm(false, false, () => line, w); }
            assert(Typed("RESET") == ResetCommand.Confirmation.Confirmed, "reset-confirm: typing the word confirms");
            assert(Typed("  RESET  ") == ResetCommand.Confirmation.Confirmed, "reset-confirm: surrounding spaces do not matter");
            assert(Typed("reset") == ResetCommand.Confirmation.Declined, "reset-confirm: the word is case-sensitive (a reflex 'reset' is not it)");
            assert(Typed("yes") == ResetCommand.Confirmation.Declined, "reset-confirm: yes is not the word");
            assert(Typed("y") == ResetCommand.Confirmation.Declined, "reset-confirm: y is not the word");
            assert(Typed("") == ResetCommand.Confirmation.Declined, "reset-confirm: Enter alone declines");
            assert(Typed(null) == ResetCommand.Confirmation.Declined, "reset-confirm: end of input declines");
            var asked = new StringWriter();
            ResetCommand.Confirm(false, false, () => "no", asked);
            assert(asked.ToString().Contains(ResetCommand.ConfirmWord), "reset-confirm: the prompt says which word to type");
        }

        static void TestAbsentLibrary(Action<bool, string> assert)
        {
            assert(ResetCommand.LibraryLooksAbsent(0, 0), "reset-library: a library that lists nothing cannot be rebuilt");
            assert(ResetCommand.LibraryLooksAbsent(200, 0), "reset-library: none of the sampled files are here");
            assert(ResetCommand.LibraryLooksAbsent(200, 99), "reset-library: under half of the sampled files are here");
            assert(!ResetCommand.LibraryLooksAbsent(200, 100), "reset-library: half are here");
            assert(!ResetCommand.LibraryLooksAbsent(1, 1), "reset-library: a one-track library whose track is here");
            var picked = ResetCommand.SampleIndexes(1000, 200);
            int lo = picked.Count > 0 ? picked.Min() : -1, hi = picked.Count > 0 ? picked.Max() : -1;
            assert(picked.Count == 200 && picked.Distinct().Count() == 200 && lo >= 0 && hi < 1000,
                $"reset-library: the sample is spread over the whole list ({picked.Count} of 1000, {lo}..{hi})");
            assert(hi > 900, "reset-library: the sample reaches the end of the list, not only its head");
            assert(ResetCommand.SampleIndexes(3, 200).SequenceEqual(new[] { 0, 1, 2 }), "reset-library: a short list is checked whole");
        }

        /// <summary>Which store file is this library's, and does the scan after the reset use one. Decided while the file
        /// still exists: its existence is what makes a plain scan adopt it, and the reset is about to remove it.</summary>
        static void TestStoreAfterReset(Action<bool, string> assert)
        {
            string here = Path.Combine(Path.GetTempPath(), "lib", "truedat.db"), other = Path.Combine(Path.GetTempPath(), "elsewhere", "truedat.db");
            string Show((string? Store, bool InForce) r) => (r.Store ?? "none") + "/" + r.InForce;

            var asked = ResetCommand.StoreAfterReset(here, storeExplicit: false, derived: here, derivedExists: true, noStoreTyped: false, configStoreOff: false);
            assert(asked.Store == here && asked.InForce, $"reset-store: a store this run already resolved stays in force ({Show(asked)})");

            var adopted = ResetCommand.StoreAfterReset(null, false, here, derivedExists: true, noStoreTyped: false, configStoreOff: false);
            assert(adopted.Store == here && adopted.InForce, $"reset-store: a store beside the catalog that a plain scan would adopt is recreated after the reset ({Show(adopted)})");

            var refused = ResetCommand.StoreAfterReset(null, false, here, derivedExists: true, noStoreTyped: true, configStoreOff: false);
            assert(refused.Store == here && !refused.InForce, $"reset-store: under --no-store the file is still archived and removed, and no store comes back ({Show(refused)})");

            var configOff = ResetCommand.StoreAfterReset(null, false, here, derivedExists: true, noStoreTyped: false, configStoreOff: true);
            assert(configOff.Store == here && !configOff.InForce, $"reset-store: config store false reads the same way ({Show(configOff)})");

            var none = ResetCommand.StoreAfterReset(null, false, here, derivedExists: false, noStoreTyped: false, configStoreOff: false);
            assert(none.Store == null && !none.InForce, $"reset-store: no store before, no store after ({Show(none)})");

            var guessed = ResetCommand.StoreAfterReset(other, storeExplicit: false, derived: here, derivedExists: true, noStoreTyped: false, configStoreOff: false);
            assert(guessed.Store == here && guessed.InForce, $"reset-store: a store resolved against a guessed catalog gives way to the one beside the real catalog ({Show(guessed)})");

            var named = ResetCommand.StoreAfterReset(other, storeExplicit: true, derived: here, derivedExists: true, noStoreTyped: false, configStoreOff: false);
            assert(named.Store == other && named.InForce, $"reset-store: a store the command line named is the one, wherever it is ({Show(named)})");
        }

        /// <summary>A store whose last changes are still in its write-ahead log (a scan that was killed) must go into the
        /// archive whole. <c>--restore</c> takes the store's main file out of an archive, so anything left in the log would
        /// be in the zip and yet missing from what is restored.</summary>
        static void TestStoreIsWholeBeforeArchiving(Action<bool, string> assert)
        {
            var root = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "reset-wal-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "killed"));
                var live = Path.Combine(root, "live.db");
                var killed = Path.Combine(root, "killed", "truedat.db");
                void CopyShared(string from, string to)
                {
                    using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var dst = File.Create(to);
                    src.CopyTo(dst);
                }
                long GenerationOfMainFileAlone(string db)
                {
                    var alone = Path.Combine(root, "alone-" + Guid.NewGuid().ToString("N") + ".db");
                    CopyShared(db, alone);
                    using var d = global::Truedat.Store.SqliteDb.Open(alone, create: false);
                    using var os = new global::Truedat.Store.StoreDal(d);
                    return os.Generation();
                }
                using (global::Truedat.Store.StoreLog.Quiet())
                {
                    using (var os = global::Truedat.Store.OpenStore.Open(live, true, VersionInfo.Display, DateTime.UtcNow.ToString("o")))
                        os.Dal.AdvanceGeneration();   // generation 1, checkpointed on close
                    using (var w = global::Truedat.Store.SqliteDb.Open(live, create: false))
                    {
                        w.Exec("PRAGMA wal_autocheckpoint=0;");
                        w.Exec("UPDATE store_meta SET value = '777' WHERE key = '" + global::Truedat.Store.StoreDal.GenerationKey + "';");   // committed, in the log only
                        CopyShared(live, killed);
                        CopyShared(live + "-wal", killed + "-wal");
                    }
                    long loose = GenerationOfMainFileAlone(killed);
                    assert(loose == 1, $"reset-wal: the fixture is a store whose last change is only in its log (main file alone reads generation {loose})");
                    bool folded = ResetCommand.FoldStoreLog(killed);
                    long whole = GenerationOfMainFileAlone(killed);
                    assert(folded && whole == 777, $"reset-wal: before archiving, the log is folded into the store's main file (folded {folded}, main file alone reads generation {whole})");
                    assert(!ResetCommand.FoldStoreLog(Path.Combine(root, "missing.db")), "reset-wal: a store that is not there is not an error, and nothing is made");
                    assert(!File.Exists(Path.Combine(root, "missing.db")), "reset-wal: in particular no empty store is created");
                }
            }
            catch (Exception ex) { assert(false, "reset-wal: threw " + ex.GetType().Name + ": " + ex.Message); }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        static void TestCollectAndExecute(Action<bool, string> assert)
        {
            var root = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "reset-" + Guid.NewGuid().ToString("N"));
            try
            {
                var lib = Path.Combine(root, "Library");
                var review = Path.Combine(root, "AppData", "MBXHub", "review");
                var far = Path.Combine(root, "stores");
                Directory.CreateDirectory(lib); Directory.CreateDirectory(review); Directory.CreateDirectory(far);
                void Put(string dir, string name) => File.WriteAllText(Path.Combine(dir, name), "content of " + name);

                // What truedat writes beside a catalog.
                var ours = new[]
                {
                    "mbxmoods.json", "mbxmoods.mbxs", "mbxedges.v1.json", "truedat.db", "truedat.db-wal", "truedat.db-shm",
                    "truedat.db.bak.v2.20260101-000000", "mbxmoods-errors.csv", "mbxmoods-errors.log", "mbxmoods-skipped.csv",
                    "mbxmoods-scan-2026-01-01-1.txt", "mbxmoods-verify.csv", "store-result.json", "store-import-result.json",
                    "mbxmoods.json.bak.20260101-000000.zip", "mbxmoods.json.imported.abc123.zip", "truedat.log",
                };
                // What is not scan output, or is an archive somebody made on purpose, or is not ours at all.
                var theirs = new[]
                {
                    "mbxmoods-exclude.json", "mbxmoods-snapshot-20260101-000000.zip", "mbxmoods-reset-20250101-000000.zip",
                    "truedat.config.json", "notes.txt", "iTunes Music Library.xml",
                };
                foreach (var n in ours) Put(lib, n);
                foreach (var n in theirs) Put(lib, n);
                Put(review, ReviewLedger.FileName);
                Put(review, "preview.json");

                var catalog = Path.Combine(lib, "mbxmoods.json");
                var files = ResetCommand.Collect(catalog, Path.Combine(lib, "truedat.db"), Path.Combine(review, ReviewLedger.FileName));
                var got = string.Join("|", files.Select(f => f.EntryName).OrderBy(s => s, StringComparer.Ordinal));
                var want = string.Join("|", ours.Concat(new[] { "review/" + ReviewLedger.FileName }).OrderBy(s => s, StringComparer.Ordinal));
                assert(got == want, "reset-collect: the file set is everything truedat wrote and nothing else" + (got == want ? "" : $"\n      got  {got}\n      want {want}"));
                assert(files.All(f => File.Exists(f.Path)), "reset-collect: every listed file is a file on disk");

                // No store named: a store sitting at the default place is still this library's file.
                var noStore = ResetCommand.Collect(catalog, null, null);
                assert(!noStore.Any(f => f.EntryName.StartsWith("review/", StringComparison.Ordinal)), "reset-collect: no review path given, no review entry");

                // A store kept somewhere else (--store-dir, or a network library's local store).
                Put(far, "truedat.db"); Put(far, "truedat.db-wal"); Put(far, "store-result.json"); Put(far, "someone-elses.txt");
                Put(far, "truedat.db.bak.v2.20260101-000000");
                // That folder is not this library's: another library's lists, a log and an exported edges file may sit there too.
                Put(far, "mbxmoods-errors.csv"); Put(far, "truedat.log"); Put(far, "mbxedges.v1.json"); Put(far, "other.db");
                var split = ResetCommand.Collect(catalog, Path.Combine(far, "truedat.db"), null);
                var farGot = string.Join("|", split.Where(f => f.EntryName.StartsWith("store/", StringComparison.Ordinal)).Select(f => f.EntryName).OrderBy(s => s, StringComparer.Ordinal));
                assert(farGot == "store/store-result.json|store/truedat.db|store/truedat.db-wal|store/truedat.db.bak.v2.20260101-000000",
                    $"reset-collect: from a store's own folder, only the store, what is named after it, and its result files ({farGot})");

                // ---- a held file stops everything before anything is written or removed
                string? held;
                using (new FileStream(Path.Combine(lib, "mbxmoods-errors.csv"), FileMode.Open, FileAccess.Read, FileShare.Read))
                    held = ResetCommand.FirstHeld(files);
                assert(held != null && held.EndsWith("mbxmoods-errors.csv", StringComparison.OrdinalIgnoreCase), $"reset-held: the file another handle holds is named ({held})");
                assert(ResetCommand.FirstHeld(files) == null, "reset-held: with nothing holding them, nothing is named");

                // ---- an archive that cannot be written removes nothing
                var taken = Path.Combine(lib, "mbxmoods-reset-taken.zip");
                File.WriteAllText(taken, "already here");
                bool threw = false;
                try { ResetCommand.Execute(files, taken); } catch (IOException) { threw = true; }
                assert(threw, "reset-execute: an archive name that is taken is refused");
                assert(files.All(f => File.Exists(f.Path)), "reset-execute: and when the archive is not written, no file is removed");
                assert(File.ReadAllText(taken) == "already here", "reset-execute: the file already at that name is not written over");

                // ---- the real thing, with a leftover from a reset that was stopped while it was archiving
                var staleTemp = Path.Combine(lib, "mbxmoods-reset-20250505-050505.zip.tmp");
                File.WriteAllText(staleTemp, "half an archive");
                var before = files.ToDictionary(f => f.EntryName, f => File.ReadAllBytes(f.Path), StringComparer.Ordinal);
                var zipPath = Path.Combine(lib, "mbxmoods-reset-20260102-030405.zip");
                var result = ResetCommand.Execute(files, zipPath);
                assert(File.Exists(zipPath), "reset-execute: the archive is written");
                assert(!File.Exists(staleTemp), "reset-execute: an unfinished archive left by an earlier reset is removed, not kept for good");
                assert(File.Exists(Path.Combine(lib, "mbxmoods-reset-20250101-000000.zip")), "reset-execute: a finished earlier archive is still kept");
                assert(result.Archived == files.Count && result.NotRemoved.Count == 0, $"reset-execute: every file archived and removed ({result.Archived} of {files.Count}, {result.NotRemoved.Count} left)");
                assert(result.Bytes == before.Values.Sum(b => (long)b.Length), $"reset-execute: the byte count is the files' own ({result.Bytes})");
                var left = files.Where(f => File.Exists(f.Path)).Select(f => f.EntryName).ToList();
                assert(left.Count == 0, "reset-execute: none of them is left on disk" + (left.Count > 0 ? " (" + string.Join(", ", left) + ")" : ""));
                foreach (var n in theirs)
                    assert(File.Exists(Path.Combine(lib, n)) && File.ReadAllText(Path.Combine(lib, n)) == "content of " + n, $"reset-execute: {n} is untouched");
                assert(File.Exists(Path.Combine(review, "preview.json")), "reset-execute: another manifest in the review folder is untouched");
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    var names = string.Join("|", zip.Entries.Select(e => e.FullName).OrderBy(s => s, StringComparer.Ordinal));
                    assert(names == want, "reset-execute: the archive holds exactly the file set" + (names == want ? "" : $"\n      got  {names}"));
                    bool same = true; string firstBad = "";
                    foreach (var e in zip.Entries)
                    {
                        using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms);
                        if (!before.TryGetValue(e.FullName, out var b) || !b.SequenceEqual(ms.ToArray())) { same = false; firstBad = e.FullName; break; }
                    }
                    assert(same, "reset-execute: every archived file holds the bytes that were on disk" + (same ? "" : $" (differs: {firstBad})"));
                }
                assert(ResetCommand.Collect(catalog, Path.Combine(lib, "truedat.db"), Path.Combine(review, ReviewLedger.FileName)).Count == 0,
                    "reset-execute: a second look finds nothing left to reset (the archive itself is kept)");

                var name = Path.GetFileName(ResetCommand.ArchivePathFor(catalog, new DateTime(2026, 1, 2, 3, 4, 5)));
                assert(name == "mbxmoods-reset-20260102-030405.zip", $"reset-archive: named by its time, beside the catalog ({name})");
                assert(ResetCommand.IsKept(name), "reset-archive: and the name it is given is one a later reset keeps");
            }
            catch (Exception ex) { assert(false, "reset: threw " + ex.GetType().Name + ": " + ex.Message); }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>The verb itself, in a child process: refused without a confirmation (nothing changed), and with one the
        /// old state is in the archive and the scan started from nothing.</summary>
        static void TestCli(Action<bool, string> assert)
        {
            var exe = typeof(Program).Assembly.Location;
            var exeDir = Path.GetDirectoryName(exe) ?? ".";
            if (!File.Exists(Path.Combine(exeDir, "essentia_streaming_extractor_music.exe")))
            {
                Console.WriteLine("  NOT RUN  reset through the scan: a library scan refuses to start without essentia_streaming_extractor_music.exe beside the test binary");
                return;
            }
            var root = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "reset-cli-" + Guid.NewGuid().ToString("N"));
            bool keep = false;
            try
            {
                var lib = Path.Combine(root, "Library");
                var music = Path.Combine(root, "music");
                // <root>\AppData\MBXHub makes the review folder resolve INSIDE the fixture, never into a real instance.
                var review = Path.Combine(root, "AppData", "MBXHub", "review");
                var stage = Path.Combine(root, "stage");
                Directory.CreateDirectory(lib); Directory.CreateDirectory(music); Directory.CreateDirectory(review); Directory.CreateDirectory(stage);

                // A track the extractor can analyze, made here when ffmpeg is beside the binary: that is what lets the test
                // say the scan after the reset produced a fresh entry and exited 0. Without ffmpeg the embedded stub stands
                // in — it is silent, so the extractor refuses it, and those two checks are reported as not run.
                var track = Path.Combine(music, "track.flac");
                var ffmpeg = Path.Combine(exeDir, "ffmpeg.exe");
                bool analyzable = false;
                if (File.Exists(ffmpeg))
                {
                    RunChild(ffmpeg, music, "-v error -f lavfi -i sine=frequency=440:duration=30 -ac 2 -ar 44100 -y " + Q(track), out int ffExit);
                    analyzable = ffExit == 0 && File.Exists(track);
                }
                if (!analyzable)
                {
                    track = Path.Combine(music, "track.mp3");
                    string resource = typeof(Program).Assembly.GetManifestResourceNames().Single(n => n.EndsWith("stub.mp3", StringComparison.Ordinal));
                    using (var input = typeof(Program).Assembly.GetManifestResourceStream(resource)!)
                    using (var output = File.Create(track)) input.CopyTo(output);
                }

                var xml = Path.Combine(lib, "iTunes Music Library.xml");
                File.WriteAllText(xml,
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<plist version=\"1.0\">\n<dict>\n\t<key>Tracks</key>\n\t<dict>\n"
                    + "\t\t<key>1</key>\n\t\t<dict>\n\t\t\t<key>Track ID</key><integer>1</integer>\n\t\t\t<key>Name</key><string>track</string>\n"
                    + "\t\t\t<key>Artist</key><string>Fixture</string>\n\t\t\t<key>Total Time</key><integer>30000</integer>\n"
                    + "\t\t\t<key>Location</key><string>" + new Uri(track).AbsoluteUri + "</string>\n\t\t</dict>\n"
                    + "\t</dict>\n</dict>\n</plist>\n");

                // The old state: a catalog whose one entry is NOT something a scan would write, an error list, a skip
                // list, a review record, and the operator's own exclusion file.
                const string OldCatalog = "{\"version\":1,\"generatedBy\":\"reset-selftest\",\"trackCount\":0,\"tracks\":{}}";
                var catalog = Path.Combine(lib, "mbxmoods.json");
                var errors = Path.Combine(lib, "mbxmoods-errors.csv");
                var skipped = Path.Combine(lib, "mbxmoods-skipped.csv");
                var exclude = Path.Combine(lib, ExclusionStore.FileName);
                var ledger = Path.Combine(review, ReviewLedger.FileName);
                File.WriteAllText(catalog, OldCatalog);
                File.WriteAllText(errors, "old error list\r\n");
                File.WriteAllText(skipped, "old skip list\r\n");
                const string Rules = "{\"version\":1,\"rules\":[]}";
                File.WriteAllText(exclude, Rules);
                var seed = new ReviewLedger();
                var t0 = new DateTime(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc);
                seed.Upsert(new ReviewRecord { Path = Path.Combine(music, "ghost.flac"), Disposition = ReviewDisposition.Failed, Reason = "old failure" }, t0, false);
                File.WriteAllText(ledger, seed.ToJson("selftest", t0).ToJsonString());
                var oldLedger = File.ReadAllText(ledger);

                bool ok = true;
                void Check(bool cond, string msg) { if (!cond) { ok = false; keep = true; } assert(cond, msg + (cond ? "" : "  [fixture kept: " + root + "]")); }
                string common = "--no-config " + Q(xml) + " --no-store --stage-dir " + Q(stage) + " -p 1";

                // ---- no confirmation: refused, and nothing on disk moved
                var refused = RunChild(exe, lib, common + " --reset", out int refusedExit);
                File.WriteAllText(Path.Combine(root, "refused.log"), refused);
                Check(refusedExit == 2, $"reset-cli: without a confirmation the run is refused with exit 2 (got {refusedExit})");
                Check(refused.Contains("--imeanit"), "reset-cli: the refusal names the flag that confirms it");
                Check(File.ReadAllText(catalog) == OldCatalog && File.ReadAllText(errors) == "old error list\r\n"
                      && File.ReadAllText(skipped) == "old skip list\r\n" && File.ReadAllText(ledger) == oldLedger,
                    "reset-cli: a refused reset leaves every file as it was");
                Check(Directory.GetFiles(lib, ResetCommand.ArchivePrefix + "*").Length == 0, "reset-cli: a refused reset writes no archive");

                Check(refused.Contains("ignore"), "reset-cli: the list of what goes says the review list's ignore decisions go with it");

                // ---- no library named and none found: a refusal, never a help page and exit 0. Run WITHOUT the confirmation and
                //      with nothing to discover, so whatever a discovery ladder turned up on the machine running this, nothing changes.
                var nowhere = Path.Combine(root, "nowhere"); Directory.CreateDirectory(nowhere);
                var lost = RunChild(exe, nowhere, "--no-config --reset", out int lostExit, appData: nowhere);
                File.WriteAllText(Path.Combine(root, "nolibrary.log"), lost);
                Check(lostExit == 2, $"reset-cli: --reset with no library to reset is refused with exit 2, not reported as a success (got {lostExit})");

                // ---- another verb on the line: refused before anything
                var mixed = RunChild(exe, lib, common + " --reset --imeanit --fixup", out int mixedExit);
                File.WriteAllText(Path.Combine(root, "mixed.log"), mixed);
                Check(mixedExit == 2 && mixed.Contains("--fixup"), $"reset-cli: --reset beside another verb is refused naming it (exit {mixedExit})");
                Check(File.ReadAllText(catalog) == OldCatalog && Directory.GetFiles(lib, ResetCommand.ArchivePrefix + "*").Length == 0,
                    "reset-cli: and that refusal changed nothing either");

                // ---- confirmed
                var done = RunChild(exe, lib, common + " --reset --imeanit", out int doneExit);
                File.WriteAllText(Path.Combine(root, "reset.log"), done);
                var archives = Directory.GetFiles(lib, ResetCommand.ArchivePrefix + "*.zip");
                Check(archives.Length == 1, $"reset-cli: one archive is written beside the catalog ({archives.Length})");
                if (archives.Length == 1)
                {
                    using var zip = ZipFile.OpenRead(archives[0]);
                    string Read(string entry)
                    {
                        var e = zip.GetEntry(entry); if (e == null) return "<absent>";
                        using var r = new StreamReader(e.Open()); return r.ReadToEnd();
                    }
                    Check(Read("mbxmoods.json") == OldCatalog, "reset-cli: the archive holds the catalog as it was");
                    Check(Read("mbxmoods-errors.csv") == "old error list\r\n", "reset-cli: and the error list");
                    Check(Read("mbxmoods-skipped.csv") == "old skip list\r\n", "reset-cli: and the skip list");
                    Check(Read("review/" + ReviewLedger.FileName) == oldLedger, "reset-cli: and the review records");
                    Check(zip.GetEntry(ExclusionStore.FileName) == null, "reset-cli: the exclusion rules are not in it");
                }
                Check(File.ReadAllText(exclude) == Rules, "reset-cli: the exclusion rules are still in place, unchanged");
                Check(!File.Exists(catalog) || !File.ReadAllText(catalog).Contains("reset-selftest"), "reset-cli: the old catalog is gone from the library folder");
                Check(!File.Exists(errors) || !File.ReadAllText(errors).Contains("old error list"), "reset-cli: the old error list is gone");
                Check(!File.Exists(skipped) || !File.ReadAllText(skipped).Contains("old skip list"), "reset-cli: the old skip list is gone");
                Check(!File.Exists(ledger) || !File.ReadAllText(ledger).Contains("old failure"), "reset-cli: the old review record is gone");
                Check(done.Contains("Loading iTunes library"), "reset-cli: the scan then runs in the same call");
                Check(done.Contains("Existing moods: 0") && !done.Contains("(cached"), "reset-cli: and it starts from nothing — no entry loaded, none served from a cache");
                Check(done.Contains("Reset:      archived 4 file(s)"), "reset-cli: the scan's summary says a reset preceded it");
                if (analyzable)
                {
                    Check(doneExit == 0, $"reset-cli: the reset and its scan exit 0 (got {doneExit})");
                    Check(File.Exists(catalog) && File.ReadAllText(catalog).Contains("track.flac"), "reset-cli: the new catalog holds the track, measured by this run");

                    // ---- with a store: the scan after the reset makes the store again
                    string withStore = "--no-config " + Q(xml) + " --store --stage-dir " + Q(stage) + " -p 1";
                    var seeded = RunChild(exe, lib, withStore, out int seededExit);
                    File.WriteAllText(Path.Combine(root, "store-scan.log"), seeded);
                    Check(seededExit == 0 && File.Exists(Path.Combine(lib, "truedat.db")), $"reset-cli: a scan with a store makes one (exit {seededExit})");
                    var again = RunChild(exe, lib, withStore + " --reset --imeanit", out int againExit);
                    File.WriteAllText(Path.Combine(root, "store-reset.log"), again);
                    Check(againExit == 0 && File.Exists(Path.Combine(lib, "truedat.db")), $"reset-cli: a library with a store has one again after the reset (exit {againExit})");
                    Check(again.Contains("truedat.db") && again.Contains("Existing moods: 0"), "reset-cli: the old store was archived with the rest and the scan started from nothing");
                    Check(Directory.GetFiles(lib, ResetCommand.ArchivePrefix + "*.zip").Length == 2, "reset-cli: the second reset wrote its own archive and left the first one alone");

                    // ---- going back without a store in force: the catalog returns, and the run says the archive holds a store too
                    var newest = Directory.GetFiles(lib, ResetCommand.ArchivePrefix + "*.zip").OrderBy(f => f, StringComparer.Ordinal).Last();
                    var back = RunChild(exe, lib, "--no-config --no-store --restore " + Q(newest) + " --moods " + Q(catalog), out int backExit);
                    File.WriteAllText(Path.Combine(root, "restore.log"), back);
                    Check(backExit == 0 && back.Contains("Restored:"), $"reset-cli: --restore of a reset archive brings the catalog back (exit {backExit})");
                    Check(back.Contains("--store"), "reset-cli: and when the archive also holds a store that was not restored, the run says so and how to get it");
                }
                else
                    Console.WriteLine("  NOT RUN  reset-cli exit code and fresh entry: needs ffmpeg.exe beside the test binary to make a track the extractor will analyze");
                if (!ok) Console.WriteLine("    logs: " + root);
            }
            catch (Exception ex)
            {
                keep = true;
                assert(false, "reset-cli: threw " + ex.GetType().Name + ": " + ex.Message + "  [fixture kept: " + root + "]");
            }
            finally
            {
                if (!keep) { try { Directory.Delete(root, true); } catch { } }
            }
        }

        /// <summary>stdin is redirected and closed on purpose: a child that inherited a console would sit at the prompt.</summary>
        static string RunChild(string exe, string cwd, string args, out int exitCode, string? appData = null)
        {
            var start = new ProcessStartInfo
            {
                FileName = exe, WorkingDirectory = cwd, Arguments = args,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            };
            if (appData != null) { start.EnvironmentVariables["APPDATA"] = appData; start.EnvironmentVariables["LOCALAPPDATA"] = appData; }
            using var child = Process.Start(start) ?? throw new InvalidOperationException("could not start the child");
            child.StandardInput.Close();
            var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(180000)) { try { child.Kill(); } catch { } throw new TimeoutException("the child did not exit within 180 seconds"); }
            exitCode = child.ExitCode;
            return stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        }

        // Windows command-line quoting doubles trailing backslashes before the closing quote.
        static string Q(string value) => "\"" + value + new string('\\', value.Length - value.TrimEnd('\\').Length) + "\"";
    }
}
