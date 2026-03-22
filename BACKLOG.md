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

## DSD / Non-PCM Format Support

Convert DSD, multi-channel, and other non-PCM formats via ffmpeg before Essentia
analysis. Design doc at `docs/dsd-conversion-plan.md`.
