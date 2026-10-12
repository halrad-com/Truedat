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

        static readonly System.Text.RegularExpressions.Regex KeyShape = new System.Text.RegularExpressions.Regex("^([0-9a-f]{64}|entity:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$");
        static readonly System.Text.RegularExpressions.Regex BuildShape = new System.Text.RegularExpressions.Regex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$");

        /// <summary>Relative path (under the store dir) of the committed artifact for a content key under an extractor build:
        /// <c>artifacts\&lt;build&gt;\&lt;2&gt;\&lt;key&gt;.main.json.z</c>. The build is in the path because the artifact IS the
        /// build's output (review #1: one file per content key would have labelled .4 bytes as .5). Both components are
        /// validated to the shapes the store itself mints, so no key can escape <c>artifacts\</c>.</summary>
        internal static string RelativePathFor(string contentKey, string build)
        {
            if (!KeyShape.IsMatch(contentKey)) throw new StoreException($"artifact key is not a content hash or entity id: '{contentKey}'");
            if (!BuildShape.IsMatch(build) || build.Contains("..")) throw new StoreException($"artifact build token is not a plain name: '{build}'");
            var safe = contentKey.Replace(':', '_');
            var bucket = safe.Substring(0, 2);
            return Path.Combine("artifacts", build.Replace(':', '_'), bucket, safe + Extension);
        }

        /// <summary>Moves a pending file to its content-addressed place. When one already exists for the key the pending
        /// copy is discarded (same audio, same build: the artifact is stored once). Returns the relative path, bytes, sha256
        /// and whether this call created the file.</summary>
        internal static (string RelPath, long Bytes, string Sha256, bool Created) Commit(string storeDir, string pendingPath, string contentKey, string build)
        {
            var rel = RelativePathFor(contentKey, build);
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
