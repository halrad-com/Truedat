# Truedat - Software Bill of Materials (SBOM)

## Project

| Field | Value |
|-------|-------|
| Name | Truedat (Music Mood Extractor) |
| Version | 1.0.0 |
| License | MIT |
| Framework | .NET Framework 4.8 |
| Output | `truedat.exe` (single file, ILRepack merged) |

## Components

### truedat.exe

| Component | Version | License | Purpose |
|-----------|---------|---------|---------|
| .NET Framework | 4.8 | MIT | Runtime (ships with Windows 10/11) |
| System.Text.Json | 8.0.5 | MIT | JSON serialization (merged into exe via ILRepack) |

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
| Chromaprint | 1.5.1 | LGPL-2.1+ | Audio fingerprinting |

**Licensing:** Essentia is AGPL-3.0 for non-commercial use, with commercial licensing available from UPF. See https://essentia.upf.edu/licensing_information.html for full details including third-party dependency licenses.

**Build environment:**

| Tool | Version | Purpose |
|------|---------|---------|
| Ubuntu (WSL2) | 24.04 LTS | Build host |
| MinGW g++ | 13 | x86_64 cross-compiler (win32 threads) |
| Python | 3.12 | WAF build system |
| CMake | 3.28 | TagLib, Chromaprint, Eigen builds |
| NASM | 2.16 | FFmpeg SIMD assembly |

**Build documentation:** `essentia-build/bringup.md` (full step-by-step), `essentia-build/tools-summary.md` (all 53 built tools)

**Pre-built dependencies:** `essentia-build/3rdparty-x64-deps.tar.gz` contains all 9 third-party libraries as static x64 `.a` files, enabling a build without recompiling dependencies from source.

### Distributed Files

| File | Architecture | Source |
|------|-------------|--------|
| `essentia_streaming_extractor_music.exe` | x64 | Built from `essentia-build/` (primary) |
| `essentia_streaming_extractor_music_x64.exe` | x64 | Same binary, explicit architecture name |
| `essentia_streaming_extractor_music_i686.exe` | x86 | Legacy 32-bit (from official Essentia builds) |
| `essentia_standard_chromaprinter.exe` | x64 | Built from `essentia-build/` — Chromaprint/AcoustID fingerprinting |
| `essentia_streaming_md5.exe` | x64 | Built from `essentia-build/` — Audio payload MD5 hashing |

All Essentia tools share the same dependency tree above. The fingerprint tools (`essentia_standard_chromaprinter.exe`, `essentia_streaming_md5.exe`) are placed in the same folder as `truedat.exe`.

### FFmpeg (Optional Dependency)

Truedat can optionally use FFmpeg for multi-channel audio downmixing and audio stream probing (`--details` mode). FFmpeg is a separate download, not built or distributed by this project. Pre-built Windows binaries are available from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/).

Source: [FFmpeg](https://ffmpeg.org/)

| Component | Version | License | Purpose |
|-----------|---------|---------|---------|
| ffmpeg.exe | 7.1 (2026-02-09 git build) | GPL-3.0+ | Audio downmixing (multi-channel → stereo) |
| ffprobe.exe | 7.1 (2026-02-09 git build) | GPL-3.0+ | Audio stream probing (`--details` mode) |
| ffplay.exe | 7.1 (2026-02-09 git build) | GPL-3.0+ | Not used by truedat (bundled with FFmpeg distribution) |

**Licensing:** This FFmpeg build is compiled with `--enable-gpl`, making the resulting binaries GPL-3.0+. See https://ffmpeg.org/legal.html for full details.

**Note:** FFmpeg is an optional external dependency. Without it, multi-channel audio files are skipped with a warning and `--details` mode is unavailable. Truedat itself (MIT) does not link against FFmpeg — it invokes the executables as subprocesses.

## Output Files

| File | Description |
|------|-------------|
| `mbxmoods.json` | Mood vectors and 15 raw Essentia features per track |
| `mbxmoods-errors.csv` | Failed tracks with error reasons (mood analysis) |
| `mbxhub-fingerprints.json` | Chromaprint fingerprints and audio MD5 hashes per track |
| `mbxhub-fingerprints-errors.csv` | Failed tracks with error reasons (fingerprint mode) |
| `mbxhub-details.json` | Audio stream details: codec, bitrate, sample rate, channels per track (requires ffprobe) |
| `truedat.log` | Console output log (when `--audit` is used) |

## Platform Support

| Platform | Status |
|----------|--------|
| Windows 10 (1903+) | Supported |
| Windows 11 | Supported |

## Security Considerations

- No network access required
- Reads audio files (read-only)
- Writes JSON output next to input XML file
- No telemetry or external services
