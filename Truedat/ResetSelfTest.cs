using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;

namespace Truedat
{
    /// <summary>
    /// <c>--clear</c> and <c>--reset</c>: archive this library's truedat files into one zip, remove them, scan from nothing.
    /// The two verbs are one implementation that differs only in what it keeps (<see cref="ResetCommand.Keeps"/>):
    /// <c>--clear</c> keeps the operator's own files (the exclusion rules, apply-result.json, the duplicate verdicts) and
    /// carries the review list's ignore decisions into a fresh list; <c>--reset</c> takes all of it.
    ///
    /// The rules are pinned as functions here (which files, which are kept, what counts as a confirmation,
    /// archive-before-remove, what a restore puts back). The last block runs the real verbs in child processes, because
    /// none of the functions can pin where the scan calls them.
    /// </summary>
    internal static class ResetSelfTest
    {
        internal static void Run(Action<bool, string> assert)
        {
            Console.WriteLine("Clear and reset: archive the library's truedat files, remove them, scan from nothing");
            TestFlags(assert);
            TestKeepTable(assert);
            TestConfirm(assert);
            TestAbsentLibrary(assert);
            TestStoreAfterReset(assert);
            TestStoreIsWholeBeforeArchiving(assert);
            TestCollectAndExecute(assert);
            TestIgnoreCarried(assert);
            TestRuleRestore(assert);
            TestRestoreArchiveShapes(assert);
            TestCli(assert);
        }

        /// <summary>--restore of archives whose shape is not "a catalog and maybe its rules", in child processes (a restore needs no
        /// extractor, so these always run). Review 2026-11-21: #1 a reset archive holding only the exclusion rules installed the rules
        /// AS the catalog (the "first entry" fallback); a custom-named catalog's reset archive restored apply-result.json as the
        /// catalog the same way. #4 a rule entry whose name holds a backslash resolved outside the catalog folder, and was written there.</summary>
        static void TestRestoreArchiveShapes(Action<bool, string> assert)
        {
            var exe = typeof(Program).Assembly.Location;
            var root = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "restore-shapes-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            bool keep = false;
            try
            {
                const string Good = "{\"version\":1,\"generatedBy\":\"restore-shapes\",\"trackCount\":0,\"tracks\":{}}";
                const string Rules = "{\"version\":1,\"rules\":[{\"kind\":\"folder\",\"action\":\"exclude\",\"target\":\"X:\\\\never\"}]}";
                void Check(bool cond, string msg) { if (!cond) keep = true; assert(cond, msg + (cond ? "" : "  [fixture kept: " + root + "]")); }
                string Zip(string dir, string name, params (string Entry, string Content)[] members)
                {
                    Directory.CreateDirectory(dir);
                    var path = Path.Combine(dir, name);
                    using (var fs = new FileStream(path, FileMode.CreateNew))
                    using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                        foreach (var m in members)
                        {
                            using var w = new StreamWriter(zip.CreateEntry(m.Entry).Open());
                            w.Write(m.Content);
                        }
                    return path;
                }

                // ---- #1: a reset archive of a library that had rules and nothing else
                {
                    var lib = Path.Combine(root, "rules-only", "Library");
                    Directory.CreateDirectory(lib);
                    var catalog = Path.Combine(lib, Program.MoodsFileName);
                    File.WriteAllText(catalog, Good);
                    var before = File.ReadAllBytes(catalog);
                    var zip = Zip(Path.Combine(root, "rules-only"), "mbxmoods-reset-20261121-010203.zip", (ExclusionStore.FileName, Rules));
                    var output = RunChild(exe, lib, "--no-config --no-store --restore " + Q(zip) + " --moods " + Q(catalog), out int exit);
                    File.WriteAllText(Path.Combine(root, "rules-only.log"), output);
                    Check(File.ReadAllBytes(catalog).SequenceEqual(before),
                        "restore-shapes #1: a rules-only reset archive leaves the catalog byte for byte as it was (the rules are never installed as the catalog)");
                    var rulesBack = Path.Combine(lib, ExclusionStore.FileName);
                    Check(exit == 0 && File.Exists(rulesBack) && File.ReadAllText(rulesBack) == Rules,
                        $"restore-shapes #1: and it restores what it holds, the rule file, byte for byte (exit {exit})");
                    Check(!Directory.GetFiles(lib, "*.restore.tmp").Any() && !Directory.GetFiles(lib, Program.MoodsFileName + ".bak.*").Any(),
                        "restore-shapes #1: nothing was staged or backed up for a catalog the archive does not hold");
                }

                // ---- #1: an archive holding neither a catalog nor a rule file is refused, and changes nothing
                {
                    var lib = Path.Combine(root, "nothing", "Library");
                    Directory.CreateDirectory(lib);
                    var catalog = Path.Combine(lib, Program.MoodsFileName);
                    File.WriteAllText(catalog, Good);
                    var zip = Zip(Path.Combine(root, "nothing"), "mbxmoods-reset-20261121-010204.zip", ("store-result.json", "{\"kind\":\"x\"}"), ("work/mbxmoods-errors.csv", "e"));
                    var output = RunChild(exe, lib, "--no-config --no-store --restore " + Q(zip) + " --moods " + Q(catalog), out int exit);
                    File.WriteAllText(Path.Combine(root, "nothing.log"), output);
                    Check(exit != 0 && File.ReadAllText(catalog) == Good && output.Contains("no catalog"),
                        $"restore-shapes #1: an archive with no catalog and no rule file is refused with a sentence, the catalog untouched (exit {exit})");
                }

                // ---- #1: a custom-named catalog's reset archive restores THAT catalog, not the first .json in the archive
                {
                    var lib = Path.Combine(root, "custom", "Library");
                    Directory.CreateDirectory(lib);
                    var catalog = Path.Combine(lib, "mbxmoods-alt.json");
                    File.WriteAllText(catalog, "{\"tracks\":{}}");
                    var zip = Zip(Path.Combine(root, "custom"), "mbxmoods-reset-20261121-010205.zip",
                        ("apply-result.json", "{\"schemaVersion\":1,\"kind\":\"exclusion-apply-result\",\"ok\":true}"), ("mbxmoods-alt.json", Good));
                    var output = RunChild(exe, lib, "--no-config --no-store --restore " + Q(zip) + " --moods " + Q(catalog), out int exit);
                    File.WriteAllText(Path.Combine(root, "custom.log"), output);
                    Check(exit == 0 && File.ReadAllText(catalog) == Good,
                        $"restore-shapes #1: a custom-named catalog's reset archive restores its own catalog entry (exit {exit}, catalog now {(File.Exists(catalog) ? File.ReadAllText(catalog) : "<absent>")})");
                }

                // ---- #4: a rule entry whose name walks out of the catalog folder: the whole archive is refused before any write
                {
                    var case4 = Path.Combine(root, "escape");
                    var lib = Path.Combine(case4, "Library");
                    Directory.CreateDirectory(lib);
                    var catalog = Path.Combine(lib, Program.MoodsFileName);
                    File.WriteAllText(catalog, Good);
                    var before = File.ReadAllBytes(catalog);
                    var evilName = ExclusionStore.FileName + "\\..\\..\\x.json";
                    var zip = Zip(case4, "mbxmoods-reset-20261121-010206.zip", (Program.MoodsFileName, "{\"tracks\":{\"restored\":{}}}"), (evilName, Rules));
                    bool fixtureIsTheAttack;
                    using (var z = ZipFile.OpenRead(zip)) fixtureIsTheAttack = z.Entries.Any(e => e.FullName == evilName);
                    Check(fixtureIsTheAttack, "restore-shapes #4: the fixture archive really holds an entry named " + evilName);
                    var output = RunChild(exe, lib, "--no-config --no-store --restore " + Q(zip) + " --moods " + Q(catalog), out int exit);
                    File.WriteAllText(Path.Combine(root, "escape.log"), output);
                    var outside = Path.Combine(case4, "x.json");
                    Check(!File.Exists(outside) && !File.Exists(outside + ".restore.tmp"),
                        $"restore-shapes #4: nothing is written outside the catalog folder ({outside})");
                    Check(exit != 0 && File.ReadAllBytes(catalog).SequenceEqual(before) && !Directory.GetFiles(lib, Program.MoodsFileName + ".bak.*").Any(),
                        $"restore-shapes #4: the whole archive is refused before any write - the catalog is untouched and was not backed up (exit {exit})");
                    Check(output.Contains("is refused") && output.Contains("not a plain file name") && output.Contains(evilName),
                        "restore-shapes #4: the refusal says why and names the entry");
                }
            }
            catch (Exception ex) { keep = true; assert(false, "restore-shapes: threw " + ex.GetType().Name + ": " + ex.Message + "  [fixture kept: " + root + "]"); }
            finally { if (!keep) { try { Directory.Delete(root, true); } catch { } } }
        }

