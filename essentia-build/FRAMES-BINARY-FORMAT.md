# ESPB v1 — the extractor's binary frames file

Written by `BinaryPoolOutput` (`essentia-src/src/algorithms/io/binarypooloutput.{h,cpp}`),
new in extractor build `output-x64.5`. It replaces the `_frames` JSON/YAML file **only when
asked for** on the command line; nothing about the main output or the default call changes.
This document is the reader's contract: the C# side is written from it.

## Invocation

```
essentia_streaming_extractor_music.exe <input_audio> <output_json> [profile] [--frames-bin <path>]
```

| call | main output | frames |
|---|---|---|
| `exe in out.json` | `out.json` (as `.4`, plus `metadata.version.extractor_build`) | none |
| `exe in out.json profile.yaml` with `outputFrames: 1` | `out.json` | `out.json_frames` — YAML/JSON via `YamlOutput`, exactly as `.4` |
| `exe in out.json profile.yaml --frames-bin frames.bin` with `outputFrames: 1` | `out.json` | `frames.bin` — ESPB, **instead of** `out.json_frames` |
| `exe in out.json --frames-bin frames.bin` (no profile) | `out.json` | none; stderr warns `--frames-bin given but the profile does not set outputFrames` |

`--frames-bin` must be the literal token followed by a path; any other shape prints usage and
exits 1 (as a wrong argument count always did). The frames pool is only produced when the
profile sets `outputFrames` — the flag changes *how* frames are written, never *whether*.

**Profile key `framesInclude`** (optional, additive): a YAML list of descriptor-name prefixes.

```yaml
outputFrames: 1
outputFormat: json
framesInclude: [lowlevel.mfcc, lowlevel.spectral_centroid, tonal.hpcp, lowlevel.loudness_ebu128, rhythm.beats_position, rhythm.beats_loudness]
```

A descriptor named `N` is written when the list is absent/empty, or when some prefix `P` has
`N == P` or `N` starts with `P + "."` — the same rule `Pool::descriptorNames(ns)` uses. So
`lowlevel.loudness_ebu128` selects `lowlevel.loudness_ebu128.momentary` and `.short_term`,
while `rhythm.beats_loudness` does **not** select `rhythm.beats_loudness_band_ratio`. A
one-element list parses as a single string and is handled. `framesInclude` only affects the
binary writer; the `_frames` JSON path ignores it (that path is byte-for-byte `.4`).

## Writer limitation — the output path must be ASCII

**`--frames-bin` paths must be ASCII. A non-ASCII path silently writes to the wrong
filename.** Measured on the `.5` exe, 2026-10-12:

| path | result |
|---|---|
| input audio (CJK filename) | **works** — read and fully analyzed |
| `<output_json>` (CJK filename) | **mojibake filename** |
| `--frames-bin` (CJK filename) | **mojibake filename** |

Cause: argv is fine — `streaming_extractor_music.cpp:131` is `#ifdef _WIN32` and our MinGW
build compiles it, so `CommandLineToArgvW` + `WideCharToMultiByte(CP_UTF8)` delivers proper
UTF-8, and ffmpeg converts UTF-8 to wide internally before opening the *input*. The gap is
the **output** side: `BinaryPoolOutput` opens with a narrow `ofstream`, like
`yamloutput.cpp:394`, so the UTF-8 bytes get reinterpreted through the active ANSI codepage
(CP1252 here) and land as a garbled name.

**The failure mode is the dangerous kind, so readers must not rely on the log line:** the
process **exits 0** and prints `Writing binary frames to file <the correct name>`, then the
bytes appear under a different name. The content is intact (a 3,433,814-byte ESPB file with
correct contents, just misnamed). A caller that opens its expected path gets file-not-found
with no diagnosis anywhere in the output.

Mitigation, and it is load-bearing rather than incidental: **callers keep frames paths
ASCII by construction.** truedat's C4 producer does this already — `CreateExtractorOutputPath`
uses `--stage-dir` only when `IsAsciiPrintable(stageDir)` and otherwise falls back to
`Path.GetTempFileName()`, and the profile file goes the same way — so a non-ASCII
`--frames-bin` never reaches the extractor from truedat. Do not relax that guard without
fixing the writer.

Fixing it properly means a wide-char open in `BinaryPoolOutput` (and `YamlOutput`), which is
an extractor source change plus a rebuild. Not done; recorded here so the constraint is
visible to anyone writing a producer.

## Build identity

Both outputs carry the build id, so a reader never needs an MD5 table:

- main JSON: `metadata.version.extractor_build` = `"output-x64.5"` (string; `.4` and older have no such key)
- ESPB header: the `tag` field holds the same string

