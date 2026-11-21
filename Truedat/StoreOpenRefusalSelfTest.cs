using System;
using System.Collections.Concurrent;
using System.IO;
using Truedat.Store;

namespace Truedat
{
    /// <summary>
    /// Two things a run record and a scan must get right about the store.
    ///
    /// A store file that cannot be written (its read-only attribute is set, or the account may not write it) is refused
    /// in one sentence when it is opened — before a backup is made of it, before a migration starts, and before a scan
    /// analyzes anything it could not then save. And a scan's record names the store generation the run ended at, not
    /// the one it read before its last publication moved it.
    /// </summary>
    internal static class StoreOpenRefusalSelfTest
    {
        internal static void Run(Action<bool, string> assert)
        {
            Console.WriteLine("Store: a file that cannot be written is refused at open; the run record names the final generation");
            TestReadOnly(assert);
            TestRecordGeneration(assert);
            TestCli(assert);
        }

        static string NewDir() { var d = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "storeopen-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }
        static void MakeStore(string path, int schema) { using (var db = SqliteDb.Open(path, create: true)) SchemaMigrator.Apply(db, "selftest", schema); }
        static void Writable(string path) { try { if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal); } catch { } }

        static void TestReadOnly(Action<bool, string> assert)
        {
            var dir = NewDir();
            var store = Path.Combine(dir, "truedat.db");
            try
            {
                MakeStore(store, 4);
                assert(OpenStore.NotWritableReason(store) == null, "store-open: an ordinary store file is writable");
                assert(OpenStore.NotWritableReason(Path.Combine(dir, "absent.db")) == null, "store-open: a store that does not exist yet is not 'read-only'");
                File.SetAttributes(store, FileAttributes.ReadOnly);
                assert(OpenStore.NotWritableReason(store) != null, "store-open: a store file with the read-only attribute is seen as not writable");

                string? message = null; bool threwPlain = false;
                try { using (OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o"))) { } }
                catch (StoreException ex) { threwPlain = true; message = ex.Message; }
                catch (Exception ex) { message = ex.GetType().Name + ": " + ex.Message; }
                assert(threwPlain && message != null && message.Contains("read-only") && message.Contains(store),
                    $"store-open: opening it is refused in one sentence that names the file and says read-only ({message})");
                assert(Directory.GetFiles(dir, "truedat.db.bak.*").Length == 0, "store-open: and no backup of it was made first — nothing was going to change");

                var target = new ConcurrentDictionary<string, TrackEntry>(PathComparer.Instance);
                StoreScan.LoadResult? load = null; string? thrown = null;
                try { load = StoreScan.LoadAuthoritative(store, Path.Combine(dir, Program.MoodsFileName), target); }
                catch (Exception ex) { thrown = ex.GetType().Name; }
                assert(thrown == null && load != null && load.Refused && (load.Refusal ?? "").Contains("read-only"),
                    $"store-open: a scan's load of that store is a refusal, not an exception ({thrown ?? load?.Refusal ?? "not refused"})");

                Writable(store);
                bool opened = false;
                try { using (OpenStore.Open(store, false, VersionInfo.Display, DateTime.UtcNow.ToString("o"))) opened = true; } catch { }
                assert(opened, "store-open: with the attribute cleared the same store opens");
            }
            finally { Writable(store); try { Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>The record's store block is read at a flush; a publication after it can advance the generation (the edge
        /// version stamp does). The publication knows the generation it published at, and the record takes it.</summary>
        static void TestRecordGeneration(Action<bool, string> assert)
        {
            var dir = NewDir();
            var store = Path.Combine(dir, "truedat.db");
            var record = Path.Combine(dir, StoreResultFile.FileName);
            StoreResult.ResetScanForTest();
            try
            {
                StoreResult.BeginScan(store, "2026-11-21T00:00:00.0000000Z");
                StoreResult.NoteScanExport(new ExportReport { Published = true, Generation = 7 }, null);
                StoreResult.FinishScan(0);
                bool read = StoreResult.TryRead(record, out var r);
                assert(read && r.Export.Performed && r.Export.Generation == 7 && r.Store.Generation == 7,
                    $"record-generation: after a publication the record's store generation is the one published at (store {r.Store.Generation?.ToString() ?? "null"}, export {r.Export.Generation?.ToString() ?? "null"})");

                StoreResult.BeginScan(store, "2026-11-21T00:00:00.0000000Z");
                StoreResult.NoteScanExport(new ExportReport { Published = true, Generation = 7 }, null);
                StoreResult.NoteScanExport(new ExportReport { Published = false, Generation = 9 }, new IOException("disk full"));
                StoreResult.FinishScan(1);
                bool read2 = StoreResult.TryRead(record, out var r2);
                assert(read2 && !r2.Export.Performed && r2.Store.Generation == 7,
                    $"record-generation: a publication that failed does not move it ({r2.Store.Generation?.ToString() ?? "null"})");
            }
            finally { StoreResult.ResetScanForTest(); try { Directory.Delete(dir, true); } catch { } }
        }

        static void TestCli(Action<bool, string> assert)
        {
            var exe = typeof(Program).Assembly.Location;
            var exeDir = Path.GetDirectoryName(exe) ?? ".";
            if (!File.Exists(Path.Combine(exeDir, "essentia_streaming_extractor_music.exe")))
            {
                Console.WriteLine("  NOT RUN  a read-only store through a scan: a scan refuses to start without essentia_streaming_extractor_music.exe beside the test binary");
                return;
            }
            var root = NewDir();
            var store = Path.Combine(root, "truedat.db");
            bool keep = false;
            try
            {
                string Q(string v) => "\"" + v + "\"";
                void Check(bool cond, string msg) { if (!cond) keep = true; assert(cond, msg + (cond ? "" : "  [fixture kept: " + root + "]")); }
                var catalog = Path.Combine(root, "mbxmoods.json");
                var list = Path.Combine(root, "new.txt");
                File.WriteAllLines(list, new[] { Path.Combine(root, "music", "gone.flac") });

                // ---- an older store marked read-only: the scan refuses; it does not crash on the way through the upgrade
                MakeStore(store, 4);
                File.SetAttributes(store, FileAttributes.ReadOnly);
                var output = ListRecordSelfTest.RunChild(exe, root, "--no-config --store --no-edges --file-list " + Q(list) + " --moods " + Q(catalog) + " -p 1", out int exit);
                File.WriteAllText(Path.Combine(root, "run-readonly.log"), output);
                Check(!output.Contains("Unhandled Exception"), "store-open-cli: a scan over a read-only store does not end in an unhandled error");
                Check(exit == 2, $"store-open-cli: it is refused with an ordinary exit code ({exit})");
                Check(output.Contains("read-only"), "store-open-cli: and says the store is read-only");
                Check(Directory.GetFiles(root, "truedat.db.bak.*").Length == 0, "store-open-cli: no backup was written for an upgrade that could not happen");
                bool have = StoreResult.TryRead(Path.Combine(root, StoreResultFile.FileName), out var r);
                Check(have && r.Outcome == StoreResult.OutcomeFailed && r.ExitCode == 2 && (r.Reason ?? "").Contains("read-only"),
                    $"store-open-cli: the run record says why ({(have ? r.Outcome + ", " + r.ExitCode + ", " + r.Reason : "no record")})");
                Writable(store);

                // ---- a list scan with a store and edges: the record's store generation is the store's
                var ffmpeg = Path.Combine(exeDir, "ffmpeg.exe");
                var fresh = Path.Combine(root, "fresh"); var music = Path.Combine(fresh, "music"); Directory.CreateDirectory(music);
                if (File.Exists(ffmpeg)) ListRecordSelfTest.RunChild(ffmpeg, music, "-v error -f lavfi -i sine=frequency=440:duration=25 -ac 2 -ar 44100 -y tone.flac", out _);
                var track = Path.Combine(music, "tone.flac");
                if (!File.Exists(track)) { Console.WriteLine("  NOT RUN  the record's generation after a scan that folds edges: needs ffmpeg.exe beside the test binary to make a track"); return; }
                var list2 = Path.Combine(fresh, "new.txt"); File.WriteAllLines(list2, new[] { track });
                var out2 = ListRecordSelfTest.RunChild(exe, fresh, "--no-config --store --file-list " + Q(list2) + " --moods " + Q(Path.Combine(fresh, "mbxmoods.json")) + " -p 1", out int exit2);
                File.WriteAllText(Path.Combine(root, "run-edges.log"), out2);
                bool have2 = StoreResult.TryRead(Path.Combine(fresh, StoreResultFile.FileName), out var r2);
                long stored = -1;
                try { using (var db = SqliteDb.OpenReadOnly(Path.Combine(fresh, "truedat.db"))) stored = new StoreDal(db).Generation(); } catch { }
                Check(exit2 == 0 && have2 && r2.Export.Performed && r2.Store.Generation == stored && r2.Export.Generation == stored && stored > 0,
                    $"store-open-cli: after a list scan that folds edges the record's store generation is the store's (record {r2.Store.Generation?.ToString() ?? "null"}, published {r2.Export.Generation?.ToString() ?? "null"}, store {stored}, exit {exit2})");
            }
            catch (Exception ex) { keep = true; assert(false, "store-open-cli: the test itself failed: " + ex.Message + "  [fixture kept: " + root + "]"); }
            finally { Writable(store); if (!keep) { try { Directory.Delete(root, true); } catch { } } }
        }
    }
}
