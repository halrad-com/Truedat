using System;
using System.IO;
using System.Text;

namespace Truedat
{
    /// <summary>
    /// Detects explicit podcast markers embedded in the audio FILE itself —
    /// markers podcast downloaders write even when the library XML carries no
    /// genre: ID3v2 PCST (iTunes podcast flag), WFED (feed URL), TGID (episode
    /// GUID), TCON genre text containing "podcast"; MP4/M4A pcst / purl atoms.
    /// Pure-managed, bounded header reads, never throws (null on anything
    /// unexpected). House pattern: Mp3LameTagParser.
    /// </summary>
    internal static class PodcastTagSniffer
    {
        // ID3v2 tags larger than this are not walked — real-world podcast tags
        // sit well under it; beyond is embedded artwork we don't care about.
        private const int MaxId3Walk = 128 * 1024;
        private const int MaxAtomsVisited = 64;

        /// <summary>Marker name identifying the file as a podcast, or null.</summary>
        public static string? TryDetect(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
                return TryDetectCore(fs);
            }
            catch { return null; }
        }

        /// <summary>Stream-based core so self-tests can drive synthetic containers.</summary>
        internal static string? TryDetectCore(Stream fs)
        {
            try
            {
                var head = new byte[10];
                if (fs.Read(head, 0, 10) != 10) return null;
                if (head[0] == (byte)'I' && head[1] == (byte)'D' && head[2] == (byte)'3')
                    return ScanId3v2(fs, head);
                if (head[4] == (byte)'f' && head[5] == (byte)'t' && head[6] == (byte)'y' && head[7] == (byte)'p')
                {
                    fs.Seek(0, SeekOrigin.Begin);
                    return ScanMp4(fs);
                }
                return null;
            }
            catch { return null; }
        }

        private static string? ScanId3v2(Stream fs, byte[] head)
        {
            int major = head[3];
            if (major < 2 || major > 4) return null;
            long tagSize = ((long)(head[6] & 0x7F) << 21) | ((long)(head[7] & 0x7F) << 14)
                         | ((long)(head[8] & 0x7F) << 7) | (long)(head[9] & 0x7F);
            long walk = Math.Min(tagSize, MaxId3Walk);
            long pos = 0;
            int idLen = major == 2 ? 3 : 4;
            int hdrLen = major == 2 ? 6 : 10;
            var fh = new byte[10];
            while (pos + hdrLen <= walk)
            {
                if (fs.Read(fh, 0, hdrLen) != hdrLen) return null;
                if (fh[0] == 0) return null;   // padding reached
                string id = Encoding.ASCII.GetString(fh, 0, idLen);
                long frameSize = major == 2
                    ? ((long)fh[3] << 16) | ((long)fh[4] << 8) | fh[5]
                    : major == 3
                        ? ((long)fh[4] << 24) | ((long)fh[5] << 16) | ((long)fh[6] << 8) | fh[7]
                        : ((long)(fh[4] & 0x7F) << 21) | ((long)(fh[5] & 0x7F) << 14) | ((long)(fh[6] & 0x7F) << 7) | (long)(fh[7] & 0x7F);
                if (frameSize < 0 || pos + hdrLen + frameSize > walk + 1024) return null;   // malformed
                if (id == "PCST" || id == "PCS") return "PCST";
                if (id == "WFED" || id == "WFD") return "WFED";
                if (id == "TGID") return "TGID";
                if (id == "TCON" || id == "TCO")
                {
                    int len = (int)Math.Min(frameSize, 256);
                    var buf = new byte[len];
                    if (fs.Read(buf, 0, len) != len) return null;
                    var text = DecodeId3Text(buf);
                    if (text.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "TCON=Podcast";
                    long skipRest = frameSize - len;
                    if (skipRest > 0) fs.Seek(skipRest, SeekOrigin.Current);
                }
                else
                {
                    fs.Seek(frameSize, SeekOrigin.Current);
                }
                pos += hdrLen + frameSize;
            }
            return null;
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

        private static string? ScanMp4(Stream fs)
        {
            // Walk: moov > udta > meta(+4 version bytes) > ilst > {pcst|purl}.
            // Atom headers only; moov-at-end files are reached by seeking.
            int visited = 0;
            return WalkAtoms(fs, fs.Length, new[] { "moov", "udta", "meta", "ilst" }, 0, ref visited);
        }

        private static string? WalkAtoms(Stream fs, long end, string[] descend, int depth, ref int visited)
        {
            var hdr = new byte[8];
            while (fs.Position + 8 <= end && visited < MaxAtomsVisited)
            {
                visited++;
                long atomStart = fs.Position;
                if (fs.Read(hdr, 0, 8) != 8) return null;
                long size = ((long)hdr[0] << 24) | ((long)hdr[1] << 16) | ((long)hdr[2] << 8) | hdr[3];
                string type = Encoding.ASCII.GetString(hdr, 4, 4);
                if (size == 1)
                {
                    var big = new byte[8];
                    if (fs.Read(big, 0, 8) != 8) return null;
                    size = ((long)big[0] << 56) | ((long)big[1] << 48) | ((long)big[2] << 40) | ((long)big[3] << 32)
                         | ((long)big[4] << 24) | ((long)big[5] << 16) | ((long)big[6] << 8) | big[7];
                }
                if (size < 8 || atomStart + size > end) return null;
                if (depth == descend.Length && (type == "pcst" || type == "purl"))
                    return type + " atom";
                if (depth < descend.Length && type == descend[depth])
                {
                    if (type == "meta") fs.Seek(4, SeekOrigin.Current);   // version/flags
                    var found = WalkAtoms(fs, atomStart + size, descend, depth + 1, ref visited);
                    if (found != null) return found;
                }
                fs.Seek(atomStart + size, SeekOrigin.Begin);
            }
            return null;
        }
    }
}
