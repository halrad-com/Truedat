using System;
using System.Collections.Generic;
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

        /// <summary>store-v3 T09: a snapshot taken with a store in force carries the STORE as well, so the archive
        /// restores the authority and not just the files derived from it. The member is produced by
        /// <c>StoreTransfer.VacuumInto</c> — a consistent SQLite copy, never a file copy of a live WAL store.</summary>
        internal readonly struct StoreMember
        {
            internal StoreMember(string file, string entryName, int schema, long generation, int entries, string sha256)
            { File = file; EntryName = entryName; Schema = schema; Generation = generation; Entries = entries; Sha256 = sha256; }
            internal string File { get; }
            internal string EntryName { get; }
            internal int Schema { get; }
            internal long Generation { get; }
            internal int Entries { get; }
            internal string Sha256 { get; }
        }

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
            => WriteZipBackup(new[] { (catalogPath, CatalogEntryName) }, bakPath);

        /// <summary>The multi-file form: each (path, entry name) pair becomes one entry, staged and swapped the same way. A split
        /// catalog (mbxmoods.json.1, .2, …) is pinned as one zip holding every part under its own file name.</summary>
        internal static string WriteZipBackup(IReadOnlyList<(string Path, string EntryName)> files, string bakPath)
        {
            var tmp = bakPath + ".tmp";
            try { File.Delete(tmp); } catch { }
            using (var zfs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            using (var zip = new ZipArchive(zfs, ZipArchiveMode.Create))
            {
                foreach (var f in files)
                {
                    var entry = zip.CreateEntry(f.EntryName, CompressionLevel.Optimal);
                    using var es = entry.Open();
                    using var src = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                    src.CopyTo(es);
                }
            }
            Program.AtomicReplace(tmp, bakPath);
            return bakPath;
        }

        /// <summary>The files a catalog path means on disk: the single file when it exists, otherwise the numbered parts
        /// <c>&lt;catalog&gt;.1</c>, <c>.2</c>, … exactly as the reader stitches them (<see cref="Program.EnumerateCatalogParts"/>: every
        /// numbered part present, in order, a missing number tolerated — the INVARIANT is one file or parts, never both). Empty when
        /// neither exists. One rule for the reader, the import's hash and its pin: a contiguous-only rule here pinned fewer parts than
        /// the import had loaded, and publication then deleted the unpinned one (codex 2026-10-25, gapped split catalog).</summary>
        internal static List<string> CatalogFiles(string catalogPath)
        {
            if (File.Exists(catalogPath)) return new List<string> { catalogPath };
            return Program.EnumerateCatalogParts(catalogPath).Select(p => p.Value).ToList();
        }

        /// <summary>The part number of a pinned entry name (<c>mbxmoods.json.3</c> → 3), or 0 when the name is not a numbered part.</summary>
        static int PartNumber(string entryName)
        {
            var prefix = CatalogEntryName + ".";
            return entryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(entryName.Substring(prefix.Length), out var n) && n >= 1 ? n : 0;
        }

        /// <summary>A pin that holds a split catalog restores as ONE catalog: the parts' track objects stitched in part order (a later part
        /// wins a repeated path, as the reader does), under the first part's header. Streaming per part; the merged track set is held once.</summary>
        static void MergeCatalogParts(IReadOnlyList<ZipArchiveEntry> parts, Stream dest)
        {
            var docs = new List<JsonDocument>();
            try
            {
                var docOptions = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
                foreach (var p in parts) { using var es = p.Open(); docs.Add(JsonDocument.Parse(es, docOptions)); }
                var tracks = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                var order = new List<string>();
                foreach (var d in docs)
                {
                    if (d.RootElement.ValueKind != JsonValueKind.Object || !d.RootElement.TryGetProperty("tracks", out var t) || t.ValueKind != JsonValueKind.Object) continue;
                    foreach (var prop in t.EnumerateObject())
                    {
                        if (!tracks.ContainsKey(prop.Name)) order.Add(prop.Name);
                        tracks[prop.Name] = prop.Value;
                    }
                }
                using var jw = new Utf8JsonWriter(dest);
                jw.WriteStartObject();
                if (docs.Count > 0 && docs[0].RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var prop in docs[0].RootElement.EnumerateObject())
                        if (!prop.NameEquals("tracks")) prop.WriteTo(jw);
                jw.WritePropertyName("tracks");
                jw.WriteStartObject();
                foreach (var name in order) { jw.WritePropertyName(name); tracks[name].WriteTo(jw); }
                jw.WriteEndObject();
                jw.WriteEndObject();
            }
            finally { foreach (var d in docs) d.Dispose(); }
        }

        /// <summary>The fail-safe for a catalog a store imports (operator 2026-10-25): once the store holds it, every scan publishes over
        /// mbxmoods.json from the store with no backup, and the editing verbs' zips rotate out after keep-backups. So the imported file is
        /// zipped beside itself first as <c>&lt;catalog&gt;.imported.&lt;sha256 prefix&gt;.zip</c>: named by content, so importing the same
        /// bytes again writes nothing new, and never matched by <see cref="RotateBackups"/> (its prefix is ".bak."). Restores with --restore
        /// like any backup. A split catalog (numbered parts) is pinned whole — every part in the one zip, each under its own file
        /// name — because publication deletes the parts (one file OR parts, never both); codex 2026-10-25 N1 reproduced the parts
        /// being deleted with no copy kept. Returns the pin's path (existing or new); null when there is no catalog file to pin.</summary>
        internal static string? PinImported(string catalogPath, string sha256)
        {
            if (string.IsNullOrEmpty(sha256)) return null;
            var files = CatalogFiles(catalogPath);
            if (files.Count == 0) return null;
            var pin = $"{catalogPath}.imported.{sha256.Substring(0, Math.Min(16, sha256.Length))}.zip";
            if (File.Exists(pin)) return pin;
            if (files.Count == 1) WriteZipBackup(files[0], pin);
            else WriteZipBackup(files.Select(f => (f, Path.GetFileName(f))).ToList(), pin);
            return pin;
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
                        if (zip.GetEntry(CatalogEntryName) == null)
                        {
                            // A pinned split catalog: mbxmoods.json.1, .3, … and no single-file entry. Restore it as one catalog.
                            var parts = zip.Entries.Where(e => PartNumber(e.Name) > 0).OrderBy(e => PartNumber(e.Name)).ToList();
                            if (parts.Count > 0) { MergeCatalogParts(parts, dest); break; }
                        }
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
        internal static SnapshotInfo WriteSnapshot(string catalogPath, string? excludePath, string outPath, string toolVersion,
            StoreMember? store = null)
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
                    // store-v3 T09: the store's own facts, so a restore can say what it is about to install before
                    // it opens it, and a transfer between machines is verifiable by hash like the catalog is.
                    if (store.HasValue)
                    {
                        var st = store.Value;
                        jw.WriteString("storeEntry", st.EntryName);
                        jw.WriteNumber("storeBytes", new FileInfo(st.File).Length);
                        jw.WriteString("storeSha256", st.Sha256);
                        jw.WriteNumber("storeSchema", st.Schema);
                        jw.WriteNumber("storeGeneration", st.Generation);
                        jw.WriteNumber("storeEntries", st.Entries);
                    }
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

                // 4) the store (T09), when one is in force — the consistent copy the caller already made.
                if (store.HasValue)
                {
                    var stEntry = zip.CreateEntry(store.Value.EntryName, CompressionLevel.Optimal);
                    using var sts = stEntry.Open();
                    using var stsrc = new FileStream(store.Value.File, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                    stsrc.CopyTo(sts);
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