        static void TestFlags(Action<bool, string> assert)
        {
            var R = ResetCommand.Verb.Reset; var Cl = ResetCommand.Verb.Clear;
            string? C(ResetCommand.Verb v, params string[] args) => ResetCommand.ConflictingFlag(args, v);
            assert(C(R, "--reset", "--imeanit", "-p", "8", "--store", "--edges", "--audit") == null,
                "reset-flags: scan options ride with --reset");
            assert(C(Cl, "--clear", "--imeanit", "-p", "8", "--store", "--edges", "--audit") == null,
                "clear-flags: scan options ride with --clear");
            assert(C(R, "--reset", "--fixup") == "--fixup", $"reset-flags: another verb is named ({C(R, "--reset", "--fixup")})");
            assert(C(Cl, "--clear", "--fixup") == "--fixup", $"clear-flags: another verb is named ({C(Cl, "--clear", "--fixup")})");
            assert(C(R, "--reset", "--clear") == "--clear", $"reset-flags: --clear beside --reset is refused, naming it ({C(R, "--reset", "--clear")})");
            assert(C(Cl, "--clear", "--reset") == "--reset", $"clear-flags: --reset beside --clear is refused, naming it ({C(Cl, "--clear", "--reset")})");
            assert(C(R, "--reset", "--analyze-file", "a.flac") == "--analyze-file",
                $"reset-flags: a per-file mode is named ({C(R, "--reset", "--analyze-file", "a.flac")})");
            assert(C(R, "--reset", "--chunk", "1/2") == "--chunk", "reset-flags: --chunk is named");
            assert(C(R, "--reset", "--moods", "x.json") == "--moods", "reset-flags: --moods is named (the library scan's catalog is beside the library)");
            assert(C(R, "--reset", "--stage-dir", "stats") == null, "reset-flags: a value that spells a flag name is a value, not a flag");
            assert(C(R, "reset", "stats") == "stats", "reset-flags: a bare-word verb is caught as the parser would read it");
            assert(C(R, "--reset", Path.Combine("C:", "lib", "iTunes Music Library.xml")) == null,
                "reset-flags: the positional library path is not a flag");
            assert(C(R, "/reset", "/imeanit", "/no-store") == null, "reset-flags: slash spellings are read the same way");
            assert(C(R, "--reset", "--analyze") == null, $"reset-flags: --analyze names the library scan itself ({C(R, "--reset", "--analyze")})");
            assert(C(R, "--reset", "--audit-verdicts") == null, $"reset-flags: --audit-verdicts is --audit with more detail ({C(R, "--reset", "--audit-verdicts")})");

            var known = new HashSet<string>(Program.KnownFlags, StringComparer.OrdinalIgnoreCase);
            var unknown = ResetCommand.ScanOptions.Where(f => !known.Contains(f)).ToList();
            assert(unknown.Count == 0, "reset-flags: every allowed option is a real flag" + (unknown.Count > 0 ? " (not: " + string.Join(", ", unknown) + ")" : ""));
            assert(known.Contains(ResetCommand.Flag) && known.Contains(ResetCommand.ClearFlag) && known.Contains(ResetCommand.ConfirmFlag),
                "reset-flags: --reset, --clear and --imeanit are known flags");

            // A config key becomes an argument on every run. One that either verb refused would make it unusable on that
            // instance, with a message blaming a flag nobody typed.
            var allowed = new HashSet<string>(ResetCommand.ScanOptions, StringComparer.OrdinalIgnoreCase);
            var blocked = TruedatConfig.Settings.Select(s => s.Key).Where(k => !allowed.Contains(k)).ToList();
            assert(blocked.Count == 0, "reset-flags: every config key may ride with --clear and --reset" + (blocked.Count > 0 ? " (refused: " + string.Join(", ", blocked) + ")" : ""));
            bool IsKey(string flag) => TruedatConfig.Settings.Any(s => string.Equals(s.Key, flag, StringComparison.OrdinalIgnoreCase));
            assert(!IsKey(ResetCommand.Flag) && !IsKey(ResetCommand.ClearFlag) && !IsKey(ResetCommand.ConfirmFlag),
                "reset-flags: neither --clear, --reset nor their confirmation can be set in the config file");
        }

