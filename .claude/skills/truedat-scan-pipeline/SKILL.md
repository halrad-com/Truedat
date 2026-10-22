---
name: truedat-scan-pipeline
description: The scan pipeline — RunSourceWorkers fan-out, resample-once, source staging, hash-only mode, the cache tiers and re-extract gate, scan health. Read before touching staging, the cache tiers, the fan-out or ClassifyTrackHealth.
---

<!-- Moved verbatim from the root CLAUDE.md on 2026-10-22 (Claude Code doctor, check 4) so it loads only when relevant. The root file keeps the universal rules and a pointer here. -->

## Concurrent hashing in mood analysis

The per-track fan-out lives in **`RunSourceWorkers`** — one shared helper called from all three scan paths (MoodsMode, `--file-list`, `--analyze-file`). It launches Essentia + `ComputeFileMd5` (`--file-md5` only; the task returns null otherwise) + `ComputeFingerprintV1` + `ComputeAudioStreamSha256FromFile` + `ComputeBitUsage` (Phase 2.5) + `ComputeHfAnalysis` (Phase 3+5, produces both `hfEnergyRatio` and `hfSpectralStructure` from one ffmpeg pipe) + SMFM concurrently via `Task.Run` + `Task.WaitAll`, returns a `WorkerResults` bundle. Wall-clock is ~`max(analysis, slowest-task)` per track. The `extractTags` parameter controls whether `ExtractFileTags` runs (false for MoodsMode — iTunes XML supplies metadata; true for `--file-list` / `--analyze-file`). `knownDurationSec > 0` short-circuits the bitUsage TagLib re-parse for callers that already have duration. All workers read from `src.Path` (the staged copy if staging happened, the original otherwise). All managed hashes are pure-managed (no subprocess), no path-escape exposure. The `audioStreamSha256` task hashes the TagLib invariant audio region with SHA-256 — content-stable, portable, fast (SHA-NI hardware accel). `ComputeBitUsage` and `ComputeHfAnalysis` are the subprocess-bearing tasks (each is a single ffmpeg pipe) — both silently return null when ffmpeg.exe is absent, so the worker still completes. Phase 5 consolidated the prior two-ffmpeg `hfEnergyRatio` pipeline into a single ffmpeg pipe feeding a managed-FFT loop, netting −1 subprocess per applicable track vs Phase 3.

## Resample once (hi-res)

A source above 44.1 kHz (mono or stereo, ffmpeg present) is converted **once** to a 44.1 kHz 32-bit float WAV in the stage dir (`ResampleTo44k`, ffmpeg `aresample=resampler=soxr:precision=28`) and the extractor analyses that copy (`AnalyzeWithEssentia`, deleted in `finally`). The extractor analyses at 44.1 kHz anyway but otherwise resamples the file five channel-times with libsamplerate 0.1.8's medium converter — measured 2x slower at 48 kHz, ~3x at 88.2/96, ~5x at 176.4/192. Measured on 22 hi-res files (2026-10-18): no key, scale, BPM or speech-verdict change, `replay_gain`/`length` unchanged, median drift ≤0.3% of the library's interquartile range per consumed field (worst single field on one noisy transfer ~17%); operator: "if we can measure it as inconsequential the faster speed is a win". Two rules that make it safe: **soxr, not ffmpeg's default resampler** (the default flipped a weak tempo 3:2 in the spike; soxr did not), and **32-bit float, not 16-bit** (quantising would itself move the low-level features). **The authenticity checks (`ComputeBitUsage`, `ComputeHfAnalysis`) and the identity hashes still read the ORIGINAL file** — native rate and untouched samples are their whole point — which is why the conversion lives at the extractor call, not in source staging. More than two channels keeps the downmix retry path; below 44.1 kHz is left alone; no ffmpeg means the original goes to the extractor as before. Existing catalog entries are never re-analysed for this (cache hits), so a library holds both kinds of hi-res values until a track is re-analysed for some other reason.

## Source staging

