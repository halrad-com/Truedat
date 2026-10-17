# Truedat - Software Bill of Materials (SBOM)

## Project

| Field | Value |
|-------|-------|
| Name | Truedat (Music Mood Extractor & Fingerprinter) |
| Version | 1.0.0 (assembly); release v0.5.5.3-EV2.1 — `truedat --version` prints both, e.g. `1.0.0.0-v0.5.5.3-EV2.1` |
| License | MIT |
| Framework | .NET Framework 4.8 |
| Output | `truedat.exe` (single file, ILRepack merged) |
| Code signing | `truedat.exe` is Authenticode-signed by **Halrad LLC** (SHA-256, RFC3161 timestamp from Sectigo) via `dist/sign.cmd`. The third-party binaries shipped beside it (Essentia, FFmpeg) are not signed by Halrad. |

## Components

### truedat.exe

| Component | Version | License | Purpose |
|-----------|---------|---------|---------|
| .NET Framework | 4.8 | MIT | Runtime (ships with Windows 10/11) |
| System.Security.Cryptography | 4.8 (BCL) | MIT | MD5 and SHA-256 used by file-MD5, audio-head MD5 (`fingerprint.v1`), and audio-stream SHA-256 (`audioStreamSha256`); SHA-NI hardware-accelerated on supported CPUs (framework assembly reference) |
| System.Text.Json | 8.0.5 | MIT | JSON serialization (merged into exe via ILRepack) |
| winsqlite3.dll | OS-provided (Windows 10 1803+) | Public Domain (SQLite) | The v2 store, via `DllImport("winsqlite3.dll")` — no SQLite binary is shipped or built, and no NuGet provider is used. Probed at store open; a missing or too-old library refuses with a message instead of crashing (minimum 3.40). Measured 3.51.1 on Windows 10 and 11; compile options differ per OS build, so optional features are probed, never assumed |
| TagLibSharp | 2.3.0 | LGPL-2.1 | ID3 tag writing for synthetic library generation; audio-property parsing and `InvariantStartPosition/EndPosition` locator for Phase 2 identity signals (merged into exe via ILRepack) |

**Build tools:**

| Tool | Version | Purpose |
|------|---------|---------|
| ILRepack | 2.0.34.2 | Merge assemblies into single exe |
| signtool | Windows SDK (via VS 2022 `VsDevCmd.bat`) | Authenticode-sign `truedat.exe` after build (`dist/sign.cmd`) |

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

| File | Architecture | Build | Source |
|------|-------------|-------|--------|
| `essentia_streaming_extractor_music.exe` | x64 | `output-x64.5` | Built from `essentia-build/` — mood analysis. `.5` adds a binary frame-level output (`--frames-bin`, `framesInclude`) that `--edges` folds, and a build id in the output; the 2/3-argument invocations are unchanged from `.4` |

The running build is identified by MD5 and named in `truedat --version` / the scan's tool check
(`KnownExtractors` in `Truedat/Program.cs`); `--edges` refuses an older one rather than silently
skipping the fold.

| File | MD5 | SHA-256 |
|------|-----|---------|
| `essentia_streaming_extractor_music.exe` (`output-x64.5`) | `6f47bdfcaadfd4f510c5e1621c07945d` | `5114426816ef64f11cb3b8c53844dc324cf420272ba9d757e148bc7edf224a98` |

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

The copies in `dist/truedat/` hash differently from the `truedat-deps` records above, so the two
are not the same files; whichever is authoritative, they need reconciling:

| File (as shipped in `dist/truedat/`) | SHA-256 |
|------|---------|
| `ffmpeg.exe` | `59c8a17f012f148bdce12b93f8cba2d37f281a3e0e856d505608ca5eaa4520ed` |
| `ffprobe.exe` | `2fac541ccf404a600c62c9f7be14600dd98ae5679defb31f5ed2fe71e3a388a8` |

**Licensing:** This FFmpeg build is compiled with `--enable-gpl`, making the resulting binaries GPL-3.0+. See https://ffmpeg.org/legal.html for full details.

**Note:** FFmpeg is an optional external dependency. Without it, multi-channel audio files are skipped with a warning and the bitUsage / HF-analysis fields are omitted. Truedat itself (MIT) does not link against FFmpeg — it invokes the executables as subprocesses.

## Output Files

### Analysis Mode

| File | Description |
|------|-------------|
| `mbxmoods.json` | Mood vectors and the raw Essentia feature set per track (core always present, later waves nullable for back-compat; README's *Feature set* section carries the exact counts and how to re-measure them), plus identity fields `fileMd5` (maintained only with `--file-md5`), `fingerprint.v1`, `audioStreamSha256` (each nullable, omitted when missing); all hashes run concurrently with Essentia per track and are pure-managed (no subprocess). Legacy `audioMd5` / `chromaprint` keys from old scans are ignored on read and stripped by `--migrate`. |
| `mbxmoods.<host>.json` | Output of `--chunk M/N` — hostname-suffixed shard. Each machine in a chunked scan writes its own shard; combine with `--merge-moods` for a unified file. |
| `mbxmoods-errors.csv` | Failed tracks with error reasons (mood analysis). Suffixed `mbxmoods-errors.<host>.csv` under `--chunk`. A file's row is removed when a later scan puts it in the catalog. |
| `mbxmoods-review.json` | Review ledger: every file the catalog does not hold (excluded / skipped / failed), with the reason. A `tags` failure names what TagLib threw or returned. A file's record is removed when a later scan puts it in the catalog. Listed by `--list-review`. |
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
| stdout (JSON) | Single track's Essentia feature set as JSON (nullable beyond the core; counts in README's *Feature set* section) |

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
