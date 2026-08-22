# Truedat - Software Bill of Materials (SBOM)

## Project

| Field | Value |
|-------|-------|
| Name | Truedat (Music Mood Extractor & Fingerprinter) |
| Version | 1.0.0 |
| License | MIT |
| Framework | .NET Framework 4.8 |
| Output | `truedat.exe` (single file, ILRepack merged) |

## Components

### truedat.exe

| Component | Version | License | Purpose |
|-----------|---------|---------|---------|
| .NET Framework | 4.8 | MIT | Runtime (ships with Windows 10/11) |
| System.Security.Cryptography | 4.8 (BCL) | MIT | MD5 and SHA-256 used by file-MD5, audio-head MD5 (`fingerprint.v1`), and audio-stream SHA-256 (`audioStreamSha256`); SHA-NI hardware-accelerated on supported CPUs (framework assembly reference) |
| System.Text.Json | 8.0.5 | MIT | JSON serialization (merged into exe via ILRepack) |
| TagLibSharp | 2.3.0 | LGPL-2.1 | ID3 tag writing for synthetic library generation; audio-property parsing and `InvariantStartPosition/EndPosition` locator for Phase 2 identity signals (merged into exe via ILRepack) |

**Build tools:**

| Tool | Version | Purpose |
|------|---------|---------|
| ILRepack | 2.0.34.2 | Merge assemblies into single exe |

### essentia_streaming_extractor_music.exe (Built from Source)

The extractor is built from an Essentia source fork in `essentia-build/` targeting 64-bit Windows. The official Essentia builds are 32-bit only, which hits a 2GB memory limitation on large audio files. This fork cross-compiles x64 static binaries using MinGW on WSL2.

Source: [Essentia](https://essentia.upf.edu/) by Music Technology Group, Universitat Pompeu Fabra

| Component | Version | License | Purpose |
|-----------|---------|---------|---------|
| Essentia | 2.1-beta6-dev | AGPL-3.0 | Audio feature extraction (100+ features) |
| FFmpeg | 7.1.1 | LGPL-2.1+ | Audio file decoding |
| FFTW3 | 3.3.2 | GPL-2.0+ | FFT computation |
| Eigen3 | 3.3.7 | MPL-2.0 | Linear algebra (header-only) |
| LAME | 3.100 | LGPL-2.0 | MP3 encoding |
| libsamplerate | 0.1.8 | BSD-2-Clause | Audio resampling |
| zlib | 1.2.12 | zlib | Compression |
| TagLib | 1.11.1 | LGPL-2.1 | Audio metadata reading |
| libyaml | 0.1.5 | MIT | YAML/JSON output |

**Licensing:** Essentia is AGPL-3.0 for non-commercial use, with commercial licensing available from UPF. See https://essentia.upf.edu/licensing_information.html for full details including third-party dependency licenses.

**Build environment:**

| Tool | Version | Purpose |
|------|---------|---------|
| Ubuntu (WSL2) | 24.04 LTS | Build host |
| MinGW g++ | 13 | x86_64 cross-compiler (win32 threads) |
| Python | 3.12 | WAF build system |
| CMake | 3.28 | TagLib, Eigen builds |
| NASM | 2.16 | FFmpeg SIMD assembly |

**Build documentation:** `essentia-build/bringup.md` (full step-by-step), `essentia-build/tools-summary.md` (all 53 built tools)

**Pre-built dependencies:** `essentia-build/3rdparty-x64-deps.tar.gz` contains all 9 third-party libraries as static x64 `.a` files, enabling a build without recompiling dependencies from source.

### Built Tools

| File | Architecture | Source |
|------|-------------|--------|
| `essentia_streaming_extractor_music.exe` | x64 | Built from `essentia-build/` — mood analysis |

Place it in the same folder as `truedat.exe`.

### FFmpeg (Optional Dependency)

Truedat can optionally use FFmpeg for multi-channel audio downmixing, opus decode retry, the bitUsage / HF-analysis authenticity signals, and the standalone `--transcode` utility. FFmpeg is a separate download, not built by this project. Pre-built Windows binaries are available from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/).

