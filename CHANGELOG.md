# Changelog

All notable changes to Truedat. Format loosely follows [Keep a Changelog](https://keepachangelog.com);
Truedat versions are release-candidate tags (`vX.Y.Z-RCn`) on `main`.

## [Unreleased]

### Changed

- **`--refresh` is now the verb** for the feature-refresh scan (was `--refresh-features`).
  The old `--refresh-features` still works as an undocumented alias, so existing scripts
  and in-flight passes keep running.

### Added

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

### Changed

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
