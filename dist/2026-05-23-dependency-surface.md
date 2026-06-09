# Truedat dependency surface — what's needed, what breaks if missing

Date: 2026-05-23 (updated 2026-06-09 for source staging + signal opt-outs)
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
| **`onnxruntime.dll`** + **`onnxruntime_providers_shared.dll`** | **HARD** *only when ORT is touched* | VAM pipeline (`VamPipeline.cs`), VAD stage. Excluded from ILRepack (`ILRepack.targets:39-40, 73-74`) — must sit next to the exe. | If absent and `--vam` (opt-in per checkpoint 2026-05-19) or `--vam-smoke*` is invoked, the first ORT P/Invoke throws `DllNotFoundException` and the process exits unhandled. Default scan today does NOT touch ORT (VAM is opt-in), so absence is silent in the normal flow. |
| **`DirectML.dll`** | **SOFT** for VAM | DirectML EP path inside ORT | Only loaded if a session opts for the DML EP. CPU EP still works. Absence narrows VAM EP options but doesn't crash CPU-only inference. |
| **`_models/vam-skeleton.onnx`** | Auto-created skeleton | VAM stage (`VocalAffectStage.cs:62`) | If missing, the stage logs `[vam] model file not found at '<path>' — writing development skeleton` and writes a 130-byte synthetic placeholder. **This is dev-only behavior** — produces meaningless outputs until a real licensed model lands (see `docs/plans/2026-05-19-vam-roadmap.md`). Will be replaced by the V1 license-audit GO model. |
| **`_models/silero-vad-skeleton.onnx`** | Auto-created skeleton | VAD stage (`VadStage.cs:70`) | Same skeleton fallback as VAM. |
| **`essentia_standard_chromaprinter.exe`** | Unused at runtime | (shipped in dist; not invoked by `Program.cs`) | Dead weight in `dist/`. Grep shows zero `FindTool`/`RunTool` references. Candidate for removal. |
| **`ffplay.exe`** | Unused at runtime | (shipped in dist) | Same — no code references. Candidate for removal. |
| **NuGet: `TagLibSharp`, `System.Text.Json`** | Merged into exe via ILRepack | All TagLib reads, all JSON I/O | Not separable at runtime — already inside `truedat.exe`. |

## Source staging (2026-06-09)

Independent of the dependency list above, the default scan path now stages UNC-sourced files (and local non-NTFS / hardlink-fallback cases) to `%TEMP%\.truedat-stage\<guid>.<ext>` before fanning out the 8-9 concurrent workers per track. Net effect: 1× full network read per track instead of ~3× full + ≥3× partial. The non-ASCII / 8.3-disabled footgun on UNC sources disappears for free because the staged copy uses a GUID-based ASCII-only filename on a local volume.

Flags (`--help` carries the same):

- `--no-stage` — disable staging; workers read source directly (today's pre-staging behavior).
- `--stage-dir <path>` — override staging dir (default `%TEMP%\.truedat-stage`). Validated writable at startup; per-track stage failures fall back to direct read with a `  Warning:` stderr line.
- `--no-bitusage` — suppress `ComputeBitUsage` (omits the `bitUsage` JSON block).
- `--no-hf-analysis` — suppress `ComputeHfAnalysis` (omits `hfEnergyRatio` + `hfEnergyMethod` + `hfSpectralStructure`).

No new dist dependencies; staging is pure-managed `File.Copy`. Orphans from killed scans are swept at next startup by `CleanupOrphanedFiles`.

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

- **Confirm safe to remove:** `essentia_standard_chromaprinter.exe` and `ffplay.exe` from `dist/truedat/`. Zero code references; they only inflate the distribution. (Note: do not delete without user confirm — they may be there intentionally as future-options.)
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
| All present, `--no-stage` | ✓ | ✓ | ✓ | Same output as "all present"; UNC sources are read directly from network instead of via local copy |

