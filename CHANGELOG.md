# Changelog

All notable changes to Truedat. Format loosely follows [Keep a Changelog](https://keepachangelog.com);
Truedat versions are release-candidate tags (`vX.Y.Z-RCn`) on `main`.

## [Unreleased]

### Added — average track length in the scan summary (2026-07-24)

- The end-of-scan summary now reports **`Avg length`** — the mean audio duration
  of the analyzed tracks, with the measured **real-time factor** — next to the
  existing `Avg/track` analysis time and `Analyzed IO … MB/s`. The three describe
  the same run from different angles, and length is the one that explains a slow
  scan: Essentia cost scales with audio duration, so a 40-minute average track
  costs roughly 20× a 2-minute one at identical MB/s. Sampled from the
  duration-known tracks (the same matched pair the ETA model uses); when that is
  a subset of the analyzed set, the line says so.

### Added — read-only review surfaces (2026-07-22)

- **`--list-speech [path]`** — lists the entries whose verdict is
  `speechLikely=yes`, i.e. exactly the set `--migrate` prunes, so they can be
  reviewed *before* pruning. Writes `mbxmoods-speech.csv` (path, artist, title,
  album, genre, codec, confidence, method) plus a console count and preview.
  Read-only: writes the CSV and nothing else. The `--stats` **Recommended**
  block now routes through this instead of naming `--migrate` directly — the
  bare recommendation cost three real tracks their catalog entries.
- **`--list-missing-smfm [path]`** — lists catalog entries carrying no Sony
  SMFM (12-TONE) block, with coverage (present / total / %), to
  `mbxmoods-smfm-missing.csv`. truedat only *reads* SMFM, so a missing block
  means the file never went through Sony's tagger and no rescan can add it;
  for that reason this is a standalone mode and deliberately absent from the
  **Recommended** block, which only names gaps a truedat command can close.

### Fixed — speech verdict false positives on instrumental music (2026-07-22)

- `speechLikely` now also requires **`danceability < 0.50`** before returning
  `"yes"`. Sparse, live and free-form *instrumental* music craters on every
  other signal in the panel exactly like speech does — no stable tempo peak,
  weak chords, weak key strength, high silence, high zero-crossing rate — so
  those signals alone cannot separate a bebop sax solo from a spoken word.
  Danceability can: genuine speech measures 0.00, while real-music false
  positives measured 0.66–1.10. Without this gate `--migrate` pruned Charlie
  Parker "Hot House", Nine Inch Nails "Burn" (live) and Travis "Outro" from a
  live catalog. Demotes to `"unknown"`, never `"no"`. Method tag →
  `truedat-speech-v1.2-untuned-2026-07-22`. Verdict is computed at write time,
  so this applies catalog-wide on the next save with **no rescan**.

### Fixed — `--migrate` pruned on a stale verdict (2026-07-23)

- `--migrate` read the **persisted** `truedat.speechLikely` when deciding what to
  prune, while `--list-speech` and the `--stats` advisor **recompute** the verdict
  live from stored features. On any catalog last saved by an older build the two
  disagreed: `--list-speech` would correctly clear an instrumental track while
  `--migrate` still pruned it from the frozen `"yes"` — silently defeating the
  review-before-prune workflow on exactly the catalogs that needed it. `--migrate`
  now recomputes from features (single source of truth) and reports
  `Kept N entries whose stored speech verdict was stale`. Verified against a real
  72,123-track catalog: prunes 2, keeps 4 that the previous build would have taken.
- `--list-speech` / `--list-missing-smfm` added to the `--chunk` and `--transcode`
  mutual-exclusion guards, matching every other read-only mode.
- Speech regression tests reworked: the instrumental cases now supply the full
  six-signal panel that reproduces the real incident, so all five danceability
  values genuinely exercise the veto (three of them previously returned
  `"unknown"` before the veto was consulted and would have passed with the gate
  deleted). Added a paired assertion that the *same* panel at danceability 0 still
  reaches `"yes"`, proving the veto is what demotes the others.

### Changed — terminology (2026-07-22)

- Documentation and console output now say `--migrate` **prunes catalog
  entries**, not "deletes". `--migrate` removes entries from `mbxmoods.json`
  and never touches audio files; the old wording was both alarming and
  inaccurate. Genuine file-deletion references (staging cleanup, temp files,
  removing duplicates inside MusicBee) are unchanged, as is the standing
  guarantee that truedat never deletes or modifies audio files.

### Added — scan traceability & self-healing (2026-07-21/22)

- **Skip ledger covers every pre-scan drop.** `mbxmoods-skipped.csv` now records
  podcast-labeled episodes (with what triggered the classification), video files,
  playlist/redirector entries, **remote stream URLs** (un-downloaded podcast-feed
  episodes — previously mangled into fake local paths and reported as missing
  files every run), and missing files (path length called out past MAX_PATH).
  `--audit` lists each dropped track on console.
- **Podcast policy settled:** anything the XML *labels* podcast (`Podcast=true`
  or `Genre=Podcast`) is skipped by default; **`--include-podcasts`** analyzes
  them instead and keeps their entries under `--migrate`. The false-positive
  `Episode Date` heuristic is gone (it classified plain music as podcasts).
  Skipped podcasts with existing catalog entries get their stored genre
  refreshed so `--migrate` can purge them.
- **Over-MAX_PATH files now scan** via a per-track `\\?\` extended-length
  fallback through the staging path (subprocess tools only ever see the short
  staged copy). `--check-filenames` gains a Long paths tier.
- **Honest progress reporting:** ETA prices remaining new tracks by **audio
  duration at the measured analysis real-time-factor** (no more byte-rate
  spikes from small files mispricing long episodes), stays silent until
  something has actually been measured, and the progress line shows the
  analyzed-class average plus live MB/s. End-of-scan summary breaks down count
  and average cost per outcome class; catalog-summary hash rows appear only
  when actionable.
- **FLAC transition rescue in the default scan (tier 2.5):** transition-era
  FLAC entries whose tags were rewritten before migrating are re-keyed in
  place (audio-props gated) instead of falling through to a full re-analysis;
  `--verify --backfill --accept-flac-tag-drift` applies the same rule in a
  verify pass.

### Added — stricter podcast detection + speech cleanup (2026-07-22)

- **File-marker sniff on cache-miss.** All three scan paths (MoodsMode,
  `--file-list`, `--analyze-file`) now open cache-miss files and check for
  embedded podcast markers before spending Essentia time on them — ID3v2
  `PCST`/`WFED`/`TGID`/`TCON` containing "podcast", or MP4 `pcst`/`purl` atoms.
  Ledger reason: `podcast (file marker: PCST)`. MoodsMode's end-of-scan summary
  reports these separately: `Podcast: N (file markers — see mbxmoods-skipped.csv)`.
  Bypassed by `--include-podcasts`.
- **2-of-3 XML signal vote.** At parse time, a track trips podcast
  classification when at least 2 of {Episode Date key present, Publisher key
  present, duration ≥30 min} hold — catching feeds that don't set the explicit
  `Podcast=true` / `Genre=Podcast` labels. Ledger reason: `podcast (signals:
  Episode Date + 52min)`. Explicit labels remain single-signal sufficient.
  `--include-podcasts` bypasses.
- **`speechLikely` verdict.** New field in the `truedat` verdict block
  (`speechLikely` "yes"/"no"/"unknown"/"n/a", `speechConfidence`,
  `speechMethod` = `truedat-speech-v1-untuned-2026-07-22`), computed at write
  time from stored features (danceability, chordsStrength, silenceRate30dB,
  zeroCrossingRate, bpmFirstPeakWeight, keyVotes strength) — no rescan needed,
  the whole catalog picks it up on next save. `"yes"` additionally requires
  the zero-crossing signal to fire, so tones/ambient beds demote to
  `"unknown"` instead of a false positive. Per-signal trace under `--audit`.
- **`--migrate` purges `speechLikely == "yes"` entries** (kept with
  `--include-podcasts`, whole-file backup as always, throw-safe on malformed
  JSON nodes via `SafeStr`).
- **Recommended-commands advisor.** `--stats` (and the end-of-scan catalog
  summary) now ends with a `Recommended:` block mapping detected catalog state
  — entries lacking the tonal/rhythm wave, missing `fingerprint.v1`, stray
  `fileMd5` values, speech-likely entries — to the exact command that fixes
  it. A matching one-line advisory prints at scan startup when wave-missing
  entries exist. Advisory only: truedat never prompts interactively.

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

Fill an existing catalog with **`--refresh-features`**: entries missing the new
fields re-analyze during a normal scan, everything else stays cached; resumable
(saves every 25 tracks), idempotent — run in sessions until coverage completes.

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
