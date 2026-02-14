using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
        public double[]? Mfcc { get; set; }
    }

    class TrackEntry
    {
        public TrackFeatures Features = null!;
        public DateTime LastModified;
        public double? AnalysisDurationSecs;
    }

    public class TrackFingerprint
    {
        public int TrackId { get; set; }
        public string Artist { get; set; } = "";
        public string Title { get; set; } = "";
        public string Album { get; set; } = "";
        public string Genre { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string Chromaprint { get; set; } = "";
        public int Duration { get; set; }
        public string Md5 { get; set; } = "";
    }

    class FingerprintEntry
    {
        public TrackFingerprint Fp = null!;
        public DateTime LastModified;
    }

    class Program
    {
        static int _analyzeCount;
        static long _analyzeTicksTotal;

        static void Main(string[] args)
        {
            var parallelism = Environment.ProcessorCount;
            string? xmlPath = null;
            bool fixupMode = false;
            bool retryErrors = false;
            bool migrateMode = false;
            bool fingerprintMode = false;
            bool chromaprintOnly = false;
            bool md5Only = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--fixup") fixupMode = true;
                else if (args[i] == "--retry-errors") retryErrors = true;
                else if (args[i] == "--migrate") migrateMode = true;
                else if (args[i] == "--fingerprint") fingerprintMode = true;
                else if (args[i] == "--chromaprint-only") { fingerprintMode = true; chromaprintOnly = true; }
                else if (args[i] == "--md5-only") { fingerprintMode = true; md5Only = true; }
                else if ((args[i] == "-p" || args[i] == "--parallel") && i + 1 < args.Length && int.TryParse(args[i + 1], out var p) && p > 0) { parallelism = p; i++; }
                else if (!args[i].StartsWith("-") && xmlPath == null) xmlPath = args[i];
            }

            xmlPath = xmlPath ?? "iTunes Music Library.xml";

            if (!File.Exists(xmlPath))
            {
                Console.WriteLine($"iTunes library not found: {xmlPath}");
                Console.WriteLine("Usage: truedat.exe <path-to-iTunes-Music-Library.xml> [options]");
                Console.WriteLine();
                Console.WriteLine("Options:");
                Console.WriteLine($"  -p, --parallel      Number of parallel threads (default: {Environment.ProcessorCount})");
                Console.WriteLine("  --fixup             Validate and remap paths in mbxmoods.json without re-analyzing");
                Console.WriteLine("  --retry-errors      Re-attempt all previously failed files (clears error log)");
                Console.WriteLine("  --migrate           Strip legacy valence/arousal fields from mbxmoods.json (creates backup)");
                Console.WriteLine("  --fingerprint       Run fingerprint mode (chromaprint + md5) → mbxhub-fingerprints.json");
                Console.WriteLine("  --chromaprint-only  Fingerprint mode: only run chromaprint (skip md5)");
                Console.WriteLine("  --md5-only          Fingerprint mode: only run audio md5 (skip chromaprint)");
                return;
            }

            var outputDir = Path.GetDirectoryName(Path.GetFullPath(xmlPath)) ?? ".";
            var moodsPath = Path.Combine(outputDir, "mbxmoods.json");

            if (migrateMode) { RunMigrate(moodsPath); return; }
            if (fixupMode) { RunFixup(xmlPath, moodsPath); return; }
            if (fingerprintMode) { RunFingerprint(xmlPath, outputDir, parallelism, retryErrors, chromaprintOnly, md5Only); return; }

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

            // Single in-memory dataset — loaded from disk once, updated by workers, streamed on save.
            // Eliminates the old pattern of re-reading/re-parsing the entire JSON on every save.
            var allTracks = new ConcurrentDictionary<string, TrackEntry>(StringComparer.OrdinalIgnoreCase);
            int existingCount = LoadExistingMoods(moodsPath, allTracks);
            Console.WriteLine($"Existing moods: {existingCount}");

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

            int cachedCount = 0;
            int analyzed = 0;
            int skipped = 0;
            int failed = 0;
            int timedOut = 0;
            int processed = 0;
            int total = tracks.Count;
            int lastSaveAnalyzed = 0;
            const int SaveInterval = 25;
            var saveLock = new object();

            var startTime = DateTime.Now;
            var sw = Stopwatch.StartNew();

            var cts = new CancellationTokenSource();
            var cancelRequested = 0;
            Console.CancelKeyPress += (_, e) =>
            {
                if (Interlocked.Exchange(ref cancelRequested, 1) == 0)
                {
                    e.Cancel = true;
                    Console.WriteLine();
                    Console.WriteLine("Ctrl+C received - finishing current tracks and saving...");
                    cts.Cancel();
                }
                else
                {
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

                        if (existingErrors.TryGetValue(t.Location, out var prevError))
                        {
                            Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name} (skip: {prevError})");
                            Interlocked.Increment(ref skipped);
                            return;
                        }

                        // Check if already in moods and file unchanged
                        if (allTracks.TryGetValue(t.Location, out var existing))
                        {
                            try
                            {
                                var currentLastMod = File.GetLastWriteTimeUtc(t.Location);
                                if (TruncateToSeconds(currentLastMod) == TruncateToSeconds(existing.LastModified))
                                {
                                    var f = existing.Features;
                                    f.TrackId = t.TrackId;
                                    f.Artist = t.Artist;
                                    f.Title = t.Name;
                                    f.Album = t.Album;
                                    f.Genre = t.Genre;
                                    f.FilePath = t.Location;
                                    existing.LastModified = currentLastMod;
                                    Interlocked.Increment(ref cachedCount);
                                    Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name} (cached)");
                                    return;
                                }
                            }
                            catch { }
                        }

                        long fileSizeBytes = 0;
                        var sizeTag = "";
                        try
                        {
                            fileSizeBytes = new FileInfo(t.Location).Length;
                            var sizeMb = fileSizeBytes / (1024.0 * 1024.0);
                            if (sizeMb >= 100) sizeTag = $" [{sizeMb:F0} MB]";
                        }
                        catch { }
                        Console.WriteLine($"[{current}/{total} {pct}%{eta}] {t.Artist} - {t.Name}{sizeTag}");

                        var analyzeStart = Stopwatch.GetTimestamp();
                        var (feat, errorReason) = AnalyzeWithEssentia(essentiaExe, t.Location, fileSizeBytes);
                        var analyzeTicks = Stopwatch.GetTimestamp() - analyzeStart;
                        var analyzeDuration = TimeSpan.FromTicks(analyzeTicks);
                        Interlocked.Add(ref _analyzeTicksTotal, analyzeTicks);
                        Interlocked.Increment(ref _analyzeCount);

                        if (feat == null)
                        {
                            var err = errorReason ?? "Unknown error";
                            var sizeMb = fileSizeBytes / (1024.0 * 1024.0);
                            AppendError(errorsPath, t.Location, t.Artist, t.Name, err, sizeMb, analyzeDuration.TotalSeconds, saveLock);
                            Console.WriteLine($"  FAILED: {err}");
                            Interlocked.Increment(ref failed);
                            if (err.Contains("Timeout")) Interlocked.Increment(ref timedOut);
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
                        allTracks[t.Location] = new TrackEntry { Features = feat, LastModified = lastMod, AnalysisDurationSecs = analyzeDuration.TotalSeconds };
                        var newAnalyzed = Interlocked.Increment(ref analyzed);

                        if (newAnalyzed - lastSaveAnalyzed >= SaveInterval)
                        {
                            lock (saveLock)
                            {
                                if (newAnalyzed - lastSaveAnalyzed >= SaveInterval)
                                {
                                    lastSaveAnalyzed = newAnalyzed;
                                    var saveSw = Stopwatch.StartNew();
                                    SaveResults(moodsPath, allTracks);
                                    saveSw.Stop();
                                    Console.WriteLine($"  [Saved {allTracks.Count} tracks in {saveSw.Elapsed.TotalSeconds:F1}s]");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error: {t.Artist} - {t.Name}: {ex.Message}");
                        AppendError(errorsPath, t.Location, t.Artist, t.Name, ex.Message, 0, 0, saveLock);
                        Interlocked.Increment(ref failed);
                    }
                });
            }
            catch (OperationCanceledException) { }

            sw.Stop();
            var endTime = DateTime.Now;
            var wasCancelled = Volatile.Read(ref cancelRequested) != 0;

            var finalSaveSw = Stopwatch.StartNew();
            SaveResults(moodsPath, allTracks);
            finalSaveSw.Stop();

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
            Console.WriteLine($"  Failed:     {failed}{(timedOut > 0 ? $"  ({timedOut} timed out)" : "")}");
            Console.WriteLine($"  ────────    ─────");
            Console.WriteLine($"  Processed:  {cachedCount + analyzed + skipped + failed}");
            Console.WriteLine($"  Output:     {allTracks.Count} tracks in moods file");
            if (analyzed > 0)
            {
                var avgAnalyze = TimeSpan.FromTicks(_analyzeTicksTotal / analyzed);
                Console.WriteLine($"  Avg/track:  {avgAnalyze.TotalSeconds:F1}s (analysis only)");
            }
            Console.WriteLine($"  Last save:  {finalSaveSw.Elapsed.TotalSeconds:F1}s");
            try
            {
                var peakMb = Process.GetCurrentProcess().PeakWorkingSet64 / (1024.0 * 1024.0);
                Console.WriteLine($"  Peak mem:   {peakMb:F0} MB");
            }
            catch { }
            Console.WriteLine();
            Console.WriteLine($"Output: {moodsPath}");
        }

        static void RunFixup(string xmlPath, string moodsPath)
        {
            Console.WriteLine("=== Fixup Mode ===");
            Console.WriteLine();

            if (!File.Exists(moodsPath)) { Console.WriteLine($"No moods file found: {moodsPath}"); return; }

            Console.WriteLine($"Loading moods: {moodsPath}");
            var json = File.ReadAllText(moodsPath);
            var root = JObject.Parse(json);
            var tracks = root["tracks"] as JObject;
            if (tracks == null || tracks.Count == 0) { Console.WriteLine("No tracks in moods file."); return; }
            Console.WriteLine($"Moods entries: {tracks.Count}");

            Console.WriteLine($"Loading iTunes library: {xmlPath}");
            var library = ITunesParser.Parse(xmlPath);
            Console.WriteLine($"Library tracks: {library.Count}");

            var byFilename = new Dictionary<string, List<ITunesTrack>>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in library)
            {
                var filename = Path.GetFileName(t.Location);
                if (string.IsNullOrEmpty(filename)) continue;
                if (!byFilename.TryGetValue(filename, out var list)) { list = new List<ITunesTrack>(); byFilename[filename] = list; }
                list.Add(t);
            }

            int unchanged = 0, remapped = 0, orphaned = 0;
            var newTracks = new JObject();
            var orphanedEntries = new List<(string OldPath, string Artist, string Title)>();

            foreach (var prop in tracks.Properties().ToList())
            {
                var oldPath = prop.Name;
                var trackData = prop.Value as JObject;
                if (trackData == null) continue;
                var normalizedOldPath = oldPath.Replace('/', '\\');

                if (File.Exists(normalizedOldPath)) { newTracks[normalizedOldPath] = trackData; unchanged++; continue; }

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

                    if (strictMatches.Count == 1) match = strictMatches[0];
                    else if (strictMatches.Count > 1) Console.WriteLine($"  AMBIGUOUS ({strictMatches.Count} matches): {moodArtist} - {moodTitle}");
                }

                if (match != null)
                {
                    trackData["trackId"] = match.TrackId;
                    newTracks[match.Location] = trackData;
                    remapped++;
                    Console.WriteLine($"  REMAP: {moodArtist} - {moodTitle}");
                    Console.WriteLine($"    {oldPath}");
                    Console.WriteLine($"    -> {match.Location}");
                }
                else { orphaned++; orphanedEntries.Add((oldPath, moodArtist, moodTitle)); }
            }

            var moodPaths = new HashSet<string>(newTracks.Properties().Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
            int unanalyzed = library.Count(t => !moodPaths.Contains(t.Location));

            Console.WriteLine();
            Console.WriteLine("=== Results ===");
            Console.WriteLine($"  Unchanged:   {unchanged}");
            Console.WriteLine($"  Remapped:    {remapped}");
            Console.WriteLine($"  Orphaned:    {orphaned}");
            Console.WriteLine($"  Unanalyzed:  {unanalyzed} (in library, no mood data)");
            Console.WriteLine($"  Total out:   {newTracks.Count}");

            if (orphanedEntries.Count > 0 && orphanedEntries.Count <= 20)
            {
                Console.WriteLine(); Console.WriteLine("Orphaned entries (no match found):");
                foreach (var (path, artist, title) in orphanedEntries) Console.WriteLine($"  {artist} - {title}: {path}");
            }
            else if (orphanedEntries.Count > 20)
            {
                Console.WriteLine(); Console.WriteLine($"Orphaned entries ({orphanedEntries.Count} total, showing first 20):");
                foreach (var (path, artist, title) in orphanedEntries.Take(20)) Console.WriteLine($"  {artist} - {title}: {path}");
                Console.WriteLine($"  ... and {orphanedEntries.Count - 20} more");
            }

            if (remapped > 0 || orphaned > 0)
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd.HHmmss");
                var bakPath = $"{moodsPath}.bak.{timestamp}";
                File.Copy(moodsPath, bakPath);
                Console.WriteLine(); Console.WriteLine($"Backup: {bakPath}");
                root["tracks"] = newTracks; root["trackCount"] = newTracks.Count; root["generatedAt"] = DateTime.UtcNow.ToString("o");
                var tmpPath = moodsPath + ".tmp";
                File.WriteAllText(tmpPath, root.ToString(Formatting.Indented));
                File.Delete(moodsPath); File.Move(tmpPath, moodsPath);
                Console.WriteLine($"Updated: {moodsPath}");
            }
            else { Console.WriteLine(); Console.WriteLine("All paths valid, no changes needed."); }
        }

        static void RunMigrate(string moodsPath)
        {
            Console.WriteLine("=== Migrate Mode ===");
            Console.WriteLine("Strips legacy valence/arousal fields from mbxmoods.json");
            Console.WriteLine();

            if (!File.Exists(moodsPath)) { Console.WriteLine($"No moods file found: {moodsPath}"); return; }

            Console.WriteLine($"Loading: {moodsPath}");
            var json = File.ReadAllText(moodsPath);
            var root = JObject.Parse(json);
            var tracks = root["tracks"] as JObject;
            if (tracks == null || tracks.Count == 0) { Console.WriteLine("No tracks in moods file."); return; }

            int stripped = 0, total = tracks.Count;
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
            if (stripped == 0) { Console.WriteLine(); Console.WriteLine("No legacy fields found, nothing to do."); return; }

            var timestamp = DateTime.Now.ToString("yyyyMMdd.HHmmss");
            var bakPath = $"{moodsPath}.bak.{timestamp}";
            File.Copy(moodsPath, bakPath);
            Console.WriteLine(); Console.WriteLine($"Backup: {bakPath}");
            root["generatedAt"] = DateTime.UtcNow.ToString("o");
            var tmpPath = moodsPath + ".tmp";
            File.WriteAllText(tmpPath, root.ToString(Formatting.Indented));
            File.Delete(moodsPath); File.Move(tmpPath, moodsPath);
            Console.WriteLine($"Updated: {moodsPath}");
        }

        static Dictionary<string, string> LoadExistingErrors(string path)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return result;
            try
            {
                foreach (var line in File.ReadAllLines(path).Skip(1))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = ParseCsvLine(line);
                    if (parts.Length >= 4) result[parts[3]] = parts[0];
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
                if (c == '"') { if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { current += '"'; i++; } else inQuotes = !inQuotes; }
                else if (c == ',' && !inQuotes) { result.Add(current); current = ""; }
                else current += c;
            }
            result.Add(current);
            return result.ToArray();
        }

        static void AppendError(string errorsPath, string filePath, string artist, string title,
            string error, double sizeMb, double durationSecs, object lockObj)
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
                            if (needsHeader) writer.WriteLine("Error,Artist,Title,FilePath,SizeMB,Duration");
                            writer.WriteLine($"{CsvEscape(error)},{CsvEscape(artist)},{CsvEscape(title)},{CsvEscape(filePath)},{sizeMb:F1},{durationSecs:F1}");
                        }
                        return;
                    }
                    catch (IOException) when (attempt < 4) { Thread.Sleep(200 * (attempt + 1)); }
                    catch (Exception ex) { Console.WriteLine($"  Warning: Could not write to errors CSV: {ex.Message}"); return; }
                }
            }
        }

        /// <summary>
        /// Stream allTracks to disk using JsonTextWriter — no intermediate strings or JObject trees.
        /// Writes directly to FileStream with 64K buffer. Memory usage is O(1) per track.
        /// </summary>
        static void SaveResults(string moodsPath, ConcurrentDictionary<string, TrackEntry> allTracks)
        {
            var tmpPath = moodsPath + ".tmp";
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }

            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            using (var sw = new StreamWriter(fs, new UTF8Encoding(false), 65536))
            using (var jw = new JsonTextWriter(sw) { Formatting = Formatting.Indented })
            {
                jw.WriteStartObject();
                jw.WritePropertyName("version"); jw.WriteValue("1.0");
                jw.WritePropertyName("generatedAt"); jw.WriteValue(DateTime.UtcNow.ToString("o"));
                jw.WritePropertyName("trackCount"); jw.WriteValue(allTracks.Count);
                jw.WritePropertyName("tracks");
                jw.WriteStartObject();
                foreach (var kvp in allTracks)
                    WriteTrackEntry(jw, kvp.Key, kvp.Value);
                jw.WriteEndObject();
                jw.WriteEndObject();
            }

            if (File.Exists(moodsPath)) File.Delete(moodsPath);
            File.Move(tmpPath, moodsPath);
        }

        static void WriteTrackEntry(JsonTextWriter jw, string path, TrackEntry entry)
        {
            var f = entry.Features;
            jw.WritePropertyName(path);
            jw.WriteStartObject();
            jw.WritePropertyName("trackId"); jw.WriteValue(f.TrackId);
            jw.WritePropertyName("artist"); jw.WriteValue(f.Artist);
            jw.WritePropertyName("title"); jw.WriteValue(f.Title);
            jw.WritePropertyName("album"); jw.WriteValue(f.Album);
            jw.WritePropertyName("genre"); jw.WriteValue(f.Genre);
            jw.WritePropertyName("bpm"); jw.WriteValue(f.Bpm);
            jw.WritePropertyName("key"); jw.WriteValue(f.Key);
            jw.WritePropertyName("mode"); jw.WriteValue(f.Mode);
            jw.WritePropertyName("spectralCentroid"); jw.WriteValue(f.SpectralCentroid);
            jw.WritePropertyName("spectralFlux"); jw.WriteValue(f.SpectralFlux);
            jw.WritePropertyName("loudness"); jw.WriteValue(f.Loudness);
            jw.WritePropertyName("danceability"); jw.WriteValue(f.Danceability);
            jw.WritePropertyName("onsetRate"); jw.WriteValue(f.OnsetRate);
            jw.WritePropertyName("zeroCrossingRate"); jw.WriteValue(f.ZeroCrossingRate);
            jw.WritePropertyName("spectralRms"); jw.WriteValue(f.SpectralRms);
            jw.WritePropertyName("spectralFlatness"); jw.WriteValue(f.SpectralFlatness);
            jw.WritePropertyName("dissonance"); jw.WriteValue(f.Dissonance);
            jw.WritePropertyName("pitchSalience"); jw.WriteValue(f.PitchSalience);
            jw.WritePropertyName("chordsChangesRate"); jw.WriteValue(f.ChordsChangesRate);
            if (f.Mfcc != null)
            {
                jw.WritePropertyName("mfcc");
                jw.WriteStartArray();
                foreach (var v in f.Mfcc) jw.WriteValue(v);
                jw.WriteEndArray();
            }
            jw.WritePropertyName("lastModified"); jw.WriteValue(entry.LastModified.ToString("o"));
            if (entry.AnalysisDurationSecs.HasValue)
            {
                jw.WritePropertyName("analysisDuration"); jw.WriteValue(Math.Round(entry.AnalysisDurationSecs.Value, 1));
            }
            jw.WriteEndObject();
        }

        static int LoadExistingMoods(string path, ConcurrentDictionary<string, TrackEntry> allTracks)
        {
            if (!File.Exists(path)) return 0;
            try
            {
                var json = File.ReadAllText(path);
                var root = JObject.Parse(json);
                var tracks = root["tracks"] as JObject;
                if (tracks == null) return 0;

                foreach (var prop in tracks.Properties())
                {
                    var filePath = prop.Name.Replace('/', '\\');
                    var track = prop.Value as JObject;
                    if (track == null) continue;

                    DateTime lastMod;
                    var lastModStr = track["lastModified"]?.Value<string>();
                    if (string.IsNullOrEmpty(lastModStr))
                    {
                        try { lastMod = File.GetLastWriteTimeUtc(filePath); }
                        catch { continue; }
                    }
                    else if (!DateTime.TryParse(lastModStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out lastMod))
                        continue;

                    double[]? mfcc = null;
                    var mfccToken = track["mfcc"];
                    if (mfccToken != null && mfccToken.Type == JTokenType.Array)
                        mfcc = mfccToken.Values<double>().ToArray();

                    allTracks[filePath] = new TrackEntry
                    {
                        LastModified = lastMod,
                        Features = new TrackFeatures
                        {
                            FilePath = filePath,
                            TrackId = track["trackId"]?.Value<int>() ?? 0,
                            Artist = track["artist"]?.Value<string>() ?? "",
                            Title = track["title"]?.Value<string>() ?? "",
                            Album = track["album"]?.Value<string>() ?? "",
                            Genre = track["genre"]?.Value<string>() ?? "",
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
                        },
                        AnalysisDurationSecs = track["analysisDuration"]?.Value<double>()
                    };
                }
                return allTracks.Count;
            }
            catch (JsonReaderException ex)
            {
                var bakPath = path + $".corrupt.{DateTime.Now:yyyyMMdd.HHmmss}";
                try { File.Copy(path, bakPath); } catch { }
                Console.WriteLine($"WARNING: Existing moods file is corrupt ({ex.Message})");
                Console.WriteLine($"Backup saved to: {bakPath}");
                Console.WriteLine($"Starting fresh - tracks will be re-analyzed.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARNING: Could not load existing moods ({ex.Message})");
                return 0;
            }
        }

        static string ExtractEssentiaError(string stderr, int exitCode)
        {
            if (string.IsNullOrWhiteSpace(stderr)) return $"Exit code {exitCode}";
            var lines = stderr.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var errorLines = lines.Where(line =>
                !line.TrimStart().StartsWith("[   INFO   ]") &&
                !line.TrimStart().StartsWith("[  DEBUG  ]") &&
                !line.TrimStart().StartsWith("[  INFO  ]") &&
                !string.IsNullOrWhiteSpace(line)
            ).ToList();

            if (errorLines.Count > 0)
            {
                var relevant = errorLines.Skip(Math.Max(0, errorLines.Count - 3)).ToList();
                return string.Join(" | ", relevant);
            }
            return $"Exit code {exitCode} (stderr: {lines.Last()})";
        }

        /// <summary>
        /// Run Essentia extractor on an audio file. Uses async stderr read so the
        /// 2-minute timeout actually fires if the process hangs.
        /// </summary>
        static (TrackFeatures? Features, string? Error) AnalyzeWithEssentia(string essentiaExe, string audioPath, long fileSizeBytes)
        {
            if (!File.Exists(audioPath))
                return (null, "File not found");

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

                var timer = Stopwatch.StartNew();
                using var proc = Process.Start(psi);
                var pid = proc.Id;

                // Async stderr — ReadToEnd() blocks forever if process hangs, preventing the timeout
                var stderrTask = proc.StandardError.ReadToEndAsync();

                // Catch hung/zombie processes — real analysis rarely exceeds 2-3 min even for large files
                const int timeoutMs = 5 * 60 * 1000; // 5 minutes

                if (!proc.WaitForExit(timeoutMs))
                {
                    try { proc.Kill(); proc.WaitForExit(5000); } catch { }
                    timer.Stop();
                    var partialStderr = stderrTask.Wait(3000) ? stderrTask.Result : "";
                    var sizeMb = fileSizeBytes / (1024.0 * 1024.0);
                    var hint = !string.IsNullOrWhiteSpace(partialStderr) ? $" | {ExtractEssentiaError(partialStderr, -1)}" : "";
                    return (null, $"Timeout after {timer.Elapsed.TotalSeconds:F0}s (PID {pid}, {sizeMb:F0} MB){hint}");
                }

                var stderr = stderrTask.Wait(5000) ? stderrTask.Result : "";
                timer.Stop();
                var exitCode = proc.ExitCode;

                if (exitCode != 0)
                {
                    var sizeMb = fileSizeBytes / (1024.0 * 1024.0);
                    var errorMsg = ExtractEssentiaError(stderr, exitCode);
                    return (null, $"{errorMsg} (exit {exitCode}, PID {pid}, {sizeMb:F0} MB, {timer.Elapsed.TotalSeconds:F1}s)");
                }

                if (!File.Exists(tempJson) || new FileInfo(tempJson).Length == 0)
                    return (null, $"Empty output from Essentia ({ExtractEssentiaError(stderr, 0)})");

                var json = File.ReadAllText(tempJson);
                var features = ParseEssentiaOutput(json);
                if (features != null) return (features, null);

                var jsonSize = new FileInfo(tempJson).Length;
                var parseHint = !string.IsNullOrWhiteSpace(stderr) ? ExtractEssentiaError(stderr, 0) : $"output {jsonSize} bytes";
                return (null, $"Failed to parse Essentia output ({parseHint})");
            }
            catch (Exception ex)
            {
                return (null, $"Exception: {ex.Message}");
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
                var bpm = root.SelectToken("rhythm.bpm")?.Value<double>() ?? 0;
                var key = root.SelectToken("tonal.key_edma.key")?.Value<string>()
                       ?? root.SelectToken("tonal.key_krumhansl.key")?.Value<string>()
                       ?? root.SelectToken("tonal.chords_key")?.Value<string>() ?? "";
                var scale = root.SelectToken("tonal.key_edma.scale")?.Value<string>()
                         ?? root.SelectToken("tonal.key_krumhansl.scale")?.Value<string>()
                         ?? root.SelectToken("tonal.chords_scale")?.Value<string>() ?? "";
                var loudness = root.SelectToken("lowlevel.loudness_ebu128.integrated")?.Value<double>()
                            ?? root.SelectToken("lowlevel.average_loudness")?.Value<double>() ?? -20;
                var spectralCentroidMean = root.SelectToken("lowlevel.spectral_centroid.mean")?.Value<double>() ?? 2000;
                var spectralFluxMean = root.SelectToken("lowlevel.spectral_flux.mean")?.Value<double>() ?? 0.1;
                var danceability = root.SelectToken("rhythm.danceability")?.Value<double>() ?? 0.5;
                var onsetRate = root.SelectToken("rhythm.onset_rate")?.Value<double>() ?? 0;
                var zeroCrossingRate = root.SelectToken("lowlevel.zerocrossingrate.mean")?.Value<double>() ?? 0;
                var spectralRms = root.SelectToken("lowlevel.spectral_rms.mean")?.Value<double>() ?? 0;
                var spectralFlatness = root.SelectToken("lowlevel.spectral_flatness_db.mean")?.Value<double>() ?? 0;
                var dissonance = root.SelectToken("lowlevel.dissonance.mean")?.Value<double>() ?? 0;
                var pitchSalience = root.SelectToken("lowlevel.pitch_salience.mean")?.Value<double>() ?? 0;
                var chordsChangesRate = root.SelectToken("tonal.chords_changes_rate")?.Value<double>() ?? 0;
                double[]? mfcc = null;
                var mfccToken = root.SelectToken("lowlevel.mfcc.mean");
                if (mfccToken != null && mfccToken.Type == JTokenType.Array)
                    mfcc = mfccToken.Values<double>().ToArray();

                return new TrackFeatures
                {
                    Bpm = Math.Round(bpm, 1), Key = key, Mode = scale,
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
            catch { return null; }
        }

        static DateTime TruncateToSeconds(DateTime dt)
        {
            return new DateTime(dt.Ticks - dt.Ticks % TimeSpan.TicksPerSecond, dt.Kind);
        }

        static string FormatEta(TimeSpan elapsed, int done, int total)
        {
            if (done < 10 || total <= done) return "";
            var remaining = total - done;
            var analyzeCount = Volatile.Read(ref _analyzeCount);
            if (analyzeCount >= 3)
            {
                var analyzeTicks = Volatile.Read(ref _analyzeTicksTotal);
                var avgAnalyzeSecs = TimeSpan.FromTicks(analyzeTicks / analyzeCount).TotalSeconds;
                var analyzeRatio = (double)analyzeCount / done;
                return $" ETA {FormatTimeSpan(TimeSpan.FromSeconds(remaining * analyzeRatio * avgAnalyzeSecs))}";
            }
            return $" ETA {FormatTimeSpan(TimeSpan.FromSeconds(elapsed.TotalSeconds / done * remaining))}";
        }

        static string FormatTimeSpan(TimeSpan ts)
        {
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h{ts.Minutes:D2}m";
            if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m{ts.Seconds:D2}s";
            return $"{ts.TotalSeconds:F1}s";
        }

        static string CsvEscape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            value = value.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ");
            if (value.Contains(",") || value.Contains("\"")) return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }
    }
}
