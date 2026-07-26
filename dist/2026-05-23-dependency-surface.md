# Truedat dependency surface — what's needed, what breaks if missing

Date: 2026-05-23 (updated 2026-07-11 — legacy fingerprint pipeline removed; updated 2026-07-26 —
re-verified against `Program.cs`, including staging/sweep behavior changed the same day)
Scope: runtime dependencies of `dist/truedat/truedat.exe` and what happens when each is absent. Authoritative source: `Truedat/Program.cs` + `dist/truedat/` contents.

## Lookup mechanism

All native helpers are located via `FindTool(exeName, params searchDirs)` in `Program.cs` — a simple `File.Exists` walk across the directories passed in. Caller order is always: exe-dir → output-dir / library-dir → CWD. No PATH probe except for `ffmpeg`/`ffprobe`, which use a separate `FindFfmpeg`/`FindFfprobe` that *also* falls back to `where.exe`. Result: drop binaries next to `truedat.exe` and they're found; nothing else needs configuration.

## Dependency matrix

| File | Required? | Used by | Missing-file behavior |
|---|---|---|---|
| **`essentia_streaming_extractor_music.exe`** | **HARD** (default scan) | `MoodsMode`, `--file-list`, `--analyze-file`, `--folder` | `Console.Error.WriteLine("Error: essentia_streaming_extractor_music.exe not found")` and the mode aborts. No fallback — moods cannot be computed without it. |
| **`ffmpeg.exe`** | **SOFT** for default scan / **HARD** for some modes | `ComputeBitUsage`, `ComputeHfAnalysis` (Phase 2.5+3+5 signals), multi-channel downmix retry, `Unsupported codec` retry (e.g. `.opus`), `--transcode`, backfill features tier | Lazy-resolved via `_ffmpegPath`. On null: every helper that consumes it returns `null` and the worker continues — affected features simply omit from JSON. Status banner prints `not found (multi-channel files will be skipped)`. **Hard fail** in `--transcode` and the backfill features tier prints `WARNING: ffmpeg not found on PATH — features tier will silently skip every entry.` Phase 4 verdict block downgrades to `"unknown"`/`"n/a"` because Signal F (`hfSpectralStructure`) and `bitUsage` vote weights vanish. **Same omission shape can be requested explicitly** via `--no-bitusage` (drops `ComputeBitUsage` only) or `--no-hf-analysis` (drops `ComputeHfAnalysis` only) — both flags are orthogonal to ffmpeg presence and skip the subprocess regardless. |
| **`ffprobe.exe`** | **SOFT** | `--transcode` (source-rate matching) | Lazy. `--transcode` still works without it but loses native-rate matching. Default scan does not call ffprobe. |
| **NuGet: `TagLibSharp`, `System.Text.Json`** | Merged into exe via ILRepack | All TagLib reads, all JSON I/O | Not separable at runtime — already inside `truedat.exe`. |

The legacy fingerprint binaries (`essentia_streaming_md5.exe`, `fpcalc.exe`, `essentia_standard_chromaprinter.exe`) and the modes that invoked them (`--fingerprint` / `--md5-only` / `--quick-fingerprint` / `--details`) were removed on 2026-07-11 (v5.4.0).

## Source staging

Independent of the dependency list above, the default scan path stages remote and non-ASCII source files to `%TEMP%\.truedat-stage\<guid>.<ext>` before fanning out the 8-9 concurrent workers per track. Net effect: 1× full network read per track instead of ~3× full + ≥3× partial. The non-ASCII / 8.3-disabled footgun disappears for free — the staged copy uses a GUID-based ASCII-only filename on a local volume, and the extension is sanitized to `.bin` when the source ext isn't ASCII printable.

Staging triggers for:

