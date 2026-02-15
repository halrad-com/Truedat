# Truedat - Music Mood Extractor & Fingerprinter

Audio feature extraction tool that generates mood vectors (valence/arousal) and audio fingerprints for music libraries using [Essentia](https://essentia.upf.edu/). Extracts 15 audio features per track, maps every song onto a 2D emotion space, and optionally generates perceptual fingerprints and audio-data hashes.

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
- **15 raw Essentia features** stored per track for runtime recomputation

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
  --retry-errors          Re-attempt all previously failed files (clears error log)
  --migrate               Strip legacy valence/arousal fields from mbxmoods.json (creates backup)
  --fingerprint           Run fingerprint mode (chromaprint + md5) → mbxhub-fingerprints.json
  --chromaprint-only      Fingerprint mode: only run chromaprint (skip md5)
  --md5-only              Fingerprint mode: only run audio md5 (skip chromaprint)
  --details               Use ffprobe → mbxhub-details.json (implies --fingerprint)
  --audit                 Write all console output to truedat.log (for debugging)
  --check-filenames       Scan for filenames with characters that break Essentia tools
```

**Optional:** Place `ffmpeg.exe` and `ffprobe.exe` alongside `truedat.exe` (or on PATH) to enable auto-downmix of multi-channel (5.1+) audio files and the `--details` probe mode. Without ffmpeg, multi-channel files are skipped with a warning.

### Large Libraries

For large libraries (50K+ tracks), expect multi-day scans for mood analysis. Fingerprinting is much faster. Both modes are designed for this:

- **Incremental** - Skips tracks already processed (by file path + last-modified timestamp)
- **Resumable** - Stop and restart anytime. Progress is saved every 25 analyzed tracks.
- **ETA tracking** - Shows per-track rate and estimated completion time
- **Error resilience** - Failed tracks logged to errors CSV, skipped on retry

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
```

## Installation

Place `truedat.exe` and the required tools in the same folder. No additional runtime needed on Windows 10+.

### Dependencies

Truedat calls these tools as subprocesses. Place them alongside `truedat.exe` or on PATH.

| Tool | Enables | License | Source |
| ---- | ------- | ------- | ------ |
| [Essentia](https://essentia.upf.edu/) `essentia_streaming_extractor_music.exe` | Mood analysis (default mode) | AGPL-3.0 | [Essentia](https://github.com/MTG/essentia) / [x64 build](https://github.com/halrad-com/Truedat/tree/main/essentia-build/output-x64) / [dist](https://github.com/halrad-com/Truedat/tree/main/dist/truedat) |
| [Essentia](https://essentia.upf.edu/) `essentia_standard_chromaprinter.exe` | `--fingerprint` (chromaprint) | AGPL-3.0 | [Essentia](https://github.com/MTG/essentia) / [x64 build](https://github.com/halrad-com/Truedat/tree/main/essentia-build/output-x64) / [dist](https://github.com/halrad-com/Truedat/tree/main/dist/truedat) |
| [Essentia](https://essentia.upf.edu/) `essentia_streaming_md5.exe` | `--fingerprint` (md5) | AGPL-3.0 | [Essentia](https://github.com/MTG/essentia) / [x64 build](https://github.com/halrad-com/Truedat/tree/main/essentia-build/output-x64) / [dist](https://github.com/halrad-com/Truedat/tree/main/dist/truedat) |
| [FFmpeg](https://ffmpeg.org/) `ffmpeg.exe` | Multi-channel audio downmix | GPL-3.0+ | [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) / [deps](https://github.com/halrad-com/Truedat/tree/truedat-deps) |
| [FFmpeg](https://ffmpeg.org/) `ffprobe.exe` | `--details` audio probing | GPL-3.0+ | [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) / [deps](https://github.com/halrad-com/Truedat/tree/truedat-deps) |

All tools are optional — truedat runs without them but the corresponding features are unavailable. Only install the tools for the modes you need. Custom x64 Essentia builds are in [`essentia-build/`](essentia-build/), ready to use from [`dist/truedat/`](https://github.com/halrad-com/Truedat/tree/main/dist/truedat).

### Essentia Builds

All Essentia tools are custom 64-bit builds from source. See [`essentia-build/`](essentia-build/) for build scripts and documentation. The x64 builds handle large files that exceed the 2 GB address space limit of 32-bit binaries.

### Building from Source

```cmd
build-all.cmd
```

Creates `dist/truedat/truedat.exe` (single file, ~1 MB). Requires .NET SDK 8.0+.

## Extracted Features

Truedat extracts 15 audio features per track from Essentia's output:

### Arousal-related (energy/intensity)

| Feature            | Essentia Path                         | What It Measures                  |
| ------------------ | ------------------------------------- | --------------------------------- |
| BPM                | `rhythm.bpm`                          | Tempo in beats per minute         |
| Onset rate         | `rhythm.onset_rate`                   | Percussive events per second      |
| Loudness           | `lowlevel.loudness_ebu128.integrated` | Perceived loudness (EBU R128, dB) |
| Spectral flux      | `lowlevel.spectral_flux.mean`         | Rate of spectral change           |
| Spectral RMS       | `lowlevel.spectral_rms.mean`          | Raw energy level                  |
| Zero-crossing rate | `lowlevel.zerocrossingrate.mean`      | Noise/distortion indicator        |
| Danceability       | `rhythm.danceability`                 | Rhythmic regularity (0-1)         |

### Valence-related (positivity/happiness)

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
      "lastModified": "2025-12-01T00:00:00.0000000Z",
      "analysisDuration": 4.2
    }
  }
}
```

Raw features are stored so MBXHub can compute valence/arousal at runtime with tunable weights - no re-scan needed to adjust the formulas. The `analysisDuration` field records how long Essentia took to analyze each track (in seconds).

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