        /// <summary>The one function that answers "what does this verb keep", as a table: every row of the task's table, for both verbs.</summary>
        static void TestKeepTable(Action<bool, string> assert)
        {
            const bool K = true, T = false;   // kept / taken (archived, then removed)
            var rows = new (string Name, bool Clear, bool Reset, string Why)[]
            {
                // truedat's analysis data: both take it
                (Program.MoodsFileName, T, T, "the catalog"), ("mbxmoods.json.1", T, T, "a catalog part"), ("mbxmoods.mbxs", T, T, "the sidecar"),
                ("mbxedges.v1.json", T, T, "the edges file"), ("truedat.db", T, T, "the store"), ("truedat.db-wal", T, T, "the store's log"),
                ("store-result.json", T, T, "a store-*.json record"), ("mbxmoods-errors.csv", T, T, "the error list"), ("mbxmoods-skipped.csv", T, T, "the skip list"),
                ("mbxmoods-scan-2026-11-21-1.txt", T, T, "a scan summary"), ("truedat.log", T, T, "the log"),
                ("mbxmoods.json.bak.20261121.010203.zip", T, T, "a catalog backup"), ("truedat.db.bak.v4.20261121-010203", T, T, "a store backup"),
                (ReviewLedger.FileName, T, T, "the review ledger (clear carries its ignore decisions into a new one)"),
                // the duplicate report files: both take them
                ("mbxmoods-duplicates.json", T, T, "the duplicate report"), ("mbxmoods-duplicates.html", T, T, "the duplicate page"),
                ("mbxmoods-duplicate-losers.m3u8", T, T, "the losers playlist"),
                // the operator's own files: clear keeps them, reset takes them
                (ExclusionStore.FileName, K, T, "the exclusion rules"), (ExclusionStore.FileName + ".bak.20260101-000000.zip", K, T, "the rules' own backups"),
                ("mbxmoods-exclude.m3u8", K, T, "the exclusion playlist"), (ExclusionStore.ApplyResultFileName, K, T, "apply-result.json"),
                (WorkFolder.DedupeVerdictsFileName, K, T, "the duplicate verdicts"),
                // kept by both
                ("mbxmoods-snapshot-20260101-000000.zip", K, K, "a snapshot"), ("mbxmoods-reset-20260101-000000.zip", K, K, "an earlier reset archive"),
                ("MBXMOODS-RESET-20260101-000000.ZIP", K, K, "an earlier reset archive, whatever the case"), ("mbxmoods-clear-20260101-000000.zip", K, K, "an earlier clear archive"),
                (TruedatConfig.FileName, K, K, "the config file"), ("mbxmoods-schema.json", K, K, "the field registry"),
                (ExclusionStore.FileName + ExclusionStore.LockFileSuffix, K, K, "the rules' lock file (a mechanism, never deleted)"),
            };
            foreach (var r in rows)
            {
                bool c = ResetCommand.Keeps(ResetCommand.Verb.Clear, r.Name), s = ResetCommand.Keeps(ResetCommand.Verb.Reset, r.Name);
                string Say(bool kept) => kept ? "keeps" : "takes";
                assert(c == r.Clear && s == r.Reset, $"keep table: {r.Name} ({r.Why}): --clear {Say(r.Clear)}, --reset {Say(r.Reset)} (got {Say(c)} / {Say(s)})");
            }
            foreach (var n in new[] { ExclusionStore.FileName, ExclusionStore.ApplyResultFileName, WorkFolder.DedupeVerdictsFileName, "mbxmoods-exclude.m3u8" })
                assert(ResetCommand.IsOperatorFile(n), $"keep table: {n} is one of the operator's own files");
            foreach (var n in new[] { Program.MoodsFileName, "mbxmoods-duplicates.json", ExclusionStore.FileName + ExclusionStore.LockFileSuffix, "notes.txt" })
                assert(!ResetCommand.IsOperatorFile(n), $"keep table: {n} is not one of the operator's own files");
        }

        static void TestConfirm(Action<bool, string> assert)
        {
            foreach (var verb in new[] { ResetCommand.Verb.Clear, ResetCommand.Verb.Reset })
            {
                string word = verb == ResetCommand.Verb.Clear ? "CLEAR" : "RESET", other = verb == ResetCommand.Verb.Clear ? "RESET" : "CLEAR";
                string tag = verb == ResetCommand.Verb.Clear ? "clear-confirm" : "reset-confirm";
                assert(ResetCommand.WordOf(verb) == word, $"{tag}: the word to type is {word} ({ResetCommand.WordOf(verb)})");
                var sink = new StringWriter();
                string? Never() => throw new InvalidOperationException("the prompt was read");
                assert(ResetCommand.Confirm(verb, true, false, Never, sink) == ResetCommand.Confirmation.Confirmed, $"{tag}: --imeanit confirms without asking");
                assert(ResetCommand.Confirm(verb, true, true, Never, sink) == ResetCommand.Confirmation.Confirmed, $"{tag}: --imeanit confirms when nobody is at the console");
                assert(ResetCommand.Confirm(verb, false, true, Never, sink) == ResetCommand.Confirmation.NotAsked,
                    $"{tag}: with no console and no flag nothing is read and nothing is confirmed");
                assert(sink.ToString().Length == 0, $"{tag}: no prompt is written when nobody can answer it");

                ResetCommand.Confirmation Typed(string? line) { var w = new StringWriter(); return ResetCommand.Confirm(verb, false, false, () => line, w); }
                assert(Typed(word) == ResetCommand.Confirmation.Confirmed, $"{tag}: typing {word} confirms");
                assert(Typed("  " + word + "  ") == ResetCommand.Confirmation.Confirmed, $"{tag}: surrounding spaces do not matter");
                assert(Typed(word.ToLowerInvariant()) == ResetCommand.Confirmation.Declined, $"{tag}: the word is case-sensitive (a reflex '{word.ToLowerInvariant()}' is not it)");
                assert(Typed(other) == ResetCommand.Confirmation.Declined, $"{tag}: the other verb's word is not it");
                assert(Typed("yes") == ResetCommand.Confirmation.Declined, $"{tag}: yes is not the word");
                assert(Typed("") == ResetCommand.Confirmation.Declined, $"{tag}: Enter alone declines");
                assert(Typed(null) == ResetCommand.Confirmation.Declined, $"{tag}: end of input declines");
                var asked = new StringWriter();
                ResetCommand.Confirm(verb, false, false, () => "no", asked);
                assert(asked.ToString().Contains(word), $"{tag}: the prompt says which word to type");
            }
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
                    "mbxmoods-duplicates.json", "mbxmoods-duplicate-losers.m3u8",
                };
                // The operator's own files: --clear keeps them, --reset takes them.
                var operators = new[] { "mbxmoods-exclude.json", "apply-result.json", "mbxmoods-duplicates.verdicts.json" };
                // An archive somebody made on purpose, the config file, and what is not truedat's at all: both keep them.
                var theirs = new[]
                {
                    "mbxmoods-snapshot-20260101-000000.zip", "mbxmoods-reset-20250101-000000.zip", "mbxmoods-clear-20250101-000000.zip",
                    "mbxmoods-exclude.json.lock", "truedat.config.json", "notes.txt", "iTunes Music Library.xml",
                };
                foreach (var n in ours) Put(lib, n);
                foreach (var n in operators) Put(lib, n);
                foreach (var n in theirs) Put(lib, n);
                Put(review, ReviewLedger.FileName);
                Put(review, "preview.json");

                var catalog = Path.Combine(lib, "mbxmoods.json");
                string Names(IEnumerable<(string Path, string EntryName)> fs) => string.Join("|", fs.Select(f => f.EntryName).OrderBy(s => s, StringComparer.Ordinal));
                string Want(IEnumerable<string> names) => string.Join("|", names.OrderBy(s => s, StringComparer.Ordinal));
                var ledgerPath = Path.Combine(review, ReviewLedger.FileName);

