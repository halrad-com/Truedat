# Truedat Backlog

## Seed mbxmoods.json from AcousticBrainz/MusicBrainz

Instead of scanning every file with Essentia (hours for large libraries), match tracks
against AcousticBrainz by MusicBrainz Recording ID (via Chromaprint fingerprint or
metadata lookup) and pull pre-computed Essentia features. Only scan files that don't
match. Could seed on first run, then do lazy background verification/update via MBXHub
Shell.

**Depends on:** Synthetic library catalog infrastructure (same data source).

## DSD / Non-PCM Format Support

Convert DSD, multi-channel, and other non-PCM formats via ffmpeg before Essentia
analysis. Design doc at `docs/dsd-conversion-plan.md`.
