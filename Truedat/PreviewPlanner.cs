using System;
using System.Collections.Generic;

namespace Truedat
{
    /// <summary>Everything PreviewPlanner needs, injected rather than reached for, so the
    /// planner is testable with no filesystem and no verdict engine.</summary>
    internal sealed class PreviewPlannerInput
    {
        public List<ITunesTrack> Tracks = new List<ITunesTrack>();
        public IDictionary<string, TrackEntry> Catalog = new Dictionary<string, TrackEntry>();
        public ExclusionSet Exclusions = ExclusionSet.Empty;
        public string XmlPath = "";
        public string MoodsPath = "";
        public string ExclusionsPath = "";
        public int MaxDurationSecs = 12000;
        public string MaxDurationSource = "default";
        public int LongTrackSecs = 1800;
        public int ReviewCap = 500;
        /// <summary>Existence check for a track's local file; null disables the check (which is
        /// what keeps the existing self-tests filesystem-free).</summary>
        public Func<string, bool>? FileExists;
        /// <summary>Bounded header read for embedded podcast markers; null disables sniffing.</summary>
        public Func<string, string?>? SniffMarkers;
        /// <summary>Measured analysis thread-seconds per audio-second. 0 = nothing measured.</summary>
        public double MeasuredRtf;
        public int Parallelism = 1;
        /// <summary>Recomputed speech verdict for an already-analyzed entry; null disables.</summary>
        public Func<TrackEntry, string?>? SpeechVerdict;
        /// <summary>Decides whether a catalog entry counts as already analyzed — i.e. a cache
        /// hit the scan would NOT re-run. Inject the scan's own HasCurrentFeatures so preview,
        /// the pre-flight and the scan share one classifier and cannot disagree (I1). Null falls
        /// back to "has any features", which under-reports a --refresh-features wave — so the
        /// real caller always injects it, and only self-tests that predate the wave rely on the
        /// default.</summary>
        public Func<TrackEntry?, bool>? IsAnalyzed;
        /// <summary>What to call the ETA's model when <see cref="MeasuredRtf"/> is usable.
        /// Injected rather than relabelled afterwards: --preview's RTF is derived from the
        /// catalog's stored analysis times ("catalog-rtf"), not from a fresh measurement, and
        /// a caller that had to post-edit the field would duplicate this file's string
        /// literal across files — which is exactly the coupling this replaces.</summary>
        public string EtaBasisLabel = "measured-rtf";
    }

    /// <summary>
    /// Builds the read-only scan work plan. Pure computation over the parsed library, the
    /// existing catalog and the exclusion rules — it never analyzes audio and never writes
    /// anything. The one exception is a bounded header sniff over review CANDIDATES only
    /// (hundreds of files, not the whole library), which is what lets the evidence column
    /// say "marker:PCST"; the count of files sniffed is reported rather than hidden.
    ///
    /// The classification split matters and mirrors the scan's own layering: STRUCTURAL
    /// skips (missing, video, stream URL, non-audio, DSD, over the duration ceiling) are
    /// things a scan *cannot* analyze, so they are counted and never offered for review — no
    /// human decision can change them. Everything else is policy, and policy is reviewable.
    ///
    /// The new-work accounting (newTracks / newBytes / the ETA) counts only tracks a scan
    /// would actually hand to Essentia — so every filter a scan applies has to be applied
    /// here first, which is why the exclusion determination sits ABOVE the catalog-state
    /// block rather than after it. A speech label is no longer a filter (see the
    /// "speech-labelled" review reason below) — it is evidence only, so it never removes
    /// a track from new-work accounting.
    /// </summary>
    internal static class PreviewPlanner
    {
        /// <summary>
        /// Maps <see cref="Program.StructuralSkipReason"/>'s reason text onto the
        /// preview.json <c>autoSkip[].class</c> names. The classifier is shared with the
        /// scan entry points so preview cannot drift from what a scan actually refuses
        /// (open-coding the extension sets here is what silently omitted DSD), but the
        /// class names are consumer-visible contract, so they are mapped rather than
        /// inheriting the human-readable reason strings verbatim.
        /// </summary>
        static string StructuralBucketClass(string reason) =>
            reason == "unsupported codec: DSD" ? "dsd"
            : reason == "video file extension" ? "video"
            : "nonAudio";

