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
    }

    public static class ITunesParser
    {
        public static List<ITunesTrack> Parse(string xmlPath)
        {
            // Read and sanitize XML - iTunes can have invalid control characters
            var xml = SanitizeXml(File.ReadAllText(xmlPath));
            var doc = XDocument.Parse(xml);

            // Find the "Tracks" dict
            var plistDict = doc.Root!.Element("dict");
            var keys = plistDict!.Elements("key").ToList();
            var tracksKey = keys.First(k => k.Value == "Tracks");
            var tracksDict = tracksKey.ElementsAfterSelf("dict").First();

            var result = new List<ITunesTrack>();

            // Each track is a <key>id</key><dict>...</dict> pair
            var elements = tracksDict.Elements().ToList();
            for (int i = 0; i < elements.Count; i++)
            {
                if (elements[i].Name == "key")
                {
                    var id = int.Parse(elements[i].Value);
                    var dict = elements[i + 1];
                    var track = ParseTrackDict(id, dict);
                    if (!string.IsNullOrEmpty(track.Location))
                        result.Add(track);
                }
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
                }
            }

            return track;
        }

        private static string ParseLocation(string location)
        {
            // iTunes stores paths as file:// URLs
            // Windows: file://localhost/C:/Users/...
            // Mac: file:///Users/...
            var path = location
                .Replace("file://localhost/", "")
                .Replace("file:///", "")
                .Replace("file://", "");
            path = Uri.UnescapeDataString(path);

            // Normalize to Windows backslash paths (iTunes uses forward slashes)
            path = path.Replace('/', '\\');

            return path;
        }

        private static string SanitizeXml(string xml)
        {
            // Remove invalid XML 1.0 characters (control chars except tab, newline, carriage return)
            var sb = new StringBuilder(xml.Length);
            foreach (var c in xml)
            {
                if (c == 0x9 || c == 0xA || c == 0xD || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD))
                    sb.Append(c);
                // else skip invalid character
            }
            return sb.ToString();
        }
    }
}
