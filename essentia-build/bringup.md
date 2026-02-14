# Essentia x64 Build — Environment Bringup

Step-by-step from a fresh Windows machine to a working 64-bit `streaming_extractor_music.exe`.

Successfully completed Feb 13, 2026.

## Prerequisites

- Windows 10/11 with WSL2 enabled
- Internet connection (for downloading Ubuntu + source tarballs)

## Step 0: Install WSL2 + Ubuntu

From **PowerShell (Admin)**:

```powershell
wsl --install -d Ubuntu-24.04
```

Creates a Linux user account (prompts for username/password). Verify:

```powershell
wsl --list --verbose
# Should show: Ubuntu-24.04  Running  2
```

## Step 1: Install Build Tools

From WSL:

```bash
sudo apt-get update && sudo apt-get install -y \
    build-essential \
    cmake \
    pkg-config \
    python3 \
    nasm \
    yasm \
    curl \
    g++-mingw-w64-x86-64
```

Verify:

```bash
x86_64-w64-mingw32-g++ --version   # MinGW cross-compiler
python3 --version                    # Python 3.x (for WAF build system)
cmake --version                      # CMake (for TagLib, Chromaprint, Eigen)
pkg-config --version                 # pkg-config (dependency detection)
nasm --version                       # NASM assembler (for FFmpeg SIMD)
```

## Step 2: Copy Build Tree to Linux Filesystem

**Important**: Building on NTFS (`/mnt/c/`) causes `tar` failures ("Cannot utime: Operation not permitted") and is significantly slower. Always build on the native Linux filesystem.

```bash
cp -r /mnt/c/Users/scott/source/repos/MBX/truedat/essentia-build ~/essentia-build
cd ~/essentia-build
```

## Step 3: Build Third-Party Dependencies

**Option A — Build from source** (~30-60 min):

```bash
bash build_3rdparty.sh
```

**Option B — Use pre-built archive** (~5 sec):

If `3rdparty-x64-deps.tar.gz` is checked into the repo, skip the build entirely:

```bash
cd ~/essentia-build/3rdparty
tar xzf ../3rdparty-x64-deps.tar.gz
```

Then jump to Step 4.

---

Option A builds 9 libraries as static x64 `.a` files:

| # | Library | Version | Purpose |
|---|---------|---------|---------|
| 1 | Eigen3 | 3.3.7 | Linear algebra (header-only) |
| 2 | FFTW3 | 3.3.2 | FFT computation |
| 3 | LAME | 3.100 | MP3 encoding |
| 4 | FFmpeg | 7.1.1 | Audio file decoding |
| 5 | libsamplerate | 0.1.8 | Audio resampling |
| 6 | zlib | 1.2.12 | Compression |
| 7 | TagLib | 1.11.1 | Audio metadata reading |
| 8 | libyaml | 0.1.5 | YAML/JSON output |
| 9 | Chromaprint | 1.5.1 | Audio fingerprinting |

Output: `3rdparty/lib/*.a` (12 files — FFmpeg produces multiple libs) and `3rdparty/include/`

Verify:

```bash
ls 3rdparty/lib/*.a | wc -l          # Should be 12
file 3rdparty/lib/libfftw3f.a        # Should say "current ar archive"
```

## Step 4: Build Essentia + Extractor

```bash
bash build_essentia.sh
```

Patches the WAF wscript from i686→x86_64 compiler references, configures, and builds libessentia + all example tools (~10-20 min).

Output: `output-x64/*.exe` (53 tools including `essentia_streaming_extractor_music.exe`)

## Step 5: Copy Output Back to Windows

```bash
cp ~/essentia-build/output-x64/*.exe /mnt/c/Users/scott/source/repos/MBX/truedat/essentia-build/output-x64/
```

## Step 6: Verify the Binary

```powershell
# From PowerShell — confirm x64 PE header
$pe = [IO.File]::ReadAllBytes("C:\Users\scott\source\repos\MBX\truedat\essentia-build\output\essentia_streaming_extractor_music.exe"); $off = [BitConverter]::ToInt32($pe, 60); $m = [BitConverter]::ToUInt16($pe, $off + 4); if ($m -eq 0x8664) { "x64 - SUCCESS" } elseif ($m -eq 0x14c) { "x86 - WRONG" }
```

Test with a real audio file:

```cmd
essentia-build\output\essentia_streaming_extractor_music.exe "test.mp3" "C:\Users\scott\test_output.json"
```

Output files are written with `_statistics` and `_frames` suffixes appended to the base name.

## Step 7: Deploy to Truedat

```cmd
copy essentia-build\output\essentia_streaming_extractor_music.exe Truedat\streaming_extractor_music.exe
```

Truedat invokes the extractor as a subprocess — the CLI is identical between 32/64-bit.

## Issues Encountered and Fixed

### 1. NTFS tar failures

**Symptom**: Hundreds of "Cannot utime: Operation not permitted" errors when extracting tarballs on `/mnt/c/` (NTFS). Scripts fail due to `set -e`.

**Root cause**: WSL's NTFS driver doesn't support setting file timestamps via `utime()`.

**Fix**: Build on the native Linux filesystem (`~/essentia-build`) instead of NTFS. This also makes builds significantly faster.

### 2. FFTW3 aligned malloc error

**Symptom**: `#error "Don't know how to malloc() aligned memory ... try configuring --with-our-malloc"`

**Root cause**: `--with-our-malloc16` was removed from `build_config.sh` thinking x64 doesn't need it. While x64 malloc does return aligned memory, FFTW 3.3.2's configure script can't detect this when cross-compiling (it can't run test programs on the target).

**Fix**: Added `--with-our-malloc16` back to `FFTW_FLAGS` in `build_config.sh`. Note: `--with-incoming-stack-boundary=2` is correctly still removed (that's genuinely i686-only).

### 3. PREFIX double-nesting

**Symptom**: Dependencies install to `3rdparty/3rdparty/` instead of `3rdparty/`. `build_essentia.sh` then can't find them.

**Root cause**: `build_config.sh` used `PREFIX=$(pwd)/3rdparty`. When sourced from scripts in the `3rdparty/` directory, `pwd` is already `3rdparty/`, creating a double-nested path.

**Fix**: Changed `build_config.sh` to resolve PREFIX relative to its own location using `BASH_SOURCE` instead of `pwd`. Now works correctly regardless of the calling directory.

### 4. zlib download URL

**Symptom**: `tar: This does not look like a tar archive` — 355-byte response (redirect page, not tarball).

**Root cause**: `zlib.net` moved older versions (1.2.12) to a `/fossils/` subdirectory.

**Fix**: Changed URL in `build_zlib.sh` from `https://zlib.net/` to `https://zlib.net/fossils/`.

### 5. Chromaprint directory name

**Symptom**: `cd: chromaprint-v1.5.1: No such file or directory`

**Root cause**: Script expected `chromaprint-v1.5.1` (with `v` prefix) but the tarball extracts to `chromaprint-1.5.1`.

**Fix**: Changed `cd chromaprint-v$CHROMAPRINT_VERSION` to `cd chromaprint-$CHROMAPRINT_VERSION` in `build_chromaprint.sh`.

### 6. WAF configure fails on i686 compiler

**Symptom**: `Could not find the program ['i686-w64-mingw32-gcc']`

**Root cause**: The wscript hardcodes `i686-w64-mingw32-{gcc,g++,ar}` in `find_program()`. The original `build_essentia.sh` patched the WAF cache *after* configure, but configure itself fails because it can't find the i686 compiler.

**Fix**: Changed `build_essentia.sh` to patch the wscript *before* configure using `sed`, replacing i686 references with x86_64. Also patches the hardcoded pkg-config path to point to our deps directory.

## Harmless Warnings (ignore these)

- **Eigen BLAS/LAPACK/CHOLMOD/UMFPACK/SUPERLU/Qt not found**: Eigen is header-only. These are optional dependencies for its test suite. Not needed.
- **TagLib CMake CMP0022 deprecation**: Old CMakeLists.txt policy. Doesn't affect the build.
- **Buffer size mismatch**: Normal Essentia behavior when processing audio at non-standard sample rates (e.g., 96kHz). Internal buffers auto-resize.

## Toolchain Reference (verified Feb 2026)

| Tool | Version |
|------|---------|
| Ubuntu | 24.04 LTS |
| MinGW g++ | 13 (x86_64, win32 threads) |
| Python | 3.12 |
| CMake | 3.28 |
| pkg-config | 1.8 |
| NASM | 2.16 |
