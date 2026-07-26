using System.Collections.Generic;

namespace Truedat
{
    /// <summary>
    /// The work plan `--preview` produces: what a scan would do, what it would
    /// skip and why, and which tracks a human should look at before it runs. Plain data —
    /// PreviewPlanner fills it, PreviewWriter serialises it, and (Phase 2b) the review page
    /// renders it. Deliberately free of behaviour so both sides can be tested against a
    /// hand-built instance.
    /// </summary>
    internal sealed class PreviewPlan
    {
        public string XmlPath = "";
        public string MoodsPath = "";
        public string ExclusionsPath = "";
        public PreviewLimits Limits = new PreviewLimits();
        public PreviewCounts Counts = new PreviewCounts();
        public PreviewEstimate Estimate = new PreviewEstimate();
        /// <summary>Structural skips, by class — tracks a scan cannot analyze at all.</summary>
        public List<PreviewBucket> AutoSkip = new List<PreviewBucket>();
        /// <summary>Every exclusion rule with its hit count, so a stale rule is visible.</summary>
        public List<PreviewRuleStat> Rules = new List<PreviewRuleStat>();
        public List<PreviewGenre> Genres = new List<PreviewGenre>();
        /// <summary>Review candidates, capped. ReviewTotal carries the true count.</summary>
        public List<PreviewCandidate> Review = new List<PreviewCandidate>();
        public int ReviewTotal = 0;
        public bool ReviewTruncated = false;
        /// <summary>How many files had their headers read for speech markers. The only
        /// place preview touches audio, so it is reported rather than silent.</summary>
        public int SniffedCount = 0;
    }

    /// <summary>The ceiling and the review threshold, both read from configuration so no
    /// consumer ever hardcodes them.</summary>
    internal sealed class PreviewLimits
    {
        public int MaxDurationSecs = 0;
        /// <summary>"default" or "--max-duration".</summary>
        public string MaxDurationSource = "default";
        public int LongTrackSecs = 0;
        public string ExtractorNote = "";
    }

    /// <summary>Headline counts, so a consumer can badge "N awaiting review" without
    /// reading thousands of candidate rows.</summary>
    internal sealed class PreviewCounts
    {
        public int LibraryTotal = 0;
        public int Analyzed = 0;
        public int Excluded = 0;
        public int AwaitingReview = 0;
    }

    /// <summary>What the scan would cost. EtaBasis names the model used so a consumer can
    /// tell a measured estimate from a guess.</summary>
    internal sealed class PreviewEstimate
    {
        public int NewTracks = 0;
        public long NewBytes = 0;
        public int CachedTracks = 0;
        /// <summary>Negative means "not estimable" — omitted rather than guessed.</summary>
        public double EtaSecs = -1;
        /// <summary>"catalog-rtf" (what --preview emits: derived from the catalog's stored
        /// per-track analysis times) | "measured-rtf" (the planner's default label, for a
        /// caller that genuinely measured) | "unavailable". Set from
        /// PreviewPlannerInput.EtaBasisLabel, never post-edited by the caller.</summary>
        public string EtaBasis = "unavailable";
    }

    internal sealed class PreviewBucket
    {
        public string Class = "";
        public int Count = 0;
    }

    /// <summary>
    /// One exclusion rule's hit count, plus why a zero count means what it means.
    ///
    /// MatchCount alone is the documented stale-rule signal, and on a `file` rule it lies in a
    /// specific way: a rule whose target MOVED reports 0 exactly like a rule whose target never
    /// existed, and the documented operator response to a zero count is to delete the rule —
    /// which silently re-admits the file. <see cref="State"/> separates the two, and
    /// <see cref="Candidates"/> names where the content is now when the rule recorded an
    /// audioStreamSha256 and the catalog still holds it.
    ///
    /// This is REPORTING, not matching. Spec §4.2 says a moved `file` rule is "reported as moved
    /// or deleted and is re-resolvable against the catalog's sha index" — an offer to the
    /// operator, not a silent re-match. Nothing here makes a rule start excluding a path the
    /// operator did not name; that would widen a rule's reach behind their back, and because
    /// audioStreamSha256 is exactly what --duplicates groups on, one sha routinely maps to
    /// several copies.
    /// </summary>
    internal sealed class PreviewRuleStat
    {
        /// <summary>Path-matching rule whose target is present, or a rule whose kind is not
        /// path-anchored (folder/genre), or existence could not be checked.</summary>
        public const string StateLive = "live";
        /// <summary>`file` rule whose path is absent from disk but STILL PRESENT in the catalog:
        /// the audio has not moved, this machine just cannot see it. The metadata-mirror case
        /// (audio on another box) — reporting those as moved-or-deleted would recreate exactly
        /// the "every rule looks stale on a mirror" failure C-1 removed.</summary>
        public const string StateUnreachable = "path-unreachable";
        /// <summary>`file` rule whose path is absent from disk AND from the catalog.</summary>
        public const string StateMoved = "moved-or-deleted";

        public string Rule = "";
        public string Action = "";
        public long MatchCount = 0;
        /// <summary>One of the State* constants above.</summary>
        public string State = StateLive;
        /// <summary>Current catalog paths whose audioStreamSha256 equals the rule's recorded
        /// sha — where the content is now. Empty when the rule carries no sha, when the sha is
        /// not in the catalog (an unanalyzed file has no entry, so the operator could not have
        /// obtained its sha either — likely genuinely deleted), or when State is not
        /// <see cref="StateMoved"/>. ALL matches are listed, never an arbitrary one.</summary>
        public List<string> Candidates = new List<string>();
    }

    internal sealed class PreviewGenre
    {
        public string Name = "";
        public int Tracks = 0;
        public long TotalSecs = 0;
    }

    /// <summary>One track a human should decide about, with the evidence for why it surfaced.</summary>
    internal sealed class PreviewCandidate
    {
        public string Path = "";
        public string Artist = "";
        public string Title = "";
        public string Album = "";
        public string Genre = "";
        public string Codec = "";
        public int DurationSecs = 0;
        /// <summary>"new" | "analyzed".</summary>
        public string State = "new";
        /// <summary>Why this surfaced: "long", "over-limit", "marker:PCST", "genre-rule",
        /// "speech-likely", "excluded".</summary>
        public List<string> Reasons = new List<string>();
        public string? SpeechLikely = null;
        public bool OverLimit = false;
        /// <summary>"excluded" | "included" | "undecided" — the current rule verdict, so
        /// decisions are reversible from the same surface.</summary>
        public string CurrentDecision = "undecided";
    }
}
