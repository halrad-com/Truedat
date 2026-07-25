using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Truedat
{
    /// <summary>
    /// Everything a bounded header sniff found in one file, graded by what each
    /// marker actually asserts. Three different claims used to be flattened into
    /// one "first marker wins" verdict:
    ///   strong      — ID3 PCST / MP4 pcst: an app asserting "this IS a podcast".
    ///   provenance  — ID3 WFED / TGID, MP4 purl: "this came from a feed", which
    ///                 says nothing about content (music is distributed by RSS too).
    ///   genre text  — ID3 TCON exactly "Podcast": author-authored free text.
    /// This is evidence for a human reading the --preview review surface, not a
    /// verdict; nothing in truedat skips work on it.
    /// </summary>
    internal sealed class PodcastMarkers
    {
        /// <summary>Markers asserting the item IS a podcast (PCST, pcst).</summary>
        public List<string> Strong { get; } = new List<string>();

        /// <summary>Markers asserting feed provenance only (WFED, TGID, purl).</summary>
        public List<string> Provenance { get; } = new List<string>();

        /// <summary>TCON genre text was exactly "Podcast" (trimmed, case-insensitive).</summary>
        public bool GenreText { get; set; }

        public bool Any => Strong.Count > 0 || Provenance.Count > 0 || GenreText;

        internal void AddStrong(string name) { if (!Strong.Contains(name)) Strong.Add(name); }

        internal void AddProvenance(string name) { if (!Provenance.Contains(name)) Provenance.Add(name); }

        /// <summary>Short human string, strongest evidence first. This lands in the
        /// preview evidence column, so its job is to tell an operator how much to
        /// trust the finding — not merely that something was found.</summary>
        public string Describe()
        {
            var parts = new List<string>();
            if (Strong.Count > 0) parts.Add(string.Join("+", Strong.ToArray()));
            if (Provenance.Count > 0) parts.Add(string.Join("+", Provenance.ToArray()) + " (feed provenance)");
            if (GenreText) parts.Add("genre text");
            return parts.Count == 0 ? "none" : string.Join(", ", parts.ToArray());
        }
    }

    /// <summary>
    /// Detects explicit podcast markers embedded in the audio FILE itself —
    /// markers podcast downloaders write even when the library XML carries no
    /// genre: ID3v2 PCST (iTunes podcast flag), WFED (feed URL), TGID (episode
    /// GUID), TCON genre exactly "Podcast"; MP4/M4A pcst / purl atoms. Every
    /// marker present is reported, graded by strength — first-match-wins would
    /// throw away exactly the distinction a reviewing human needs.
    /// Pure-managed, bounded header reads, never throws (null on anything
    /// unexpected). House pattern: Mp3LameTagParser.
    /// </summary>
    internal static class PodcastTagSniffer
    {
        // ID3v2 tags larger than this are not walked — real-world podcast tags
        // sit well under it; beyond is embedded artwork we don't care about.
        private const int MaxId3Walk = 128 * 1024;
        private const int MaxAtomsVisited = 64;

        /// <summary>Read exactly count bytes unless EOF intervenes — Stream.Read may
        /// return short before EOF (network streams). House pattern: Mp3LameTagParser.</summary>
        private static bool ReadFully(Stream fs, byte[] buf, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int r = fs.Read(buf, offset + total, count - total);
                if (r <= 0) return false;
                total += r;
            }
            return true;
        }

        /// <summary>Every podcast marker found in the file, or null when none were.</summary>
        public static PodcastMarkers? TryDetectAll(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
                return TryDetectAllCore(fs);
            }
            catch { return null; }
        }

        /// <summary>Stream-based core so self-tests can drive synthetic containers.</summary>
        internal static PodcastMarkers? TryDetectAllCore(Stream fs)
        {
            try
            {
                var markers = new PodcastMarkers();
                var head = new byte[10];
                if (!ReadFully(fs, head, 0, 10)) return null;
                if (head[0] == (byte)'I' && head[1] == (byte)'D' && head[2] == (byte)'3')
                    ScanId3v2(fs, head, markers);
                else if (head[4] == (byte)'f' && head[5] == (byte)'t' && head[6] == (byte)'y' && head[7] == (byte)'p')
                {
                    fs.Seek(0, SeekOrigin.Begin);
                    ScanMp4(fs, markers);
                }
                return markers.Any ? markers : null;
            }
            catch { return null; }
        }

        /// <summary>Walks the ID3v2 frame chain, accumulating every marker. Bails out of
        /// the walk on padding / malformed frames / short reads exactly where the
        /// first-match version returned null — the bound is unchanged; only the
        /// disposition of evidence already gathered differs (it is kept, not discarded).</summary>
        private static void ScanId3v2(Stream fs, byte[] head, PodcastMarkers markers)
        {
            int major = head[3];
            if (major < 2 || major > 4) return;
            long tagSize = ((long)(head[6] & 0x7F) << 21) | ((long)(head[7] & 0x7F) << 14)
                         | ((long)(head[8] & 0x7F) << 7) | (long)(head[9] & 0x7F);
            long walk = Math.Min(tagSize, MaxId3Walk);
            long pos = 0;
            int idLen = major == 2 ? 3 : 4;
            int hdrLen = major == 2 ? 6 : 10;
            var fh = new byte[10];
            while (pos + hdrLen <= walk)
            {
                if (!ReadFully(fs, fh, 0, hdrLen)) return;
                if (fh[0] == 0) return;   // padding reached
                string id = Encoding.ASCII.GetString(fh, 0, idLen);
                long frameSize = major == 2
                    ? ((long)fh[3] << 16) | ((long)fh[4] << 8) | fh[5]
                    : major == 3
                        ? ((long)fh[4] << 24) | ((long)fh[5] << 16) | ((long)fh[6] << 8) | fh[7]
                        : ((long)(fh[4] & 0x7F) << 21) | ((long)(fh[5] & 0x7F) << 14) | ((long)(fh[6] & 0x7F) << 7) | (long)(fh[7] & 0x7F);
                if (frameSize < 0 || pos + hdrLen + frameSize > walk + 1024) return;   // malformed
                if (id == "PCST" || id == "PCS")
                {
                    markers.AddStrong("PCST");
                    fs.Seek(frameSize, SeekOrigin.Current);
                }
                else if (id == "WFED" || id == "WFD")
                {
                    markers.AddProvenance("WFED");
                    fs.Seek(frameSize, SeekOrigin.Current);
                }
                else if (id == "TGID")
                {
                    markers.AddProvenance("TGID");
                    fs.Seek(frameSize, SeekOrigin.Current);
                }
                else if (id == "TCON" || id == "TCO")
                {
                    int len = (int)Math.Min(frameSize, 256);
                    var buf = new byte[len];
                    if (!ReadFully(fs, buf, 0, len)) return;
                    var text = DecodeId3Text(buf);
                    // Exact-equals, trimmed and case-insensitive — matching the
                    // library-side genre rule. A substring test made the file rule
                    // strictly looser than the library rule it mirrors, so
                    // "Comedy Podcast" / "Podcasts" / "Podcast Rock" all tripped it.
                    if (string.Equals(text.Trim(), "Podcast", StringComparison.OrdinalIgnoreCase))
                        markers.GenreText = true;
                    long skipRest = frameSize - len;
                    if (skipRest > 0) fs.Seek(skipRest, SeekOrigin.Current);
                }
                else
                {
                    fs.Seek(frameSize, SeekOrigin.Current);
                }
                pos += hdrLen + frameSize;
            }
        }

        private static string DecodeId3Text(byte[] buf)
        {
            if (buf.Length < 2) return "";
            try
            {
                string text;
                switch (buf[0])
                {
                    case 1:
                        text = Encoding.Unicode.GetString(buf, 1, buf.Length - 1);
                        break;
                    case 2:
                        text = Encoding.BigEndianUnicode.GetString(buf, 1, buf.Length - 1);
                        break;
                    case 3:
                        text = Encoding.UTF8.GetString(buf, 1, buf.Length - 1);
                        break;
                    default:
                        text = Encoding.GetEncoding("ISO-8859-1").GetString(buf, 1, buf.Length - 1);
                        break;
                }
                return text.TrimEnd('\0');
            }
            catch { return ""; }
        }

        private static void ScanMp4(Stream fs, PodcastMarkers markers)
        {
            // Walk: moov > udta > meta(+4 version bytes) > ilst > {pcst|purl}.
            // Atom headers only; moov-at-end files are reached by seeking.
            int visited = 0;
            WalkAtoms(fs, fs.Length, new[] { "moov", "udta", "meta", "ilst" }, 0, ref visited, markers);
        }

        private static void WalkAtoms(Stream fs, long end, string[] descend, int depth, ref int visited, PodcastMarkers markers)
        {
            var hdr = new byte[8];
            while (fs.Position + 8 <= end && visited < MaxAtomsVisited)
            {
                visited++;
                long atomStart = fs.Position;
                if (!ReadFully(fs, hdr, 0, 8)) return;
                long size = ((long)hdr[0] << 24) | ((long)hdr[1] << 16) | ((long)hdr[2] << 8) | hdr[3];
                string type = Encoding.ASCII.GetString(hdr, 4, 4);
                if (size == 1)
                {
                    var big = new byte[8];
                    if (!ReadFully(fs, big, 0, 8)) return;
                    size = ((long)big[0] << 56) | ((long)big[1] << 48) | ((long)big[2] << 40) | ((long)big[3] << 32)
                         | ((long)big[4] << 24) | ((long)big[5] << 16) | ((long)big[6] << 8) | big[7];
                }
                if (size < 8 || atomStart + size > end) return;
                if (depth == descend.Length)
                {
                    if (type == "pcst") markers.AddStrong("pcst");
                    else if (type == "purl") markers.AddProvenance("purl");
                }
                if (depth < descend.Length && type == descend[depth])
                {
                    if (type == "meta") fs.Seek(4, SeekOrigin.Current);   // version/flags
                    WalkAtoms(fs, atomStart + size, descend, depth + 1, ref visited, markers);
                }
                fs.Seek(atomStart + size, SeekOrigin.Begin);
            }
        }
    }
}