                var resetFiles = ResetCommand.Collect(ResetCommand.Verb.Reset, catalog, Path.Combine(lib, "truedat.db"), ledgerPath);
                var resetWant = Want(ours.Concat(operators).Concat(new[] { "review/" + ReviewLedger.FileName }));
                assert(Names(resetFiles) == resetWant, "reset-collect: --reset takes everything truedat wrote AND the operator's own files" + (Names(resetFiles) == resetWant ? "" : $"\n      got  {Names(resetFiles)}\n      want {resetWant}"));

                var files = ResetCommand.Collect(ResetCommand.Verb.Clear, catalog, Path.Combine(lib, "truedat.db"), ledgerPath);
                var want = Want(ours.Concat(new[] { "review/" + ReviewLedger.FileName }));
                assert(Names(files) == want, "clear-collect: --clear takes everything truedat wrote and keeps the operator's own files" + (Names(files) == want ? "" : $"\n      got  {Names(files)}\n      want {want}"));
                assert(files.All(f => File.Exists(f.Path)), "reset-collect: every listed file is a file on disk");

                // No store named: a store sitting at the default place is still this library's file.
                var noStore = ResetCommand.Collect(ResetCommand.Verb.Clear, catalog, null, null);
                assert(!noStore.Any(f => f.EntryName.StartsWith("review/", StringComparison.Ordinal)), "reset-collect: no review path given, no review entry");

                // A store kept somewhere else (--store-dir, or a network library's local store).
                Put(far, "truedat.db"); Put(far, "truedat.db-wal"); Put(far, "store-result.json"); Put(far, "someone-elses.txt");
                Put(far, "truedat.db.bak.v2.20260101-000000");
                // That folder is not this library's: another library's lists, a log and an exported edges file may sit there too.
                Put(far, "mbxmoods-errors.csv"); Put(far, "truedat.log"); Put(far, "mbxedges.v1.json"); Put(far, "other.db");
                Put(far, "mbxmoods-exclude.json");
                var split = ResetCommand.Collect(ResetCommand.Verb.Reset, catalog, Path.Combine(far, "truedat.db"), null);
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
                var taken = Path.Combine(lib, "mbxmoods-clear-taken.zip");
                File.WriteAllText(taken, "already here");
                bool threw = false;
                try { ResetCommand.Execute(files, taken); } catch (IOException) { threw = true; }
                assert(threw, "reset-execute: an archive name that is taken is refused");
                assert(files.All(f => File.Exists(f.Path)), "reset-execute: and when the archive is not written, no file is removed");
                assert(File.ReadAllText(taken) == "already here", "reset-execute: the file already at that name is not written over");

                // ---- the real thing (--clear's set), with leftovers from a clear and a reset stopped while archiving
                var staleReset = Path.Combine(lib, "mbxmoods-reset-20250505-050505.zip.tmp");
                var staleClear = Path.Combine(lib, "mbxmoods-clear-20250505-050505.zip.tmp");
                File.WriteAllText(staleReset, "half an archive"); File.WriteAllText(staleClear, "half an archive");
                var operatorTimes = operators.ToDictionary(n => n, n => File.GetLastWriteTimeUtc(Path.Combine(lib, n)));
                var before = files.ToDictionary(f => f.EntryName, f => File.ReadAllBytes(f.Path), StringComparer.Ordinal);
                var zipPath = Path.Combine(lib, "mbxmoods-clear-20260102-030405.zip");
                var result = ResetCommand.Execute(files, zipPath);
                assert(File.Exists(zipPath), "reset-execute: the archive is written");
                assert(!File.Exists(staleReset) && !File.Exists(staleClear), "reset-execute: an unfinished archive left by an earlier clear or reset is removed, not kept for good");
                assert(File.Exists(Path.Combine(lib, "mbxmoods-reset-20250101-000000.zip")) && File.Exists(Path.Combine(lib, "mbxmoods-clear-20250101-000000.zip")),
                    "reset-execute: finished earlier archives are still kept");
                assert(result.Archived == files.Count && result.NotRemoved.Count == 0, $"reset-execute: every file archived and removed ({result.Archived} of {files.Count}, {result.NotRemoved.Count} left)");
                assert(result.Bytes == before.Values.Sum(b => (long)b.Length), $"reset-execute: the byte count is the files' own ({result.Bytes})");
                var left = files.Where(f => File.Exists(f.Path)).Select(f => f.EntryName).ToList();
                assert(left.Count == 0, "reset-execute: none of them is left on disk" + (left.Count > 0 ? " (" + string.Join(", ", left) + ")" : ""));
                foreach (var n in theirs)
                    assert(File.Exists(Path.Combine(lib, n)) && File.ReadAllText(Path.Combine(lib, n)) == "content of " + n, $"reset-execute: {n} is untouched");
                foreach (var n in operators)
                    assert(File.ReadAllText(Path.Combine(lib, n)) == "content of " + n && File.GetLastWriteTimeUtc(Path.Combine(lib, n)) == operatorTimes[n],
                        $"clear-execute: {n} is untouched, bytes and time");
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
                assert(ResetCommand.Collect(ResetCommand.Verb.Clear, catalog, Path.Combine(lib, "truedat.db"), ledgerPath).Count == 0,
                    "reset-execute: a second look finds nothing left to clear (the archive itself is kept)");
                assert(ResetCommand.Collect(ResetCommand.Verb.Reset, catalog, Path.Combine(lib, "truedat.db"), ledgerPath).Select(f => f.EntryName).OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(operators.OrderBy(s => s, StringComparer.Ordinal)),
                    "reset-execute: and what a reset would still take is exactly the operator's own files");

