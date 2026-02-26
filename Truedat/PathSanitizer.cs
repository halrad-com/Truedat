using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Truedat
{
    /// <summary>
    /// Sanitizes arbitrary text (artist names, album titles, track titles) into safe
    /// Windows filesystem path components. Handles all known Windows filesystem edge cases:
    /// reserved characters, reserved device names, path length limits, encoding issues.
    /// </summary>
    static class PathSanitizer
    {
        const int MaxComponentLength = 80;
        const int MaxTotalPathLength = 248;
        const string FallbackName = "_Unknown";

        static readonly HashSet<char> IllegalChars = new HashSet<char>(
            Path.GetInvalidFileNameChars()
                .Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' })
        );

        static readonly HashSet<string> ReservedNames = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>
        /// Sanitize a single path component (directory name or filename without extension).
        /// </summary>
        public static string SanitizeComponent(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return FallbackName;

            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (c == '\0') continue;
                if (char.IsControl(c)) continue;
                if (IllegalChars.Contains(c))
                {
                    sb.Append('_');
                    continue;
                }
                sb.Append(c);
            }

            string result = sb.ToString();
            result = result.Trim('.', ' ');
            result = Regex.Replace(result, @"[\s_]+", " ").Trim();

            if (string.IsNullOrWhiteSpace(result))
                return FallbackName;

            string nameWithoutExt = result.Contains('.')
                ? result.Substring(0, result.IndexOf('.'))
                : result;
            if (ReservedNames.Contains(nameWithoutExt))
                result = "_" + result;

            if (result.Length > MaxComponentLength)
                result = result.Substring(0, MaxComponentLength).TrimEnd('.', ' ');

            if (string.IsNullOrWhiteSpace(result))
                return FallbackName;

            return result;
        }

        /// <summary>
        /// Build a full file path for a synthetic track, with collision detection.
        /// Format: {outputDir}/{Artist}/{Album}/{NN} {Title}.mp3
        /// </summary>
        public static string BuildTrackPath(string outputDir, string artist, string album,
                                             int trackNo, string title,
                                             HashSet<string> existingPaths)
        {
            string safeArtist = SanitizeComponent(artist);
            string safeAlbum = SanitizeComponent(album);
            string safeTitle = SanitizeComponent(title);
            string trackPrefix = trackNo.ToString("D2");

            string filename = trackPrefix + " " + safeTitle + ".mp3";
            string path = Path.Combine(outputDir, safeArtist, safeAlbum, filename);

            // Validate total path length — truncate title first, then album
            if (path.Length > MaxTotalPathLength)
            {
                int excess = path.Length - MaxTotalPathLength;
                if (safeTitle.Length > excess + 5)
                {
                    safeTitle = safeTitle.Substring(0, safeTitle.Length - excess).TrimEnd('.', ' ');
                }
                else
                {
                    safeTitle = safeTitle.Length > 10 ? safeTitle.Substring(0, 10) : safeTitle;
                    filename = trackPrefix + " " + safeTitle + ".mp3";
                    path = Path.Combine(outputDir, safeArtist, safeAlbum, filename);
                    excess = path.Length - MaxTotalPathLength;
                    if (excess > 0 && safeAlbum.Length > excess + 5)
                        safeAlbum = safeAlbum.Substring(0, safeAlbum.Length - excess).TrimEnd('.', ' ');
                }
                filename = trackPrefix + " " + safeTitle + ".mp3";
                path = Path.Combine(outputDir, safeArtist, safeAlbum, filename);
            }

            // Case-insensitive collision detection
            string normalizedPath = path.Replace('/', '\\').ToLowerInvariant();
            if (existingPaths.Contains(normalizedPath))
            {
                bool resolved = false;
                for (int i = 2; i < 1000; i++)
                {
                    string altFilename = trackPrefix + " " + safeTitle + " (" + i + ").mp3";
                    string altPath = Path.Combine(outputDir, safeArtist, safeAlbum, altFilename);
                    string altNorm = altPath.Replace('/', '\\').ToLowerInvariant();
                    if (!existingPaths.Contains(altNorm))
                    {
                        path = altPath;
                        normalizedPath = altNorm;
                        resolved = true;
                        break;
                    }
                }
                if (!resolved)
                {
                    // Fallback: use a hash suffix to guarantee uniqueness
                    string hash = title.GetHashCode().ToString("x8");
                    string altFilename = trackPrefix + " " + safeTitle.Substring(0, Math.Min(safeTitle.Length, 40)) + "_" + hash + ".mp3";
                    path = Path.Combine(outputDir, safeArtist, safeAlbum, altFilename);
                    normalizedPath = path.Replace('/', '\\').ToLowerInvariant();
                }
            }

            // Final guard: if path still exceeds limit (e.g. outputDir is very long),
            // truncate artist to keep path under MAX_PATH
            if (path.Length > MaxTotalPathLength)
            {
                int excess = path.Length - MaxTotalPathLength;
                if (safeArtist.Length > excess + 5)
                    safeArtist = safeArtist.Substring(0, safeArtist.Length - excess).TrimEnd('.', ' ');
                else
                    safeArtist = safeArtist.Length > 5 ? safeArtist.Substring(0, 5) : safeArtist;
                filename = trackPrefix + " " + safeTitle + ".mp3";
                path = Path.Combine(outputDir, safeArtist, safeAlbum, filename);
                normalizedPath = path.Replace('/', '\\').ToLowerInvariant();
            }

            existingPaths.Add(normalizedPath);
            return path;
        }

        /// <summary>
        /// Normalize text for metadata matching (seeding lookup).
        /// MUST produce identical results to Python's _normalize_text() in catalog-prep.py.
        /// </summary>
        public static string NormalizeForLookup(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            string normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (char c in normalized)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }

            string result = sb.ToString().ToLowerInvariant();
            result = Regex.Replace(result, @"[^\w\s]", "");
            result = Regex.Replace(result, @"\s+", " ").Trim();
            return result;
        }
    }
}
