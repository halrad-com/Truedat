using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Truedat
{
    /// <summary>
    /// Compression + snapshot/restore for mbxmoods.json BACKUPS and deliberate
    /// snapshots. The LIVE catalog stays plain JSON — MBXHub's tolerant reader
    /// consumes it as plain JSON, so compressing it is a cross-repo contract change
    /// (the gated "compressed live catalog" stretch in BACKLOG). This half touches
    /// only backups and snapshots.
    ///
    /// Write: ZIP (DEFLATE), because Windows Explorer opens .zip natively and a ZIP
    /// is a container (a snapshot bundles catalog + manifest + exclude file in one
    /// double-clickable archive). Read: self-describing — sniff the first bytes so
    /// zip, gz, and plain all load (backward-compatible, reversible). Streaming
    /// throughout, so memory stays O(1) like SaveResults; net48 ships ZipArchive and
    /// GZipStream, so zero new dependencies.
    /// </summary>
    internal static class CatalogArchive
    {
        internal const string CatalogEntryName = "mbxmoods.json";
        internal const string ExcludeEntryName = "mbxmoods-exclude.json";
        internal const string ManifestEntryName = "manifest.json";

        // trackCount is written before the tracks object (SaveResults order:
        // version, generatedAt, trackCount, tracks), so a leading window always
        // carries it for real catalogs. Bounded so a pathological property order
        // can't drag the whole file into memory — the field is convenience, not the
        // archive's verification anchor (contentSha256 is).
        private const int PrefixWindowBytes = 1 << 20; // 1 MiB

        internal enum CatalogFormat { Plain, Gzip, Zip }

        /// <summary>Sniff a catalog/archive format from its first bytes: PK (50 4B) =
        /// ZIP, 1F 8B = gzip, anything else = plain JSON (a UTF-8 BOM or leading
        /// whitespace can only be plain — neither collides with the two magic pairs).
        /// A seekable stream is left rewound to where it started.</summary>
        internal static CatalogFormat SniffStream(Stream s)
        {
            long start = s.CanSeek ? s.Position : 0;
            int b0 = s.ReadByte();
            int b1 = s.ReadByte();
            if (s.CanSeek) s.Position = start;
            if (b0 == 0x50 && b1 == 0x4B) return CatalogFormat.Zip;   // "PK"
            if (b0 == 0x1F && b1 == 0x8B) return CatalogFormat.Gzip;
            return CatalogFormat.Plain;
        }

        internal static CatalogFormat SniffFile(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return SniffStream(fs);
        }

        /// <summary>Compress an existing plain-JSON catalog file into a ZIP at
        /// <paramref name="bakPath"/> (entry name mbxmoods.json), staged to a .tmp and
        /// atomically moved into place. Returns the backup path written.</summary>
        internal static string WriteZipBackup(string catalogPath, string bakPath)
        {
            var tmp = bakPath + ".tmp";
            try { File.Delete(tmp); } catch { }
            using (var zfs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            using (var zip = new ZipArchive(zfs, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry(CatalogEntryName, CompressionLevel.Optimal);
                using var es = entry.Open();
                using var src = new FileStream(catalogPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                src.CopyTo(es);
            }
            Program.AtomicReplace(tmp, bakPath);
            return bakPath;
        }

        /// <summary>Keep the newest <paramref name="keepN"/> timestamped backups for a
        /// catalog (names matching "&lt;catalog file name&gt;.bak.*"), deleting older
        /// ones. Rotation, never truncation: keepN &lt;= 0 keeps everything. The
        /// transient SaveResults ".bak" (no timestamp) and the live catalog are never
        /// matched — the prefix requires the trailing dot that only timestamped backups
        /// carry. Returns how many were deleted.</summary>
        internal static int RotateBackups(string catalogPath, int keepN)
        {
            if (keepN <= 0) return 0;
            var dir = Path.GetDirectoryName(Path.GetFullPath(catalogPath));
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return 0;

            // "mbxmoods.json.bak." — the trailing dot excludes the transient
            // "mbxmoods.json.bak" and the live "mbxmoods.json".
            var prefix = Path.GetFileName(catalogPath) + ".bak.";
            var backups = Directory.GetFiles(dir!)
                .Where(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                // Timestamp is yyyyMMdd.HHmmss — fixed width, so ordinal-descending on the
                // filename is newest-first.
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();

            int deleted = 0;
            foreach (var old in backups.Skip(keepN))
            {
                try { File.Delete(old); deleted++; } catch { }
            }
            return deleted;
        }

        /// <summary>Decompress/copy the catalog JSON out of an archive (zip/gz/plain)
        /// into <paramref name="dest"/>. Streaming — never materializes the whole
        /// catalog.</summary>
        internal static void ExtractCatalogTo(string archivePath, Stream dest)
        {
            var fmt = SniffFile(archivePath);
            using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            switch (fmt)
            {
                case CatalogFormat.Zip:
                    using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
                    {
                        var entry = FindCatalogEntry(zip)
                            ?? throw new InvalidDataException($"No {CatalogEntryName} entry found in archive: {archivePath}");
                        using var es = entry.Open();
                        es.CopyTo(dest);
                    }
                    break;
                case CatalogFormat.Gzip:
                    using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                        gz.CopyTo(dest);
                    break;
                default:
                    fs.CopyTo(dest);
                    break;
            }
        }

        private static ZipArchiveEntry? FindCatalogEntry(ZipArchive zip)
        {
            // Prefer the canonical name; fall back to the first .json entry that isn't
            // the manifest or the exclusion file, then to the first entry.
            var e = zip.GetEntry(CatalogEntryName);
            if (e != null) return e;
            e = zip.Entries.FirstOrDefault(x =>
                x.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && !x.Name.Equals(ManifestEntryName, StringComparison.OrdinalIgnoreCase)
                && !x.Name.Equals(ExcludeEntryName, StringComparison.OrdinalIgnoreCase));
            return e ?? zip.Entries.FirstOrDefault();
        }

        /// <summary>True when a ZIP archive carries an mbxmoods-exclude.json entry.</summary>
        internal static bool ArchiveContainsExclude(string archivePath)
        {
            if (SniffFile(archivePath) != CatalogFormat.Zip) return false;
            using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            return zip.GetEntry(ExcludeEntryName) != null;
        }

        /// <summary>Best-effort read of the top-level "trackCount" from a JSON prefix
        /// window (SaveResults writes it before the tracks object, so the leading bytes
        /// carry it). Tolerates a leading UTF-8 BOM; returns null when the value isn't
        /// in the window or isn't an integer. Only depth-1 properties count — a nested
        /// "trackCount" is ignored.</summary>
        internal static int? TryReadTrackCountFromPrefix(byte[] prefix, int len)
        {
            if (prefix == null || len <= 0) return null;
            int start = 0;
            // Utf8JsonReader treats a UTF-8 BOM as invalid content rather than skipping it.
            if (len >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF) start = 3;
            var span = new ReadOnlySpan<byte>(prefix, start, len - start);
            // isFinalBlock:false so a window that cuts mid-value stops (returns false)
            // instead of throwing.
            var reader = new Utf8JsonReader(span, isFinalBlock: false, state: default);
            try
            {
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.PropertyName
                        && reader.CurrentDepth == 1
                        && reader.ValueTextEquals("trackCount"))
                    {
                        if (reader.Read() && reader.TokenType == JsonTokenType.Number
                            && reader.TryGetInt32(out var n))
                            return n;
                        return null;
                    }
                }
            }
            catch (JsonException) { /* window cut mid-token — best effort */ }
            return null;
        }

        internal readonly struct SnapshotInfo
        {
            internal SnapshotInfo(int? trackCount, string contentSha256, long catalogBytes)
            {
                TrackCount = trackCount;
                ContentSha256 = contentSha256;
                CatalogBytes = catalogBytes;
            }
            internal int? TrackCount { get; }
            internal string ContentSha256 { get; }
            internal long CatalogBytes { get; }
        }

        /// <summary>Stream the live catalog into a snapshot ZIP at
        /// <paramref name="outPath"/> — entry mbxmoods.json, a manifest.json
        /// (trackCount, generatedAt, content SHA-256, byte size, tool version), and
        /// mbxmoods-exclude.json when <paramref name="excludePath"/> exists — so the
        /// archive is a complete, self-verifying, restorable state in one file.
        /// Read-only over the live catalog. Returns the computed manifest facts.</summary>
        internal static SnapshotInfo WriteSnapshot(string catalogPath, string? excludePath, string outPath, string toolVersion)
        {
            var tmp = outPath + ".tmp";
            try { File.Delete(tmp); } catch { }

            string sha;
            long catalogBytes;
            int? trackCount;
            var prefixBuf = new byte[PrefixWindowBytes];
            int prefixLen = 0;

            using (var zfs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            using (var zip = new ZipArchive(zfs, ZipArchiveMode.Create))
            {
                // 1) catalog entry — stream it in once while hashing and capturing a
                //    leading window for the trackCount read (single read pass).
                using (var sha256 = SHA256.Create())
                {
                    var catEntry = zip.CreateEntry(CatalogEntryName, CompressionLevel.Optimal);
                    using (var es = catEntry.Open())
                    using (var src = new FileStream(catalogPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
                    {
                        var buf = new byte[81920];
                        long total = 0;
                        int read;
                        while ((read = src.Read(buf, 0, buf.Length)) > 0)
                        {
                            es.Write(buf, 0, read);
                            sha256.TransformBlock(buf, 0, read, null, 0);
                            total += read;
                            if (prefixLen < prefixBuf.Length)
                            {
                                int copy = Math.Min(read, prefixBuf.Length - prefixLen);
                                Array.Copy(buf, 0, prefixBuf, prefixLen, copy);
                                prefixLen += copy;
                            }
                        }
                        catalogBytes = total;
                    }
                    sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    sha = Hex(sha256.Hash!);
                }

                trackCount = TryReadTrackCountFromPrefix(prefixBuf, prefixLen);

                // 2) manifest entry — the self-verifying facts.
                var manEntry = zip.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                using (var ms = manEntry.Open())
                using (var jw = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
                {
                    jw.WriteStartObject();
                    jw.WriteString("kind", "catalog-snapshot");
                    jw.WriteNumber("schemaVersion", 1);
                    jw.WriteString("generatedAt", DateTime.UtcNow.ToString("o"));
                    if (trackCount.HasValue) jw.WriteNumber("trackCount", trackCount.Value);
                    else jw.WriteNull("trackCount");
                    jw.WriteString("contentSha256", sha);
                    jw.WriteNumber("catalogBytes", catalogBytes);
                    jw.WriteString("catalogEntry", CatalogEntryName);
                    jw.WriteBoolean("hasExclusions", !string.IsNullOrEmpty(excludePath) && File.Exists(excludePath));
                    jw.WriteString("truedatVersion", toolVersion ?? "");
                    jw.WriteEndObject();
                }

                // 3) exclusion file, when present — makes the snapshot a complete state.
                if (!string.IsNullOrEmpty(excludePath) && File.Exists(excludePath))
                {
                    var exEntry = zip.CreateEntry(ExcludeEntryName, CompressionLevel.Optimal);
                    using var exs = exEntry.Open();
                    using var exsrc = new FileStream(excludePath!, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                    exsrc.CopyTo(exs);
                }
            }

            // The zip + underlying FileStream are closed here, so the .tmp is complete
            // and unlocked — now swap it into place atomically.
            Program.AtomicReplace(tmp, outPath);
            return new SnapshotInfo(trackCount, sha, catalogBytes);
        }

        private static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
    }
}
