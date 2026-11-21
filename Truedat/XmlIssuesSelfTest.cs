using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Truedat
{
    /// <summary>
    /// A library XML can hold a character XML itself does not allow (a control character pasted
    /// into a tag). The reader removes it and carries on. The scan used to print a bare
    /// "WARNING: Stripped 1 invalid XML character(s)" on every run: it read as a fault, named
    /// nothing, and there was nothing to do about it.
    ///
    /// What matters is where the character was. In a title or a comment, nothing is affected. In
    /// a file path, that track may not be found — which is worth one line naming the track.
    /// </summary>
    internal static class XmlIssuesSelfTest
    {
        internal static void Run(Action<bool, string> assert)
        {
            Console.WriteLine("Library XML: a removed character says what it touched, and only when it matters");
            var root = Path.Combine(Path.GetTempPath(), ".truedat-selftest", "xml-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                string Track(int id, string name, string location, string extra = "")
                    => "\t\t<key>" + id + "</key>\n\t\t<dict>\n\t\t\t<key>Track ID</key><integer>" + id + "</integer>\n"
                     + "\t\t\t<key>Name</key><string>" + name + "</string>\n"
                     + "\t\t\t<key>Artist</key><string>Fixture</string>\n"
                     + extra
                     + "\t\t\t<key>Location</key><string>" + location + "</string>\n\t\t</dict>\n";
                string Doc(string tracks)
                    => "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<plist version=\"1.0\">\n<dict>\n\t<key>Tracks</key>\n\t<dict>\n" + tracks + "\t</dict>\n</dict>\n</plist>\n";

                var marked = Path.Combine(root, "marked.xml");
                File.WriteAllText(marked, Doc(
                      Track(1, "one\u0001", "file://localhost/C:/music/one.flac")
                    + Track(2, "two", "file://localhost/C:/music/tw\u0002o.flac")
                    + Track(3, "three", "file://localhost/C:/music/three.flac", "\t\t\t<key>Comments</key><string>note\u0003</string>\n")),
                    new UTF8Encoding(false));

                var savedOut = Console.Out; var savedErr = Console.Error;
                var captured = new StringWriter();
                List<ITunesTrack> tracks; List<string>? all; List<string>? paths;
                Console.SetOut(captured); Console.SetError(captured);
                try { tracks = ITunesParser.Parse(marked, out all, out paths); }
                finally { Console.SetOut(savedOut); Console.SetError(savedErr); }

                assert(tracks.Count == 3 && tracks[0].Name == "one" && tracks[1].Location.EndsWith("two.flac", StringComparison.OrdinalIgnoreCase),
                    $"xml-issues: the library is read with the characters removed ({tracks.Count} tracks, '{(tracks.Count > 0 ? tracks[0].Name : "")}')");
                assert(captured.ToString().Length == 0, $"xml-issues: reading the library prints nothing by itself ('{captured.ToString().Trim()}')");
                string Join(List<string>? l) => l == null ? "<null>" : string.Join(" | ", l.Select(s => s.Trim()));
                assert(all != null && all.Count == 3, $"xml-issues: every removed character is on the detailed list ({Join(all)})");
                assert(all != null && all.Any(s => s.Contains("U+0001") && s.Contains("Name") && s.Contains("Fixture - one")),
                    $"xml-issues: one in a title names the field and the track ({Join(all)})");
                assert(all != null && all.Any(s => s.Contains("U+0003") && s.Contains("Comments") && s.Contains("Fixture - three")),
                    $"xml-issues: one in a field the scan does not read is still named ({Join(all)})");
                assert(paths != null && paths.Count == 1 && paths[0].Contains("Fixture - two") && paths[0].Contains("path"),
                    $"xml-issues: only the one in a file path is worth a line by default, and it names the track ({Join(paths)})");
                assert(paths != null && paths.Count == 1 && !paths[0].Contains("WARNING") && !paths[0].Contains("Error"),
                    "xml-issues: and that line is not dressed as a warning or an error");

                var clean = Path.Combine(root, "clean.xml");
                File.WriteAllText(clean, Doc(Track(1, "one", "file://localhost/C:/music/one.flac")), new UTF8Encoding(false));
                ITunesParser.Parse(clean, out var cleanAll, out var cleanPaths);
                assert(cleanAll == null && cleanPaths == null, "xml-issues: a library with nothing removed has nothing to report");

                ITunesParser.Parse(marked, out var legacy);
                assert(legacy != null && legacy.Count == 3, "xml-issues: the two-argument call still returns the detailed list");
            }
            catch (Exception ex) { assert(false, "xml-issues: threw " + ex.GetType().Name + ": " + ex.Message); }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