The constant is `MUSIC_EXTRACTOR_BUILD_ID` in `essentia-src/src/essentia/utils/extractor_music/extractor_version.h`.

## Byte layout

Everything is **little-endian**. Integers are unsigned. Strings are UTF-8, length-prefixed with a
`u32` byte count, no terminator. Floats are IEEE-754 binary32 — Essentia's `Real` is `float`
in this build (pinned by a `static_assert`), and values are written **unrounded**, straight
from the pool. There is no compression, no alignment padding and no checksum.

```
offset  size  field
------  ----  -------------------------------------------------------------
0       4     magic            'E' 'S' 'P' 'B'  (0x45 0x53 0x50 0x42)
4       u32   formatVersion    1
8       u32   byteOrderMark    0x04030201 when read as a little-endian u32
                               (bytes on disk: 01 02 03 04); anything else = not LE, refuse
12      u32   descriptorCount  number of entries in the descriptor table
16      u64   payloadOffset    absolute offset of the first payload byte
                               (== offset where the table ends; a consistency check)
24      str   tag              u32 length + bytes (the build id; may be empty)
        ---   descriptor table: descriptorCount entries, back to back, sorted by name ---
        str   name             u32 length + bytes, e.g. "lowlevel.mfcc"
        u32   dtype            see table below
        u32   flags            bit 0 (value 1) = SINGLE: the pool value was set once
                               (Pool::set) rather than accumulated per frame (Pool::add).
                               Other bits are 0 in v1.
        u32   count            see dtype table
        u32   dims             see dtype table
        u64   offset           absolute file offset of this descriptor's payload
        u64   length           payload byte length
        ---   payloads: back to back in table order, starting at payloadOffset ---
```

Each table entry is `4 + len(name) + 4 + 4 + 4 + 4 + 8 + 8` bytes. Payloads are contiguous
(`entry[i].offset + entry[i].length == entry[i+1].offset`) and the last one ends at the file
size; the writer verifies the final size against this layout before returning.

### dtype

| dtype | name | pool source | count | dims | payload |
|---|---|---|---|---|---|
| 1 | `F32_SCALAR` | single `Real` (`Pool::set`) | 1 | 1 | one f32 (4 bytes) |
| 2 | `F32_VECTOR` | `vector<Real>` — either a per-frame series of a scalar descriptor (flags=0) or one vector set once (flags=SINGLE) | n values | 1 | n × f32 |
| 3 | `F32_MATRIX` | `vector<vector<Real>>` — per-frame series of a vector descriptor (e.g. `lowlevel.mfcc`: frames × 13) | rows (frames) | columns | rows × dims × f32, row-major (frame 0's dims values, then frame 1's, ...) |
| 4 | `STRING` | single `string` | 1 | 0 | u32 len + bytes |
| 5 | `STRING_VECTOR` | `vector<string>` | n strings | 0 | n × (u32 len + bytes) |

`length` always equals the payload's exact byte count: `4 × count × dims` for dtypes 1–3; the
sum of `4 + len` over the strings for 4–5.

**Not written** (skipped with an `E_WARNING` on stderr naming the descriptor): ragged
`vector<vector<Real>>` (rows of differing length), `vector<vector<string>>`, `Array2D`,
`StereoSample` streams and tensors. None occur in the music extractor's frames pool today; the
rule exists so a future descriptor of such a kind degrades to "absent" rather than a corrupt file.

### Reading one descriptor in C#

Read the 24-byte header, the tag, then `descriptorCount` table entries into a dictionary by name.
To get `lowlevel.mfcc`: seek to `offset`, read `length` bytes, `MemoryMarshal.Cast<byte, float>`
(or `Buffer.BlockCopy` into a `float[count * dims]`), index `[frame * dims + coeff]`. Nothing
else in the file has to be touched. Reject the file if the magic, `formatVersion`, or
`byteOrderMark` are off, or if the table does not end exactly at `payloadOffset`.

Frame cadence is a property of the extractor build, not of the file: lowlevel series are
2048/1024-hop at the 44.1 kHz analysis rate (43.07 frames/s), tonal series (`tonal.hpcp`,
chords) at 4096/2048 (21.53 frames/s), `lowlevel.loudness_ebu128.momentary` / `.short_term`
at 10/s, and `rhythm.beats_position` / `rhythm.beats_loudness` are one value per detected
beat. Two series of the same cadence have the same `count` for the same track.

## Worked example

A real file: `essentia-build/spike-x5/sine_curated.bin`, written by `.5` for a 60 s 44.1 kHz
stereo sine with `spike-x5/frames-curated.yaml` (the six-prefix `framesInclude` above).
337,102 bytes, 9 descriptors. The first 96 bytes (`od -A d -t x1`):

