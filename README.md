# Truedat - Music Mood Extractor, Fingerprinter & Test Toolkit

Truedat serves two purposes:

### 1. Production: Audio Analysis for MBXHub

Runs [Essentia](https://essentia.upf.edu/) against your music library to extract 55 acoustic features per track (15 core mood features + 40 extended descriptors), maps every song onto a 2D emotion space (valence/arousal), and optionally generates perceptual fingerprints and audio-data hashes. The output (`mbxmoods.json`) is the mood data file that MBXHub's AutoQ engine uses for mood-aware shuffle.

### 2. Test Tooling: Synthetic Library Generation & Mood Seeding

Generates large synthetic libraries (100k-500k+ tracks) with real metadata from [MusicBrainz](https://musicbrainz.org/) and real acoustic features from [AcousticBrainz](https://acousticbrainz.org/). Used to test MBXHub at scale — exercising AutoQ scoring, mood channels, diversity quotas, and performance — before real users with large libraries report issues. Can also seed `mbxmoods.json` from the AcousticBrainz catalog, providing instant mood data for tracks that match by artist+title without running Essentia.

**Output:**

- `mbxmoods.json` - mood coordinates and raw audio features for every track
- `mbxhub-fingerprints.json` - Chromaprint perceptual fingerprints and audio MD5 hashes
- `mbxhub-details.json` - audio metadata from ffprobe (codec, bitrate, sample rate, etc.)
- `mbxmoods-errors.csv` - tracks that failed mood analysis (with error reason, file size, duration)
- `mbxhub-fingerprints-errors.csv` - tracks that failed fingerprinting
- `truedat.log` - full console output for diagnostics (when `--audit` is used)

## What It Does

Truedat reads an iTunes Music Library XML file and runs each audio file through Essentia tools:

### Mood Analysis (default mode)

- **Valence** (0-1): Sad ← → Happy (8 input features)
- **Arousal** (0-1): Calm ← → Energetic (7 input features)
- **15 core + 40 extended Essentia features** stored per track for runtime recomputation (extended set covers loudness envelope, silence profile, spectral shape, psychoacoustic bands, rhythm/tonal aggregates)

This enables mood-based selection in MBXHub - pick a vibe like "Energetic" or "Chill" and the AutoQ engine filters your library accordingly.

### Fingerprint Mode (`--fingerprint`)

- **Chromaprint** - Perceptual audio fingerprint (AcoustID). Identifies the same *song* regardless of encoding, bitrate, or format.
- **Audio MD5** - Hash of raw decoded audio data (ignores metadata tags). Identifies the exact same *audio data*.

### Filename Check (`--check-filenames`)

Scans your library for filenames with characters that cause Essentia tools to fail. Reports three tiers:

- **Errors** - Fullwidth Unicode substitution characters (e.g. `⧸` `：` `＂`) that are known to break Essentia's ANSI argv parsing. These files will always fail analysis.
- **Warnings** - Other non-ASCII characters where 8.3 short path fallback is unavailable. These files may fail depending on system configuration.
- **Suspects** - Audio files under 50 KB that may be corrupt or truncated.

## Quick Start

```cmd
REM Mood analysis (default)
truedat.exe "iTunes Music Library.xml"

REM Fingerprint mode
truedat.exe "iTunes Music Library.xml" --fingerprint
```

Output: `mbxmoods.json` / `mbxhub-fingerprints.json` (next to the XML file)

### Options

```
truedat.exe <path-to-iTunes-Music-Library.xml> [options]

  -p, --parallel <N>      Number of parallel threads (default: all cores)
  --fixup                 Validate and remap paths in mbxmoods.json without re-analyzing
  --verify                Recompute audioStreamSha256 per entry, report drift / missing
                          (read-only; writes mbxmoods-verify.csv next to the moods file)
  --chunk M/N             Split scan across machines via deterministic hash-mod assignment
                          (output auto-suffixed: mbxmoods.<hostname>.json; combine via --merge-moods)
  --retry-errors          Re-attempt all previously failed files (clears error log)
  --migrate               Strip legacy valence/arousal fields from mbxmoods.json (creates backup)
  --fingerprint           Run fingerprint mode (chromaprint + md5) → mbxhub-fingerprints.json
  --chromaprint-only      Fingerprint mode: only run chromaprint (skip md5)
  --md5-only              Fingerprint mode: only run audio md5 (skip chromaprint)
  --details               Use ffprobe → mbxhub-details.json (implies --fingerprint)
  --output <path>         --hash-only mode: append identity envelopes as NDJSON to <path>
  --hash-only             Identity-only mode (no Essentia). Requires --level, --file-list, --output
  --level <name>          With --hash-only: 'fingerprint' (cheap composite) or 'stream' (durable SHA-256)
  --audit                 Write all console output to truedat.log (for debugging)
  --analyze-file <path> Analyze a single audio file with Essentia (no iTunes XML needed)
  --file-list <path>    Analyze files listed in a text file (one path per line, UTF-8, # comments)
                        Mutually exclusive with --analyze-file; -p sets parallelism
  --check-filenames       Scan for filenames with characters that break Essentia tools
```

**Optional:** Place `ffmpeg.exe` and `ffprobe.exe` alongside `truedat.exe` (or on PATH) to enable auto-downmix of multi-channel (5.1+) audio files and the `--details` probe mode. Without ffmpeg, multi-channel files are skipped with a warning.

### Large Libraries

For large libraries (50K+ tracks), expect multi-day scans for mood analysis. Fingerprinting is much faster. Both modes are designed for this:

- **Incremental** - Skips tracks already processed (by file path + last-modified timestamp).
- **Tag-edit resilient** - When mtime drifts but the audio bytes are unchanged (e.g. tag editor rewrote a frame), the cache reuses Essentia features by recomputing only `audioStreamSha256` (~50ms managed SHA per file) and refreshing the tag-affected identity fields. No full re-extraction.
- **Cross-path resilient** - File moved or renamed? Cross-MD5 / cross-SHA fallbacks re-key the cached entry to the new path without re-analyzing.
- **Multi-machine chunking** - Two boxes pointed at the same library run `--chunk 1/2` and `--chunk 2/2` and produce hostname-suffixed shards (`mbxmoods.<host>.json`); merge later with `--merge-moods`. Hash-mod assignment means iTunes XMLs need not be identical between machines.
- **Resumable** - Stop and restart anytime. Progress is saved every 25 analyzed tracks.
- **Verifiable** - `truedat --verify` walks the moods file and confirms each entry's `audioStreamSha256` still matches the disk. Detail goes to `mbxmoods-verify.csv`; exit 1 on any drift / missing / error makes it CI-friendly.
- **ETA tracking** - Shows per-track rate and estimated completion time.
- **Error resilience** - Failed tracks logged to errors CSV, skipped on retry.

```cmd
REM First run - analyzes everything
truedat.exe "iTunes Music Library.xml" -p 4

REM Resume after interruption - picks up where it left off
truedat.exe "iTunes Music Library.xml" -p 4

REM Fix path separators without re-analyzing (e.g., after moving files)
truedat.exe "iTunes Music Library.xml" --fixup

REM Generate fingerprints for the whole library
truedat.exe "iTunes Music Library.xml" --fingerprint

REM Only chromaprint (e.g., for duplicate detection)
truedat.exe "iTunes Music Library.xml" --chromaprint-only

REM Check for problematic filenames before scanning
truedat.exe "iTunes Music Library.xml" --check-filenames

REM Probe audio details (codec, bitrate, sample rate, etc.)
truedat.exe "iTunes Music Library.xml" --details

REM Analyze a single file (no iTunes XML needed)
truedat.exe --analyze-file "C:\Music\song.mp3" --json-output

REM Batch analyze files from a list, write entries to a moods file
truedat.exe --file-list files.txt --moods C:\Music\mbxmoods.json -p 4

REM Hash-only: cheap composite fingerprint, append to NDJSON manifest (ms per file)
truedat.exe --hash-only --level fingerprint --file-list files.txt --output manifest.ndjson -p 32

REM Hash-only: durable audioStreamSha256 (disk-bound; emits fingerprint.v1 too)
truedat.exe --hash-only --level stream --file-list files.txt --output manifest.ndjson -p 8

REM Verify the cache against disk (read-only — recomputes audioStreamSha256 per entry)
truedat.exe --verify --moods C:\Music\mbxmoods.json

REM Two-machine same-library scan: each box does its own deterministic shard
truedat.exe "iTunes Music Library.xml" --chunk 1/2     REM machine A
truedat.exe "iTunes Music Library.xml" --chunk 2/2     REM machine B
truedat.exe --merge-moods --merge-source mbxmoods.machineA.json --merge-source mbxmoods.machineB.json --merge-output mbxmoods.json
```

## Synthetic Library Generation (Test Tooling)

Generate a synthetic MusicBee library from a prepared catalog of real MusicBrainz metadata and AcousticBrainz acoustic features:

```cmd
REM Dry run — preview what would be created
truedat.exe --synthesize --catalog data\synthlib-catalog.jsonl.gz --count 100 --dry-run

REM Generate 430,000 synthetic tracks
truedat.exe --synthesize --catalog data\synthlib-catalog.jsonl.gz --synth-output D:\synthlib --count 430000

REM Generate and merge mood data into an existing mbxmoods.json
truedat.exe --synthesize --catalog data\synthlib-catalog.jsonl.gz --synth-output D:\synthlib --synth-moods C:\path\to\mbxmoods.json
```

### Synthesize Options

```
  --synthesize            Generate a synthetic MusicBee library from a catalog
  --catalog <path>        Path to catalog JSONL (.jsonl or .jsonl.gz)
  --synth-output <dir>    Output directory for synthesized library
  --count <n>             Number of tracks to generate (default: 430000)
  --album-ratio <r>       Fraction of tracks in albums vs singles (default: 0.5)
  --synth-moods <path>    Path to existing mbxmoods.json to merge into
  --seed <n>              Random seed for reproducibility (default: 42)
  --dry-run               Preview without writing files
```

Each generated track is a stub MP3 (~12 KB, 3 seconds of silence) with real ID3 metadata (Title, Artist, Album, Genre, Year, BPM) written via TagLib#. Tracks are organized as `{output}\{Artist}\{Album}\{NN} {Title}.mp3`.

### Identifying Synthetic Tracks

All synthetic tracks are marked for easy identification:

- **ID3 Grouping tag** = `Synthetic` — filterable in MusicBee column browser
- **ID3 Comment tag** = `SYNTH:{seed}:{mbid}` — searchable, links to source recording MBID
- **File path** — all generated files live under the `--synth-output` directory

### Building the Catalog

The catalog is built using a developer-only Python script that joins AcousticBrainz acoustic features with MusicBrainz metadata. This downloads ~21 GB of data dumps:

```cmd
cd src
pip install -r requirements-catalog.txt
python catalog-prep.py --download --build --stats
```

Output: `data/synthlib-catalog.jsonl.gz` — a gzipped JSON Lines file where each line contains one track with full metadata and 15 Essentia acoustic features.

### Playbook: Expanding a Real Library for Scale Testing

End-to-end steps to combine your real library with synthetic tracks for testing MBXHub at scale.

**1. Build the catalog** (one-time, ~21 GB download):

```cmd
cd src
pip install -r requirements-catalog.txt
python catalog-prep.py --download --build --stats
```

**2. Generate synthetic tracks with mood data merged into your existing moods file:**

```cmd
truedat.exe --synthesize --catalog data\synthlib-catalog.jsonl.gz --synth-output D:\synthlib --count 100000 --synth-moods C:\MusicBee\mbxmoods.json
```

This creates 100k stub MP3s with real metadata and writes their acoustic features directly into your `mbxmoods.json`. Real analysis data is preserved — synthetic entries are added alongside existing entries in a single file.

**3. Add the synthetic folder to MusicBee:**

In MusicBee, go to **Edit > Preferences > Library** and add `D:\synthlib` as a monitored folder. MusicBee scans it and includes synthetic tracks in your unified library and iTunes XML export.

**4. Test at scale:**

MBXHub loads one `mbxmoods.json` containing both real and synthetic tracks. AutoQ, mood channels, diversity quotas, and performance can now be exercised against a much larger library.

**5. Clean up when done:**

Remove `D:\synthlib` from MusicBee's monitored folders. Orphaned synthetic entries in `mbxmoods.json` are harmless (MBXHub ignores paths not in the XML), or delete `D:\synthlib` and restore your moods file from backup.

Synthetic tracks are identifiable by their `Grouping = Synthetic` ID3 tag — use MusicBee's column browser or a smart playlist (`Grouping IS Synthetic`) to filter them.

## Mood Seeding from AcousticBrainz

Seed `mbxmoods.json` with pre-computed acoustic features from the AcousticBrainz catalog, matched by normalized artist+title. Faster than running Essentia on every file — instant mood data for matched tracks.

```cmd
REM Seed moods for your library from the AcousticBrainz catalog
truedat.exe "iTunes Music Library.xml" --seed-moods --seed-catalog data\synthlib-catalog.jsonl.gz

REM Specify a target moods file
truedat.exe "iTunes Music Library.xml" --seed-moods --seed-catalog data\synthlib-catalog.jsonl.gz --seed-target C:\path\to\mbxmoods.json
```

### Seed Options

```
  --seed-moods              Seed mbxmoods.json from AcousticBrainz catalog
  --seed-catalog <path>     Path to synthlib-catalog.jsonl.gz
  --seed-target <path>      Target mbxmoods.json path (default: next to library XML)
```

Seeded entries have `_confidence: 0.6` and `_source: "ab-metadata"`. Local Essentia analysis (confidence 1.0) is never overwritten — seeding only adds new entries or upgrades lower-confidence data.

## Installation

Place `truedat.exe` and the required tools in the same folder. No additional runtime needed on Windows 10+.

### Dependencies

Truedat calls these tools as subprocesses. Place them alongside `truedat.exe` or on PATH.

| Tool | Enables | License | Source |
| ---- | ------- | ------- | ------ |
| [Essentia](https://essentia.upf.edu/) `essentia_streaming_extractor_music.exe` | Mood analysis (default mode) | AGPL-3.0 | [Essentia](https://github.com/MTG/essentia) / [x64 build](https://github.com/halrad-com/Truedat/tree/main/essentia-build/output-x64) / [dist](https://github.com/halrad-com/Truedat/tree/main/dist/truedat) |
| [Essentia](https://essentia.upf.edu/) `essentia_streaming_md5.exe` | `--fingerprint` and `--md5-only` modes only (decoded-PCM MD5). Not used by default mood scan. | AGPL-3.0 | [Essentia](https://github.com/MTG/essentia) / [x64 build](https://github.com/halrad-com/Truedat/tree/main/essentia-build/output-x64) / [dist](https://github.com/halrad-com/Truedat/tree/main/dist/truedat) |
| [Chromaprint](https://acoustid.org/chromaprint) `fpcalc.exe` | `--fingerprint` and `--quick-fingerprint` modes only (perceptual fingerprint). Not used by default mood scan. | LGPL-2.1+ | [AcoustID](https://acoustid.org/chromaprint) |
| [Essentia](https://essentia.upf.edu/) `essentia_standard_chromaprinter.exe` | `--fingerprint` mode (chromaprint persisted to `mbxhub-fingerprints.json`) | AGPL-3.0 | [Essentia](https://github.com/MTG/essentia) / [x64 build](https://github.com/halrad-com/Truedat/tree/main/essentia-build/output-x64) / [dist](https://github.com/halrad-com/Truedat/tree/main/dist/truedat) |
| [FFmpeg](https://ffmpeg.org/) `ffmpeg.exe` | Multi-channel audio downmix | GPL-3.0+ | [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) / [deps](https://github.com/halrad-com/Truedat/tree/truedat-deps) |
| [FFmpeg](https://ffmpeg.org/) `ffprobe.exe` | `--details` audio probing | GPL-3.0+ | [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) / [deps](https://github.com/halrad-com/Truedat/tree/truedat-deps) |

All tools are optional — truedat runs without them but the corresponding features are unavailable. Only install the tools for the modes you need. Bundle `essentia_streaming_md5.exe` and `fpcalc.exe` alongside `truedat.exe` so every analysis run emits the full identity tier set (`fileMd5` + `audioMd5` + `chromaprint`) without a separate `--fingerprint` pass. Custom x64 Essentia builds are in [`essentia-build/`](essentia-build/), ready to use from [`dist/truedat/`](https://github.com/halrad-com/Truedat/tree/main/dist/truedat).

### Essentia Builds

All Essentia tools are custom 64-bit builds from source. See [`essentia-build/`](essentia-build/) for build scripts and documentation. The x64 builds handle large files that exceed the 2 GB address space limit of 32-bit binaries.

### Building from Source

```cmd
build-truedat.cmd
```

Creates `dist/truedat/truedat.exe` (single file, ~1 MB). Requires .NET SDK 8.0+.

## Extracted Features

Truedat extracts 55 audio features per track from Essentia's output — 15 core features that feed valence/arousal, plus 40 extended descriptors that MBXHub persists for richer downstream scoring (sub-genre profiling, loudness normalisation, fingerprint-free clustering, etc.).

Extended features are emitted as nullable JSON fields — absent/NaN Essentia paths become omitted keys, not zeros. Older mbxmoods.json entries that pre-date the extended set round-trip cleanly as `null`s.

### Core — Arousal-related (energy/intensity)

| Feature            | Essentia Path                         | What It Measures                  |
| ------------------ | ------------------------------------- | --------------------------------- |
| BPM                | `rhythm.bpm`                          | Tempo in beats per minute         |
| Onset rate         | `rhythm.onset_rate`                   | Percussive events per second      |
| Loudness           | `lowlevel.loudness_ebu128.integrated` | Perceived loudness (EBU R128, dB) |
| Spectral flux      | `lowlevel.spectral_flux.mean`         | Rate of spectral change           |
| Spectral RMS       | `lowlevel.spectral_rms.mean`          | Raw energy level                  |
| Zero-crossing rate | `lowlevel.zerocrossingrate.mean`      | Noise/distortion indicator        |
| Danceability       | `rhythm.danceability`                 | Rhythmic regularity (0-1)         |

### Core — Valence-related (positivity/happiness)

| Feature            | Essentia Path                        | What It Measures                  |
| ------------------ | ------------------------------------ | --------------------------------- |
| Key                | `tonal.key_edma.key`                 | Musical key (C, D, E...)          |
| Mode               | `tonal.key_edma.scale`               | Major (bright) vs minor (dark)    |
| Spectral centroid  | `lowlevel.spectral_centroid.mean`    | Brightness/timbre (Hz)            |
| Spectral flatness  | `lowlevel.spectral_flatness_db.mean` | Tonal vs noise-like               |
| Dissonance         | `lowlevel.dissonance.mean`           | Harmonic tension                  |
| Pitch salience     | `lowlevel.pitch_salience.mean`       | Harmonic clarity (HNR proxy)      |
| Chord changes rate | `tonal.chords_changes_rate`          | Rate of harmonic movement         |
| MFCCs              | `lowlevel.mfcc.mean`                 | 13-coefficient timbre fingerprint |

### Extended — Loudness envelope (EBU R128)

| JSON key              | Essentia Path                                 | What It Measures                                       |
| --------------------- | --------------------------------------------- | ------------------------------------------------------ |
| `dynamicRange`        | `lowlevel.loudness_ebu128.loudness_range`     | EBU R128 loudness range (LRA) in LU — quiet↔loud span  |
| `loudnessMomentary`   | `lowlevel.loudness_ebu128.momentary.mean`     | Mean momentary loudness (400 ms gate), LU              |
| `loudnessShortTerm`   | `lowlevel.loudness_ebu128.short_term.mean`    | Mean short-term loudness (3 s gate), LU                |
| `replayGain`          | `metadata.audio_properties.replay_gain`       | ReplayGain adjustment for normalised playback, dB      |

### Extended — Silence profile

Fraction of analysis frames whose RMS falls below the given threshold — higher = sparser / more silent content.

| JSON key           | Essentia Path                      | Threshold |
| ------------------ | ---------------------------------- | --------- |
| `silenceRate20dB`  | `lowlevel.silence_rate_20dB.mean`  | -20 dB    |
| `silenceRate30dB`  | `lowlevel.silence_rate_30dB.mean`  | -30 dB    |
| `silenceRate60dB`  | `lowlevel.silence_rate_60dB.mean`  | -60 dB    |

### Extended — Spectral shape

| JSON key                | Essentia Path                                    | What It Measures                                            |
| ----------------------- | ------------------------------------------------ | ----------------------------------------------------------- |
| `spectralRolloff`       | `lowlevel.spectral_rolloff.mean`                 | Hz below which 85% of spectral energy sits (bright↔dark)    |
| `spectralComplexity`    | `lowlevel.spectral_complexity.mean`              | Count of significant spectral peaks per frame               |
| `spectralEntropy`       | `lowlevel.spectral_entropy.mean`                 | Shannon entropy of normalised spectrum (noise↔tone)         |
| `spectralKurtosis`      | `lowlevel.spectral_kurtosis.mean`                | Peakedness of spectral distribution                         |
| `spectralSkewness`      | `lowlevel.spectral_skewness.mean`                | Asymmetry of spectral distribution                          |
| `spectralSpread`        | `lowlevel.spectral_spread.mean`                  | 2nd-moment spread around centroid                           |
| `spectralStrongPeak`    | `lowlevel.spectral_strongpeak.mean`              | Prominence of dominant spectral peak                        |
| `spectralDecrease`      | `lowlevel.spectral_decrease.mean`                | Average slope; negative ⇒ energy concentrated at low freqs  |
| `spectralEnergy`        | `lowlevel.spectral_energy.mean`                  | Total spectral energy                                       |
| `spectralEnergyLow`     | `lowlevel.spectral_energyband_low.mean`          | Energy in low band (Essentia split)                         |
| `spectralEnergyMidLow`  | `lowlevel.spectral_energyband_middle_low.mean`   | Energy in mid-low band                                      |
| `spectralEnergyMidHigh` | `lowlevel.spectral_energyband_middle_high.mean`  | Energy in mid-high band                                     |
| `spectralEnergyHigh`    | `lowlevel.spectral_energyband_high.mean`         | Energy in high band                                         |
| `hfc`                   | `lowlevel.hfc.mean`                              | High-frequency content — cymbals, sibilance, brightness     |

### Extended — Psychoacoustic bands

Shape statistics over perceptually-spaced filterbanks. Same five statistics across three scales: **Bark** (27 critical bands), **ERB** (40 equivalent-rectangular-bandwidth bands), **Mel** (40 mel-scaled bands).

| JSON key (per scale)       | Essentia Path                              | What It Measures                                           |
| -------------------------- | ------------------------------------------ | ---------------------------------------------------------- |
| `{bark,erb,mel}Crest`      | `lowlevel.{bark,erb,mel}bands_crest.mean`  | Peak-to-mean ratio — high ⇒ tonal/peaky within the band    |
| `{bark,erb,mel}Flatness`   | `lowlevel.{bark,erb,mel}bands_flatness_db.mean` | Geometric/arithmetic mean ratio in dB (noisy↔tonal)    |
| `{bark,erb,mel}Kurtosis`   | `lowlevel.{bark,erb,mel}bands_kurtosis.mean` | Peakedness of the band distribution                      |
| `{bark,erb,mel}Skewness`   | `lowlevel.{bark,erb,mel}bands_skewness.mean` | Asymmetry of the band distribution                       |
| `{bark,erb,mel}Spread`     | `lowlevel.{bark,erb,mel}bands_spread.mean`  | Second-moment spread across the bands                     |

### Extended — Rhythm & tonal aggregates

| JSON key         | Essentia Path                  | What It Measures                                                    |
| ---------------- | ------------------------------ | ------------------------------------------------------------------- |
| `beatsLoudness`  | `rhythm.beats_loudness.mean`   | Mean loudness at beat positions — kick/snare intensity              |
| `chordsStrength` | `tonal.chords_strength.mean`   | Mean chord-detector confidence (0-1)                                |
| `hpcpCrest`      | `tonal.hpcp_crest.mean`        | Crest of 12-bin harmonic pitch class profile — tonal focus (dB)     |
| `hpcpEntropy`    | `tonal.hpcp_entropy.mean`      | Entropy of HPCP — high ⇒ atonal/chromatic, low ⇒ diatonic           |

## Output Format

`mbxmoods.json`:

```json
{
  "version": "1.0",
  "generatedAt": "2026-02-06T...",
  "trackCount": 70000,
  "tracks": {
    "C:\\Music\\Artist\\Song.mp3": {
      "trackId": 123,
      "artist": "Artist",
      "title": "Song",
      "album": "Album",
      "genre": "Rock",
      "bpm": 128.0,
      "key": "C",
      "mode": "major",
      "spectralCentroid": 2456.3,
      "spectralFlux": 0.2134,
      "loudness": -8.52,
      "danceability": 0.7821,
      "onsetRate": 3.45,
      "zeroCrossingRate": 0.0892,
      "spectralRms": 0.1245,
      "spectralFlatness": 0.2341,
      "dissonance": 0.3456,
      "pitchSalience": 0.6789,
      "chordsChangesRate": 0.8901,
      "mfcc": [-234.5, 45.2, -12.3, 8.7, -3.1, 1.2, -0.8, 0.5, -0.3, 0.2, -0.1, 0.1, -0.05],
      "dynamicRange": 7.3,
      "dynamicRangeSource": "essentia-lra",
      "loudnessMomentary": -11.82, "loudnessShortTerm": -10.47, "replayGain": -8.4,
      "silenceRate20dB": 0.02, "silenceRate30dB": 0.07, "silenceRate60dB": 0.21,
      "spectralRolloff": 4211.5, "spectralComplexity": 14.2, "spectralEntropy": 7.86,
      "spectralKurtosis": 21.1, "spectralSkewness": 3.42, "spectralSpread": 2.18e6,
      "spectralStrongPeak": 1.7, "spectralDecrease": -1.8e-8,
      "spectralEnergy": 0.0041, "spectralEnergyLow": 0.0015, "spectralEnergyMidLow": 0.0012,
      "spectralEnergyMidHigh": 0.0009, "spectralEnergyHigh": 0.0005,
      "hfc": 112.4,
      "barkCrest": 9.4, "barkFlatness": -9.1, "barkKurtosis": 7.3, "barkSkewness": 2.1, "barkSpread": 21.3,
      "erbCrest": 11.2, "erbFlatness": -8.7, "erbKurtosis": 5.8, "erbSkewness": 1.9, "erbSpread": 18.2,
      "melCrest": 10.6, "melFlatness": -8.4, "melKurtosis": 6.1, "melSkewness": 2.0, "melSpread": 19.5,
      "beatsLoudness": -14.1, "chordsStrength": 0.61, "hpcpCrest": 6.2, "hpcpEntropy": 2.87,
      "lastModified": "2025-12-01T00:00:00.0000000Z",
      "analysisDuration": 4.2,
      "fileMd5": "d41d8cd98f00b204e9800998ecf8427e",
      "audioMd5": "8a7d9c0b1e2f3a4b5c6d7e8f9a0b1c2d"
    }
  }
}
```

Raw features are stored so MBXHub can compute valence/arousal at runtime with tunable weights — no re-scan needed to adjust the formulas. The 40 extended fields are persisted for future downstream scoring (sub-genre profiling, loudness normalisation, clustering). Every extended field is nullable; legacy entries produced before the extended set was added simply omit those keys rather than storing zeros. The `analysisDuration` field records how long Essentia took to analyze each track (in seconds).

`fileMd5` (MD5 of the file bytes), `fingerprint.v1` (cheap composite — TagLib parse + 64 KB invariant-region MD5), and `audioStreamSha256` (streaming SHA-256 over the audio invariant region — content-stable across tag edits and file moves) are all computed concurrently with the Essentia feature extraction in pure-managed code. No subprocesses, no path-escape exposure, no codepage drama. Wall-clock per track is roughly `max(analysis, slowest-hash)` rather than the sum, with Essentia dominating. `audioStreamSha256` is the primary content thumbprint — it survives file moves, renames, and tag edits, and works identically on NTFS, exFAT, and any path Windows can open.

### Fingerprint Output

`mbxhub-fingerprints.json`:

```json
{
  "version": "1.0",
  "generatedAt": "2026-02-14T...",
  "trackCount": 70000,
  "tracks": {
    "C:\\Music\\Artist\\Song.mp3": {
      "trackId": 123,
      "artist": "Artist",
      "title": "Song",
      "album": "Album",
      "genre": "Rock",
      "chromaprint": "AQADtNIyhZKo...",
      "duration": 245,
      "md5": "a1b2c3d4e5f6...",
      "lastModified": "2026-01-15T00:00:00.0000000Z"
    }
  }
}
```

Fields are omitted when the tool wasn't run (`--chromaprint-only` omits `md5`, `--md5-only` omits `chromaprint`/`duration`). Existing cached entries with both fields are preserved even when re-running with a subset flag.

### Details Output

`mbxhub-details.json` (generated by `--details`):

```json
{
  "version": "1.0",
  "generatedAt": "2026-02-14T...",
  "trackCount": 70000,
  "tracks": {
    "C:\\Music\\Artist\\Song.mp3": {
      "trackId": 123,
      "artist": "Artist",
      "title": "Song",
      "codec": "mp3",
      "channels": 2,
      "sampleRate": 44100,
      "bitRate": 320,
      "bitDepth": 0,
      "duration": 245.3,
      "format": "mp3",
      "sizeMb": 9.4,
      "lastProbed": "2026-02-14T00:00:00.0000000Z",
      "lastModified": "2026-01-15T00:00:00.0000000Z"
    }
  }
}
```

Audio metadata extracted via ffprobe. `bitRate` is in kbps, `bitDepth` is 0 for lossy codecs that don't have a fixed bit depth (e.g. MP3, AAC).

## How Mood Vectors Work

Based on Russell's circumplex model of emotion. Each track gets a 2D coordinate `[valence, arousal]`:

```
                    High Arousal
                         |
         Angry/Tense     |     Energetic/Happy
                         |
  Low Valence -----------+----------- High Valence
                         |
         Sad/Melancholy  |     Calm/Relaxed
                         |
                    Low Arousal
```

**Valence** = weighted combination of 8 features:
mode (0.25), dissonance (0.15), spectral centroid (0.15), spectral flatness (0.10), pitch salience (0.10), danceability (0.10), MFCC2 (0.10), chord changes (0.05)

**Arousal** = weighted combination of 7 features:
BPM (0.20), onset rate (0.15), spectral RMS (0.15), loudness (0.15), spectral flux (0.15), zero-crossing rate (0.10), danceability (0.10)

All weights are configurable in MBXHub's `autoQ.estimation` settings.

## Visualization

```cmd
python src/visualize.py mbxmoods.json
```

Scatter plot of your library's mood distribution.

## iTunes Music Library XML

Truedat uses the iTunes Music Library XML format as its input. MusicBee can export your library in this format:

1. In MusicBee, go to **Edit > Preferences > Library**
2. Enable **"iTunes Music Library.xml"** export
3. MusicBee writes `iTunes Music Library.xml` to your library folder, updating it automatically

This is a standard XML format originally from iTunes/Apple Music that many music players support as an export option.

## Integration with MBXHub

[Features - MBXHub](https://mbxhub.com/features.html#autoq)

[Download - MBXHub](https://mbxhub.com/download.html)

Truedat generates the mood data that MBXHub's AutoQ engine consumes. The workflow:

1. **Truedat** scans your library using the iTunes XML export and produces `mbxmoods.json`
2. **MBXHub** loads the file at startup and recomputes valence/arousal using its current weight settings
3. **AutoQ** uses mood vectors for mood-aware shuffle, reactions, and influence scoring

Place `mbxmoods.json` in your MusicBee Library folder (sibling to `AppData`) or in `%APPDATA%\MusicBee\MBXHub\`. MBXHub searches both locations automatically.

## License

- **truedat.exe**: MIT - Copyright (c) 2026 Halrad LLC
- **System.Text.Json**: MIT - Copyright (c) .NET Foundation (merged into exe)
- **TagLibSharp**: LGPL-2.1 - [TagLibSharp](https://github.com/mono/taglib-sharp) (merged into exe, used for synthetic track metadata)
- **Essentia tools**: AGPL-3.0 - [Essentia](https://github.com/MTG/essentia) by Music Technology Group, Universitat Pompeu Fabra
- **FFmpeg tools**: GPL-3.0+ - [FFmpeg](https://ffmpeg.org/) (optional dependency)

See [LICENSE](LICENSE) for details.

## Acknowledgments

This software uses [Essentia](https://essentia.upf.edu/), an open-source C++ library for audio analysis developed by the Music Technology Group at Universitat Pompeu Fabra.

If you use this in academic work, please cite:

> Bogdanov, D., Wack N., Gomez E., Gulati S., Herrera P., Mayor O., et al. (2013).
> ESSENTIA: an Audio Analysis Library for Music Information Retrieval.
> International Society for Music Information Retrieval Conference (ISMIR'13).

- [Essentia on GitHub](https://github.com/MTG/essentia)
- [Essentia Documentation](https://essentia.upf.edu/documentation.html)
