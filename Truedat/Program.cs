using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Truedat
{
    public class TrackFeatures
    {
        public int TrackId { get; set; }
        public string Artist { get; set; } = "";
        public string Title { get; set; } = "";
        public string Album { get; set; } = "";
        public string Genre { get; set; } = "";
        public string FilePath { get; set; } = "";

        public double Bpm { get; set; }
        public string Key { get; set; } = "";
        public string Mode { get; set; } = "";

        // Raw Essentia features (MBXHub computes valence/arousal from these with tunable weights)
        public double SpectralCentroid { get; set; }
        public double SpectralFlux { get; set; }
        public double Loudness { get; set; }
        public double Danceability { get; set; }

        // Arousal features
        public double OnsetRate { get; set; }
        public double ZeroCrossingRate { get; set; }
        public double SpectralRms { get; set; }

        // Valence features
        public double SpectralFlatness { get; set; }
        public double Dissonance { get; set; }
        public double PitchSalience { get; set; }
        public double ChordsChangesRate { get; set; }
        public double[] Mfcc { get; set; }
    }

    class Program
    {
        static void Main(string[] args)
        {
            var parallelism = Environment.ProcessorCount;
            string? xmlPath = null;
            bool fixupMode = false;
            bool retryErrors = false;
            bool migrateMode = false;

            // Parse arguments
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--fixup")
                {
                    fixupMode = true;
                }
                else if (args[i] == "--retry-errors")
                {
                    retryErrors = true;
                }
                else if (args[i] == "--migrate")
                {
                    migrateMode = true;
                }
                else if ((args[i] == "-p" || args[i] == "--parallel") && i + 1 < args.Length && int.TryParse(args[i + 1], out var p) && p > 0)
                {
                    parallelism = p;
                    i++; // Skip the number
                }
                else if (!args[i].StartsWith("-") && xmlPath == null)
                {
                    xmlPath = args[i];
                }
            }

            xmlPath ??= "iTunes Music Library.xml";

            if (!File.Exists(xmlPath))
            {
                Console.WriteLine($"iTunes library not found: {xmlPath}");
                Console.WriteLine("Usage: truedat.exe <path-to-iTunes-Music-Library.xml> [options]");
                Console.WriteLine();
                Console.WriteLine("Options:");
                Console.WriteLine($"  -p, --parallel    Number of parallel threads (default: {Environment.ProcessorCount})");
                Console.WriteLine("  --fixup           Validate and remap paths in mbxmoods.json without re-analyzing");
                Console.WriteLine("  --retry-errors    Re-attempt all previously failed files (clears error log)");
                Console.WriteLine("  --migrate         Strip legacy valence/arousal fields from mbxmoods.json (creates backup)");
                return;
            }

            // Output files go next to the input XML file
            var outputDir = Path.GetDirectoryName(Path.GetFullPath(xmlPath)) ?? ".";
            var moodsPath = Path.Combine(outputDir, "mbxmoods.json");

            // Migrate mode: strip legacy valence/arousal fields
            if (migrateMode)
            {
                RunMigrate(moodsPath);
                return;
            }

            // Fixup mode: remap paths without Essentia analysis
            if (fixupMode)
            {
                RunFixup(xmlPath, moodsPath);
                return;
            }

            // Look for Essentia extractor
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var essentiaExe = Path.Combine(baseDir, "essentia_streaming_extractor_music.exe");

            if (!File.Exists(essentiaExe))
            {
                Console.WriteLine($"Essentia extractor not found: {essentiaExe}");
                Console.WriteLine("Download from: https://essentia.upf.edu/extractors/");
                return;
            }

            var errorsPath = Path.Combine(outputDir, "mbxmoods-errors.csv");

            Console.WriteLine($"Loading iTunes library: {xmlPath}");
            var tracks = ITunesParser.Parse(xmlPath);
            Console.WriteLine($"Found {tracks.Count} tracks");

            // Load existing moods file as cache
            var existingMoods = LoadExistingMoods(moodsPath);
            Console.WriteLine($"Existing moods: {existingMoods.Count}");

            // Load existing errors to skip known failures (--retry-errors to re-attempt)
            Dictionary<string, string> existingErrors;
            if (retryErrors)
            {
                existingErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(errorsPath))
                {
                    File.Delete(errorsPath);
                    Console.WriteLine("Errors CSV cleared (--retry-errors)");
                }
            }
            else
            {
                existingErrors = LoadExistingErrors(errorsPath);
            }
            Console.WriteLine($"Existing errors: {existingErrors.Count}");

            var results = new ConcurrentBag<(TrackFeatures Features, DateTime LastModified)>();
            var errors = new ConcurrentBag<(string FilePath, string Artist, string Title, string Error)>();
            int cachedCount = 0;
            int analyzed = 0;
            int skipped = 0;
            int failed = 0;
            int processed = 0;
            int total = tracks.Count;
            int lastSaveCount = 0;
            const int SaveInterval = 5;
            var saveLock = new object();

            var startTime = DateTime.Now;
            var sw = Stopwatch.StartNew();

            // Ctrl+C graceful shutdown
            var cts = new CancellationTokenSource();
            var cancelRequested = 0; // 0 = running, 1 = cancel requested
            Console.CancelKeyPress += (_, e) =>
            {
                if (Interlocked.Exchange(ref cancelRequested, 1) == 0)
                {
                    e.Cancel = true; // Don't kill the process immediately
                    Console.WriteLine();
                    Console.WriteLine("Ctrl+C received - finishing current tracks and saving...");
                    cts.Cancel();
                }
                else
                {
                    // Second Ctrl+C - force exit
                    Console.WriteLine("Force exit.");
                }
            };

            Console.WriteLine($"Started:     {startTime:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"Parallelism: {parallelism} threads");
            Console.WriteLine();
            try
            {
                Parallel.ForEach(tracks, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cts.Token }, t =>
                {
                    var current = Interlocked.Increment(ref processed);

                    try
                    {
                        var pct = (current * 100) / total;
                        var eta = FormatEta(sw.Elapsed, current, total);

                        // Skip files that failed in a previous run
                        if (existingErrors.TryGetValue(t.Location, out var prevError))
                        {
                            Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name} (skip: {prevError})");
                            Interlocked.Increment(ref skipped);
                            return;
                        }

                        // Check if already in moods file and file unchanged
                        if (existingMoods.TryGetValue(t.Location, out var cached) && cached.Features != null)
                        {
                            // Check if file has been modified
                            // Truncate to seconds for comparison - older moods files lack sub-second precision
                            try
                            {
                                var cachedLastMod = File.GetLastWriteTimeUtc(t.Location);
                                var fileSeconds = new DateTime(cachedLastMod.Ticks - cachedLastMod.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
                                var jsonSeconds = new DateTime(cached.LastModified.Ticks - cached.LastModified.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
                                if (fileSeconds == jsonSeconds)
                                {
                                    var cachedFeat = cached.Features;
                                    cachedFeat.TrackId = t.TrackId;
                                    cachedFeat.Artist = t.Artist;
                                    cachedFeat.Title = t.Name;
                                    cachedFeat.Album = t.Album;
                                    cachedFeat.Genre = t.Genre;
                                    cachedFeat.FilePath = t.Location;

                                    results.Add((cachedFeat, cachedLastMod));
                                    Interlocked.Increment(ref cachedCount);
                                    Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name} (cached)");
                                    return;
                                }
                            }
                            catch { }
                        }

                        // Log file size for large files (helps diagnose OOM failures)
                        var sizeTag = "";
                        try
                        {
                            var sizeMb = new FileInfo(t.Location).Length / (1024.0 * 1024.0);
                            if (sizeMb >= 100) sizeTag = $" [{sizeMb:F0} MB]";
                        }
                        catch { }
                        Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name}{sizeTag}");

                        var analyzeStart = Stopwatch.GetTimestamp();
                        var (feat, errorReason) = AnalyzeWithEssentia(essentiaExe, t.Location);
                        var analyzeTicks = Stopwatch.GetTimestamp() - analyzeStart;
                        Interlocked.Add(ref _analyzeTicksTotal, analyzeTicks);
                        Interlocked.Increment(ref _analyzeCount);

                        if (feat == null)
                        {
                            var err = errorReason ?? "Unknown error";
                            errors.Add((t.Location, t.Artist, t.Name, err));
                            AppendError(errorsPath, t.Location, t.Artist, t.Name, err, saveLock);
                            Console.WriteLine($"  FAILED: {err}");
                            Interlocked.Increment(ref failed);
                            return;
                        }

                        feat.TrackId = t.TrackId;
                        feat.Artist = t.Artist;
                        feat.Title = t.Name;
                        feat.Album = t.Album;
                        feat.Genre = t.Genre;
                        feat.FilePath = t.Location;

                        var lastMod = DateTime.MinValue;
                        try { lastMod = File.GetLastWriteTimeUtc(t.Location); } catch { }
                        results.Add((feat, lastMod));
                        var newAnalyzed = Interlocked.Increment(ref analyzed);

                        // Incremental save every N analyzed tracks
                        if (newAnalyzed - lastSaveCount >= SaveInterval)
                        {
                            lock (saveLock)
                            {
                                if (newAnalyzed - lastSaveCount >= SaveInterval)
                                {
                                    lastSaveCount = newAnalyzed;
                                    SaveResults(moodsPath, results);
                                    Console.WriteLine($"  [Saved {results.Count} tracks]");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error: {t.Artist} - {t.Name}: {ex.Message}");
                        errors.Add((t.Location, t.Artist, t.Name, ex.Message));
                        AppendError(errorsPath, t.Location, t.Artist, t.Name, ex.Message, saveLock);
                        Interlocked.Increment(ref failed);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C - fall through to final save
            }

            sw.Stop();
            var endTime = DateTime.Now;
            var wasCancelled = Volatile.Read(ref cancelRequested) != 0;

            // Final save
            SaveResults(moodsPath, results);

            Console.WriteLine();
            if (wasCancelled)
                Console.WriteLine("=== Interrupted (Ctrl+C) - progress saved ===");
            Console.WriteLine($"Started:    {startTime:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"Finished:   {endTime:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"Elapsed:    {FormatTimeSpan(sw.Elapsed)}");
            Console.WriteLine();
            Console.WriteLine($"  Cached:     {cachedCount}");
            Console.WriteLine($"  Analyzed:   {analyzed}");
            Console.WriteLine($"  Skipped:    {skipped}  (errors from previous run)");
            Console.WriteLine($"  Failed:     {failed}");
            Console.WriteLine($"  ──────────  ─────");
            Console.WriteLine($"  Processed:  {cachedCount + analyzed + skipped + failed}");
            Console.WriteLine($"  Output:     {results.Count} tracks in moods file");
            if (analyzed > 0)
            {
                var avgAnalyze = TimeSpan.FromTicks(_analyzeTicksTotal / analyzed);
                Console.WriteLine($"  Avg/track:  {avgAnalyze.TotalSeconds:F1}s (analysis only)");
            }
            Console.WriteLine();
            Console.WriteLine($"Output: {moodsPath}");
        }

        /// <summary>
        /// Validate and remap paths in mbxmoods.json against the current iTunes library.
        /// Matches by filename (primary key) with strict metadata verification.
        /// Does not require Essentia - preserves existing analysis data.
        /// </summary>
        static void RunFixup(string xmlPath, string moodsPath)
        {
            Console.WriteLine("=== Fixup Mode ===");
            Console.WriteLine();

            if (!File.Exists(moodsPath))
            {
                Console.WriteLine($"No moods file found: {moodsPath}");
                return;
            }

            // Load moods file as JObject to preserve all fields
            Console.WriteLine($"Loading moods: {moodsPath}");
            var json = File.ReadAllText(moodsPath);
            var root = JObject.Parse(json);
            var tracks = root["tracks"] as JObject;
            if (tracks == null || tracks.Count == 0)
            {
                Console.WriteLine("No tracks in moods file.");
                return;
            }
            Console.WriteLine($"Moods entries: {tracks.Count}");

            // Load iTunes library for current paths
            Console.WriteLine($"Loading iTunes library: {xmlPath}");
            var library = ITunesParser.Parse(xmlPath);
            Console.WriteLine($"Library tracks: {library.Count}");

            // Build filename → [ITunesTrack] lookup (case-insensitive)
            var byFilename = new Dictionary<string, List<ITunesTrack>>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in library)
            {
                var filename = Path.GetFileName(t.Location);
                if (string.IsNullOrEmpty(filename)) continue;
                if (!byFilename.TryGetValue(filename, out var list))
                {
                    list = new List<ITunesTrack>();
                    byFilename[filename] = list;
                }
                list.Add(t);
            }

            // Walk moods entries and classify
            int unchanged = 0;
            int remapped = 0;
            int orphaned = 0;
            var newTracks = new JObject();
            var orphanedEntries = new List<(string OldPath, string Artist, string Title)>();

            foreach (var prop in tracks.Properties().ToList())
            {
                var oldPath = prop.Name;
                var trackData = prop.Value as JObject;
                if (trackData == null) continue;

                // Normalize path for file existence check (old files may have forward slashes)
                var normalizedOldPath = oldPath.Replace('/', '\\');

                // 1. Path still valid?
                if (File.Exists(normalizedOldPath))
                {
                    newTracks[normalizedOldPath] = trackData;
                    unchanged++;
                    continue;
                }

                // 2. Try to find by filename + strict metadata match
                var filename = Path.GetFileName(normalizedOldPath);
                var moodArtist = trackData["artist"]?.Value<string>() ?? "";
                var moodTitle = trackData["title"]?.Value<string>() ?? "";
                var moodAlbum = trackData["album"]?.Value<string>() ?? "";
                var moodGenre = trackData["genre"]?.Value<string>() ?? "";

                ITunesTrack? match = null;

                if (!string.IsNullOrEmpty(filename) && byFilename.TryGetValue(filename, out var candidates))
                {
                    var strictMatches = candidates.Where(c =>
                        string.Equals(c.Artist, moodArtist, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(c.Name, moodTitle, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(c.Album, moodAlbum, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(c.Genre, moodGenre, StringComparison.OrdinalIgnoreCase)
                    ).ToList();

                    if (strictMatches.Count == 1)
                    {
                        match = strictMatches[0];
                    }
                    else if (strictMatches.Count > 1)
                    {
                        // Multiple strict matches (same file in multiple locations) - skip, ambiguous
                        Console.WriteLine($"  AMBIGUOUS ({strictMatches.Count} matches): {moodArtist} - {moodTitle}");
                    }
                }

                if (match != null)
                {
                    // Remap: preserve all mood data, update path key and metadata
                    var newPath = match.Location;
                    trackData["trackId"] = match.TrackId;
                    newTracks[newPath] = trackData;
                    remapped++;
                    Console.WriteLine($"  REMAP: {moodArtist} - {moodTitle}");
                    Console.WriteLine($"    {oldPath}");
                    Console.WriteLine($"    -> {newPath}");
                }
                else
                {
                    orphaned++;
                    orphanedEntries.Add((oldPath, moodArtist, moodTitle));
                }
            }

            // Count unanalyzed (in library but not in moods)
            var moodPaths = new HashSet<string>(
                newTracks.Properties().Select(p => p.Name),
                StringComparer.OrdinalIgnoreCase);
            int unanalyzed = library.Count(t => !moodPaths.Contains(t.Location));

            // Report
            Console.WriteLine();
            Console.WriteLine("=== Results ===");
            Console.WriteLine($"  Unchanged:   {unchanged}");
            Console.WriteLine($"  Remapped:    {remapped}");
            Console.WriteLine($"  Orphaned:    {orphaned}");
            Console.WriteLine($"  Unanalyzed:  {unanalyzed} (in library, no mood data)");
            Console.WriteLine($"  Total out:   {newTracks.Count}");

            if (orphanedEntries.Count > 0 && orphanedEntries.Count <= 20)
            {
                Console.WriteLine();
                Console.WriteLine("Orphaned entries (no match found):");
                foreach (var (path, artist, title) in orphanedEntries)
                    Console.WriteLine($"  {artist} - {title}: {path}");
            }
            else if (orphanedEntries.Count > 20)
            {
                Console.WriteLine();
                Console.WriteLine($"Orphaned entries ({orphanedEntries.Count} total, showing first 20):");
                foreach (var (path, artist, title) in orphanedEntries.Take(20))
                    Console.WriteLine($"  {artist} - {title}: {path}");
                Console.WriteLine($"  ... and {orphanedEntries.Count - 20} more");
            }

            // Write updated file (only if something changed)
            if (remapped > 0 || orphaned > 0)
            {
                // Create backup before modifying
                var timestamp = DateTime.Now.ToString("yyyyMMdd.HHmmss");
                var bakPath = $"{moodsPath}.bak.{timestamp}";
                File.Copy(moodsPath, bakPath);
                Console.WriteLine();
                Console.WriteLine($"Backup: {bakPath}");

                root["tracks"] = newTracks;
                root["trackCount"] = newTracks.Count;
                root["generatedAt"] = DateTime.UtcNow.ToString("o");
                var tmpPath = moodsPath + ".tmp";
                File.WriteAllText(tmpPath, root.ToString(Formatting.Indented));
                File.Delete(moodsPath);
                File.Move(tmpPath, moodsPath);
                Console.WriteLine($"Updated: {moodsPath}");
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("All paths valid, no changes needed.");
            }
        }

        /// <summary>
        /// Strip legacy valence/arousal fields from mbxmoods.json.
        /// These are now computed by MBXHub from raw features at load time.
        /// Creates a timestamped backup before modifying.
        /// </summary>
        static void RunMigrate(string moodsPath)
        {
            Console.WriteLine("=== Migrate Mode ===");
            Console.WriteLine("Strips legacy valence/arousal fields from mbxmoods.json");
            Console.WriteLine();

            if (!File.Exists(moodsPath))
            {
                Console.WriteLine($"No moods file found: {moodsPath}");
                return;
            }

            Console.WriteLine($"Loading: {moodsPath}");
            var json = File.ReadAllText(moodsPath);
            var root = JObject.Parse(json);
            var tracks = root["tracks"] as JObject;
            if (tracks == null || tracks.Count == 0)
            {
                Console.WriteLine("No tracks in moods file.");
                return;
            }

            int stripped = 0;
            int total = tracks.Count;

            foreach (var prop in tracks.Properties())
            {
                var trackData = prop.Value as JObject;
                if (trackData == null) continue;

                bool changed = false;
                if (trackData.Remove("valence")) changed = true;
                if (trackData.Remove("arousal")) changed = true;
                if (changed) stripped++;
            }

            Console.WriteLine($"Tracks: {total}");
            Console.WriteLine($"Stripped valence/arousal from: {stripped}");

            if (stripped == 0)
            {
                Console.WriteLine();
                Console.WriteLine("No legacy fields found, nothing to do.");
                return;
            }

            // Create backup
            var timestamp = DateTime.Now.ToString("yyyyMMdd.HHmmss");
            var bakPath = $"{moodsPath}.bak.{timestamp}";
            File.Copy(moodsPath, bakPath);
            Console.WriteLine();
            Console.WriteLine($"Backup: {bakPath}");

            // Write updated file
            root["generatedAt"] = DateTime.UtcNow.ToString("o");
            var tmpPath = moodsPath + ".tmp";
            File.WriteAllText(tmpPath, root.ToString(Formatting.Indented));
            File.Delete(moodsPath);
            File.Move(tmpPath, moodsPath);
            Console.WriteLine($"Updated: {moodsPath}");
        }

        static Dictionary<string, string> LoadExistingErrors(string path)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path))
                return result;

            try
            {
                var lines = File.ReadAllLines(path);
                foreach (var line in lines.Skip(1)) // Skip header
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    // CSV format: Error,Artist,Title,FilePath
                    var parts = ParseCsvLine(line);
                    if (parts.Length >= 4)
                    {
                        var error = parts[0];
                        var filePath = parts[3];
                        result[filePath] = error;
                    }
                }
            }
            catch { }

            return result;
        }

        static string[] ParseCsvLine(string line)
        {
            var result = new List<string>();
            var current = "";
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current += '"';
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    result.Add(current);
                    current = "";
                }
                else
                {
                    current += c;
                }
            }
            result.Add(current);
            return result.ToArray();
        }

        static void AppendError(string errorsPath, string filePath, string artist, string title, string error, object lockObj)
        {
            lock (lockObj)
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        bool needsHeader = !File.Exists(errorsPath) || new FileInfo(errorsPath).Length == 0;
                        using (var fs = new FileStream(errorsPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                        using (var writer = new StreamWriter(fs))
                        {
                            if (needsHeader)
                                writer.WriteLine("Error,Artist,Title,FilePath");
                            writer.WriteLine($"{CsvEscape(error)},{CsvEscape(artist)},{CsvEscape(title)},{CsvEscape(filePath)}");
                        }
                        return;
                    }
                    catch (IOException) when (attempt < 4)
                    {
                        Thread.Sleep(200 * (attempt + 1));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  Warning: Could not write to errors CSV: {ex.Message}");
                        return;
                    }
                }
            }
        }

        static void SaveResults(string moodsPath, ConcurrentBag<(TrackFeatures Features, DateTime LastModified)> results)
        {
            // Merge with existing file — never overwrite a larger dataset with a smaller one
            var merged = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            // Load existing tracks first
            if (File.Exists(moodsPath))
            {
                try
                {
                    var existingJson = File.ReadAllText(moodsPath);
                    var existingRoot = JObject.Parse(existingJson);
                    var existingTracks = existingRoot["tracks"] as JObject;
                    if (existingTracks != null)
                    {
                        foreach (var prop in existingTracks.Properties())
                        {
                            // Normalize old forward-slash keys for backward compat
                            var key = prop.Name.Replace('/', '\\');
                            merged[key] = prop.Value;
                        }
                    }
                }
                catch (JsonReaderException ex)
                {
                    // Existing file is corrupt - back it up, don't lose it
                    var bakPath = moodsPath + $".corrupt.{DateTime.Now:yyyyMMdd.HHmmss}";
                    try { File.Copy(moodsPath, bakPath); } catch { }
                    Console.WriteLine($"  WARNING: Existing moods file is corrupt ({ex.Message})");
                    Console.WriteLine($"  Backup saved to: {bakPath}");
                    Console.WriteLine($"  Starting fresh merge from current session data only.");
                }
            }

            // Overlay current results (new/updated data wins)
            foreach (var (r, lastMod) in results)
            {
                merged[r.FilePath] = JObject.FromObject(new
                {
                    trackId = r.TrackId,
                    artist = r.Artist,
                    title = r.Title,
                    album = r.Album,
                    genre = r.Genre,
                    bpm = r.Bpm,
                    key = r.Key,
                    mode = r.Mode,
                    spectralCentroid = r.SpectralCentroid,
                    spectralFlux = r.SpectralFlux,
                    loudness = r.Loudness,
                    danceability = r.Danceability,
                    onsetRate = r.OnsetRate,
                    zeroCrossingRate = r.ZeroCrossingRate,
                    spectralRms = r.SpectralRms,
                    spectralFlatness = r.SpectralFlatness,
                    dissonance = r.Dissonance,
                    pitchSalience = r.PitchSalience,
                    chordsChangesRate = r.ChordsChangesRate,
                    mfcc = r.Mfcc,
                    lastModified = lastMod.ToString("o")
                });
            }

            var output = new JObject
            {
                ["version"] = "1.0",
                ["generatedAt"] = DateTime.UtcNow.ToString("o"),
                ["trackCount"] = merged.Count,
                ["tracks"] = JObject.FromObject(merged)
            };

            // Atomic write: write to temp file, then rename
            var tmpPath = moodsPath + ".tmp";
            File.WriteAllText(tmpPath, output.ToString(Formatting.Indented));
            if (File.Exists(moodsPath))
                File.Delete(moodsPath);
            File.Move(tmpPath, moodsPath);
        }

        static Dictionary<string, (DateTime LastModified, TrackFeatures Features)> LoadExistingMoods(string path)
        {
            var result = new Dictionary<string, (DateTime, TrackFeatures)>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path))
                return result;

            try
            {
                var json = File.ReadAllText(path);
                var root = JObject.Parse(json);
                var tracks = root["tracks"] as JObject;
                if (tracks == null)
                    return result;

                foreach (var prop in tracks.Properties())
                {
                    // Normalize path separators for consistent lookup
                    var filePath = prop.Name.Replace('/', '\\');
                    var track = prop.Value as JObject;
                    if (track == null) continue;

                    DateTime lastMod;
                    var lastModStr = track["lastModified"]?.Value<string>();
                    if (string.IsNullOrEmpty(lastModStr))
                    {
                        // No timestamp in cache - get current file timestamp so it will be used
                        // (will rescan if file has changed since we got this timestamp)
                        try { lastMod = File.GetLastWriteTimeUtc(filePath); }
                        catch { continue; }
                    }
                    else if (!DateTime.TryParse(lastModStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out lastMod))
                        continue;

                    double[] mfcc = null;
                    var mfccToken = track["mfcc"];
                    if (mfccToken != null && mfccToken.Type == JTokenType.Array)
                        mfcc = mfccToken.Values<double>().ToArray();

                    var features = new TrackFeatures
                    {
                        Bpm = track["bpm"]?.Value<double>() ?? 0,
                        Key = track["key"]?.Value<string>() ?? "",
                        Mode = track["mode"]?.Value<string>() ?? "",
                        SpectralCentroid = track["spectralCentroid"]?.Value<double>() ?? 0,
                        SpectralFlux = track["spectralFlux"]?.Value<double>() ?? 0,
                        Loudness = track["loudness"]?.Value<double>() ?? 0,
                        Danceability = track["danceability"]?.Value<double>() ?? 0,
                        OnsetRate = track["onsetRate"]?.Value<double>() ?? 0,
                        ZeroCrossingRate = track["zeroCrossingRate"]?.Value<double>() ?? 0,
                        SpectralRms = track["spectralRms"]?.Value<double>() ?? 0,
                        SpectralFlatness = track["spectralFlatness"]?.Value<double>() ?? 0,
                        Dissonance = track["dissonance"]?.Value<double>() ?? 0,
                        PitchSalience = track["pitchSalience"]?.Value<double>() ?? 0,
                        ChordsChangesRate = track["chordsChangesRate"]?.Value<double>() ?? 0,
                        Mfcc = mfcc
                    };

                    result[filePath] = (lastMod, features);
                }
            }
            catch { }

            return result;
        }

        /// <summary>
        /// Extract the meaningful error from Essentia's stderr output.
        /// Essentia writes INFO/DEBUG log lines to stderr alongside actual errors,
        /// so we filter those out and look for real error indicators.
        /// </summary>
        static string ExtractEssentiaError(string stderr, int exitCode)
        {
            if (string.IsNullOrWhiteSpace(stderr))
                return $"Exit code {exitCode}";

            var lines = stderr.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            // Find lines that look like actual errors (not INFO/DEBUG log noise)
            var errorLines = lines.Where(line =>
                !line.TrimStart().StartsWith("[   INFO   ]") &&
                !line.TrimStart().StartsWith("[  DEBUG  ]") &&
                !line.TrimStart().StartsWith("[  INFO  ]") &&
                !string.IsNullOrWhiteSpace(line)
            ).ToList();

            if (errorLines.Count > 0)
            {
                // Return the last real error line (typically the most specific)
                // but include up to 3 error lines for context
                var relevant = errorLines.Skip(Math.Max(0, errorLines.Count - 3)).ToList();
                return string.Join(" | ", relevant);
            }

            // All lines were INFO/DEBUG — return the last line anyway with a prefix
            return $"Exit code {exitCode} (stderr: {lines.Last()})";
        }

        static (TrackFeatures? Features, string? Error) AnalyzeWithEssentia(string essentiaExe, string audioPath)
        {
            // Check if file exists
            if (!File.Exists(audioPath))
            {
                Console.WriteLine($"  File not found: {audioPath}");
                return (null, "File not found");
            }

            // Get file size for diagnostics on failure
            long fileSizeBytes = 0;
            try { fileSizeBytes = new FileInfo(audioPath).Length; } catch { }

            // Create temp file for Essentia output
            var tempJson = Path.GetTempFileName();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = essentiaExe,
                    Arguments = $"\"{audioPath}\" \"{tempJson}\"",
                    RedirectStandardOutput = false,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi)!;
                var stderr = proc.StandardError.ReadToEnd();
                if (!proc.WaitForExit(120000)) // 2 minute timeout
                {
                    try { proc.Kill(); } catch { }
                    var sizeMb = fileSizeBytes / (1024.0 * 1024.0);
                    return (null, $"Timeout (>2 min, {sizeMb:F0} MB)");
                }

                if (proc.ExitCode != 0)
                {
                    var errorMsg = ExtractEssentiaError(stderr, proc.ExitCode);
                    // Append file size for memory-related errors
                    if (errorMsg.Contains("bad_alloc") || errorMsg.Contains("Out of memory"))
                    {
                        var sizeMb = fileSizeBytes / (1024.0 * 1024.0);
                        errorMsg += $" ({sizeMb:F0} MB)";
                    }
                    return (null, errorMsg);
                }

                if (!File.Exists(tempJson) || new FileInfo(tempJson).Length == 0)
                {
                    // Essentia exited 0 but produced no output — include stderr for clues
                    var hint = ExtractEssentiaError(stderr, 0);
                    return (null, $"Empty output from Essentia ({hint})");
                }

                var json = File.ReadAllText(tempJson);
                var features = ParseEssentiaOutput(json);
                if (features != null)
                    return (features, null);

                // Parse failed — include stderr and output size for diagnostics
                var jsonSize = new FileInfo(tempJson).Length;
                var parseHint = !string.IsNullOrWhiteSpace(stderr)
                    ? ExtractEssentiaError(stderr, 0)
                    : $"output {jsonSize} bytes";
                return (null, $"Failed to parse Essentia output ({parseHint})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Exception: {ex.Message}");
                return (null, ex.Message);
            }
            finally
            {
                try { File.Delete(tempJson); } catch { }
            }
        }

        static TrackFeatures? ParseEssentiaOutput(string json)
        {
            try
            {
                var root = JObject.Parse(json);

                // Extract raw features from Essentia output
                var bpm = root.SelectToken("rhythm.bpm")?.Value<double>() ?? 0;
                var key = root.SelectToken("tonal.key_edma.key")?.Value<string>()
                       ?? root.SelectToken("tonal.key_krumhansl.key")?.Value<string>()
                       ?? root.SelectToken("tonal.chords_key")?.Value<string>() ?? "";
                var scale = root.SelectToken("tonal.key_edma.scale")?.Value<string>()
                         ?? root.SelectToken("tonal.key_krumhansl.scale")?.Value<string>()
                         ?? root.SelectToken("tonal.chords_scale")?.Value<string>() ?? "";

                // Lowlevel features for mood computation
                var loudness = root.SelectToken("lowlevel.loudness_ebu128.integrated")?.Value<double>()
                            ?? root.SelectToken("lowlevel.average_loudness")?.Value<double>() ?? -20;
                var spectralCentroidMean = root.SelectToken("lowlevel.spectral_centroid.mean")?.Value<double>() ?? 2000;
                var spectralFluxMean = root.SelectToken("lowlevel.spectral_flux.mean")?.Value<double>() ?? 0.1;
                var danceability = root.SelectToken("rhythm.danceability")?.Value<double>() ?? 0.5;

                // Arousal features
                var onsetRate = root.SelectToken("rhythm.onset_rate")?.Value<double>() ?? 0;
                var zeroCrossingRate = root.SelectToken("lowlevel.zerocrossingrate.mean")?.Value<double>() ?? 0;
                var spectralRms = root.SelectToken("lowlevel.spectral_rms.mean")?.Value<double>() ?? 0;

                // Valence features
                var spectralFlatness = root.SelectToken("lowlevel.spectral_flatness_db.mean")?.Value<double>() ?? 0;
                var dissonance = root.SelectToken("lowlevel.dissonance.mean")?.Value<double>() ?? 0;
                var pitchSalience = root.SelectToken("lowlevel.pitch_salience.mean")?.Value<double>() ?? 0;
                var chordsChangesRate = root.SelectToken("tonal.chords_changes_rate")?.Value<double>() ?? 0;

                // MFCCs (13-coefficient timbre vector)
                double[] mfcc = null;
                var mfccToken = root.SelectToken("lowlevel.mfcc.mean");
                if (mfccToken != null && mfccToken.Type == JTokenType.Array)
                    mfcc = mfccToken.Values<double>().ToArray();

                return new TrackFeatures
                {
                    Bpm = Math.Round(bpm, 1),
                    Key = key,
                    Mode = scale,
                    SpectralCentroid = Math.Round(spectralCentroidMean, 1),
                    SpectralFlux = Math.Round(spectralFluxMean, 4),
                    Loudness = Math.Round(loudness, 2),
                    Danceability = Math.Round(danceability, 4),
                    OnsetRate = Math.Round(onsetRate, 2),
                    ZeroCrossingRate = Math.Round(zeroCrossingRate, 6),
                    SpectralRms = Math.Round(spectralRms, 6),
                    SpectralFlatness = Math.Round(spectralFlatness, 6),
                    Dissonance = Math.Round(dissonance, 4),
                    PitchSalience = Math.Round(pitchSalience, 4),
                    ChordsChangesRate = Math.Round(chordsChangesRate, 4),
                    Mfcc = mfcc?.Select(v => Math.Round(v, 4)).ToArray()
                };
            }
            catch
            {
                return null;
            }
        }

        // Analysis-only tracking for accurate ETA (cached tracks are instant, don't skew the rate)
        static int _analyzeCount;
        static long _analyzeTicksTotal;

        static string FormatEta(TimeSpan elapsed, int done, int total)
        {
            if (done < 10 || total <= done) return "";
            var remaining = total - done;

            // If we have analysis timing data, use it to estimate remaining analysis work
            var analyzeCount = Volatile.Read(ref _analyzeCount);
            if (analyzeCount >= 3)
            {
                var analyzeTicks = Volatile.Read(ref _analyzeTicksTotal);
                var avgAnalyzeSecs = TimeSpan.FromTicks(analyzeTicks / analyzeCount).TotalSeconds;

                // Estimate how many remaining tracks will need analysis vs cache hit
                // Use the ratio observed so far: analyzed / processed
                var analyzeRatio = (double)analyzeCount / done;
                var remainingAnalyses = remaining * analyzeRatio;
                var etaSecs = remainingAnalyses * avgAnalyzeSecs;
                return $" ETA {FormatTimeSpan(TimeSpan.FromSeconds(etaSecs))}";
            }

            // Fallback: blended rate (before enough analysis samples)
            var secsPerTrack = elapsed.TotalSeconds / done;
            var etaFallback = secsPerTrack * remaining;
            return $" ETA {FormatTimeSpan(TimeSpan.FromSeconds(etaFallback))}";
        }

        static string FormatTimeSpan(TimeSpan ts)
        {
            if (ts.TotalHours >= 1)
                return $"{(int)ts.TotalHours}h{ts.Minutes:D2}m";
            if (ts.TotalMinutes >= 1)
                return $"{(int)ts.TotalMinutes}m{ts.Seconds:D2}s";
            return $"{ts.TotalSeconds:F1}s";
        }

        static string CsvEscape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            value = value.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ");
            if (value.Contains(",") || value.Contains("\""))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }
    }
}
