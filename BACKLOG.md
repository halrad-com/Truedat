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

**~~Phase 3 — `hfEnergyRatio`~~ DONE** (commits below). Two concurrent ffmpeg
passes per track (total RMS + `highpass=f=22050` filtered RMS) at the source's
native sample rate; emits `hfEnergyRatio` (0..1) and `hfEnergyMethod` (frozen
tag). Only populated when `sourceSampleRate > 44100` (CD-rate files have no
Nyquist headroom). Catches spectral fake-hi-res that `bitUsage` alone can't —
an upsampler that adds dither fools `bitUsage` but can't fabricate energy
above the original Nyquist. The originally-planned full `spectralCeilingHz`
(via FFT) was descoped to this single-number ratio: ~30 LOC vs ~300, zero new
deps (ffmpeg's `highpass` filter does the work), one number that's easy to
threshold. If finer-grained spectral data is needed later, a real FFT pass
can be added as Phase 5+. See
[`docs/plans/2026-05-18-data-plumbing-phase3.md`](docs/plans/2026-05-18-data-plumbing-phase3.md).

**~~Phase 4 — `truedat.*` verdict block (corpus1-tuned)~~ DONE**. Implementation
shipped: TruedatVerdict class, ComputeTruedatVerdict helper, four-string-enum
output (`yes` / `no` / `unknown` / `n/a`), multi-signal weighted voting with ±0.7
threshold, codec-aware applicability gates, inline emission at write time so
threshold changes don't require rescans, `--audit` per-signal vote+weight trace.

**Corpus-validated** against a 23-file hand-built test set at
`C:\Users\scott\Music\_truedat-corpus\` covering real hi-res (Pink Floyd Animals
24/192, Pink Floyd The Wall 24/96, Foo Fighters 24/96 with shaped dither, NIN
24/96, Sleepy Lagoon 24/48), genre-diverse CD-rate FLACs + WAV, ffmpeg-upsampled
fake hi-res, real-LAME 320 originals (via standalone `lame.exe 3.100`),
128→320 and 320→320 LAME chains, Lavc native transcode, CDDA-narrow-band
false-positive case, and LP-rip MP3s with custom encoder strings. Final
scorecard: **20/23 (87%) hi-res, 21/23 (91%) transcode**.

**Tuning applied** based on corpus findings: dropped spectralRolloff (Signal D —
false-positives on naturally narrow-band music); dropped `bitrate >= 256`
inner gate on Signal B (VBR `--preset extreme` dips below 256 on simple
content); method tag bumped from `truedat-v1-untuned-2026-05-18` to
**`truedat-v1-corpus1-2026-05-18`**.

**5 remaining mismatches are documented signal-gap limitations** (NOT bugs):
- ~~3 hi-res misses: ffmpeg-upsampled fake 24/96/192 verdict "yes"~~
  **Closed by Phase 5** — see entry below. All 3 now verdict "unknown"
  (block suppressed); no longer falsely classified as real.
- 2 transcode misses: LAME→LAME re-encode chains verdict "no" — second LAME
  encode rewrites Xing tag masking source. Needs cascade-encode artifact
  detection (Phase 5+).

Plan: [`docs/plans/2026-05-18-data-plumbing-phase4.md`](docs/plans/2026-05-18-data-plumbing-phase4.md).
Validation review: [`docs/reviews/2026-05-18-phase4-corpus-validation.md`](docs/reviews/2026-05-18-phase4-corpus-validation.md).

**~~Phase 5 — FFT-based hi-res signal (`hfSpectralStructure`)~~ DONE**.
Hand-rolled radix-2 Cooley-Tukey FFT in `Truedat/Fft.cs` (~135 LOC, pure-
managed, zero new deps). `ComputeHfAnalysis` replaces `ComputeHfEnergyRatio`:
single ffmpeg s32le pipe, 4096-sample Hann-windowed 50%-overlap walk over
the same 30s mid-track segment — net **−1 subprocess per track** vs Phase 3
(was 2 RMS passes, now 1 FFT pipe). Emits `hfSpectralStructure: { flatness,
peakToMean, imagingSymmetry, method }` alongside a bin-sharp `hfEnergyRatio`.
Signal F (weight 0.35, lossless-only gate) added to the hi-res verdict.
`hfEnergyRatio` Phase-3 thresholds retuned: bin-sharp values run 3 orders
of magnitude below the old IIR-leaked values, so the `+1` threshold dropped
from 0.001 to 1e-5 and the unreliable `-1` vote was dropped entirely
(Signal F handles fake detection). Method tag bumped to
**`truedat-v1-fft-corpus1-2026-05-18`**. Corpus re-validation: 5/5 real
hi-res still verdict "yes"; 3/3 ffmpeg-upsampled fakes flipped from "yes"
to "unknown" (block suppressed) — clean win on the gap that motivated the
phase. Transcode verdicts unchanged (Signal F is hi-res only). Inline
`--self-test` flag verifies the FFT primitive (Parseval, synthetic tones,
Hann cache, error handling). New scorecard: **23/23 hi-res classified
correctly** (5 yes + 3 unknown + 15 n/a), transcode unchanged at 21/23.
Plan: [`docs/plans/2026-05-18-data-plumbing-phase5-fft-hires-signal.md`](docs/plans/2026-05-18-data-plumbing-phase5-fft-hires-signal.md).

**Phase 5+ candidates** (ordered roughly value/cost — full discussion in resume doc):
1. ~~FFT-based hi-res signal to close the ffmpeg-upsample-fake gap~~ DONE (Phase 5)
2. Encoder string whitelist/blacklist (helps LP rips, EAC, dBpoweramp)
3. ML weight tuning spike (sklearn logistic regression → calibrated thresholds)
4. DSD/DSF codec support (currently fails analysis) — **P4, deferred**.
   Phase 5.1 catches `.dsf` / `.dff` / `.dsd` cleanly at scan entry: rows
   land in `mbxmoods-skipped.csv` with reason `unsupported codec: DSD`,
   no entry in `mbxmoods.json` or `mbxmoods-errors.csv`, console emits
   `[skipped DSD]`. User-visible failures are gone; full DSD-to-PCM
   support (likely via an ffmpeg `dsd2pcm` bridge) is still P4.
5. AAC ESDS-box encoder fingerprint (MP3 LAME tag equivalent for AAC/M4A)
6. Verdict-only re-emit mode (tune thresholds on a 70k library without rescan)
7. LAME→LAME re-encode chain detection (the remaining transcode gap)

**Mood-enhancement axes** (decided 2026-05-18 — most are cross-repo handoffs
or deferred; see [`docs/plans/2026-05-18-mood-axes-mbxhub-handoff.md`](docs/plans/2026-05-18-mood-axes-mbxhub-handoff.md)
for the Path A architecture):

| Axis | Owner | Status |
|---|---|---|
| **Tension** (P1) | MBXHub `mood-formulas` spec | In flight via dispatch B-20260518-204500 — all inputs already in `mbxmoods.json`; Path A |
| **Dominance** (P1) | MBXHub `mood-formulas` spec | Same dispatch as Tension; ships as a follow-on commit per "one at a time" |
| **Genre fingerprint** | — | **CUT.** Existing ID3 `genre` tag is already in `mbxmoods.json`; audio-derived prediction would need either ~50-200 MB Essentia SVM models (blows up the single-exe distribution) or an ONNX classifier (gated on the §5.0 ORT spike). Maybe revisit as part of a future ML extension once the ORT path is unlocked for VAM. |
| **Section-aware mood** (P2) | Truedat extraction | Parked — structural schema change (per-section arrays vs scalars); needs MBXHub-side consumer changes and a new validation corpus. Future phase. |
| **VADER lyrical sentiment** | — | **PARKED indefinitely.** Lyrics supply chain — runtime fetching is off-limits per offline-first invariant; library coverage = whatever fraction has tagged USLT/SYLT (low). Revisit only if a lyrics-curation flow exists upstream. |
| **VAM (vocal affect)** | Truedat (in-process ONNX) | Active design exists at [`docs/plans/2026-05-16-vader-vam-roadmap.md`](docs/plans/2026-05-16-vader-vam-roadmap.md); gated on the §5.0 ORT verification spike (1 day, not yet run). |

**Phase 5 — ML-derived weights for the explicit voter** (spike, not a
rewrite): once the explicit voter from Phase 4 is running on the live
library, every scanned track becomes a candidate for a labeled corpus.
After ~200+ hand-reviewed tracks per question accumulate, run a
logistic-regression spike in Python (sklearn) against the verdict-input
features. The learned coefficients become **better starting thresholds
for the explicit voter** — keep the explicit voting algorithm (debuggable,
tunable, no retraining loop), just feed it ML-derived weights instead of
hand-picked ones. Same output schema, no consumer change. Full ML
inference in production is deferred further still — the explicit voter
is the labeled-data accumulator first.

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

## Energy-threshold VAD for podcasts / audiobooks (future)

Idea captured 2026-05-19. Not on the active VAM roadmap.

The VAM roadmap deliberately skips energy-threshold VAD because energy is
the wrong signal for music — music has continuous full-band energy whether
vocals are present or not, so RMS thresholding tags every track as
"vocal-everywhere" and the gate (`pure-instrumental returns no V/A`) fails.
Silero VAD is the right tool for music and is what S2.3 / S2.3.5 ship.

**But** for podcast / audiobook material, energy thresholding is the
*correct* baseline: real speech has silence between utterances, the
silence-vs-utterance contrast is exactly what RMS thresholding picks out,
and Silero would be overkill (and slower). A future `--content-type
podcast|audiobook|music` flag could pick the VAD strategy:
- `music` (default) → Silero VAD
- `podcast` / `audiobook` → energy threshold with a per-content-type
  silence-floor (e.g. -45 dBFS, 100 ms hangover)

Out of scope for the current VAM sprint. File when a podcast/audiobook
analysis use case actually materialises — Truedat's current scope is
music libraries (iTunes XML / MBXHub).

## ~~UNC source staging + experimental-signal opt-outs~~ DONE

Shipped 2026-06-09. Three changes in one feature branch (`feat/source-staging`,
8 commits, fast-forwarded to main):

- **UNC source staging.** `OpenStagedSource` copies UNC-sourced (and
  hardlink-failed local-source) audio files once to
  `%TEMP%\.truedat-stage\<guid>.<ext>`, then the 8-9 concurrent workers per
  track all read from the local copy. Wired at all three fan-out sites:
  `--analyze-file`, `--file-list`, MoodsMode. Net: 1× full network read per
  track instead of ~3× full + ≥3× partial. GUID-based ASCII-only staged
  filename also eliminates the 8.3 / non-ASCII downstream-tool footgun on
  UNC paths (hardlinks can't span volumes, so the existing
  `TryCreateHardlink` mitigation didn't help UNC). Per-track `using`-based
  cleanup keeps steady-state disk footprint at `parallel_worker_count`
  files; `CleanupOrphanedFiles` sweeps the staging dir at startup for
  crash-recovery.
- **Failure semantics: robocopy-style.** Per-track stage failures emit a
  single `  Warning:` stderr line, append a row to the errors CSV, and
  fall through to a direct read — scans never abort. Unwritable
  `--stage-dir` at startup is a hard error before the scan begins.
- **`--no-bitusage` and `--no-hf-analysis` opt-outs.** Suppress
  `ComputeBitUsage` / `ComputeHfAnalysis` at all three fan-out sites. JSON
  schema unchanged (same omit-when-null shape as the ffmpeg-absent case).
  The `truedat` verdict block still emits but with a reduced signal set —
  Signal A drops with `--no-bitusage`; Signals B + F drop with
  `--no-hf-analysis`. Cache canary is **not** widened to recognise these
  as "partial" — the fields are explicitly optional, matching the existing
  ffmpeg-absent install pattern; backfill via
  `--verify --backfill --backfill-level features`.

Flags: `--no-stage`, `--stage-dir <path>`, `--no-bitusage`,
`--no-hf-analysis` (documented in both `--help` blocks).

**Review-fix follow-on (shipped 2026-06-09 as `fix(scan)` 353919d on
`feat/source-staging-fixes`, fast-forwarded to main):**
- Mapped network drives (`Z:\` mapped to `\\server\share`) now stage too —
  per-root memoized `DriveInfo.DriveType == Network` check.
- `SourceHandle.SourceLastWriteUtc` snapshot captured inside
  `OpenStagedSource` immediately after `File.Copy`; recorded as
  `TrackEntry.LastModified` so a tag touch between copy and persist can't
  invalidate the recorded mtime against the analyzed bytes.
- Staged extension sanitized to `.bin` when the source ext isn't ASCII
  printable.
- Cache tiers 2/3/4 + cache-miss share one lazily-opened staged copy via
  `EnsureStagedSrc()` instead of re-reading the source N times across the
  network. Tier-1 (path-mtime hit) still skips staging entirely.
- Three near-identical per-track fan-outs collapsed into a shared
  `RunSourceWorkers` helper returning a `WorkerResults` bundle.
- End-of-scan `staging: N staged [, M direct-fallback]` summary; stderr
  warning when >5% of stages fall back so wedged stage-dirs surface
  visibly instead of silently slow.

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
