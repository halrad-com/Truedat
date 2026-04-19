# Truedat Backlog

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
