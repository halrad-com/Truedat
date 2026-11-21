using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Truedat
{
    /// <summary>
    /// Scanning from a list of paths (<c>--file-list</c>): the entry a launcher uses to hand truedat
    /// the tracks it knows about, without the library XML.
    ///
    /// Three things had to hold for that to be usable as the normal way new tracks arrive: a path
    /// listed twice is analyzed once; several lists on one command line are all read; and a later
    /// library scan does not put an older XML's text over what a list scan read from the file.
    /// </summary>
    internal static class ListScanSelfTest
    {
        internal static void Run(Action<bool, string> assert)
        {
            Console.WriteLine("List scans: each path once, every list read, and an older XML does not win");
            TestDedupe(assert);
            TestXmlText(assert);
            TestCli(assert);
        }

        static void TestDedupe(Action<bool, string> assert)
        {
            string a = Path.Combine("D:" + Path.DirectorySeparatorChar, "Music", "a.flac");
            string b = Path.Combine("D:" + Path.DirectorySeparatorChar, "Music", "b.flac");
            var unique = Program.DedupePaths(new[] { a, b, a, a.ToUpperInvariant(), a.Replace(Path.DirectorySeparatorChar, '/'), b }, out int repeated);
            assert(unique.Count == 2 && repeated == 4, $"list-dedupe: one path in several spellings, several times, is one path ({unique.Count} kept, {repeated} repeats)");
            assert(unique.Count == 2 && unique[0] == a && unique[1] == b, "list-dedupe: the first spelling and the listed order are kept");
            var none = Program.DedupePaths(new[] { a, b }, out repeated);
            assert(none.Count == 2 && repeated == 0, "list-dedupe: a list with no repeats is returned as it is");
            assert(Program.DedupePaths(new string[0], out repeated).Count == 0 && repeated == 0, "list-dedupe: an empty list is an empty list");
        }

        /// <summary>A library scan takes a track's title, artist, album and genre from the XML. That is right when the XML
        /// is the newer of the two. A file written after the XML was written has text the XML cannot know about, and a
        /// list scan may already have read it from the file's tags.</summary>
        static void TestXmlText(Action<bool, string> assert)
        {
            var xmlWritten = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
            assert(!Program.XmlTextIsOlderThanFile(xmlWritten.AddHours(-1), xmlWritten), "list-xml: a file last written before the XML: the XML's text is current");
            assert(Program.XmlTextIsOlderThanFile(xmlWritten.AddHours(1), xmlWritten), "list-xml: a file written after the XML: the XML's text may be behind the file's");
            assert(!Program.XmlTextIsOlderThanFile(xmlWritten, xmlWritten), "list-xml: the same second is not 'after'");
            assert(!Program.XmlTextIsOlderThanFile(xmlWritten.AddHours(1), default(DateTime)), "list-xml: with no XML time known, the XML is used as before");

            var saved = Program.LibraryXmlWrittenUtc;
            try
            {
                Program.LibraryXmlWrittenUtc = xmlWritten;
                var held = new TrackEntry
                {
                    Features = new TrackFeatures { Artist = "Tag artist", Title = "Tag title", Album = "Tag album", Genre = "Tag genre", Bpm = 120 },
                    LastModified = xmlWritten.AddHours(1),
                };
                var listed = new ITunesTrack { TrackId = 7, Artist = "XML artist", Name = "XML title", Album = "XML album", Genre = "XML genre", Location = "x" };

                var newer = Program.RebuildCacheEntry(held, listed, xmlWritten.AddHours(1), null, null);
                assert(newer.Features.Title == "Tag title" && newer.Features.Artist == "Tag artist" && newer.Features.Album == "Tag album" && newer.Features.Genre == "Tag genre",
                    $"list-xml: a cached track whose file is newer than the XML keeps the text it already has ({newer.Features.Artist} - {newer.Features.Title})");
                assert(newer.Features.Bpm == 120 && newer.Features.TrackId == 7, "list-xml: and everything else about the cache hit is as before");

                var older = Program.RebuildCacheEntry(held, listed, xmlWritten.AddHours(-1), null, null);
                assert(older.Features.Title == "XML title" && older.Features.Artist == "XML artist",
                    $"list-xml: a cached track whose file is older than the XML takes the XML's text, as before ({older.Features.Artist} - {older.Features.Title})");

                var blank = new TrackEntry { Features = new TrackFeatures { Artist = "", Title = "", Album = "", Genre = "" } };
                var filled = Program.RebuildCacheEntry(blank, listed, xmlWritten.AddHours(1), null, null);
                assert(filled.Features.Title == "XML title" && filled.Features.Artist == "XML artist", "list-xml: an entry holding no text takes the XML's rather than staying empty");
            }
            finally { Program.LibraryXmlWrittenUtc = saved; }
        }

        /// <summary>Two lists on one command line, overlapping and each repeating a path. The paths do not exist, so
        /// nothing is analyzed; what is being read is how many the scan set out to process.</summary>
        static void TestCli(Action<bool, string> assert)
        {
            var exe = typeof(Program).Assembly.Location;
            var exeDir = Path.GetDirectoryName(exe) ?? ".";
            if (!File.Exists(Path.Combine(exeDir, "essentia_streaming_extractor_music.exe")))
            {
                Console.WriteLine("  NOT RUN  list scan through the command line: a scan refuses to start without essentia_streaming_extractor_music.exe beside the test binary");
                return;
            }
            var root = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "list-" + Guid.NewGuid().ToString("N"));
            bool keep = false;
            try
            {
                Directory.CreateDirectory(root);
                string P(string name) => Path.Combine(root, "music", name);
                var first = Path.Combine(root, "first.txt");
                var second = Path.Combine(root, "second.txt");
                // The last list alone holds two paths; both lists together hold three. A run that kept only the last list
                // would say two.
                File.WriteAllLines(first, new[] { P("a.flac"), P("b.flac"), P("a.flac"), P("A.FLAC") });
                File.WriteAllLines(second, new[] { P("b.flac"), P("c.flac") });
                var catalog = Path.Combine(root, "mbxmoods.json");
                string Q(string v) => "\"" + v + "\"";

                var output = RunChild(exe, root, "--no-config --no-store --file-list " + Q(first) + " --file-list " + Q(second) + " --moods " + Q(catalog) + " -p 1", out int exit);
                File.WriteAllText(Path.Combine(root, "run.log"), output);
                void Check(bool cond, string msg) { if (!cond) keep = true; assert(cond, msg + (cond ? "" : "  [fixture kept: " + root + "]")); }
                Check(output.Contains("Processing 3 files"), "list-cli: two lists holding a, b, a, A and b, c are three files to process");
                Check(output.Contains("more than once"), "list-cli: and the run says some paths were listed more than once");
                Check(exit == 0, $"list-cli: listed files that are not on disk are skipped, not an error for the run (exit {exit})");
            }
            catch (Exception ex) { keep = true; assert(false, "list-cli: threw " + ex.GetType().Name + ": " + ex.Message + "  [fixture kept: " + root + "]"); }
            finally { if (!keep) { try { Directory.Delete(root, true); } catch { } } }
        }

        static string RunChild(string exe, string cwd, string args, out int exitCode)
        {
            var start = new ProcessStartInfo
            {
                FileName = exe, WorkingDirectory = cwd, Arguments = args, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            };
            using var child = Process.Start(start) ?? throw new InvalidOperationException("could not start the child");
            child.StandardInput.Close();
            var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(120000)) { try { child.Kill(); } catch { } throw new TimeoutException("the child did not exit within 120 seconds"); }
            exitCode = child.ExitCode;
            return stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        }
    }
}
