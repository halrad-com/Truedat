# Changelog

All notable changes to Truedat. Format loosely follows [Keep a Changelog](https://keepachangelog.com);
Truedat ships as stage tags on `main` — `DV` (design validation), `EV` (engineering
validation), `PV` (public validation), `RC` (release candidate). There is no final
release state: a version is a snapshot along the arc, never promoted to a bare
`vX.Y.Z`.



## [0.5.5.3-EV4.2.1] — 2026-10-27

### Added

- New measurements for performance optimization.

## [0.5.5.3-EV4.2] — 2026-10-26

### Changed

- The store and edges are opt in.

## [0.5.5.3-EV4.1] — 2026-10-25

### Added

- Multipart catalog support.

## [0.5.5.3-EV4] — 2026-10-25

### Changed

- Extractor `.7` build: optimization performance improvements.

### Added

- A backup is created before a catalog is imported into the store.

## [0.5.5.3-EV3.3] — 2026-10-24

### Fixed

- A library spread across several drives, or one whose root got registered twice, publishes the whole catalog.
  Stores that already have the problem fix themselves the next time they open, after making a backup.
- A store copied to another machine publishes the whole library there too.
- Changing a file's tags no longer brings back the old file hash on the next scan.
- Merging catalogs from two machines keeps every value either side had.
- Stopping a scan (Ctrl+C twice, closing the window, logging off, shutting down) saves what finished before it exits.
- A drive that's offline during a scan is not treated as deleted files.
- `--fixup --remap` and `--restore` refuse with a sentence instead of crashing, and `--restore` keeps a copy of what
  it replaced.
- Two authenticity measurement errors (clipped tracks, failed decodes) corrected.

### Added

- `--refresh-authenticity` re-measures tracks analysed with an older authenticity method.

## [0.5.5.3-EV2.2.3.2] — 2026-10-20

### Changed

- Updated output summary - shows the version and inputs used.

### Fixed

- `spectralFlatness` is Essentia's Bark-band flatness, not truedat's average of three bands.
  Any scan corrects an existing catalog without re-analysis of the files.

## [0.5.5.3-EV2.2.3.1] — 2026-10-19

### Fixed

- When a library had more than one root (files on two drives, or a scan that recorded a sub-folder as a root
  first), `mbxmoods.json` was not written - fixed.

- **The scan summary says what it wrote.** When the catalog could not be written from the store it prints
  `NOT PUBLISHED` with the reason.

- The catalog line says when the file on disk is behind the store.

- **Local copies are not reported as network staging.** Files with non-ASCII names copied locally for the tools
  no longer show a "pulled over the source link" rate.

## [0.5.5.3-EV2.2.3] — 2026-10-18

### Changed

- **The store and edges are on by default.**  A library that already has a catalog is seeded from it
  first (no audio read, seconds) and nothing is re-analysed: tracks already in the catalog get edges when you run
  `truedat --refresh`. Using `--store` on an existing library now seeds like you did a `--store-import`.
  `--no-store` / `--no-edges` turn it off for one run.

- **Hi-res files scan 2-5x faster.** A file above 44.1 kHz is converted to 44.1 kHz once before analysis instead
  of being resampled on every pass inside the extractor: measured 2x at 48 kHz, 3x at 96 kHz and over 4x at
  192 kHz, the conversion counted in. The higher the rate the more there is to recover, and a converted file
  then analyses at the same speed as an ordinary CD-rate one. A library gains in proportion to its hi-res share
  — one at 2.8% came in 3.6% faster over a 14-hour scan, a mostly hi-res library far more. On 22 hi-res files
  no key, scale, tempo or speech verdict changed. The authenticity checks and the identity hash still read the
  original file. Needs ffmpeg; without it nothing changes.

## [0.5.5.3-EV2.2.1] — 2026-10-18

### Changed

- **The end-of-scan report names every file the run wrote, and prints the list once.** `mbxedges.v1.json` was
  never named by any summary, the catalog and sidecar sizes were printed twice by two different emitters, and
  nine files — 9.6 MB of 89.1 on a 7,651-track library — went unlisted. One block now: catalog, sidecar, edges,
  store, reports, backups, and a total.
- **Report lines fit an 80-column window.** A wrapped line destroyed the table's alignment; long notes now sit
  on their own indented lines and count columns are sized to the catalog.
- **The store line carries both counts.** `7,534 descriptions for 7,651 catalog tracks (117 duplicate paths)` —
  the store describes audio, the catalog holds paths, and the gap used to read as missing edge rows.
- **The Sony SMFM row is omitted when nothing is captured and the reader is off** (its default). No command
  fills that gap, so a 0% line reported a feature that is not running as missing data. A catalog that holds
  SMFM blocks still gets the row.
- **`Elapsed` covers the whole run.** The clock stopped before the catalog, store, sidecar and companion were
  written, so a 4.4 s write was reported as `Elapsed 0.3s` with a finish time from before those files existed.
  It now reads `4.7s (scan 0.3s, write 4.4s)`, and the separate `Last save` line is gone.
- **`Output:` lists what the run wrote**, not just the catalog.

### Fixed

- **Scan spill files no longer land beside the library.** Each fold is spilled to disk and deleted by the flush
  that commits it — transient working data that was creating an `artifacts\` folder next to `mbxmoods.json`.
  It now goes under the stage directory, so `--stage-dir` moves every scratch file truedat writes. Existing
  `artifacts\pending` leftovers are swept.
- **A successful scan no longer prints `Error: no catalog beside which to write mbxedges.v1.json`** while
  publishing that exact file.

## [0.5.5.3-EV2.2] — 2026-10-18

### Fixed

- **`--store-roundtrip` no longer reports the store as missing values it holds.** A catalog written by an older build spells
  some numbers one step off their decimal; the store keeps each field at its declared precision and writes the decimal back.
  Those entries now count as equal, reported on their own line, and the gate uses the same comparison as the scan.
- **The `--store-import` summary names its two conflict kinds.** `sha collisions` (same hash, different audio) and value-drift
  conflicts were both called conflicts, so `conflicts: 0` beside `4 drifted` read as a contradiction.

## [0.5.5.3-EV2.1] — 2026-10-17

### Fixed

- **A store that already exists is used again.** Nothing creates one unasked, but once a store is at the
  resolved location a plain `truedat` opens it and turns edges on, and says so on the `Store:` line. The
  documented upgrade (`--store-import`, then a refresh) previously left a populated store that the next
  ordinary scan ignored, so the catalog and the store diverged with nothing on screen saying so. No config
  step is needed any more; `--no-store` / `--no-edges` still override for one run.
- **The stall watchdog no longer kills healthy work.** It counted only CPU time, and an extractor blocked on
  IO accrues none while working normally — so the full-file md5/loudness pass read as a hang under a busy
  scan. Read bytes now count as progress too and the idle window is 180 s. Measured: an 849 MB FLAC killed
  at 85.7 s completed in 701 s untouched. If large files have been landing in `mbxmoods-errors.csv` as
  stalled, re-run with `--retry-errors`.
- **The decode-coverage gate stops inventing a length.** Raw ADTS (`.aac`) records no duration, so every
  reported figure is file size over an assumed bitrate — one 159.8 s file was reported as 2023.3 s by
  TagLib, 180.9 s by MusicBee, 174.2 s by ffprobe and 163 s by VLC. Four `.aac` files decoded in full and
  were failed anyway as `decode-7%`, losing their catalog entries. A format that records no length now gets
  no coverage verdict, and where two claims exist the smaller one is the reference.
- **A failed track gets a second attempt before it is recorded as failed.** A recorded failure is skipped by
  every later scan, so one unlucky attempt kept a healthy track out of the catalog until `--retry-errors`. The
  library scan and `--file-list` / `--folder` now re-analyze a failing track once in the same scan; only a
  second failure is written to `mbxmoods-errors.csv`, and the scan summary counts the tracks that made it
  in on the retry.

## [0.5.5.3-DV2] — 2026-10-16

### Changed

- **With a store in force, the store is what changes — the files are written from it.** A scan loads from
  the store, records every catalog change into it, and publishes `mbxmoods.json`, the sidecar and
  `mbxedges.v1.json` from it together, at one generation. All nine catalog-changing verbs (`--fixup`,
  `--remap`, `--verify --backfill`, `--migrate`, `--strip-smfm`, `--prune-entry`, `--prune-excluded`,
  `--merge-moods`, `--restore`) go the same way: the store's state is published, the verb runs unchanged,
  its result is written into the store, and the files are republished. A store and a catalog that disagree
  refuse the run and say which command reconciles them, rather than quietly picking one.
- **`--snapshot` carries the store; `--restore` restores it.** With a store in force the snapshot bundles a
  consistent `VACUUM INTO` copy of the store (never a file copy — a live store's committed state spans
  three files) plus its schema, generation, location count and SHA-256. Restoring such an archive restores
  the store — refusing first if it will not open, fails SQLite's integrity check, or is from a newer schema —
  snapshots the store it replaces, and republishes the three files from the restored store at a new
  generation. An archive with no store still refuses, and names the archive that would have worked.
- **`--merge-moods` merges stores as well as catalogs.** A `--merge-source` may be a `--chunk` shard's
  `truedat.db`; its entries are read across every root and merged exactly as a catalog's are.
- **The catalog names where it came from.** Every published catalog carries a `contract` + `origin` header
  (machine, store schema, truedat version, generation, the sidecar's SHA-256, the root published). The hub
  binds the sidecar by that hash instead of guessing from timestamps; older catalogs without the header
  still read.
- **Every store verb leaves `store-result.json`** beside the store: schema, committed generation, what was
  published (by hash), and an honest outcome — `completed`, `failed`, or `interrupted` when a scan died
  without reaching its boundary.

### Added

- **Typed store columns (schema v3) and a typed edge table (v4).** Each numeric field is held at the
  precision it is written with — scaled integers, int32 vectors, a sparse histogram — so nothing is widened
  or rounded on the way in. The edge row is scalars + HPCP by default; the arrays come only with
  `--edge-arrays`, stay with a track once captured (sticky), and are removed only by `--edge-arrays-drop
  [--dry-run]`. A v2 or v3 store migrates on open, backup first, and refuses a store from a newer build.
- **`--store-info` reports state, not just measurements**: the store's generation, the generation the
  published catalog carries, whether the files are behind it and why, how many descriptions hold edge
  arrays, and the one command that closes the gap. Same fields under `state` in `store-info.json`.
- **The catalog's `truedat.*` verdict block is projected into the store**, one row per copy, re-derived when
  the verdict or speech algorithm tag changes — so the authenticity and speech readings can be queried
  without re-exporting the catalog.

## [0.5.5.3-DV1] — 2026-10-12

### Added

- **SMFM off by default.** Sony 12-TONE tags are read only with `--smfm` (new), config `smfm`, or
  `--refresh-smfm`; `--no-smfm` still overrides. SMFM blocks already in a catalog are kept. Newly analysed
  tracks carry no `smfm*` fields unless SMFM is turned on.

- **`--stats` no longer prints the store's `store open` line inside its summary** (`--audit` still shows it).

- **Console lines name their file.** `FAILED:` lines in the library scan carry artist, title and file name;
  the ffmpeg transcode and downmix announcements and the under-decode retry name the source file. In a
  parallel scan the track's own line can be far above the message.

- **`--stats` shows the store.** With a store in force: a `Store:` line (size, descriptions, bytes per
  track, edge coverage) beside Catalog/Sidecar/Backups, and a Recommended entry pointing at `--refresh`
  while edge rows are missing. Detail stays in `--store-info`.

- **`--refresh` fills in missing edges in the same scan.** With the store and edges on, a track the store
  lists as lacking edge rows counts as stale, so the scan's own parallel workers, progress line, ETA and
  saves handle it like any other re-analysis. The store now saves at the scan's five-minute checkpoint
  as well as at the end, so closing the window mid-run keeps what was done. A track whose edges failed
  is not retried until `--retry-errors`. `--budget 90s|30m|2h|1h30m` stops the library scan cleanly (save
  and exit) once that long has passed. The old one-track-at-a-time `--work` drain is gone. The
  import's "legacy FLAC sha: N unrecoverable" line now says what it counts (FLAC entries with only the
  current hash; normal).

- **Store and edges off by default.** They run only with `--store` / `--edges` or `truedat --config store
  true` / `edges true`; `--no-store` / `--no-edges` override for one run. A scan no longer keeps the
  extractor's compressed output beside the store (`artifacts\`, ~37 KB per track, read by nothing).

- **`STORE.md`** — the store, its tables, verbs, the edges layer, and how to open the file (DB Browser for
  SQLite, `sqlite3 -readonly`). The store verbs are in `--help` too. The README's store section carries the
  measured numbers (200-track slice: 2.8 s/track with edges, 6.4 KB/track of edge rows, 19.7 KB/track
  all-in, 1.7 KB/track companion) and what is and is not verified yet.

- **Store precision: six significant figures.** Every float enters the store and leaves it on
  `--store-export` at 6 sf, written exactly (`0.022454`, never `0.022454000000000002`); the round-trip
  gate compares both sides at that precision. The scan's catalog writer and its bytes are unchanged.

- **Edge folds are spilled to disk during a scan** instead of held in memory until the store write.

- **Extractor build `output-x64.5` recognised as current.** `.4` is still accepted but the startup
  report now says what `.5` adds (a binary frames file behind `--frames-bin`, the `framesInclude`
  profile key, `metadata.version.extractor_build` in the main output) and that `--edges` will need
  it. The main output is otherwise unchanged against `.4`: verified on five real files across
  MP3/M4A/WMA/FLAC, every difference within the extractor's own run-to-run noise.

- **`--edges` (preview, v2 S2).** With `--store`, every track a scan analyses also gets an *edge row*
  in the store: the first and last 30 s folded from a binary frames file the `.5` extractor writes
  (EBU loudness series at 10/s, beats and tempo, a key vote per edge and per 10 s sub-window, MFCC
  and spectral texture, fade and silence) plus RMS crest and transition from a short ffmpeg PCM pass
  (null without ffmpeg). One packed row per edge, about 6.3 KB per track, with an `edges` run row
  carrying the fold and PCM time; a failed fold is a failed run and no rows. Refuses plainly without
  `--store`, on an extractor older than `.5`, and in the per-file modes without `--moods`. Writes
  nothing to `mbxmoods.json`; without the flag a scan runs exactly as before. `--store-info` reports
  edge coverage, bytes per track and median fold time.

- **`mbxedges.v1.json`.** The hub's edge companion file beside `mbxmoods.json`: per track (keyed by
  `audioStreamSha256`) the two edges' status, window, key, bpm, loudness, texture, divergence and `rms`,
  plus the head-vs-tail divergence; compact, about 1.8 KB per track; written after any `--edges` scan that
  added rows, and by `--edges-export [path]` on demand. Media without a sha are omitted.

- **A SQLite store (preview, v2 S1).** `--store-import` loads a catalog and the ledgers beside it
  into a store keyed by identity — a UUID per file entity, content-hash facets that name their
  algorithm, one location per copy on a machine, a run row per attempt — idempotently, in
  batches, with a mirror catalog resolving to the same entities. `--store-export` writes a
  catalog and `.mbxs` back out through the unchanged writer, plus `key-moves.json` whenever a
  content hash was superseded. `--store-roundtrip` proves per-entry byte equality of store
  against catalog. `--store-info` reports bytes per track, analysis speed, coverage by wave and
  shared content.
  `--stats --store` and `--duplicates --store` answer from the store. Uses Windows' built-in
  SQLite (`winsqlite3.dll`, 3.40+, checked at open with a plain refusal when missing); nothing is
  installed or shipped. `mbxmoods.json` stays what MBXHub reads; every existing mode is unchanged;
  the store path is always explicit.

- **Schema v2: one audio description per media.** `track_feature` (one wide row per location) is split
  into `track_content` (every Essentia descriptor, bitUsage, HF, the content hash — once per media) and
  `track_copy` (tags, `fingerprint.v1`, `fileMd5`, SMFM, `analysisDuration` — once per location). A v1
  store migrates on first open; the first-imported copy's description wins, differences within the
  extractor's run-to-run noise are counted and differences beyond it become queryable `conflict` rows
  (`field`, `value_a`, `value_b`). `--store-roundtrip` reports
  merged copies apart from unexplained differences and stays EQUAL for every single-copy entry.
  `value` gains `segment_id`; `conflict` gains `field`, `run_a`/`run_b`, `value_a`/`value_b`.

- **A differing value is a conflict, never an overwrite.** Re-importing a catalog compares each entry with
  the stored description: equal (within the extractor's run-to-run noise) writes nothing, so a repeat import
  leaves every row's `updated_utc` alone; a difference beyond noise under the same producer becomes a
  `value-drift` conflict (field, both values, both runs) and the description is kept; a different producer
  replaces it with the old run kept as history and a resolved `content-replaced` conflict naming both
  producers — but an OLDER extractor build never replaces a newer one's description (that is drift); a key
  move re-keys it; a stored null gaining a value is filled, never a conflict. Copy facts (tags,
  `fingerprint.v1`, SMFM) take the newer value, exactly. Identical drifts are recorded once. `--store-info`
  lists open conflicts by kind. On the real catalog a re-import
  is 22 s, 72,445 unchanged, 32 drifted, the store unchanged in size.

- **The extractor's aggregate output is kept.** With `--store`, the main JSON the extractor writes per
  track (every descriptor's statistics, covariances, full HPCP, histograms — deleted at parse until now) is
  compressed and filed once per content hash and extractor build as `artifacts\<build>\<2>\<hash>.main.json.z` beside the store, with
  an `artifact` row (kind `main`, format = the extractor build, bytes, sha256, run). Any future field that
  is a fold of that output is a re-fold of the file, not a rescan. `--store-info` reports count and bytes.

- **The scan writes the store.** With `--store`, every scan mode writes the entries it analysed this run
  into the store after the catalog is saved, through the import's new in-memory entry (no re-read of the
  catalog): producer `essentia@<build>`, one run row per entry with its analysis time, a failed run per
  failure with its components, the same drift and no-churn rules as an import. Roots come from the
  store's existing roots, else from the whole catalog, never from the analysed subset. A store problem
  is reported and never costs a catalog entry; `store-result.json` beside the store records each write.

- **The store has a home.** `truedat.db` beside `mbxmoods.json`, found by the same resolver as the
  catalog, so the store verbs need no `--store`; bare `--store` on `--stats` / `--duplicates` means that
  default. The file is always local: a catalog on a network share gets a local per-library store under
  `%LOCALAPPDATA%\truedat\`, `--store-dir` (settable) relocates it, and an explicit network store opens
  with exclusive locking plus a warning. Closing a store leaves no `-wal` behind, so the file can travel
  by the same sync as the catalog. `--paths` lists the store and why it resolved there.

- **16 KB store pages and `--store-vacuum`.** A feature row is ~2 KB, so at SQLite's 4 KB default
  each row took a page of its own and the store was 1.34× its JSON. New stores are created at
  16 KB pages (0.95× the JSON on the same catalog); `--store-vacuum [--page-size N]` repacks an
  existing store in seconds and reports before/after. Contents are unchanged — `--store-roundtrip`
  stays EQUAL.

## [0.5.5.2-EV1] — 2026-10-03

### Fixed

- **`fingerprint.v1.pathTail` recorded the staging copy, not the track.** Any scan of a
  library over a UNC path, a mapped network drive, or a non-ASCII local path wrote the temp
  copy's name (`temp\.truedat-stage\<guid>.flac`) as the track's path witness. `--migrate`
  re-derives the tail from each entry's own key: catalog-only, no library XML, no file access.
  `--fixup` repairs
  it too, after reconciliation.
- **A locked fingerprint read gets retried instead of losing the track.** The sha and tags
  reads retry a sharing violation; the fingerprint read, opened the same way in the same
  fan-out, did not — so the same lock still cost the track its catalog entry. Same ladder,
  same rules, and the end-of-scan report counts recovered and still-locked fingerprint reads
  separately from the other two.
- **`--verify --backfill` honours the review ledger.** A record stopped the attempt in every
  scan mode but not in backfill, which is the one path that can fill a legacy entry's missing
  `fingerprint.v1` — so a file TagLib refuses was re-read in full on every pass, forever. The
  ledger is now consulted before any read, and a failure inside backfill is recorded so the
  next pass skips it with a reason. Read-only `--verify` still checks every entry: a record
  governs work, not the integrity question. Skipped entries report `REVIEW_SKIPPED`, never
  `OK`.
- **A locked tags read gets retried instead of losing the track.** The sha read already
  retried a sharing violation; the tags read did not, and an empty tags result fails the
  health gate and writes no catalog entry — reported as `analysis incomplete: tags`. Up to
  four retries, 150 ms apart, on a lock only; a parse that reports no duration, a missing
  file and an access-denied file are not retried. A give-up now says it stayed locked and
  for how many retries, and the end-of-scan report counts recovered and still-locked tags
  reads separately from sha's.

## [0.5.5.0-EV2] — 2026-08-29

### Added

- **A locked file gets retried instead of losing its catalog entry.** The worker fan-out
  reads one file from several readers at once, and a momentary sharing conflict returned
  null — which failed the health gate and wrote no entry, reported as `analysis incomplete:
  sha`. Up to four retries, 150 ms apart, on a sharing violation only. The end-of-scan
  report names files recovered this way and files still locked after the retries, so
  contention is visible instead of looking like corrupt files.

### Fixed

- **A 44.1 kHz file is no longer certified as genuine hi-res.** `hiresGenuine` now requires
  `sampleRate > 44100` as well as 24-bit lossless — at CD rate the high-frequency checks
  cannot run, so the verdict was answering `"yes"` from bit-depth evidence alone. It reads
  `"n/a"` there now; `prov: pad16` still reports at any rate. Verdicts recompute on every
  save, so catalogs correct themselves on the next scan — no rescan, no backfill. Method
  tag: `truedat-v1-fft-rategate-2026-08-28`.
- **The duplicate report's `fakeHires` flag applies the same rule.** A 24/44.1 copy is no
  longer labelled `upsampled`; a 16/96 upsample is still caught. Affects
  `mbxmoods-duplicates.json`, the review manifest, the interactive page and keeper ranking.
- **A padded copy no longer wins the duplicate it faked.** The keeper ranked on claimed bit
  depth, so a 24-bit container holding 16-bit content outranked the genuine 16-bit rip
  beside it. Bit-usage evidence now demotes it first; copies with no bit-usage measured are
  never demoted. A new `paddedDepth` field carries the reason into the same three outputs,
  so the demotion is visible — the non-keepers are what `--losers-m3u` hands you to act on.
- **The contention line no longer counts give-ups as rescues.** It summed retry attempts
  into one counter and incremented on both paths, so files that stayed locked and lost
  their catalog entry were reported as `recovered by retry`. Recoveries and give-ups are
  now counted per file, on separate lines.

## [0.5.5.0-EV1.3] — 2026-08-24

### Added

- **`mbxmoods-review.json`** — one file listing everything the catalog does not hold and
  why. Each record carries the verbatim tool message, when it was first seen, and a state:
  `review` (needs you), `ignore` (you decided), `auto` (truedat decided, and it says on what
  evidence). A record stops the file being attempted in every mode. `--list-review` names
  them. It supersedes the errors and skipped CSVs, which still ship this release.
- **`--audit-verdicts`** — the per-signal verdict trace, now opt-in. It was recomputed for
  every entry on every save and buried the rest of `--audit` under ~137k lines.
- **`playlist` exclusion rule kind.** Membership is re-read each run, so editing the
  playlist in MusicBee changes the next scan.
- **Per-root latency profile** at the `--fixup` root check, and throughput per phase in the
  run report — so a bandwidth-bound phase and a latency-bound one stop looking identical.

### Changed

- **`--fixup` checks files on disk in parallel**, sized from the measured latency of each
  root. Measured on a 72k library over a share: 13 minutes to under a minute; local disk
  stays serial, where concurrency would only add contention.
- **`--stats` reports instead of prescribing.** It no longer recommends a command that
  cannot clear the count it just printed.
- **Excluded entries name the rule that caught them**, so an exclusion can be traced back
  and undone.

## [0.5.5.0-EV1.2] — 2026-08-23

Covers EV1.1 as well, which shipped without a section of its own.

### Fixed

- **Better support for multiple libraries.** A bare `truedat` now finds the active library
  whatever its folder is called. A positional path still overrides.

### Changed

- **Catalog writes stream.** The DOM-mutating verbs (`--fixup`, `--remap`, `--migrate`,
  `--merge-moods`, `--strip-smfm`, `--prune-*`) built the whole catalog as one string
  before writing it, which on a large library is a multi-gigabyte allocation and the same
  2 GB ceiling EV1 fixed on the read side. Measured on a 265 MB catalog: 81% of the time,
  54% of the allocation, output byte-identical.
- **`--fixup` checks the filesystem once per entry instead of twice.** Local disks will not
  notice; a large library over SMB halves its metadata round-trips.

### Documentation

- The output-format reference now covers three schema waves it had never mentioned, states
  that `fingerprint.v1` is one flat key rather than a nested object, and drops the
  hand-kept feature total in favour of measuring the file.
- The authenticity section says what each check catches and what it is blind to, including
  the cases nothing can answer — a file that has been through a streaming platform comes
  back genuinely re-encoded, and its history is not recoverable from the audio.

## [0.5.5.0-EV1] — 2026-08-21

Opens the 0.5.5.0 line. No source changes since `v0.5.4.9-RC7` — same code, new version
number, first public stage tag of the extended beta. See RC7 for what it does.

## [0.5.4.9-RC7] — 2026-08-21

### Added

- **Configuration support.** `truedat.config.json` beside the exe holds default values for
  the tuning flags. Keys are the flag names — `"no-smfm": true` means always skip SMFM.
  Precedence: explicit flag > config > built-in default.
  
  | Key                | Type          | Effect                                               |
  | ------------------ | ------------- | ---------------------------------------------------- |
  | `no-stage`         | bool          | read sources directly, never stage a local copy      |
  | `no-quick-cache`   | bool          | disable the head-64k quick cache tier                |
  | `no-bitusage`      | bool          | skip the bitUsage signal                             |
  | `no-hf-analysis`   | bool          | skip the HF signals                                  |
  | `no-smfm`          | bool          | never read Sony SMFM tags                            |
  | `file-md5`         | bool          | compute and store the whole-file MD5                 |
  | `enableshortfiles` | bool          | analyze files under the short-file threshold         |
  | `allow-sleep`      | bool          | do not hold the machine awake during a scan          |
  | `refresh-features` | bool          | re-analyze entries missing a later feature wave      |
  | `refresh-smfm`     | bool          | re-read SMFM tags on cache hits                      |
  | `audit`            | bool          | verbose per-track diagnostics to stderr              |
  | `fixup-after-scan` | bool          | reconcile the catalog against disk after a full scan |
  | `parallel`         | int 1–1024    | worker count                                         |
  | `cpu-limit`        | int 1–100     | percent CPU cap for subprocesses                     |
  | `keep-backups`     | int 0–10000   | catalog backups to retain (0 = keep all)             |
  | `long-track-mins`  | int 1–10000   | duration that tags a track as long                   |
  | `max-duration`     | int 0–1000000 | skip tracks longer than this (seconds)               |
  | `stage-dir`        | text          | where staged copies are written                      |
  
  ```
  truedat --config                 show what is in force
  truedat --config <key> <value>   set it
  truedat --config reset           delete the file
  truedat --no-config              ignore it for this run
  ```
  
  Verbs and target paths are not settable. An unparseable file or unknown key stops the run.

- **`--fixup-after-scan`** — runs `--fixup` when a scan finishes, so the catalog drops entries
  for files that are gone. Refused under `--chunk` and when there is no library XML.

### Fixed

- A staged copy locked by antivirus, backup or sync software no longer costs the track its
  catalog entry. Retries on sharing/lock violations, then reads the source directly; the error
  names the process holding the file, and recovered contention is counted in the staging
  summary.
- Silent tracks above 44.1 kHz no longer fail the scan-health gate — the HF signal now reports
  *not applicable* on silence, as bitUsage already did.
- Catalogs between ~1.07 GB and 1.9 GB can be opened again by `--fixup`, `--remap`,
  `--migrate`, `--merge-moods`, `--prune-*` and `--strip-smfm`. Loaders parse from the stream
  instead of `File.ReadAllText`, whose UTF-16 string hit the 2 GB object limit at half the
  documented file size.

## [0.5.4.9-RC6] — 2026-08-19

### Added

- **`--prune-entry <path>` (repeatable)** — removes one named catalog entry: the targeted
  counterpart to `--prune-excluded`, which acts on rules. Matches on the catalog's own path
  comparer and echoes the catalog's spelling of what it matched; a path matching nothing is
  listed by name and the run exits non-zero. Rotated `.zip` backup, atomic swap, sidecar
  regenerated, `--dry-run` writes nothing. Removes the **entry, not the file** — the next scan
  analyzes it again unless you also write an exclusion rule. Never consults the filesystem, so
  it works on a metadata mirror and on entries whose volume is offline.

- **`--strip-smfm [path]`** — removes Sony 12-TONE data from a catalog. Nothing could before:
  `--refresh-smfm` backfills but structurally cannot clear, so a file stripped by `smfm-tools`
  took a cache hit and the catalog kept the stale scores. Strips both key generations (`smfm*`
  and the legacy `sensme*`), keeps every track and every other field, backs up, swaps atomically
  and regenerates the `.mbxs` sidecar — the hub boots sidecar-first, so a JSON-only edit changes
  nothing. Reports how many entries carry scored data before writing, as a count. `--dry-run`
  supported. Catalog-only, so it runs on a metadata mirror.

- **`--no-smfm`** — never read SMFM tags, which is what makes a strip durable: SMFM is read from
  the file's tag and never computed, so ordinary analysis refills it otherwise. Sits beside
  `--no-bitusage` / `--no-hf-analysis`; refuses to run alongside `--refresh-smfm`.

## [0.5.4.9-RC5] — 2026-08-19

Covers everything since RC3 — `v0.5.4.9-RC4` shipped without its own section, so the
`--list-formats` work tagged there is recorded here.

### Added

- **`--list-formats [path]`** — what the library is made of, and where. Buckets catalog entries
  by `fingerprint.v1.codec` with count, share and lossy/lossless character, then rolls each
  format up by folder so a format can be located rather than just counted. Full listing to
  `mbxmoods-formats.csv` (path, codec, codecRaw, bitrate, sampleRate, bitDepth, fileSize,
  artist, album, title). Read-only and catalog-only — runs on a metadata mirror.
  Two buckets stay unresolved: `wma` covers WMA and WMA Lossless, `m4a` covers AAC and ALAC,
  and the catalog does not carry the distinction — `--convert-dir <folder> --wma-lossless-only`
  settles it from the bytes. Entries predating `fingerprint.v1` get a `(not recorded)` row with
  the backfill command rather than a codec guessed from the file extension.

- **`--list-smfm [path]`** — lists every catalog entry's Sony 12-TONE state to
  `mbxmoods-smfm.csv` (path, state, artist, title, album, codec, smfmBpm, smfmChannel, topScore,
  scores). Three states, not two: `data`, `no-data` (block present, every score zero) and
  `no-smfm` (no block). Re-running Sony's tagger is the fix for `no-smfm` and is exactly what
  already produced `no-data`, so collapsing them hands out a work list that is partly wrong.
  Every entry gets a row, so the three counts sum to the catalog. Read-only, catalog-only.

- **`smfm-tools/check_moods_smfm.py`** reports the same three states, writing `smfm-data.csv` /
  `smfm-no-data.csv` / `smfm-no-smfm.csv` (replacing `smfm-missing.txt`; path is still column 1).
  Sibling tooling, not part of `truedat.exe`.

- **`smfm-tools/smfm_strip_copy.py`** — writes a copy of a FLAC or MP3 without its SMFM block.
  Audio frames are copied byte-for-byte, so the copy keeps the source's `audioStreamSha256` and
  truedat cross-SHA re-keys it instead of re-analyzing — usable as a control copy. The source is
  opened read-only and never modified. WMA and M4A are refused by name.

## [0.5.4.9-RC3] — 2026-08-16

### Changed

- The scan progress line tags **long** tracks, not big ones. The tag is now the track's duration
  and appears at or past `--long-track-mins` (default 30) — one threshold shared with
  `--preview`. File size predicted nothing: Essentia's cost scales with duration, so a
  three-hour 64 kbps talk file went untagged while a six-minute 24/192 FLAC was tagged and
  finished fast.

## [0.5.4.9-RC2] — 2026-08-15

### Added

- **`--paths`** — prints every path this run would use: catalog, sidecar, exclusion file,
  exclude playlist, library XML, log, the host-suffixed error/skipped ledgers, verify and
  duplicates outputs, review directory and staging directory — each marked present or absent,
  plus which step resolved the catalog. Creates nothing, not even the review directory
  `--preview` makes. `--paths --json` emits the same data for MBXHub's status page.

- **`--verify --chunk M/N`** — verify in restartable slices. A whole-catalog verify re-reads
  every file's audio; on a large library over WiFi that is a many-hour one-shot pass.
  Each chunk now verifies its own hash-mod slice and writes
  `mbxmoods-verify.<host>.<M>of<N>.csv`, so a finished shard is durably done. Under `--backfill`
  shards must run one at a time — each save rewrites the whole catalog from its own copy, and
  truedat says so rather than letting a parallel run silently drop work.

- **Verify coverage receipts + `--verify-coverage`** — each run drops a receipt naming the slice
  it walked, folded into an order-independent XOR digest; `--verify-coverage` combines them and
  proves the pass covered the catalog, every shard exactly once, against one catalog state. A
  skipped shard leaves a residue, a doubled shard cancels itself. It attests to the process, not
  to the audio — that is what the CSV says.

### Fixed

- **`spectralFlatness` phantom zeros are repaired, not just prevented.** The 2026-08-11 fix
  corrected the write path but could not reach existing entries, and that was nearly all of
  them: 72,041 of 72,129 entries carried a phantom `0`, at least 71,971 of them gate-current, so
  no scan and no `--refresh-features` would ever re-derive them. `--fixup` now repairs in place
  (`Reflattened:` in the results block) and the cache-reuse copy heals a phantom zero as it
  copies, so an ordinary scan fixes it too. Pure arithmetic over the stored Bark/ERB/Mel bands —
  no audio, no Essentia, no re-analysis. Only a stored `0` is touched. `spectralDecrease` is
  **not** repairable this way: what is stored is the already-annihilated `0.000000` and the raw
  extractor output is deleted at parse, so those entries need a genuine re-analysis.

- **Verify refuses when the library is unreachable** instead of completing with every entry
  MISSING. It probes each library root before walking and re-probes on the first MISSING, so a
  volume that drops mid-pass stops the run rather than mislabelling the remainder. Nothing is
  written on either path — no CSV, no receipt, no backfill. Same guard `--fixup` already had.

- **Catalog modes that WRITE no longer act on whatever `mbxmoods.json` is in the current
  directory.** `--compact`, `--prettify`, `--restore` and `--prune-excluded` now refuse and name
  the path they would have written when nothing anchors the target. Run from a source checkout,
  they found the repo's own test fixture and rewrote it. Read-only modes keep the convenience.

- **Catalog modes find the library the same way a bare scan does.** They went straight to the
  current directory, so on the layout the README recommends a bare scan worked while a bare
  `--stats` reported the catalog missing — and since the exclusion file resolves from the
  catalog's directory, the operator's rules went missing with it. Both resolvers now share one
  probe order: exe-dir's parent, exe-dir, then the current directory. Reported from the field.

- **truedat run from MusicBee's hub data folder now finds the library.** Discovery walks a
  bounded number of parent directories and checks each one's `Library\` subfolder, nearest-first,
  so `<root>\AppData\MBXHub\truedat.exe` reaches `<root>\Library`.

- **The catalog is found anywhere under a MusicBee instance**, including the instance's
  `AppData\` subfolders, with `%APPDATA%\MusicBee` checked last so a tool inside a portable
  instance resolves to that instance. A fixed set of known locations, not a recursive scan.

### Changed

- **Essentia's per-track output JSON now follows `--stage-dir`** instead of being pinned to
  `%TEMP%`, and comes under the startup orphan sweep with it. Falls back to `%TEMP%` when the
  staging path is not ASCII-printable. The per-drive `.truedat-tmp` hardlinks stay put — a
  hardlink cannot cross volumes.

- **`--stage-dir` is documented as the antivirus answer**, not just a relocation knob: a scan
  writes and deletes one staged copy per track, so real-time protection rescans a stream of
  copies of files it has already seen. New "Antivirus" note in the README.

## [0.5.4.9-RC1] — 2026-08-15

### Added

- **`--stats` reports the `.mbxs` sidecar's version, freshness and entry count**, not just its
  size — i.e. whether MBXHub can boot from it or is silently parsing the whole JSON. That
  readout existed only in `--verify`, which walks every file. An unusable sidecar earns a
  Recommended line naming the fix; a healthy one stays silent. The old-version case is the one
  size alone could never show: a v2 sidecar looks healthy while the hub declines it.

### Fixed

- `--stats`' README description still documented the single union speech count that `4d8a60c`
  split into two disjoint lines.

## [0.5.4.9-RC0] — 2026-08-15

### Added

- **`--prune-excluded`** — retires catalog entries an exclusion rule now covers. Rules gate
  *future* scanning and never retired entries analyzed before the rule existed, so those sat in
  `mbxmoods.json` indefinitely. `--dry-run` prints the exact removal list and writes nothing; a
  compressed rotated backup precedes the atomic swap and the `.mbxs` sidecar is regenerated.
  Rules are the sole authority — `include` still wins, nothing is removed on a classification,
  and `--no-exclusions` is refused rather than obeyed. Needs no XML and reads no audio, so it
  runs on a metadata mirror. Kept separate from `--fixup`, which needs the XML and reconciles
  paths against the filesystem.

- **`--stats` surfaces that gap** — the Recommended block counts the entries your rules cover and
  names `--prune-excluded`, and the wave-missing breakdown's "excluded from scanning" bucket now
  says those can be retired instead of dead-ending. A test pins the advisor's count to the
  prune's own removal set.

### Fixed

- **`--compact` and `--prettify` were missing from `--help`.** Both shipped in `f4cc04d` and are
  in the README, but the binary's help never listed them. The self-test's drift guard checks one
  direction only — a flag in help must exist in `KnownFlags`, not the reverse.

## [0.5.4.8-RC1] — 2026-08-13

### Added

- **The catalog writes compact by default; `--prettify` for viewing.** Single-line JSON cuts a
  real 72k-track catalog ~31% (565 MB pretty → 388 MB compact) with zero information loss.
  `--compact <path>` re-formats an existing catalog in place (archived backup first),
  `--prettify <src> [out]` produces the readable form. Both are pure formatters — they never
  re-analyze and never change values.
- **Multi-part catalog split at the 2 GB wall.** When a save would exceed `Int32.MaxValue`, the
  catalog splits into numbered parts (`mbxmoods.json.1`, `.2`, …) that every reader merges
  transparently, with contiguity and per-part track counts verified on load. Catalogs under the
  cap keep the single-file form.
- **`.mbxs` sidecar grown to v3 — the complete AutoQ hot path.** Carries a permanent
  identity+gate core (path, `audioStreamSha256`, genre, `speechLikely`) that a model retrain
  cannot drop, the full V/A model-input set, `averageLoudness`, the chords summary scalars
  (`chordsConcentration`/`chordsEntropy`, ported formula-exact from the hub), and per-track JSON
  offset+length so cold fields lazy-load without a parse. 27 MB against a 388 MB catalog, so the
  hub boots from the sidecar alone; the full JSON stays the durable record and the fallback.
- **`generatedBy` stamp** — every catalog write records the truedat build version in the header,
  through one shared finalize path.
- **`--verify` reports sidecar integrity** before the hash walk: present/size, format version,
  track count vs the catalog, freshness. Read-only, never affects the exit code.
- **Compact `prov` / `content` codes in the `truedat` verdict block**, both absent on a clean
  ordinary-music track. `prov` carries provenance defects — `pad16` (16-bit content padded into a
  24-bit container) and `tc` (lossy re-encoded from lossy); `up44`/`lossy` arrive with the Tier 1
  ceiling detector. `content` names a non-music class — `speech`, or `silence` (gated on
  `silenceRate60dB` plus loudness/energy corroborators). Write-time, retroactive and untuned; a
  `?` suffix marks low confidence.

### Changed

- **`trackId` is no longer written** and `--fixup` strips stored values — iTunes-XML-local
  numbering consumed by nothing, churning on every XML re-export and meaningless across merged
  libraries.

### Fixed

- **The speech verdict's silence vote was firing on ~the whole catalog.** It read
  `silenceRate30dB`, which an Essentia units bug (an amplitude threshold compared against a
  mean-of-squares power) gates at RMS < −15 dBFS — ordinary music trips it constantly, a Charlie
  Parker fixture reads 0.997. The vote now reads `silenceRate60dB` (effective −30 dBFS) with
  thresholds from the live catalog distribution (n=3716 ordinary-music entries: median 0.101,
  p90 0.299): talk above 0.30, music below 0.06. `speechMethod` bumps to
  `truedat-speech-v1.3-untuned-2026-08-13`. Write-time and retroactive — no rescan.
- **Catalog doubles now serialize in shortest text form.** net48's `Utf8JsonWriter` lacks the
  shortest-round-trip double formatter, so a dp-rounded value landed as its G17 expansion —
  `0.10100000000000001` for a stored `0.101`. A number's text is only shortened when parsing it
  back yields the bit-identical double; everything else keeps today's bytes. Applies to new saves
  and, through the token copier, to `--compact`/`--prettify` on existing catalogs. Measured on a
  72k-track catalog: 372.7 MB → 260.6 MB (**30% smaller**), values verified intact.

## [0.5.4.7-RC5] — 2026-08-11

### Changed

- **`--refresh` is now the verb** for the feature-refresh scan (was `--refresh-features`, which
  still works as an undocumented alias so in-flight passes keep running).
- **Throughput is duration-normalized.** The progress line and summary lead with `Nx realtime`
  (audio-seconds analyzed per wall-second, from the length Essentia actually decoded) with MB/s
  demoted to an IO footnote.
- **Scan-loop perf.** Workers no longer queue behind the 5-minute periodic catalog save (nor do
  error-CSV appends); a cache-miss track no longer pays a second whole-file hash pass when the
  cache-tier walk already read the body; the whole-file MD5 half of that pass is skipped when
  `--file-md5` is off; the bitUsage/HF ffmpeg subprocesses now attach to the `--cpu-limit` job
  object like every other child.

### Added

- **Field-emission policy (`mbxmoods-schema.json`) + `bpmHistogram` dropped.** Catalog field
  emission is now rule-driven by a schema file read by both the scan writer and `--fixup`
  (exclude-default: unlisted fields are emitted unchanged). Its first and only cut is
  `bpmHistogram` — **~29% of the catalog file (~217 MB on 72k tracks)**, mostly zeros, read by
  nothing; the usable tempo signal is in `bpmFirstPeak`/`bpmSecondPeak`, which stay. New scans
  omit it, existing catalogs shed it on the next `--fixup` — no re-analysis. Every chord field
  is unchanged, including the raw `chordsHistogram`.

- **Harmonic-texture + capture-once fields.** Each analyzed track now retains `hpcp12` (the
  12-semitone chroma profile, folded from the extractor's 36-bin `tonal.hpcp.mean`, unit-max
  normalized, 4dp), `dynamicComplexity`, and — captured in the same pass so no second re-scan is
  needed — `thpcp12` (key-invariant chroma), a beat-interval summary
  (`beatsIntervalMean`/`Stdev`/`Min`/`Max`), five frame-variability stdevs, and
  `mfccStdev`/`zeroCrossingRateStdev`. All were already computed and previously discarded — no
  extractor change. AutoQ can now judge how much two tracks' harmonic content overlaps rather
  than reading one Camelot key, and hold energy steady across a transition. Not a breaking
  change: existing catalogs read as before and nothing re-analyzes on its own. They are
  Essentia-derived with no parse-only backfill, so **populating an already-scanned library needs
  a refresh scan (`--refresh-features`)**, whose staleness check now correctly catches entries
  scanned before this wave.

- **Exclusion playlists.** Keep the exclusion list as a MusicBee playlist named
  `mbxmoods-exclude`: every scan discovers `mbxmoods-exclude.m3u8`/`.m3u` beside the library XML
  or in its `Playlists` folder and excludes those entries for the run. `--exclude-playlist <path>`
  names one explicitly; `--apply-exclusions` also accepts a `.m3u/.m3u8` directly, converting
  entries into permanent `file`/`exclude` rules. `include` rules still win, `--no-exclusions`
  bypasses the playlist too, and a present-but-unusable playlist stops the scan.

- **Scan health: incomplete analysis is a FAIL, not a silent success.** A track that Essentia
  decodes but that fails any component it should have produced — audio hash / fingerprint, tags
  (per-file modes), an applicable bitUsage/HF signal — or whose decode covered less than 95% of
  its claimed duration (a corrupt MP3 claiming 224 s decoded 62 s) is recorded in
  `mbxmoods-errors.csv` with a new `FailedComponents` column (`decode-27%;fingerprint;sha`) and
  **no catalog entry is written**. Failed files are skipped on later scans (`--retry-errors`
  re-attempts). Absent-by-design never fails a track: bitUsage on lossy/sub-24-bit files or
  silent windows, HF analysis at 44.1 kHz, either without ffmpeg, SMFM, and `fileMd5` without
  `--file-md5`.

### Fixed

- **Two always-zero feature fields corrected, kept not dropped.** `spectralFlatness` read a
  global Essentia key the music extractor never emits, so it was `0` on every track since
  2026-02-13; it is now derived from the per-band flatnesses already captured
  (`barkbands`/`erbbands`/`melbands_flatness_db`), which keep being emitted separately.
  `spectralDecrease` was a real ~1e-9 signal rounded to `0.000000` by 6-dp precision; it now
  keeps 12 dp. Existing entries gain the corrected values on re-analysis; both were weight-0 in
  the consumer model, so no pick behaviour changes until it is retrained.
- **Phantom-key detector.** A scan now records every Essentia key it reads and whether that key
  ever resolved, and lists any key queried but never resolved on any track — the signature of a
  wrong or absent key silently read as a default. This would have surfaced the `spectralFlatness`
  bug in the first scan log instead of hiding for six months. Detection only.

## [5.4.5] — 2026-07-30

### Changed

- **`--max-duration` default raised 12000 → 48000 seconds (200 → 800 min).**
  Paired with dist shipping the `output-x64.3` large-buffer Essentia extractor
  (which also carries the new `-O2` optimization fix — roughly 3× faster
  analysis). Long tracks (podcasts, DJ sets, up to ~13.3 h) now analyze by
  default; pass `--max-duration 12000` only if running the old small-buffer
  `.1` extractor.
- `--help` now shows a short everyday front page; the full option wall moved to
  `--help all`. Unknown/typo'd flags now error (exit 1) with a nearest-flag
  suggestion instead of being silently ignored — a typo'd flag used to run a
  plain full scan with no warning.

### Added

- **Preview page: searchable genre-rule picker.** The `--preview` review page's genre
  dialog lists every genre in the library with its track count and filters the list as
  you type (the box keeps focus while results narrow). Each genre row carries its own
  `excl` / `incl` buttons, so a rule is one keystroke-and-click away instead of hunting
  a wall of chips; the folder builder gained the same per-row split buttons. The picker
  dialog itself landed at the tail of the 5.4.4 window — the include half and the
  per-row button pattern are new here.
- **Scans keep the machine awake (AC only).** Work-bearing runs hold a Windows
  power request (`powercfg /requests` shows the reason; auto-released on exit,
  display still sleeps) so new Win11 machines stop sleeping mid-scan overnight.
  On battery normal sleep applies; `--allow-sleep` opts out. Truedat and its
  analysis subprocesses also opt out of Win11 efficiency throttling except
  under `--background`/`--cpu-limit`.

## [5.4.4] — 2026-07-25

Speech handling was rebuilt inside this window: the label/marker auto-skip and the `--migrate`
purges that shipped early in it were removed again before the tag. Only the end state is recorded.

### Added

- **`mbxmoods-exclude.json` — explicit scan exclusions**, now the only thing that keeps a track
  out of analysis for policy reasons. Three rule kinds (`folder` subtree pattern, `genre`, single
  `file`), each `exclude` or `include`, with `include` always winning. Folder patterns can be
  root-independent fragments (`\Podcasts\**`) so one rule works across a library mirrored on
  several drives; genre matching is exact and case-insensitive, never substring. Exclusions never
  remove existing catalog entries — they only stop future analysis, so the decision is reversible.
- **`--apply-exclusions <path>`** merges a decisions delta into that file, backs up the previous
  version, reports added / removed / already-set / not-present, and writes `apply-result.json`
  beside it on failure as well as success. Merging rather than overwriting is deliberate — the
  file has several legitimate authors. `--exclusions <path>` overrides the location,
  `--no-exclusions` bypasses it for one run with a warning.
- Every exclusion is ledgered in `mbxmoods-skipped.csv` with the rule that caused it, on every
  scan mode; the library scan also prints per-rule hit counts so a rule matching nothing is
  visible. A missing exclusion file excludes nothing; an unparseable one **exits 1 rather than
  scanning**. Invalid individual rules are skipped, counted and reported.
- **`--preview [path]`** — what a scan would do, without analyzing anything: library / analyzed /
  new counts, a time estimate, structural skip buckets, per-rule exclusion hit counts, a genre
  histogram, and the tracks worth a human decision. Writes `preview.json`, which doubles as the
  review-surface manifest MBXHub renders; never writes `mbxmoods.json` or the exclusion file.
  Auto-discovers the library XML the same way a scan does. The estimate comes from the catalog's
  own stored analysis times against known track lengths (`catalog-rtf`), omitted rather than
  guessed when nothing has been analyzed.
- **`mbxmoods-preview.html`** — a self-contained review page co-emitted beside `preview.json`,
  served byte-for-byte by MBXHub or opened offline from `file://`. Searchable genre picker,
  per-row exclude/include, per-row folder-subtree picker, and exclude/include all shown over the
  filtered rows. It emits a decisions delta; `--apply-exclusions` stays the only writer.
  Over-limit tracks are read-only — a structural skip is not a decision.
- **`--long-track-mins N`** (default 30) — the duration that flags a track for review. A prompt,
  not a rule: an hours-long ambient piece is one coherent thing, a setlist rip is many.
- **`--list-speech [path]`** — the entries whose recomputed verdict is `speechLikely=yes`, to
  `mbxmoods-speech.csv` (path, artist, title, album, genre, codec, confidence, method).
  Read-only; `--stats`' Recommended block routes through it.
- **`--list-missing-smfm [path]`** — catalog entries carrying no Sony SMFM block, with coverage,
  to `mbxmoods-smfm-missing.csv`. Deliberately absent from the Recommended block: truedat only
  reads SMFM, so no truedat command can close that gap.
- **`speechLikely` verdict** in the `truedat` block (`speechLikely`, `speechConfidence`,
  `speechMethod`), computed at write time from stored features, so the whole catalog picks it up
  on the next save with no rescan. Advisory only — it skips nothing and removes nothing.
- **`Avg length` in the scan summary** — mean audio duration of the analyzed tracks with the
  measured real-time factor. Essentia's cost scales with duration, so this is the line that
  explains a slow scan; sampled from the duration-known tracks, and it says so when that is a
  subset.
- **Scan traceability.** The skip ledger covers every pre-scan drop — video files, playlist /
  redirector entries, remote stream URLs (previously mangled into fake local paths and reported
  as missing every run), and missing files with path length called out past MAX_PATH.
  Over-MAX_PATH files now scan via a per-track `\\?\` fallback through staging. ETA prices
  remaining tracks by audio duration at the measured real-time factor and stays silent until
  something has actually been measured.
- **Recommended-commands advisor** — `--stats` and the end-of-scan summary map detected catalog
  state (wave-missing entries, absent `fingerprint.v1`, stray `fileMd5`) to the exact command
  that fixes it. Advisory only; truedat never prompts interactively.

### Changed

- **Nothing guesses at speech any more — the only thing that keeps a file out of analysis is a
  rule you write.** Breaking: earlier builds skipped XML-labelled speech and files carrying
  embedded markers, and `--migrate` pruned speech-labelled and speech-likely entries. All of that
  is gone, `--include-podcasts` with it. If you relied on the auto-skip, a large talk folder now
  gets analyzed unless you write a rule first: `truedat --preview`, write a decisions delta, e.g.
  `{"schemaVersion":1,"kind":"exclusion-decisions","add":[{"kind":"folder","action":"exclude","pattern":"\\Podcasts\\**"}],"remove":[]}`,
  then `truedat --apply-exclusions decisions.json` and scan.
- **The class is "speech", not "podcast".** Audiobook, comedy, lecture, news, interview and
  talk-dominant are one genus. "Podcast" is a piece of *evidence* about a speech track, not a
  class of its own; identifiers owned by other systems keep their real names (`Podcast=true`,
  `Genre=Podcast`, `PCST`, `WFED`, `TGID`, `pcst`, `purl`). The `--preview` reason
  `podcast-labelled` is now `speech-labelled`.
- **The speech signals survive purely as evidence** on `--preview`'s review candidates, and the
  file-marker evidence is now graded rather than first-match-wins: **strong** (ID3 `PCST` / MP4
  `pcst` — an app asserting "this IS a podcast") outranks **provenance** (`WFED` / `TGID` /
  `purl` — "came from a feed", which says nothing about content) outranks **genre text** (ID3
  `TCON`, now exact-equals rather than substring, which used to trip on "Podcast Rock").
- Docs and console say `--migrate` **prunes catalog entries**, not "deletes" — it removes entries
  from `mbxmoods.json` and never touches audio files.

### Fixed

- **`--apply-exclusions` wrote to the wrong file and reported success.** With no positional
  argument it fell back to the current directory, creating a brand-new exclusion file wherever
  you were standing, printing `Added: 1` and exiting 0 — while `--preview` and the scan read the
  library's file and excluded nothing. It now resolves the library like every other mode and
  **refuses** rather than silently creating a file it cannot place; the target is printed before
  the merge.
- **`genre` rules did nothing on the per-file scan paths.** `--file-list`, `--folder` and
  `--analyze-file` check exclusions before any cache tier and passed a hard-coded null genre, so
  a genre rule worked on the library scan and was inert on the autoscan path — where new files
  actually arrive. Now read via a header-only lookup, gated so the cost is paid only when a genre
  rule exists.
- **`--preview` reported working rules as stale.** It counted rule hits after its structural
  checks and the scan counted them before, so preview under-reported and printed
  `0 matched (stale rule?)` against live rules — on a metadata-mirror box, against *every* rule.
  Both now count over the same population.
- **Structural skips agree across every scan mode.** Video containers, DSD streams and playlist /
  redirector files cannot be decoded by any route, but the three per-file scan guards only
  checked DSD — so a `.mp4` piped through `--file-list` (the autoscan path) reached Essentia,
  failed, and landed in `mbxmoods-errors.csv` looking like a real error. One shared
  `StructuralSkipReason` now backs all of them, with reason text matching the XML-path filters
  exactly so the ledger reads the same whichever mode wrote the row.
- **Speech false positives on instrumental music.** `speechLikely` now also requires
  `danceability < 0.50` before returning `"yes"`. Sparse, live and free-form instrumental music
  craters on every other signal in the panel exactly like speech — no tempo peak, weak chords,
  weak key, high silence, high zero-crossing — and danceability is what separates them: genuine
  speech measures 0.00, real-music false positives 0.66–1.10. Without the gate, Charlie Parker
  "Hot House", Nine Inch Nails "Burn" (live) and Travis "Outro" were pruned from a live catalog.
  Demotes to `"unknown"`, never `"no"`. Method → `truedat-speech-v1.2-untuned-2026-07-22`.
- **`--migrate` pruned on a stale verdict.** It read the *persisted* `truedat.speechLikely` while
  the review surfaces recompute live from stored features, so on any catalog last saved by an
  older build the two disagreed and the review-before-prune workflow was silently defeated on
  exactly the catalogs that needed it. (The purge itself was removed later in this window.)
- **FLAC transition rescue in the default scan (tier 2.5)** — transition-era FLAC entries whose
  tags were rewritten before migrating are re-keyed in place, audio-props gated, instead of
  falling through to a full re-analysis. `--verify --backfill --accept-flac-tag-drift` applies
  the same rule in a verify pass.

### Removed

- **The `Episode Date` podcast vote.** MusicBee maps ID3v2.4 `TDRL` — a *release* date — into
  that key, so the anchor sits on ordinary music at scale, and "release date + a publisher" or
  "release date + 30 minutes" describes live albums, label releases, classical works and
  full-album rips. An invalid anchor rather than a mistuned threshold, which is why three rounds
  of tuning each traded one false-positive class for another. Ten self-test assertions pin the
  whole TDRL class as *not* a podcast so it cannot come back a third time.

### Schema

- **Tonal/rhythm extension wave — additive, not breaking.** All new keys are omit-when-missing:
  `keyVotes` (all three key profiles, each `{key, scale, strength}`), the tempo-histogram peaks
  (`bpmFirstPeak`/`Weight`, `bpmSecondPeak`/`Weight`/`Spread`), the chord fields (`chordsKey`,
  `chordsScale`, `chordsHistogram[24]`, `chordsNumberRate`), four tuning scalars, and
  `averageLoudness`. Essentia-derived, so legacy entries lack them until re-analyzed and existing
  caches stay valid. Fill an existing catalog with `--refresh-features` — resumable (saves every
  25 tracks) and idempotent.
- **FLAC identity is frame-anchored.** `audioStreamSha256` / `audioHead64kMd5` for FLAC now cover
  the audio frames only (`*Source: "flac-frames"`): TagLib's invariant region includes FLAC
  metadata blocks, so embedded tag writes drifted the old hashes with byte-identical audio.
  Stored old-style values upgrade in place on scan and `--verify --backfill`, no re-analysis.

### Docs

- **Feature counts are measured, not hand-kept.** Three places advertised "55 audio features per
  track" — right for the core and extended sets, never updated when the tonal/rhythm wave added
  fifteen more. The real figure is **70 named feature fields** (74 scalar values; 111 numbers once
  `mfcc[]`, `chordsHistogram[]` and the `keyVotes` strengths are counted). Two places also said
  truedat produces a valence/arousal mapping: it does not — it writes features, the mapping is
  made downstream, and `--migrate` strips stored `valence`/`arousal` as legacy keys.
- **Migration guidance corrected: analyze once, then exclude.** AutoQ picks from the MusicBee
  library and drops talk by reading each track's `speechLikely`, which exists only once the track
  is analyzed — so excluding speech *before* analysis makes AutoQ **more** likely to play it. The
  README now leads with analyze-once-then-exclude and states the trade for the other order.

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
