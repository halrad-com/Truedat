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
  toolchain (`spike-x6/references.txt`): `.6` f32 PCM byte-identical (same length, same bytes)
  on all **43 decoded** rows, and the two Opus rows refused as expected (`parity-x6.txt`). The
  `.5` loader (an intermediate build whose extractor is byte-identical to `.5` on `sine60.wav`)
  is byte-identical on every decoded row except the three stereo 65,535-block FLACs, where it
  crashes (`parity-build-a.txt`). So for the codecs **in this corpus** — FLAC, PCM WAV/AIFF,
  MP3, AAC, WMA v2, Vorbis, ALAC, WavPack — the loop defects lost no samples: those decoders
  return one frame per packet and no delayed frames here. **Where the loop defects DID lose
  audio — codecs whose packets carry many frames:** two real files, added to the corpus
  after review (2026-10-24) named the gap:
  - **WMA Lossless** (44.1 kHz stereo 16-bit, ~5 min): `.6` 13,337,604 frames, byte-identical
    to the reference; the `.5` loader 2,133,778 — **84 % lost**, from frame 28,672 on.
  - **WMA Pro** (96 kHz stereo 24-bit): `.6` 27,463,836 frames, byte-identical; the `.5` loader
    3,440,640 — **87 % lost**, from frame 62,464 on.
  - **APE** (no sample here, no FFmpeg encoder): by the pinned source, `libavformat/ape.c`
    packs 294,912 samples into one packet (APE 3950–3970) and `libavcodec/apedec.c` emits 4,608
    per decode call, leaving the packet partially consumed — the same shape, so the same loss.
  The `.5` extractor on such a file does not fail: it analyses the fraction it decoded (the
  loader also logs one `avcodec_send_packet error: Invalid data` per dropped packet). Every
  `.5` entry for an APE, WMA Lossless or WMA Pro file is therefore wrong and is the refresh set
  once `.6` ships; WMA v2 (the common `.wma`) is bit-identical in both and needs nothing.
- **Decoder-state fixtures** (`essentia_test_audioloader_decodeloop`, the loader's own loop
  against a scripted decoder: multi-frame packets, `EAGAIN` backpressure, delayed frames at
  drain, drain-once, mid-stream error, an `EAGAIN`-forever decoder): `.5` loop 5 of 8 fail,
  `.6` 8 of 8 pass (`fixtures-*.txt`).
- **ASan** (FFmpeg 7.1.1 built natively with `--toolchain=gcc-asan --disable-asm`, so
  libswresample is instrumented): `.6` over the corpus, 0 ASan/LeakSanitizer reports; `.5`'s
  conversion on the big-block rows, heap-buffer-overflow in `libswresample/audioconvert.c` via
  `swr_convert` on the three stereo rows and nothing on the mono control (`asan-*report.txt`).
- **End-to-end, `.5` vs `.6`**, 2-arg call, judged against a `.5` × 3 same-binary band
  (`repeat-band.*`). On the 34 rows where `.5` repeats exactly (every synthetic row and two
  real files), `.6` is **exactly equal** on every key. On the five real rows that are noisy
  run-to-run, the per-key test (2× each key's own three-run band) flags 56 values; an
  out-of-sample `.6` vs `.6` pair flags 50 on the same files, so the flagged keys are the
  run-to-run noise `.3` documented — three runs under-sample it, which is why exact rows carry
  the gate. The pooled statistic-family test is reported too but is weak: the `min` family's
  band is 1.91, so 2× it exceeds the relative metric's ceiling of 2 and cannot fail
  (`field-table-*.txt`).
- **Call shapes** (`additive-check.txt`): 2-arg and 4-arg main JSON differ from `.5` only in
  `extractor_build`; 3-arg JSON frames byte-identical; the ESPB file differs in one byte, the
  header tag.

Against the shipped `dist/truedat/ffmpeg.exe` (FFmpeg 8.x) AAC in MP4 decodes 288–867 frames
shorter (8.x trims the end padding) and AAC/WMA differ in the last bits; both loaders are
bit-exact against 7.1.1 there, so these are FFmpeg-version effects (`references.txt`).
Not verified: an Opus decoder (none in this FFmpeg build), multichannel input, real files with
large FLAC blocks (none found), non-ASCII paths, APE (no sample, no encoder).

Corrections after review (2026-10-24): the codec statement above was narrowed from "every
codec" to the corpus, and the WMA rows added; the parity checker rules on bytes (earlier tables
kept as `parity-*-v1.txt`); the per-key band is the primary end-to-end test.

