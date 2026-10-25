# spike-x6 — output-x64.6 verification

Evidence and tools for the `.6` extractor build. Drop 1 (B1, decoder correctness) is
recorded here; the build record is the `.6` section of `../OUTPUT-BUILDS.md`.
Small text evidence and the scripts are tracked; generated audio, decoded PCM,
extractor outputs and native build trees are not (`.gitignore`) — every one of them
regenerates from the scripts below.

## What B1 fixes (`essentia-src/src/essentia/utils/audioloader_decode.h`)

| `.5` defect | `.6` behavior |
|---|---|
| A packet refused with `EAGAIN` was dropped; one frame received per packet | On `EAGAIN`, receive the pending frames and resend the **same** packet; receive until the decoder wants input |
| The drain packet was re-sent every iteration; the second send's `AVERROR_EOF` ended the drain after **one** delayed frame | Drain sent once; receive until `AVERROR_EOF` |
| Fixed 384,000-byte conversion buffer; `swr_convert` told the frame's sample count as capacity; clamp **after** the write | Capacity from the received frame and `swr_get_out_samples`; buffer grown on demand and reused; nothing clamped after a write |

Also in the same change: non-audio packets (cover art) are unreffed instead of
overwritten, every converted frame is unreffed, the frame is freed with
`av_frame_free`, and frames are pushed downstream in chunks of at most 65,536 (a
whole frame is pushed now, and one request above the source's 262,144-token phantom
size throws).

## Corpus (`make-corpus.sh` → `manifest.tsv`, `corpus-md5.txt`, `corpus/`)

45 rows, one per codec / container / rate / channel layout, generated with the
shipped `dist/truedat/ffmpeg.exe` from a deterministic `aevalsrc` expression, plus
eight real files read in place from the operator's test bed (never copied, never a
library's catalog folder, never an iTunes XML). Synthetic rows regenerate
**byte-identically** (`corpus-md5.txt`; Ogg rows need `-fflags +bitexact`, since
the muxer otherwise draws a random stream serial).

- 3.77 s is divisible by no codec's frame size, so every row ends on an incomplete
  final frame; 0.05 s rows are shorter than priming plus one frame; `*_trunc` rows
  have bytes cut off the end (a damaged download).
- `*_bigblock` FLAC rows use block size 65,535: stereo 16-bit 44.1k
  (524,280 B per frame, overflowed `.5`), stereo 24-bit 96k, stereo 16-bit 192k,
  and a **mono control** (262,140 B fits `.5`'s buffer, so any `.5` loss on it
  would be the loop, not the capacity).
- Not a row: 192k + 24-bit + block 65,535 — FFmpeg's encoder writes it, but no
  FFmpeg (7.1.1 or 8.x) reads a frame back.
- `expect=unsupported`: Opus. The extractor's FFmpeg has no Opus decoder (plan B4).

## PCM parity (`run-parity.sh`, `pcm_parity.py`, `parity-*.txt`)

Loader side: `essentia_standard_audioloader_dump` (built with the extractor; writes
the `AudioLoader`'s output as interleaved f32le at the native rate and channel
count; mono writes the left channel only, which is all the loader fills).

Reference side, **both** run as `ffmpeg -i <in> -map 0:a:0 -c:a pcm_f32le -f f32le`
with no `-ar` / `-ac` — the loader converts sample format only (in rate = out rate,
in layout = out layout), so the reference must too:

- **`same` — the gate.** `ref/ffmpeg-7.1.1-mingw.exe`, cross-built by
  `ref/build-ffmpeg-ref.sh` from the extractor's own FFmpeg source, configure flags
  and MinGW toolchain, adding only the CLI, the audio plumbing filters and the
  f32le muxer/encoder. Any difference against it is the loader's.
- **`shipped` — informational** (`parity-*-shipped.txt`). `dist/truedat/ffmpeg.exe`
  is FFmpeg 8.x (libavcodec 63 against the extractor's 61): its float decoders
  differ in the last bits and it trims AAC end padding differently, so it measures
  FFmpeg drift as much as the loader.

Delay and padding: encoder delay and end padding (`AV_PKT_DATA_SKIP_SAMPLES`, MOV
edit lists) are applied inside libavcodec / libavformat on both sides. Neither side
trims anything itself; no offset is applied before comparing.

A `decode` row passes when the loader exits 0, its frame count equals the
reference's, and the PCM is byte-identical; an `unsupported` row passes when the
loader refuses the file.

## Decoder-state fixtures (`fixtures-*.txt`)

`essentia_test_audioloader_decodeloop` drives the loader's own loop
(`audioloader_decode.h`, included, not copied) with a scripted fake decoder:
multi-frame packets, send backpressure from asynchronous completion, both
together, four delayed frames at drain, drain sent once, a mid-stream decode
error, and a decoder that answers `EAGAIN` to everything (must not spin). Each
checks the exact frame sequence. `fixtures-legacy-native.txt` and
`fixtures-build-a.txt` are the RED runs against the `.5` loop.

## ASan (`asan/`)

`asan/build-ffmpeg-asan.sh` builds FFmpeg 7.1.1 natively under WSL with
`--toolchain=gcc-asan --disable-asm`: the `.5` overflow is a write **by**
`swr_convert`, and ASan sees neither an uninstrumented library nor nasm kernels.
`asan/run-asan.sh` builds `asan/asan_harness.cpp` (the loader's open sequence and
swr setup around the same header) and decodes every row with LeakSanitizer on;
`--legacy-capacity` is the instrument check — `.5`'s conversion on the big-block
rows, which must report the overflow.

## End-to-end band (`run-repeat.sh`, `band.py`, `repeat-band.*`, `field-table-*`)

`.5` run three times over the corpus (2-arg call, as truedat invokes it) gives the
same-binary band per key and per statistic family. `band.py cross` judges a
candidate against it two ways — STRICT (2× the key's own band; a key that never
moved between same-binary runs may not move) and FAMILY (2× its family's pooled
band, the `.5` method in `OUTPUT-BUILDS.md`) — with each row's PCM verdict beside
it, so movement is attributed to recovered audio, not excused by noise.

## Build A

`out/build-a` is an intermediate build: the loop moved into the shared header at
`.5` semantics, conversion unchanged. Its extractor is byte-identical to `.5` on
`../spike-x5/sine60.wav` (`sine-equivalence.txt`), so the RED rows recorded with
its tools are the `.5` loader's behavior.

## Regenerate

```
bash make-corpus.sh
wsl.exe bash ref/build-ffmpeg-ref.sh                      # long: scheduled task
bash run-parity.sh <label> <..._audioloader_dump.exe> [same|shipped]
python -I pcm_parity.py <label> [same|shipped]
gitbash.cmd run-repeat.sh x5 x5a x5b x5c                  # scheduled task
python -I band.py same x5a x5b x5c
python -I band.py cross x5a x6a [parity label]
wsl.exe bash asan/build-ffmpeg-asan.sh                    # long: scheduled task
wsl.exe bash asan/run-asan.sh [--legacy-capacity]
```
