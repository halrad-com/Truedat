# Truedat Backlog

## Authenticity-data plumbing — Phase 2.5 (bitUsage) + Phase 3 (spectralCeiling)

Builds on the shipped Phase 1 (`bitDepth` + `encoder` in `fingerprint.v1` +
`--verify --backfill`, see
[`docs/plans/2026-05-18-data-plumbing-phase1.md`](docs/plans/2026-05-18-data-plumbing-phase1.md))
and Phase 2 (MP3 LAME tag block, see
[`docs/plans/2026-05-18-data-plumbing-phase2.md`](docs/plans/2026-05-18-data-plumbing-phase2.md)).

**~~Phase 2.5 — `bitUsage` block~~ DONE** (commits below). ffmpeg-pipe PCM walk
over 30 s mid-track; emits `lowestNonZeroBit`, `bottomBitActivity`,
`effectiveBits`, `samplesAnalyzed`, `method`. Wired as a 5th concurrent task
in all three scan paths (`--analyze-file`, `--file-list`/`--folder`,
MoodsMode). Cache hits preserve cached values; legacy entries pick it up on
the next full scan (not added to the re-extract canary — would force
unwanted Essentia re-runs across the whole library on first upgrade).
**Detection-only valid for codec ∈ {flac, alac, wav, aiff} claiming
`bitDepth ≥ 24`** — lossy decode floods the LSBs with reconstruction noise,
so the metric is meaningless for MP3/AAC and Phase 4's verdict block must
gate it accordingly. See
[`docs/plans/2026-05-18-data-plumbing-phase2.5.md`](docs/plans/2026-05-18-data-plumbing-phase2.5.md).

**Phase 3 — `spectralCeilingHz`**: requires either a custom Essentia descriptor
(rebuild extractor) or a dedicated FFT pass at native sample rate over
[22 kHz, Nyquist]. The existing 85 % `spectralRolloff` and the 4-band
`spectralEnergy*` fields cannot answer "is there content above 22 kHz?" —
see self-review note in the Phase 2 plan. Needed for catching spectral
fake-hi-res (orthogonal to bit-depth fakery caught by `bitUsage`).

**Phase 4 — the `truedat.*` verdict block**: consumes Phase 1 / 2 / 2.5 / 3
fields and emits booleans + confidence (`truedat.lossySourceLikely`,
`truedat.hiresGenuine`, `truedat.spectralCeilingHz`, `truedat.confidence`).
This is the user-facing answer; everything above is data plumbing.

### Known detection challenges (must inform Phase 4 design)

Single-signal heuristics fail in well-documented ways. Real-world cases the
verdict block must handle without making them worse:

