using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Truedat
{
    /// <summary>Result of reading an exclusion playlist. Error != null means the
    /// playlist could not serve as an exclusion source (missing file, IO failure,
    /// or zero usable entries) — callers fail closed rather than scanning with
    /// fewer exclusions than the operator believes are in force.</summary>
    internal sealed class PlaylistReadResult
    {
        public List<string> Paths = new List<string>();
        public int UrlSkipped;
        public int LineCount;
        public string? Error;
    }

    /// <summary>Reads .m3u/.m3u8 playlists as exclusion sources (2026-07-30).
    /// The operator maintains a playlist named "mbxmoods-exclude" in MusicBee;
    /// scans discover it by convention (beside the XML/moods file, or in the
    /// Playlists subfolder) or take an explicit path via --exclude-playlist.
    /// UTF-8 read — MusicBee exports .m3u8 UTF-8; a legacy ANSI .m3u with
    /// non-ASCII paths is a documented limitation.</summary>
    internal static class PlaylistReader
    {
        public const string ConventionName = "mbxmoods-exclude";

        public static bool IsPlaylistPath(string? path) =>
            path != null
            && (path.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase));

        /// <summary>Probe the convention locations, first hit wins:
        /// <c>&lt;dir&gt;\mbxmoods-exclude.m3u8</c>, <c>.m3u</c>, then the same pair
        /// under <c>&lt;dir&gt;\Playlists\</c> (MusicBee's playlist folder).
        /// Null when none exists — a legitimate "nothing excluded via playlist".</summary>
        public static string? Discover(string baseDir)
        {
            foreach (var dir in new[] { baseDir, Path.Combine(baseDir, "Playlists") })
            {
                foreach (var ext in new[] { ".m3u8", ".m3u" })
                {
                    var candidate = Path.Combine(dir, ConventionName + ext);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return null;
        }

        /// <summary>Parse the playlist: skip blank/#-comment lines, skip URLs
        /// (counted — stream entries are not scannable files), resolve relative
        /// entries against the playlist's directory (m3u convention), dedupe
        /// case-insensitively on the normalized path.</summary>
        public static PlaylistReadResult Read(string playlistPath)
        {
            var result = new PlaylistReadResult();
            string[] lines;
            try { lines = File.ReadAllLines(playlistPath, Encoding.UTF8); }
            catch (Exception ex)
            {
                result.Error = $"cannot read playlist {playlistPath}: {ex.Message}";
                return result;
            }

            var baseDir = Path.GetDirectoryName(Path.GetFullPath(playlistPath)) ?? ".";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                result.LineCount++;
                if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    result.UrlSkipped++;
                    continue;
                }
                string resolved;
                try
                {
                    resolved = Path.IsPathRooted(line) ? line : Path.GetFullPath(Path.Combine(baseDir, line));
                }
                catch { continue; }   // invalid path chars — nothing to exclude on
                if (seen.Add(ExclusionSet.NormalizePath(resolved)))
                    result.Paths.Add(resolved);
            }

            if (result.Paths.Count == 0)
                result.Error = result.UrlSkipped > 0
                    ? $"playlist {playlistPath} contains only stream URLs — no file entries"
                    : $"playlist {playlistPath} contains no usable entries";
            return result;
        }

        /// <summary>Build the --apply-exclusions decisions-delta JSON for these paths —
        /// one file/exclude rule per entry, fed to the EXISTING ExclusionStore.Merge so
        /// the playlist-to-durable-JSON conversion reuses lock/backup/atomic-write/
        /// apply-result machinery unchanged.</summary>
        public static string ToDecisionsJson(IEnumerable<string> paths)
        {
            var sb = new StringBuilder();
            sb.Append("{\"add\":[");
            bool first = true;
            foreach (var p in paths)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"kind\":\"file\",\"action\":\"exclude\",\"path\":");
                sb.Append(System.Text.Json.JsonSerializer.Serialize(p));
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }
}
