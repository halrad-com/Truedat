# Truedat Scan Output Reference

This document explains the input and output lines printed to the console when running a mood analysis scan.

## Input Lines

When you run a scan (e.g., `truedat.exe "iTunes Music Library.xml"`), truedat reports these inputs:

| Line | Description |
|------|-------------|
| `Scanning <path>...` | The source being analyzed. For iTunes XML, the file path. For `--file-list`, the files.txt path. For `--folder`, the directory being scanned. |
| `Loaded <N> tracks` | Number of tracks found in the source. For iTunes XML, total entries in the library. For file-list or folder scan, count of audio files discovered. |
| `Cache: <N> tracks` | Number of pre-existing entries in `mbxmoods.json` from a previous run. |

## Output Summary Lines

At scan completion, truedat prints a summary block (example from user's 5h25m scan):

```
Started:    2026-06-10 00:26:09
Finished:   2026-06-10 05:51:15
Elapsed:    5h25m

  Cached:     66651
  Cross-MD5:  203  (of 66651 cached)
  Cross-SHA:  18908  (of 66651 cached: 18853 same-path tag-edits, 55 cross-path)
  Analyzed:   4953
  Skipped:    151  (errors from previous run)
  SkippedDSD: 34  (unsupported codec)
  Failed:     1
  --------    -----
  Processed:  71790
  Output:     71404 tracks in moods file
  Avg/track:  79.0s (analysis only)
  Last save:  2.5s
  Peak mem:   4174 MB
  staging:    829 staged

Output: D:\MusicBee\Library\mbxmoods.json
```

### Summary Line Definitions

#### Timestamps & Duration

| Line | Meaning |
|------|---------|
| `Started: YYYY-MM-DD HH:MM:SS` | Scan begin time (UTC with local offset applied). |
| `Finished: YYYY-MM-DD HH:MM:SS` | Scan end time. |
| `Elapsed: XhYYmZZs` | Total wall-clock duration. Includes all I/O, cache checks, and analysis. |

#### Cache Statistics

| Line | Meaning |
|------|---------|
| `Cached: N` | Tracks already in the cache (path + mtime matched exactly, no body read). Fast tier-1 reuse. No Essentia re-run. |
| `Cross-MD5: M (of N cached)` | Subset of cached entries re-keyed via MD5 match (file moved to a different path, mtime signature changed, audio bytes unchanged). Tag-affected fields refreshed, Essentia features reused. `M of N` format shows count vs. total cached. |
| `Cross-SHA: M (of N cached: A same-path tag-edits, B cross-path)` | Subset of cached entries re-keyed via audioStreamSha256 match. `A`: same path + different mtime but unchanged audio (tag editor rewrote metadata). `B`: different path + audio unchanged (moved files detected by audio hash, not path). Total available reuse = `Cached + Cross-MD5 + Cross-SHA` minus overlaps. |

#### Analysis & Skips

| Line | Meaning |
|------|---------|
| `Analyzed: N` | Tracks that required a full Essentia audio decode. Cache miss — either not seen before or file changed. These are the "slow" tracks that dominate wall-clock time. |
| `Skipped: N (errors from previous run)` | Tracks in the skip list from a prior scan (logged to `mbxmoods-errors.csv` on the last run). Skipped again without retry unless `--retry-errors` is passed. |
| `SkippedDSD: N (unsupported codec)` | Tracks with DSD audio codec (`.dsf`, `.dff`). Not analyzed; logged to `mbxmoods-skipped.csv` with reason `unsupported codec`. DSD is a niche lossless format. Truedat doesn't decode DSD. |
| `Failed: N` | Tracks that failed analysis during this run (Essentia crash, ffmpeg transcoding error, file unreadable, etc.). Logged to `mbxmoods-errors.csv` with error reason. These will be skipped on the next run and can be retried with `--retry-errors`. |

#### Processing Summary

| Line | Meaning |
|------|---------|
| `Processed: N` | Total tracks touched = `Cached + Cross-MD5 + Cross-SHA + Analyzed + Skipped + SkippedDSD + Failed`. Should equal the total in the source (iTunes XML count, file-list line count, or folder walk). |
| `Output: M tracks in moods file` | Entries written to `mbxmoods.json`. Equals `Processed - Failed` (failed tracks are not written). In the example, `71790 - 1 failed = 71789`, but the actual value is `71404`, indicating some entries were also pruned or consolidated (e.g., duplicates by path, or entries dropped from the cache due to a schema bump). |

#### Performance Metrics

| Line | Meaning |
|------|---------|
| `Avg/track: N.Ns (analysis only)` | Average wall-clock time per **analyzed** (slow) track. Excludes cached hits; measures Essentia + identity hashing + authenticity analysis per file. In the example, `79.0s` per track = `4953 analyzed × 79.0s ≈ 391,287 s ÷ (5h25m wall-clock)`. The per-track average is higher than wall-clock average because parallelism hides the slowest track(s). |
| `Last save: X.Xs` | Time to write the final `mbxmoods.json` file to disk. Includes JSON serialization and atomic file rename. |
| `Peak mem: YYYY MB` | Maximum heap memory used during the scan. Depends on parallelism (`-p` flag) and library size. Single file deserves ~40 MB; 4-core `max(Analyzed / 4, ConcurrentWorkers)` tasks each holding ~200 MB. |
| `staging: N staged` | Files staged to a local temp directory (UNC shares, mapped network drives, non-ASCII paths). Each staged file is a full copy to `%TEMP%\.truedat-stage\<guid>.<ext>`. `N staged` = count of files that required a staging copy. Omitted from output if none were staged. |
| `staging: N staged, M direct-fallback` | If any staged-copy attempts failed, `M direct-fallback` reports how many fell back to reading the original source directly (with a one-line warning per fallback). When >5% of attempts fall back, a stderr warning flags a possible wedged staging directory. |

#### Final Output

| Line | Meaning |
|------|---------|
| `Output: <path>` | Filesystem path to the written `mbxmoods.json` file. Default: next to the source (iTunes XML, file-list, or folder). Override with `--moods <path>`. |

## Exit Codes

- **0** — Success. All tracked files processed. `Failed` count is 0 (or failures are acceptable within recovery strategy). Output written.
- **1** — Errors encountered. Check `mbxmoods-errors.csv` for details. Run with `--retry-errors` to retry failed files on the next invocation.

## Other Output Files

Alongside `mbxmoods.json`, these may be written:

| File | When | Contents |
|------|------|----------|
| `mbxmoods-errors.csv` | Failures occurred | Tracks that failed Essentia analysis: path, codec, size, duration, error reason. Consulted on next run to skip previously-failed files. |
| `mbxmoods-skipped.csv` | DSD or unsupported codecs | Tracks skipped because the codec isn't supported. Columns: `path`, `extension`, `reason`, `timestamp`. |
| `mbxmoods-verify.csv` | `--verify` or `--verify --backfill` mode | Per-entry status (OK / BACKFILLED / DRIFT / MISSING / REANALYZE_NEEDED / ERROR), plus the list of fields that were backfilled. |
| `truedat.log` | `--audit` flag used | Full console output including per-track progress and timings. Useful for debugging slow scans or understanding where time is spent. |

## Example Interpretation

The user's scan analyzed a **71,790-track library** over **5h25m**:

- **66,651 cached** → Most of the library was already analyzed (tier-1 fast path).
- **4,953 analyzed** → Only ~7% of the library required Essentia re-run (cache hits were excellent).
- **203 + 18,908 cross-matched** → ~28% of cached entries were re-identified via MD5/SHA because of path changes or tag edits, but audio was unchanged (no Essentia re-run).
- **151 skipped** → Pre-existing errors from a prior scan (not retried this run).
- **34 DSD** → Niche format, logged to skipped CSV.
- **1 failed** → One track failed during this run (logged to errors CSV).
- **79.0 s/track analysis time** → The 4,953 full analyses took ~79 seconds each on average (wall-clock per track is masked by parallelism across ~6-8 cores).
- **71,404 output** → Final entry count in `mbxmoods.json` (failed track + some consolidation = 386 entries not written).

Next run: the 66,651 cached entries will be tier-1 fast hits again. Any files that changed (mtime, tag edits, or were moved) will trigger tier-2/3/4 checks. Only actual audio changes or new files will require Essentia re-run.
