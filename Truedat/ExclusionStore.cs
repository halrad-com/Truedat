using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Truedat
{
    /// <summary>Outcome of applying a decisions delta, so the operator (or the hub UI
    /// relaying this) sees exactly what changed instead of a silent write.</summary>
    internal sealed class MergeReport
    {
        public int Added;
        public int Removed;
        public int AlreadyPresent;
        public int NotFound;
        public bool Changed;
        public string? BackupPath;
        /// <summary>Non-fatal per-rule diagnostics from the incoming decisions delta —
        /// e.g. an "add" array with one good rule and one typo'd one applies the good
        /// one and reports the typo here, rather than dropping it with no trace.</summary>
        public List<string> Diagnostics = new List<string>();
    }

    /// <summary>
    /// Filesystem side of the exclusion file: where it lives, how it is written, and
    /// how a decisions delta merges into it.
    ///
    /// Merge (not overwrite) is the point. The file has several legitimate authors —
    /// a hand edit, --apply-exclusions, and MBXHub relaying a review page — so a
    /// whole-file write would silently discard whichever author went second. Deltas
    /// plus a backup make concurrent authorship safe, and keeping the merge here
    /// means the hub delegates to truedat rather than reimplementing the semantics.
    /// </summary>
    internal static class ExclusionStore
    {
        public const string FileName = "mbxmoods-exclude.json";
        public const string ApplyResultFileName = "apply-result.json";

        /// <summary>
        /// Write the outcome of an apply beside the exclusion file, so a caller reads the
        /// result instead of parsing console output. MBXHub keeps only a bounded, async tail
        /// of stdout, which would truncate intermittently rather than obviously.
        ///
        /// Written on FAILURE as well as success — a refusal is precisely when the caller
        /// needs the reason, and `ok` plus `error` say so unambiguously.
        /// </summary>
        public static string WriteApplyResult(string exclusionsPath, MergeReport report, string? error)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(exclusionsPath));
            if (string.IsNullOrEmpty(dir)) dir = Environment.CurrentDirectory;
            var path = Path.Combine(dir!, ApplyResultFileName);

            var root = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["kind"] = "exclusion-apply-result",
                ["generated"] = DateTime.UtcNow.ToString("o"),
                ["exclusionsPath"] = exclusionsPath,
                ["ok"] = error == null,
                ["added"] = report.Added,
                ["removed"] = report.Removed,
                ["alreadyPresent"] = report.AlreadyPresent,
                ["notFound"] = report.NotFound,
                ["changed"] = report.Changed,
            };
            if (report.BackupPath != null) root["backupPath"] = report.BackupPath;
            if (error != null) root["error"] = error;
            // Diagnostics is inline-initialised and never assigned null, so only the count
            // matters here — a null check would read as if it could be.
            if (report.Diagnostics.Count > 0)
            {
                var arr = new JsonArray();
                foreach (var d in report.Diagnostics) arr.Add(d);
                root["diagnostics"] = arr;
            }
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            return path;
        }

        /// <summary>Canonical exclusion path for a given moods file (same directory).</summary>
        public static string Resolve(string moodsPath)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(moodsPath));
            if (string.IsNullOrEmpty(dir)) dir = Environment.CurrentDirectory;
            return Path.Combine(dir!, FileName);
        }

        /// <summary>
        /// Load the file if present. A missing file is NOT an error — that is a fresh
        /// install with nothing excluded. A present-but-broken file IS an error, because
        /// continuing would scan everything while the operator believes otherwise.
        /// </summary>
        public static ExclusionSet Load(string path, out string? fatalError)
        {
            fatalError = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return ExclusionSet.Empty;
            string json;
            try { json = File.ReadAllText(path); }
            catch (Exception ex)
            {
                fatalError = $"cannot read exclusion file {path}: {ex.Message}";
                return ExclusionSet.Empty;
            }
            if (json.Trim().Length == 0) return ExclusionSet.Empty;
            var set = ExclusionSet.FromJson(json, out fatalError);
            if (fatalError != null) fatalError = $"{path}: {fatalError}";
            return set;
        }

        public static MergeReport Merge(string canonicalPath, string decisionsJson, string updatedBy, out string? error)
        {
            error = null;
            var report = new MergeReport();

            var add = new List<ExclusionRule>();
            var remove = new List<ExclusionRule>();
            if (!TryParseDecisions(decisionsJson, add, remove, report.Diagnostics, out error)) return report;

            // Refuse to merge into a file we cannot understand: rewriting it would
            // destroy rules we failed to parse.
            var existingSet = Load(canonicalPath, out var loadErr);
            if (loadErr != null) { error = loadErr; return report; }

            // Same refusal for the PARTIAL case: Load is a tolerant reader that drops
            // unparseable/unknown-kind rules and merely counts them. Merge rebuilds the
            // file from existingSet.Rules, so writing here would silently erase every
            // rule that failed to parse — exactly what the comment above already
            // forbids for the fatal case. A future-kind rule from a newer MBXHub build
            // is the concrete scenario this guards: an older truedat must not delete a
            // decision it doesn't understand.
            if (existingSet.InvalidRuleCount > 0)
            {
                error = $"{canonicalPath}: {existingSet.InvalidRuleCount} existing rule(s) failed to parse — refusing to merge (would silently drop them):\n"
                    + string.Join("\n", existingSet.Diagnostics);
                return report;
            }

            var merged = new List<ExclusionRule>(existingSet.Rules);
            var byIdentity = new Dictionary<string, ExclusionRule>(StringComparer.Ordinal);
            foreach (var r in merged) byIdentity[r.Identity()] = r;

            foreach (var r in remove)
            {
                if (byIdentity.TryGetValue(r.Identity(), out var hit))
                {
                    merged.Remove(hit);
                    byIdentity.Remove(r.Identity());
                    report.Removed++;
                }
                else report.NotFound++;
            }
            foreach (var r in add)
            {
                if (byIdentity.ContainsKey(r.Identity())) { report.AlreadyPresent++; continue; }
                merged.Add(r);
                byIdentity[r.Identity()] = r;
                report.Added++;
            }

            report.Changed = report.Added > 0 || report.Removed > 0;
            if (!report.Changed) return report;

            if (File.Exists(canonicalPath))
            {
                try
                {
                    var backup = canonicalPath + ".bak." + DateTime.Now.ToString("yyyyMMdd.HHmmss");
                    File.Copy(canonicalPath, backup, true);
                    report.BackupPath = backup;
                }
                catch (Exception ex)
                {
                    error = $"cannot back up {canonicalPath}: {ex.Message}";
                    return report;
                }
            }

            try { Write(canonicalPath, merged, updatedBy); }
            catch (Exception ex) { error = $"cannot write {canonicalPath}: {ex.Message}"; }
            return report;
        }

        public static void Write(string path, IEnumerable<ExclusionRule> rules, string updatedBy)
        {
            var arr = new JsonArray();
            foreach (var r in rules)
            {
                var obj = new JsonObject
                {
                    ["kind"] = r.Kind == ExclusionKind.Folder ? "folder" : r.Kind == ExclusionKind.Genre ? "genre" : "file",
                    ["action"] = r.Action == ExclusionAction.Include ? "include" : "exclude",
                };
                if (r.Kind == ExclusionKind.Folder) obj["pattern"] = r.Value;
                else if (r.Kind == ExclusionKind.Genre) obj["value"] = r.Value;
                else obj["path"] = r.Value;
                if (!string.IsNullOrEmpty(r.Sha)) obj["audioStreamSha256"] = r.Sha;
                if (!string.IsNullOrEmpty(r.Note)) obj["note"] = r.Note;
                arr.Add(obj);
            }
            var root = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["updatedUtc"] = DateTime.UtcNow.ToString("o"),
                ["updatedBy"] = updatedBy,
                ["rules"] = arr,
            };
            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }

        /// <summary>
        /// Parse a decisions delta. Reuses ExclusionSet's rule parser by wrapping each
        /// side in a rules document, so the two paths cannot drift on what a valid rule is.
        /// </summary>
        static bool TryParseDecisions(string json, List<ExclusionRule> add, List<ExclusionRule> remove, List<string> diagnostics, out string? error)
        {
            error = null;
            JsonNode? root;
            try { root = JsonNode.Parse(json); }
            catch (Exception ex) { error = "decisions document is not valid JSON: " + ex.Message; return false; }
            if (root == null) { error = "decisions document is empty"; return false; }

            if (!Side(root, "add", add, diagnostics, out error)) return false;
            if (!Side(root, "remove", remove, diagnostics, out error)) return false;
            if (add.Count == 0 && remove.Count == 0)
            {
                error = "decisions document contains no valid rules in add or remove";
                return false;
            }
            return true;
        }

        static bool Side(JsonNode root, string name, List<ExclusionRule> into, List<string> diagnostics, out string? error)
        {
            error = null;
            JsonArray? arr = null;
            try { arr = root[name] as JsonArray; } catch { }
            if (arr == null || arr.Count == 0) return true;
            var wrapped = new JsonObject { ["schemaVersion"] = 1, ["rules"] = JsonNode.Parse(arr.ToJsonString()) };
            var set = ExclusionSet.FromJson(wrapped.ToJsonString(), out var why);
            if (why != null)
            {
                // wrapped is JSON we built ourselves from a non-empty array, so the only
                // reachable failure here is "none of this side's rules are valid" — relay
                // it in decisions-document terms. ExclusionSet.FromJson's own wording talks
                // about "refusing to scan", which is misleading here: nothing is being
                // scanned, and MBXHub is specced to relay this string verbatim into a UI
                // response, so it must not claim a scan is in play.
                error = $"decisions '{name}': has {arr.Count} rule(s) but none are valid";
                return false;
            }
            // Non-fatal: some rules in this side were invalid but at least one was good.
            // Those get applied silently unless we carry the diagnostic forward — this is
            // the delta-side twin of the canonical-file InvalidRuleCount check above.
            foreach (var d in set.Diagnostics) diagnostics.Add($"decisions '{name}': {d}");
            foreach (var r in set.Rules) into.Add(r);
            return true;
        }
    }
}