Source: [FFmpeg](https://ffmpeg.org/)

| Component | Version | License | Purpose |
|-----------|---------|---------|---------|
| ffmpeg.exe | git master `2026-08-20-git-7d77562d2a-full_build` (gcc 16.1.0, MSYS2) | GPL-3.0+ | Audio downmixing, decode retry, bitUsage / HF analysis, `--transcode` |
| ffprobe.exe | git master `2026-08-20-git-7d77562d2a-full_build` (gcc 16.1.0, MSYS2) | GPL-3.0+ | `--transcode` source-property matching |

**Binaries:** the exact copies used are carried on the repo's `truedat-deps` branch (Git LFS), with a SHA-256 recorded per file in its `README.md`:

| File | SHA-256 |
|------|---------|
| `ffmpeg.exe` | `9c6b03ec0c5b5efc6c471ec81d3bdc99b50d88c0cdefbfe0cb6e1b55e28cd331` |
| `ffprobe.exe` | `307a1f169d1e3c0c099aa3eb384a5de53b1a489ccb9b9a4c2d3082b8bf1d2188` |

**Licensing:** This FFmpeg build is compiled with `--enable-gpl`, making the resulting binaries GPL-3.0+. See https://ffmpeg.org/legal.html for full details.

**Note:** FFmpeg is an optional external dependency. Without it, multi-channel audio files are skipped with a warning and the bitUsage / HF-analysis fields are omitted. Truedat itself (MIT) does not link against FFmpeg — it invokes the executables as subprocesses.

## Output Files

### Analysis Mode

| File | Description |
|------|-------------|
| `mbxmoods.json` | Mood vectors and 55 raw Essentia features per track (15 core + 40 extended, all nullable for back-compat), plus identity fields `fileMd5` (maintained only with `--file-md5`), `fingerprint.v1`, `audioStreamSha256` (each nullable, omitted when missing); all hashes run concurrently with Essentia per track and are pure-managed (no subprocess). Legacy `audioMd5` / `chromaprint` keys from old scans are ignored on read and stripped by `--migrate`. |
| `mbxmoods.<host>.json` | Output of `--chunk M/N` — hostname-suffixed shard. Each machine in a chunked scan writes its own shard; combine with `--merge-moods` for a unified file. |
| `mbxmoods-errors.csv` | Failed tracks with error reasons (mood analysis). Suffixed `mbxmoods-errors.<host>.csv` under `--chunk`. |
| `mbxmoods-verify.csv` | Output of `--verify` — per-entry integrity report (status: OK / DRIFT / MISSING / NO_HASH / ERROR). Tab-separated. Excludes OK rows to keep size small. |
| `truedat.log` | Console output log (when `--audit` is used) |

### Synthetic Library Mode (`--synthesize`)

| File | Description |
|------|-------------|
| `{output}/**/*.mp3` | Stub MP3 files with real ID3 metadata (artist, album, genre, year, BPM) |
| `{output}/mbxmoods.json` | Mood entries with 15 core Essentia features for all generated tracks (extended set only populated by live analysis) |
| `{output}/.synthetic-manifest.json` | Manifest for idempotent reruns (tracks generated, seed, settings) |

### Analyze Mode (`--analyze-file`, single-track)

| File | Description |
|------|-------------|
| stdout (JSON) | Single track's 55 Essentia features as JSON (15 core + 40 extended, nullable; consumed by MBXHub ScanWorker) |

### Batch Mode (`--file-list`)

| File | Description |
|------|-------------|
| stdout (JSON) | Summary with processed/failed counts, elapsed time, and error details (when `--json-output` or failures) |

### Hash-Only Mode (`--hash-only --level fingerprint\|stream`)

Identity-only passes (no Essentia). Requires `--file-list` + `--output <ndjson>`. Appends one identity envelope per file as NDJSON to the manifest path.

| File | Description |
|------|-------------|
| `<output>` | NDJSON manifest, one identity envelope per line |
| `mbxhub-hash-only-errors.csv` | Failed tracks, tab-separated `path\terror` (written next to the file list) |
| stderr | `[OK]` / `[FAIL]` / `[SKIP]` per file + run summary |

Each envelope carries `identity.fingerprint.v1` (composite: pathTail + fileSize + audio props + 64 KB invariant-region MD5) in both levels; `identity.audioStreamSha256` (streaming SHA-256 over the audio region) in `--level stream` only.

### Catalog Prep (`src/catalog-prep.py`, developer tool)

| File | Description |
|------|-------------|
| `data/synthlib-catalog.jsonl.gz` | Gzipped JSON Lines catalog of track metadata + acoustic features |
| `data/downloads.json` | Download manifest with SHA-256 integrity hashes |

## Developer Tools (Python, not shipped)

| Script | Purpose | Dependencies |
|--------|---------|-------------|
| `src/catalog-prep.py` | Build synthetic library catalog from MusicBrainz + AcousticBrainz data dumps | zstandard, requests, tqdm |
| `src/analyze.py` | Direct Essentia analysis via Python bindings | essentia, numpy |
| `src/visualize.py` | Scatter plot of mood distribution from mbxmoods.json | matplotlib |
| `src/libscan.py` | Library scanning and statistics | (stdlib only) |

Python dependencies for catalog-prep are listed in `src/requirements-catalog.txt`.

## Platform Support

| Platform | Status |
|----------|--------|
| Windows 10 (1903+) | Supported |
| Windows 11 | Supported |

## Security Considerations

- No network access at runtime
- Reads audio files (read-only) and iTunes Music Library XML
- Writes JSON output next to input XML file
- No telemetry or external services
- Child process arguments (ffmpeg, ffprobe, Essentia) are sanitized via `PathHelper.QuoteArg()` to prevent command injection from malicious file paths in the iTunes XML
- iTunes XML parser uses `DtdProcessing.Ignore` to prevent XXE entity expansion
- Video files and structurally non-analyzable inputs (DSD, remote stream URLs) are filtered out before processing; speech content (podcasts, talk) is analyzed by default and skipped only by an explicit exclusion rule
