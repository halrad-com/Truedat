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

## output-x64.4 — bpm-peak key-collision fix (2026-08-10)

Identical to `.3` (`-O2` + `forLargeAudioStream` 13-hour cap) plus a one-line fix to
`MusicRhythmDescriptors.cpp`: `firstPeakSpread` was connected to the **same** pool key as
`firstPeakWeight` (`bpm_histogram_first_peak_weight`), so the spread silently clobbered the
weight and no `..._first_peak_spread` key was ever emitted. Rekeyed to
`bpm_histogram_first_peak_spread` (matching the correct second-peak block in the same file +
the sibling `FreesoundRhythmDescriptors`). **Confirmed on a real file:** `.3` reported
`first_peak_weight = 0.598086` (which is actually the SPREAD); `.4` correctly gives
`first_peak_weight = 0.074733` + `first_peak_spread = 0.598086`. So every catalog scanned with
`.1`/`.2`/`.3` holds a **wrong** `bpmFirstPeakWeight` (it's the spread) — a rescan on `.4`
corrects it and populates `bpmFirstPeakSpread`.

Incremental build (reused `.3`'s object cache; recompiled only the one file + relinked): 417
steps, 7m13s. `essentia_streaming_extractor_music.exe`: MD5 `c6a436d0fe9a33a4c8eac120d9e6243f`.

Capabilities: optimized (`-O2`), accurate bpm, 13-hour file support. truedat's startup detector
(`ReportExtractorBuild`) hashes the resolved extractor against these MD5s and warns when an
older one is in use. **Shipped in `dist/` as of 2026-10-12** (`.5` below is built and verified
but not yet wired into truedat).

## output-x64.5 — additive binary frames writer, descriptor filter, build id (2026-10-12)

Identical analysis code to `.4` — no algorithm, beat-tracking, key or optimisation-flag change;
`-O2 -ffp-contract=off` and the 13-hour `forLargeAudioStream` cap carry forward — plus three
**additive** changes (operator ruling 2026-10-11/12: do not modify the existing writers; select
the binary writer by a new argument path):

- **`BinaryPoolOutput`** — a new standard algorithm beside `YamlOutput`
  (`essentia-src/src/algorithms/io/binarypooloutput.{h,cpp}`), picked up by the build's
  algorithm scan (the registration file is generated; nothing hand-edited). Writes a Pool as
  the **ESPB v1** flat binary: header, descriptor table (name, dtype, flags, count, dims,
  absolute payload offset + length), raw little-endian float32 payloads. Parameters `filename`,
  `include` (descriptor-name prefixes, Pool namespace rule) and `tag`. Full layout, a worked
  example and the C# read recipe: **`FRAMES-BINARY-FORMAT.md`**.
- **`streaming_extractor_music.cpp`** — new optional argument
  `input output [profile] [--frames-bin <path>]`. When present *and* the profile sets
  `outputFrames`, the frames pool goes to the binary file **instead of** `<output>_frames`.
  New profile key `framesInclude: [prefix, ...]` filters the binary (absent = all). The 2-arg
  and 3-arg calls take exactly the `.4` code path; a wrong shape prints usage and exits 1 as
  before; `--frames-bin` without `outputFrames` writes no frames file and warns on stderr.
- **`metadata.version.extractor_build`** = `"output-x64.5"` in the **main** output
  (`MUSIC_EXTRACTOR_BUILD_ID` in `src/essentia/utils/extractor_music/extractor_version.h`;
  also the ESPB header `tag`). This is the only new key; truedat ignores unknown keys and can
  read the build from the output instead of an MD5 table.

Incremental build on `.4`'s object cache (`run-x5-build.sh`, one-shot scheduled task, `-j6`):
418 steps (+1 source), **8m19s** (20:22:53 → 20:31:12 PDT 2026-10-11), log `build-x5.log`.
`essentia_streaming_extractor_music.exe`: MD5 **`6f47bdfcaadfd4f510c5e1621c07945d`**.

**Verification (2026-10-11 PDT, this box, solo process, no CPU cap; scripts in `spike-x5/`,
run with `python -I`):**

- (i) 60 s 44.1 kHz stereo sine WAV, 2-arg call, `.4` vs `.5` main JSON: 4499 vs 4500 keys,
  the one extra key is `metadata.version.extractor_build`, **0 numeric values differ**
  (`diff_json.py`).
- (ii) 3-arg call with `outputFrames: 1`: `<out>_frames` JSON written exactly as `.4` writes it
  (15.3 MB for the 60 s WAV).
- (iii) `--frames-bin`: 3.43 MB ESPB file, 105 descriptors; all 856,759 values match the JSON
  frames of a separate run to max rel diff 5e-12; names, counts and dims identical
  (`check_espb.py`).
- (iv) `framesInclude` with `lowlevel.mfcc, lowlevel.spectral_centroid, tonal.hpcp,
  lowlevel.loudness_ebu128, rhythm.beats_position, rhythm.beats_loudness`: 9 descriptors,
  337 KB, nothing outside the six prefixes (`rhythm.beats_loudness_band_ratio` correctly
  excluded by the prefix-plus-dot rule).
- (v) **Real audio, five files from the local library** (MP3 "I Ran" 5:00, M4A Tiny Desk
  concert, WMA "Get Born Again", a 78-rpm HD FLAC transfer, and a 25.7-minute live FLAC),
  `.4` vs `.5` with the identical 2-arg call: **key sets identical on all five except the one
  allowed key; zero non-numeric (string) differences.** Numeric differences were judged
  against run-to-run noise the way the `.3` section describes: three `.4` runs of the MP3 differ
  from each other in 701–730 of 5131 values (max rel 1.53, in `.min`/`.max` statistics of
  spectral-shape descriptors); `.4` vs `.5` on the same file differs in 658–732 values
  (max rel 1.93) in the same statistic families. Pooled over the five files, per statistic
  family (mean/var/stdev/median/min/max/dmean/dmean2/dvar/dvar2), **13 of 14 cross-build pairs
  have 0 values beyond 2× the same-binary noise; the 14th has one** —
  `lowlevel.spectral_spread.dmean2` on the long FLAC at rel 0.0023 vs a same-binary family
  max of 0.0011 (2.06×, with 6 same-binary pairs bounding 14 cross pairs). `means` differ by
  ≤ 3.7e-4 relative everywhere. `noise_table.py mp3 m4a wma flac long <two extra .4 outputs>`
  reproduces the table.
- (vi) `.5` main JSON with and without `--frames-bin` on the same five files: **not
  byte-identical** — the extractor is not run-to-run deterministic on real audio (`.3`
  finding) — but key sets identical and the differences sit in the same-binary rows of the
  table above (they *are* the `.5`-vs-`.5` noise sample). The frames path does not touch the
  main output: it runs after `outputToFile(results, …)` on a different pool.
- **Frame-level noise is pre-existing.** Two `.4` runs of the 78-rpm FLAC with `outputFrames`
  differ in 16,955 of 2,664,376 frame values (max rel 1.82), concentrated in frame 0, the
  first ~55 frames of the quiet lead-in and the last frame, in the spectral-shape descriptors
  (`spectral_centroid/contrast/entropy/kurtosis/skewness/spread/rolloff`,
  `*bands_crest/flatness_db/kurtosis/skewness/spread`, `dissonance`, `pitch_salience`,
  `zerocrossingrate`). `.5`'s binary vs a separate `.5` JSON-frames run shows the same
  pattern (26–27 descriptors, 1–131 frames each); `mfcc`, `hpcp`, the band matrices,
  `loudness_ebu128.*` and `rhythm.beats_*` were identical across runs on every file.

**Timing** (wall clock, one process, this box):

| file | `.4` 2-arg | `.5` 2-arg | `.5` + JSON frames | `.5` + `--frames-bin` (all) |
|---|---|---|---|---|
| sine 60 s | 1.68 s | 1.61 s | 3.17 s (+97%) | 1.70 s (+5%) |
| MP3 5:00 | 9.24 s | 9.02 s | 16.93 s (+88%) | 9.05 s (+0.3%) |
| FLAC 25.7 min | 44.8 s | 47.3 s | — | 48.8 s (+3%) |

Binary frames cost ~3.4 MB/min of audio (88.0 MB for 25.7 min; 17.4 MB vs 76.2 MB JSON for
the MP3) and essentially no wall time; the curated `framesInclude` list is ~10× smaller again.
A sine is not music — the real measurement is the 20-track slice on max (architect).

**Additive guarantee:** with two or three arguments `.5` executes the `.4` code paths; the only
difference in any output is the `metadata.version.extractor_build` key, plus the run-to-run noise
that `.4` already has. Truedat pairing unchanged (`--max-duration 48000`); the truedat side —
`ReportExtractorBuild`'s MD5 table, `truedat-deps`, the C# ESPB reader — is separate work.

## output-x64.6 — decoder correctness, decoder-only (2026-10-25)

`AudioLoader`'s libavcodec loop and frame conversion, moved into
`src/essentia/utils/audioloader_decode.h` (outside `algorithms/`, whose headers the build
turns into algorithm registrations) so the loader, a scripted-decoder fixture and a native
ASan harness run the same code:

- **Send/receive loop.** `.5` sent each packet once and received one frame: a packet refused
  with `EAGAIN` was dropped, and a multi-frame packet's later frames were left behind. `.6`
  receives the pending frames and resends the same packet, and receives until the decoder
  wants input.
- **Drain.** `.5` re-sent the drain packet every iteration, so the second send's `AVERROR_EOF`
  ended the drain after one delayed frame. `.6` sends it once and receives until EOF.
- **Conversion capacity.** `.5` converted into a fixed 384,000-byte buffer, told `swr_convert`
  the frame's sample count as capacity and clamped only after the write: a 65,535-sample stereo
  frame (524,280 B) overflowed the heap and the process died (exit 139). `.6` sizes the buffer
  from the received frame and `swr_get_out_samples`, grows it on demand and reuses it.
- Also: non-audio packets (cover art) are unreffed instead of overwritten, each converted
  frame is unreffed, the frame is freed with `av_frame_free`, and frames are pushed downstream
  in chunks of at most 65,536 (a frame is pushed whole now; one request above the source's
  262,144-token phantom size throws).

`metadata.version.extractor_build` = `"output-x64.6"`. The `.5` writer, `--frames-bin`,
`framesInclude` and every call shape are untouched.

Incremental build on `.5`'s object cache (`run-x6-build.sh`, one-shot scheduled task, `-j6`):
5 sources recompiled, **8m33s** (21:03:13 → 21:11:46 PDT 2026-10-24).
`essentia_streaming_extractor_music.exe`: MD5 **`2fe852f564c894691e144697c6a0d200`**.

**Verification** (evidence and method: `spike-x6/README.md`; 45-row corpus — every codec the
extractor's FFmpeg decodes, 44.1–192 kHz, mono/stereo, incomplete final frames, short and
truncated files, 65,535-block FLAC — synthetic rows regenerating byte-identically, plus eight
real files):

- **PCM parity against FFmpeg 7.1.1** built from the extractor's own FFmpeg source, flags and
  toolchain (`spike-x6/references.txt`): `.6` sample count and f32 PCM byte-identical on
  **45 of 45** rows (`parity-x6.txt`). The `.5` loader (an intermediate build whose extractor is
  byte-identical to `.5` on `sine60.wav`) is byte-identical on every row except the three stereo
  65,535-block FLACs, where it crashes (`parity-build-a.txt`). So with this FFmpeg the loop
  defects lost no samples on any codec here — its decoders return one frame per packet and no
  delayed frames — and the capacity defect is the only one real files reach.
- **Decoder-state fixtures** (`essentia_test_audioloader_decodeloop`, the loader's own loop
  against a scripted decoder: multi-frame packets, `EAGAIN` backpressure, delayed frames at
  drain, drain-once, mid-stream error, an `EAGAIN`-forever decoder): `.5` loop 5 of 8 fail,
  `.6` 8 of 8 pass (`fixtures-*.txt`).
- **ASan** (FFmpeg 7.1.1 built natively with `--toolchain=gcc-asan --disable-asm`, so
  libswresample is instrumented): `.6` over the corpus, 0 ASan/LeakSanitizer reports; `.5`'s
  conversion on the big-block rows, heap-buffer-overflow in `libswresample/audioconvert.c` via
  `swr_convert` on the three stereo rows and nothing on the mono control (`asan-*report.txt`).
- **End-to-end, `.5` vs `.6`**, 2-arg call, judged against a `.5` × 3 same-binary band
  (`repeat-band.*`): on every row `.5` analyses, 0 values beyond 2× the statistic family's
  band; synthetic rows move 0 keys at all. Per-key (2× the key's own three-run band) flags 56
  values on four real files, and an out-of-sample `.6` vs `.6` pair flags 50 on the same four —
  the run-to-run noise `.3` documented, not the change (`field-table-*.txt`).
- **Call shapes** (`additive-check.txt`): 2-arg and 4-arg main JSON differ from `.5` only in
  `extractor_build`; 3-arg JSON frames byte-identical; the ESPB file differs in one byte, the
  header tag.

Against the shipped `dist/truedat/ffmpeg.exe` (FFmpeg 8.x) AAC in MP4 decodes 288–867 frames
shorter (8.x trims the end padding) and AAC/WMA differ in the last bits; both loaders are
bit-exact against 7.1.1 there, so these are FFmpeg-version effects (`references.txt`).
Not verified: an Opus decoder (none in this FFmpeg build), multichannel input, real files with
large FLAC blocks (none found), non-ASCII paths.
