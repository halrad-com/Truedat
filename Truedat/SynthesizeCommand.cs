using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Truedat
{
    class CatalogEntry
    {
        [JsonPropertyName("mbid")]
        public string Mbid { get; set; } = "";

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("artist")]
        public string Artist { get; set; } = "";

        [JsonPropertyName("artist_mbid")]
        public string ArtistMbid { get; set; } = "";

        [JsonPropertyName("album")]
        public string Album { get; set; } = "";

        [JsonPropertyName("album_artist")]
        public string AlbumArtist { get; set; } = "";

        [JsonPropertyName("release_mbid")]
        public string ReleaseMbid { get; set; } = "";

        [JsonPropertyName("release_group_mbid")]
        public string ReleaseGroupMbid { get; set; } = "";

        [JsonPropertyName("genre")]
        public string Genre { get; set; } = "";

        [JsonPropertyName("year")]
        public int Year { get; set; }

        [JsonPropertyName("track_no")]
        public int TrackNo { get; set; }

        [JsonPropertyName("total_tracks")]
        public int TotalTracks { get; set; }

        [JsonPropertyName("bpm")]
        public double Bpm { get; set; }

        [JsonPropertyName("key")]
        public string Key { get; set; } = "";

        [JsonPropertyName("mode")]
        public string Mode { get; set; } = "";

        [JsonPropertyName("loudness")]
        public double Loudness { get; set; }

        [JsonPropertyName("spectral_centroid")]
        public double SpectralCentroid { get; set; }

        [JsonPropertyName("spectral_flux")]
        public double SpectralFlux { get; set; }

        [JsonPropertyName("danceability")]
        public double Danceability { get; set; }

        [JsonPropertyName("onset_rate")]
        public double OnsetRate { get; set; }

        [JsonPropertyName("zero_crossing_rate")]
        public double ZeroCrossingRate { get; set; }

        [JsonPropertyName("spectral_rms")]
        public double SpectralRms { get; set; }

        [JsonPropertyName("spectral_flatness")]
        public double SpectralFlatness { get; set; }

        [JsonPropertyName("dissonance")]
        public double Dissonance { get; set; }

        [JsonPropertyName("pitch_salience")]
        public double PitchSalience { get; set; }

        [JsonPropertyName("chords_changes_rate")]
        public double ChordsChangesRate { get; set; }

        [JsonPropertyName("mfcc0")]
        public double Mfcc0 { get; set; }
    }

    class SynthTrack
    {
        public CatalogEntry Catalog = null!;
        public string OutputPath = "";
        public int AssignedTrackNo;
        public int AssignedTotalTracks;
        public string AssignedAlbum = "";
        public int Rating;
        public int PlayCount;
    }

    class AlbumGroup
    {
        public string Artist = "";
        public string Album = "";
        public List<SynthTrack> Tracks = new List<SynthTrack>();
    }

    class SynthesizeCommand
    {
        readonly string _catalogPath;
        readonly string _outputDir;
        readonly int _count;
        readonly double _albumRatio;
        readonly string? _moodsPath;
        readonly int _seed;
        readonly bool _dryRun;

        public SynthesizeCommand(string catalogPath, string outputDir, int count,
                                  double albumRatio, string? moodsPath, int seed, bool dryRun)
        {
            _catalogPath = catalogPath;
            _outputDir = outputDir;
            _count = count;
            _albumRatio = albumRatio;
            _moodsPath = moodsPath;
            _seed = seed;
            _dryRun = dryRun;
        }

        public int Run()
        {
            Console.WriteLine("=== Synthesize Library ===");
            Console.WriteLine($"  Catalog:     {_catalogPath}");
            Console.WriteLine($"  Output:      {(_dryRun ? "(dry run)" : _outputDir)}");
            Console.WriteLine($"  Count:       {_count:N0}");
            Console.WriteLine($"  Album ratio: {_albumRatio:P0}");
            Console.WriteLine($"  Moods:       {_moodsPath ?? "(none)"}");
            Console.WriteLine($"  Seed:        {_seed}");
            Console.WriteLine($"  Dry run:     {_dryRun}");
            Console.WriteLine();

            // Load catalog
            Console.WriteLine("Loading catalog...");
            var catalog = LoadCatalog();
            if (catalog.Count == 0)
            {
                Console.WriteLine("Error: catalog is empty or could not be loaded.");
                return 1;
            }
            Console.WriteLine($"  Loaded {catalog.Count:N0} entries from catalog");

            if (_count > catalog.Count)
            {
                Console.WriteLine($"  Warning: requested {_count:N0} tracks but catalog only has {catalog.Count:N0}");
            }

            int sampleSize = Math.Min(_count, catalog.Count);

            // Load existing manifest for idempotent rerun
            var manifestPath = _dryRun ? "" : Path.Combine(_outputDir, ".synthetic-manifest.json");
            var existingManifest = LoadManifest(manifestPath);
            if (existingManifest.Count > 0)
                Console.WriteLine($"  Existing manifest: {existingManifest.Count:N0} tracks");

            // Sample
            Console.WriteLine($"Sampling {sampleSize:N0} tracks (seed={_seed})...");
            var sampled = Sample(catalog, sampleSize);

            // Build album groups
            Console.WriteLine("Building album groups...");
            var (albumGroups, singles) = BuildAlbumGroups(sampled);
            int albumTrackCount = albumGroups.Sum(g => g.Tracks.Count);
            Console.WriteLine($"  Albums: {albumGroups.Count} ({albumTrackCount:N0} tracks)");
            Console.WriteLine($"  Singles: {singles.Count:N0}");

            // Assign paths, ratings, play counts
            Console.WriteLine("Assigning paths and metadata...");
            var allTracks = new List<SynthTrack>();
            var existingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AssignPaths(albumGroups, singles, allTracks, existingPaths);
            Console.WriteLine($"  Total tracks to write: {allTracks.Count:N0}");

            if (_dryRun)
            {
                PrintDryRunSummary(allTracks, albumGroups, singles);
                return 0;
            }

            // Write tracks
            Console.WriteLine();
            Console.WriteLine("Writing tracks...");
            byte[] stubBytes = LoadStubMp3();
            int written = 0;
            int skipped = 0;
            int errors = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            foreach (var track in allTracks)
            {
                if (existingManifest.Contains(track.Catalog.Mbid))
                {
                    if (File.Exists(track.OutputPath))
                    {
                        skipped++;
                        continue;
                    }
                }

                try
                {
                    WriteTrack(track, stubBytes);
                    written++;
                }
                catch (Exception ex)
                {
                    errors++;
                    if (errors <= 10)
                        Console.WriteLine($"  ERROR: {track.OutputPath}: {ex.Message}");
                    else if (errors == 11)
                        Console.WriteLine("  (suppressing further error messages)");
                }

                if ((written + skipped) % 10000 == 0)
                {
                    Console.WriteLine($"  Progress: {written + skipped:N0}/{allTracks.Count:N0} " +
                                      $"(written={written:N0}, skipped={skipped:N0}, errors={errors:N0}) " +
                                      $"[{sw.Elapsed:hh\\:mm\\:ss}]");
                }
            }

            sw.Stop();
            Console.WriteLine($"  Done: {written:N0} written, {skipped:N0} skipped, {errors:N0} errors [{sw.Elapsed:hh\\:mm\\:ss}]");

            // Write manifest (atomic)
            Console.WriteLine("Writing manifest...");
            WriteManifest(manifestPath, allTracks);

            // Write mbxmoods.json (atomic, merge if --synth-moods specified)
            Console.WriteLine("Writing mbxmoods.json...");
            WriteMoods(allTracks);

            Console.WriteLine();
            Console.WriteLine("=== Synthesize Complete ===");
            Console.WriteLine($"  Output:   {_outputDir}");
            Console.WriteLine($"  Tracks:   {allTracks.Count:N0}");
            Console.WriteLine($"  Written:  {written:N0}");
            Console.WriteLine($"  Skipped:  {skipped:N0}");
            Console.WriteLine($"  Errors:   {errors:N0}");
            Console.WriteLine($"  Duration: {sw.Elapsed:hh\\:mm\\:ss}");

            return errors > 0 ? 1 : 0;
        }

        List<CatalogEntry> LoadCatalog()
        {
            var entries = new List<CatalogEntry>();
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            };

            using var fileStream = File.OpenRead(_catalogPath);
            Stream decompressed;

            // Detect gzip by magic bytes
            if (_catalogPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                decompressed = new GZipStream(fileStream, CompressionMode.Decompress);
            }
            else
            {
                decompressed = fileStream;
            }

            using (decompressed)
            using (var reader = new StreamReader(decompressed, System.Text.Encoding.UTF8))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var entry = JsonSerializer.Deserialize<CatalogEntry>(line, options);
                        if (entry != null && !string.IsNullOrEmpty(entry.Mbid))
                            entries.Add(entry);
                    }
                    catch
                    {
                        // Skip malformed lines
                    }
                }
            }

            return entries;
        }

        HashSet<string> LoadManifest(string manifestPath)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(manifestPath) || !File.Exists(manifestPath))
                return set;

            try
            {
                var json = File.ReadAllText(manifestPath);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("tracks", out var tracksEl) &&
                    tracksEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in tracksEl.EnumerateArray())
                    {
                        if (item.TryGetProperty("mbid", out var mbidEl))
                        {
                            var mbid = mbidEl.GetString();
                            if (!string.IsNullOrEmpty(mbid))
                                set.Add(mbid!);
                        }
                    }
                }
            }
            catch
            {
                // Manifest corrupt or unreadable — start fresh
            }

            return set;
        }

        /// <summary>
        /// Fisher-Yates partial shuffle for sampling without replacement.
        /// </summary>
        List<CatalogEntry> Sample(List<CatalogEntry> catalog, int count)
        {
            var rng = new Random(_seed);
            var arr = catalog.ToArray();
            int n = Math.Min(count, arr.Length);

            for (int i = 0; i < n; i++)
            {
                int j = i + rng.Next(arr.Length - i);
                var tmp = arr[i];
                arr[i] = arr[j];
                arr[j] = tmp;
            }

            var result = new List<CatalogEntry>(n);
            for (int i = 0; i < n; i++)
                result.Add(arr[i]);
            return result;
        }

        (List<AlbumGroup> albumGroups, List<SynthTrack> singles) BuildAlbumGroups(List<CatalogEntry> entries)
        {
            int albumTarget = (int)(entries.Count * _albumRatio);

            // Group by ReleaseMbid
            var releaseGroups = new Dictionary<string, List<CatalogEntry>>(StringComparer.OrdinalIgnoreCase);
            var noRelease = new List<CatalogEntry>();

            foreach (var e in entries)
            {
                if (string.IsNullOrEmpty(e.ReleaseMbid))
                {
                    noRelease.Add(e);
                    continue;
                }

                if (!releaseGroups.TryGetValue(e.ReleaseMbid, out var list))
                {
                    list = new List<CatalogEntry>();
                    releaseGroups[e.ReleaseMbid] = list;
                }
                list.Add(e);
            }

            // Albums need 3+ tracks
            var albumGroups = new List<AlbumGroup>();
            var singleEntries = new List<CatalogEntry>(noRelease);
            int albumTracksSoFar = 0;

            foreach (var kvp in releaseGroups.OrderByDescending(k => k.Value.Count))
            {
                if (kvp.Value.Count >= 3 && albumTracksSoFar < albumTarget)
                {
                    var group = new AlbumGroup
                    {
                        Artist = kvp.Value[0].AlbumArtist != ""
                            ? kvp.Value[0].AlbumArtist
                            : kvp.Value[0].Artist,
                        Album = kvp.Value[0].Album,
                        Tracks = kvp.Value
                            .OrderBy(e => e.TrackNo)
                            .Select(e => new SynthTrack { Catalog = e })
                            .ToList()
                    };
                    albumGroups.Add(group);
                    albumTracksSoFar += group.Tracks.Count;
                }
                else
                {
                    singleEntries.AddRange(kvp.Value);
                }
            }

            var singles = singleEntries.Select(e => new SynthTrack { Catalog = e }).ToList();
            return (albumGroups, singles);
        }

        void AssignPaths(List<AlbumGroup> albumGroups, List<SynthTrack> singles,
                         List<SynthTrack> allTracks, HashSet<string> existingPaths)
        {
            var rng = new Random(_seed + 1); // Separate RNG for ratings/playcounts

            // Album tracks
            foreach (var group in albumGroups)
            {
                for (int i = 0; i < group.Tracks.Count; i++)
                {
                    var track = group.Tracks[i];
                    track.AssignedAlbum = group.Album;
                    track.AssignedTrackNo = track.Catalog.TrackNo > 0 ? track.Catalog.TrackNo : i + 1;
                    track.AssignedTotalTracks = track.Catalog.TotalTracks > 0
                        ? track.Catalog.TotalTracks
                        : group.Tracks.Count;

                    track.OutputPath = PathSanitizer.BuildTrackPath(
                        _outputDir, group.Artist, group.Album,
                        track.AssignedTrackNo, track.Catalog.Title, existingPaths);

                    AssignRatingAndPlayCount(track, rng);
                    allTracks.Add(track);
                }
            }

            // Singles — album name = "Singles" under the artist
            foreach (var track in singles)
            {
                track.AssignedAlbum = "Singles";
                track.AssignedTrackNo = 1;
                track.AssignedTotalTracks = 1;

                string artist = !string.IsNullOrEmpty(track.Catalog.AlbumArtist)
                    ? track.Catalog.AlbumArtist
                    : track.Catalog.Artist;

                track.OutputPath = PathSanitizer.BuildTrackPath(
                    _outputDir, artist, "Singles",
                    track.AssignedTrackNo, track.Catalog.Title, existingPaths);

                AssignRatingAndPlayCount(track, rng);
                allTracks.Add(track);
            }
        }

        /// <summary>
        /// Rating distribution: 40% unrated, 15% 1-star, 10% 2-star, 15% 3-star, 12% 4-star, 8% 5-star.
        /// Play count distribution: 60% zero, 20% 1-5, 10% 6-20, 7% 21-100, 3% 100+.
        /// </summary>
        void AssignRatingAndPlayCount(SynthTrack track, Random rng)
        {
            // Rating (MusicBee uses 0-100 scale: 0=unrated, 20=1star, 40=2star, 60=3star, 80=4star, 100=5star)
            double r = rng.NextDouble();
            if (r < 0.40) track.Rating = 0;
            else if (r < 0.55) track.Rating = 20;
            else if (r < 0.65) track.Rating = 40;
            else if (r < 0.80) track.Rating = 60;
            else if (r < 0.92) track.Rating = 80;
            else track.Rating = 100;

            // Play count
            double p = rng.NextDouble();
            if (p < 0.60) track.PlayCount = 0;
            else if (p < 0.80) track.PlayCount = rng.Next(1, 6);
            else if (p < 0.90) track.PlayCount = rng.Next(6, 21);
            else if (p < 0.97) track.PlayCount = rng.Next(21, 101);
            else track.PlayCount = rng.Next(101, 501);
        }

        byte[] LoadStubMp3()
        {
            var asm = Assembly.GetExecutingAssembly();
            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("stub.mp3", StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
                throw new InvalidOperationException("Embedded stub.mp3 resource not found");

            using var stream = asm.GetManifestResourceStream(resourceName)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        void WriteTrack(SynthTrack track, byte[] stubBytes)
        {
            var dir = Path.GetDirectoryName(track.OutputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllBytes(track.OutputPath, stubBytes);

            using var file = TagLib.File.Create(track.OutputPath);
            file.Tag.Title = track.Catalog.Title;
            file.Tag.Performers = new[] { track.Catalog.Artist };
            file.Tag.AlbumArtists = new[] { !string.IsNullOrEmpty(track.Catalog.AlbumArtist)
                ? track.Catalog.AlbumArtist
                : track.Catalog.Artist };
            file.Tag.Album = track.AssignedAlbum;
            file.Tag.Genres = new[] { track.Catalog.Genre };
            file.Tag.Year = (uint)Math.Max(0, track.Catalog.Year);
            file.Tag.Track = (uint)Math.Max(0, track.AssignedTrackNo);
            file.Tag.TrackCount = (uint)Math.Max(0, track.AssignedTotalTracks);
            file.Tag.Disc = 1;
            file.Tag.DiscCount = 1;
            file.Tag.BeatsPerMinute = (uint)Math.Round(track.Catalog.Bpm);
            file.Tag.Grouping = "Synthetic";
            file.Tag.Comment = $"SYNTH:{_seed}:{track.Catalog.Mbid}";
            file.Save();
        }

        void WriteManifest(string manifestPath, List<SynthTrack> allTracks)
        {
            var manifest = new JsonObject
            {
                ["seed"] = _seed,
                ["count"] = allTracks.Count,
                ["albumRatio"] = _albumRatio,
                ["catalog"] = _catalogPath,
                ["generatedUtc"] = DateTime.UtcNow.ToString("o"),
                ["tracks"] = new JsonArray(
                    allTracks.Select(t => (JsonNode)new JsonObject
                    {
                        ["mbid"] = t.Catalog.Mbid,
                        ["path"] = t.OutputPath,
                        ["rating"] = t.Rating,
                        ["playCount"] = t.PlayCount,
                    }).ToArray()
                )
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = manifest.ToJsonString(options);

            // Atomic write: write to temp, then move
            var tmpPath = manifestPath + ".tmp";
            File.WriteAllText(tmpPath, json, System.Text.Encoding.UTF8);
            if (File.Exists(manifestPath))
                File.Delete(manifestPath);
            File.Move(tmpPath, manifestPath);
        }

        void WriteMoods(List<SynthTrack> allTracks)
        {
            var moodsOutputPath = !string.IsNullOrEmpty(_moodsPath)
                ? _moodsPath
                : Path.Combine(_outputDir, "mbxmoods.json");

            // Build moods data — keyed by output path
            var moodsDict = new JsonObject();

            // If merging with existing moods file, load it first
            if (!string.IsNullOrEmpty(_moodsPath) && File.Exists(_moodsPath))
            {
                try
                {
                    var existingJson = File.ReadAllText(_moodsPath);
                    var existingDoc = JsonNode.Parse(existingJson);
                    if (existingDoc is JsonObject existingObj &&
                        existingObj.ContainsKey("tracks") &&
                        existingObj["tracks"] is JsonObject existingTracks)
                    {
                        foreach (var kvp in existingTracks)
                        {
                            moodsDict[kvp.Key] = kvp.Value?.DeepClone();
                        }
                    }
                }
                catch
                {
                    Console.WriteLine("  Warning: could not parse existing moods file, starting fresh");
                }
            }

            foreach (var track in allTracks)
            {
                var c = track.Catalog;
                var normalizedPath = PathHelper.NormalizeSeparators(track.OutputPath);
                var obj = new JsonObject
                {
                    ["bpm"] = c.Bpm,
                    ["key"] = c.Key,
                    ["mode"] = c.Mode,
                    ["spectralCentroid"] = c.SpectralCentroid,
                    ["spectralFlux"] = c.SpectralFlux,
                    ["loudness"] = c.Loudness,
                    ["danceability"] = c.Danceability,
                    ["onsetRate"] = c.OnsetRate,
                    ["zeroCrossingRate"] = c.ZeroCrossingRate,
                    ["spectralRms"] = c.SpectralRms,
                    ["spectralFlatness"] = c.SpectralFlatness,
                    ["dissonance"] = c.Dissonance,
                    ["pitchSalience"] = c.PitchSalience,
                    ["chordsChangesRate"] = c.ChordsChangesRate,
                    ["mfcc"] = new JsonArray(c.Mfcc0),
                };
                moodsDict[normalizedPath] = obj;
            }

            var root = new JsonObject
            {
                ["tracks"] = moodsDict
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = root.ToJsonString(options);

            // Atomic write
            var tmpPath = moodsOutputPath + ".tmp";
            File.WriteAllText(tmpPath, json, System.Text.Encoding.UTF8);
            if (File.Exists(moodsOutputPath))
                File.Delete(moodsOutputPath);
            File.Move(tmpPath, moodsOutputPath);

            Console.WriteLine($"  Wrote {allTracks.Count:N0} mood entries to {moodsOutputPath}");
        }

        void PrintDryRunSummary(List<SynthTrack> allTracks, List<AlbumGroup> albumGroups, List<SynthTrack> singles)
        {
            Console.WriteLine();
            Console.WriteLine("=== Dry Run Summary ===");
            Console.WriteLine($"  Total tracks:  {allTracks.Count:N0}");
            Console.WriteLine($"  Album groups:  {albumGroups.Count:N0} ({albumGroups.Sum(g => g.Tracks.Count):N0} tracks)");
            Console.WriteLine($"  Singles:       {singles.Count:N0}");
            Console.WriteLine();

            // Rating distribution
            var ratingGroups = allTracks.GroupBy(t => t.Rating).OrderBy(g => g.Key);
            Console.WriteLine("  Rating distribution:");
            foreach (var g in ratingGroups)
            {
                string label = g.Key == 0 ? "Unrated" : $"{g.Key / 20}-star";
                double pct = 100.0 * g.Count() / allTracks.Count;
                Console.WriteLine($"    {label,-10} {g.Count(),8:N0}  ({pct:F1}%)");
            }
            Console.WriteLine();

            // Play count distribution
            var pcGroups = new[] { (0, 0), (1, 5), (6, 20), (21, 100), (101, int.MaxValue) };
            Console.WriteLine("  Play count distribution:");
            foreach (var (lo, hi) in pcGroups)
            {
                int count = allTracks.Count(t => t.PlayCount >= lo && t.PlayCount <= hi);
                string label = hi == int.MaxValue ? $"{lo}+" : lo == hi ? $"{lo}" : $"{lo}-{hi}";
                double pct = 100.0 * count / allTracks.Count;
                Console.WriteLine($"    {label,-10} {count,8:N0}  ({pct:F1}%)");
            }
            Console.WriteLine();

            // Genre distribution (top 15)
            var genreGroups = allTracks
                .GroupBy(t => string.IsNullOrEmpty(t.Catalog.Genre) ? "(none)" : t.Catalog.Genre)
                .OrderByDescending(g => g.Count())
                .Take(15);
            Console.WriteLine("  Top genres:");
            foreach (var g in genreGroups)
            {
                double pct = 100.0 * g.Count() / allTracks.Count;
                Console.WriteLine($"    {g.Key,-30} {g.Count(),8:N0}  ({pct:F1}%)");
            }
            Console.WriteLine();

            // Sample paths (first 10)
            Console.WriteLine("  Sample paths (first 10):");
            foreach (var track in allTracks.Take(10))
            {
                Console.WriteLine($"    {track.OutputPath}");
            }

            if (allTracks.Count > 10)
                Console.WriteLine($"    ... and {allTracks.Count - 10:N0} more");
        }
    }
}
