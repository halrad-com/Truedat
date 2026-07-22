# Changelog

All notable changes to Truedat. Format loosely follows [Keep a Changelog](https://keepachangelog.com);
Truedat versions are release-candidate tags (`vX.Y.Z-RCn`) on `main`.

## [Unreleased]

### Schema addition — tonal/rhythm extension wave (2026-07-22)

**Additive, NOT breaking** — all new keys are omit-when-missing; tolerant readers
(MBXHub) are unaffected. New per-track fields, populated on fresh analysis only
(Essentia-derived → not backfillable; legacy entries lack them until re-analyzed;
NOT part of the re-extract canary, so existing caches stay valid):

- **`keyVotes`** — nested block with all three tonal key profiles
  (`krumhansl` / `temperley` / `edma`), each `{key, scale, strength}`.
  Confidence + cross-profile agreement for Camelot/harmonic mixing.
- **`bpmFirstPeak` / `bpmFirstPeakWeight` / `bpmSecondPeak` /
  `bpmSecondPeakWeight` / `bpmSecondPeakSpread`** — tempo-histogram peaks;
  half/double-time disambiguation.
- **`chordsKey` / `chordsScale` / `chordsHistogram[24]` / `chordsNumberRate`** —
  chord-level tonality.
- **`tuningFrequency` / `tuningEqualTemperedDeviation` / `tuningDiatonicStrength` /
  `tuningNontemperedEnergyRatio`** — tuning reference (~440 Hz) and temperament.
- **`averageLoudness`** — simple 0..1 loudness (distinct from the LUFS envelope).

### Schema change — FLAC identity is frame-anchored (2026-07-21)

`audioStreamSha256` / `audioHead64kMd5` for FLAC now cover the audio frames only
(`*Source: "flac-frames"`), because TagLib's invariant region includes FLAC
metadata blocks — embedded tag writes (e.g. MBXHub's mood field) drifted the old
hashes with byte-identical audio. Stored old-style values migrate automatically:
scans and `--verify --backfill` recognize and upgrade them in place (no
re-analysis); tag-drifted transition entries are rescued in the default scan
via an audio-props gate (tier 2.5, `FLAC re-key` summary line).

## [5.4.0] — 2026-07-11

### Removed — legacy fingerprint pipeline
- **`--fingerprint`, `--md5-only`, `--quick-fingerprint`, `--details`** modes and the
  binaries that backed them (`essentia_streaming_md5.exe`, `fpcalc.exe`). Identity is
  now pure-managed: `fingerprint.v1` + `audioStreamSha256`, computed on every scan.
- **`audioMd5` / `chromaprint` passthrough** — old scans' values are no longer carried
  forward; keys are ignored on read and **`--migrate` strips them** (with backup).
- **Tier-3 cross-MD5 cache tier** — the cross-SHA tier catches a strict superset
  (clean moves, preserved-mtime copies, moved-plus-retagged files), so nothing is lost.
- **`fileMd5` is now opt-in** — written only under `--file-md5`; without the flag,
  scans never write it and `--migrate` strips stored values. Nothing consumes it
  (MBXHub indexes `audioStreamSha256` only).

No rescan needed — existing `mbxmoods.json` files load unchanged; run `--migrate`
once to clean stripped fields out of the file.

## [5.3.9] — 2026-07-05

### Duplicate review workflow
- **`--duplicates`** now always writes a self-contained interactive **review page**
  (`mbxmoods-duplicates.html`) alongside the CSV/JSON, and prints it as a clickable
  console link. Offline, no server.
- Review page: **chunk-based include model** (groups start not-included; tick the ones
  to act on), pick keepers, **Build losers playlist** → downloads an `.m3u8` for
  review/removal inside MusicBee. Decisions persist in `localStorage`.
- **Folder-pair rollup** — duplicate groups that split across the same two folders roll
  into one "folder duplicate" card with a single keep-A / keep-B choice; "show tracks"
  renders the two folders **side by side, row for row**, with per-track keep overrides.
- **Play buttons** per file (shared HTML5 player; Firefox plays FLAC/MP3/OGG/WAV from
  `file://`) and **click-to-open-folder** links.
- **Track length**, bitrate, sample rate, bit depth, size, and SMFM shown per copy.
- **Keeper** ranking gained a **SMFM-tagged** tiebreaker (Sony 12-TONE copy wins on a
  quality tie) and a **genuine-over-fake-hi-res** rule.
- **Fake hi-res detection** — a copy that claims hi-res (>44.1 kHz or ≥24-bit) but has
  zero ultrasonic energy (`hfEnergyRatio ≈ 0`) is flagged as upsampled; in a duplicate
  group it's provably fake, so the keeper prefers the genuine original (e.g. a real
  16-bit master beats an upsampled 24-bit re-encode). Badged red on the page.
- **`mbxmoods-duplicates.json`** members gained `album`, `smfm`, and `fakeHires`
  (`bitDepth` already present).
- **`--manifest [path]`** emits a `kind:dupes` review-surface manifest for a companion
  web UI; with no path it auto-locates the running MusicBee instance's review folder and
  co-emits the interactive page beside it.

### Scan performance
- **SHA256Cng** (hardware SHA-NI) for `audioStreamSha256` — faster on CPU-bound rescans.
- **Single-pass** whole-file MD5 + invariant-region SHA in the cache-tier walks (one read
  instead of up to three).
- **Tier-1.5 quick tags-only cache** — after a mass tag edit, an mtime-drifted file is
  reconciled from a ~64 KB head-hash instead of a full audio-hash read; `--no-quick-cache`
  opts out.
- **Default parallelism = cores − 2** (leaves headroom for the foreground); `-p max` uses
  all cores, `-p N` sets exactly N.

### `--fixup`
- A remap target must now **exist on disk** — a stale iTunes XML that still lists a
  deleted file no longer resurrects it via a no-op same-path remap.
- **Hash-resolve**: when a gone file's audio survives at another path (the kept copy of a
  deleted duplicate, matched by `audioStreamSha256`), the redundant entry is dropped as
  *Resolved* instead of kept.

### Fixed
- Review-page HTML now escapes `'` and `"` — paths with an apostrophe no longer break a
  group's include control.
