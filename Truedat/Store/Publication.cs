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
        /// <summary>The companion's contract (its root <c>contract</c> key; EdgeCompanion.Contract).</summary>
        internal const string CompanionContractV1 = "mbxedges.v1";

        internal string Kind = "scan";            // scan | export | import | restore | <verb>
        internal string MachineId = "";
        internal int StoreSchema;
        internal string Truedat = "";
        internal string ExportedAt = "";          // ISO-8601 UTC
        internal long Generation;
        /// <summary>SHA-256 of the sidecar bytes written from the same generation: 64 lower-case
        /// hex characters. The encoding is a contract (same class as <c>ruleId</c>).</summary>
        internal string SidecarSha256 = "";
        /// <summary>store-intent.md item (8): the root the export published — <c>library_root.path</c> byte for byte as STORED (no
        /// normalisation) and that row's role. Null when absent (a legacy export, a scan-written catalog): the file binding never
        /// depends on them; an absent root leaves the catalog unresolved for STORE selection only.</summary>
        internal string? RootPath;
        internal string? RootRole;

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
            // store-intent.md item (8): flat, after sidecarSha256, so a bounded prefix reader takes them like generation.
            if (o.RootPath != null) w.WriteString("rootPath", o.RootPath);
            if (o.RootRole != null) w.WriteString("rootRole", o.RootRole);
            w.WriteEndObject();
        }

        /// <summary>How many leading bytes <see cref="TryRead"/> examines. The header keys precede
        /// <c>tracks</c>, so this is the same bounded read the hub performs — never the whole file.</summary>
        internal const int HeadBytes = 64 * 1024;

        /// <summary>
        /// Read <c>contract</c> + <c>origin</c> from the head of a v2 catalog without parsing the
        /// file. False for a legacy v1 catalog (no <c>contract</c>), an unreadable file, a head that
        /// does not reach the keys before <c>tracks</c>, or a malformed origin (wrong token type,
        /// out-of-range number). Never throws.
        /// </summary>
        internal static bool TryRead(string catalogPath, out CatalogOrigin origin)
        {
            if (File.Exists(catalogPath) || !(Program.EnumerateCatalogParts(catalogPath) is var parts) || parts.Count == 0)
                return TryReadHead(catalogPath, ContractV2, out origin);
            // A split catalog (mbxmoods.json.1 .. .K): EVERY part carries the same origin, and the set reads as published only when they
            // all agree on the generation and the sidecar binding. A torn set (a publication interrupted between part renames, a part from
            // another generation) reads as "no v2 origin", so FilesBehind reports it and the next publication rewrites it whole — the same
            // every-part rule the hub applies (hub:H03a, MBX 51c1e9757).
            origin = new CatalogOrigin();
            for (int i = 0; i < parts.Count; i++)
            {
                if (!TryReadHead(parts[i].Value, ContractV2, out var o)) return false;
                if (i == 0) { origin = o; continue; }
                if (o.Generation != origin.Generation || !string.Equals(o.SidecarSha256, origin.SidecarSha256, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        /// <summary>
        /// The same bounded read for any export whose root carries <c>contract</c> and <c>origin</c>
        /// (the companion carries <c>origin.generation</c> from T07 on). <paramref name="requiredContract"/>
        /// must match the root <c>contract</c> exactly.
        /// </summary>
        internal static bool TryReadHead(string path, string requiredContract, out CatalogOrigin origin)
        {
            origin = new CatalogOrigin();
            byte[] head;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
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
                            && reader.GetString() == requiredContract;
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
                            // Malformed origin (wrong token type, out-of-range number) -> false, never a throw.
                            switch (prop)
                            {
                                case "kind": if (!Str(ref reader, out origin.Kind)) return false; break;
                                case "machineId": if (!Str(ref reader, out origin.MachineId)) return false; break;
                                case "storeSchema": if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out origin.StoreSchema)) return false; break;
                                case "truedat": if (!Str(ref reader, out origin.Truedat)) return false; break;
                                case "exportedAt": if (!Str(ref reader, out origin.ExportedAt)) return false; break;
                                case "generation": if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out origin.Generation)) return false; break;
                                case "sidecarSha256": if (!Str(ref reader, out origin.SidecarSha256)) return false; break;
                                case "rootPath": { if (!Str(ref reader, out var rp)) return false; origin.RootPath = rp; break; }
                                case "rootRole": { if (!Str(ref reader, out var rr)) return false; origin.RootRole = rr; break; }
                                default:   // an unknown origin key: skip its value, whatever shape
                                    if ((reader.TokenType == JsonTokenType.StartObject || reader.TokenType == JsonTokenType.StartArray) && !reader.TrySkip()) return false;
                                    break;
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
            catch (InvalidOperationException) { return false; }
            catch (FormatException) { return false; }
            return sawContract && sawOrigin;
        }

        static bool Str(ref Utf8JsonReader reader, out string value)
        {
            value = "";
            if (reader.TokenType != JsonTokenType.String) return false;
            value = reader.GetString() ?? "";
            return true;
        }
    }

    /// <summary>The temp files and their targets for one publication.</summary>
    internal sealed class PublicationPlan
    {
        internal string CatalogPath = "";
        internal string SidecarPath = "";
        /// <summary>The companion's KNOWN target path — always set, whether or not this generation
        /// publishes one, because a previous live companion must be retired either way.</summary>
        internal string CompanionPath = "";
        internal string CatalogTmp = "";
        /// <summary>A SPLIT publication: the part temps and their targets (mbxmoods.json.1 .. .K), in part order. Null = one catalog file
        /// (<see cref="CatalogTmp"/> -> <see cref="CatalogPath"/>). The catalog step renames every part, then removes the single file and any
        /// higher-numbered part a larger previous split left — before the companion and the sidecar, so a reader never pairs the new
        /// sidecar with an old catalog (INVARIANT: one file OR the parts, never both).</summary>
        internal System.Collections.Generic.List<(string Tmp, string Target)>? CatalogParts;
        internal string SidecarTmp = "";
        /// <summary>Null when this generation publishes no companion (edges off). The old one at
        /// <see cref="CompanionPath"/> is still moved aside and removed on completion.</summary>
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
    /// (the state the keeper measured as mixed; operator call #7 = aside while the window is open);
    /// this runs whether or not the new generation publishes a companion, so edges on → off never
    /// leaves the old one live beside the new catalog;</item>
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
            if (string.IsNullOrEmpty(plan.CompanionPath)) throw new ArgumentException("CompanionPath is the known target path and is always required", nameof(plan));
            if (plan.CatalogParts == null && !File.Exists(plan.CatalogTmp)) throw new FileNotFoundException("catalog temp missing", plan.CatalogTmp);
            if (plan.CatalogParts != null)
            {
                if (plan.CatalogParts.Count == 0) throw new ArgumentException("a split publication needs at least one part", nameof(plan));
                foreach (var p in plan.CatalogParts) if (!File.Exists(p.Tmp)) throw new FileNotFoundException("catalog part temp missing", p.Tmp);
            }
            if (!File.Exists(plan.SidecarTmp)) throw new FileNotFoundException("sidecar temp missing", plan.SidecarTmp);
            bool newCompanion = !string.IsNullOrEmpty(plan.CompanionTmp);
            if (newCompanion && !File.Exists(plan.CompanionTmp!))
                throw new FileNotFoundException("companion temp missing", plan.CompanionTmp);

            var result = new PublicationResult { Generation = plan.Generation };
            var aside = plan.CompanionPath + CompanionAsideSuffix;

            // Stamp the temps BEFORE any rename (rename preserves the write time).
            var tc = CatalogMtime(nowUtc, plan.SidecarPath);
            var ts = SidecarMtime(tc);
            if (plan.CatalogParts == null) File.SetLastWriteTimeUtc(plan.CatalogTmp, tc);
            else foreach (var p in plan.CatalogParts) File.SetLastWriteTimeUtc(p.Tmp, tc);
            File.SetLastWriteTimeUtc(plan.SidecarTmp, ts);
            result.CatalogMtimeUtc = tc;
            result.SidecarMtimeUtc = ts;
            result.SidecarSha256 = Sha256Hex(plan.SidecarTmp);

            // (1) companion aside — always, so a previous live companion never outlives this generation
            if (stopBefore != null && stopBefore(PublicationStep.CompanionAside)) return result;
            if (File.Exists(plan.CompanionPath))
            {
                if (File.Exists(aside)) File.Delete(aside);
                File.Move(plan.CompanionPath, aside);
            }
            result.Reached = PublicationStep.CompanionAside;

            // (2) catalog — the commit point for upgraded readers (all parts, then what the new set no longer holds)
            if (stopBefore != null && stopBefore(PublicationStep.Catalog)) return result;
            if (plan.CatalogParts == null) Swap(plan.CatalogTmp, plan.CatalogPath);
            else
            {
                foreach (var p in plan.CatalogParts) Swap(p.Tmp, p.Target);
                if (File.Exists(plan.CatalogPath)) File.Delete(plan.CatalogPath);
                foreach (var stale in Program.EnumerateCatalogParts(plan.CatalogPath))
                    if (stale.Key > plan.CatalogParts.Count) File.Delete(stale.Value);
            }
            result.Reached = PublicationStep.Catalog;

            // (3) companion — only when this generation has one
            if (stopBefore != null && stopBefore(PublicationStep.Companion)) return result;
            if (newCompanion) Swap(plan.CompanionTmp!, plan.CompanionPath);
            result.Reached = PublicationStep.Companion;

            // (4) sidecar LAST — completes the fast-boot pair
            if (stopBefore != null && stopBefore(PublicationStep.Sidecar)) return result;
            Swap(plan.SidecarTmp, plan.SidecarPath);
            result.Reached = PublicationStep.Sidecar;

            // Done: the aside copy has served its purpose (it is only evidence after an interruption).
            try { if (File.Exists(aside)) File.Delete(aside); } catch { /* best effort */ }
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
        /// M8 — the recovery predicate: is the published SET behind the store? It checks the whole
        /// set, not the catalog alone: the catalog must carry a v2 origin at the store's generation,
        /// the sidecar beside it must hash to <c>origin.sidecarSha256</c>, and the companion must be
        /// present at that generation when edges are on — or absent when they are off (a stale one
        /// left live is "behind", so the next publish retires it). Any failure names its reason.
        /// The caller re-publishes every temp from the committed store (idempotent).
        /// </summary>
        internal static bool FilesBehind(string catalogPath, string sidecarPath, string companionPath, bool expectCompanion,
            long storeGeneration, out string reason)
        {
            reason = "";
            if (!CatalogOrigin.TryRead(catalogPath, out var o)) { reason = "catalog has no v2 origin"; return true; }
            if (o.Generation != storeGeneration) { reason = $"catalog at generation {o.Generation}, store at {storeGeneration}"; return true; }
            if (!File.Exists(sidecarPath)) { reason = "sidecar missing"; return true; }
            string sha;
            try { sha = Sha256Hex(sidecarPath); } catch (Exception ex) { reason = "sidecar unreadable: " + ex.Message; return true; }
            if (!string.Equals(sha, o.SidecarSha256, StringComparison.Ordinal)) { reason = "sidecar is not the catalog's (hash mismatch)"; return true; }
            bool companionPresent = File.Exists(companionPath);
            if (expectCompanion)
            {
                if (!companionPresent) { reason = "companion missing"; return true; }
                if (!CatalogOrigin.TryReadHead(companionPath, CatalogOrigin.CompanionContractV1, out var c)) { reason = "companion has no origin"; return true; }
                if (c.Generation != storeGeneration) { reason = $"companion at generation {c.Generation}, store at {storeGeneration}"; return true; }
            }
            else if (companionPresent) { reason = "stale companion present (edges are off)"; return true; }
            return false;
        }
    }
}
