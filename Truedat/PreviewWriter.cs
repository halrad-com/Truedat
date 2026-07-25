using System;
using System.IO;
using System.Text.Json;

namespace Truedat
{
    /// <summary>
    /// Serialises a PreviewPlan to preview.json. Two things about this file are contract,
    /// not preference:
    ///
    /// 1. preview.json IS the review-surface manifest MBXHub serves via GET /review/{id} —
    ///    their GetAsset route serves only .html and no route serves an arbitrary sibling
    ///    JSON, so the plan payload has to ride inside the manifest envelope rather than
    ///    living beside it.
    /// 2. An unestimable ETA is OMITTED, never written as the -1 sentinel. A consumer that
    ///    reads -1 as a number would render "-1 seconds"; absent is unambiguous.
    /// </summary>
    internal static class PreviewWriter
    {
        public const string FileName = "preview.json";

        /// <summary>Review-folder destination, falling back beside the moods file.</summary>
        public static string ResolveDest(string moodsDir)
        {
            var rd = Program.ResolveReviewDir(moodsDir);
            if (rd != null) return Path.Combine(rd, FileName);
            Console.WriteLine("  (no MusicBee/MBXHub instance found — writing preview next to moods; pass --preview <path> to override)");
            return Path.Combine(moodsDir, "mbxmoods-preview.json");
        }

        public static void WritePreviewJson(string path, PreviewPlan plan)
        {
            // ResolveDest's own targets (the MBXHub review folder, or beside the moods
            // file) already exist by construction, but an explicit --preview <path> can
            // name a directory that doesn't — mirror the tolerant-create-first pattern
            // used elsewhere for an explicit output path rather than letting FileStream
            // surface a raw DirectoryNotFoundException.
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);

            var opts = new JsonWriterOptions { Indented = true };
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new Utf8JsonWriter(fs, opts))
            {
                w.WriteStartObject();

                // --- review-surface envelope ---
                w.WriteString("id", "preview");
                w.WriteString("kind", "preview");
                w.WriteNumber("schemaVersion", 1);
                w.WriteString("title", "Scan preview");
                w.WriteString("generated", DateTime.UtcNow.ToString("o"));
                w.WriteStartObject("source");
                w.WriteString("xmlPath", plan.XmlPath);
                w.WriteString("moodsPath", plan.MoodsPath);
                w.WriteString("exclusionsPath", plan.ExclusionsPath);
                // reviewHtml is deliberately absent until Phase 2b writes the page —
                // pointing the hub at a file that does not exist would 404 its asset route.
                w.WriteEndObject();

                w.WriteStartObject("limits");
                w.WriteNumber("maxDurationSecs", plan.Limits.MaxDurationSecs);
                w.WriteString("maxDurationSource", plan.Limits.MaxDurationSource);
                w.WriteNumber("longTrackSecs", plan.Limits.LongTrackSecs);
                w.WriteString("extractorNote", plan.Limits.ExtractorNote);
                w.WriteEndObject();

                w.WriteStartObject("counts");
                w.WriteNumber("libraryTotal", plan.Counts.LibraryTotal);
                w.WriteNumber("analyzed", plan.Counts.Analyzed);
                w.WriteNumber("excluded", plan.Counts.Excluded);
                w.WriteNumber("awaitingReview", plan.Counts.AwaitingReview);
                w.WriteEndObject();

                w.WriteStartObject("estimate");
                w.WriteNumber("newTracks", plan.Estimate.NewTracks);
                w.WriteNumber("newBytes", plan.Estimate.NewBytes);
                w.WriteNumber("cachedTracks", plan.Estimate.CachedTracks);
                if (plan.Estimate.EtaSecs >= 0) w.WriteNumber("etaSecs", Math.Round(plan.Estimate.EtaSecs, 1));
                w.WriteString("etaBasis", plan.Estimate.EtaBasis);
                w.WriteEndObject();

                w.WriteStartArray("autoSkip");
                foreach (var b in plan.AutoSkip)
                {
                    w.WriteStartObject();
                    w.WriteString("class", b.Class);
                    w.WriteNumber("count", b.Count);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartArray("rules");
                foreach (var r in plan.Rules)
                {
                    w.WriteStartObject();
                    w.WriteString("rule", r.Rule);
                    w.WriteString("action", r.Action);
                    w.WriteNumber("matchCount", r.MatchCount);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartArray("genres");
                foreach (var g in plan.Genres)
                {
                    w.WriteStartObject();
                    w.WriteString("name", g.Name);
                    w.WriteNumber("tracks", g.Tracks);
                    w.WriteNumber("totalSecs", g.TotalSecs);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteNumber("reviewTotal", plan.ReviewTotal);
                w.WriteBoolean("reviewTruncated", plan.ReviewTruncated);
                // Declared rather than left for the page to infer, so client-side paging
                // slices a list whose order it did not have to guess at.
                w.WriteString("reviewSort", "durationSecs-desc");
                w.WriteNumber("sniffedCount", plan.SniffedCount);

                w.WriteStartArray("review");
                foreach (var c in plan.Review)
                {
                    w.WriteStartObject();
                    w.WriteString("path", c.Path);
                    w.WriteString("artist", c.Artist);
                    w.WriteString("title", c.Title);
                    w.WriteString("album", c.Album);
                    w.WriteString("genre", c.Genre);
                    w.WriteString("codec", c.Codec);
                    w.WriteNumber("durationSecs", c.DurationSecs);
                    w.WriteString("state", c.State);
                    w.WriteBoolean("overLimit", c.OverLimit);
                    w.WriteString("currentDecision", c.CurrentDecision);
                    if (c.SpeechLikely != null) w.WriteString("speechLikely", c.SpeechLikely);
                    w.WriteStartArray("reasons");
                    foreach (var reason in c.Reasons) w.WriteStringValue(reason);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteEndObject();
            }
        }
    }
}
