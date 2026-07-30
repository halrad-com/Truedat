# output-x64 Build Variants

Three builds of the x64 Essentia extractor suite live side by side. Same source
tree (`essentia-src/`, forked from essentia-master v2.1-beta6-dev), same
toolchain (WSL2 Ubuntu, `x86_64-w64-mingw32-g++`, static link against the
prebuilt deps in `3rdparty-x64-deps.tar.gz`).

## output-x64.1 — original x64 port (2026-02-13)

The initial 64-bit cross-compile of the official (32-bit-only) extractors.
Unpatched upstream algorithm code. Streaming `ChordsDetection` uses
`BufferUsage::forMultipleFrames` (262,144-element output buffer), which caps
analyzable track length at ~12,172 s (~200 min) at the extractor's 44.1 kHz /
2048-hop analysis rate — longer tracks overflow the buffer and the extractor
dies mid-analysis. Truedat's matching pre-flight guard was
`MaxEssentiaDurationSecs = 12000`.

`essentia_streaming_extractor_music.exe`: MD5 `05c4c77bb204026e87b3a593e0d0ed2e`

## output-x64.2 — large-file patch (2026-07-10)

Identical to `.1` plus one local source patch:

- `essentia-src/src/algorithms/tonal/chordsdetection.cpp` — the `_chords` and
  `_strength` output buffers switched from `forMultipleFrames` (262,144
  elements) to `forLargeAudioStream` (1,048,576 elements, see
  `src/essentia/streaming/phantombuffer.h`).

This raises the length ceiling 4× to ~48,695 s (~13.5 h). Truedat's matching
pre-flight guard is the `--max-duration <secs>` flag: since 2026-07-29 it
**defaults to 48000** (800 min, paired with dist shipping a `.2`-lineage
extractor — the `.3` rollout). Pass `--max-duration 12000` if running the old
small-buffer `.1` binary; leaving 48000 against `.1` lets >200-min tracks
through to an extractor that dies at ~12,172 s (logged as FAILED per track;
the scan continues).

RAM note: the bigger buffers are allocated eagerly per extractor process —
about +36 MB each (1.31 M `std::string` slots for chords + floats for
strength). At truedat's default parallelism (cores−2 ≈ 20 workers) that is
under 1 GB system-wide during a scan; accepted as negligible.

`essentia_streaming_extractor_music.exe`: MD5 `f52e166e9063b23cdda538cdf8530ed4`
(byte size is identical to `.1` — the patch swaps an enum constant, which
doesn't change code layout — so distinguish builds by hash, not size).

Build log for `.2`: `build-chordsfix.log` (417 steps, 18m42s).
Rebuild recipe: extract deps (`tar xzf 3rdparty-x64-deps.tar.gz -C 3rdparty/`),
fix the `.pc` prefix paths to the current mount, then `bash build_essentia.sh`
(or `resume-build.sh` for an incremental run — it caps WAF at `-j8` because the
default `-j$(nproc)` OOM-kills mingw on a 15 GB WSL VM).

## output-x64.3 — optimization fix, -O2 (2026-07-29)

Identical source to `.2` (carries the chordsdetection `forLargeAudioStream`
patch and its ~+36 MB/process buffer forward unchanged) plus a build-flag fix:

- `essentia-src/wscript` (MinGW cross-compile branch, ~line 297) — upstream code
  plain-assigns `CXXFLAGS = ['-static-libgcc', '-static-libstdc++']`, clobbering
  the `-O2` that release mode set earlier in configure. Result: **the `.1` and
  `.2` extractors were compiled at `-O0`** (confirmed in the cached configure
  env `essentia-src/build/c4che/_cache.py` — no `-O` flag at all). Only
  Essentia's own DSP code was affected; the 3rdparty deps (FFTW, FFmpeg) always
  built with their own `-O3` defaults.
- `.3` appends `-O2 -ffp-contract=off` at that site. `-O2` restores what release
  mode intended (expected ~2-4x on the extractor's own code); `-ffp-contract=off`
  locks out FMA contraction so feature values stay bit-identical to generic
  x86-64 `-O2` semantics — the mbxmoods.json schema is locked and downstream
  verdict thresholds are calibrated against these numbers.
- `build_essentia.sh` now caps WAF at `-j6` (uncapped `-j$(nproc)` OOM-kills
  mingw; `-O2` raises per-process compiler memory further).

**Validation (2026-07-29, PASSED):** measured on real files, solo process:
MP3 22.06s → 6.76s (3.26×), M4A 18.83s → 6.14s (3.07×). Output diff verdict:
`.2` vs `.3` differ in late decimals — but so do **two runs of `.2` on the same
file** (210 vs 208 differing lines, same fields, same magnitudes; e.g.
spectral-centroid-scale mean: `.2` run1 1793.406, `.2` run2 1793.656, `.3`
1793.527 — the `.3` value sits between two `.2` runs). **The extractor was
never bit-deterministic run-to-run**; a bit-identical gate is unsatisfiable
even between two runs of the same binary. `.3` is statistically
indistinguishable from re-running `.2`: no method-tag bump, no recalibration.
Truedat's 4dp rounding and the verdict's "unknown" margins have always
absorbed this noise.

Truedat pairing: unchanged from `.2` — `--max-duration 48000` applies when
scanning with `.2`-lineage extractors (`.3` included).

Build log for `.3`: `build-o2.log` (417 steps, 19m20s; launched via
`run-o2-build.sh` one-shot).
`essentia_streaming_extractor_music.exe`: MD5 `6c6e2737d97c146fdedf13fb4832183e`
