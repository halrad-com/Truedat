using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Truedat.Store
{
    /// <summary>S2 C-A — the aggregate artifact. The extractor's main output (every descriptor's mean, var, median,
    /// min, max, dmean, dvar, the covariances, the full HPCP, the histograms — ~93 KB of JSON per track) was deleted at
    /// parse; every feature wave since July was a multi-day rescan to read numbers that file already held. It is now
    /// kept, DEFLATE-compressed, as a content-addressed FILE beside the store (never a blob): one per content key
    /// however many copies hold it, so any later field that is a selection or fold of the aggregate is a re-fold,
    /// never a rescan. Layout: &lt;store dir&gt;\artifacts\&lt;2&gt;\&lt;key&gt;.main.json.z; pending stash under
    /// artifacts\pending\ until the scan's flush knows the content key.</summary>
    internal static class ArtifactStore
    {
        internal const string MainKind = "main";
        internal const string Extension = ".main.json.z";

        internal static string ArtifactsDir(string storeDir) => Path.Combine(storeDir, "artifacts");
        internal static string PendingDir(string storeDir) => Path.Combine(ArtifactsDir(storeDir), "pending");

        /// <summary>Compresses the extractor's JSON text into a pending file and returns its path.</summary>
        internal static string Stash(string storeDir, string json)
        {
            var dir = PendingDir(storeDir);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".z");
            using (var fs = File.Create(path))
            using (var z = new DeflateStream(fs, CompressionLevel.Optimal))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                z.Write(bytes, 0, bytes.Length);
            }
            return path;
        }

        /// <summary>Relative path (under the store dir) of the committed artifact for a content key.</summary>
        internal static string RelativePathFor(string contentKey)
        {
            var safe = contentKey.Replace(':', '_');
            var bucket = safe.Length >= 2 ? safe.Substring(0, 2) : safe;
            return Path.Combine("artifacts", bucket, safe + Extension);
        }

        /// <summary>Moves a pending file to its content-addressed place. When one already exists for the key the pending
        /// copy is discarded (same audio, same build: the artifact is stored once). Returns the relative path, bytes, sha256
        /// and whether this call created the file.</summary>
        internal static (string RelPath, long Bytes, string Sha256, bool Created) Commit(string storeDir, string pendingPath, string contentKey)
        {
            var rel = RelativePathFor(contentKey);
            var full = Path.Combine(storeDir, rel);
            bool created = false;
            if (File.Exists(full)) { try { File.Delete(pendingPath); } catch { } }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.Move(pendingPath, full);
                created = true;
            }
            long bytes = new FileInfo(full).Length;
            string sha;
            using (var h = SHA256.Create())
            using (var fs = File.OpenRead(full))
                sha = BitConverter.ToString(h.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            return (rel, bytes, sha, created);
        }

        /// <summary>Inflates a committed artifact back to the extractor's JSON text (the re-fold entry point).</summary>
        internal static string ReadMainJson(string storeDir, string relPath)
        {
            using var fs = File.OpenRead(Path.Combine(storeDir, relPath));
            using var z = new DeflateStream(fs, CompressionMode.Decompress);
            using var r = new StreamReader(z, Encoding.UTF8);
            return r.ReadToEnd();
        }

        /// <summary>Pending files a killed scan left behind (older than a day) are deleted; the catalog still has the
        /// entry and the next analysis re-stashes it.</summary>
        internal static int SweepPending(string storeDir, TimeSpan olderThan)
        {
            var dir = PendingDir(storeDir);
            if (!Directory.Exists(dir)) return 0;
            int n = 0; var cutoff = DateTime.UtcNow - olderThan;
            foreach (var f in Directory.GetFiles(dir, "*.z"))
            {
                try { if (File.GetLastWriteTimeUtc(f) < cutoff) { File.Delete(f); n++; } } catch { }
            }
            return n;
        }
    }
}
