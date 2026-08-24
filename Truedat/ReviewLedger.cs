using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Truedat
{
    /// <summary>
    /// One record per file the catalog does NOT hold analyzed features for, and why.
    ///
    /// Replaces <c>mbxmoods-errors.csv</c>, <c>mbxmoods-skipped.csv</c> and
    /// <c>mbxmoods-errors.log</c> — three artefacts describing one question ("why isn't
    /// this track in my catalog") that could disagree with each other, and did.
    ///
    /// Two independent axes, because they answer different questions:
    ///   DISPOSITION — why it is not in the catalog (a rule, a filter, a failure)
    ///   STATE       — why it is not being attempted (nobody looked / operator said
    ///                 leave it / truedat concluded it cannot succeed)
    ///
    /// Trying is the DEFAULT: a file with no record is scanned and needs no state.
    /// Excluding is an ACTION that removes a record from the queue by writing a rule —
    /// it is deliberately not a resting state here, because an excluded file never
    /// reaches the scan and so can never fail.
    /// </summary>
    internal enum ReviewDisposition
    {
        /// <summary>An operator rule in mbxmoods-exclude.json matched. Names the rule.</summary>
        Excluded,
        /// <summary>Structural or policy: video, stream URL, non-audio, DSD, over
        /// --max-duration, short file. Never attempted.</summary>
        Skipped,
        /// <summary>Analysis ran and produced nothing usable.</summary>
        Failed,
    }

    /// <summary>Why this file is not being attempted. See <see cref="ReviewDisposition"/>
    /// for why it is not in the catalog — the two are independent.</summary>
    internal enum ReviewState
    {
        /// <summary>Default for any new record: a human has not looked at it yet.
        /// This is the ONLY state counted as needing review.</summary>
        Review,
        /// <summary>The operator looked and chose to leave it alone.</summary>
        Ignore,
        /// <summary>truedat's OWN conclusion, on evidence that is not a guess.
        /// Never re-attempted automatically. Always carries the trigger that fired.</summary>
        Auto,
    }

    /// <summary>One failing component and what it actually said. The message is the tool's
    /// VERBATIM output — never summarized, never normalized. It is the evidence, and a
    /// paraphrase of an error is not evidence.</summary>
    internal sealed class ReviewComponent
    {
        public string Name = "";
        public string Message = "";
        public int? ExitCode;
        public long? DurationMs;
    }

    internal sealed class ReviewRecord
    {
        public string Path = "";
        public ReviewDisposition Disposition;
        public string Reason = "";
        public ReviewState State = ReviewState.Review;

        /// <summary>Which <see cref="ReviewState.Auto"/> trigger fired, and the value behind
        /// it. Present ONLY for Auto, and mandatory there: an automatic conclusion the
        /// operator cannot audit is indistinguishable from a guess.</summary>
        public string? StateReason;

        public DateTime FirstSeenUtc;
        public DateTime LastSeenUtc;

        /// <summary>How many times analysis has been ATTEMPTED. Only an explicit retry can
        /// advance this past 1, because a first failure stops automatic re-attempts — which
        /// is what makes the failed-too-often trigger a guard against repeated manual
        /// retries rather than a slow-converging counter.</summary>
        public int Attempts = 1;

        public List<ReviewComponent> Components = new List<ReviewComponent>();

        /// <summary>For <see cref="ReviewDisposition.Excluded"/>: the id of the rule that
        /// caught it. An exclusion the operator cannot trace back to its cause is
        /// unreviewable — they cannot undo it without guessing.</summary>
        public string? RuleId;

        public double? SizeMb;
        public double? DurationSec;
        public string? LastRunMode;
    }

    internal static class ReviewLedgerRules
    {
        /// <summary>A file at or under this size cannot carry analyzable audio.</summary>
        internal const long TinyFileBytes = 5 * 1024;

        /// <summary>Attempts after which truedat stops obliging without an explicit
        /// override. Only manual retries can reach it (see <see cref="ReviewRecord.Attempts"/>).</summary>
        internal const int TooManyAttempts = 2;

        /// <summary>
        /// truedat's own conclusion that a file cannot succeed, or null to leave it in
        /// <see cref="ReviewState.Review"/> for a human.
        ///
        /// Deliberately NARROW. Only evidence that is definitive rather than inferred
        /// qualifies: the extractor refusing the content outright, a file too small to hold
        /// audio, or a failure the operator has already re-driven past the point of value.
        /// Everything else stays in Review, because guessing here reproduces the
        /// heuristics-decide failure the exclusion arc removed.
        ///
        /// Notably NOT a trigger: speech. speechLikely is untuned and demonstrably fires on
        /// sparse instrumental, live and ambient music; auto-skipping on it would silently
        /// stop analyzing real music with nothing surfacing that it happened.
        /// </summary>
        internal static string? AutoConclusion(string reason, long sizeBytes, int attempts)
        {
            if (sizeBytes > 0 && sizeBytes <= TinyFileBytes)
                return $"file is {sizeBytes} bytes — too small to hold analyzable audio";
            if (!string.IsNullOrEmpty(reason) && reason.IndexOf("silent", StringComparison.OrdinalIgnoreCase) >= 0)
                return "the extractor identified the content as silent";
            if (attempts > TooManyAttempts)
                return $"attempted {attempts} times, failing each time";
            return null;
        }

        /// <summary>The review queue is what a human still has to look at. `ignore` was
        /// decided by the operator and `auto` by truedat, so neither is pending work.</summary>
        internal static bool NeedsReview(ReviewRecord r) => r.State == ReviewState.Review;
    }

    /// <summary>
    /// Load / upsert / save. Written ONCE per run, atomically (staged + swapped), never
    /// live-updating: the hub globs this directory and reads each manifest, and a reader
    /// that hits a file mid-write loses the manifest from its list entirely. A stale count
    /// is wrong by a little; a vanished manifest is wrong by everything.
    /// </summary>
    internal sealed class ReviewLedger
    {
        internal const string FileName = "mbxmoods-review.json";
        internal const string Kind = "review";

        readonly Dictionary<string, ReviewRecord> _byPath =
            new Dictionary<string, ReviewRecord>(PathComparer.Instance);

        internal int Count => _byPath.Count;
        internal IEnumerable<ReviewRecord> Records => _byPath.Values;

        internal ReviewRecord? Find(string path)
            => _byPath.TryGetValue(path ?? "", out var r) ? r : null;

        /// <summary>True when a record exists that should stop this file being attempted.
        /// Existence is sufficient — nothing is retried implicitly, in any mode.</summary>
        internal bool ShouldSkip(string path) => Find(path) != null;

        /// <summary>
        /// Add or update. The operator's <see cref="ReviewRecord.State"/> and the original
        /// <see cref="ReviewRecord.FirstSeenUtc"/> SURVIVE — a scan may observe, it may not
        /// overwrite a human decision or rewrite history. This is the direct fix for the
        /// old ledger, which was destroyed wholesale by the very command the advisor
        /// recommended, so the failure history never survived a retry.
        /// </summary>
        internal ReviewRecord Upsert(ReviewRecord incoming, DateTime nowUtc, bool isRetry)
        {
            if (!_byPath.TryGetValue(incoming.Path, out var existing))
            {
                incoming.FirstSeenUtc = nowUtc;
                incoming.LastSeenUtc = nowUtc;
                incoming.Attempts = 1;
                ApplyAuto(incoming);
                _byPath[incoming.Path] = incoming;
                return incoming;
            }

            existing.LastSeenUtc = nowUtc;
            existing.Disposition = incoming.Disposition;
            existing.Reason = incoming.Reason;
            existing.Components = incoming.Components;
            existing.RuleId = incoming.RuleId;
            existing.SizeMb = incoming.SizeMb ?? existing.SizeMb;
            existing.DurationSec = incoming.DurationSec ?? existing.DurationSec;
            existing.LastRunMode = incoming.LastRunMode ?? existing.LastRunMode;
            if (isRetry) existing.Attempts++;
            // An operator decision is never overwritten by an automatic conclusion.
            if (existing.State == ReviewState.Review) ApplyAuto(existing);
            return existing;
        }

        static void ApplyAuto(ReviewRecord r)
        {
            long bytes = r.SizeMb.HasValue ? (long)(r.SizeMb.Value * 1024 * 1024) : 0;
            var why = ReviewLedgerRules.AutoConclusion(r.Reason, bytes, r.Attempts);
            if (why == null) return;
            r.State = ReviewState.Auto;
            r.StateReason = why;
        }

        /// <summary>
        /// Fold another shard's ledger into this one. Union by path.
        ///
        /// `--chunk` partitions deterministically by <c>ChunkOwns</c>, so the same path should
        /// appear in exactly one shard and most of this never fires. It is defined anyway
        /// because "should" is not "does": a path that moved between shard runs, or overlapping
        /// M/N arguments, produces a genuine conflict, and a merge that resolved those
        /// arbitrarily would silently drop an operator decision.
        ///
        /// Conflict rules, each chosen so the merge cannot LOSE information:
        ///   firstSeen  — earliest wins. A merge must not rewrite history any more than a
        ///                rescan may.
        ///   lastSeen   — latest wins.
        ///   attempts   — MAX, not sum. The shards describe the same file, not two attempts
        ///                at it; summing would inflate it toward the auto trigger.
        ///   state      — a DECISION beats undecided, and an OPERATOR decision beats
        ///                truedat's: Ignore > Auto > Review. Anything else lets a shard that
        ///                merely has not looked at a file overwrite a shard where the
        ///                operator already ruled on it.
        ///   the rest   — from whichever record was observed most recently.
        /// </summary>
        internal void MergeFrom(ReviewLedger other)
        {
            if (other == null) return;
            foreach (var incoming in other._byPath.Values)
            {
                if (!_byPath.TryGetValue(incoming.Path, out var mine))
                {
                    _byPath[incoming.Path] = incoming;
                    continue;
                }

                var newer = incoming.LastSeenUtc > mine.LastSeenUtc ? incoming : mine;
                var older = ReferenceEquals(newer, incoming) ? mine : incoming;

                var merged = newer;
                merged.FirstSeenUtc = (older.FirstSeenUtc != DateTime.MinValue
                                       && (merged.FirstSeenUtc == DateTime.MinValue
                                           || older.FirstSeenUtc < merged.FirstSeenUtc))
                                      ? older.FirstSeenUtc : merged.FirstSeenUtc;
                merged.Attempts = Math.Max(mine.Attempts, incoming.Attempts);
                merged.State = StrongerState(mine.State, incoming.State);
                if (merged.State == ReviewState.Auto && string.IsNullOrEmpty(merged.StateReason))
                    merged.StateReason = mine.StateReason ?? incoming.StateReason;
                _byPath[incoming.Path] = merged;
            }
        }

        /// <summary>Ignore &gt; Auto &gt; Review. An operator decision outranks truedat's, and
        /// any decision outranks not having looked.</summary>
        internal static ReviewState StrongerState(ReviewState a, ReviewState b)
        {
            if (a == ReviewState.Ignore || b == ReviewState.Ignore) return ReviewState.Ignore;
            if (a == ReviewState.Auto || b == ReviewState.Auto) return ReviewState.Auto;
            return ReviewState.Review;
        }

        /// <summary>Set an operator state. Returns false when the path has no record.</summary>
        internal bool SetState(string path, ReviewState state)
        {
            var r = Find(path);
            if (r == null) return false;
            r.State = state;
            if (state != ReviewState.Auto) r.StateReason = null;
            return true;
        }

        internal int NeedsReviewCount => _byPath.Values.Count(ReviewLedgerRules.NeedsReview);

        // -- persistence ---------------------------------------------------

        /// <summary>Tolerant read: a missing file is an empty ledger (not an error), and
        /// unknown fields are ignored so a newer truedat's ledger does not break an older
        /// one. A CORRUPT file is different — it is returned as a failure so the caller can
        /// refuse rather than silently start from empty and lose the operator's states.</summary>
        internal static ReviewLedger Load(string path, out string? error)
        {
            error = null;
            var ledger = new ReviewLedger();
            if (!File.Exists(path)) return ledger;
            try
            {
                var text = File.ReadAllText(path);
                var root = JsonNode.Parse(text)?.AsObject();
                var arr = root? ["records"]?.AsArray();
                if (arr == null) return ledger;
                foreach (var node in arr)
                {
                    var o = node?.AsObject();
                    if (o == null) continue;
                    var rec = FromJson(o);
                    if (rec != null && rec.Path.Length > 0) ledger._byPath[rec.Path] = rec;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            return ledger;
        }

        static ReviewRecord? FromJson(JsonObject o)
        {
            string S(string k) => o[k]?.GetValue<string>() ?? "";
            var rec = new ReviewRecord
            {
                Path = S("path"),
                Reason = S("reason"),
                RuleId = o["ruleId"]?.GetValue<string>(),
                StateReason = o["stateReason"]?.GetValue<string>(),
                LastRunMode = o["lastRunMode"]?.GetValue<string>(),
            };
            rec.Disposition = ParseDisposition(S("disposition"));
            rec.State = ParseState(S("state"));
            if (int.TryParse(o["attempts"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var a))
                rec.Attempts = a;
            rec.FirstSeenUtc = ParseUtc(S("firstSeen"));
            rec.LastSeenUtc = ParseUtc(S("lastSeen"));
            if (double.TryParse(o["sizeMb"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var mb))
                rec.SizeMb = mb;
            if (double.TryParse(o["durationSec"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ds))
                rec.DurationSec = ds;
            var comps = o["components"]?.AsArray();
            if (comps != null)
            {
                foreach (var c in comps)
                {
                    var co = c?.AsObject();
                    if (co == null) continue;
                    var comp = new ReviewComponent
                    {
                        Name = co["name"]?.GetValue<string>() ?? "",
                        Message = co["message"]?.GetValue<string>() ?? "",
                    };
                    if (int.TryParse(co["exitCode"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ec))
                        comp.ExitCode = ec;
                    if (long.TryParse(co["durationMs"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dm))
                        comp.DurationMs = dm;
                    rec.Components.Add(comp);
                }
            }
            return rec;
        }

        internal static ReviewDisposition ParseDisposition(string s)
        {
            if (string.Equals(s, "excluded", StringComparison.OrdinalIgnoreCase)) return ReviewDisposition.Excluded;
            if (string.Equals(s, "skipped", StringComparison.OrdinalIgnoreCase)) return ReviewDisposition.Skipped;
            return ReviewDisposition.Failed;
        }

        internal static ReviewState ParseState(string s)
        {
            if (string.Equals(s, "ignore", StringComparison.OrdinalIgnoreCase)) return ReviewState.Ignore;
            if (string.Equals(s, "auto", StringComparison.OrdinalIgnoreCase)) return ReviewState.Auto;
            return ReviewState.Review;
        }

        internal static string DispositionName(ReviewDisposition d)
            => d == ReviewDisposition.Excluded ? "excluded"
             : d == ReviewDisposition.Skipped ? "skipped"
             : "failed";

        internal static string StateName(ReviewState s)
            => s == ReviewState.Ignore ? "ignore"
             : s == ReviewState.Auto ? "auto"
             : "review";

        static DateTime ParseUtc(string s)
            => DateTime.TryParse(s, CultureInfo.InvariantCulture,
                   DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
               ? d : DateTime.MinValue;

        static string Utc(DateTime d) => d == DateTime.MinValue ? "" : d.ToString("o", CultureInfo.InvariantCulture);

        /// <summary>Serialize to the manifest envelope the hub renders. <c>kind</c> is
        /// REQUIRED and declared, never inferred from the payload's shape: a shape-sniff
        /// fails silently and renders the wrong view with no error.</summary>
        internal JsonObject ToJson(string generatedBy, DateTime nowUtc)
        {
            var arr = new JsonArray();
            foreach (var r in _byPath.Values.OrderBy(r => ReviewLedgerRules.NeedsReview(r) ? 0 : 1)
                                            .ThenBy(r => DispositionName(r.Disposition), StringComparer.Ordinal)
                                            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
            {
                var o = new JsonObject
                {
                    ["path"] = r.Path,
                    ["disposition"] = DispositionName(r.Disposition),
                    ["reason"] = r.Reason,
                    ["state"] = StateName(r.State),
                    ["firstSeen"] = Utc(r.FirstSeenUtc),
                    ["lastSeen"] = Utc(r.LastSeenUtc),
                    ["attempts"] = r.Attempts,
                };
                if (!string.IsNullOrEmpty(r.StateReason)) o["stateReason"] = r.StateReason;
                if (!string.IsNullOrEmpty(r.RuleId)) o["ruleId"] = r.RuleId;
                if (r.SizeMb.HasValue) o["sizeMb"] = Math.Round(r.SizeMb.Value, 2);
                if (r.DurationSec.HasValue) o["durationSec"] = Math.Round(r.DurationSec.Value, 1);
                if (!string.IsNullOrEmpty(r.LastRunMode)) o["lastRunMode"] = r.LastRunMode;
                if (r.Components.Count > 0)
                {
                    var ca = new JsonArray();
                    foreach (var c in r.Components)
                    {
                        var co = new JsonObject { ["name"] = c.Name, ["message"] = c.Message };
                        if (c.ExitCode.HasValue) co["exitCode"] = c.ExitCode.Value;
                        if (c.DurationMs.HasValue) co["durationMs"] = c.DurationMs.Value;
                        ca.Add(co);
                    }
                    o["components"] = ca;
                }
                arr.Add(o);
            }

            return new JsonObject
            {
                ["kind"] = Kind,
                ["version"] = 1,
                ["generatedAt"] = Utc(nowUtc),
                ["generatedBy"] = generatedBy,
                ["counts"] = new JsonObject
                {
                    ["total"] = _byPath.Count,
                    ["needsReview"] = NeedsReviewCount,
                    ["excluded"] = _byPath.Values.Count(r => r.Disposition == ReviewDisposition.Excluded),
                    ["skipped"] = _byPath.Values.Count(r => r.Disposition == ReviewDisposition.Skipped),
                    ["failed"] = _byPath.Values.Count(r => r.Disposition == ReviewDisposition.Failed),
                },
                ["records"] = arr,
            };
        }
    }
}