- **UNC paths** (`\\server\share\…`) — method `staged`.
- **Mapped network drives** (a drive letter where `DriveInfo.DriveType == Network` — e.g. `Z:\` mapped to `\\server\share`) — method `staged`. Per-root memoized; one `DriveInfo` lookup per unique drive letter, then cached for the rest of the scan.
- **Local non-ASCII paths** — method `staged-fallback`. Covers the case where the existing `TryCreateHardlink` mitigation can't help (hardlinks don't help paths on volumes that don't host hardlinks).
- Local ASCII-only paths on local NTFS pass through directly — no copy.

The cache hierarchy is stage-aware and is tiers **1, 1.5, 2, and 4** (the former tier 3, cross-MD5, was removed in v5.4.0; cross-SHA at tier 4 is a strict superset, so the "4" name was kept rather than renumbered). Tier-1 (path + mtime equality) and tier-1.5 (path match + mtime drift + a 64 KB head-hash check, `--no-quick-cache` to disable) do NOT open a staging handle — tier-1.5 reads only the first 64 KB, straight from the original path. Tier-2 (path + audioStreamSha256), tier-4 (cross-SHA), and the cache-miss worker fan-out all route their body reads through one lazily-opened staged copy via `EnsureStagedSrc()` — opened on first body read, reused for every subsequent read, disposed in `finally`. So a tier-1 or tier-1.5 hit on a UNC library track stays free (or nearly free); a tier-2/4 hit or cache miss copies once and amortizes the copy across all 8-9 reads.

`SourceHandle.SourceLastWriteUtc` captures the source's mtime inside `OpenStagedSource` immediately after `File.Copy`. The scan modes record `TrackEntry.LastModified` from this snapshot instead of re-stat'ing at end-of-work, so a tag touch between the copy and the entry being persisted can't make the recorded mtime newer than the analyzed audio.

The per-track concurrent worker fan-out (Essentia + ComputeFileMd5 (only when `--file-md5` is passed — otherwise the task returns `null` and `fileMd5` is never written) + ComputeFingerprintV1 + ComputeAudioStreamSha256FromFile + ComputeBitUsage + ComputeHfAnalysis + SMFM + optional ExtractFileTags) lives in `RunSourceWorkers` — one shared helper called from all three scan paths. `extractTags=false` lets MoodsMode skip TagLib re-read when iTunes XML already supplies metadata; `knownDurationSec > 0` short-circuits the bitUsage TagLib re-parse for the same case.

Flags (`--help` carries the same):

- `--no-stage` — disable staging; workers read source directly (pre-staging behavior).
- `--stage-dir <path>` — override the scratch dir used for staged source copies **and** the multi-channel downmix WAVs (default `%TEMP%\.truedat-stage`). Validated writable at startup; per-track stage failures fall back to direct read with a `  Warning:` stderr line. Since 2026-07-26 this is also the isolation lever for running two truedat invocations concurrently against different stage dirs — see the sweep-scope note below; it isolates two of the three sweep targets, not all three.
- `--no-bitusage` — suppress `ComputeBitUsage` (omits the `bitUsage` JSON block).
- `--no-hf-analysis` — suppress `ComputeHfAnalysis` (omits `hfEnergyRatio` + `hfEnergyMethod` + `hfSpectralStructure`).
- `--no-quick-cache` — disable the tier-1.5 head-64k quick-cache check. Relevant here because tier 1.5 is the one cache tier that does **not** open a staged copy on a mtime-drifted hit (it reads just the first 64 KB from the original path); disabling it pushes those hits down to tier 2, which does stage the whole file. On a UNC/mapped-drive library this flag trades a cheaper check for a full network copy on every tag-touched track, so it interacts with the staging cost even though it isn't a staging flag itself.

End-of-scan summary: `EmitStagingSummary` prints `staging: N staged` (or `N staged, M direct-fallback` when fallbacks occurred), and emits a `WARNING: staging degraded` stderr line when more than 5% of attempted stages fell back to direct read — surfaces a wedged stage-dir (no disk space, missing permissions) instead of silently slow scans.

No new dist dependencies; staging is pure-managed `File.Copy`. Orphans from killed scans are swept at next startup by `CleanupOrphanedFiles`.

**Startup sweep scope (changed 2026-07-26, `fix(staging): stop the startup sweep reaching past --stage-dir`).** The sweep deletes aged-out (60+ minute old) leftovers from three places: staged copies, multi-channel downmix WAVs, and per-drive `<drive>\.truedat-tmp` hardlink scratch. Before this change, downmix WAVs were hard-coded to `%TEMP%` in both writer and sweeper, so a second invocation using a different `--stage-dir` could still delete a *live* run's in-flight downmix — and a WAV disappearing mid-Essentia surfaced as a mis-diagnosed analysis failure on that track, not as a collision. `DownmixToStereo` now writes into `--stage-dir`, so **two of the three** sweep targets (staged copies, downmixes) are isolated by giving each invocation its own `--stage-dir`. The third, `<drive>\.truedat-tmp` hardlinks, **cannot be relocated by `--stage-dir`** — a hardlink must live on the same volume as its source, so there is no directory to redirect. That target instead relies on two runtime guards, which now exist for all three targets as the backstop: the sweep refuses entirely (with a stderr warning naming `--chunk` as the supported alternative) when `CountSiblingTruedatProcesses()` finds another truedat process on the machine, and separately spares any file touched within the last 60 minutes regardless of process visibility (covering a sibling on another machine, or a blocked process-enumeration call). Truedat is still single-instance by design and the supported cross-machine scaling path is `--chunk M/N` plus `--merge-moods`, not concurrent runs against one library — the guards make an accidental second run *safe* (it won't delete the first run's live files), not *supported*.

## Failure-mode summary

Three categories:

1. **Default scan blocks** without `essentia_streaming_extractor_music.exe`. This is the only true hard runtime requirement for the primary flow. Everything else is optional.

2. **Quality degrades silently** without `ffmpeg.exe`:
   - No `bitUsage` block (Phase 2.5).
   - No `hfEnergyRatio` / `hfSpectralStructure` (Phase 3+5).
   - Multi-channel files / `.opus` / other unsupported-codec files get skipped instead of retried.
   - Phase 4 verdict downgrades to `"unknown"` for the affected questions because Signals C/F drop out.
   - No `--transcode`.
   - Backfill `features` tier becomes a no-op (prints warning).

3. **Mode-specific aborts** with clear error messages:
   - `--transcode` → needs `ffmpeg.exe` (ffprobe optional).

## Recommendations

- **Consider preflight banner:** the default-scan modes print one ffmpeg/ffprobe status line on startup, but a user who runs only `--analyze-file` doesn't get a similar advisory before Phase 2.5/3+5 silently no-op. Optional polish, not a defect.

## Quick reference: dependency on/off matrix for the default scan

| `truedat library.xml` (default scan) | essentia ext. | ffmpeg | ffprobe | Result |
|---|---|---|---|---|
| All present | ✓ | ✓ | ✓ | Full feature set (70 named fields / 111 numbers / 74 scalar values — see README's *Extracted Features* section for the measured figures; don't repeat a hand-maintained count here) + verdict |
| ffprobe missing | ✓ | ✓ | ✗ | Same (ffprobe not used here) |
| ffmpeg missing | ✓ | ✗ | — | Core 15 + most extended; no Phase 2.5/3/5 fields, verdict mostly "unknown", multi-channel skipped |
| Essentia missing | ✗ | — | — | Abort with error |
| All present, `--no-bitusage` | ✓ | ✓ | ✓ | Full features minus `bitUsage` block; verdict loses Signal A |
| All present, `--no-hf-analysis` | ✓ | ✓ | ✓ | Full features minus `hfEnergyRatio` + `hfSpectralStructure`; verdict loses Signals B + F |
| All present, `--no-stage` | ✓ | ✓ | ✓ | Same output as "all present"; UNC / mapped-network / non-ASCII-local sources are read directly instead of via local copy |
