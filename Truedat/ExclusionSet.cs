using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;

namespace Truedat
{
    internal enum ExclusionKind { Folder, Genre, File }

    internal enum ExclusionAction { Exclude, Include }

    /// <summary>
    /// One typed exclusion rule. The vocabulary is deliberately tiny — folder glob,
    /// genre, single file — because every previous attempt at keeping files out of
    /// analysis inferred intent from an overloaded signal, and the fix is that a rule
    /// says exactly what it is and names itself in the ledger.
    /// </summary>
    internal sealed class ExclusionRule
    {
        public ExclusionKind Kind;
        public ExclusionAction Action;
        /// <summary>Folder pattern, genre name, or file path — exactly as authored.</summary>
        public string Value = "";
        /// <summary>
        /// Optional durable content identity (audioStreamSha256) for `file` rules. Read by
        /// PreviewPlanner.BuildRuleStats to report a rule whose path has gone as
        /// <c>moved-or-deleted</c> and to NAME the catalog paths that still hold the content.
        /// It is deliberately NOT part of matching: a `file` rule is exact normalized path
        /// equality, and re-matching by content would silently widen the rule to every copy of
        /// the audio (one sha maps to several paths — it is exactly what --duplicates groups on).
        /// Report and offer, never re-match. Also not part of <see cref="Identity"/>.
        /// </summary>
        public string? Sha;
        /// <summary>Optional operator note, round-tripped so the reason for a decision survives.</summary>
        public string? Note;
        /// <summary>Hits observed this run. Zero on a rule that should match something is the stale-rule signal.</summary>
        public long MatchCount;

        /// <summary>Pre-normalized match target, computed once at parse time.</summary>
        internal string Norm = "";
        /// <summary>True for a folder rule whose pattern is a root-independent fragment.</summary>
        internal bool IsFragment;

        /// <summary>What the ledger prints, e.g. "genre=Podcast".</summary>
        public string Describe()
        {
            switch (Kind)
            {
                case ExclusionKind.Folder: return "folder=" + Value;
                case ExclusionKind.Genre: return "genre=" + Value;
                default: return "file=" + Value;
            }
        }

        /// <summary>
        /// Merge/dedupe key: kind + action + normalized target. Note and sha are
        /// metadata, NOT identity — re-adding a rule that differs only by note/sha is
        /// a no-op (Merge counts it AlreadyPresent and keeps the stored Note/Sha as-is);
        /// it does not update them. Identity exists to prevent a second copy, not to
        /// carry edits.
        /// </summary>
        public string Identity() => (int)Kind + "|" + (int)Action + "|" + Norm;
    }

    /// <summary>
    /// The parsed exclusion file, and the only thing that decides whether a track is
    /// kept out of analysis. Pure: strings in, verdict out. No filesystem, no XML.
    ///
    /// Evaluation is one sentence with no precedence puzzle: if any include rule
    /// matches, keep; otherwise if any exclude rule matches, drop.
    ///
    /// Tolerant reader, matching the mbxmoods.json convention: unknown fields and
    /// unknown rule kinds are ignored-and-counted rather than fatal. The two fatal
    /// cases are unparseable JSON and "non-empty-but-nothing-valid" —
    /// both mean the operator believes exclusions are active when they are not, and
    /// scanning blind is the failure mode this whole design exists to prevent.
    /// </summary>
    internal sealed class ExclusionSet
    {
        static readonly ExclusionRule[] NoRules = new ExclusionRule[0];
        static readonly string[] NoDiagnostics = new string[0];

        public static ExclusionSet Empty { get; } = new ExclusionSet(NoRules, 0, NoDiagnostics);

        readonly ExclusionRule[] _rules;
        readonly string[] _diagnostics;

        ExclusionSet(ExclusionRule[] rules, int invalidCount, string[] diagnostics)
        {
            _rules = rules;
            _diagnostics = diagnostics;
            InvalidRuleCount = invalidCount;
            foreach (var r in rules)
                if (r.Kind == ExclusionKind.Genre) { HasGenreRules = true; break; }
        }

        public IReadOnlyList<ExclusionRule> Rules => _rules;
        public IReadOnlyList<string> Diagnostics => _diagnostics;
        public int InvalidRuleCount { get; }
        public bool IsEmpty => _rules.Length == 0;

