# Truedat Scan Output Reference

Date: 2026-05-23 (revised 2026-07-26 — re-verified every line against `Program.cs`; several
console strings had drifted, including one relabelled the same day as this revision; re-verified
2026-08-09 for v0.5.4.7 — console I/O is unchanged by the harmonic / capture-once field wave, which
adds JSON fields only; documented the pre-scan "lack the latest features" refresh advisory)

This document explains the input and output lines printed to the console when running a mood
analysis scan. It is primarily written against the **default iTunes-XML scan** (the zero-arg /
`truedat.exe "iTunes Music Library.xml"` invocation, internally `MoodsMode`) — that is the
scan the vast majority of runs use. `--file-list` and `--folder` print a much shorter one-line
summary instead of the block below; see [Other scan modes](#other-scan-modes) at the end.

## Input Lines

When you run a default scan, truedat prints these lines while it loads:

| Line | Meaning |
|------|---------|
| `Loading iTunes library: <path>` | The iTunes XML file being read. |
| `Found <N> tracks` | Total `<dict>` track entries parsed from the XML — before any filtering. A track referencing the same file path as another counts twice here (see the `Output` note below). |
| `  Exclusions: <N> rule(s) from <path>` | Printed when `mbxmoods-exclude.json` (or `--exclusions <path>`) has at least one rule. Omitted when the exclusion set is empty. |
| `  WARNING: --no-exclusions — the exclusion file is being ignored for this run` | Printed instead of the line above when `--no-exclusions` is passed. |
| `Existing moods: <N>` | Entries already in `mbxmoods.json` from a previous run, loaded before this run's work list is built. |
| `  SHA index: <N> entries available for tag-edit / cross-machine matching` | Size of the `audioStreamSha256 → entry` cross-index built from the existing catalog; feeds the Cross-SHA cache tier. |
| `Existing errors: <N>` | Rows loaded from `mbxmoods-errors.csv` (tracks that failed on a prior run and are skipped again unless `--retry-errors`). |
| `  New to catalog: up to <N> track(s) / <size> — full analysis expected (estimate; cache tiers may reduce this at scan time)` | Pre-flight estimate of tracks that will need full Essentia analysis, computed from cache membership alone (no file IO) — an upper bound, since the sha/head-64k cache tiers can still turn some of these into cache hits once the scan actually reads them. |
| `  Estimated time: <duration> (<audio-duration> of new audio, <rtf>x realtime, <N> workers)` | Duration-based ETA from the catalog's own measured real-time factor. Omitted (`not estimable yet...`) when there's no analyzed history to learn a rate from. |
| `  <N> entries lack the latest features — run: truedat --refresh` | A nudge printed after the pre-flight estimate when Essentia-analyzed catalog entries lack the newest feature wave (currently the 2026-08-09 harmonic / capture-once fields, detected via `dynamicComplexity`). Run a `--refresh-features` pass to populate them. Omitted when `--refresh-features` is already set or the count is zero. |

`Cache: <N> tracks` and a bare `Scanning <path>...` line — as an earlier revision of this doc
showed — do not appear anywhere in the current code for any scan mode. `Existing moods:` and
`Loading iTunes library:` are the real lines; don't grep your logs for the old ones.

## Pre-scan filtering (exclusions, structural drops)

Four filters run over the full track list before the per-track loop begins, each printing its
own line (and each ledgering every dropped track to `mbxmoods-skipped.csv` — see
[Other Output Files](#other-output-files)):

1. **Remote stream URLs** (un-downloaded podcast-feed episodes whose XML `Location` is a
   stream URL, not a file):
   `  Skipped <N> remote stream URL(s) — listed in mbxmoods-skipped.csv (--audit lists each)`.
2. **Exclusions** — `mbxmoods-exclude.json` is the sole authority over what a scan skips for
   *policy* reasons (as opposed to "structurally cannot analyze"). Three rule kinds — `folder`,
   `genre`, `file` — each `exclude` or `include`, with `include` always winning. When any track
   is dropped:
   ```
     Excluded 340 track(s) by rule — listed in mbxmoods-skipped.csv (--audit lists each)
       genre=Podcast: 340 matched
       folder=\Old Shows\**: 0 matched   (stale rule?)
   ```
   Every rule prints its match count, always — a rule matching zero tracks is how a renamed
   folder or retagged genre announces itself (`(stale rule?)`), which is the diagnostic the old
   podcast heuristics never had. Under `--chunk M/N`, rule evaluation is still library-wide (so
   the match counts stay identical and trustworthy on every shard), but each shard's ledgered
   count and CSV rows are its own bucket only; the console adds a note explaining the two
   numbers legitimately disagree in that case.
3. **Video files** (extension-based):
   `  Skipped <N> video file(s) — listed in mbxmoods-skipped.csv (--audit lists each)`.
4. **Playlists / redirectors** (`.m3u`, `.m3u8`, `.pls`, `.wpl`, `.asx`, `.cue`, `.xspf`):
   `  Skipped <N> playlist / redirector file(s) — listed in mbxmoods-skipped.csv (--audit lists each)`.

The `(--audit lists each)` suffix is only appended when `--audit` was **not** passed — it's a
pointer to the flag that would have printed the per-track detail instead of just the count. With
`--audit`, each dropped track gets its own `[skipped ...] Artist - Title :: path` line and the
suffix is omitted.

Tracks dropped here are removed from the work list entirely — they are never counted in
`Processed` below, which only tallies what reaches the per-track loop. DSD files (`.dsf`,
`.dff`, `.dsd`) are **not** filtered at this stage; they survive into the loop and are caught
there (next section), which is why the in-loop counter is named for DSD even though its label
now also covers video/playlist as a defensive second check.

`--preview` reports the same four categories, but folds them into one compact block with
different, shorter labels (`streamUrl`, `video`, `missing`, `dsd`, plus its own `Excluded:`
line) — see the README's *Previewing a scan* section. Don't expect the exact wording to match
between `--preview` and a real scan; the underlying counts do.

## Output Summary Lines

At scan completion, truedat prints a summary block. This is an **illustrative** example (not a
real captured run) built to show every line in one place — a real run typically won't show every
optional line at once:

```
Started:    2026-07-26 09:00:00
Finished:   2026-07-26 09:41:12
Elapsed:    41m12s

  Cached:     11400
  Head-quick: 500  (of 11400 cached: tags-only via audioHead64kMd5)
  Cross-SHA:  150  (of 11400 cached: 120 same-path tag-edits, 30 cross-path)
  Analyzed:   250
  Skipped:    4  (errors from previous run)
  Skipped (structural): 6  (DSD / video / playlist — see mbxmoods-skipped.csv)
  Over-length: 2  (exceeds --max-duration ceiling — see mbxmoods-skipped.csv)
  Missing:    5  (file not found — see mbxmoods-skipped.csv)
  Failed:     21
  --------    -----
  Processed:  11688
  Output:     11867 tracks in moods file
  Avg/track:  74.3s (analysis only)
  Avg length: 3m48s of audio per analyzed track  (0.320x realtime)
  Analyzed IO: 9.8 GB @ 4.0 MB/s scan-wide
  Last save:  1.1s
  Peak mem:   3120 MB
  staging:    150 staged

Output: D:\MusicBee\Library\mbxmoods.json
```

### Summary Line Definitions

#### Timestamps & Duration

| Line | Meaning |
|------|---------|
| `Started: YYYY-MM-DD HH:MM:SS` | Scan begin time (local clock). |
| `Finished: YYYY-MM-DD HH:MM:SS` | Scan end time. |
| `Elapsed: XhYYmZZs` | Total wall-clock duration. Includes all I/O, cache checks, and analysis. |

#### Cache Statistics

| Line | Meaning |
|------|---------|
| `Cached: N` | Tracks reused from the cache this run, by any tier. Tier-1 (path + mtime equality, no body read at all) is the bulk of this in a steady-state library. |
| `Head-quick: H (of N cached: tags-only via audioHead64kMd5)` | Subset of `Cached` resolved by tier 1.5 — path matched, mtime drifted, but a 64 KB head-hash check confirmed only tags changed (no full audio re-hash needed). Printed only when `H > 0`; disabled by `--no-quick-cache`. |
| `Cross-SHA: M (of N cached: A same-path tag-edits, B cross-path)` | Subset of `Cached` re-keyed via `audioStreamSha256` match (tier 2 / tier 4). `A`: same path, different mtime, unchanged audio. `B`: different path, unchanged audio (moved/renamed file, detected by audio hash). Printed only when `A + B > 0`. (An older `Cross-MD5` subset line existed before v5.4.0 and no longer prints — the cross-MD5 tier was removed; cross-SHA is a strict superset.) |
| `SHA backfill: N (legacy cache hits gained audioStreamSha256)` | Cache hits from entries that pre-date `audioStreamSha256` and had it computed in place this run. Printed only when `N > 0`. |
| `FLAC re-key: N (pre-transition hash + tag rewrite; audio props verified, no re-analysis)` | FLAC entries rescued across the 2026-07-21 frame-anchored-hash transition (see CLAUDE.md). Printed only when `N > 0`. |
| `SMFM added: N (tracks that gained Sony 12-TONE data this scan)` | Cache hits whose file newly carries an embedded SMFM/12-TONE tag that wasn't there (or wasn't read) before. Printed only when `N > 0`. |

#### Analysis & Skips

| Line | Meaning |
|------|---------|
| `Analyzed: N` | Tracks that required a full Essentia audio decode this run (cache miss). The slow tracks that dominate wall-clock time. |
| `Skipped: N (errors from previous run)` | Tracks in `mbxmoods-errors.csv` from a prior scan. Skipped again without retry unless `--retry-errors`. |
| `Skipped (structural): N (DSD / video / playlist — see mbxmoods-skipped.csv)` | **Relabelled 2026-07-26** — the older label read `SkippedDSD: N (unsupported codec)`, which was already inaccurate: the counter (internally still named for DSD) has always covered any DSD/video/playlist file that reached the per-track loop, not only DSD, and calling a skipped `.mkv` or `.m3u` an "unsupported codec" was wrong. In ordinary operation this line is almost entirely DSD, since video and playlist files are already removed by the pre-scan filters above — it exists as a defensive second check so every mode agrees on the same definition of "structurally unanalyzable." |
| `Over-length: N (exceeds --max-duration ceiling — see mbxmoods-skipped.csv)` | **Changed 2026-07-26.** A track whose duration exceeds `--max-duration` (default 800 min / 48000 s, the large-buffer Essentia extractor's `ChordsDetection` ceiling; the old small-buffer .1 extractor needs `--max-duration 12000`) is now a **structural skip** — ledgered to `mbxmoods-skipped.csv`, not counted as a failure, and *not* written to `mbxmoods-errors.csv`. Before this change it was recorded as a failure, which meant raising `--max-duration` on a later run could never re-evaluate it — the errors-CSV row made it a permanent silent skip. Now the next run re-evaluates it automatically if the ceiling is raised. This mode-scoped fix currently applies to the default iTunes-XML scan only; `--analyze-file` / `--file-list` / `--folder` do not enforce `--max-duration` at all. |
| `Missing: N (file not found — see mbxmoods-skipped.csv)` | **Changed 2026-07-26** for consistency across modes — a track whose file no longer exists is a structural skip (ledgered to `mbxmoods-skipped.csv`) on **every** scan mode now, including the per-file paths (`--file-list`, `--analyze-file`), not just the iTunes-XML walk. Not a failure, not written to `mbxmoods-errors.csv`, and does not cause a non-zero exit code by itself. The reason names the MAX_PATH cause when the path is ≥260 characters, since that's usually the actual explanation rather than deletion. |
| `Failed: N (M timed out)` | Tracks that failed analysis this run (Essentia crash, ffmpeg transcode error, unreadable file, timeout, etc.). Logged to `mbxmoods-errors.csv` with a reason. Skipped on the next run unless `--retry-errors`. The `(M timed out)` suffix is only appended when at least one failure was a timeout. |

#### Processing Summary

| Line | Meaning |
|------|---------|
| `Processed: N` | `Cached + Analyzed + Skipped + Skipped(structural) + Over-length + Missing + Failed` — every track that reached the per-track loop, classified into exactly one bucket. This is **not** the full XML track count: tracks dropped by the pre-scan filters (excluded-by-rule, remote URL, video, playlist — see above) are removed from the work list before `Processed` is ever tallied, so they never appear in this arithmetic. |
| `Output: M tracks in moods file` | Entries in `mbxmoods.json` after this run's save — the size of the whole in-memory catalog dictionary, not a count derived from `Processed`. It is **not guaranteed to equal `Processed − Failed`**, and can differ in either direction: **higher**, because the catalog can hold entries this run never touched — tracks dropped by the pre-scan filters (e.g. now excluded by a rule, but analyzed before that rule existed) and true orphans (removed from the library entirely) both stay in the catalog until an explicit `truedat --fixup`, which is the only thing that prunes them; **lower**, because `mbxmoods.json` is keyed by file path, and an iTunes library can list the same file `Location` under more than one track entry (each counted separately in `Found <N> tracks` and in `Processed`, but occupying one dictionary slot). There is no "schema bump" or automatic dedup/consolidation logic in the code that prunes entries during a scan — an earlier revision of this doc speculated that as the explanation for an observed shortfall; that speculation has been removed rather than repeated, since it wasn't verifiable and doesn't match how `allTracks` is maintained. |

#### Performance Metrics

| Line | Meaning |
|------|---------|
| `Avg/track: N.Ns (analysis only)` | Average wall-clock time per **analyzed** (cache-miss) track — total analysis thread-time divided by `Analyzed`. Higher than true wall-clock-per-track because parallelism hides the slowest tracks. |
| `Avg length: MmSSs of audio per analyzed track (R.RRRx realtime)` | Average duration of the audio itself for analyzed tracks, next to the measured real-time factor (analysis thread-time ÷ audio duration). Explains *why* a scan is slow: cost scales with audio duration at a roughly constant RTF, so a 40-minute average track costs ~20× what a 2-minute one does. Sampled from the duration-known subset when that's fewer than `Analyzed` (a `, K of N sampled` suffix appears in that case). |
| `Analyzed IO: <size> @ N.N MB/s scan-wide` | Total bytes read for analyzed tracks and the effective throughput across the whole elapsed wall-clock. Printed only when analyzed bytes were tracked. |
| `Last save: X.Xs` | Time to write the final `mbxmoods.json` to disk (serialization + atomic rename). |
| `Peak mem: NNNN MB` | Peak working-set memory for the process. Depends on parallelism (`-p`) and library size. |
| `staging: N staged` | Files staged to a local temp directory this run (UNC shares, mapped network drives, non-ASCII local paths). Omitted entirely if nothing was staged. |
| `staging: N staged, M direct-fallback` | Printed instead of the line above when any staged-copy attempts fell back to reading the original source directly. A `WARNING: staging degraded` line is also printed to stderr when the fallback rate exceeds 5% (a wedged stage directory — no disk space or bad permissions — surfaces visibly instead of just running slow). |

There is also an optional **"Per-track cost by outcome"** block (thread-time average per track
class — e.g. `analyzed`, `cached·sha`, `skip·overlength`) printed just before `Analyzed IO`,
whenever at least one track was classified this run. It exists to show which outcome actually
dominates the run's cost; it isn't reproduced here since its rows vary run to run.

#### Final Output

| Line | Meaning |
|------|---------|
| `Output: <path>` | Filesystem path to the written `mbxmoods.json`. Default: next to the iTunes XML. Override with `--moods <path>`. |

## Exit Codes

Exit code is a direct, unconditional function of `Failed`:

- **0** — `Failed` is 0. Structural skips (DSD/video/playlist, over-length, missing,
  excluded-by-rule) never affect this — only `Failed > 0` does.
- **1** — `Failed` is greater than 0 (at least one track failed analysis this run). Check
  `mbxmoods-errors.csv` for the reasons. Re-run with `--retry-errors` to retry.

## Other Output Files

Alongside `mbxmoods.json`, these may be written:

| File | When | Contents |
|------|------|----------|
| `mbxmoods-errors.csv` | Failures occurred | Tracks that failed Essentia analysis: path, codec, size, duration, error reason. Consulted on the next run to skip previously-failed files unless `--retry-errors`. |
| `mbxmoods-skipped.csv` | Any pre-analysis drop occurred | The ledger for **every** track a scan declined to analyze for a reason other than "it failed": remote stream URLs, tracks matching an exclusion rule, video files, playlists/redirectors, DSD files, tracks over `--max-duration`, and missing files — on all scan modes. Columns: `path`, `extension`, `reason`, `timestamp`. The `reason` column values in current use: `remote stream URL (not a file)`, `excluded (rule: <description>)`, `video file extension`, `playlist / redirector extension`, `unsupported codec: DSD`, `over max duration: <dur> > <dur> ceiling (--max-duration to override)`, `file not found` (or `file not found (path N chars >= 260 MAX_PATH)`). Under `--chunk M/N`, each shard writes only the rows for tracks it owns — the union of every shard's CSV is the library's full picture, not any single shard's file. |
| `mbxmoods-verify.csv` | `--verify` or `--verify --backfill` mode | Per-entry status (OK / BACKFILLED / DRIFT / MISSING / REANALYZE_NEEDED / ERROR), plus the list of fields that were backfilled. |
| `preview.json` / `mbxmoods-preview.json` / `mbxmoods-preview.html` | `--preview` mode | See below. |
| `truedat.log` | `--audit` flag used | Full console output including per-track progress and timings. Useful for debugging slow scans or understanding where time is spent. |

## Previewing a scan (`--preview`)

`--preview` answers "what would a scan do?" without scanning: it analyzes nothing, writes no
`mbxmoods.json`, and never touches the exclusion file. It is **not** read-only over the
filesystem, though — with no explicit output path it creates
`<library root>\AppData\MBXHub\review\` (a running MBXHub instance's data directory, if one
owns that library) and writes `preview.json` plus a self-contained `mbxmoods-preview.html`
review page into it; falling back to beside the moods file
(`mbxmoods-preview.json`) when no instance owns the library. Pass `--preview <path>` to choose
the destination yourself. See the README's *Previewing a scan* section for the full console
output shape and the exact guarantees.

`preview.json` is also the machine-readable manifest MBXHub's review surface renders directly —
there's no separate offline producer for it.

## Other scan modes

`--file-list` and `--folder` share one code path and print a **single summary line** instead of
the block above (to stderr):

```
Done: 412 processed (380 cached, 30 analyzed), 2 failed, 3 skipped in 118.4s
```

`skipped` here lumps together every non-failure drop this run (missing files, DSD/video/
playlist, and excluded-by-rule tracks) behind one counter — it does not break them out the way
the default scan's summary does; consult `mbxmoods-skipped.csv` for the per-track reasons. A
`, N SMFM-added` suffix appears when applicable. `--analyze-file` processes a single file and
prints its own one-track result rather than any of the above.

## Cache tier names

The cache hierarchy is tiers **1, 1.5, 2, and 4** (not 1-2-3-4): the former tier 3 (cross-MD5)
was removed in v5.4.0 because cross-SHA (tier 4) catches a strict superset of what it caught.
The "4" name was kept as-is, rather than renumbered to "3", so historical hit-tag labels and
discussion of the cache hierarchy stay legible across that change.
