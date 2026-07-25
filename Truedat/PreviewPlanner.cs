using System;
using System.Collections.Generic;
using System.IO;

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
        /// <summary>Bounded header read for embedded podcast markers; null disables sniffing.</summary>
        public Func<string, string?>? SniffMarkers;
        /// <summary>Measured analysis thread-seconds per audio-second. 0 = nothing measured.</summary>
        public double MeasuredRtf;
        public int Parallelism = 1;
        /// <summary>Recomputed speech verdict for an already-analyzed entry; null disables.</summary>
        public Func<TrackEntry, string?>? SpeechVerdict;
    }

    /// <summary>
    /// Builds the read-only scan work plan. Pure computation over the parsed library, the
    /// existing catalog and the exclusion rules — it never analyzes audio and never writes
    /// anything. The one exception is a bounded header sniff over review CANDIDATES only
    /// (hundreds of files, not the whole library), which is what lets the evidence column
    /// say "marker:PCST"; the count of files sniffed is reported rather than hidden.
    ///
    /// The classification split matters and mirrors the scan's own layering: STRUCTURAL
    /// skips (missing, video, stream URL, non-audio, over the duration ceiling) are things
    /// a scan *cannot* analyze, so they are counted and never offered for review — no human
    /// decision can change them. Everything else is policy, and policy is reviewable.
    /// </summary>
    internal static class PreviewPlanner
    {
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
                var ext = Program.GetExtensionSafe(loc);
                if (Program.VideoExtensions.Contains(ext)) { Bump("video"); continue; }
                if (Program.NonAudioExtensions.Contains(ext)) { Bump("nonAudio"); continue; }

                int durSecs = t.TotalTimeMs > 0 ? t.TotalTimeMs / 1000 : 0;
                bool overLimit = durSecs > input.MaxDurationSecs;
                if (overLimit) Bump("overLimit");

                // --- catalog state ---
                TrackEntry? entry;
                bool analyzed = input.Catalog.TryGetValue(loc, out entry) && entry?.Features != null;
                if (analyzed) { cachedTracks++; plan.Counts.Analyzed++; }
                else if (!overLimit)
                {
                    newTracks++;
                    newBytes += Math.Max(0, t.SizeBytes);
                    newAudioMs += Math.Max(0, t.TotalTimeMs);
                }

                // --- policy: exclusion rules. IsExcluded increments per-rule hit counts,
                // which is what makes a stale rule visible below.
                string exclReason;
                bool excluded = input.Exclusions.IsExcluded(loc, t.Genre, out exclReason);
                bool included = !excluded && input.Exclusions.IsIncluded(loc, t.Genre);
                if (excluded) plan.Counts.Excluded++;

                // --- review candidacy ---
                var reasons = new List<string>();
                if (durSecs >= input.LongTrackSecs) reasons.Add("long");
                if (overLimit) reasons.Add("over-limit");
                if (excluded) reasons.Add("excluded");
                if (t.IsPodcast) reasons.Add("podcast-labelled");

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
                plan.Estimate.EtaBasis = "measured-rtf";
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