                var when = new DateTime(2026, 1, 2, 3, 4, 5);
                var clearName = Path.GetFileName(ResetCommand.ArchivePathFor(ResetCommand.Verb.Clear, catalog, when));
                var resetName = Path.GetFileName(ResetCommand.ArchivePathFor(ResetCommand.Verb.Reset, catalog, when));
                assert(clearName == "mbxmoods-clear-20260102-030405.zip" && resetName == "mbxmoods-reset-20260102-030405.zip",
                    $"reset-archive: each archive is named for its verb and its time ({clearName}, {resetName})");
                assert(ResetCommand.Keeps(ResetCommand.Verb.Clear, clearName) && ResetCommand.Keeps(ResetCommand.Verb.Reset, clearName)
                       && ResetCommand.Keeps(ResetCommand.Verb.Clear, resetName) && ResetCommand.Keeps(ResetCommand.Verb.Reset, resetName),
                    "reset-archive: and the names they are given are ones a later clear or reset keeps");
            }
            catch (Exception ex) { assert(false, "reset: threw " + ex.GetType().Name + ": " + ex.Message); }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>--clear carries the operator's ignore decisions into a fresh review list: exactly those records, each the
        /// same record byte for byte, in a list of the same kind written by the writer the scan uses.</summary>
        static void TestIgnoreCarried(Action<bool, string> assert)
        {
            var root = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "clear-ignore-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                var t0 = new DateTime(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc);
                var ledger = new ReviewLedger();
                ledger.Upsert(new ReviewRecord { Path = @"D:\Music\ignored.flac", Disposition = ReviewDisposition.Failed, Reason = "decode failed", SizeMb = 12.34, DurationSec = 201.5,
                    Components = { new ReviewComponent { Name = "essentia", Message = "boom", ExitCode = 1, DurationMs = 400 } } }, t0, false);
                ledger.SetState(@"D:\Music\ignored.flac", ReviewState.Ignore);
                ledger.Upsert(new ReviewRecord { Path = @"D:\Music\failed.flac", Disposition = ReviewDisposition.Failed, Reason = "old failure" }, t0, false);
                ledger.Upsert(new ReviewRecord { Path = @"D:\Music\short.flac", Disposition = ReviewDisposition.Skipped, Reason = "shorter than the minimum" }, t0, false);
                var oldPath = Path.Combine(root, "old.json");
                assert(Program.SaveReviewLedger(ledger, oldPath, out _), "clear-ignore: the fixture ledger is written by the scan's writer");
                var loaded = ReviewLedger.Load(oldPath, out var err);
                assert(err == null && loaded.Count == 3, $"clear-ignore: the fixture holds an ignore, a failure and a policy record ({loaded.Count})");

                var carried = loaded.IgnoreOnly();
                assert(carried.Count == 1 && carried.Find(@"D:\Music\ignored.flac")?.State == ReviewState.Ignore,
                    $"clear-ignore: only the ignore record is carried ({carried.Count})");
                var newPath = Path.Combine(root, "new.json");
                assert(Program.SaveReviewLedger(carried, newPath, out _), "clear-ignore: the new list is written by the same writer");
                JsonNode? Record(string file, string path) => (JsonNode.Parse(File.ReadAllText(file))?["records"] as JsonArray)?
                    .FirstOrDefault(r => string.Equals(r?["path"]?.GetValue<string>(), path, StringComparison.Ordinal));
                var a = Record(oldPath, @"D:\Music\ignored.flac")?.ToJsonString();
                var b = Record(newPath, @"D:\Music\ignored.flac")?.ToJsonString();
                assert(a != null && a == b, "clear-ignore: the carried record is the same record, byte for byte" + (a == b ? "" : $"\n      was {a}\n      now {b}"));
                var doc = JsonNode.Parse(File.ReadAllText(newPath));
                assert(doc?["kind"]?.GetValue<string>() == ReviewLedger.Kind && (doc?["records"] as JsonArray)?.Count == 1,
                    "clear-ignore: the new list is a review list of the same kind holding that one record");
                assert(ledger.Count == 3 && loaded.Count == 3, "clear-ignore: picking the ignore records changes nothing in the list they came from");
            }
            catch (Exception ex) { assert(false, "clear-ignore: threw " + ex.GetType().Name + ": " + ex.Message); }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>A reset archive holds the operator's own files at its top level; --restore puts them back where they were,
        /// byte for byte — and refuses, naming the file, rather than write over a rule file that exists now. A snapshot also
        /// carries the exclusion file, and restoring a snapshot leaves it in the archive, as it always has.</summary>
        static void TestRuleRestore(Action<bool, string> assert)
        {
            var root = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "reset-rules-" + Guid.NewGuid().ToString("N"));
            try
            {
                var src = Path.Combine(root, "src"); var lib = Path.Combine(root, "Library");
                Directory.CreateDirectory(src); Directory.CreateDirectory(lib);
                var content = new Dictionary<string, string>
                {
                    ["mbxmoods.json"] = "{\"tracks\":{}}", [ExclusionStore.FileName] = "{\"version\":1,\"rules\":[{\"kind\":\"file\"}]}",
                    [ExclusionStore.ApplyResultFileName] = "{\"kind\":\"exclusion-apply-result\"}", [WorkFolder.DedupeVerdictsFileName] = "{\"version\":1,\"verdicts\":[]}",
                    ["mbxmoods-errors.csv"] = "e",
                };
                foreach (var kv in content) File.WriteAllText(Path.Combine(src, kv.Key), kv.Value);
                var entries = content.Keys.Where(k => k != "mbxmoods-errors.csv").Select(k => (Path.Combine(src, k), k))
                    .Concat(new[] { (Path.Combine(src, "mbxmoods-errors.csv"), "work/mbxmoods-errors.csv") }).ToList();
                var resetZip = Path.Combine(root, "mbxmoods-reset-20260101-000000.zip");
                CatalogArchive.WriteZipBackup(entries, resetZip, requireWholeDocuments: false);

                var rules = ResetCommand.RuleEntries(resetZip).OrderBy(s => s, StringComparer.Ordinal).ToList();
                var wantRules = new[] { ExclusionStore.ApplyResultFileName, WorkFolder.DedupeVerdictsFileName, ExclusionStore.FileName }.OrderBy(s => s, StringComparer.Ordinal).ToList();
                assert(rules.SequenceEqual(wantRules), $"reset-restore: the operator's files in a reset archive are found by name ({string.Join(", ", rules)})");

                var snapZip = Path.Combine(root, "mbxmoods-snapshot-20260101-000000.zip");
                File.WriteAllText(Path.Combine(src, CatalogArchive.ManifestEntryName), "{}");
                CatalogArchive.WriteZipBackup(new[] { (Path.Combine(src, "mbxmoods.json"), "mbxmoods.json"), (Path.Combine(src, CatalogArchive.ManifestEntryName), CatalogArchive.ManifestEntryName),
                                                      (Path.Combine(src, ExclusionStore.FileName), ExclusionStore.FileName) }, snapZip, requireWholeDocuments: false);
                assert(ResetCommand.RuleEntries(snapZip).Count == 0, "reset-restore: a snapshot's exclusion file stays in the archive, as it always has");

                File.WriteAllText(Path.Combine(lib, ExclusionStore.FileName), "a newer decision");
                var conflicts = ResetCommand.RuleConflicts(rules, lib);
                assert(conflicts.Count == 1 && conflicts[0].EndsWith(ExclusionStore.FileName, StringComparison.OrdinalIgnoreCase),
                    $"reset-restore: a rule file that exists now is named before anything is restored ({string.Join(", ", conflicts)})");
                File.Delete(Path.Combine(lib, ExclusionStore.FileName));
                assert(ResetCommand.RuleConflicts(rules, lib).Count == 0, "reset-restore: with none in the way, nothing is named");
                int put = ResetCommand.RestoreRules(resetZip, rules, lib);
                assert(put == 3, $"reset-restore: each one is put back ({put})");
                foreach (var r in wantRules)
                    assert(File.Exists(Path.Combine(lib, r)) && File.ReadAllText(Path.Combine(lib, r)) == content[r], $"reset-restore: {r} is back, byte for byte");
                assert(!File.Exists(Path.Combine(lib, "mbxmoods-errors.csv")) && !File.Exists(Path.Combine(lib, "mbxmoods.json")),
                    "reset-restore: only the operator's files are put back by this step");
            }
            catch (Exception ex) { assert(false, "reset-restore: threw " + ex.GetType().Name + ": " + ex.Message); }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>The verbs themselves, in child processes: refused without a confirmation (nothing changed); --clear keeps
        /// the operator's files and carries the ignore decisions; --reset takes them; --restore of the reset archive puts them
        /// back, and refuses rather than write over one that exists now.</summary>
        static void TestCli(Action<bool, string> assert)
        {
            var exe = typeof(Program).Assembly.Location;
            var exeDir = Path.GetDirectoryName(exe) ?? ".";
            if (!File.Exists(Path.Combine(exeDir, "essentia_streaming_extractor_music.exe")))
            {
                Console.WriteLine("  NOT RUN  clear/reset through the scan: a library scan refuses to start without essentia_streaming_extractor_music.exe beside the test binary");
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
                // in — it is silent, so the extractor refuses it, and those checks are reported as not run.
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

                // The old state: a catalog whose one entry is NOT something a scan would write, an error list, a skip list,
                // a review list with one ignore decision and one failure, and the operator's own files.
                const string OldCatalog = "{\"version\":1,\"generatedBy\":\"reset-selftest\",\"trackCount\":0,\"tracks\":{}}";
                var catalog = Path.Combine(lib, "mbxmoods.json");
                var errors = Path.Combine(lib, "mbxmoods-errors.csv");
                var skipped = Path.Combine(lib, "mbxmoods-skipped.csv");
                var ledger = Path.Combine(review, ReviewLedger.FileName);
                File.WriteAllText(catalog, OldCatalog);
                File.WriteAllText(errors, "old error list\r\n");
                File.WriteAllText(skipped, "old skip list\r\n");
                var rules = new Dictionary<string, string>
                {
                    [ExclusionStore.FileName] = "{\"version\":1,\"rules\":[]}",
                    [ExclusionStore.ApplyResultFileName] = "{\"schemaVersion\":1,\"kind\":\"exclusion-apply-result\",\"ok\":true}",
                    [WorkFolder.DedupeVerdictsFileName] = "{\"version\":1,\"verdicts\":[]}",
                };
                foreach (var kv in rules) File.WriteAllText(Path.Combine(lib, kv.Key), kv.Value);
                var ruleTimes = rules.Keys.ToDictionary(k => k, k => File.GetLastWriteTimeUtc(Path.Combine(lib, k)));
                var ignoredPath = Path.Combine(music, "ignored.flac");
                var seed = new ReviewLedger();
                var t0 = new DateTime(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc);
                seed.Upsert(new ReviewRecord { Path = Path.Combine(music, "ghost.flac"), Disposition = ReviewDisposition.Failed, Reason = "old failure" }, t0, false);
                seed.Upsert(new ReviewRecord { Path = ignoredPath, Disposition = ReviewDisposition.Failed, Reason = "the operator said ignore" }, t0, false);
                seed.SetState(ignoredPath, ReviewState.Ignore);
                Program.SaveReviewLedger(seed, ledger, out _);
                var oldLedger = File.ReadAllText(ledger);
                string? RecordOf(string file, string path)
                {
                    if (!File.Exists(file)) return null;
                    return (JsonNode.Parse(File.ReadAllText(file))?["records"] as JsonArray)?
                        .FirstOrDefault(r => string.Equals(r?["path"]?.GetValue<string>(), path, StringComparison.OrdinalIgnoreCase))?.ToJsonString();
                }
                var ignoredRecord = RecordOf(ledger, ignoredPath);

                bool ok = true;
                void Check(bool cond, string msg) { if (!cond) { ok = false; keep = true; } assert(cond, msg + (cond ? "" : "  [fixture kept: " + root + "]")); }
                string common = "--no-config " + Q(xml) + " --no-store --stage-dir " + Q(stage) + " -p 1";
                bool Unchanged() => File.ReadAllText(catalog) == OldCatalog && File.ReadAllText(errors) == "old error list\r\n"
                                    && File.ReadAllText(skipped) == "old skip list\r\n" && File.ReadAllText(ledger) == oldLedger
                                    && rules.All(kv => File.ReadAllText(Path.Combine(lib, kv.Key)) == kv.Value);
                int Archives(string prefix) => Directory.GetFiles(lib, prefix + "*.zip").Length;

                // ---- no confirmation: refused, nothing on disk moved, and the text says what each verb keeps and takes
                var clearRefused = RunChild(exe, lib, common + " --clear", out int clearRefusedExit);
                File.WriteAllText(Path.Combine(root, "clear-refused.log"), clearRefused);
                Check(clearRefusedExit == 2 && clearRefused.Contains("--imeanit") && clearRefused.Contains("CLEAR"),
                    $"clear-cli: without a confirmation the run is refused with exit 2, naming the word and the flag (got {clearRefusedExit})");
                Check(clearRefused.Contains("exclusion rules") && clearRefused.Contains("duplicate verdicts") && clearRefused.Contains("ignore decisions") && clearRefused.Contains("settings"),
                    "clear-cli: the confirmation text says it keeps your settings, exclusion rules, duplicate verdicts and ignore decisions");
                var resetRefused = RunChild(exe, lib, common + " --reset", out int resetRefusedExit);
                File.WriteAllText(Path.Combine(root, "reset-refused.log"), resetRefused);
                Check(resetRefusedExit == 2 && resetRefused.Contains("RESET"), $"reset-cli: without a confirmation the run is refused with exit 2 (got {resetRefusedExit})");
                var warn = resetRefused.Split('\n').FirstOrDefault(l => l.Contains("removes your exclusion rules")) ?? "";
                Check(warn.Contains("duplicate verdicts") && warn.Contains("--clear"),
                    $"reset-cli: one line says it removes the exclusion rules and the duplicate verdicts, and that --clear keeps them ({warn.Trim()})");
                Check(rules.Keys.All(k => resetRefused.Contains(Path.Combine(lib, k))), "reset-cli: and the files it found are named");
                Check(resetRefused.Contains("ignore"), "reset-cli: the text says the review list's ignore decisions go too");
                Check(Unchanged() && Archives(ResetCommand.ArchivePrefix) + Archives(ResetCommand.ClearArchivePrefix) == 0,
                    "reset-cli: a refused clear or reset leaves every file as it was and writes no archive");

                // ---- no library named and none found: a refusal, never a help page and exit 0. Run WITHOUT the confirmation and
                //      with nothing to discover, so whatever a discovery ladder turned up on the machine running this, nothing changes.
                var nowhere = Path.Combine(root, "nowhere"); Directory.CreateDirectory(nowhere);
                var lost = RunChild(exe, nowhere, "--no-config --reset", out int lostExit, appData: nowhere);
                File.WriteAllText(Path.Combine(root, "nolibrary.log"), lost);
                Check(lostExit == 2, $"reset-cli: --reset with no library to reset is refused with exit 2, not reported as a success (got {lostExit})");
                var lostClear = RunChild(exe, nowhere, "--no-config --clear", out int lostClearExit, appData: nowhere);
                File.WriteAllText(Path.Combine(root, "nolibrary-clear.log"), lostClear);
                Check(lostClearExit == 2, $"clear-cli: --clear with no library is refused with exit 2 (got {lostClearExit})");

                // ---- another verb on the line, the two together, --imeanit alone: refused before anything
                foreach (var (line, named, tag) in new[]
                {
                    (" --reset --imeanit --fixup", "--fixup", "reset-cli: --reset beside another verb"),
                    (" --clear --imeanit --fixup", "--fixup", "clear-cli: --clear beside another verb"),
                    (" --clear --reset --imeanit", "--reset", "clear-cli: --clear and --reset together"),
                    (" --imeanit", "--imeanit", "reset-cli: --imeanit with neither verb"),
                })
                {
                    var mixed = RunChild(exe, lib, common + line, out int mixedExit);
                    File.WriteAllText(Path.Combine(root, "mixed-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".log"), mixed);
                    Check(mixedExit == 2 && mixed.Contains(named), $"{tag} is refused naming {named} (exit {mixedExit})");
                    Check(Unchanged() && Archives(ResetCommand.ArchivePrefix) + Archives(ResetCommand.ClearArchivePrefix) == 0, $"{tag}: and that refusal changed nothing");
                }

                // ---- --clear, confirmed
                var cleared = RunChild(exe, lib, common + " --clear --imeanit", out int clearedExit);
                File.WriteAllText(Path.Combine(root, "clear.log"), cleared);
                var clearZips = Directory.GetFiles(lib, ResetCommand.ClearArchivePrefix + "*.zip");
                Check(clearZips.Length == 1 && Archives(ResetCommand.ArchivePrefix) == 0, $"clear-cli: one mbxmoods-clear-<time>.zip is written ({clearZips.Length})");
                if (clearZips.Length == 1)
                {
                    using var zip = ZipFile.OpenRead(clearZips[0]);
                    string Read(string entry) { var e = zip.GetEntry(entry); if (e == null) return "<absent>"; using var r = new StreamReader(e.Open()); return r.ReadToEnd(); }
                    Check(Read("mbxmoods.json") == OldCatalog && Read("mbxmoods-errors.csv") == "old error list\r\n" && Read("mbxmoods-skipped.csv") == "old skip list\r\n",
                        "clear-cli: the archive holds the catalog and the lists as they were");
                    Check(Read("review/" + ReviewLedger.FileName) == oldLedger, "clear-cli: and the whole review list as it was");
                    Check(rules.Keys.All(k => zip.GetEntry(k) == null), "clear-cli: the operator's own files are not in it");
                }
                Check(rules.All(kv => File.ReadAllText(Path.Combine(lib, kv.Key)) == kv.Value && File.GetLastWriteTimeUtc(Path.Combine(lib, kv.Key)) == ruleTimes[kv.Key]),
                    "clear-cli: the exclusion rules, apply-result.json and the duplicate verdicts are untouched, bytes and time");
                string Now(string p) => File.Exists(p) ? File.ReadAllText(p) : "";
                Check(!Now(catalog).Contains("reset-selftest") && !Now(errors).Contains("old error list") && !Now(skipped).Contains("old skip list"),
                    "clear-cli: the old catalog and lists are gone");
                Check(ignoredRecord != null && RecordOf(ledger, ignoredPath) == ignoredRecord, "clear-cli: the ignore decision is in the new review list, the same record byte for byte");
                Check(RecordOf(ledger, Path.Combine(music, "ghost.flac")) == null, "clear-cli: the other records are gone");
                Check(cleared.Contains("Existing moods: 0") && cleared.Contains("Clear:      archived"), "clear-cli: the scan then starts from nothing, and its summary says a clear preceded it");
                var clearedCatalog = File.Exists(catalog) ? File.ReadAllBytes(catalog) : new byte[0];

                // ---- --reset, confirmed: the operator's files go too
                var reset = RunChild(exe, lib, common + " --reset --imeanit", out int resetExit);
                File.WriteAllText(Path.Combine(root, "reset.log"), reset);
                var resetZips = Directory.GetFiles(lib, ResetCommand.ArchivePrefix + "*.zip");
                Check(resetZips.Length == 1, $"reset-cli: one mbxmoods-reset-<time>.zip is written ({resetZips.Length})");
                if (resetZips.Length == 1)
                {
                    using var zip = ZipFile.OpenRead(resetZips[0]);
                    string Read(string entry) { var e = zip.GetEntry(entry); if (e == null) return "<absent>"; using var r = new StreamReader(e.Open()); return r.ReadToEnd(); }
                    Check(rules.All(kv => Read(kv.Key) == kv.Value), "reset-cli: the archive holds the exclusion rules, apply-result.json and the duplicate verdicts");
                    Check(Read("review/" + ReviewLedger.FileName).Contains("the operator said ignore"), "reset-cli: and the review list with its ignore decision");
                }
                Check(rules.Keys.All(k => !File.Exists(Path.Combine(lib, k))), "reset-cli: and they are gone from the library folder");
                Check(RecordOf(ledger, ignoredPath) == null, "reset-cli: the ignore decision is gone with the review list");
                Check(Archives(ResetCommand.ClearArchivePrefix) == 1, "reset-cli: the earlier clear archive is kept");
                Check(reset.Contains("Reset:      archived"), "reset-cli: the scan's summary says a reset preceded it");

                // ---- --restore of the reset archive: refused while a rule file is in the way, then everything back
                if (resetZips.Length == 1)
                {
                    var newer = Path.Combine(lib, ExclusionStore.FileName);
                    File.WriteAllText(newer, "a newer decision");
                    var catalogBefore = File.Exists(catalog) ? File.ReadAllBytes(catalog) : new byte[0];
                    // A working file an earlier build left beside the catalog, and a work folder elsewhere: a writing run would move
                    // it. A refusal is not allowed to — it changes nothing, so the conflict is found before the folder is settled.
                    var leftover = Path.Combine(lib, "mbxmoods-scan-2025-01-01-9.txt");
                    File.WriteAllText(leftover, "a summary an earlier build left beside the catalog");
                    var elsewhere = Path.Combine(root, "elsewhere");
                    // Named by its bare file name, as the hints print it: finding it is what used to settle the work folder.
                    var blocked = RunChild(exe, lib, "--no-config --no-store --restore " + Q(Path.GetFileName(resetZips[0])) + " --moods " + Q(catalog) + " --work-dir " + Q(elsewhere), out int blockedExit);
                    File.WriteAllText(Path.Combine(root, "restore-blocked.log"), blocked);
                    Check(blockedExit != 0 && blocked.Contains(newer), $"reset-restore-cli: a rule file that exists now stops the restore, named (exit {blockedExit})");
                    Check(File.ReadAllText(newer) == "a newer decision" && (File.Exists(catalog) ? File.ReadAllBytes(catalog) : new byte[0]).SequenceEqual(catalogBefore),
                        "reset-restore-cli: and nothing was changed — not the newer rule file, not the catalog");
                    Check(File.Exists(leftover) && !Directory.Exists(elsewhere) && !blocked.Contains("Work folder: moved"),
                        "reset-restore-cli: the refusal moved nothing: the leftover working file is where it was and no work folder was made");
                    Check(blocked.Contains("move yours back") && !blocked.Contains("copy mbxmoods.json"),
                        "reset-restore-cli: the refusal's way to keep your rule files is one that works (move yours aside, restore, move yours back)");
                    File.Delete(newer); File.Delete(leftover);
                    var back = RunChild(exe, lib, "--no-config --no-store --restore " + Q(resetZips[0]) + " --moods " + Q(catalog), out int backExit);
                    File.WriteAllText(Path.Combine(root, "restore-reset.log"), back);
                    Check(backExit == 0 && back.Contains("Restored:"), $"reset-restore-cli: --restore of the reset archive brings the catalog back (exit {backExit})");
                    Check(!back.Contains("restore left it untouched") && back.Contains(Path.Combine(lib, ExclusionStore.FileName)),
                        "reset-restore-cli: it says the rule files were put back, and not the snapshot's note that they were left in the archive");
                    Check(File.ReadAllBytes(catalog).SequenceEqual(clearedCatalog), "reset-restore-cli: the catalog is byte-identical to the one the reset archived");
                    Check(rules.All(kv => File.Exists(Path.Combine(lib, kv.Key)) && File.ReadAllText(Path.Combine(lib, kv.Key)) == kv.Value),
                        "reset-restore-cli: and every rule file is back, byte for byte");
                }
                if (clearZips.Length == 1)
                {
                    var backClear = RunChild(exe, lib, "--no-config --no-store --restore " + Q(clearZips[0]) + " --moods " + Q(catalog), out int backClearExit);
                    File.WriteAllText(Path.Combine(root, "restore-clear.log"), backClear);
                    Check(backClearExit == 0 && File.Exists(catalog) && File.ReadAllText(catalog) == OldCatalog, $"clear-restore-cli: --restore of the clear archive brings the old catalog back byte for byte (exit {backClearExit})");
                }

                // ---- --restore of a plain catalog named by its full path, into a library whose work folder no run has settled:
                //      the restore stages the catalog as <catalog>.restore.tmp, and settling the folder must not take that file away.
                {
                    var plain = Path.Combine(root, "plain-copy.json");
                    File.WriteAllBytes(plain, clearedCatalog);
                    var fresh = Path.Combine(root, "elsewhere-plain");
                    var plainOut = RunChild(exe, lib, "--no-config --no-store --restore " + Q(plain) + " --moods " + Q(catalog) + " --work-dir " + Q(fresh), out int plainExit);
                    File.WriteAllText(Path.Combine(root, "restore-plain.log"), plainOut);
                    Check(plainExit == 0 && File.Exists(catalog) && File.ReadAllBytes(catalog).SequenceEqual(clearedCatalog),
                        $"restore-cli: a plain catalog restored by full path into a library with an unsettled work folder lands, byte for byte (exit {plainExit})");
                }

                // ---- --reset names a rule file in use that lives outside the library folder, which it does not take
                {
                    var farRules = Path.Combine(root, "rules-elsewhere", ExclusionStore.FileName);
                    Directory.CreateDirectory(Path.GetDirectoryName(farRules)!);
                    File.WriteAllText(farRules, "{\"schemaVersion\":1,\"rules\":[]}");
                    var farOut = RunChild(exe, lib, common + " --reset --exclusions " + Q(farRules), out int farExit);
                    File.WriteAllText(Path.Combine(root, "reset-far-rules.log"), farOut);
                    var farLine = farOut.Split('\n').FirstOrDefault(l => l.Contains(farRules)) ?? "";
                    Check(farExit == 2 && farLine.Contains("--exclusions") && farLine.Contains("not taken"),
                        $"reset-cli: the confirmation says the --exclusions file outside the library is not taken ({farLine.Trim()})");
                    Check(File.Exists(farRules), "reset-cli: and it is left alone");
                }

                if (analyzable)
                {
                    Check(resetExit == 0 && clearedExit == 0, $"reset-cli: the clear, the reset and their scans exit 0 (got {clearedExit}, {resetExit})");

                    // ---- with a store: the scan after the reset makes the store again
                    string withStore = "--no-config " + Q(xml) + " --store --stage-dir " + Q(stage) + " -p 1";
                    var seeded = RunChild(exe, lib, withStore, out int seededExit);
                    File.WriteAllText(Path.Combine(root, "store-scan.log"), seeded);
                    Check(seededExit == 0 && File.Exists(Path.Combine(lib, "truedat.db")), $"reset-cli: a scan with a store makes one (exit {seededExit})");
                    foreach (var verb in new[] { "--clear", "--reset" })
                    {
                        if (verb == "--reset") foreach (var k in rules.Keys) { var p = Path.Combine(lib, k); if (File.Exists(p)) File.Delete(p); }
                        var again = RunChild(exe, lib, withStore + " " + verb + " --imeanit", out int againExit);
                        File.WriteAllText(Path.Combine(root, "store" + verb + ".log"), again);
                        Check(againExit == 0 && File.Exists(Path.Combine(lib, "truedat.db")), $"reset-cli: a library with a store has one again after {verb} (exit {againExit})");
                        Check(again.Contains("truedat.db") && again.Contains("Existing moods: 0"), $"reset-cli: {verb} archived the old store with the rest and the scan started from nothing");
                    }
                    var newest = Directory.GetFiles(lib, ResetCommand.ArchivePrefix + "*.zip").OrderBy(f => f, StringComparer.Ordinal).Last();
                    var back = RunChild(exe, lib, "--no-config --no-store --restore " + Q(newest) + " --moods " + Q(catalog), out int backExit);
                    File.WriteAllText(Path.Combine(root, "restore.log"), back);
                    Check(backExit == 0 && back.Contains("Restored:"), $"reset-cli: --restore of a reset archive brings the catalog back (exit {backExit})");
                    Check(back.Contains("--store"), "reset-cli: and when the archive also holds a store that was not restored, the run says so and how to get it");
                }
                else
                    Console.WriteLine("  NOT RUN  reset-cli exit codes, fresh entries and the store: needs ffmpeg.exe beside the test binary to make a track the extractor will analyze");
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
                FileName = exe, WorkingDirectory = cwd, Arguments = WorkFolderSelfTest.ChildArgs(exe, cwd, args),
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