        /// <summary>
        /// True when any rule matches on genre. Callers that do not already know a track's
        /// genre use this to decide whether reading it is worth the IO: the per-file scan
        /// paths check exclusions BEFORE any cache tier (deliberately — an excluded file must
        /// never stage and hash itself on every run), so a genre read there costs a TagLib
        /// open per file per run. Gating on this means the cost is paid only by operators who
        /// actually wrote a genre rule, and is zero for everyone else. Computed once at parse.
        /// </summary>
        public bool HasGenreRules { get; private set; }

        /// <summary>Fold separators and case the same way PathComparer does, so a rule
        /// written with either slash on either machine behaves identically.</summary>
        internal static string NormalizePath(string s) => s.Replace('/', '\\').ToUpperInvariant();

        public static ExclusionSet FromJson(string json, out string? fatalError)
        {
            fatalError = null;
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(json);
            }
            catch (Exception ex)
            {
                fatalError = "exclusion file is not valid JSON: " + ex.Message;
                return Empty;
            }
            if (root == null)
            {
                fatalError = "exclusion file is empty or null JSON";
                return Empty;
            }

            JsonArray? arr = null;
            try { arr = root["rules"] as JsonArray; } catch { }
            if (arr == null || arr.Count == 0) return Empty;   // no rules is legitimate

            var kept = new List<ExclusionRule>();
            var diags = new List<string>();
            int index = -1;
            foreach (var node in arr)
            {
                index++;
                var rule = TryParseRule(node, index, out var why);
                if (rule == null) { diags.Add(why!); continue; }
                kept.Add(rule);
            }

            if (kept.Count == 0)
            {
                fatalError = $"exclusion file has {arr.Count} rule(s) but none are valid — refusing to scan as if nothing were excluded";
                return Empty;
            }
            return new ExclusionSet(kept.ToArray(), diags.Count, diags.ToArray());
        }

        static ExclusionRule? TryParseRule(JsonNode? node, int index, out string? why)
        {
            why = null;
            if (!(node is JsonObject obj)) { why = $"rule[{index}]: not an object"; return null; }

            var kindText = Str(obj, "kind");
            var actionText = Str(obj, "action");

            ExclusionKind kind;
            if (string.Equals(kindText, "folder", StringComparison.OrdinalIgnoreCase)) kind = ExclusionKind.Folder;
            else if (string.Equals(kindText, "genre", StringComparison.OrdinalIgnoreCase)) kind = ExclusionKind.Genre;
            else if (string.Equals(kindText, "file", StringComparison.OrdinalIgnoreCase)) kind = ExclusionKind.File;
            else { why = $"rule[{index}]: unknown kind '{kindText}' (expected folder|genre|file)"; return null; }

            ExclusionAction action;
            if (string.Equals(actionText, "exclude", StringComparison.OrdinalIgnoreCase)) action = ExclusionAction.Exclude;
            else if (string.Equals(actionText, "include", StringComparison.OrdinalIgnoreCase)) action = ExclusionAction.Include;
            else { why = $"rule[{index}]: unknown action '{actionText}' (expected exclude|include)"; return null; }

            var rule = new ExclusionRule
            {
                Kind = kind,
                Action = action,
                Sha = Str(obj, "audioStreamSha256"),
                Note = Str(obj, "note"),
            };

            switch (kind)
            {
                case ExclusionKind.Folder:
                {
                    var pattern = (Str(obj, "pattern") ?? "").Trim();
                    if (pattern.Length == 0) { why = $"rule[{index}]: folder rule needs a pattern"; return null; }
                    if (!pattern.EndsWith("**", StringComparison.Ordinal))
                    {
                        why = $"rule[{index}]: folder pattern '{pattern}' must end in ** (subtree)";
                        return null;
                    }
                    var prefix = pattern.Substring(0, pattern.Length - 2);
                    var normPrefix = NormalizePath(prefix);
                    if (!normPrefix.EndsWith("\\", StringComparison.Ordinal))
                    {
                        why = $"rule[{index}]: folder pattern '{pattern}' must have a separator before ** (e.g. \\Podcasts\\**)";
                        return null;
                    }
                    // A fragment begins with a separator: that is what distinguishes it from
                    // an absolute pattern, and it is why plain Contains() is already
                    // boundary-aligned (both ends of the fragment are separators), so
                    // \Podcasts\** cannot match \MyPodcastsBackup\.
                    rule.IsFragment = normPrefix.StartsWith("\\", StringComparison.Ordinal);
                    if (normPrefix.Length <= 1) { why = $"rule[{index}]: folder pattern '{pattern}' is too broad"; return null; }
                    rule.Value = pattern;
                    rule.Norm = normPrefix;
                    return rule;
                }
                case ExclusionKind.Genre:
                {
                    var value = (Str(obj, "value") ?? "").Trim();
                    if (value.Length == 0) { why = $"rule[{index}]: genre rule needs a non-empty value"; return null; }
                    rule.Value = value;
                    rule.Norm = value.ToUpperInvariant();
                    return rule;
                }
                default:
                {
                    var path = (Str(obj, "path") ?? "").Trim();
                    if (path.Length == 0) { why = $"rule[{index}]: file rule needs a path"; return null; }
                    rule.Value = path;
                    rule.Norm = NormalizePath(path);
                    return rule;
                }
            }
        }

