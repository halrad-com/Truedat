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

        /// <summary>
        /// True when <paramref name="path"/> already holds something a preview must never
        /// truncate: an .xml (the iTunes library is --preview's INPUT), or a JSON object with
        /// a top-level "tracks" property (a mood catalog). --preview's headline contract is
        /// "writes no mbxmoods.json", so overwriting one is the single worst thing this mode
        /// can do; and because every adjacent read-only mode takes its moods path
        /// positionally, naming the catalog after --preview is a plausible mistake rather
        /// than a contrived one. Tolerant-read, matching the rest of the codebase: a file
        /// that does not parse as JSON is not a catalog and may be overwritten.
        /// </summary>
        public static bool IsProtectedTarget(string path, out string reason)
        {
            reason = "";
            if (string.IsNullOrEmpty(path)) return false;
            if (!File.Exists(path)) return false;
            if (path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                reason = "it is an XML file (the iTunes library is --preview's input, not its output)";
                return true;
            }
            if (HasTopLevelTracksObject(path))
            {
                reason = "it is a mood catalog (top-level \"tracks\" property)";
                return true;
            }
            return false;
        }

        /// <summary>
        /// Bounded head scan for a top-level "tracks" property. Deliberately a fixed window
        /// rather than a JsonDocument parse of the whole file: a real mbxmoods.json runs to
        /// hundreds of MB, and it writes "tracks" as its fourth top-level property (after
        /// version / generatedAt / trackCount), so the window always reaches the answer
        /// while a full parse of the catalog would not be free. Any parse failure means
        /// "not a catalog" — the guard's job is to recognise catalogs, not to validate JSON.
        /// </summary>
        static bool HasTopLevelTracksObject(string path)
        {
            try
            {
                byte[] head;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    head = new byte[(int)Math.Min(fs.Length, 256L * 1024)];
                    int got = 0;
                    while (got < head.Length)
                    {
                        int n = fs.Read(head, got, head.Length - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    if (got < head.Length) Array.Resize(ref head, got);
                }
                int start = 0;
                if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) start = 3;
                var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(head, start, head.Length - start),
                                                isFinalBlock: false, state: default(JsonReaderState));
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
                    {
                        if (reader.ValueTextEquals("tracks")) return true;
                        continue;
                    }
                    if (reader.TokenType == JsonTokenType.StartObject || reader.TokenType == JsonTokenType.StartArray)
                    {
                        // A value bigger than the window means we cannot see the rest of the
                        // top level; stop rather than guess.
                        if (!reader.TrySkip()) return false;
                    }
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>Guarded write: refuses (and writes nothing) when the destination is
        /// something <see cref="IsProtectedTarget"/> recognises, printing which file and why.
        /// Callers turn a false return into exit 1.</summary>
        public static bool TryWritePreviewJson(string path, PreviewPlan plan)
        {
            string why;
            if (IsProtectedTarget(path, out why))
            {
                Console.Error.WriteLine($"Error: refusing to overwrite {path} — {why}.");
                Console.Error.WriteLine("  --preview writes only a preview manifest. Name a preview file, or omit the path to use the default destination.");
                return false;
            }
            WritePreviewJson(path, plan);
            return true;
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
