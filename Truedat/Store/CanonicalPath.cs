using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Truedat.Store
{
    /// <summary>Canonical-path rules for a location (schema v1 track_location.canonical_path): root stripped,
    /// NFC, '/' separators, case-folded for matching, original case kept for display. The same rules diffycat
    /// needs. A canonical path is a MATCHING rung (hash first, path second), never identity: the media's
    /// identity is file_entity.id and locations stack under it.</summary>
    internal static class CanonicalPath
    {
        /// <summary>Splits <paramref name="fullPath"/> under <paramref name="root"/>. <paramref name="relPath"/>
        /// keeps the original case; <paramref name="canonical"/> is the match key. Throws ArgumentException
        /// when the path is not under the root.</summary>
        internal static void Split(string root, string fullPath, out string relPath, out string canonical)
        {
            var r = fullPath.Length == 0 ? "" : Backslashes(root).TrimEnd('\\');
            var p = Backslashes(fullPath);
            if (r.Length == 0 || p.Length <= r.Length + 1
                || !p.StartsWith(r, StringComparison.OrdinalIgnoreCase)
                || p[r.Length] != '\\')
                throw new ArgumentException($"'{fullPath}' is not under the root '{root}'");

            relPath = p.Substring(r.Length + 1).Replace('\\', '/').Normalize(NormalizationForm.FormC);
            canonical = relPath.ToLowerInvariant();
        }

        /// <summary>The drive ("D:", upper-cased) or UNC share ("\\server\share") a path lives on.</summary>
        internal static string VolumeOf(string path)
        {
            var p = Backslashes(path);
            if (p.StartsWith(@"\\", StringComparison.Ordinal))
            {
                int s1 = p.IndexOf('\\', 2);
                if (s1 < 0) throw new ArgumentException($"'{path}' is not a UNC path with a share");
                int s2 = p.IndexOf('\\', s1 + 1);
                return s2 < 0 ? p : p.Substring(0, s2);
            }
            if (p.Length >= 2 && p[1] == ':' && char.IsLetter(p[0]))
                return char.ToUpperInvariant(p[0]) + ":";
            throw new ArgumentException($"'{path}' has no drive letter or UNC share");
        }

        /// <summary>Infer library roots from catalog keys: group by volume, take the longest common
        /// directory per group (case-insensitive, first-seen casing kept). A single path yields its
        /// directory. Reported to the operator, so a wrong inference is visible, and overridable with
        /// --library-root.</summary>
        internal static List<string> InferRoots(IEnumerable<string> paths)
        {
            var byVolume = new Dictionary<string, string[]?>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                var dir = Path.GetDirectoryName(Backslashes(path));
                if (string.IsNullOrEmpty(dir)) continue;
                var vol = VolumeOf(path);
                var segs = dir!.Split('\\');
                if (!byVolume.TryGetValue(vol, out var common) || common == null) { byVolume[vol] = segs; continue; }
                int n = 0;
                while (n < common.Length && n < segs.Length && string.Equals(common[n], segs[n], StringComparison.OrdinalIgnoreCase)) n++;
                if (n < common.Length) { var cut = new string[n]; Array.Copy(common, cut, n); byVolume[vol] = cut; }
            }
            var roots = new List<string>();
            foreach (var kv in byVolume)
                if (kv.Value != null) roots.Add(string.Join("\\", kv.Value));
            return roots;
        }

        /// <summary>Whether directory <paramref name="inner"/> lies strictly inside directory <paramref name="outer"/>
        /// (case-insensitive, either separator, trailing separators ignored). A directory does not contain itself, and
        /// <c>D:\Music2</c> is not inside <c>D:\Music</c>.</summary>
        internal static bool Contains(string outer, string inner)
        {
            var o = Backslashes(outer).TrimEnd('\\');
            var i = Backslashes(inner).TrimEnd('\\');
            return o.Length > 0 && i.Length > o.Length + 1 && i[o.Length] == '\\'
                && i.StartsWith(o, StringComparison.OrdinalIgnoreCase);
        }

        static string Backslashes(string path) => path.Replace('/', '\\');
    }
}