        public static PreviewPlan Build(PreviewPlannerInput input)
        {
            var plan = new PreviewPlan
            {
                XmlPath = input.XmlPath,
                MoodsPath = input.MoodsPath,
                ExclusionsPath = input.ExclusionsPath,
                Limits = new PreviewLimits
                {
                    MaxDurationSecs = input.MaxDurationSecs,
                    MaxDurationSource = input.MaxDurationSource,
                    LongTrackSecs = input.LongTrackSecs,
                    ExtractorNote = "stock Essentia extractor caps near 12,172s (~203 min); "
                                  + "raise --max-duration only against the patched build",
                },
            };
            plan.Counts.LibraryTotal = input.Tracks.Count;

            var buckets = new Dictionary<string, int>(StringComparer.Ordinal);
            void Bump(string cls)
            {
                int n;
                buckets.TryGetValue(cls, out n);
                buckets[cls] = n + 1;
            }

            var genres = new Dictionary<string, PreviewGenre>(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<PreviewCandidate>();
            long newBytes = 0;
            int newTracks = 0, cachedTracks = 0;
            long newAudioMs = 0;

            foreach (var t in input.Tracks)
            {
                var loc = t.Location ?? "";
                if (loc.Length == 0) continue;

                // --- genre histogram covers the whole library, before any filtering, so the
                // operator can see what a genre rule would actually cost them.
                var gname = string.IsNullOrEmpty(t.Genre) ? "(none)" : t.Genre!;
                PreviewGenre g;
                if (!genres.TryGetValue(gname, out g))
                {
                    g = new PreviewGenre { Name = gname };
                    genres[gname] = g;
                }
                g.Tracks++;
                if (t.TotalTimeMs > 0) g.TotalSecs += t.TotalTimeMs / 1000;

                // --- structural: cannot analyze, not reviewable ---
                if (t.IsRemote) { Bump("streamUrl"); continue; }
                // A remote stream URL has no local file, so this check must come after the
                // IsRemote check above — otherwise every stream would misclassify as "missing".
                if (input.FileExists != null && !input.FileExists(loc)) { Bump("missing"); continue; }
                // Extension-based structural classes come from the ONE shared classifier the
                // scan entry points use, so a class can never be handled in one mode and
                // missed in another (DSD was missed here while every scan mode refused it).
                var structReason = Program.StructuralSkipReason(loc);
                if (structReason != null) { Bump(StructuralBucketClass(structReason)); continue; }

                int durSecs = t.TotalTimeMs > 0 ? t.TotalTimeMs / 1000 : 0;
                bool overLimit = durSecs > input.MaxDurationSecs;
                if (overLimit) Bump("overLimit");

                // --- policy: exclusion rules. IsExcluded increments per-rule hit counts,
                // which is what makes a stale rule visible below. Determined BEFORE the
                // catalog-state block on purpose: the new-work accounting has to agree with
                // the scan's own filters, and a rule-excluded track never reaches Essentia
                // and never gets an mbxmoods.json entry (spec §10a Layering). Counting it as
                // new work reported the saving on one line while leaving it in the cost on
                // another — inverting the point of the mode.
                string exclReason;
                bool excluded = input.Exclusions.IsExcluded(loc, t.Genre, out exclReason);
                bool included = !excluded && input.Exclusions.IsIncluded(loc, t.Genre);
                if (excluded) plan.Counts.Excluded++;

                // `included` mirrors what an include rule does inside IsExcluded itself
                // (preview never mutates the track list, so there is no marker to set).
                // It still feeds CurrentDecision below — a labelled-but-included track is
                // still worth knowing about on the review surface.

                // --- catalog state ---
                TrackEntry? entry;
                bool analyzed = input.Catalog.TryGetValue(loc, out entry)
                    && (input.IsAnalyzed != null ? input.IsAnalyzed(entry) : entry?.Features != null);
                if (analyzed) { cachedTracks++; plan.Counts.Analyzed++; }
                else if (!overLimit && !excluded)
                {
                    newTracks++;
                    newBytes += Math.Max(0, t.SizeBytes);
                    newAudioMs += Math.Max(0, t.TotalTimeMs);
                }

                // --- review candidacy ---
                var reasons = new List<string>();
                if (durSecs >= input.LongTrackSecs) reasons.Add("long");
                if (overLimit) reasons.Add("over-limit");
                if (excluded) reasons.Add("excluded");
                if (t.IsSpeech) reasons.Add("speech-labelled");

                string? speech = null;
                if (analyzed && input.SpeechVerdict != null)
                {
                    speech = input.SpeechVerdict(entry!);
                    if (string.Equals(speech, "yes", StringComparison.OrdinalIgnoreCase))
                        reasons.Add("speech-likely");
                }

                if (reasons.Count == 0) continue;

                // Sniff markers only for tracks that already surfaced — bounded by the
                // candidate set, never the library.
                if (input.SniffMarkers != null)
                {
                    var marker = input.SniffMarkers(loc);
                    if (marker != null)
                    {
                        reasons.Add("marker:" + marker);
                        plan.SniffedCount++;
                    }
                    else plan.SniffedCount++;
                }

                candidates.Add(new PreviewCandidate
                {
                    Path = loc,
                    Artist = t.Artist ?? "",
                    Title = t.Name ?? "",
                    Album = t.Album ?? "",
                    Genre = t.Genre ?? "",
                    Codec = entry?.FingerprintV1?.Codec ?? "",
                    DurationSecs = durSecs,
                    State = analyzed ? "analyzed" : "new",
                    Reasons = reasons,
                    SpeechLikely = speech,
                    OverLimit = overLimit,
                    CurrentDecision = excluded ? "excluded" : included ? "included" : "undecided",
                });
            }

            foreach (var kv in buckets)
                plan.AutoSkip.Add(new PreviewBucket { Class = kv.Key, Count = kv.Value });
            plan.AutoSkip.Sort((a, b) => b.Count.CompareTo(a.Count));

            foreach (var rule in input.Exclusions.Rules)
                plan.Rules.Add(new PreviewRuleStat
                {
                    Rule = rule.Describe(),
                    Action = rule.Action == ExclusionAction.Include ? "include" : "exclude",
                    MatchCount = rule.MatchCount,
                });

            foreach (var kv in genres) plan.Genres.Add(kv.Value);
            plan.Genres.Sort((a, b) => b.Tracks.CompareTo(a.Tracks));

            // Longest first: review is triage, and a long heterogeneous file is the case
            // where an unwanted analysis does the most damage to the catalog.
            candidates.Sort((a, b) => b.DurationSecs.CompareTo(a.DurationSecs));
            plan.ReviewTotal = candidates.Count;
            plan.Counts.AwaitingReview = candidates.Count;
            if (input.ReviewCap > 0 && candidates.Count > input.ReviewCap)
            {
                plan.ReviewTruncated = true;
                plan.Review = candidates.GetRange(0, input.ReviewCap);
            }
            else plan.Review = candidates;

            plan.Estimate.NewTracks = newTracks;
            plan.Estimate.NewBytes = newBytes;
            plan.Estimate.CachedTracks = cachedTracks;
            int par = Math.Max(1, input.Parallelism);
            if (input.MeasuredRtf > 0 && newAudioMs > 0)
            {
                plan.Estimate.EtaSecs = newAudioMs / 1000.0 * input.MeasuredRtf / par;
                plan.Estimate.EtaBasis = input.EtaBasisLabel;
            }
            else
            {
                plan.Estimate.EtaSecs = -1;   // omit rather than mislead
                plan.Estimate.EtaBasis = "unavailable";
            }
            return plan;
        }
    }
}
