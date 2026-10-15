using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Truedat.Store
{
    /// <summary>
    /// The catalog header's <c>origin</c> block — the publication record for the export triple
    /// (store-v3 sprint T07/T08; plan "Publication protocol", hub:H03a/H09). The catalog IS the
    /// record: it names the generation and binds the sidecar by content. Old readers skip the
    /// unknown root keys; the hub reads them from the bounded head it already scans for
    /// <c>trackCount</c>, so <see cref="WriteHeader"/> must be called right after that key.
    /// </summary>
    internal sealed class CatalogOrigin
    {
        /// <summary>The export contract name the hub pins; absence means a legacy v1 catalog.</summary>
        internal const string ContractV2 = "mbxmoods.v2";

        internal string Kind = "scan";            // scan | export | import | restore | <verb>
        internal string MachineId = "";
        internal int StoreSchema;
        internal string Truedat = "";
        internal string ExportedAt = "";          // ISO-8601 UTC
        internal long Generation;
        /// <summary>SHA-256 of the sidecar bytes written from the same generation: 64 lower-case
        /// hex characters. The encoding is a contract (same class as <c>ruleId</c>).</summary>
        internal string SidecarSha256 = "";

        /// <summary>Emit <c>"contract"</c> and <c>"origin"</c> into an open root object. Call it
        /// immediately after <c>trackCount</c> so both land inside the hub's bounded head read.</summary>
        internal static void WriteHeader(Utf8JsonWriter w, CatalogOrigin o)
        {
            w.WriteString("contract", ContractV2);
            w.WriteStartObject("origin");
            w.WriteString("kind", o.Kind);
            w.WriteString("machineId", o.MachineId);
            w.WriteNumber("storeSchema", o.StoreSchema);
            w.WriteString("truedat", o.Truedat);
            w.WriteString("exportedAt", o.ExportedAt);
            w.WriteNumber("generation", o.Generation);
            w.WriteString("sidecarSha256", o.SidecarSha256);
            w.WriteEndObject();
        }

        /// <summary>How many leading bytes <see cref="TryRead"/> examines. The header keys precede
        /// <c>tracks</c>, so this is the same bounded read the hub performs — never the whole file.</summary>
        internal const int HeadBytes = 64 * 1024;

        /// <summary>
        /// Read <c>contract</c> + <c>origin</c> from the head of a catalog without parsing the
        /// file. Returns false for a legacy v1 catalog (no <c>contract</c>), an unreadable file, or a
        /// head that does not reach the keys before <c>tracks</c>. Never throws.
        /// </summary>
        internal static bool TryRead(string catalogPath, out CatalogOrigin origin)
        {
            origin = new CatalogOrigin();
            byte[] head;
            try
            {
                using (var fs = new FileStream(catalogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var buf = new byte[HeadBytes];
                    int n = 0, r;
                    while (n < buf.Length && (r = fs.Read(buf, n, buf.Length - n)) > 0) n += r;
                    head = new byte[n];
                    Array.Copy(buf, head, n);
                }
            }
            catch { return false; }

            // A UTF-8 BOM is tolerated by the hub's readers; skip it here too.
            int start = (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) ? 3 : 0;
            bool sawContract = false, sawOrigin = false;
            try
            {
                var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(head, start, head.Length - start),
                    isFinalBlock: false, state: new JsonReaderState());
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject) break;
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;
                    string name = reader.GetString() ?? "";
                    if (name == "tracks") break;                       // past the head — stop
                    if (name == "contract")
                    {
                        if (!reader.Read()) break;
                        sawContract = reader.TokenType == JsonTokenType.String
                            && reader.GetString() == ContractV2;
                        continue;
                    }
                    if (name == "origin")
                    {
                        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) break;
                        int depth = reader.CurrentDepth;
                        string? prop = null;
                        while (reader.Read())
                        {
                            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth) { sawOrigin = true; break; }
                            if (reader.TokenType == JsonTokenType.PropertyName) { prop = reader.GetString(); continue; }
                            switch (prop)
                            {
                                case "kind": origin.Kind = reader.GetString() ?? ""; break;
                                case "machineId": origin.MachineId = reader.GetString() ?? ""; break;
                                case "storeSchema": if (reader.TokenType == JsonTokenType.Number) origin.StoreSchema = reader.GetInt32(); break;
                                case "truedat": origin.Truedat = reader.GetString() ?? ""; break;
                                case "exportedAt": origin.ExportedAt = reader.GetString() ?? ""; break;
                                case "generation": if (reader.TokenType == JsonTokenType.Number) origin.Generation = reader.GetInt64(); break;
                                case "sidecarSha256": origin.SidecarSha256 = reader.GetString() ?? ""; break;
                            }
                            prop = null;
                        }
                        if (!sawOrigin) break;
                        continue;
                    }
                    // any other header value: skip it (string/number/object/array)
                    if (!reader.Read()) break;
                    if (reader.TokenType == JsonTokenType.StartObject || reader.TokenType == JsonTokenType.StartArray)
                    {
                        if (!reader.TrySkip()) break;
                    }
                }
            }
            catch (JsonException) { return false; }
            return sawContract && sawOrigin;
        }
    }

    /// <summary>The three temp files and their targets for one publication.</summary>
    internal sealed class PublicationPlan
    {
        internal string CatalogPath = "";
        internal string SidecarPath = "";
        /// <summary>Null when this generation publishes no companion (edges off).</summary>
        internal string? CompanionPath;
        internal string CatalogTmp = "";
        internal string SidecarTmp = "";
        internal string? CompanionTmp;
        internal long Generation;
    }

    /// <summary>The ruled rename order. Each value names the step ABOUT to run.</summary>
    internal enum PublicationStep { CompanionAside = 1, Catalog = 2, Companion = 3, Sidecar = 4, Done = 5 }

    internal sealed class PublicationResult
    {
        internal long Generation;
        internal bool Completed;
        /// <summary>The last step that ran to completion (<see cref="PublicationStep.Done"/> when all did).</summary>
        internal PublicationStep Reached;
        internal DateTime CatalogMtimeUtc;
        internal DateTime SidecarMtimeUtc;
        internal string SidecarSha256 = "";
    }

    /// <summary>
    /// Publishes an export triple (catalog, sidecar, companion) from temp files, in the order the
    /// store-v3 plan rules (operator 2026-10-15; keeper PG02 recheck; hub a4814ab53):
    /// <list type="number">
    /// <item>move the live companion aside (<c>.prev</c>) — a legacy hub sees "no edges" for these
    /// seconds instead of serving the previous generation's edge values beside the new catalog
    /// (the state the keeper measured as mixed; operator call #7 = aside while the window is open);</item>
    /// <item>catalog (all parts);</item>
    /// <item>companion;</item>
    /// <item>sidecar LAST.</item>
    /// </list>
    /// Three renames are three publication points, so what makes this coherent is not the order
    /// alone: an UPGRADED hub binds the sidecar by <c>origin.sidecarSha256</c> and the companion by
    /// <c>origin.generation</c>, rejecting anything else; a LEGACY hub binds the sidecar by
    /// <c>mtime &gt;= catalog mtime</c> plus equal counts, and its one blind spot (a NEWER sidecar
    /// beside an older catalog) is unreachable when the sidecar lands last. The mtimes are set
    /// explicitly on the temps before any rename because a rename preserves the temp's write
    /// time (measured 2026-10-15): a sidecar temp hashed before the catalog temp exists would
    /// otherwise be older than the catalog forever.
    /// </summary>
    internal static class Publication
    {
        internal const string CompanionAsideSuffix = ".prev";
        /// <summary>FAT/exFAT timestamp granularity; the margin survives robocopy /MIR.</summary>
        internal const int MtimeMarginSeconds = 2;

        /// <summary>SHA-256 of a file as 64 lower-case hex characters (SHA-NI via SHA256Cng).</summary>
        internal static string Sha256Hex(string path)
        {
            using (var sha = new SHA256Cng())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
            {
                var hash = sha.ComputeHash(fs);
                var sb = new StringBuilder(64);
                foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>Catalog mtime for the new generation: <c>max(now, previous sidecar mtime + margin)</c>,
        /// so the previous sidecar is strictly older than the new catalog on a legacy reader's rule
        /// even if the clock went backwards or the previous publish was moments ago.</summary>
        internal static DateTime CatalogMtime(DateTime nowUtc, string liveSidecarPath)
        {
            var t = nowUtc;
            if (File.Exists(liveSidecarPath))
            {
                var prev = File.GetLastWriteTimeUtc(liveSidecarPath).AddSeconds(MtimeMarginSeconds);
                if (prev > t) t = prev;
            }
            return t;
        }

        /// <summary>Sidecar mtime: catalog mtime + margin, so the finished pair satisfies
        /// <c>sidecar &gt;= catalog</c> regardless of which temp was written first.</summary>
        internal static DateTime SidecarMtime(DateTime catalogMtimeUtc) => catalogMtimeUtc.AddSeconds(MtimeMarginSeconds);

        /// <summary>
        /// Run the publication. <paramref name="stopBefore"/> is the self-test's interruption hook: when
        /// it returns true for a step, the publisher returns as if the process died there — nothing
        /// after that step runs and the temps for the remaining steps are left in place. Production
        /// passes null.
        /// </summary>
        internal static PublicationResult Publish(PublicationPlan plan, DateTime nowUtc, Func<PublicationStep, bool>? stopBefore = null)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (!File.Exists(plan.CatalogTmp)) throw new FileNotFoundException("catalog temp missing", plan.CatalogTmp);
            if (!File.Exists(plan.SidecarTmp)) throw new FileNotFoundException("sidecar temp missing", plan.SidecarTmp);
            bool withCompanion = !string.IsNullOrEmpty(plan.CompanionPath);
            if (withCompanion && (string.IsNullOrEmpty(plan.CompanionTmp) || !File.Exists(plan.CompanionTmp)))
                throw new FileNotFoundException("companion temp missing", plan.CompanionTmp ?? "(null)");

            var result = new PublicationResult { Generation = plan.Generation };

            // Stamp the temps BEFORE any rename (rename preserves the write time).
            var tc = CatalogMtime(nowUtc, plan.SidecarPath);
            var ts = SidecarMtime(tc);
            File.SetLastWriteTimeUtc(plan.CatalogTmp, tc);
            File.SetLastWriteTimeUtc(plan.SidecarTmp, ts);
            result.CatalogMtimeUtc = tc;
            result.SidecarMtimeUtc = ts;
            result.SidecarSha256 = Sha256Hex(plan.SidecarTmp);

            // (1) companion aside
            if (stopBefore != null && stopBefore(PublicationStep.CompanionAside)) return result;
            if (withCompanion && File.Exists(plan.CompanionPath!))
            {
                var aside = plan.CompanionPath + CompanionAsideSuffix;
                if (File.Exists(aside)) File.Delete(aside);
                File.Move(plan.CompanionPath!, aside);
            }
            result.Reached = PublicationStep.CompanionAside;

            // (2) catalog — the commit point for upgraded readers
            if (stopBefore != null && stopBefore(PublicationStep.Catalog)) return result;
            Swap(plan.CatalogTmp, plan.CatalogPath);
            result.Reached = PublicationStep.Catalog;

            // (3) companion
            if (stopBefore != null && stopBefore(PublicationStep.Companion)) return result;
            if (withCompanion) Swap(plan.CompanionTmp!, plan.CompanionPath!);
            result.Reached = PublicationStep.Companion;

            // (4) sidecar LAST — completes the fast-boot pair
            if (stopBefore != null && stopBefore(PublicationStep.Sidecar)) return result;
            Swap(plan.SidecarTmp, plan.SidecarPath);
            result.Reached = PublicationStep.Sidecar;

            // Done: the aside copy has served its purpose (it is only evidence after an interruption).
            if (withCompanion)
            {
                var aside = plan.CompanionPath + CompanionAsideSuffix;
                try { if (File.Exists(aside)) File.Delete(aside); } catch { /* best effort */ }
            }
            result.Reached = PublicationStep.Done;
            result.Completed = true;
            return result;
        }

        /// <summary>One atomic rename: replace when the target exists, move when it does not.</summary>
        static void Swap(string tmp, string target)
        {
            if (File.Exists(target)) File.Replace(tmp, target, null);
            else File.Move(tmp, target);
        }

        /// <summary>
        /// M8: are the published files behind the store? True when the catalog carries no v2 origin
        /// (legacy or missing) or names an older generation. The caller re-publishes from the
        /// committed store (idempotent).
        /// </summary>
        internal static bool FilesBehind(string catalogPath, long storeGeneration, out long fileGeneration)
        {
            fileGeneration = 0;
            if (!CatalogOrigin.TryRead(catalogPath, out var o)) return true;
            fileGeneration = o.Generation;
            return o.Generation < storeGeneration;
        }
    }
}
