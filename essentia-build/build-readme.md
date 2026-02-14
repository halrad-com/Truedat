# Building Essentia x64 for Windows

The Essentia `streaming_extractor_music.exe` shipped with Truedat was a **32-bit** binary from the official MTG project. No 64-bit Windows build has ever been published. The 32-bit binary hits the 2 GB address space limit on large audio files, causing `bad_alloc` failures.

This project cross-compiles a **64-bit** static binary using WSL2. The x64 build was completed Feb 13, 2026.

## Pre-Built x64 Binaries

Ready-to-use 64-bit binaries are in [`essentia-build/output-x64/`](essentia-build/output-x64/) — 53 tools including the primary `essentia_streaming_extractor_music.exe` (28MB). These are static binaries with no external dependencies.

The deployed Truedat distribution is in [`dist/truedat/`](dist/truedat/) with both i686 and x64 binaries for comparison.

For a full list of all tools and what they do, see [`essentia-build/tools-summary.md`](essentia-build/tools-summary.md).

## Building from Source

For step-by-step environment setup from a fresh machine, see [`essentia-build/bringup.md`](essentia-build/bringup.md).

### Quick Start

**Option A — Use pre-built dependency archive** (skip the 30-60 min dep build):

```bash
cp -r /mnt/c/.../truedat/essentia-build ~/essentia-build
cd ~/essentia-build/3rdparty
tar xzf ../3rdparty-x64-deps.tar.gz
cd ..
bash build_essentia.sh
```

**Option B — Build everything from source:**

```bash
cp -r /mnt/c/.../truedat/essentia-build ~/essentia-build
cd ~/essentia-build
bash build_3rdparty.sh    # ~30-60 min
bash build_essentia.sh    # ~10-20 min
```

Output: `output-x64/essentia_streaming_extractor_music.exe` (64-bit, 28MB)

### Prerequisites

WSL2 with Ubuntu 24.04:

```bash
sudo apt-get update && sudo apt-get install -y \
    build-essential cmake pkg-config python3 nasm yasm curl \
    g++-mingw-w64-x86-64
```

## Background

- **Source**: Essentia v2.1-beta6-dev (MTG/essentia on GitHub, AGPL-3.0)
- **Build system**: WAF (Python-based), cross-compiles from Linux using MinGW-w64
- **Official approach**: Cross-compile for i686 (32-bit) from Linux. No native MSVC path.
- **Our approach**: Same pipeline, retargeted to x86_64

All build scripts live in `essentia-build/`. The Essentia source tree is in `essentia-build/essentia-src/`.

## Third-Party Dependencies

All built as static libraries and linked into a single .exe.

| Library | Version | Purpose |
|---------|---------|---------|
| Eigen3 | 3.3.7 | Linear algebra (header-only) |
| FFTW | 3.3.2 | FFT computation |
| FFmpeg | 7.1.1 | Audio file decoding |
| LAME | 3.100 | MP3 encoding |
| libsamplerate | 0.1.8 | Audio resampling |
| zlib | 1.2.12 | Compression (TagLib dependency) |
| TagLib | 1.11.1 | Audio metadata reading |
| libyaml | 0.1.5 | YAML output |
| Chromaprint | 1.5.1 | Audio fingerprinting |

## What Changed for x64

The official Essentia build scripts target i686 (32-bit). The following changes were made:

| Change | i686 (original) | x64 (ours) |
|--------|-----------------|------------|
| Cross-compiler | `i686-w64-mingw32` | `x86_64-w64-mingw32` |
| FFmpeg arch | `--arch=x86_32` | `--arch=x86_64` |
| FFmpeg memalign | `--enable-memalign-hack` | removed (x64 doesn't need it) |
| FFTW stack boundary | `--with-incoming-stack-boundary=2` | removed (x64 ABI guarantees 16-byte alignment) |
| FFTW extensions | SSE2 only | SSE2 + AVX |
| WAF compiler refs | hardcoded i686 | pre-configure sed patch to x86_64 |

## Directory Layout

```
truedat/
├── essentia-build/
│   ├── build_config.sh         # x64 config: HOST, library versions, compiler flags
│   ├── build_3rdparty.sh       # Master script: builds all deps in order
│   ├── build_essentia.sh       # Builds Essentia + patches WAF for x64
│   ├── bringup.md              # Full environment setup guide
│   ├── tools-summary.md        # Summary of all 53 built tools
│   ├── 3rdparty-x64-deps.tar.gz  # Pre-built deps archive (skip dep build)
│   ├── 3rdparty/
│   │   ├── build_eigen3.sh ... build_chromaprint.sh  # Individual dep scripts
│   │   ├── include/            # (created by build)
│   │   └── lib/                # (created by build)
│   ├── essentia-src/           # Full Essentia source tree
│   └── output-x64/             # 53 x64 static binaries
├── dist/
│   └── truedat/                # Deployed Truedat + both i686/x64 extractors
├── Truedat/                    # Truedat C# source
└── build-readme.md             # This file
```

## Deploy

Copy the x64 extractor to the Truedat distribution:

```cmd
copy essentia-build\output-x64\essentia_streaming_extractor_music.exe dist\truedat\essentia_streaming_extractor_music.exe
```

Truedat invokes the extractor as a subprocess — the CLI is identical between 32/64-bit.

## Troubleshooting

See [`essentia-build/bringup.md`](essentia-build/bringup.md) for a comprehensive list of issues encountered and fixes applied during the first build.
