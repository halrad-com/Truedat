# Truedat dependency surface — what's needed, what breaks if missing

Date: 2026-05-23 (updated 2026-06-09 — source staging review fixes; DirectML drop)
Scope: runtime dependencies of `dist/truedat/truedat.exe` and what happens when each is absent. Authoritative source: `Truedat/Program.cs` + `dist/truedat/` contents.

## Lookup mechanism

All native helpers are located via `FindTool(exeName, params searchDirs)` at `Program.cs:3779` — a simple `File.Exists` walk across the directories passed in. Caller order is always: exe-dir → output-dir / library-dir → CWD. No PATH probe except for `ffmpeg`/`ffprobe`, which use a separate `FindFfmpeg`/`FindFfprobe` that *also* falls back to `where.exe` (`:4689`, `:4717`). Result: drop binaries next to `truedat.exe` and they're found; nothing else needs configuration.

## Dependency matrix

| File | Required? | Used by | Missing-file behavior |
|---|---|---|---|
| **`essentia_streaming_extractor_music.exe`** | **HARD** (default scan) | `MoodsMode`, `--file-list`, `--analyze-file`, `--folder` | `Console.Error.WriteLine("Error: essentia_streaming_extractor_music.exe not found")` and the mode aborts. Three abort points: `:1130`, `:1613`, plus the iTunes-XML branch at `:2060`. No fallback — moods cannot be computed without it. |
| **`ffmpeg.exe`** | **SOFT** for default scan / **HARD** for some modes | `ComputeBitUsage`, `ComputeHfAnalysis` (Phase 2.5+3+5 signals), multi-channel downmix retry, `Unsupported codec` retry (e.g. `.opus`), `--transcode`, `--vam-smoke*`, backfill features tier | Lazy-resolved at `:366` (`_ffmpegPath`). On null: every helper that consumes it returns `null` and the worker continues — affected features simply omit from JSON. Status banner prints `not found (multi-channel files will be skipped)`. **Hard fail** in: `--transcode` (`:5732`), `--vam-smoke` (`:5250`), `--vam-smoke-list` (`:5451`), and the backfill features tier prints `WARNING: ffmpeg not found on PATH — features tier will silently skip every entry.` (`:3336`). Phase 4 verdict block downgrades to `"unknown"`/`"n/a"` because Signal F (`hfSpectralStructure`) and `bitUsage` vote weights vanish. **Same omission shape can be requested explicitly** via `--no-bitusage` (drops `ComputeBitUsage` only) or `--no-hf-analysis` (drops `ComputeHfAnalysis` only) — both flags are orthogonal to ffmpeg presence and skip the subprocess regardless. |
| **`ffprobe.exe`** | **SOFT** | `--details` (`mbxhub-details.json`), `--transcode` (source-rate matching, `:5740`) | Lazy at `:367`. Null = `--details` silently skipped with a warning (`:4023`); fingerprint write continues. `--transcode` still works but loses native-rate matching. Default scan does not call ffprobe. |
| **`essentia_streaming_md5.exe`** | **HARD** *only in legacy modes* | `--fingerprint`, `--md5-only` (`:3979`) | If absent, the legacy mode that needs it aborts. **Default scan does not call it** — it's been out of the default codepath since the 2026-05-04 decouple. Per `feedback_hard_cut_no_soft_deprecation` it would normally be deleted, but it's retained for the legacy modes. |
| **`fpcalc.exe`** | **HARD** *only for `--quick-fingerprint`* | `--quick-fingerprint` (`:3815-3818`) | Prints `fpcalc not found. --quick-fingerprint requires fpcalc.exe next to truedat.exe...` and aborts that mode. Otherwise unreferenced. |
| **`onnxruntime.dll`** | **HARD** *only when ORT is touched* | VAM pipeline (`VamPipeline.cs`), VAD stage. Excluded from ILRepack (`ILRepack.targets`) — must sit next to the exe. CPU EP only since 2026-06-09 (DirectML drop); `onnxruntime_providers_shared.dll` is no longer required and was removed from `dist/`. | If absent and `--vam` (opt-in per checkpoint 2026-05-19) or `--vam-smoke*` is invoked, the first ORT P/Invoke throws `DllNotFoundException` and the process exits unhandled. Default scan today does NOT touch ORT (VAM is opt-in), so absence is silent in the normal flow. |
| **`_models/vam-skeleton.onnx`** | Auto-created skeleton | VAM stage (`VocalAffectStage.cs:62`) | If missing, the stage logs `[vam] model file not found at '<path>' — writing development skeleton` and writes a 130-byte synthetic placeholder. **This is dev-only behavior** — produces meaningless outputs until a real licensed model lands. |
| **`_models/silero-vad-skeleton.onnx`** | Auto-created skeleton | VAD stage (`VadStage.cs:70`) | Same skeleton fallback as VAM. |
| **`essentia_standard_chromaprinter.exe`** | Unused at runtime | (shipped in dist; not invoked by `Program.cs`) | No code references — kept in the curated tool collection (`feedback_dist_contents_are_intentional`). |
| **NuGet: `TagLibSharp`, `System.Text.Json`** | Merged into exe via ILRepack | All TagLib reads, all JSON I/O | Not separable at runtime — already inside `truedat.exe`. |