`OpenStagedSource` copies the file once to `%TEMP%\.truedat-stage\<guid>.<ext>` and points every worker at the local copy when the source is on a remote share or has a non-ASCII path: UNC (`\\server\share\…`, method `staged`), mapped network drive (drive letter where `DriveInfo.DriveType == Network`, method `staged`), or local non-ASCII path (method `staged-fallback`). Local-ASCII paths pass through directly. Net: 1× full network read per track instead of ~3× full + ≥3× partial. The GUID name is always ASCII (extension sanitized to `.bin` when the source ext isn't ASCII printable), so the non-ASCII / 8.3-disabled footgun disappears for free. `--no-stage` opts out; `--stage-dir <path>` overrides the staging directory (default `%TEMP%\.truedat-stage`). Steady-state disk footprint = at most `parallel_worker_count` staged files (per-track `using var src` / `try/finally` cleans up); orphans from killed scans are swept at the next startup by `CleanupOrphanedFiles`. Single-instance is still the design, but it is now *enforced at the sweep* rather than merely asserted (I-3): `ShouldSweepOrphans` refuses the whole sweep when `CountSiblingTruedatProcesses() > 0` (warning to stderr), and `IsSweepableOrphan` additionally spares any file touched within `OrphanMinAge` (60 min) — two guards because each covers the other's blind spot (blocked process enumeration; a sibling on another machine sharing a stage dir). `--stage-dir` now isolates **two** of the three sweep targets: staged copies and the multi-channel downmix WAVs (`DownmixToStereo` writes into `_stageOpts.StageDir`, not `%TEMP%` — the old hard-coded `%TEMP%` meant a second run deleted a live run's in-flight downmix and the failure landed in `mbxmoods-errors.csv` looking like a corrupt track). The third, `<drive>\.truedat-tmp` hardlinks, is **not relocatable** — a hardlink cannot cross volumes — so it depends on those two guards; the `TryCreateHardlink` *fallback* candidate does follow `--stage-dir`. Cross-machine scaling is `--chunk M/N`, not concurrent same-library. The original source path stays the cache key, JSON output path, and errors-CSV identifier — the GUID staging name never leaks out of the worker fan-out.

Mapped-drive detection is per-root memoized in `_networkDriveCache` (ConcurrentDictionary, FNV root → bool, failures cached as false). `SourceHandle.SourceLastWriteUtc` snapshots the source mtime inside `OpenStagedSource` immediately after `File.Copy` — the scan modes record `TrackEntry.LastModified` from this snapshot so a tag touch between copy and persist can't make the recorded mtime newer than the analyzed audio. End-of-scan `EmitStagingSummary` prints `staging: N staged [, M direct-fallback]` and warns to stderr when >5% of attempted stages fell back to direct read (wedged stage-dir surfaces visibly instead of silently slow).

## Hash-only mode (offline NDJSON manifest)

`--hash-only --level fingerprint|stream --file-list <paths.txt> --output <manifest.ndjson>` runs identity-only passes without Essentia and appends one envelope per file to an NDJSON manifest. Used by the determinism rig at `tools/verify-audiosha-determinism.ps1`.

Envelope shape is defined by `BuildIdentityOnlyEnvelope` in `Program.cs`. It has no external consumers — when the rig and the format need to evolve, evolve them together.

## Cache hierarchy & re-extract gate

All four scan modes — `MoodsMode` (default iTunes-XML), `--folder`, `--file-list`, `--analyze-file` — check the cache in this order before falling through to Essentia:

1. **Path + mtime** (`allTracks[t.Location]`, `TruncateToSeconds` equality) — the fastest path. Returns `(cached)` / `[CACHED]`. **Does NOT open a staging handle** — no body read happens, so a UNC tier-1 hit is free.
1.5. **Path + head-64k quick tier** (`(cached·head)` / `[CACHED·head]`, default ON, `--no-quick-cache` opts out) — when path matches but mtime drifted, recompute `fingerprint.v1` (TagLib parse + 64 KB head hash, ~1 ms, on the **original** path — no staging, which would copy the whole file). `IsTagsOnlyChange` accepts when the fresh and stored fingerprints agree on `audioHead64kMd5` (measured from `InvariantStartPosition`, so a tag write can't move it) plus codec/sampleRate/channels/bitDepth and durationMs within 500 ms. On a hit: reuse Essentia features, refresh `fingerprint.v1`, and **clear `fileMd5`** (the whole-file MD5 is stale after a tag write and recomputing it needs the full read this tier exists to avoid — `--verify --backfill --backfill-level identity --file-md5` refills it). Any mismatch falls through to tier 2. This is the fast path for a post-retag rescan: ~64 KB/track instead of a full audio-hash read. Residual risk: an in-place audio edit past the first 64 KB that preserves duration and codec props slips through — deliberate tampering, which `--verify` (full SHA) still catches; the durable `audioStreamSha256` in the JSON is untouched.
2. **Path + audioStreamSha256** (`(cached·sha)` / `[CACHED·sha]`) — when path matches but mtime drifted and the quick tier didn't fire (or `--no-quick-cache`), recompute `audioStreamSha256` (SHA256Cng / SHA-NI). If it matches the cached value, audio bytes are unchanged (just tags). Reuse Essentia features, refresh `lastModified` + `fileMd5` + `fingerprint.v1` (the tag-affected fields).
4. **Cross-SHA** (`moodShaIndex`, `(cached·sha)` / `[CACHED·sha]`) — different path, invariant audio region matches (covers clean moves, copies with preserved mtimes, and moved-plus-tag-edited files alike). Re-keys old → new path; recomputes `fileMd5` + `fingerprint.v1`. (The former tier-3 cross-MD5 was removed 2026-07-11 (v5.4.0) — cross-SHA catches a strict superset; the "4" name is kept so hit tags and history stay legible.)

Tiers 2/4 and the cache-miss worker fan-out all route body reads through one lazily-opened staged copy via `EnsureStagedSrc()` — opened on first body read, reused for every subsequent read in the tier walk and the workers, disposed in `finally`. Within a walk the whole-file MD5 and invariant-region SHA-256 are computed in a **single read pass** (`ComputeFileMd5AndAudioSha`, memoized per track as `EnsureBodyHashes()`): tier-2's refreshed `fileMd5` and tier-4's hashes come from that one pass — worst-case tier fall-through is one full read. So a tier-2/4 hit on a UNC track copies once and amortizes that copy across the tier's hashes + the worker fan-out, instead of re-reading the same source N times across the network. `audioStreamSha256` uses `SHA256Cng` (SHA-NI hardware accel) — `SHA256.Create()` resolves to the managed implementation on net48 (no SHA-NI, ~5-10× slower).

`MoodsMode` uses `RebuildCacheEntry` (takes an `ITunesTrack` for metadata); `--folder` / `--file-list` / `--analyze-file` use `RebuildCacheEntryFromTags` (takes TagLib-derived metadata). Both delegate to `RebuildCacheEntryCore` — single source of truth for the 55-feature copy. Adding a new `TrackFeatures` field means updating one helper, not 7 inline call sites.

The plugin-driven autoscan workflow (MBXHub plugin pipes new file paths to `truedat --file-list -` or `truedat --folder ... --moods ...` after MusicBee detects new files) depends on these tiers — without them, every poll re-runs Essentia on the entire library.

Re-extract (cache miss → full Essentia) when any of these are missing:

- `DynamicRange` — pre-LRA builds.
- `LoudnessMomentary` — pre-extended-feature builds (canary for the 40-extended set).

Under `--refresh-features` (opt-in) the gate additionally treats an entry as stale when it
lacks a later wave's marker: `averageLoudness` (2026-07-22 tonal wave) or `dynamicComplexity`
(2026-08-09 harmonic / capture-once wave). Both are markers because the extractor emits them
for any decodable track; `hpcp12` is deliberately **not** a marker (null on silence would
re-analyze that track every pass). A normal scan never re-analyzes for these — only
`--refresh-features` does — which is why one refresh pass populates the whole capture-once set.

The cache tiers ask **`HasCurrentEntry`** (per entry), not `HasCurrentFeatures` (per feature set):
under `--refresh` with a store and edges in force, an entry whose `audioStreamSha256` is on the store's
lacking-edges list (`StoreDal.ContentKeysLackingEdges`, loaded once per scan by `LoadEdgesStale`) is
stale too, so edge backfill is the scan's own cache-miss path — same workers, progress, ETA and saves —
never a second pipeline (operator, 2026-10-12, after a serial drain showed 8 days at 15% CPU). A
recorded failed fold is off the list unless `--retry-errors` (no re-analysis loop), and an entry with no
sha is never edge-stale. The store flushes at the periodic catalog save (`final: false`, which leaves
in-flight folds alone) as well as at the end.

When adding a new always-extracted field, decide whether to add it to the canary. Avoid canary conditions that depend on optional external tools — those caused infinite re-extraction loops on installs without the tool, which is why the old `audioMd5`-presence canary was removed when the binary left the default codepath.

## Scan health (pass/fail, 2026-07-30)

**No silent failures.** After the Essentia gate, every analyzed track passes through
`ClassifyTrackHealth` (pure, self-tested): a track **fails** when any component that
should have produced a value came back empty — fingerprint / `audioStreamSha256`
(TagLib refused the file), tags (per-file modes only; `DurationMs == 0` is the refusal
signature since `ExtractFileTags` swallows into an empty object), or an **applicable**
bitUsage / HF signal — or when Essentia's decoded length (`metadata.audio_properties.length`,
read into the **transient, never-persisted** `TrackFeatures.AnalyzedLengthSec`) covers
< 95% of the claimed duration (truncated file: features describe a fraction of the track).
On fail: row in `mbxmoods-errors.csv` with the `FailedComponents` column
(`decode-27%;fingerprint;sha`), **no catalog entry written** (an identity-less phantom is
what MBXHub can't index and re-queues forever), and the existing error-list skip stops
re-analysis (`--retry-errors` re-attempts). The applicability rule is what keeps the fail
list meaningful: `ComputeBitUsage`/`ComputeHfAnalysis` report `notApplicable` (no ffmpeg,
lossy/sub-24-bit codec, ≤ 44.1 kHz, **pure-silence window** — a content limitation) and
those absences never fail a track; `fileMd5` is never considered; SMFM has its own report.
Do not "simplify" the gate to plain null-checks — the tri-state exists because a silent
30 s window on a healthy 24-bit FLAC would otherwise cost the file its entry.

Throughput reporting is duration-normalized (operator ruling): progress line + summary
lead with `Nx realtime` (audio-seconds per wall-second; numerator = decoded length), MB/s
is the IO footnote. The ETA model was already RTF-based; only the display rate changed.
