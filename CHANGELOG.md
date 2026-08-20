# Changelog

All notable changes to Truedat. Format loosely follows [Keep a Changelog](https://keepachangelog.com);
Truedat versions are release-candidate tags (`vX.Y.Z-RCn`) on `main`.

## [Unreleased]

## [0.5.4.9-RC6] — 2026-08-19

### Added

- **`--prune-entry <path>` (repeatable) — remove one specific catalog entry.** Removing a known
  entry meant writing a `file` exclusion rule and running `--prune-excluded`, which works but
  conflates two different acts: a rule is a standing *policy* decision about future scans, and
  sometimes the need is just "this one entry is wrong, take it out". This is the targeted
  counterpart to `--prune-excluded` — that verb acts on rules and removes whatever they cover,
  this one removes exactly what you named.

  Matching uses `PathComparer`, the catalog's **own** key comparer, so case and slash direction
  cannot decide whether a path is the right one, and the report echoes the *catalog's* spelling
  rather than the operator's so what matched is visible. A path that matches nothing is listed by
  name and the run exits non-zero — a typo, a stale path or the wrong catalog must never read
  like a completed removal. One entry named twice is removed once. Rotated `.zip` backup, atomic
  swap, sidecar regenerated, `--dry-run` reports and writes nothing.

  **It removes the ENTRY, not the file, and says so every run.** An entry is a scan *result*, so
  the file is still in the library and the next scan analyzes it again — durability needs a rule,
  which is what the printed note points at. Presenting a removal as permanent when the next scan
  undoes it is the same shape of silent failure `--strip-smfm` had before `--no-smfm` existed.

  Ground truth is the operator, not the filesystem: unlike `--fixup` it never asks whether the
  file exists, so it runs on a metadata mirror and on entries whose volume is offline — and for
  that same reason it needs no reachability probe, since it cannot mistake a downed share for a
  deletion when it never consults the share.

- **`--strip-smfm [path]` — removing Sony 12-TONE data from a catalog was impossible.** Three
  SMFM flags existed (`--list-smfm`, `--list-missing-smfm`, `--refresh-smfm`) and none of them
  removed anything. Refresh structurally *cannot*: `ApplySmfmInPlace` returns early when the
  file has no SMFM block, so it backfills but never clears. Combined with `smfm-tools` being
  able to copy a file *without* its SMFM block — deliberately preserving the audio frames so
  `audioStreamSha256` is unchanged — that was a one-way door pointing the wrong way: strip the
  file, rescan, and truedat takes a cache hit and carries the old scores indefinitely while
  `--list-smfm` still reports the entry as tagged. The tool that removes SMFM from the files was
  the one guaranteeing the catalog never noticed.

  The new mode removes **data, not entries** — every track and every other field survives, which
  is what separates it from `--prune-excluded`'s removal class. It strips **both key
  generations** (`smfm*` and the legacy `sensme*` the reader still falls back to, so a stripped
  entry cannot keep reading as tagged through the fallback), writes a compressed rotated backup
  first, swaps atomically, and **regenerates the `.mbxs` sidecar**. That last step is why this is
  a mode and not a hand edit: the hub boots sidecar-first, so editing the JSON alone changes
  nothing at runtime.

  It reports the cost before paying it — how many entries carry *scored* 12-TONE data, since
  those drop to the essentia-only mood head, the weaker one for valence. As a **count, never a
  share**: the same command is a couple of percent of one library and 93% of another, and a
  percentage is the form nobody can act on. `--dry-run` reports and writes nothing. Catalog-only
  (no XML, no audio), so it runs on a metadata mirror, and it takes the same anchor rule as the
  other catalog-rewriting verbs — it will not act on whatever `mbxmoods.json` happens to be in
  the directory you are standing in.

- **`--no-smfm` — because a strip alone is not durable.** SMFM is *read* from the file's Sony
  tag and never computed, so a stripped catalog refills on the next real analysis of any file
  that still carries the block: the strip is undone by ordinary work. This switch sits beside
  `--no-bitusage` / `--no-hf-analysis` and closes every read route — the cache-miss fan-out and
  the `--refresh-smfm` backfill alike (guarded inside `ApplySmfmInPlace` rather than at its call
  sites, so a future caller cannot reopen the door by forgetting the check). Unlike its two
  siblings it saves no meaningful time; it is what turns a one-off cleanup into a decision. It
  **refuses** to run alongside `--refresh-smfm`: one forces the re-read the other suppresses, and
  a silent no-op is exactly the failure mode this repo keeps paying for.

## [0.5.4.9-RC5] — 2026-08-19

Covers everything since RC3: **v0.5.4.9-RC4 shipped without its own section**, so the
`--list-formats` work tagged there is recorded here rather than left unattributed.

### Added

- **`--list-formats [path]` — what the library is made of, and where.** Every existing report
  answers a *completeness* question: `--stats` says how much of the catalog has been analyzed,
  hashed, or tagged. None of them says what is actually **in** it. So an ordinary question —
  "I still have WMA somewhere, where?" — had no answer short of exporting the catalog and
  pivoting it by hand. The new mode buckets entries by `fingerprint.v1.codec`, prints each
  format's count, share and lossy/lossless character, then rolls each format up by folder so a
  format can be *located* rather than merely counted. Full listing to `mbxmoods-formats.csv`
  (path, codec, codecRaw, bitrate, sampleRate, bitDepth, fileSize, artist, album, title).
  Read-only over the catalog, resolved through the same ladder as the other catalog modes, and
  catalog-only — it runs on a metadata mirror with no audio in reach.

  `bitrate` / `sampleRate` / `bitDepth` ride along as columns rather than becoming a second
  rollup: "and by bitrate?" is then a pivot table, not another mode to build and maintain.

  **Two buckets deliberately refuse to answer.** `wma` covers WMA *and* WMA Lossless, and
  `m4a` covers AAC *and* ALAC, because `NormalizeCodec` derives the label from TagLib's MIME
  type and TagLib cannot cheaply separate either pair — the catalog simply does not carry the
  distinction. Printing a confident "lossy" over a folder of WMA Lossless would be the report
  asserting evidence it does not have, so it names the pair and points at the tool that can
  settle it (`--convert-dir <folder> --wma-lossless-only`, which uses ffprobe on the actual
  bytes, on the machine holding the audio). Entries predating `fingerprint.v1` carry no codec
  at all and get their own `(not recorded)` row with the backfill command, rather than a codec
  inferred from the file extension: a guessed value sitting in the same column as a measured
  one makes the whole column untrustworthy, and the extension is precisely what a mislabelled
  file lies about.

  The folder rollup climbs one level above the track's own folder — the artist on an
  `Artist\Album` tree, so an artist's albums aggregate into one line instead of scattering —
  and **stops at the volume**: climbing past `\\server\share` or a drive root collapses every
  library on the box into a single meaningless bucket. Both the rollup and the lossy/lossless
  classifier are pure functions, so the summary table, the rollup and any future consumer
  cannot drift into disagreeing.

- **`--list-smfm [path]` — the full Sony SMFM (12-TONE) report.** truedat could tell you which
  catalog entries were *missing* 12-TONE data and could count the ones that had it, but
  nothing would list them. The gap showed up in the obvious way: a scan prints `+smfm` for a
  track that newly gains SMFM and `SMFM added: N` in the summary, an operator asks *which
  files those were*, and the only answer was transient console output — a change notice, not
  an inventory, silent about every track that already carried it. The new mode writes
  `mbxmoods-smfm.csv` (path, **state**, artist, title, album, codec, smfmBpm, smfmChannel,
  topScore, scores), read-only over the catalog and resolved through the same ladder as the
  other catalog modes.

  It reports **three** states, not two. Building it surfaced a real disagreement: truedat's
  internal `HasSmfm` counts an all-zero score vector as present, while the sibling Python
  reporter counted it as missing — each collapsing the same file into the opposite bucket.
  Neither is wrong, because the file is genuinely a third thing: a block Sony **wrote** and
  **scored nothing**. So the report names it. `data` = block present and scored, `no-data` =
  block present, every score zero, `no-smfm` = no block at all. The distinction is
  operational, not cosmetic — re-running Sony's tagger is the fix for `no-smfm` and is
  exactly what has already been tried for `no-data`, so collapsing them either way hands the
  operator a work list that is partly wrong. Classification lives in one pure function
  (`ClassifySmfm`) so the CSV, the counts and any future consumer cannot drift apart.

  Every entry gets a row rather than only the ones carrying data, so the three counts always
  sum to the catalog — one file that accounts for everything cannot disagree with itself
  about coverage the way two half-reports can, which is the failure this whole change came
  out of. The raw score vector is a column because `smfmChannel` is the argmax **slot**, not
  a mood channel: the index alone is not usable downstream, so the list carries what a
  consumer actually needs and doubles as the corpus view for format work.
- **`smfm-tools/check_moods_smfm.py` reports the same three states**, writing
  `smfm-data.csv` / `smfm-no-data.csv` / `smfm-no-smfm.csv` (replacing the old
  `smfm-missing.txt`; path is still column 1 in each). Its classifier mirrors truedat's
  `ClassifySmfm`, which is what closed the disagreement above. Sibling tooling, not part of
  `truedat.exe`.
- **`smfm-tools/smfm_strip_copy.py`** — copy a FLAC or MP3 **without** its SMFM block. The
  source is opened read-only and a new file is written; truedat has never modified an audio
  file and this does not change that, which is why it is sibling tooling rather than a verb.
  Audio frames are copied byte-for-byte, so the copy keeps the same `audioStreamSha256` as
  the source (the FLAC hash is frame-anchored, the MP3 invariant region excludes the ID3v2
  tag) — truedat sees the stripped copy as the same track and cross-SHA re-keys it instead of
  re-analyzing, which is what makes it usable as a control copy for format work. Both
  properties are asserted before anything is written. WMA and M4A are refused by name rather
  than silently passed through.

## [0.5.4.9-RC3] — 2026-08-16

### Changed

- **The scan progress line flags long tracks, not big ones.** A track was tagged with its size
  when the file exceeded 100 MB, which predicts nothing an operator cares about: Essentia's cost
  scales with audio **duration** — the ETA model on the same line is duration × RTF — so a
  three-hour 64 kbps talk file is ~86 MB and never tagged while being the slowest track in the
  scan, and a six-minute 24/192 FLAC is 139 MB, tagged, and finished fast. The tag is now the
  track's duration and appears at or past `--long-track-mins` (default 30), which until now only
  drove `--preview`'s review prompt and is one threshold shared by both surfaces rather than a
  second 30 hard-coded in the scan loop. Whether a track needs an ffmpeg transcode is deliberately
  *not* on this line — essentia has to fail or under-decode before that is known, which is after
  the line prints, so all three retry paths keep announcing themselves on their own lines.

## [0.5.4.9-RC2] — 2026-08-15

### Added

- **`--paths` reports every path this run would use** — catalog, sidecar, exclusion file, exclude
  playlist, library XML, log, the host-suffixed error/skipped ledgers, verify and duplicates
  outputs, review directory and staging directory — each marked present or absent, plus the line
  that matters most: **which step resolved the catalog**. Path resolution is invisible until it is
  wrong, and a catalog quietly read from the wrong directory looks exactly like working software
  until the numbers stop making sense. Absent entries are reported rather than omitted, because
  half the value is seeing that the exclusion file you believe is in force is not there. Read-only
  in the strict sense — it creates nothing, not even the review directory `--preview` makes.
  `--paths --json` emits the same data for MBXHub's status page.

- **`--verify` is now restartable via `--chunk M/N`.** A whole-catalog verify re-reads every
  file's audio to recompute its hash; on a ~6 TB / 156k library over WiFi that is an 18-24 hour
  pass, and it was one-shot — a NAS blip or a power cut and every hour of it was lost, with no
  resume and no way to shard. Each chunk now verifies its own deterministic slice (the same
  hash-mod split the scan uses, so the shards partition the catalog exactly — pinned by a test
  that the union covers every entry exactly once) and writes its own
  `mbxmoods-verify.<host>.<M>of<N>.csv`, so a finished shard is durably done and the pass can be
  run in restartable windows. Plain verify stays read-only and the catalog schema is untouched —
  the checkpoint-timestamp alternative would have changed the locked schema and turned a
  read-only audit into a 900 MB write, which is why it stayed parked. Under `--backfill` each
  shard's save rewrites the whole catalog from its own copy, so shards must run one at a time;
  truedat says so rather than letting a parallel run silently drop work.

- **Verify coverage receipts + `--verify-coverage`.** Each verify run drops an immutable receipt
  beside its CSV recording which slice it walked, folded into an order-independent digest;
  `--verify-coverage` combines them and proves the pass covered the catalog — every shard run,
  each exactly once, against one catalog state. The digest is what makes it a proof rather than
  a tally: XOR of the shards' folds equals the catalog's own fold *only* if they partitioned it,
  so a skipped shard leaves a residue and a shard counted twice cancels itself. This is a
  self-integrity check on the **process** — whether you are entitled to believe the verify —
  not a judgement on the audio, which is what the CSV says. Nothing is written to the catalog;
  the receipts are new files beside it.

### Fixed

- **`spectralFlatness` phantom zeros are now repaired, not just prevented.** The 2026-08-11
  repoint fixed the *write* path but could not reach the entries that needed it, and measurement
  showed that was nearly all of them: on a 72,129-entry catalog, **72,041 entries carried a
  phantom `0`**, and at least **71,971 of those were also gate-current** — 99.8% of the catalog,
  unreachable by any existing flag. The cause was a 23-hour ordering accident: the refresh gate
  began requiring `spectralContrastCoeffs` on 2026-08-10 01:23, the repoint landed 2026-08-11
  00:29, so a `--refresh-features` pass run between them wrote the batch-2 marker *and* a phantom
  zero. Those entries satisfy `HasCurrentFeatures`, so neither a normal scan nor
  `--refresh-features` would ever re-derive them — permanently pinned.

  Both write surfaces that can see the stored bands now re-derive from them, so the value is
  written correctly wherever it is written rather than only on a fresh analysis. `--fixup`
  repairs existing entries in place (`RepairSpectralFlatness`, reported as `Reflattened:` in the
  results block), and the cache-reuse copy heals a phantom zero as it copies
  (`HealSpectralFlatness` in `RebuildCacheEntryCore`) so an ordinary scan fixes it too. **No
  audio, no Essentia, no re-analysis** — `barkFlatness`/`erbFlatness`/`melFlatness` were never
  broken and are present on 100% of entries; the repair is pure arithmetic over data already on
  disk.

  Only a stored `0` is touched: a non-zero value came from the fixed write path off these same
  bands, and re-deriving it from the already-rounded stored bands would churn the last decimal
  place across a healthy catalog for no gain. An entry with no bands keeps `0` — the field is
  core/always-present and `0` is the honest answer there.

  **`spectralDecrease` is not repairable this way** and is unaffected: its raw extractor value is
  gone (the per-track extractor JSON is deleted at parse) and what is stored is the already-
  annihilated `0.000000`, so there is no on-disk input to re-derive from. Those entries need a
  genuine re-analysis, which the same gate still blocks.

- **Verify now refuses when the library is unreachable, instead of reporting every entry
  MISSING.** `File.Exists` cannot tell a deleted file from a downed NAS, an unplugged external
  drive, or WiFi dropping — so an offline volume produced a run that *completed* with thousands
  of MISSING rows, and would now have certified coverage of a slice it never read. Verify now
  probes each library root before walking (refusing outright if any is unreachable) and
  re-probes on the first MISSING, so a volume that drops **mid-pass** stops the run at that
  point rather than mislabelling the remainder. Nothing is written on either path — no CSV, no
  receipt, and no backfill computed against a half-read library. Same guard `--fixup` has had;
  verify was the gap.
- **A catalog mode that WRITES will no longer act on whatever `mbxmoods.json` happens to be in
  the current directory.** `--compact`, `--prettify`, `--restore` and `--prune-excluded` fell
  back to `<cwd>\mbxmoods.json` when given no path — no existence check, no evidence the file
  was a library catalog — then rewrote it and left a `.bak.zip` and a `.mbxs` beside it. Run
  from a source checkout it found the repo's own test fixture, which is exactly how it was
  caught. They now refuse and name the path they would have written; read-only modes
  (`--stats`, `--list-speech`, `--list-missing-smfm`) keep the convenience, because they cannot
  destroy anything. Same defect class as the `--apply-exclusions` cwd bug fixed earlier: a
  destructive verb must never inherit its target from wherever the operator is standing.
- **The catalog modes now find your library the same way a bare scan does.** A bare `truedat`
  has auto-discovered its library since the drop-in install shipped — it probes the exe's parent
  directory, which is what makes `<library>\truedat\truedat.exe` work with no arguments. Every
  catalog mode (`--stats`, `--prune-excluded`, `--list-speech`, `--list-missing-smfm`,
  `--snapshot`, `--compact` / `--prettify`, `--restore`, `--verify-coverage`) skipped that probe
  and went straight to the current directory, so on the layout the README recommends a bare scan
  worked while a bare `--stats` looked inside the truedat folder and reported the catalog
  missing. Because the exclusion file is resolved from the catalog's directory, the operator's
  rules went missing along with it — the mode ran, found no catalog and no rules, and nothing in
  the output said *which* directory it had looked in. Both resolvers now share one probe order
  (exe-dir's parent, then exe-dir, then the current directory). Reported from the field.
- **truedat run from MusicBee's hub data folder now finds the library.** `<root>\AppData\MBXHub\`
  is a natural place to keep the tool and run it from, but the library sits two levels up and
  across at `<root>\Library`, which neither the exe's parent nor the exe's own directory reaches.
  Discovery now walks a bounded number of parent directories and checks each one's `Library\`
  subfolder — the exact inverse of the mapping that already takes a library to its hub folder, so
  the two encode one layout rather than two guesses. Nearest-first, so a catalog in the folder you
  run from still wins; bounded, so it cannot wander into an unrelated library.
- **The catalog is found anywhere under a MusicBee instance**, including the instance's
  `AppData\` subfolders, and `%APPDATA%\MusicBee` is checked last for a non-portable install
  (whose data sits there while the program lives in Program Files, so nothing instance-relative
  reaches it). `<root>\Library` still wins when several candidates exist, and the user's default
  library is only consulted once nothing instance-relative matched — a tool inside a portable
  instance must resolve to that instance. A fixed set of known locations, not a recursive scan.

### Changed

- **Essentia's per-track output JSON now follows `--stage-dir`.** It was pinned to `%TEMP%`
  regardless, so pointing staging at a folder excluded from antivirus still left a temp file per
  track being scanned in real time. It also brings that file under the startup orphan sweep, which
  only ever knew about the staging directory — a killed scan used to leave one extractor JSON in
  `%TEMP%` permanently, with nothing that would ever collect them. Falls back to `%TEMP%` if the
  staging directory is not ASCII-printable, since that path becomes an argument to the extractor
  and non-ASCII argument paths are what staging exists to avoid. The per-drive `.truedat-tmp`
  hardlinks remain where they are — a hardlink cannot cross volumes.

- **`--stage-dir` is documented as the antivirus answer**, not only as a relocation knob. A scan
  writes and deletes one staged copy per track, so real-time protection re-scans a stream of
  short-lived files that are byte-for-byte copies of library files it has already seen; pointing
  the staging directory somewhere an exclusion covers avoids paying for that twice without
  having to except all of `%TEMP%`. New "Antivirus" note in the README covers it.

## [0.5.4.9-RC1] — 2026-08-15

### Added

- **`--stats` now reports the `.mbxs` sidecar's version, freshness and entry count**, not just
  its size. This answers "can MBXHub boot from this, or is it silently parsing the whole JSON?"
  — a question that previously had no cheap surface: the readout existed only in `--verify`,
  which walks every file recomputing SHAs, so the only way to check was to start an hours-long
  verify and Ctrl+C out of it. An unusable sidecar (absent, old version, stale, wrong count)
  also earns a Recommended line naming the fix; a healthy one stays silent. The old-version case
  is the one that matters and the one size alone could never show — a v2 sidecar looks perfectly
  healthy by size and mtime while the hub declines it and falls back to JSON.

### Fixed

- **`--stats`' README description still documented the single union speech count** that was
  split into two disjoint lines in `4d8a60c`.

## [0.5.4.9-RC0] — 2026-08-15

### Added

- **`--prune-excluded` retires catalog entries an exclusion rule now covers.** Exclusion rules
  gate *future* scanning; they never retired entries analyzed before the rule existed (or before
  exclusions shipped), so those sat stale in `mbxmoods.json` indefinitely — a talk archive you
  excluded months ago still occupied the catalog and could never be refreshed. The new mode
  cross-references the rules against the catalog and removes what they cover: `--dry-run` prints
  the exact removal list and writes nothing, a compressed rotated backup precedes the atomic
  swap, and the `.mbxs` sidecar is regenerated so it can't lag the catalog. Rules are the sole
  authority — `include` still wins, and nothing is removed on a classification (no speech verdict,
  no genre heuristic, no embedded marker); `--no-exclusions` is refused rather than obeyed, since
  it would bypass the very authority the mode acts on. Needs no iTunes XML and reads no audio
  (genre comes from the entry), so it runs on a metadata mirror. Audio files are never touched.
  Kept separate from `--fixup` deliberately: that mode needs the XML and reconciles *paths* against
  the filesystem, and folding a second removal class into it would change what it costs without
  the operator typing anything new.
- **`--stats` now surfaces that gap.** The **Recommended** block counts the entries your rules
  cover and names `--prune-excluded`; the wave-missing breakdown's "excluded from scanning"
  bucket — previously a dead end reading "a rescan skips these" — now says the rule-excluded
  ones can be retired. A test pins the advisor's count to the prune's own removal set, so the
  summary can never quote a number its command won't produce (the d146a04 rule: only ever name
  a gap some command can close).

### Fixed

- **`--compact` and `--prettify` were missing from `--help`.** Both have shipped since
  `f4cc04d` and are documented in the README, but the binary's own help never listed them, so
  the only way to discover them was to already know they existed. The self-test's drift guard
  didn't catch it because it checks one direction only — a flag *mentioned in help* must exist
  in `KnownFlags`, not the reverse.

## [0.5.4.8-RC1] — 2026-08-13

### Added

- **The catalog now writes compact by default, with `--prettify` for viewing.** `mbxmoods.json`
  is an operational file first — minimal (single-line) JSON cuts it ~31% (a real 72k-track
  catalog: 565 MB pretty → 388 MB compact) with zero information loss. `--compact <path>`
  re-formats an existing catalog in place (archived backup first); `--prettify <src> [out]`
  produces the human-readable form when you want to read it. Both are pure formatters —
  they never re-analyze and never change values.
- **Multi-part catalog split at the 2 GB wall.** JSON strings and whole-file reads hit a hard
  2 GB (`Int32.MaxValue`) limit; a 150k-track catalog was already close and 200k crosses it.
  When a save would exceed the cap, the catalog now splits into numbered part files
  (`mbxmoods.json.1`, `.2`, …) that every reader merges transparently (contiguity and
  per-part track counts verified on load). Catalogs under the cap keep the single-file form.
- **`.mbxs` sidecar grown to v3 — the complete AutoQ hot path.** The binary index now carries
  everything MBXHub needs at boot and per pick: a permanent identity+gate core block
  (path, `audioStreamSha256`, genre, `speechLikely`) that a model retrain can never drop,
  the full V/A model-input set, `averageLoudness`, the chords summary scalars
  (`chordsConcentration`/`chordsEntropy`, ported formula-exact from the hub), and per-track
  JSON offset+length so cold fields lazy-load from the full file without a parse. At 27 MB
  against a 388 MB compact catalog (~14× smaller), the hub boots from the sidecar alone —
  the full JSON stays the durable record and the fallback when no sidecar exists.
- **`generatedBy` stamp.** Every catalog write records the truedat build version in the
  header via one shared finalize path, so a catalog names the writer that produced it.
- **`--verify` reports sidecar integrity** before the hash walk: present/size, format
  version, track count vs the catalog, and freshness (stale sidecar → "run a scan or
  `--compact`"). Read-only, never affects the exit code.

### Changed

- **`trackId` is no longer written** (and `--fixup` strips stored values). It was
  iTunes-XML-local numbering consumed by nothing: the hub skips it, it churns on every XML
  re-export, and it is meaningless across merged libraries.

### Fixed

- **Speech verdict's silence vote was firing on ~the whole catalog.** It read
  `silenceRate30dB`, but an Essentia units bug (its silence-rate compares an *amplitude*
  threshold against a *mean-of-squares* power — off by a square) makes that field gate at
  RMS < −15 dBFS, which ordinary music trips constantly (a Charlie Parker fixture reads
  0.997). The vote now reads `silenceRate60dB` (effective −30 dBFS, the only honest gate)
  with thresholds derived from the live-catalog distribution (n=3716 ordinary-music
  entries: median 0.101, p90 0.299): talk when > 0.30, music when < 0.06. `speechMethod`
  bumps to `truedat-speech-v1.3-untuned-2026-08-13`. Write-time and retroactive — no rescan.
- **Catalog doubles now serialize in shortest text form.** net48's `Utf8JsonWriter` lacks the
  shortest-round-trip double formatter (.NET Core 3+ only), so a dp-rounded value landed in
  the catalog as its G17 expansion — `0.10100000000000001` for a stored `0.101`. Provably
  lossless: a number's text is only shortened when parsing it back yields the bit-identical
  double; everything else (full-precision, huge, tiny, non-finite) keeps today's bytes.
  Applies to new saves and — via the same predicate in the number branch of the token copier —
  to `--compact`/`--prettify` re-formatting of existing catalogs, no rescan needed.
  Measured on a real 72k-track catalog: 372.7 MB → 260.6 MB (**30% smaller**), values verified
  intact (100% Essentia + identity coverage after the pass).

### Added

- **Compact `prov` / `content` codes in the `truedat` verdict block**, both **absent on a
  clean ordinary-music track** (the common case costs zero bytes). `prov` carries
  provenance-defect codes — `pad16` (16-bit content padded into a 24-bit container, emitted
  from the existing bit-usage hi-res verdict) and `tc` (lossy re-encoded from lossy) live
  now; `up44`/`lossy` arrive with the Tier 1 ceiling detector. `content` names a non-music
  content class — `speech` (an alias of `speechLikely: "yes"`) or `silence` (a mostly-silent
  track, gated on `silenceRate60dB` plus loudness/energy corroborators). Both are write-time,
  retroactive, and untuned; a `?` suffix marks low confidence.

## [0.5.4.7-RC5] — 2026-08-11

### Changed

- **`--refresh` is now the verb** for the feature-refresh scan (was `--refresh-features`).
  The old `--refresh-features` still works as an undocumented alias, so existing scripts
  and in-flight passes keep running.
- **Throughput is now duration-normalized.** The progress line and end-of-scan summary
  lead with `Nx realtime` (audio-seconds analyzed per wall-second — the honest unit:
  a FLAC and a low-bitrate MP3 of the same content cost the same to scan) with MB/s
  demoted to a footnote for IO overhead. The numerator is the length Essentia actually
  decoded, not the claimed duration.
- **Scan-loop perf (T1/T2/T6):** workers no longer queue behind the 5-minute periodic
  catalog save (TryEnter + skip; error-CSV appends also stop serializing against
  saves); a cache-miss track no longer pays a second whole-file hash pass when the
  cache-tier walk already read the body; the whole-file MD5 half of the single-pass
  hash is skipped entirely when `--file-md5` is off; the bitUsage/HF ffmpeg
  subprocesses are now attached to the `--cpu-limit` job object like every other child.

### Added

- **Field-emission policy (`mbxmoods-schema.json`) + `bpmHistogram` dropped.** Catalog field
  emission is now rule-driven by a small schema file read by both the scan writer and
  `--fixup` (exclude-default: any field not listed is emitted unchanged). Its first and only
  cut is `bpmHistogram` — a raw tempo histogram that was **~29% of the catalog file (~217 MB
  on a 72k-track library)**, mostly zeros, and read by nothing (the usable tempo signal lives
  in the separate `bpmFirstPeak`/`bpmSecondPeak` fields, which stay). New scans omit it;
  existing catalogs shed it on the next `--fixup` pass — **no re-analysis** to remove it.
  Everything else is unchanged, including every chord field (the raw `chordsHistogram` is
  kept). Reinstating a cut Essentia field would cost a full re-scan, so cuts are deliberate.

- **Harmonic-texture + capture-once fields.** Each analyzed track now retains a
  12-semitone chroma vector (`hpcp12` — the track's pitch-class energy profile, from the
  extractor's 36-bin `tonal.hpcp.mean` folded to 12 semitones, unit-max normalized, 4dp),
  `dynamicComplexity` (a loudness-dynamics scalar), and — captured in the same pass so no
  second re-scan is ever needed — `thpcp12` (key-invariant chroma), a beat-interval summary
  (`beatsIntervalMean`/`Stdev`/`Min`/`Max`), five frame-variability stdevs, and
  `mfccStdev`/`zeroCrossingRateStdev` (MFCC/zero-crossing variability, for speech/timbre).
  All were already computed by the analyzer and previously discarded — no extractor change.
  **What gets better:** AutoQ can now judge how much two tracks' harmonic content actually
  overlaps — the whole pitch profile, not just the one Camelot key it reads today — so it
  can pick a next track that blends more smoothly, and use the dynamics scalar to keep
  energy steady across a transition (the other captured fields are for later picking and
  speech work). **Not a breaking change** — existing `mbxmoods.json` files read exactly as
  before and nothing re-analyzes on its own. `--refresh-features` now correctly re-analyzes
  entries scanned before this wave (its staleness check previously skipped them, so the new
  fields could never populate). **To populate the new properties on an already-scanned
  library, run a refresh scan (`--refresh-features`)**: they are Essentia-derived with no
  parse-only backfill (the raw extractor output is discarded at parse), so existing tracks
  gain them only on re-analysis; new or changed tracks pick them up automatically on any
  normal scan.

- **Exclusion playlists.** Maintain your exclusion list as a MusicBee playlist named
  `mbxmoods-exclude`: every scan discovers `mbxmoods-exclude.m3u8`/`.m3u` beside the
  library XML (or in its `Playlists` folder) and excludes every entry for that run —
  edit the playlist, rescan, exclusions follow. `--exclude-playlist <path>` names one
  explicitly; `--apply-exclusions` now also accepts a `.m3u/.m3u8` directly, converting
  entries into permanent `file`/`exclude` rules (backup + `apply-result.json` as usual).
  `include` rules in `mbxmoods-exclude.json` still win; `--no-exclusions` bypasses the
  playlist too; a present-but-unusable playlist stops the scan (fail closed).

- **Scan health: incomplete analysis is now a FAIL, not a silent success.** A track
  that Essentia decodes but that fails any component it should have produced — audio
  hash / fingerprint (TagLib refuses a corrupt header), tags (per-file modes), an
  applicable bitUsage/HF signal — or whose decode covered less than 95% of its claimed
  duration (truncated file: a corrupt MP3 claiming 224 s decoded only 62 s, so its
  features described the first minute) is recorded in `mbxmoods-errors.csv` with a new
  `FailedComponents` column naming exactly what failed (e.g.
  `decode-27%;fingerprint;sha`), and **no catalog entry is written**. Failed files are
  skipped on subsequent scans (`--retry-errors` re-attempts after the file is fixed) —
  previously such files were re-analyzed every run and produced identity-less entries
  downstream consumers could never index. Absent-by-design is never a failure: bitUsage
  on lossy/sub-24-bit files or pure-silence windows, HF analysis at 44.1 kHz, either
  without ffmpeg, SMFM, and `fileMd5` without `--file-md5` are all exempt.

### Fixed

- **Two always-zero feature fields corrected (kept, not dropped).** `spectralFlatness` had read a
  global Essentia key the music extractor never emits, so it was `0` on every track since
  2026-02-13; it is now **derived** from the per-band flatnesses
  (`barkbands`/`erbbands`/`melbands_flatness_db`) that were already captured — a real tonal-vs-noisy
  value, and the three band values keep being emitted separately. `spectralDecrease` was a real
  ~1e-9 signal being **rounded to `0.000000` by 6-dp precision**; it now keeps 12 dp. Existing
  entries gain the corrected values on re-analysis; both were weight-0 in the consumer model, so no
  pick behavior changes until it is retrained.
- **Phantom-key detector (durable guard against the above).** During a scan truedat now records
  every Essentia key it reads and whether that key ever resolved; at end of scan (and always under
  `--audit`) it lists any key queried but **never** resolved on any track — the exact signature of a
  wrong/absent key silently read as a default. This would have surfaced the `spectralFlatness` bug
  in the first scan log instead of hiding for six months. Detection only — no field's default is
  changed.

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

### Fixed — post-ship gap hunt: nine defects in the exclusion and scan paths (2026-07-26)

A review pass over the shipped code, aimed at the seams *between* separately-reviewed pieces
rather than at any one change. Everything below was demonstrated with a reproduction before
being fixed. All nine shipped under this tag.

**Three that misled the operator, in the order they mattered:**

- **`--apply-exclusions` wrote to the wrong file and reported success.** With no positional
  argument it fell back to the *current directory*, creating a brand-new exclusion file
  wherever you happened to be standing, printing `Added: 1` and exiting 0 — while `--preview`
  and the scan read the library's file and excluded nothing. That was the exact invocation the
  migration note documents. It now resolves the library the same way the other modes do, and
  **refuses** rather than silently creating a file it cannot place. The target is printed
  before the merge, so a wrong one is visible even when the merge then fails.
- **`genre` rules did nothing on the per-file scan paths.** `--file-list`, `--folder` and
  `--analyze-file` check exclusions before any cache tier and so had no parsed tags; they
  passed a hard-coded null genre. A `genre` rule therefore worked on the library scan and was
  inert on the plugin-driven autoscan path — where new files actually arrive. Now read via a
  header-only lookup, gated so the cost is paid only when a genre rule exists.
- **`--preview` reported working rules as stale.** It counted rule hits after its structural
  checks, the scan counted them before, so preview under-reported and printed
  `0 matched (stale rule?)` against live rules. On a metadata-mirror box — where the audio
  lives on another machine — *every* rule read as stale. A signal built to expose dead rules
  was condemning live ones. Both now count over the same population.

### Docs — feature counts measured (2026-07-26)

Three places advertised "55 audio features per track". That was right for the core and extended
sets but was never updated when the tonal/rhythm wave added fifteen more; the real figure is **70
named feature fields** (74 scalar values; 111 numbers once `mfcc[]`, `chordsHistogram[]` and the
`keyVotes` strengths are counted as values). The counts are now measured from the JSON write
surface with the recipe written down, and `CLAUDE.md` says not to maintain a total by hand — a
hand-kept number has nothing to check it and reads as authoritative while being wrong.

The larger error was attribution: two places said truedat produces a valence/arousal mapping. It
does not — it writes features, and the mapping to valence/arousal values is made downstream. 

--migrate` actively *strips* valence` and `arousal` as legacy keys, that were once written,

in favor of computing them so the model updates are dynamic wihtout re-writes. 

so the docs promised an output the tool deliberately removes in current design intents. 

### Docs — migration guidance corrected: analyze once, then exclude (2026-07-26)

The upgrade note shipped in af3fb2f told upgraders to write an exclusion rule *before* their next
scan. For talk you want AutoQ to stop playing, that backfires: AutoQ picks from the MusicBee library,
not `mbxmoods.json`, and drops talk by reading each track's `speechLikely` verdict — which exists only
once the track is analyzed. Excluding speech from scanning before it is analyzed removes that signal, so
AutoQ becomes *more* likely to play it, not less. README now leads with **analyze once, then exclude**
and states the trade for excluding-before-scan (skip the scan cost, forfeit the AutoQ signal). Behaviour
unchanged; docs only.

### Changed — speech is analyzed by default; the class is "speech", not "podcast" (2026-07-25)

**Breaking behaviour change.** Nothing in truedat guesses at speech any more. Older builds
in this same Unreleased window skipped anything the XML labelled as speech (a podcast flag or
genre), plus files carrying embedded speech markers, and `--migrate` pruned speech-labelled and
speech-likely catalog entries. All of that is gone: **the only thing that keeps a file out of
analysis is a rule you write** into `mbxmoods-exclude.json`. If you relied on the old auto-skip,
upgrading without adding a rule first means those files get analyzed — for a large talk/podcast
folder, that can mean hours of unplanned scan time.

Migrate before your next scan:

1. `truedat --preview` — lists what a scan would do, including every track worth a look.
2. Write a decisions delta, e.g.
   `{"schemaVersion":1,"kind":"exclusion-decisions","add":[{"kind":"folder","action":"exclude","pattern":"\\Podcasts\\**"}],"remove":[]}`
   (a `genre` rule for `Podcast` works too — pick whichever matches how your library is organised).
3. `truedat --apply-exclusions decisions.json`
4. Scan.
- **The class is "speech", not "podcast".** Truedat's internal vocabulary was migrated
  podcast→speech: audiobook, comedy, lecture, news, interview and talk-dominant are one genus —
  speech. "Podcast" is one kind of *evidence* about a speech track, not a class of its own.
  External identifiers owned by other systems keep their real names — `Podcast=true`,
  `Genre=Podcast`, `PCST`, `WFED`, `TGID`, `pcst`, `purl` — renaming those would misdescribe
  what was matched. The `--preview` review reason `podcast-labelled` is now `speech-labelled`.
- **`--include-podcasts` is removed.** It was all-or-nothing; the exclusion file (with its
  per-rule `include` override) is the replacement and was already the recommended path for a
  mislabelled album.
- **`--migrate` no longer prunes anything.** Its two purges (genre-labelled speech entries, and
  `truedat.speechLikely == "yes"` entries) are both gone — pruning an entry for a file still in
  your library was self-undoing anyway, since the next scan just re-analyzed and re-added it.
  `--migrate` now only strips legacy fields and renames SMFM keys, same as it always did for
  those.
- **The speech signals still exist, purely as evidence.** The XML labels (`Podcast=true`,
  `Genre=Podcast`) and the embedded file-marker sniff both survive, but only to populate
  `reasons` on `--preview`'s review candidates — a human decides, then writes a rule.
- **The file-marker evidence is now graded** instead of first-match-wins: **strong** (ID3
  `PCST` / MP4 `pcst` — an app asserting "this IS a podcast") outranks **provenance** (ID3
  `WFED`/`TGID`, MP4 `purl` — "this came from a feed", which says nothing about content, since
  music gets distributed by RSS too), which outranks **genre text** (ID3 `TCON`). `TCON` is
  now **exact-equals** (trimmed, case-insensitive) instead of a substring match — the old
  substring test made the file rule strictly looser than the library-side genre rule, so
  `"Comedy Podcast"`, `"Podcasts"`, and `"Podcast Rock"` all tripped it.
- **`--stats` reports ONE speech count** — the union of the label (stored genre `Podcast`) and
  the acoustic `speechLikely` verdict, each entry counted once — pointing at review + an
  exclusion rule, never `--migrate`. (Earlier in this Unreleased window it printed two
  mutually-exclusive buckets; collapsed to a single count so no podcast-shaped number lingers.)

### Added — `--preview`, the read-only scan work plan (2026-07-25)

- **`truedat --preview [path]`** answers "what would a scan do?" without analyzing
  anything: library/analyzed/new counts, a time estimate, structural skip buckets,
  per-rule exclusion hit counts (a rule matching zero tracks is called out as
  possibly stale), a genre histogram, and the list of tracks worth a human
  decision. Writes `preview.json` — which doubles as the review-surface manifest
  MBXHub renders — and never writes `mbxmoods.json` or touches the exclusion file.
  Auto-discovers the iTunes XML the same way a normal scan does (exe dir, its
  parent, then the cwd), so it needs no positional argument from inside the
  library directory.
- **`--long-track-mins N`** (default 30) sets the duration that flags a track for
  review. It is a review *prompt*, not a rule: *long* is not the same class as
  *bad to analyze*. An hours-long ambient piece or a DJ set is one coherent thing;
  a radio show or a setlist rip is many pieces that collapse into one meaningless
  average. Only a human separates those from metadata.
- The estimate is derived from the catalog's own stored analysis times against
  known track lengths (median, reported as `catalog-rtf`). With nothing analyzed
  yet it is omitted rather than guessed.
- **`--apply-exclusions` now writes `apply-result.json`** beside the exclusion
  file, on failure as well as success, so a tool driving truedat reads a file
  instead of parsing console output.

### Added — `mbxmoods-preview.html`, the interactive scan-preview review page (2026-07-25)

- **`--preview` co-emits a self-contained review page** beside `preview.json` and
  records `source.reviewHtml`, so MBXHub serves it byte-for-byte (the `dupes.html`
  precedent). It also opens offline from `file://` — the plan data is embedded
  inline. It renders the manifest and emits an exclusion *decisions delta*
  (`add`/`remove`); it never writes the exclusion file — `--apply-exclusions` is the
  only writer.
- **Bulk-first triage:** a searchable genre picker dialog (excluded genres show as a
  short removable list), per-row exclude/include, a per-row folder-subtree picker,
  and *exclude / include all shown* over the currently-filtered rows. Pending genre
  and folder exclusions reflect live on the affected candidate rows.
- **Over-limit tracks are read-only** — a file past the analysis ceiling is a
  structural skip, not a decision, so it is shown for awareness (split/transcode, or
  raise `--max-duration`) without exclude/include controls.
- **Host-aware:** served from MBXHub the page refreshes counts and posts decisions
  back to the hub; opened from disk it downloads the delta and shows the
  `--apply-exclusions` line. Every served call has a timeout and falls back to the
  offline path, so the page never hangs on the hub.

### Fixed — structural skips (video/DSD/playlist) now agree across every scan mode (2026-07-25)

- Video containers, DSD streams, and playlist/redirector files are structurally
  unanalyzable — no rule, `include` exclusion, or retry can make Essentia decode
  them. The iTunes-XML scan path already dropped all three categories up front
  (`FilterVideoFiles` / `FilterNonAudio`, plus the DSD guard), but the three
  **per-file** scan-entry guards (`--analyze-file`, `--file-list`/`--folder`,
  MoodsMode's per-track guard) only checked the DSD set. A `.mp4` (or `.m3u`,
  `.wpl`, …) piped through `--file-list` — the path the MBXHub autoscan plugin
  drives — reached Essentia, failed, and landed as a real-looking row in
  `mbxmoods-errors.csv` for a file that was never analyzable, unattended.
- Added `StructuralSkipReason`, a shared helper next to the old DSD-only
  `IsUnsupportedExtensionForAnalysis` (removed), that returns the drop reason
  for any of the three buckets or `null` when the extension is fine to attempt.
  Reason text matches the XML-path filters exactly (`"unsupported codec: DSD"`,
  `"video file extension"`, `"playlist / redirector extension"`) so
  `mbxmoods-skipped.csv` reads consistently no matter which mode wrote the row.
  All three per-file guards now call it instead of the DSD-only check; DSD's
  console output and ledger reason are unchanged.

### Removed — the Episode Date podcast vote (2026-07-24)

- **Deleted the `Episode Date` + corroborator podcast vote.** MusicBee maps
  ID3v2.4 **`TDRL`** into the iTunes XML `Episode Date` key, and `TDRL` is a
  **release date** per the ID3v2.4 spec — Apple repurposed it for podcast
  episode dates and the ecosystem followed. So the vote's anchor is present on
  ordinary music at scale, and "release date + a publisher" or "release date +
  30 minutes" describes a large slice of legitimate music (live albums, label
  releases, classical works, full-album rips). This is an invalid anchor rather
  than a mistuned threshold, which is why three rounds of tuning each traded one
  false-positive class for another. Podcast labelling is now **explicit only**:
  iTunes-native `Podcast=true`, or `Genre=Podcast`. Ten self-test assertions pin
  the whole TDRL class as *not* a podcast so it cannot come back a third time.

### Added — explicit scan exclusions (2026-07-24)

- **`mbxmoods-exclude.json`** — a per-library decision file, beside `mbxmoods.json`,
  that is now the only thing which keeps a track out of analysis for policy
  reasons. Three rule kinds (`folder` subtree pattern, `genre`, single `file`),
  each `exclude` or `include`, with `include` always winning. Folder patterns
  can be written as root-independent fragments (`\Podcasts\**`) so one rule
  works across a library mirrored on several drives. Genre matching is exact and
  case-insensitive, never substring.
- **`--apply-exclusions <path>`** merges a decisions delta into that file, backing
  up the previous version and reporting added / removed / already-set /
  not-present counts. Merging rather than overwriting is deliberate — the file
  has several legitimate authors and a whole-file write would discard whichever
  went second. **`--exclusions <path>`** overrides the location;
  **`--no-exclusions`** bypasses it for one run with a warning.
- Every exclusion is ledgered in `mbxmoods-skipped.csv` with the rule that caused
  it, on every scan mode. The iTunes-XML library scan additionally prints per-rule
  hit counts, so a rule that matches nothing is visible instead of silently doing
  nothing; the per-file modes (`--analyze-file`, `--file-list`/`--folder`) have
  nothing to count for a single invocation, but still print a warning for any
  rule that failed to parse.
- A missing exclusion file excludes nothing. A file that cannot be parsed makes
  truedat **exit 1 rather than scan** — analyzing everything while you believe
  your rules are in force is the silent failure this replaces. Invalid individual
  rules are skipped, counted and reported.
- Exclusions do **not** remove existing `mbxmoods.json` entries; they only stop
  future analysis, so the decision is reversible.

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