        /// <summary>Never throws on an unexpected node shape — a hostile file must not crash a scan.</summary>
        static string? Str(JsonObject obj, string key)
        {
            try
            {
                if (!obj.TryGetPropertyValue(key, out var v) || v == null) return null;
                return v.GetValue<string>();
            }
            catch { return null; }
        }

        /// <summary>
        /// True when this track should be kept out of analysis. <paramref name="genre"/>
        /// may be null (scan paths with no library metadata in hand) — genre rules simply
        /// never match then, which is correct rather than partial: folder and file rules
        /// still apply.
        /// </summary>
        public bool IsExcluded(string path, string? genre, out string reason)
        {
            reason = "";
            if (_rules.Length == 0 || string.IsNullOrEmpty(path)) return false;

            var normPath = NormalizePath(path);
            var normGenre = genre == null ? null : genre.Trim().ToUpperInvariant();

            ExclusionRule? firstExclude = null;
            bool included = false;
            foreach (var rule in _rules)
            {
                if (!Matches(rule, normPath, normGenre)) continue;
                Interlocked.Increment(ref rule.MatchCount);
                if (rule.Action == ExclusionAction.Include) included = true;
                else if (firstExclude == null) firstExclude = rule;
            }
            if (included || firstExclude == null) return false;
            reason = firstExclude.Describe();
            return true;
        }

        /// <summary>
        /// True when an <c>include</c> rule matched. Distinct from !IsExcluded, which is
        /// also true for a track no rule mentions at all — only a deliberate include
        /// overrides an exclude rule (include always wins, see IsExcluded). The former
        /// "overrides the legacy podcast heuristics" framing is gone with the heuristics
        /// themselves (Phase 3/4, 2026-07-25) — the sole production caller is now
        /// PreviewPlanner.cs (feeding CurrentDecision), not FilterExclusions (deleted).
        /// Does not touch MatchCount: this is a second look at rules IsExcluded already counted.
        /// </summary>
        public bool IsIncluded(string path, string? genre)
        {
            if (_rules.Length == 0 || string.IsNullOrEmpty(path)) return false;
            var normPath = NormalizePath(path);
            var normGenre = genre == null ? null : genre.Trim().ToUpperInvariant();
            foreach (var rule in _rules)
                if (rule.Action == ExclusionAction.Include && Matches(rule, normPath, normGenre))
                    return true;
            return false;
        }

        static bool Matches(ExclusionRule rule, string normPath, string? normGenre)
        {
            switch (rule.Kind)
            {
                case ExclusionKind.Folder:
                    return rule.IsFragment
                        ? normPath.IndexOf(rule.Norm, StringComparison.Ordinal) >= 0
                        : normPath.StartsWith(rule.Norm, StringComparison.Ordinal);
                case ExclusionKind.Genre:
                    return normGenre != null && normGenre.Length > 0 && normGenre == rule.Norm;
                default:
                    return normPath == rule.Norm;
            }
        }
    }
}
