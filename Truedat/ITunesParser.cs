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
        /// <summary>True when the XML Location is a remote URL (http/https) rather than
        /// a file — e.g. un-downloaded podcast-feed episodes, which MusicBee exports
        /// with the stream URL as their location. Not scannable; Location keeps the
        /// original URL for skip-ledger display.</summary>
        public bool IsRemote { get; set; }
        /// <summary>True if the iTunes XML carries an explicit speech label (a podcast
        /// flag or podcast genre) — one kind of evidence that this track is talk, not
        /// music.</summary>
        public bool IsSpeech { get; set; }
        /// <summary>What triggered the speech label ("Podcast=true" — iTunes-native
        /// boolean — or "Genre=Podcast" — MusicBee exports) — traceability for skip
        /// logs. These literals are the external identifiers we matched, not our class
        /// name.</summary>
        public string SpeechReason { get; set; } = "";
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
        private readonly List<(int Line, int Code)> _removed = new List<(int Line, int Code)>();

        // Pending low surrogate from a valid pair split across Read() calls
        private int _pending = -1;

        public int StrippedCount => _stripped;
        /// <summary>Each character removed, with the line it was on, in the order it was read.</summary>
        public List<(int Line, int Code)> Removed => _removed;

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
            _removed.Add((_line, (int)c));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    public static class ITunesParser
    {
        /// <summary>
        /// Why this file is not an iTunes library XML, or null when it looks like one.
        ///
        /// The library path is whatever landed in the positional argument slot, and
        /// <c>ResolveITunesXml</c> returns an explicit one verbatim — so the only gate used to be
        /// <c>File.Exists</c>. Field report 2026-10-26: a stray <c>.\truedat.db</c> on a scan's
        /// command line passed that gate and a SQLite store went into the XML reader. Any
        /// XML document begins with '&lt;' once a BOM and leading whitespace are stepped over, so
        /// sniffing the first bytes costs nothing and cannot reject a real library.
        ///
        /// Names what it found rather than just refusing: "not an iTunes library" sends the
        /// operator back to the command line, "that is a SQLite database" tells them which
        /// argument is wrong.
        /// </summary>
        public static string? NotALibraryReason(string path)
        {
            byte[] head = new byte[512];
            int n;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                n = fs.Read(head, 0, head.Length);
            }
            catch
            {
                // Unreadable, absent, locked: not our story to tell. The caller's File.Exists
                // gate already has a message for a missing library, and a reader that turns a
                // transient share hiccup into "that is not a library" would be worse than the
                // hole this closes.
                return null;
            }

            // Named formats first: "that is a SQLite database" points at the wrong ARGUMENT,
            // where "not an XML document" only says the run failed.
            if (Begins(head, n, "SQLite format 3\0"))
                return "it is a SQLite database — truedat's own store is one";
            if (n >= 4 && head[0] == 0x50 && head[1] == 0x4B && (head[2] == 0x03 || head[2] == 0x05 || head[2] == 0x07))
                return "it is a zip archive — a catalog backup or pin, perhaps";

            // Step over a byte-order mark and any leading whitespace. 0x00 counts as skippable
            // so UTF-16 reaches its first real character ('<' is 3C 00 little-endian, 00 3C big).
            int i = 0;
            if (n >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) i = 3;
            else if (n >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF))) i = 2;
            while (i < n && (head[i] == 0x20 || head[i] == 0x09 || head[i] == 0x0A || head[i] == 0x0D || head[i] == 0x00)) i++;

            if (i >= n)
                return n == 0 ? "it is empty" : "it holds no document";

            // Every XML document opens with '<' once the BOM and whitespace are behind us, so
            // this can never reject a real library — and it catches the realistic wrong
            // arguments: the moods catalog ('{'), a CSV, a log, a media file.
            if (head[i] == (byte)'<') return null;

            if (head[i] == (byte)'{' || head[i] == (byte)'[')
                return "it is a JSON file — the moods catalog is JSON, the library is not";
            return "it does not begin with an XML document";
        }

        static bool Begins(byte[] buf, int n, string ascii)
        {
            if (n < ascii.Length) return false;
            for (int i = 0; i < ascii.Length; i++) if (buf[i] != (byte)ascii[i]) return false;
            return true;
        }

        public static List<ITunesTrack> Parse(string xmlPath, out List<string>? xmlIssues)
            => Parse(xmlPath, out xmlIssues, out _);

        /// <summary>Read the library. <paramref name="xmlIssues"/> lists every character that had to be removed because XML
        /// cannot hold it, each with the field and track it was in (null when there were none) — detail for --audit.
        /// <paramref name="pathIssues"/> holds the ones worth a line on any run: a character removed from a file PATH, where
        /// the track may then not be found. One in a title or a comment changes nothing a scan does, so it is not on that
        /// list; a line about it on every run read as a fault and gave the reader nothing to do. Prints nothing itself.</summary>
        public static List<ITunesTrack> Parse(string xmlPath, out List<string>? xmlIssues, out List<string>? pathIssues)
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
            var notes = new RemovedCharNotes(sanitizer, reader as IXmlLineInfo);

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

            if (!reader.Read()) { notes.Finish(out xmlIssues, out pathIssues); return result; }

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
                        var track = ReadTrackDict(reader, id, notes);
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

            notes.Finish(out xmlIssues, out pathIssues);
            return result;
        }

        /// <summary>Says where each removed character was. The sanitizer knows the line; the reader knows which field and
        /// which track a line belongs to; this joins the two as the tracks are read. The sanitizer reads ahead of the
        /// reader, so a character on a line is already recorded by the time the reader gets to that line.</summary>
        sealed class RemovedCharNotes
        {
            readonly SanitizingTextReader _source;
            readonly IXmlLineInfo? _lines;
            int _next;   // the first removed character not yet placed
            readonly List<(int Line, int Code, string Field)> _inTrack = new List<(int Line, int Code, string Field)>();
            readonly List<string> _all = new List<string>();
            readonly List<string> _paths = new List<string>();

            public RemovedCharNotes(SanitizingTextReader source, IXmlLineInfo? lines)
            {
                _source = source;
                _lines = lines != null && lines.HasLineInfo() ? lines : null;
            }

            /// <summary>The line the reader is on, or 0 when it cannot say.</summary>
            public int Line => _lines?.LineNumber ?? 0;

            /// <summary>A field that began on <paramref name="fromLine"/> and whose next sibling begins on <paramref name="toLine"/>.</summary>
            public void Field(int fromLine, int toLine, string field)
            {
                if (fromLine <= 0) return;
                var removed = _source.Removed;
                while (_next < removed.Count)
                {
                    var r = removed[_next];
                    if (r.Line < fromLine) { _all.Add($"  line {r.Line}: U+{r.Code:X4} removed (not in a track's field)"); _next++; continue; }
                    bool inside = r.Line < toLine || (toLine <= fromLine && r.Line == fromLine);
                    if (!inside) break;
                    _inTrack.Add((r.Line, r.Code, field));
                    _next++;
                }
            }

            public void EndTrack(ITunesTrack track)
            {
                if (_inTrack.Count == 0) return;
                string label = Label(track);
                foreach (var r in _inTrack)
                {
                    _all.Add($"  line {r.Line}: U+{r.Code:X4} removed from {r.Field} of {label}");
                    if (r.Field == "Location")
                        _paths.Add($"  NOTE: the library lists {label} with a character in its file path that XML cannot hold (line {r.Line}, U+{r.Code:X4}). "
                                 + "The path was read without it; if that track is reported missing, rename the file and export the library again.");
                }
                _inTrack.Clear();
            }

            static string Label(ITunesTrack t)
            {
                string artist = (t.Artist ?? "").Trim(), name = (t.Name ?? "").Trim();
                if (artist.Length > 0 && name.Length > 0) return artist + " - " + name;
                if (name.Length > 0) return name;
                if (!string.IsNullOrEmpty(t.Location)) { try { return Path.GetFileName(t.Location); } catch { } }
                return "track " + t.TrackId;
            }

            /// <summary>Anything removed up to where the reader stopped that no field claimed is listed plainly; what the
            /// sanitizer read ahead beyond that point belongs to parts of the file the scan does not read.</summary>
            public void Finish(out List<string>? all, out List<string>? paths)
            {
                int reached = _lines != null ? Line : int.MaxValue;
                var removed = _source.Removed;
                for (; _next < removed.Count && removed[_next].Line <= reached; _next++)
                    _all.Add($"  line {removed[_next].Line}: U+{removed[_next].Code:X4} removed (not in a track's field)");
                all = _all.Count > 0 ? _all : null;
                paths = _paths.Count > 0 ? _paths : null;
            }
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

        private static ITunesTrack ReadTrackDict(XmlReader reader, int id, RemovedCharNotes notes)
        {
            var track = new ITunesTrack { TrackId = id };
            int depth = reader.Depth;

            reader.Read(); // move inside the <dict>

            while (reader.Depth > depth)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "key")
                {
                    int keyLine = notes.Line;
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
                            var rawLocation = reader.ReadElementContentAsString();
                            // Remote stream URLs (un-downloaded podcast-feed episodes)
                            // must be flagged BEFORE ParseLocation: Uri.LocalPath mangles
                            // "https://host/p/x/audio.mp3" into a fake local path
                            // ("\p\x\audio.mp3") that then masquerades as a missing file.
                            if (rawLocation.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                                || rawLocation.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                            {
                                track.Location = rawLocation;
                                track.IsRemote = true;
                            }
                            else
                            {
                                track.Location = ParseLocation(rawLocation);
                            }
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
                            // Episode Date is deliberately NOT consulted: MusicBee maps
                            // ID3v2.4 TDRL (a RELEASE date per spec, repurposed by Apple
                            // for podcast episodes) into that key, so it rides on ordinary
                            // music. Removed 050868f, briefly returned as a vote anchor,
                            // deleted for good 2026-07-24. Do not reinstate.
                            if (reader.Name == "true")
                            {
                                track.IsSpeech = true;
                                track.SpeechReason = "Podcast=true";
                            }
                            SkipElement(reader);
                            break;
                        default:
                            // Skip value elements we don't care about
                            SkipElement(reader);
                            break;
                    }
                    notes.Field(keyLine, notes.Line, key);   // the reader is on whatever follows this field's value
                }
                else
                {
                    reader.Read();
                }
            }

            // Consume the end element of this track's <dict>
            if (reader.NodeType == XmlNodeType.EndElement)
                reader.Read();
            notes.EndTrack(track);

            // MusicBee's iTunes XML export has no Podcast boolean, no podcast Kind,
            // and no Track Type marker — a Genre=Podcast tag is the speech label it
            // does carry (the same rule ReportCatalog's speech class stat applies to
            // stored entries, Program.cs — --migrate uses no such rule, it only strips
            // legacy fields, see CLAUDE.md's Scan exclusions section).
            // Checked after the dict so key order doesn't matter.
            if (!track.IsSpeech && string.Equals(track.Genre, "Podcast", StringComparison.OrdinalIgnoreCase))
            {
                track.IsSpeech = true;
                track.SpeechReason = "Genre=Podcast";
            }

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
