# Essentia x64 Windows Build

Cross-compile `essentia_streaming_extractor_music.exe` as a 64-bit static binary.

Forked from essentia-master (v2.1-beta6-dev). Official builds are 32-bit only.

## Prerequisites

**WSL2 with Ubuntu** (run all commands in WSL):

```bash
# Core build tools
sudo apt-get update
sudo apt-get install -y build-essential cmake pkg-config python3 nasm yasm

# x64 MinGW cross-compiler
sudo apt-get install -y g++-mingw-w64-x86-64

# For downloading sources
sudo apt-get install -y curl
```

## Build

```bash
# From WSL, navigate to the build directory
cd /mnt/c/Users/builder/source/repos/MBX/truedat/essentia-build

# Step 1: Build all 9 third-party dependencies (~30-60 min)
bash build_3rdparty.sh

# Step 2: Build Essentia + extractor (~10-20 min)
bash build_essentia.sh
```

Output: `output/essentia_streaming_extractor_music.exe` (64-bit)

## What Changed from Official i686 Build

| File | Change | Why |
|------|--------|-----|
| `build_config.sh` | `HOST=x86_64-w64-mingw32` | Target x64 instead of i686 |
| `build_config.sh` | FFTW: removed `--with-incoming-stack-boundary=2`, `--with-our-malloc16`; added `--enable-avx` | Those flags are i686 stack/malloc alignment workarounds; x64 ABI guarantees alignment |
| `3rdparty/build_ffmpeg.sh` | `--arch=x86_64`, removed `--enable-memalign-hack` | x64 architecture, memalign hack is i686-only |
| `build_essentia.sh` | Post-configure sed to patch compiler paths | WAF hardcodes i686 compilers in `--cross-compile-mingw32` mode |

## Directory Layout

```
essentia-build/
├── BUILD.md              ← this file
├── build_config.sh       ← x64 config (HOST, versions, flags)
├── build_3rdparty.sh     ← master script: builds all deps
├── build_essentia.sh     ← builds Essentia + extractor (full rebuild, plain flags)
├── run-x6-build.sh       ← incremental build of the extractor + its test tools (output-x64.6 / .7)
├── run-x8-build.sh       ← profile-guided + link-time-optimized build (output-x64.8)
├── OUTPUT-BUILDS.md      ← what each output-x64.N build is, how it was verified
├── 3rdparty/             ← individual dep build scripts + install prefix
│   ├── build_eigen3.sh
│   ├── build_fftw3.sh
│   ├── build_lame.sh
│   ├── build_ffmpeg.sh
│   ├── build_libsamplerate.sh
│   ├── build_zlib.sh
│   ├── build_taglib.sh
│   ├── build_yaml.sh
│   ├── build_chromaprint.sh
│   ├── include/          ← (created by build)
│   └── lib/              ← (created by build)
├── essentia-src/         ← full Essentia source tree
├── output-x64.N/         ← one folder per released extractor build (see OUTPUT-BUILDS.md)
└── output/               ← final .exe lands here
```

## The current extractor build (output-x64.8, 2026-11-21)

The extractor truedat ships is built by `run-x8-build.sh`, not by `build_essentia.sh`. It
is the same sources and the same compiler flags (`-O2 -ffp-contract=off`, baseline
x86-64), built in three steps:

1. an **instrumented** build (`-fprofile-generate`);
2. a **training run** of that build over a list of local audio files, in the call shape a
   truedat scan uses, which records where the time goes;
3. an **optimized** build that reads the recording and optimizes across source files at
   link time (`-fprofile-use -flto`).

```bash
# record a new profile and build from it (the audio list is local, not in the repo)
bash run-x8-build.sh <dest dir> <training list>
# or rebuild from the profile a build was made with - no audio needed
bash run-x8-build.sh <dest dir> output-x64.8/profile-gcda.tar.gz
```

The script's header says how the extra flags reach waf without editing the wscript, and
how the counters get from the Windows program back into the build tree. Recorded counters
fit only the sources and the gcc they were recorded with: after a source change, record a
new profile. A new build is accepted on its values, not on the recipe — see the
`output-x64.8` section of `OUTPUT-BUILDS.md` for the gate.

## Rebuild Notes (2026-07-10 large-file-patch build)

Lessons from the second full build (in-place on `/mnt/c`, WSL Ubuntu-24.04 — see
`OUTPUT-BUILDS.md` for what `.1`/`.2` outputs are):

- **Deps tarball extracts INTO `3rdparty/`** — it's rooted at `lib/`/`include/`:
  `tar xzf 3rdparty-x64-deps.tar.gz -C 3rdparty/`
- **Fix pkg-config prefixes after extracting.** The `.pc` files hardcode the
  original build home (`/home/builder/essentia-build/3rdparty`). When building
  in-place, rewrite them or WAF configure gets dead paths:
  ```bash
  sed -i 's|/home/builder/essentia-build/3rdparty|/mnt/c/<this-repo>/essentia-build/3rdparty|g' 3rdparty/lib/pkgconfig/*.pc
  ```
- **Cap parallelism.** WAF defaults to `-j$(nproc)` (22 on this box); each mingw
  g++ peaks ~1 GB, and 22 of them OOM-killed the build on a 15 GB WSL VM. Use
  `python3 waf -j8` — `resume-build.sh` does this and skips the reconfigure.
- **The build must finish in one uninterrupted run.** WAF persists its
  incremental state only on clean exit — a killed build (timeout, OOM, closed
  terminal) restarts from step 0 next time. Run it from a shell that won't be
  killed; ~19 min at `-j8` on `/mnt/c`.
- `build_essentia.sh` does `rm -rf build` + reconfigure (full rebuild every
  time) and sed-patches `essentia-src/wscript` in place (idempotent, but shows
  as working-tree churn in git).

## Troubleshooting

**`x86_64-w64-mingw32-g++ not found`**: Install the cross-compiler:
```bash
sudo apt-get install g++-mingw-w64-x86-64
```

**WAF configure fails with pkg-config errors**: Dependencies weren't built yet. Run `build_3rdparty.sh` first.

**Linking errors about missing symbols**: Check that all deps built for x64. Look in `3rdparty/lib/` for `.a` files and verify with:
```bash
file 3rdparty/lib/libfftw3f.a  # should say "x86-64"
```