## Source staging (2026-06-09, review-fixed)

Independent of the dependency list above, the default scan path stages remote and non-ASCII source files to `%TEMP%\.truedat-stage\<guid>.<ext>` before fanning out the 8-9 concurrent workers per track. Net effect: 1× full network read per track instead of ~3× full + ≥3× partial. The non-ASCII / 8.3-disabled footgun disappears for free — the staged copy uses a GUID-based ASCII-only filename on a local volume, and the extension is sanitized to `.bin` when the source ext isn't ASCII printable.

Staging triggers for:

- **UNC paths** (`\\server\share\…`) — method `staged`.
- **Mapped network drives** (a drive letter where `DriveInfo.DriveType == Network` — e.g. `Z:\` mapped to `\\server\share`) — method `staged`. Per-root memoized; one `DriveInfo` lookup per unique drive letter, then cached for the rest of the scan.
- **Local non-ASCII paths** — method `staged-fallback`. Covers the case where the existing `TryCreateHardlink` mitigation can't help (hardlinks don't help paths on volumes that don't host hardlinks).
- Local ASCII-only paths on local NTFS pass through directly — no copy.

The cache hierarchy is stage-aware: tier-1 (path + mtime equality, the fast path) does NOT open a staging handle. Tier-2 (path + audioStreamSha256), tier-3 (cross-MD5), tier-4 (cross-SHA), and the cache-miss worker fan-out all route their body reads through one lazily-opened staged copy via `EnsureStagedSrc()` — opened on first body read, reused for every subsequent read, disposed in `finally`. So a tier-1 hit on a UNC library track stays free; a tier-2/3/4 hit or cache miss copies once and amortizes the copy across all 8-9 reads.

`SourceHandle.SourceLastWriteUtc` captures the source's mtime inside `OpenStagedSource` immediately after `File.Copy`. The scan modes record `TrackEntry.LastModified` from this snapshot instead of re-stat'ing at end-of-work, so a tag touch between the copy and the entry being persisted can't make the recorded mtime newer than the analyzed audio.

The per-track concurrent worker fan-out (Essentia + ComputeFileMd5 + ComputeFingerprintV1 + ComputeAudioStreamSha256FromFile + ComputeBitUsage + ComputeHfAnalysis + VAM + SMFM + optional ExtractFileTags) lives in `RunSourceWorkers` — one shared helper called from all three scan paths. `extractTags=false` lets MoodsMode skip TagLib re-read when iTunes XML already supplies metadata; `knownDurationSec > 0` short-circuits the bitUsage TagLib re-parse for the same case.

Flags (`--help` carries the same):

- `--no-stage` — disable staging; workers read source directly (pre-staging behavior).
- `--stage-dir <path>` — override staging dir (default `%TEMP%\.truedat-stage`). Validated writable at startup; per-track stage failures fall back to direct read with a `  Warning:` stderr line.
- `--no-bitusage` — suppress `ComputeBitUsage` (omits the `bitUsage` JSON block).
- `--no-hf-analysis` — suppress `ComputeHfAnalysis` (omits `hfEnergyRatio` + `hfEnergyMethod` + `hfSpectralStructure`).

End-of-scan summary: `EmitStagingSummary` prints `staging: N staged` (or `N staged, M direct-fallback` when fallbacks occurred), and emits a `WARNING: staging degraded` stderr line when more than 5% of attempted stages fell back to direct read — surfaces a wedged stage-dir (no disk space, missing permissions) instead of silently slow scans.

No new dist dependencies; staging is pure-managed `File.Copy`. Orphans from killed scans are swept at next startup by `CleanupOrphanedFiles`. Single-instance only: concurrent truedat invocations sharing a stage-dir would race this sweep. Cross-machine scaling is `--chunk M/N`, not concurrent same-library.

## Failure-mode summary

Three categories:

1. **Default scan blocks** without `essentia_streaming_extractor_music.exe`. This is the only true hard runtime requirement for the primary flow. Everything else is optional.

2. **Quality degrades silently** without `ffmpeg.exe`:
   - No `bitUsage` block (Phase 2.5).
   - No `hfEnergyRatio` / `hfSpectralStructure` (Phase 3+5).
   - Multi-channel files / `.opus` / other unsupported-codec files get skipped instead of retried.
   - Phase 4 verdict downgrades to `"unknown"` for the affected questions because Signals C/F drop out.
   - No `--transcode`, no VAM modes.
   - Backfill `features` tier becomes a no-op (prints warning).

3. **Mode-specific aborts** with clear error messages:
   - `--quick-fingerprint` → needs `fpcalc.exe`.
   - `--fingerprint` / `--md5-only` → need `essentia_streaming_md5.exe`.
   - `--transcode` → needs `ffmpeg.exe` (ffprobe optional).
   - `--details` → silently skipped without `ffprobe.exe`.
   - `--vam` / `--vam-smoke*` → need `ffmpeg.exe` + the two ONNX Runtime DLLs.

## Recommendations

- **Consider preflight banner:** the default-scan modes print one ffmpeg/ffprobe status line on startup, but a user who runs only `--analyze-file` doesn't get a similar advisory before Phase 2.5/3+5 silently no-op. Optional polish, not a defect.
- **VAM exe-side DLL coupling is fragile.** Because `onnxruntime.dll` is ILRepack-excluded by design, an `xcopy dist\truedat\truedat.exe somewhere` deploy will run fine until the first `--vam` invocation, then crash with a Windows loader error. Document this in README "deploying" section if not already.

## Quick reference: dependency on/off matrix for the default scan

| `truedat library.xml` (default scan) | essentia ext. | ffmpeg | ffprobe | Result |
|---|---|---|---|---|
| All present | ✓ | ✓ | ✓ | Full 55-feature set + bitUsage + hfSpectral + verdict |
| ffprobe missing | ✓ | ✓ | ✗ | Same (ffprobe not used here) |
| ffmpeg missing | ✓ | ✗ | — | Core 15 + most extended; no Phase 2.5/3/5 fields, verdict mostly "unknown", multi-channel skipped |
| Essentia missing | ✗ | — | — | Abort with error |
| All present, `--no-bitusage` | ✓ | ✓ | ✓ | Full features minus `bitUsage` block; verdict loses Signal A |
| All present, `--no-hf-analysis` | ✓ | ✓ | ✓ | Full features minus `hfEnergyRatio` + `hfSpectralStructure`; verdict loses Signals B + F |
| All present, `--no-stage` | ✓ | ✓ | ✓ | Same output as "all present"; UNC / mapped-network / non-ASCII-local sources are read directly instead of via local copy |

