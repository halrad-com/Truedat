using System.Collections.Generic;

namespace Truedat
{
    /// <summary>
    /// The read-only work plan `--preview` produces: what a scan would do, what it would
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
        /// <summary>How many files had their headers read for podcast markers. The only
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
        /// <summary>"measured-rtf" | "default-rtf" | "unavailable".</summary>
        public string EtaBasis = "unavailable";
    }

    internal sealed class PreviewBucket
    {
        public string Class = "";
        public int Count = 0;
    }

    internal sealed class PreviewRuleStat
    {
        public string Rule = "";
        public string Action = "";
        public long MatchCount = 0;
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
