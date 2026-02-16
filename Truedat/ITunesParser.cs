using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Truedat
{
    public class ITunesTrack
    {
        public int TrackId { get; set; }
        public string Name { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Album { get; set; } = "";
        public string Genre { get; set; } = "";
        public string Location { get; set; } = "";
        /// <summary>Duration in milliseconds from iTunes XML (Total Time). 0 if unavailable.</summary>
        public int TotalTimeMs { get; set; }
    }

    public static class ITunesParser
    {
        public static List<ITunesTrack> Parse(string xmlPath, out List<string>? xmlIssues)
        {
            var xml = SanitizeXml(File.ReadAllText(xmlPath, Encoding.UTF8), out xmlIssues);
            var doc = XDocument.Parse(xml);

            var root = doc.Root;
            if (root == null)
                throw new InvalidOperationException("Invalid iTunes library XML: missing root element.");

            var plistDict = root.Element("dict");
            if (plistDict == null)
                throw new InvalidOperationException("Invalid iTunes library XML: missing root <dict>.");

            var tracksKey = plistDict.Elements("key").FirstOrDefault(k => k.Value == "Tracks");
            if (tracksKey == null)
                throw new InvalidOperationException("Invalid iTunes library XML: no 'Tracks' key found. Is this a valid iTunes Music Library.xml file?");

            var tracksDict = tracksKey.ElementsAfterSelf("dict").FirstOrDefault();
            if (tracksDict == null)
                throw new InvalidOperationException("Invalid iTunes library XML: no tracks dictionary found after 'Tracks' key.");

            var result = new List<ITunesTrack>();
            var elements = tracksDict.Elements().ToList();
            for (int i = 0; i < elements.Count - 1; i++)
            {
                if (elements[i].Name != "key") continue;
                if (!int.TryParse(elements[i].Value, out var id)) continue;
                var dict = elements[i + 1];
                if (dict.Name != "dict") continue;
                var track = ParseTrackDict(id, dict);
                if (!string.IsNullOrEmpty(track.Location))
                    result.Add(track);
            }

            return result;
        }

        private static ITunesTrack ParseTrackDict(int id, XElement dict)
        {
            var track = new ITunesTrack { TrackId = id };

            var elems = dict.Elements().ToList();
            for (int i = 0; i < elems.Count - 1; i++)
            {
                if (elems[i].Name != "key") continue;
                var key = elems[i].Value;
                var valElem = elems[i + 1];

                switch (key)
                {
                    case "Name": track.Name = valElem.Value; break;
                    case "Artist": track.Artist = valElem.Value; break;
                    case "Album": track.Album = valElem.Value; break;
                    case "Genre": track.Genre = valElem.Value; break;
                    case "Location": track.Location = ParseLocation(valElem.Value); break;
                    case "Total Time": int.TryParse(valElem.Value, out var ms); track.TotalTimeMs = ms; break;
                }
            }

            return track;
        }

        private static string ParseLocation(string location)
        {
            if (string.IsNullOrWhiteSpace(location)) return "";

            // Use System.Uri for proper URL handling — correctly decodes percent-encoding
            // and all RFC 8089 file URI forms
            try
            {
                var uri = new Uri(location);
                var path = uri.LocalPath;
                // Uri treats file://localhost/ as UNC \\localhost\ — convert back to local path
                if (path.StartsWith(@"\\localhost\", StringComparison.OrdinalIgnoreCase))
                    path = path.Substring(@"\\localhost\".Length);
                return PathHelper.NormalizeSeparators(path);
            }
            catch
            {
                // Fallback for malformed URIs
                var path = location
                    .Replace("file://localhost/", "")
                    .Replace("file:///", "")
                    .Replace("file://", "");
                path = Uri.UnescapeDataString(path);
                return PathHelper.NormalizeSeparators(path);
            }
        }

        private static string SanitizeXml(string xml, out List<string>? issues)
        {
            issues = null;

            // Fast path: scan for invalid XML 1.0 characters; if none, return input with zero allocation
            // Valid XML 1.0: #x9 | #xA | #xD | [#x20-#xD7FF] | [#xE000-#xFFFD] | [#x10000-#x10FFFF]
            // UTF-16 surrogate pairs (D800-DFFF) encode codepoints U+10000+ and are valid when paired
            for (int i = 0; i < xml.Length; i++)
            {
                var c = xml[i];
                if (c == 0x9 || c == 0xA || c == 0xD || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD))
                    continue;
                if (char.IsHighSurrogate(c) && i + 1 < xml.Length && char.IsLowSurrogate(xml[i + 1]))
                    { i++; continue; }
                goto needsSanitization;
            }
            return xml;

            needsSanitization:
            int stripped = 0;
            issues = new List<string>();
            var sb = new StringBuilder(xml.Length);
            int line = 1;
            for (int j = 0; j < xml.Length; j++)
            {
                var c = xml[j];
                if (c == 0x9 || c == 0xA || c == 0xD || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD))
                {
                    sb.Append(c);
                    if (c == 0xA) line++;
                }
                else if (char.IsHighSurrogate(c) && j + 1 < xml.Length && char.IsLowSurrogate(xml[j + 1]))
                {
                    // Valid surrogate pair — emoji and other U+10000+ codepoints
                    sb.Append(c);
                    sb.Append(xml[++j]);
                }
                else
                {
                    stripped++;
                    var start = Math.Max(0, j - 40);
                    var end = Math.Min(xml.Length, j + 40);
                    var snippet = new StringBuilder(end - start + 10);
                    for (int k = start; k < end; k++)
                    {
                        var sc = xml[k];
                        if (sc < 0x20 && sc != 0x9 && sc != 0xA && sc != 0xD) snippet.Append($"[U+{(int)sc:X4}]");
                        else if (sc == '\r' || sc == '\n') snippet.Append(' ');
                        else snippet.Append(sc);
                    }
                    issues.Add($"  Line {line}: U+{(int)c:X4} in ...{snippet}...");
                }
            }
            Console.WriteLine($"WARNING: Stripped {stripped} invalid XML character(s) from library file");
            return sb.ToString();
        }
    }
}