```
0000000 45 53 50 42  01 00 00 00  01 02 03 04  09 00 00 00
        'E''S''P''B' version=1   byteOrderMark  descriptorCount=9
0000016 56 02 00 00 00 00 00 00  0c 00 00 00  6f 75 74 70
        payloadOffset=0x256=598  tagLen=12    't''a''g'…  "outp
0000032 75 74 2d 78 36 34 2e 35  23 00 00 00  6c 6f 77 6c
        ut-x64.5"                nameLen=35   "lowl
0000048 65 76 65 6c 2e 6c 6f 75 64 6e 65 73 73 5f 65 62
        evel.loudness_eb
0000064 75 31 32 38 2e 69 6e 74 65 67 72 61 74 65 64  01
        u128.integrated"                                dtype=1 (F32_SCALAR) …
0000080 00 00 00  01 00 00 00  01 00 00 00  01 00 00 00  56
        …         flags=1       count=1      dims=1       offset=0x256 …
```

so the first table entry is `lowlevel.loudness_ebu128.integrated`, a single `Real`
(dtype 1, flags SINGLE, count 1, dims 1) whose 4-byte payload starts at offset 598 — the
very first payload byte, because entries are sorted by name and payloads follow table order.
The full table as the checker prints it:

```
lowlevel.loudness_ebu128.integrated      f32_scalar  flags=1 count=1     dims=1
lowlevel.loudness_ebu128.loudness_range  f32_scalar  flags=1 count=1     dims=1
lowlevel.loudness_ebu128.momentary       f32_vector  flags=0 count=597   dims=1    (10/s)
lowlevel.loudness_ebu128.short_term      f32_vector  flags=0 count=571   dims=1    (10/s)
lowlevel.mfcc                            f32_matrix  flags=0 count=2585  dims=13   (43.07/s × 13)
lowlevel.spectral_centroid               f32_vector  flags=0 count=2585  dims=1
rhythm.beats_loudness                    f32_vector  flags=0 count=109   dims=1    (per beat)
rhythm.beats_position                    f32_vector  flags=1 count=109   dims=1    (per beat; SINGLE — set once as one vector)
tonal.hpcp                               f32_matrix  flags=0 count=1293  dims=36   (21.53/s × 36)
```

Arithmetic check against the file size: header 24 + tag (4 + 12) = 40; table = 9 entries ×
36 fixed bytes + 234 name bytes = 558, so `payloadOffset` = 598 (= 0x256, as the header says);
values = 1 + 1 + 597 + 571 + 2585·13 + 2585 + 109 + 109 + 1293·36 = 84,126 floats × 4 = 336,504
payload bytes; 598 + 336,504 = 337,102 = the file size. A reader should still trust each entry's
`offset`/`length` rather than recompute them.

`rhythm.beats_position` carries `flags=SINGLE` while `rhythm.beats_loudness` does not: the
beat tracker emits positions once as a whole vector (`Pool::set`), the loudness is accumulated
beat by beat (`Pool::add`). Both are plain `F32_VECTOR`s of the same length; the flag is
informational.

### Checking a file

`spike-x5/check_espb.py` (run with `python -I`) parses any ESPB v1 file, prints the table,
verifies the layout invariants (table ends at `payloadOffset`, payloads contiguous, file size
matches), and optionally cross-checks every descriptor against the extractor's `_frames` JSON
for the same track (names, dims, counts, values within 1e-6 relative) and against an expected
`framesInclude` list. On the spike WAV the all-descriptor file (105 descriptors, 856,759 values)
matched the JSON frames to a max relative difference of 5e-12.

## Run-to-run noise (what the reader should expect)

The extractor is not bit-deterministic on real audio (known since `.3`, see
`OUTPUT-BUILDS.md`). At frame level this shows up as a small number of frames — frame 0, the
first ~1.3 s on a FLAC with a quiet lead-in, and the last frame — differing between two runs of
the **same** binary in the spectral-shape descriptors (`spectral_centroid/contrast/entropy/
kurtosis/skewness/spread/rolloff`, `*bands_crest/flatness_db/kurtosis/skewness/spread`,
`dissonance`, `pitch_salience`, `zerocrossingrate`, a few `gfcc` cells). `lowlevel.mfcc`,
`tonal.hpcp`, the `melbands`/`barkbands`/`erbbands` matrices, `loudness_ebu128.*` and
`rhythm.beats_*` were identical across runs on every file tested. A consumer comparing two
frames files of one track must therefore not expect byte equality; descriptor names, counts and
dims are stable.