| Case | Why naive detectors fail | What we need |
|---|---|---|
| **False positive on CDDA / "Fakin the Funk"-style rips** | Naive spectral-rolloff sees the ~15 kHz ceiling and flags it as transcoded; ignores that the rip is genuinely from a low-HF source (pre-1985 recording, deliberately rolled-off master, dark instrumentation) | LAME-tag absence on a non-Xing MP3 + spectral rolloff is the joint signal, not either alone. A genuine CDDA-→MP3 rip with a real LAME tag and `lowpassHz: 19500` shouldn't be flagged just because the *music* lacks 16+ kHz content. |
| **False negative on 320→320 transcode** | Second encoder preserves the first's lowpass character; spectral evidence is gone | Encoder fingerprint is the only handle — original 320k from LAME has `Mp3LameTag.version: "LAME3.x"`; transcoded 320k typically has either no LAME tag (ffmpeg) or a *different* LAME version (re-encoded via lame). Music CRC drift across the file is a secondary tell. |
| **False negative on 128→320 transcode** | Second encoder can introduce HF noise above what the source had, making rolloff look "wide enough" | Same as above — LAME tag absence + Lavc/Lavf encoder string. Also: if the source LAME tag survived re-muxing (rare), `lowpassHz: 16000` on a "320k" file is a definitive flag regardless of spectral measurement. |
| **Genuine 16-bit FLAC with low HF** | Spectral-only detection would see "ceiling at 15 kHz" and call it fake | Don't apply hi-res detection to anything claiming `bitDepth < 24`. The detection is about the *bit depth claim*, not generic spectral analysis. |
| **Upsampled CD with dither added** | Adding dither in the upsample chain restores LSB activity, defeating naive `lowestNonZeroBit ≥ 16` | This is a real evasion. Detection becomes probabilistic. Multiple signals needed: `bitUsage.bottomBitActivity` distribution (real 24-bit is more uniform), `spectralCeilingHz` (Phase 3 — content above 22 kHz can't be faked by dither), encoder string forensics. |

**Implications for the verdict block:**

1. **Voting / weighting over signals**, not a single threshold. Each signal
   votes (yes/no/abstain) with a confidence weight. Verdict combines them.
2. **Refuse to verdict** when signals are weak / contradictory — emit
   `truedat.confidence: low` rather than guess.
3. **Codec-aware gating**: hi-res checks only apply to claimed-lossless
   claimed-≥24-bit files. Transcode checks only apply to MP3/AAC. Don't
   waste signal on inapplicable measurements.
4. **Treat older-recording corpora differently** (informed by genre /
   year metadata when available): "naturally low HF" is a legitimate
   class that mustn't be falsely flagged.

**Test corpus is needed.** Per the user's feedback (2026-05-18), we need
ground-truth pairs labeled by hand to validate the verdict block before
shipping. At minimum: a CDDA rip flagged by naive detectors but genuine
(e.g. Fakin the Funk), a known-good 24/96, a known-fake 24/96, a 128→320
transcode, a 320→320 transcode, a deliberately-rolled-off modern
production. Build this in Phase 4's planning sprint, not now.

## Vader-sprint (next planned work) — VADER lyrical sentiment

S1 of the VADER+VAM multimodal roadmap. Adds `lyrical.*` block to
`mbxmoods.json` per track that has lyrics (USLT/SYLT tag or `.lrc` sidecar).

**Decision recorded:** port VADER from Python to C# with trimmed lexicon
(rather than VaderSharp2 NuGet or vendoring the BobLd C# source). Rationale,
data, and the 3-option comparison live in
[`docs/analysis/2026-05-17-vader-source-selection.md`](docs/analysis/2026-05-17-vader-source-selection.md).

**Plan:** [`docs/plans/2026-05-17-vader-vendoring-port.md`](docs/plans/2026-05-17-vader-vendoring-port.md)
— ~610 LOC port across 4 files, embedded trimmed lexicon (~85 KB),
LICENSE/NOTICE/`--licenses` flag, integration hook in the scan pipeline.

**Effort:** 1-2 focused days end-to-end. Not gated by the §5.0 SPIKE (that's
VAM-only); VADER ships independently.

**Out of scope for this sprint** (deferred to follow-up plans):
- Tier-2 cache `lyricalSourceHash` re-run logic (S1.2)
- `--enrich vader --file-list` mode (cross-cutting)
- LRCLib fetch (MBXHub-side)
- Everything VAM (S2+)

**Parent roadmap:** [`docs/plans/2026-05-16-vader-vam-roadmap.md`](docs/plans/2026-05-16-vader-vam-roadmap.md)

## ~~Opus support via ffmpeg transcode~~ DONE

Two changes layered on the existing ffmpeg-transcode pattern (the one that
already handled `more than 2 channels` downmix):

- **Scan-side retry on `Unsupported codec`** in `AnalyzeWithEssentia`. When
  essentia's `AudioLoader` rejects a file (e.g. every `.opus` track —
  this essentia build lacks libopus), `DownmixToStereo` transcodes the
  source to a stereo WAV and `AnalyzeWithEssentiaCore` re-runs against
  the WAV. Reactive (not preemptive on `.opus` extension) so it picks
  up future unsupported codecs without rewiring; failed first attempt
  is ~0.1s so the overhead is negligible. Same temp-WAV cleanup as the
  multi-channel branch.

- **Standalone `--transcode` utility mode**: `--transcode <in>
  --transcode-out <out.flac> [--sample-rate N] [--bit-depth 16|24]`.
  Pure ffmpeg-driven opus/other → uncompressed FLAC. Defaults to source
  sample rate / bit depth via `ProbeAudio`; falls back to 48000/24 when
  the source reports float-internal (opus's container has no bit
  depth). FLAC native sample formats — `s16` for 16-bit, `s32` with
  `-bits_per_raw_sample 24` for 24-bit; `-compression_level 0`.
  Timeout 300s matching `DownmixToStereo`. No essentia, no cache, no
  mbxmoods.json. Mutually exclusive with all other standalone modes.

Acceptance: opus file that previously exited 1 in 0.1s with
`AudioLoader: Unsupported codec!` now analyzes end-to-end (exit 0, all
55 features populated, `fingerprint.v1.codec="opus"`,
`audioStreamSha256` set). Reduces (but doesn't close) the broader
"DSD / Non-PCM Format Support" item below — DSD likely errors with a
different essentia string and may need its own trigger.

Commits: `b9ebdc7` (code), `e4f1205` (binary).

## ~~Phase 2 hash-first identity (Track A)~~ DONE

`--hash-only --level fingerprint|stream` CLI for identity-only passes without
Essentia. `fingerprint.v1` composite (pathTail + fileSize + audio props + 64 KB
invariant-region MD5) is the ms-scale peer-pull ping primitive; `stream` level
adds durable `audioStreamSha256` over the audio region. Default Essentia scans
ride-along both `fingerprint.v1` and `audioStreamSha256` via two concurrent
tasks (6 total: essentia + fileMd5 + audioMd5 + chromaprint + fingerprintV1 +
audioStreamSha256), so every full scan emits an identity-complete row —
not just `--hash-only --level stream` runs. Zero new deps. Wire format frozen
at `docs/reference/identity-wire-format.md`; consumed by the MetaServer side
(Phase 2 Track B, separate repo).

Commits: `a6a0fe7` (Track A CLI + fingerprint.v1 ride-along), `10a2940`
(audioStreamSha256 ride-along in default mode), `eb241a3` (wire-format +
CLAUDE.md sync).

## ~~Seed mbxmoods.json from AcousticBrainz/MusicBrainz~~ DONE

Implemented `--seed-moods` command: bulk seeds mbxmoods.json from AcousticBrainz
pre-computed features via normalized artist+title matching (confidence 0.6). Tiered
confidence model never downgrades existing data. Also implemented `--synthesize` for
generating 430k-track synthetic test libraries. Python pipeline (`catalog-prep.py`)
handles download with SHA-256 manifest, retry/backoff, and atomic outputs.

Design: `docs/plans/2026-02-26-ab-seeding-and-robustness-design.md`

**Future:** MBXHub Shell integration for background incremental seeding (.6 sprint),
MBID tag lookup (tier 2), Chromaprint fingerprint matching (tier 3).

## ~~Batch File-List Analysis~~ DONE

Implemented `--file-list <path>` flag: reads file paths from a text file (one per
line, UTF-8, # comments), processes them in parallel via existing Essentia
infrastructure, POSTs per-track to `--meta-server`. Exit code 1 for partial
failures with JSON summary on stdout.

Plan: `docs/plans/2026-03-22-file-list-flag.md`

## ~~Concurrent fileMd5 + audioMd5 + chromaprint in Mood Analysis~~ DONE

Mood analysis now runs Essentia extraction, file MD5, audio MD5
(`essentia_streaming_md5.exe`), and chromaprint (`fpcalc.exe`)
concurrently per track via `Task.WaitAll`, so wall-clock is
`max(analysis, slowest-hash)` rather than sum. `TrackEntry` gains an
`AudioMd5` field; `mbxmoods.json` now emits `audioMd5` alongside
`fileMd5` (omitted when the MD5 tool is absent). MetaServer receives
all three identity tiers in one pass (`fileMd5`, `audioMd5`,
`chromaprint` + `chromaprintDuration`), satisfying its path → fileMd5 →
audioMd5 → chromaprint → metadataKey lookup walk without requiring a
separate `--fingerprint` run. Cache re-extract gate requires `audioMd5`
when `md5Exe` is available so rescans backfill it on pre-hash entries.

Commits: `a4424fe` (extended features), `e7cc22a` (review + fixes),
`e24a4d9` (inline fileMd5 + audioMd5 + chromaprint).

## ~~Extended Essentia Feature Set~~ DONE

Added 40 extended acoustic descriptors to `TrackFeatures` and `mbxmoods.json`:
loudness envelope (momentary, short-term, replay gain, DR/LRA), silence profile
(20/30/60 dB), spectral shape (rolloff, complexity, entropy, kurtosis, skewness,
spread, strong peak, decrease, energy + 4 energybands), high-frequency content,
Bark/ERB/Mel band shape statistics (crest, flatness, kurtosis, skewness, spread),
and rhythm/tonal aggregates (beats loudness, chords strength, HPCP crest + entropy).

All 40 are nullable — writer omits missing keys, reader tolerates older entries that
pre-date the set. Round-trip covers extract → mbxmoods.json → MetaServer ingest →
cache preservation. Commits: `72d8e65` (DR), `a4424fe` (extended 39).

Review: `docs/reviews/2026-04-18-extended-features-review.md`

## DSD / Non-PCM Format Support

Convert DSD, multi-channel, and other non-PCM formats via ffmpeg before Essentia
analysis. Design doc at `docs/dsd-conversion-plan.md`.