**APE through the full extractor (found by an executed review, 2026-10-24; fixed in the drop 2
build).** The decoder-only binary above decodes an APE file correctly in isolation but the full
extractor exits 1 on it: `acquireForWrite: Requested number of tokens (176800) > phantom size
(4096) in StereoDemuxer::left`. Its loader emitted every frame of a packet in one scheduler
step; an APE packet is 294,912 samples (64 frames of 4,608), `StereoDemuxer`'s output buffer
is 65,536 tokens with a 4,096 phantom, and the demuxer had a pre-existing status bug — it
answered a full output with `NO_INPUT`, so the scheduler never rescheduled it, and at end of
stream it asked for the whole backlog at once. Nothing in the corpus had a packet that large
(the 65,535-sample FLAC block was one sample under). Two fixes, both in the drop 2 binary:
the loader emits **one decoded frame per `process()` call** (`audioloader_decode.h` `step()`:
libavcodec's decoder holds the rest; a packet it refuses with `EAGAIN` is kept and re-sent after
the next receive; the drain packet is sent once; the `AVFrame` is reused across `reset()`,
closing the leak noted below), which for every one-frame-per-packet codec is exactly `.5`'s
downstream rhythm; and `StereoDemuxer` returns `NO_OUTPUT` when its output is full (one line,
`stereodemuxer.cpp`, from the operator's review session). Gates: the fixtures in step form, a
64-frames-per-packet scenario added; `luckynight.ape` as corpus row `r_ape` — `.6` loader
byte-exact, `.5` loader 175,104 of 2,667,168 frames; the full extractor completes on it under
the 2-arg call and the scan profile with length 60.48; `spike-x6/test_ape_regression.py` 3/3;
ASan 48 rows, 0 reports; the sine output byte-identical to the decoder-only binary.

### Drop 2 — the scan profile: compute only what truedat reads (2026-10-25)

Same build id; the decoder-only binary above stays in history at `7319de5`. The 2-arg call and
any `.5` profile are the FULL extractor, unchanged. New, all additive:

- **Measurement first.** `src/essentia/utils/x6profile.*`: with `ESSENTIA_PROFILE` naming a
  file, the scheduler times every streaming `process()` call and `MusicExtractor` its phases,
  and the run appends a per-algorithm table; otherwise one predictable branch per call. Output
  byte-identical on and off (`spike-x6/instrumentation-check.txt`). The full extractor's CPU
  over the corpus, before any cut, is `spike-x6/cpu-baseline.txt`: network1 57 % (onset front
  end ~21 %), network2 17 % (HPCP 9 %), the EBU R128 pass 14 %, replay gain 7 %, four
  decode+resample passes 19 % in total, **aggregation 3.4 %, output 1.1 %**.
- **Output-neutral code, every profile:** `PoolAggregator` computes only the requested
  statistics, each by the expression it always used, reading the pool in place
  (`poolaggregator.cpp`); `SingleGaussian` takes `computeInverse` and skips the LU inverse
  when `icov` is not asked for (`singlegaussian.{h,cpp}`).
- **Profile switches** (`src/essentia/utils/extractor_music/x6options.h`; absent = on):
  `x6.melbands128` (the 128-band mel branch, read by nothing), `x6.bandsStorage` (per-frame
  melbands / erbbands / barkbands vectors in the pool; their shape, flatness and crest
  descriptors still consume them in-graph), `x6.md5` (MD5 over the encoded packets).
- **The scan profile** (`spike-x6/scan-profile.yaml`; `scan-edges-profile.yaml` adds
  `EdgeProducer`'s `outputFrames` + `framesInclude` lines): the extractor's existing
  per-namespace statistics lists narrowed to what `ParseEssentiaOutput` reads
  (`lowlevel.stats: [mean, stdev]`, `tonal.stats: [mean, stdev]`, `rhythm.stats: [mean]`,
  `lowlevel.mfccStats: [mean, cov]`, `lowlevel.gfccStats: [mean]`) and the three switches off.
  Allowlist: `spike-x6/scan-allowlist.txt` (94 keys read of 599 emitted, `cut-candidates.txt`).

Incremental build (`run-x6-build.sh`, one-shot scheduled task, `-j6`): 4 sources recompiled,
**7m07s** (22:25:46 → 22:32:53 PDT 2026-10-24), on top of the drop 1 object cache; this binary
also carries the APE fix above. `essentia_streaming_extractor_music.exe`: MD5
**`64b55cec008649c673fa13c9ab8dd4b6`**.

**Measurement** (`spike-x6/cpu-table.txt`; every pass alone on the machine, elapsed per
phase/algorithm, process CPU per run; 45 decodable rows). Same-binary spread on this binary
and night: +4.2 % elapsed, +9.9 % wall between two identical full-profile passes, so only the
rows a cut touches are read as results. Each cut alone: the statistics lists take the
aggregation phase from 2.81 s to 0.18 s and the JSON write from 1.04 s to 0.83 s; the 128-band
mel branch is 0.48 s of `MelBands` plus 0.82 s of aggregation; the band-vector storage is
0.21 s of `PoolStorage` plus 0.84 s of aggregation; the packet MD5 is not measurable. The scan
profile (all of them): aggregation 2.81 → 0.11 s, output −0.21 s, `MelBands` −0.48 s,
`PoolStorage` −0.26 s — **about 3 % of the extractor's time, measured; the end-to-end totals
move inside the run-to-run spread.** The main output shrinks from ~600 keys to the ~100
truedat reads. The CPU is in features truedat reads: the onset front end
(`OnsetDetection[method=complex]` ×2 12.9 % + `CartesianToPolar` 8.4 %; the `hfc` method is
0.1 %), `HPCP[size=120]` 5.8 % and `HPCP[size=36]` ×2 3.8 %, the EBU R128 pass 14.5 %, four
decode+resample passes ~19 %. FFT-class work is 2.5 % of the total, which closes the FFTW /
`-march` toolchain question with a number; the JSON write is 1.2 %. The `build-p` baseline in
`cpu-baseline.txt` was ~10 % high in every phase from its own profiler's per-call keying
(`s3` keys by instance); the shares held to within a point.

**Verification** (`spike-x6/README.md`; gate tables `spike-x6/gate-*.txt`, bands `s3-band.*`,
`espb-band.tsv`; 45 decodable rows, 37 of which the extractor repeats exactly run to run —
every synthetic row plus three real files including APE — and 8 noisy real rows):

- **The full profile is the full extractor.** `profiles/full.yaml` (every key at its default)
  against the 2-arg call: the 37 exact rows identical on every key, 0 key-set differences
  (`field-table-sfull-sraw.txt`). The same pair is the noise reference for the noisy rows: two
  identical-profile runs of the same binary differ on 179 values by the per-key test and on 3 by
  the family test, which is what a 2–4-run band under-samples.
- **Scan profile.** Every allowlisted key present on all 45 rows and equal to the full output:
  exactly on the 37 exact rows (`gate-scan-sfull-sscan.txt`); on the 8 noisy rows, 0 by the
  family test and one value by the per-key test — `lowlevel.barkbands_crest.mean` on one AAC
  file, relative difference 2.3e-4 against that key's four-run band of 1.1e-4 and a mean-family
  band of 1.2e-3 (the gate's rule marks it; it is the size of the extractor's own repeat
  noise). `metadata.audio_properties.length` — the key truedat's under-decode gate
  reads — is present on the APE, WMA Lossless and WMA Pro rows with the file's duration. The
  scan output carries 187 keys (94 read; the rest are the metadata block and the per-namespace
  `.stdev` of descriptors truedat reads only the mean of), down from 599
  (`cut-candidates.txt` run on the scan output).
- **Edges call shape.** The frames file under the scan+edges profile against the full edges
  profile: 45 rows PASS — descriptor set, dtypes, counts and dims identical, the 37 exact rows
  byte-identical, the noisy rows inside a three-run frames band
  (`gate-edges-sedges-sscanedges.txt`, `espb-band.tsv`). The first scan profile also switched
  the packet MD5 off; that removed `metadata.audio_properties.md5_encoded` from the frames file
  (the include is a prefix) for a saving that did not measure, so the scan profiles leave it on.
- **Loader step + demuxer guard against the registered decoder-only `.6`** on the 45 original
  rows (`field-table-x6a-sraw.txt`, `gate-edges-x6ea-sedges.txt`): the 34 exactly-repeating
  rows identical in the main output and byte-identical in the frames file; on the noisy rows the
  one family exceedance (`spectral_spread.dvar` on one FLAC, 2.4× a 3-run band) is also exceeded
  by the same binary against itself, so no fix is attributed any movement.
- Fixtures 8/8 (step form); ASan 48 rows, 0 reports; `test_ape_regression.py` 3/3.

Not verified: the APE, WMA Lossless and WMA Pro rows have no `.5` extractor output to compare
against (the `.5` analysis of them is wrong by construction); any decoder not in the corpus;
multichannel; non-ASCII paths; whole-scan wall time — this binary was not raced against `.5`
on a library, and the per-cut numbers are elapsed on a shared machine.

`.6` is frozen at drop 2 (`64b55cec`) as the fallback build. The drop 3 items (buffer reuse,
the StereoMuxer status, the ESPB write) were folded into `.7`.

## output-x64.7 — decode once, one HPCP pass for chords and tuning, no full-PCM retention (2026-10-25)

Same output as `.6` drop 2 in every call shape (the 2-arg call, the full profile, the scan
profile, the edges frames file) apart from the build id (`metadata.version.extractor_build`
and the ESPB header tag). Four changes, each built, gated and measured alone; the first three
against `.6`, the fourth (B5, added before `.7` was released, same build id) against the first three:

1. **Output pools filled in place** (`5485e89`). `MusicExtractor::compute` wrote a local
   results and statistics pool and copied both into its outputs; it now fills the outputs
   directly. The StereoMuxer status guard (`43db55f`) is the other code item folded in from
   drop 3; the ESPB write makes no pool copy (its cost is the file write), so it is unchanged.
2. **`HPCPFused`** (`3645acc`, `src/algorithms/spectral/hpcpfused.{h,cpp}`). The chords (36
   bins) and tuning (120 bins) HPCPs are configured identically except size; one pass over the
   peaks computes both — the hypothesised fundamental and `log2(f / ref)` once per peak and
   harmonic position, every bin by HPCP's own expressions in HPCP's order. Bit-identical to the
   two HPCPs it replaces on every frame of the real rows (`spike-x6/hpcp-bench.txt`). The key
   HPCP is untouched.
3. **Shared decode** (`6f669a9`, `src/essentia/utils/audioloader_spool.h`). The first pass's
   `AudioLoader` (the one hashing the packets) also records what it pushes — one record per
   `process()` call, the interleaved float bytes it converted, plus the metadata tokens it
   pushes at reset — and the loaders of the replay-gain pass and both descriptor networks replay
   those records instead of opening the file, through the same copy routine. The spool is held
   in memory only, never on disk, under a per-track cap: profile key `x7.spoolMaxBytes`
   (default 256 MiB, ~12.7 min of 44.1 kHz stereo); a track estimated above it, or one that
   crosses it, is decoded by every pass as before. `x7.sharedDecode: 0` switches it off. A
   build that predates `.7` ignores both keys.
4. **Streaming Danceability and DynamicComplexity** (`820c85e`, B5). Both stored every mono
   44.1 kHz sample in a pool and ran the standard algorithm at the end — 352,800 B per
   audio-second for the pair. They now stream: each keeps the frame being filled and its small
   per-frame series, and does the arithmetic with the standard algorithm's own steps over the
   same samples in the same order.

Incremental build (`run-x6-build.sh`, one-shot scheduled task, `-j6`). `essentia_streaming_
extractor_music.exe`: MD5 **`0fb6f804f8f53dad65c4c963e6447e47`** (`220d877`). It replaced
the three-change build, `6af75d493dfb3b4949ec1fd433b4c58c` (`9844483`), which was never released.

**Verification** (`spike-x6/`, against the `.6` drop 2 outputs on the strict gate tools —
coverage, freshness and finite/non-finite checks — 45 decodable rows, 37 exactly repeating):

- Every exactly-repeating row identical in the main output and byte-identical in the frames
  file, with the spool on (`field-table-sraw-c3raw.txt`, `field-table-sscan-c3scan.txt`,
  `gate-edges-sscanedges-c3scanedges.txt`) and with it forced off by a 1-byte cap
  (`field-table-sscanedges-c3zscanedges.txt`, `gate-edges-sscanedges-c3zscanedges.txt`); the
  APE row, whose packets carry 64 frames, is among them. Noisy rows: every family band clean
  (family-beyond 0 in each table). The per-key bands are NOT all clean: strict-beyond 12 in the
  full output, 1 in the scan profile, and 1 with the forced over-cap path (the `totals` lines of the
  three field tables). An independent review counted one more in the spool-on scan + edges main
  output, which has no field table committed. None of these keys was repeated, so they are
  unresolved as noise, not shown to be noise. Two family flags on one AAC file in change 1 were
  shown to be the extractor's own run-to-run spread by repeating the row on both builds
  (`noise-r_m4a_2-drop2*.txt`); those are different keys. A same-binary control taken for
  change 4 puts the per-key counts in scale: the three-change build against a second run of
  itself gives strict-beyond 133 on the full output (`field-table-c3raw-c3raw2.txt`) and 9 in
  the scan + edges shape (`field-table-c3pa-c3pb.txt`).
- Change 4 against the three-change build: `danceability`, `dynamic_complexity` and
  `average_loudness` identical on every row in all six pass shapes (`b5-exact.txt`); exact rows
  identical; frames gates pass with the spool on and forced off (`gate-edges-c3scanedges-
  b5scanedges.txt`, `gate-edges-c3zscanedges-b5zscanedges.txt`); noisy-row movement at the
  same-binary level — strict-beyond 127 against 133 on the full output, 7 against 9 and 8 in
  the scan + edges shape, family 0. Natively, streaming against standard bit for bit at chunk
  sizes from 1 sample to the whole track, synthetic signals and two real files: 232/232
  (`b5-streaming-test.txt`).
- The spool, natively under ASan and UBSan: 19/19 (`spool-test.txt`); the first run found that
  a cap below one block could never record, fixed before the build. Decoder fixtures 8/8.
- Memory (`c3-mem-cap256m.txt`, `c3-mem-cap100m.txt`, same binary, spool on vs off): peak
  working set rises by the spool — 352,800 B per audio-second for stereo, half for mono —
  within +4.5 MiB, and not at all for a track over the cap. The extractor's own peak was
  106.6 MiB + 0.414 MiB per audio-second before change 4 (`mem-fit.txt`; `ram-tiers.txt`
  projects that build for 4 / 8 / 20 workers on 8 / 16 / 32 GB). With change 4 it is
  109.1 MiB + 0.066 MiB per audio-second, spool off, 10 real rows, residuals within 1.6 MiB
  (`b5-mem.txt`; spool on: `b5-mem-spool.txt`); every row's saving equals the 352,800 B/s the
  two pools held.
- Disk: the spool code has no file API, and the bytes the process writes (`written_mib`,
  GetProcessIoCounters) differ with and without the spool only by its log lines on stdout.

**Measurement** (production-shaped corpus — rows above 44.1 kHz converted first, as a truedat
scan does; scan + edges profile; two passes each): the HPCP rows fall by ~2.8 % of the
extractor's time (`profile-c1pa-vs-c2pb.txt`); the shared decode, same binary on vs off,
−8.5 % (`profile-c3na-vs-c3pb.txt`, `decode-share-c3.txt`) — the three replaying loaders from
~2.5 s each to ~0.2 s, recording +0.4 s on the first pass. By codec: WMA Lossless and APE
−25 to −28 %, 44.1 kHz FLAC / MP3 / AAC 0–9 %, hi-res after truedat's conversion about 0.
Against `.6` drop 2 the corpus runs ~16 % faster (`profile-p0a-vs-c3pb.txt`); about 11 points
are attributable to the two changes, the rest is between-pass spread on a shared machine.
Change 4's own rows (Danceability, DynamicComplexity) cost +0.69 s over a 75.6 s corpus; its
passes ran under other load (algorithms it does not touch were 12–17 % slower too), so no
whole-run timing is claimed for it.

Not verified: the AudioLoader record / replay code under a sanitizer (the spool itself is;
the loader code is shown by the identical outputs); the mid-stream abandon on a real file
(no duration-less decodable stream in the corpus — unit-tested); more than two channels
(the loader refuses them before any spool; truedat downmixes first); a physical 8 GB machine;
a library scan.
