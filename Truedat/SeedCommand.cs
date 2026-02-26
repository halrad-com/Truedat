using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Truedat
{
    /// <summary>
    /// Seeds mbxmoods.json from AcousticBrainz pre-computed features.
    /// Matches real library tracks against a catalog by normalized artist+title.
    /// Never downgrades existing entries — only adds new or upgrades lower-confidence data.
    /// </summary>
    internal class SeedCommand
    {
        private readonly string _catalogPath;
        private readonly string _xmlPath;
        private readonly string _moodsPath;

        // Seed confidence is 0.6 — metadata match from AcousticBrainz catalog.
        // Local Essentia analysis (no _source field, or _source: "essentia") defaults to 1.0.
        // This ensures local analysis is never overwritten by seeded data.
        private const double SeedConfidence = 0.6;
        private const string SeedSource = "ab-metadata";

        public SeedCommand(string catalogPath, string xmlPath, string moodsPath)
        {
            _catalogPath = catalogPath;
            _xmlPath = xmlPath;
            _moodsPath = moodsPath;
        }

        public int Run()
        {
            var sw = Stopwatch.StartNew();

            Console.WriteLine("=== Mood Seeding from AcousticBrainz ===");
            Console.WriteLine($"Catalog: {_catalogPath}");
            Console.WriteLine($"Library: {_xmlPath}");
            Console.WriteLine($"Target:  {_moodsPath}");
            Console.WriteLine();

            if (!File.Exists(_catalogPath))
            {
                Console.WriteLine($"ERROR: Catalog not found: {_catalogPath}");
                Console.WriteLine("Run: python src/catalog-prep.py --build");
                return 1;
            }
            if (!File.Exists(_xmlPath))
            {
                Console.WriteLine($"ERROR: Library XML not found: {_xmlPath}");
                return 1;
            }

            // 1. Build lookup index from catalog
            Console.Write("Loading catalog into lookup index...");
            var index = BuildLookupIndex();
            Console.WriteLine($" {index.Count:N0} entries");

            // 2. Parse iTunes library
            Console.Write("Parsing iTunes library...");
            var library = ITunesParser.Parse(_xmlPath, out _);
            Console.WriteLine($" {library.Count:N0} tracks");

            // 3. Load existing moods to check confidence
            var existingMoods = LoadExistingMoods();
            Console.WriteLine($"Existing moods: {existingMoods.Count:N0} entries");

            // 4. Match tracks
            int matched = 0, upgraded = 0, alreadyBetter = 0, noMatch = 0;
            var newEntries = new Dictionary<string, JsonObject>(PathComparer.Instance);

            foreach (var track in library)
            {
                string filePath = PathHelper.NormalizeSeparators(track.Location);
                if (string.IsNullOrEmpty(filePath)) continue;

                // Check existing confidence — never downgrade
                double existingConf = GetExistingConfidence(existingMoods, filePath);
                if (SeedConfidence <= existingConf)
                {
                    alreadyBetter++;
                    continue;
                }

                // Normalize and look up
                string normArtist = PathSanitizer.NormalizeForLookup(track.Artist);
                string normTitle = PathSanitizer.NormalizeForLookup(track.Name);
                string key = normArtist + "|" + normTitle;

                if (index.TryGetValue(key, out var entry))
                {
                    newEntries[filePath] = BuildMoodEntry(entry);
                    if (existingConf > 0) upgraded++;
                    else matched++;
                }
                else
                {
                    noMatch++;
                }
            }

            Console.WriteLine();
            Console.WriteLine("Results:");
            Console.WriteLine($"  New matches:     {matched:N0}");
            Console.WriteLine($"  Upgraded:        {upgraded:N0}");
            Console.WriteLine($"  Already better:  {alreadyBetter:N0}");
            Console.WriteLine($"  No match:        {noMatch:N0}");

            // 5. Write moods
            if (newEntries.Count > 0)
            {
                WriteMoods(newEntries, existingMoods);
                Console.WriteLine($"Wrote {newEntries.Count:N0} entries to {_moodsPath}");
            }
            else
            {
                Console.WriteLine("No new entries to write.");
            }

            sw.Stop();
            Console.WriteLine($"Elapsed: {sw.Elapsed.TotalSeconds:F1}s");
            return 0;
        }

        /// <summary>
        /// Build lookup dict keyed by "normalizedArtist|normalizedTitle"
        /// from the gzipped JSONL catalog.
        /// </summary>
        private Dictionary<string, CatalogEntry> BuildLookupIndex()
        {
            var index = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            using var fs = File.OpenRead(_catalogPath);
            Stream stream = _catalogPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                ? (Stream)new GZipStream(fs, CompressionMode.Decompress)
                : fs;
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<CatalogEntry>(line, options);
                    if (entry == null || string.IsNullOrEmpty(entry.Artist)
                        || string.IsNullOrEmpty(entry.Title))
                        continue;

                    // Build lookup key from pre-normalized fields if available,
                    // otherwise normalize on the fly
                    string normArtist = !string.IsNullOrEmpty(entry.NormalizedArtist)
                        ? entry.NormalizedArtist
                        : PathSanitizer.NormalizeForLookup(entry.Artist);
                    string normTitle = !string.IsNullOrEmpty(entry.NormalizedTitle)
                        ? entry.NormalizedTitle
                        : PathSanitizer.NormalizeForLookup(entry.Title);

                    string key = normArtist + "|" + normTitle;
                    // Keep first occurrence (don't overwrite)
                    if (!index.ContainsKey(key))
                        index[key] = entry;
                }
                catch (JsonException) { /* Skip malformed lines */ }
            }

            return index;
        }

        /// <summary>
        /// Load existing mbxmoods.json into a dictionary keyed by file path.
        /// Returns empty dictionary if file doesn't exist or can't be parsed.
        /// </summary>
        private Dictionary<string, JsonObject> LoadExistingMoods()
        {
            var result = new Dictionary<string, JsonObject>(PathComparer.Instance);
            if (!File.Exists(_moodsPath))
                return result;

            try
            {
                var json = File.ReadAllText(_moodsPath, Encoding.UTF8);
                var doc = JsonNode.Parse(json);
                if (doc is JsonObject root
                    && root.ContainsKey("tracks")
                    && root["tracks"] is JsonObject tracks)
                {
                    foreach (var kvp in tracks)
                    {
                        if (kvp.Value is JsonObject trackObj)
                            result[kvp.Key] = trackObj;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: could not parse existing moods file: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Get the confidence of an existing mood entry.
        /// Entries without _source or _confidence are assumed to be local Essentia analysis (confidence 1.0).
        /// Seeded entries have _confidence explicitly set (typically 0.6).
        /// Returns 0 if no entry exists.
        /// </summary>
        private static double GetExistingConfidence(Dictionary<string, JsonObject> existingMoods, string filePath)
        {
            if (!existingMoods.TryGetValue(filePath, out var entry))
                return 0;

            // Explicit _confidence field takes priority
            if (entry.TryGetPropertyValue("_confidence", out var confNode) && confNode != null)
            {
                try { return confNode.GetValue<double>(); }
                catch { }
            }

            // No _confidence field: check _source.
            // If _source is absent, this is legacy local Essentia data → confidence 1.0.
            // If _source is "essentia", also 1.0.
            if (entry.TryGetPropertyValue("_source", out var sourceNode) && sourceNode != null)
            {
                string source = sourceNode.GetValue<string>() ?? "";
                if (source == "essentia") return 1.0;
                // Unknown source without explicit confidence — treat conservatively as 0.5
                return 0.5;
            }

            // No _source, no _confidence → local Essentia analysis, highest confidence
            return 1.0;
        }

        /// <summary>
        /// Build a mood entry JsonObject from a catalog entry with all 15 features
        /// plus seed metadata fields.
        /// </summary>
        private static JsonObject BuildMoodEntry(CatalogEntry entry)
        {
            var obj = new JsonObject
            {
                ["bpm"] = entry.Bpm,
                ["key"] = entry.Key,
                ["mode"] = entry.Mode,
                ["spectralCentroid"] = entry.SpectralCentroid,
                ["spectralFlux"] = entry.SpectralFlux,
                ["loudness"] = entry.Loudness,
                ["danceability"] = entry.Danceability,
                ["onsetRate"] = entry.OnsetRate,
                ["zeroCrossingRate"] = entry.ZeroCrossingRate,
                ["spectralRms"] = entry.SpectralRms,
                ["spectralFlatness"] = entry.SpectralFlatness,
                ["dissonance"] = entry.Dissonance,
                ["pitchSalience"] = entry.PitchSalience,
                ["chordsChangesRate"] = entry.ChordsChangesRate,
                ["mfcc"] = new JsonArray(entry.Mfcc0),
                ["_source"] = SeedSource,
                ["_confidence"] = SeedConfidence,
            };

            // Include recording MBID for traceability if available
            if (!string.IsNullOrEmpty(entry.Mbid))
                obj["_seedMbid"] = entry.Mbid;

            return obj;
        }

        /// <summary>
        /// Atomic merge of new entries into existing mbxmoods.json.
        /// New entries overwrite existing entries for the same path (confidence
        /// check was already done before building newEntries).
        /// </summary>
        private void WriteMoods(Dictionary<string, JsonObject> newEntries,
                                Dictionary<string, JsonObject> existingMoods)
        {
            // Start with existing entries
            var mergedTracks = new JsonObject();
            foreach (var kvp in existingMoods)
            {
                mergedTracks[kvp.Key] = kvp.Value.DeepClone();
            }

            // Overlay new/upgraded entries
            foreach (var kvp in newEntries)
            {
                mergedTracks[kvp.Key] = kvp.Value.DeepClone();
            }

            var root = new JsonObject
            {
                ["tracks"] = mergedTracks
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = root.ToJsonString(options);

            // Atomic write: write to temp, then move
            var tmpPath = _moodsPath + ".tmp";
            File.WriteAllText(tmpPath, json, Encoding.UTF8);
            if (File.Exists(_moodsPath))
                File.Delete(_moodsPath);
            File.Move(tmpPath, _moodsPath);
        }
    }
}
