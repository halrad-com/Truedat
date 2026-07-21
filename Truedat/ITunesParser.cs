using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

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
        /// <summary>File size in bytes from iTunes XML (Size). 0 if unavailable.
        /// Feeds the scan ETA model (bytes remaining / measured MB/s) without per-file stats.</summary>
        public long SizeBytes { get; set; }
        /// <summary>True if the iTunes XML marks this track as a podcast episode.</summary>
        public bool IsPodcast { get; set; }
        /// <summary>Which XML key(s) triggered the podcast classification
        /// ("Podcast=true", "Episode Date", or both) — traceability for skip logs.</summary>
        public string PodcastReason { get; set; } = "";
    }

    /// <summary>
    /// TextReader wrapper that strips invalid XML 1.0 characters on the fly,
    /// avoiding the need to load the entire file into memory for sanitization.
    /// Valid XML 1.0: #x9 | #xA | #xD | [#x20-#xD7FF] | [#xE000-#xFFFD] | [#x10000-#x10FFFF]
    /// UTF-16 surrogate pairs (D800-DFFF) encode codepoints U+10000+ and are valid when paired.
    /// </summary>
    sealed class SanitizingTextReader : TextReader
    {
        private readonly StreamReader _inner;
        private int _stripped;
        private int _line = 1;
        private List<string>? _issues;

        // Pending low surrogate from a valid pair split across Read() calls
        private int _pending = -1;

        public int StrippedCount => _stripped;
        public List<string>? Issues => _issues;

        public SanitizingTextReader(StreamReader inner)
        {
            _inner = inner;
        }

        public override int Read()
        {
            if (_pending >= 0)
            {
                int p = _pending;
                _pending = -1;
                return p;
            }

            while (true)
            {
                int raw = _inner.Read();
                if (raw < 0) return -1;

                char c = (char)raw;

                if (c == 0x9 || c == 0xA || c == 0xD || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD))
                {
                    if (c == 0xA) _line++;
                    return c;
                }

                if (char.IsHighSurrogate(c))
                {
                    int next = _inner.Read();
                    if (next >= 0 && char.IsLowSurrogate((char)next))
                    {
                        _pending = next;
                        return c;
                    }
                    // Unpaired high surrogate — strip it (and put back next if valid)
                    RecordStripped(c);
                    if (next >= 0)
                    {
                        // Re-evaluate 'next' on the next iteration by recursing
                        char nc = (char)next;
                        if (nc == 0x9 || nc == 0xA || nc == 0xD || (nc >= 0x20 && nc <= 0xD7FF) || (nc >= 0xE000 && nc <= 0xFFFD))
                        {
                            if (nc == 0xA) _line++;
                            return nc;
                        }
                        RecordStripped(nc);
                    }
                    continue;
                }

                RecordStripped(c);
            }
        }

        public override int Read(char[] buffer, int index, int count)
        {
            int filled = 0;
            while (filled < count)
            {
                int ch = Read();
                if (ch < 0) break;
                buffer[index + filled] = (char)ch;
                filled++;
            }
            return filled;
        }

        private void RecordStripped(char c)
        {
            _stripped++;
            _issues ??= new List<string>();
            _issues.Add($"  Line {_line}: U+{(int)c:X4}");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    public static class ITunesParser
    {
        public static List<ITunesTrack> Parse(string xmlPath, out List<string>? xmlIssues)
        {
            // Stream the XML through a sanitizing reader — never loads the full file into memory.
            // Previous approach used File.ReadAllText + XDocument.Parse which required ~10x the
            // file size in memory (UTF-16 string + sanitization copy + DOM tree).
            using var sr = new StreamReader(xmlPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 65536);
            using var sanitizer = new SanitizingTextReader(sr);

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreWhitespace = true,
                IgnoreComments = true,
            };
            using var reader = XmlReader.Create(sanitizer, settings);

            // Navigate to root <plist> element
            if (!reader.ReadToFollowing("plist"))
                throw new InvalidOperationException("Invalid iTunes library XML: missing root element.");

            // Find the root <dict> inside <plist>
            if (!reader.ReadToDescendant("dict"))
                throw new InvalidOperationException("Invalid iTunes library XML: missing root <dict>.");

            // Scan keys in the root dict to find "Tracks"
            if (!AdvanceToKey(reader, "Tracks"))
                throw new InvalidOperationException("Invalid iTunes library XML: no 'Tracks' key found. Is this a valid iTunes Music Library.xml file?");

            // AdvanceToKey leaves reader on the node right after <key>Tracks</key>,
            // which should be the tracks <dict> element itself. Don't use ReadToNextSibling
            // here — that would skip past it looking for the *next* dict sibling.
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "dict")
                throw new InvalidOperationException("Invalid iTunes library XML: no tracks dictionary found after 'Tracks' key.");

            // Now inside the tracks dict — read track entries
            var result = new List<ITunesTrack>();
            int depth = reader.Depth;

            if (!reader.Read()) { FinalizeIssues(sanitizer, out xmlIssues); return result; }

            while (reader.Depth > depth)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "key")
                {
                    var idText = reader.ReadElementContentAsString();
                    if (!int.TryParse(idText, out var id))
                    {
                        // Skip whatever follows this non-numeric key
                        if (reader.NodeType == XmlNodeType.Element) SkipElement(reader); else reader.Read();
                        continue;
                    }

                    // Next element should be this track's <dict>
                    if (reader.NodeType == XmlNodeType.Element && reader.Name == "dict")
                    {
                        var track = ReadTrackDict(reader, id);
                        if (!string.IsNullOrEmpty(track.Location))
                            result.Add(track);
                    }
                    else
                    {
                        // Unexpected element — skip it
                        if (reader.NodeType == XmlNodeType.Element) SkipElement(reader); else reader.Read();
                    }
                }
                else
                {
                    reader.Read();
                }
            }

            FinalizeIssues(sanitizer, out xmlIssues);
            return result;
        }

        private static void FinalizeIssues(SanitizingTextReader sanitizer, out List<string>? xmlIssues)
        {
            xmlIssues = sanitizer.Issues;
            if (sanitizer.StrippedCount > 0)
                Console.WriteLine($"WARNING: Stripped {sanitizer.StrippedCount} invalid XML character(s) from library file");
        }

        /// <summary>
        /// Scan sibling key elements looking for one with the given value.
        /// Leaves reader positioned just after that key's content.
        /// </summary>
        private static bool AdvanceToKey(XmlReader reader, string targetKey)
        {
            int depth = reader.Depth;
            if (!reader.Read()) return false;

            while (reader.Depth > depth)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "key")
                {
                    var keyValue = reader.ReadElementContentAsString();
                    if (keyValue == targetKey)
                        return true;
                    // Skip the value element that follows this key
                    if (reader.NodeType == XmlNodeType.Element)
                        SkipElement(reader);
                }
                else
                {
                    reader.Read();
                }
            }
            return false;
        }

        private static ITunesTrack ReadTrackDict(XmlReader reader, int id)
        {
            var track = new ITunesTrack { TrackId = id };
            int depth = reader.Depth;

            reader.Read(); // move inside the <dict>

            while (reader.Depth > depth)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "key")
                {
                    var key = reader.ReadElementContentAsString();

                    // reader is now on the value element (string, integer, true, false, etc.)
                    if (reader.NodeType != XmlNodeType.Element)
                    {
                        reader.Read();
                        continue;
                    }

                    switch (key)
                    {
                        case "Name":
                            track.Name = reader.ReadElementContentAsString();
                            break;
                        case "Artist":
                            track.Artist = reader.ReadElementContentAsString();
                            break;
                        case "Album":
                            track.Album = reader.ReadElementContentAsString();
                            break;
                        case "Genre":
                            track.Genre = reader.ReadElementContentAsString();
                            break;
                        case "Location":
                            track.Location = ParseLocation(reader.ReadElementContentAsString());
                            break;
                        case "Total Time":
                            var val = reader.ReadElementContentAsString();
                            if (int.TryParse(val, out var ms))
                                track.TotalTimeMs = ms;
                            break;
                        case "Size":
                            var sizeVal = reader.ReadElementContentAsString();
                            if (long.TryParse(sizeVal, out var sizeBytes))
                                track.SizeBytes = sizeBytes;
                            break;
                        case "Podcast":
                            // iTunes/Apple Music writes <key>Podcast</key><true/>.
                            // Sticky: only set on <true/> so key order can't un-flag.
                            if (reader.Name == "true")
                            {
                                track.IsPodcast = true;
                                track.PodcastReason = track.PodcastReason.Length == 0
                                    ? "Podcast=true" : track.PodcastReason + " + Podcast=true";
                            }
                            SkipElement(reader);
                            break;
                        case "Episode Date":
                            // MusicBee writes <key>Episode Date</key> for podcast episodes.
                            // Heuristic — presence of the key alone classifies. The skip log
                            // records this reason so false positives are diagnosable.
                            track.IsPodcast = true;
                            track.PodcastReason = track.PodcastReason.Length == 0
                                ? "Episode Date" : track.PodcastReason + " + Episode Date";
                            SkipElement(reader);
                            break;
                        default:
                            // Skip value elements we don't care about
                            SkipElement(reader);
                            break;
                    }
                }
                else
                {
                    reader.Read();
                }
            }

            // Consume the end element of this track's <dict>
            if (reader.NodeType == XmlNodeType.EndElement)
                reader.Read();

            return track;
        }

        /// <summary>
        /// Skip the current element and all its children, leaving the reader
        /// positioned on the next sibling node.
        /// </summary>
        private static void SkipElement(XmlReader reader)
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.IsEmptyElement)
                    reader.Read();
                else
                    reader.Skip();
            }
            else
            {
                reader.Read();
            }
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
                {
                    path = path.Substring(@"\\localhost\".Length);
                    // iTunes encodes a real UNC path as file://localhost//server/share/... which
                    // .NET parses to \\localhost\\server\share\... — stripping leaves a single
                    // leading backslash. Restore the second one to form a valid UNC path.
                    if (path.StartsWith(@"\") && !path.StartsWith(@"\\"))
                        path = @"\" + path;
                }
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
    }
}
