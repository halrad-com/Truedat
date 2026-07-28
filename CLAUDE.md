# Truedat — LLM Session Context

This file is loaded automatically by Claude Code / Claude Agent sessions scoped to this repo. Keep it short and decision-making-focused; deep reference material belongs in `README.md` and `SBOM.md`. Internal design notes and session journals live in the local `docs/` directory, which is gitignored — never reference them from any file that ships in the repo.

## What this project is

Truedat is a Windows .NET command-line tool that analyses a music library's audio and writes `mbxmoods.json` (mood + Essentia features + identity + authenticity). The output file is consumed by MBXHub's AutoQ engine (separate repo).

- Build: `build-truedat.cmd` (requires .NET SDK 8.0+). Single-file output at `dist/truedat/truedat.exe` (~1 MB, ILRepack-merged).
- Runtime deps: `essentia_streaming_extractor_music.exe` (required for default scan), plus optional `ffmpeg.exe` (multi-channel downmix, `Unsupported codec` retry — e.g. `.opus`, since this essentia build lacks libopus — the bitUsage/HF authenticity signals, and the standalone `--transcode` utility) and `ffprobe.exe` (matching source rate/depth in `--transcode`). The legacy fingerprint pipeline (`--fingerprint` / `--md5-only` / `--quick-fingerprint` / `--details` and the `essentia_streaming_md5.exe` / `fpcalc.exe` binaries) was removed 2026-07-11 (v5.4.0) — identity is pure-managed.
- Framework: **.NET Framework 4.8**. No .NET 6/8 APIs, no `ValueTask`, no `init` setters on public types. Use `System.Text.Json` (merged via ILRepack).

## mbxmoods.json schema (current)

Per-track JSON object under `"tracks"[path]`. Feature fields fall into **15 core** (always present) + **40 extended** + **15 tonal/rhythm** + the authenticity blocks; extended and later fields are nullable — missing means the key is omitted, not written as `null` — plus metadata/identity fields. README's *Feature set* section carries the exact counts and how to re-measure them. **Do not maintain a total by hand here:** this line read "55 numeric feature fields" for months after the 2026-07-22 tonal/rhythm wave added 15 more, because a hand-kept number has nothing to check it and reads as authoritative while being wrong. What matters for decisions is the structure below — which fields are always present, which are nullable, and which are Essentia-derived and therefore neither backfillable nor in the re-extract canary.

- **Core features** (always present): `bpm`, `key`, `mode`, `spectralCentroid`, `spectralFlux`, `loudness`, `danceability`, `onsetRate`, `zeroCrossingRate`, `spectralRms`, `spectralFlatness`, `dissonance`, `pitchSalience`, `chordsChangesRate`, `mfcc[]`.
- **Tonal/rhythm extension wave (2026-07-22, nullable, omit-when-missing)**: `keyVotes` (nested block — all three key profiles `krumhansl`/`temperley`/`edma`, each `{key, scale, strength}`; the flat `key`/`mode` come from edma), `bpmFirstPeak`/`bpmFirstPeakWeight`/`bpmSecondPeak`/`bpmSecondPeakWeight`/`bpmSecondPeakSpread` (tempo-histogram peaks — half/double-time evidence), `chordsKey`/`chordsScale`/`chordsHistogram[24]`/`chordsNumberRate`, `tuningFrequency` + 3 tuning-temperament scalars, `averageLoudness`. **Essentia-derived → NOT backfillable and NOT in the re-extract canary**: fresh analyses populate them, legacy entries simply lack them until a deliberate re-analysis. Primary consumer: AutoQ Camelot/harmonic mixing (key confidence + agreement) and tempo matching.
- **Extended** (nullable, omit-when-missing): `dynamicRange` + `dynamicRangeSource`; loudness envelope (`loudnessMomentary`, `loudnessShortTerm`, `replayGain`); silence (`silenceRate20dB/30dB/60dB`); spectral shape (rolloff, complexity, entropy, kurtosis, skewness, spread, strongPeak, decrease, energy + 4 energybands); `hfc`; Bark/ERB/Mel band stats (crest, flatness, kurtosis, skewness, spread × 3 scales = 15); rhythm/tonal (`beatsLoudness`, `chordsStrength`, `hpcpCrest`, `hpcpEntropy`); Phase 2.5 `bitUsage` block (`lowestNonZeroBit`, `bottomBitActivity`, `effectiveBits`, `samplesAnalyzed`, `method`) — populated during fresh analysis via `ComputeBitUsage` (ffmpeg → s32le mono 30 s mid-track → trailing-zeros histogram). Null on legacy entries, ffmpeg-absent installs, and silent / undecodable files. **Detection-only valid for codec ∈ {flac, alac, wav, aiff} claiming `bitDepth ≥ 24`** — lossy decode floods the LSBs with reconstruction noise so the metric is meaningless for MP3/AAC/etc. Phase 3+5 `hfEnergyRatio` + `hfEnergyMethod` and `hfSpectralStructure` block — derived from a **single** ffmpeg-piped managed-FFT pass (`ComputeHfAnalysis`) over 30 s mid-track at the source's native sample rate. `hfEnergyRatio` is the fraction of audio energy above 22.05 kHz (Parseval-style energy-weighted across windows). `hfSpectralStructure` carries three FFT-derived discriminators: `flatness` (Wiener entropy of HF bins, range [0,1] — broadband = real, peaky = imaging artifact), `peakToMean` (max/mean HF mag², ratio — catches narrow spikes), `imagingSymmetry` (Pearson r of HF bins with their mirror about the original 22.05 kHz Nyquist, range [-1,1] — currently unused in the verdict; Lanczos suppression made it useless on corpus-1, kept for future tuning). Only populated when `sourceSampleRate > 44100` — no Nyquist headroom otherwise. Method tag `managed-fft-radix2-30s-mid-native`. Together with `bitUsage`, these are the independent fake-hi-res signals that dither evasion can't fabricate.
- **Identity** (nullable, omit-when-missing): `fileMd5` (whole-file MD5 — written only under `--file-md5` since 2026-07-11; without the flag, scans never write it (fan-out skips it, tier-2/4 refreshes drop it) and `--migrate` strips stored values; nothing consumes it — MBXHub indexes `audioStreamSha256` only), `fingerprint.v1` (composite, written via `WriteFingerprintV1` — fields: `fileSize`, `pathTail`, `durationMs`, `sampleRate`, `channels`, `bitDepth` (omit-when-0), `codec`, `codecRaw`, `bitrate`, `encoder` (omit-when-empty), `encoderRaw` (omit-when-empty), `mp3LameTag` (Phase 2 nested object, populated when codec=mp3 and the file has a Xing/Info+LAME header — fields: `version`, `vbrMethodCode`, `vbrMethod`, `lowpassHz`, `encoderDelay`, `encoderPadding`, `musicCrc`, `infoTagRevision`), `audioHead64kMd5` + source flag), `audioStreamSha256` (ride-along in default Essentia mode via `ComputeAudioStreamSha256FromFile`; emitted in all three scan-path modes — MoodsMode, `--file-list`, `--analyze-file` — plus `--hash-only --level stream`). **FLAC hashes are frame-anchored since 2026-07-21** (`audioStreamSha256Source: "flac-frames"`, `GetFlacAudioStart` walks the metadata-block chain): TagLibSharp's invariant region for FLAC *includes* the metadata blocks, so a Vorbis-comment write (MBXHub's embedded mood field, any retag) drifted the old hashes even with byte-identical frames. The cache tiers and `--verify` compute the legacy TagLib-region sha in the same read pass and match stored pre-transition values against it, upgrading entries in place (`audioStreamSha256-upgraded`) with zero re-analysis; FLACs retagged *before* migrating can't be matched either way — `--verify --backfill --accept-flac-tag-drift` re-keys them when audio props still agree with the stored fingerprint. `fingerprint.v1.audioHead64kMd5` uses the same frame anchor for FLAC (`audioHead64kMd5Source: "flac-frames"`); `IsTagsOnlyChange` requires matching sources.
- **Housekeeping**: `lastModified`, `analysisDuration`.
- **Sony SMFM (12-TONE)** (nullable, omit-when-missing; read fresh every scan via `SmfmReader`, never cached): `smfmScores` (raw STMO slot scores, `int[10]`, 0-255), `smfmChannel` (dominant raw STMO slot index = argmax — **NOT a mood channel**), `smfmChannelName` (**always null** — the slot→name labels were **device-refuted** on 2026-06-27: SMFM slots are not 1:1 with Sony's mood channels, which are 2-D arousal×valence regions; kept as a back-compat property), `smfmBpm` (Sony GBPM). **Renamed from the former `sensme*` keys on 2026-06-28** — the read path keeps a `sensme*` old-key fallback for un-migrated libraries, and `--migrate` rewrites existing files `sensme*→smfm*` (with backup). The interpreted `(arousal, valence)` signal is derived **downstream** (MBXHub/AutoQ), not in truedat. Format docs + extraction tooling: `smfm-tools/` (public, curated from local research). `--list-missing-smfm [path]` (read-only) lists entries with no SMFM block plus coverage → `mbxmoods-smfm-missing.csv`; it is deliberately **not** in the `--stats` Recommended block, because truedat only reads SMFM and never writes it — no truedat command closes that gap, and the advisor must only name gaps a command can fix (same rule as the d146a04 reachability fix).
- **Phase 4 verdict** (nested `truedat` object): `hiresGenuine` + `hiresConfidence`, `lossyTranscodeLikely` + `lossyTranscodeConfidence`, `method`. Computed inline at write time by `ComputeTruedatVerdict` from the already-extracted features; **not persisted in TrackEntry / cache** so threshold changes ship without rescan. Four-string enum (`"yes"` / `"no"` / `"unknown"` / `"n/a"`) — explicitly NOT a bool, because collapsing "unknown" into yes/no is what produces the false-positive/negative failures the verdict block was designed to avoid (see BACKLOG "Known detection challenges"). Block omitted entirely when none of `hiresGenuine`/`lossyTranscodeLikely`/`speechLikely` reached a decided yes/no (legacy entries without fingerprint.v1, weird-codec files, or entries lacking enough signal). Method tag identifies the algorithm/threshold generation — currently `truedat-v1-fft-corpus1-2026-05-18` (corpus-1-calibrated), bumps again when a broader labeled corpus retunes the thresholds (Phase 4 corpus is the gating step in BACKLOG before the verdict can be considered tuned). Per-signal vote+weight trace emitted to stderr under `--audit`. **`speechLikely` (2026-07-22)** — same block, own fields (`speechLikely` + `speechConfidence` + `speechMethod`, tag `truedat-speech-v1.2-untuned-2026-07-22`): talk-vs-music classification computed at write time from stored features (danceability, chordsStrength, silenceRate30dB, zeroCrossingRate, bpmFirstPeakWeight, keyVotes strength). Write-time + not-persisted means it's retroactive across the whole catalog on the next save (any scan, cache hit or miss) with no rescan and no backfill tier needed. `"yes"` additionally gates on the zero-crossing signal firing so tone/ambient beds (which share talk's shape on the other signals) demote to `"unknown"` instead of a false `"yes"`. **This gate was originally load-bearing because `--migrate` pruned the catalog entry on `"yes"` — that purge was removed 2026-07-25 (Heuristics → Evidence Phase 3/4, `--migrate`'s speech purge deleted in `4b0c4fa`): `--migrate` now strips legacy fields only (`valence`/`arousal`, `audioMd5`, `chromaprint`, `chromaprintDuration`; `fileMd5` unless `--file-md5`; renames `sensme*`→`smfm*`) and never removes an entry on any verdict — speech-labelled, speech-likely, or otherwise.** `RecomputeSpeechLikely` is the JSON-facing twin of `ComputeTruedatVerdict`'s speech block, for a caller that only has a raw JSON node rather than a `TrackEntry`; as of 2026-07-25 it has **zero production callers** — its only caller was `--migrate`'s speech purge, and that purge is gone. It is kept, pinned by the self-test `list-speech recompute matches the TrackEntry path` (renamed 2026-07-25 from `migrate speech recompute matches the TrackEntry path` when its target moved), as the drift guard for any future JSON-only caller: `RecomputeSpeechLikely` reads exactly the verdict's input signals, so extend it whenever that list changes. **Rev-1.2 adds a second gate: `"yes"` also requires `danceability < 0.50`.** Sparse / live / free-form *instrumental* music craters on the whole rest of the panel exactly like speech (no tempo peak, weak chords, weak key, high silence, high zcr) — danceability is the one signal that separates them: measured 2026-07-22, genuine speech sits at 0.00 while real-music false positives ran 0.66–1.10. Without this gate the (now-removed) `--migrate` purge would have pruned Charlie Parker "Hot House", NIN "Burn" (live) and Travis "Outro". The 0.50 cut is deliberately tighter than the danceability signal's own talk-vote threshold (0.70) because the 0.50–0.70 band held real music (Beatles "Sgt. Pepper Effects Tape 2" 0.657, "For Lovers Only (Reprise)" 0.698) — the gate's margin costs no real recall against genuine speech (0.00) even though it no longer guards a destructive action. `--list-speech [path]` is the read-only review surface for the full `speechLikely == "yes"` set (`RunListSpeech`) — it lists **every** speech-yes entry regardless of genre label. Since 2026-07-28 (`4d8a60c`) `--stats` reports two DISJOINT lines instead of the old union count: `Speech (acoustic)` = recomputed `speechLikely == "yes"` and is **count-equal with `mbxmoods-speech.csv` by construction** (the live 1-vs-0 advisor mismatch this fixed), and `Speech (genre label)` = stored `genre == "Podcast"` but NOT acoustic-yes — invisible to `--list-speech`, so its advisor points at a genre rule (`kind:genre, value:Podcast`) instead. Each line only points at a surface that can show what it counted (the d146a04 advisor rule). Review the CSV, then add a rule to a decisions delta and run `--apply-exclusions`; the verdict is still `-untuned` (real ambient/spoken-intro music can reach `"yes"`).

Four I/O surfaces must stay in sync: `AnalyzeWithEssentiaCore` (extract), `WriteTrackEntry` (write), `LoadExistingMoods` (read via `ParseTrackFeaturesFromJson`), and `RebuildCacheEntryCore` (cache-reuse copy). Adding a field means touching all four. Legacy `audioMd5` / `chromaprint` / `chromaprintDuration` keys in old files are ignored on read and stripped by `--migrate`.

## Rounding convention (extended features)

`Opt(v, int dp = 4)` / `OptN(root, path, int dp = 4)` — default 4 dp. Overrides:

- 2 dp: dB/LU values (`loudnessMomentary`, `loudnessShortTerm`, `replayGain`, `beatsLoudness`, `hpcpCrest`, `hfc`), `spectralComplexity`.
- 1 dp: Hz (`spectralRolloff`).
- 6 dp: tiny spectral values (`spectralDecrease`, `spectralEnergy*`).

Don't regress to a uniform 6 dp — it inflates JSON size without analytic value.

## Concurrent hashing in mood analysis

The per-track fan-out lives in **`RunSourceWorkers`** — one shared helper called from all three scan paths (MoodsMode, `--file-list`, `--analyze-file`). It launches Essentia + `ComputeFileMd5` (`--file-md5` only; the task returns null otherwise) + `ComputeFingerprintV1` + `ComputeAudioStreamSha256FromFile` + `ComputeBitUsage` (Phase 2.5) + `ComputeHfAnalysis` (Phase 3+5, produces both `hfEnergyRatio` and `hfSpectralStructure` from one ffmpeg pipe) + SMFM concurrently via `Task.Run` + `Task.WaitAll`, returns a `WorkerResults` bundle. Wall-clock is ~`max(analysis, slowest-task)` per track. The `extractTags` parameter controls whether `ExtractFileTags` runs (false for MoodsMode — iTunes XML supplies metadata; true for `--file-list` / `--analyze-file`). `knownDurationSec > 0` short-circuits the bitUsage TagLib re-parse for callers that already have duration. All workers read from `src.Path` (the staged copy if staging happened, the original otherwise). All managed hashes are pure-managed (no subprocess), no path-escape exposure. The `audioStreamSha256` task hashes the TagLib invariant audio region with SHA-256 — content-stable, portable, fast (SHA-NI hardware accel). `ComputeBitUsage` and `ComputeHfAnalysis` are the subprocess-bearing tasks (each is a single ffmpeg pipe) — both silently return null when ffmpeg.exe is absent, so the worker still completes. Phase 5 consolidated the prior two-ffmpeg `hfEnergyRatio` pipeline into a single ffmpeg pipe feeding a managed-FFT loop, netting −1 subprocess per applicable track vs Phase 3.

## Source staging

`OpenStagedSource` copies the file once to `%TEMP%\.truedat-stage\<guid>.<ext>` and points every worker at the local copy when the source is on a remote share or has a non-ASCII path: UNC (`\\server\share\…`, method `staged`), mapped network drive (drive letter where `DriveInfo.DriveType == Network`, method `staged`), or local non-ASCII path (method `staged-fallback`). Local-ASCII paths pass through directly. Net: 1× full network read per track instead of ~3× full + ≥3× partial. The GUID name is always ASCII (extension sanitized to `.bin` when the source ext isn't ASCII printable), so the non-ASCII / 8.3-disabled footgun disappears for free. `--no-stage` opts out; `--stage-dir <path>` overrides the staging directory (default `%TEMP%\.truedat-stage`). Steady-state disk footprint = at most `parallel_worker_count` staged files (per-track `using var src` / `try/finally` cleans up); orphans from killed scans are swept at the next startup by `CleanupOrphanedFiles`. Single-instance is still the design, but it is now *enforced at the sweep* rather than merely asserted (I-3): `ShouldSweepOrphans` refuses the whole sweep when `CountSiblingTruedatProcesses() > 0` (warning to stderr), and `IsSweepableOrphan` additionally spares any file touched within `OrphanMinAge` (60 min) — two guards because each covers the other's blind spot (blocked process enumeration; a sibling on another machine sharing a stage dir). `--stage-dir` now isolates **two** of the three sweep targets: staged copies and the multi-channel downmix WAVs (`DownmixToStereo` writes into `_stageOpts.StageDir`, not `%TEMP%` — the old hard-coded `%TEMP%` meant a second run deleted a live run's in-flight downmix and the failure landed in `mbxmoods-errors.csv` looking like a corrupt track). The third, `<drive>\.truedat-tmp` hardlinks, is **not relocatable** — a hardlink cannot cross volumes — so it depends on those two guards; the `TryCreateHardlink` *fallback* candidate does follow `--stage-dir`. Cross-machine scaling is `--chunk M/N`, not concurrent same-library. The original source path stays the cache key, JSON output path, and errors-CSV identifier — the GUID staging name never leaks out of the worker fan-out.

Mapped-drive detection is per-root memoized in `_networkDriveCache` (ConcurrentDictionary, FNV root → bool, failures cached as false). `SourceHandle.SourceLastWriteUtc` snapshots the source mtime inside `OpenStagedSource` immediately after `File.Copy` — the scan modes record `TrackEntry.LastModified` from this snapshot so a tag touch between copy and persist can't make the recorded mtime newer than the analyzed audio. End-of-scan `EmitStagingSummary` prints `staging: N staged [, M direct-fallback]` and warns to stderr when >5% of attempted stages fell back to direct read (wedged stage-dir surfaces visibly instead of silently slow).

## Hash-only mode (offline NDJSON manifest)

`--hash-only --level fingerprint|stream --file-list <paths.txt> --output <manifest.ndjson>` runs identity-only passes without Essentia and appends one envelope per file to an NDJSON manifest. Used by the determinism rig at `tools/verify-audiosha-determinism.ps1`.

- `fingerprint`: TagLib parse + 64 KB MD5 at `InvariantStartPosition`. Sub-10 ms warm. Envelope carries the `fingerprint.v1` composite (pathTail + fileSize + audio props + audioHead64kMd5).
- `stream`: streaming SHA-256 over `[InvariantStartPosition, InvariantEndPosition)`. Disk-bound. Superset — envelope carries `fingerprint.v1` *and* `audioStreamSha256`.

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

When adding a new always-extracted field, decide whether to add it to the canary. Avoid canary conditions that depend on optional external tools — those caused infinite re-extraction loops on installs without the tool, which is why the old `audioMd5`-presence canary was removed when the binary left the default codepath.

## Scan exclusions

`mbxmoods-exclude.json` beside the moods file is the **only** authority over what a scan
skips for policy reasons (`ExclusionSet` parses and matches; `ExclusionStore` reads, writes
and merges). Three rule kinds — `folder` (pattern must end `**`; a leading-separator
fragment matches under any root, which is what makes rules portable across mirrored
libraries), `genre` (exact, case-insensitive, trimmed — deliberately **not** substring) and
`file` — each `exclude` or `include`, with **`include` always winning** regardless of order.

A `file` rule's optional `audioStreamSha256` is **report-only, never matching** (I-2, 2026-07-26).
Matching stays exact normalized path equality; the sha exists so that when the path is gone,
`PreviewPlanner.BuildRuleStats` can report `PreviewRuleStat.State` as `moved-or-deleted` and name
the catalog paths still holding that content in `Candidates` (emitted as `state` / `candidates[]`
in `preview.json`, rendered by `PreviewRuleNote` on the console and `ruleNote()` in the page).
Absent-from-disk but still-in-catalog is a third state, `path-unreachable` — the metadata-mirror
case; calling it "moved" would condemn every live rule on a mirror box, which is the same
inversion C-1 fixed. Do **not** make the sha match: one `audioStreamSha256` covers every copy of
the audio (it is what `--duplicates` groups on), so re-matching would silently widen a rule the
operator wrote narrowly. Candidate resolution deliberately does **not** use `BuildHashIndex` —
that index is first-wins on duplicate shas, so it would name an arbitrary copy; `BuildRuleStats`
scans the catalog once and lists all matches, sorted.

Layering is two-tier, and no heuristic sits in it any more: structural skips (missing /
video / stream URL / non-audio / DSD / over `--max-duration`) are "cannot analyze" and ignore
`include`; everything else is policy, decided solely by `ExclusionSet.IsExcluded` —
`include` > `exclude` > keep. A speech label, a genre string, an embedded file marker: none
of it filters anything by itself any more. The only way a track is kept out of analysis for
a policy reason is a rule the operator wrote into `mbxmoods-exclude.json`.

A **missing** file excludes nothing (not an error). A **present but unparseable** file makes
truedat exit 1 rather than scan — analyzing everything while the operator believes rules are
in force is the silent failure the whole design exists to remove. Invalid individual rules are
skipped, counted and diagnosed (tolerant-reader convention). Exclusions never prune existing
catalog entries. Every skip is ledgered in `mbxmoods-skipped.csv` on all scan modes; the
iTunes-XML library scan additionally prints per-rule hit counts so stale rules surface (the
per-file modes have nothing to count, but still print a warning per ignored/invalid rule
diagnostic). `--exclusions`
overrides the location, `--no-exclusions` bypasses for one run, `--apply-exclusions <delta>`
merges (with backup) and is the **single** implementation of the merge semantics — MBXHub is
expected to invoke it rather than write the file itself.

The merge is the one write in the system over an artefact that **cannot be regenerated** — it is
pure operator judgement — so it gets the strongest discipline: `ExclusionStore.Write` stages to
`mbxmoods-exclude.json.tmp` in the **same directory** (`TempWritePath`; `File.Replace` is
same-volume) and swaps via `Program.AtomicReplace`, matching `SaveResults` / `--migrate` /
`--merge-moods`; the tmp is deleted on failure so a half-written sibling never sits beside the
policy file. `Merge`'s whole load-modify-write runs under `AcquireWriteLock` — a zero-byte
`mbxmoods-exclude.json.lock` sidecar opened `FileShare.None`, 10 × 100 ms then refuse. The lock
file is **deliberately never deleted** (deleting it would let a third process create a fresh one
while a second still held the old handle, and both would believe they held the lock). Scope is
honest: it serialises truedat against truedat, which is the lost-update case; it cannot serialise
truedat against a text editor saving the file, and the `.bak` remains the recovery path there.
Because every refusal path returns before touching the file and the write is atomic, the failure
message is `The exclusion file was not modified.` — the old `Nothing was written.` was false both
for a mid-write failure and for the `apply-result.json` the same run does write.

The `Episode Date` podcast heuristic is **deleted** (2026-07-24) and must not return:
MusicBee maps ID3v2.4 `TDRL` (a *release* date) into that key, so it rides on ordinary music.
Explicit labels only — `Podcast=true` or `Genre=Podcast` — and even those are evidence now,
not a filter (see below).

**Speech heuristics do not decide anything, and must not be reinstated as a filter.**
`ITunesParser` still sets `IsSpeech`/`SpeechReason` on parse (from the explicit XML labels
only — the `Episode Date`/Publisher/duration vote is gone too, see above), and
`SpeechTagSniffer` still sniffs embedded file markers, but both are purely evidence for the
`--preview` review surface (`reasons` on a review candidate; graded `SpeechMarkers.Strong`
/ `.Provenance` / `.GenreText` — strong = ID3 `PCST`/MP4 `pcst` asserting "this IS a
podcast", provenance = ID3 `WFED`/`TGID`/MP4 `purl` asserting only "this came from a feed",
genre-text = ID3 `TCON` exactly `Podcast` trimmed/case-insensitive, **not** substring).
Nothing reads either signal to skip
a file or prune a catalog entry — `FilterPodcasts` and the MoodsMode prune that consumed it
are deleted; `SeedCommand.cs`'s own `library.RemoveAll(t => t.IsSpeech)` work-list filter
(an independent open-coded copy that never called `FilterPodcasts`, which is why the plan's
original file map missed it) is deleted too, and the cache-miss embedded-marker skip in the
three per-file scan paths is deleted as well (the sniffer now runs only inside `--preview`). `--migrate`
strips legacy fields only (`valence`/`arousal`, `audioMd5`, `chromaprint`; `fileMd5` unless
`--file-md5`; renames `sensme*`→`smfm*`) and never removes an entry on any verdict —
speech-labelled, speech-likely, or otherwise. `speechLikely` is advisory only; **AutoQ (MBXHub)
is its downstream consumer**, via the read side of `mbxmoods.json`. Inside truedat, only the
read-only review surfaces (`--list-speech`, the `--stats` speech count) recompute and
report it — neither one skips a file or removes an entry. `--include-podcasts` and
`ITunesTrack.ExclusionIncluded` are retired outright (hard cut, 2026-07-25) — the file
`mbxmoods-exclude.json` is the only override, via its own `include` rules.

**Taxonomy — the class is SPEECH, and there will be no podcast detector** (operator ruling,
2026-07-25). Audiobook, comedy, lecture, news, interview and talk-dominant are ONE GENUS: speech.
"Podcast" is never a class — it is the name of a particular piece of *evidence* (the `Genre=Podcast`
or `Podcast=true` label, a `PCST` marker) or an external key, and those literals keep their real
names precisely because they belong to other systems. Identification therefore has three sources
answering two different questions: the XML labels and the embedded markers are **declared**
evidence, and `speechLikely` is the **measured** one — the only source that can see a *downloaded
or copied archive* of a show, which carries no feed registration and is invisible to every label
and marker. That archive class is the entire reason the acoustic verdict exists.
**Do not add a `podcastLikely` verdict.** Content analysis can answer "is this speech-dominant"
and nothing more; podcast-vs-audiobook-vs-lecture is *provenance* — was this distributed as an
episodic feed — and provenance is not present in the audio. A file that lost its feed registration
lost that fact permanently, and no signal processing recovers it. Species-level questions are
answered by folder layout, genre and feed registration, i.e. by operator-written rules. Proposing
an audio-derived podcast classifier means proposing to assert something the evidence cannot
support, which is the same category error the whole Heuristics → Evidence arc removed.
This has been walked back **twice** now (the `Episode Date` vote in 2026-07-24, then the
label/marker skip + the two `--migrate` purges + `--include-podcasts` in this phase) —
a third heuristic-decides-something attempt should have to argue against this paragraph in
writing, not rediscover why the first two didn't work.

`--check-filenames` (`RunCheckFilenames`) deliberately does **not** apply `_exclusions` —
it reports filesystem problems (over-length paths, zero-byte files) that are worth
surfacing whether or not a track is excluded from analysis ("check is check, not
exclude"); it filters only `FilterRemoteUrls` / `FilterVideoFiles` / `FilterNonAudio`
(structural) and never calls `FilterExclusions`, so don't "fix" that by wiring exclusions
in without a deliberate decision to do so.

`--preview` builds the same picture without scanning. **It is read-only over the CATALOG, not
over the filesystem** — say "never writes `mbxmoods.json`, never analyzes", because unqualified
"read-only" is false and already misled one reviewer (I-6): with no explicit path it *creates*
`<library root>\AppData\MBXHub\review\` and writes `preview.json` + `mbxmoods-preview.html`
into it, i.e. into a running application's data directory. Those files are regenerable and the
destination is anchored to the library truedat was pointed at, so this is by design — but it is a
write and the docs must not imply otherwise. `PreviewPlanner` computes a `PreviewPlan` from the
parsed XML + existing catalog + `ExclusionSet`, and `PreviewWriter` emits `preview.json` into the
MBXHub review folder via `ResolveReviewDir` — the **same** function `ResolveManifestDest` uses for
the duplicates manifest, so both derive the instance root from the moods file's `<root>\Library\`
location and neither can drift to "whichever MusicBee the OS lists first". Do not write a second
resolver; the self-tests pin that preview and the manifest share one anchor and two filenames. **`preview.json` IS the review-surface manifest** — MBXHub's asset route serves only
`.html` and no route serves an arbitrary sibling JSON, so the payload rides inside the envelope;
`source.reviewHtml` stays absent until the Phase 2b page exists. Preview touches audio in exactly
one place — a bounded header sniff over review *candidates* only — and reports `sniffedCount` so
that is visible rather than implied. The ETA comes from the catalog's stored `analysisDuration`
against known track lengths (median), reported as `catalog-rtf`; with no history it is omitted,
never guessed. `--long-track-mins` (default 30) is a review **prompt**, not a rule.
`--apply-exclusions` writes `apply-result.json` beside the exclusion file on both success and
failure, because MBXHub's stdout capture is a bounded async tail and must not be parsed.

## Chunked scanning across machines

`--chunk M/N` uses hash-mod assignment: `(PathComparer.GetHashCode(path) & 0x7FFFFFFF) % N == M-1`, in **one** helper — `ChunkOwns` — used by both the work filter and the ledger scope, so what a shard does and what it records cannot diverge. `PathComparer`'s hash is FNV-1a, deterministic across processes and machines, separator-normalized, case-folded. Same path → same bucket on every box, **regardless of XML differences**. Asymmetric libraries are tolerated. Output filenames auto-suffix with hostname to prevent shard collisions when machines write to a shared library directory. Combine with `--merge-moods`.

**Evaluate library-wide, ledger shard-scoped** (I-4). The four pre-scan filters (`FilterRemoteUrls` / `FilterExclusions` / `FilterVideoFiles` / `FilterNonAudio`) deliberately run BEFORE the chunk filter, so `IsExcluded` sees every track and per-rule `MatchCount` is library-wide and identical on every machine — that cross-machine agreement is what makes the stale-rule signal trustworthy, and it must not be "fixed" by moving the chunk filter up. But the ledger is a per-run artifact: `_ledgerScope` (set from `ChunkOwns` in MoodsMode, `_ => true` otherwise) gates the `mbxmoods-skipped.csv` row, the `--audit` line and the printed count in all four filters, so the union of N hostname-suffixed ledgers is the library rather than N copies of it. Removal from the work list stays library-wide — a video in another shard's bucket is not this shard's work either way. Under `--chunk` the printed counts carry ` (this chunk)` and a line states that rule counts are library-wide while the ledger is the shard's share, because the two numbers legitimately disagree. `--merge-moods` still reconciles **only** catalogs, never the CSV ledgers — it is a catalog merger and the ledgers no longer need reconciling.

Mutually exclusive with `--analyze-file` / `--file-list` / `--folder` / `--migrate` / `--fixup` / `--verify` / `--merge-moods` / `--synthesize` / `--seed-moods` / `--hash-only`.

## Verify mode

`--verify` (read-only) walks `mbxmoods.json`, recomputes `audioStreamSha256` per entry, and writes `mbxmoods-verify.csv` (status: OK / DRIFT / MISSING / NO_HASH / ERROR). Exit 1 on any drift / missing / error so it's CI-friendly. Pure diagnostic — no auto-repair (the right action depends on cause: tag edit → rescan, re-encode → reanalyze, file gone → drop).

`--verify --backfill` extends the same walker to populate missing fields in-place. Two tiers, both gated by the SHA drift check (drifted entries become `REANALYZE_NEEDED` and are NOT modified):

**Identity tier — TagLib + cheap file IO, no audio decode:**

- **Tier A** — entry-level: `audioStreamSha256` (computed if absent), `fileMd5` (computed if absent, only under `--file-md5`).
- **Tier B** — whole `fingerprint.v1` block, when null (entries written by pre-fingerprint.v1 builds; they pass the re-extract canary so the tier-1 cache pins them forever — backfill is the only path that adds the block). Calls `ComputeFingerprintV1` directly; a fresh fingerprint contains every IdentityField so Tier C is moot for that entry.
- **Tier C** — sub-fields inside an existing `fingerprint.v1`, driven by the `IdentityFields[]` spec list (one entry per field). Adding a future TagLib-readable identity field = one new `IdentityFieldSpec` + one new class field + matching read/write — same four-surfaces rule.
- **Tier C/Phase 2 — MP3 LAME tag (FileBytesShallow)**: when `codec=="mp3"` and the LAME-tag fields are unpopulated, `ApplyBackfillIdentity` invokes `Mp3LameTagParser.TryParse` (pure-managed, reads ~8 KB from file start, skips any ID3v2, finds first MPEG frame, locates Xing/Info magic, decodes the appended LAME tag). Off the `IdentityFields[]` list because it doesn't use TagLib — kept as a separate guarded branch to preserve the spec list's "TagLib-only" contract.

**Features tier — ffmpeg-driven, no Essentia (`ApplyBackfillFeatures`):**

- `bitUsage`, `hfEnergyRatio`, and `hfSpectralStructure` — when missing on entries the source helpers consider applicable (codec ∈ {flac,alac,wav,aiff} ∧ bitDepth ≥ 24 for bitUsage; sourceSampleRate > 44.1k for the HF analysis pair). `hfEnergyRatio` and `hfSpectralStructure` fall out of a single `ComputeHfAnalysis` call so they're always backfilled together. Each applicable track costs ~30 s of ffmpeg decode, so this tier is the slow path. Silently skips entries when ffmpeg is absent.

`--backfill-level identity|features|all` selects scope; default is `all`. Use `identity` to keep backfill fast / ffmpeg-independent; use `features` to backfill only the ffmpeg tier on a library whose identity is already complete.

Backfill is idempotent (re-runs do zero IO) and atomic (single `SaveResults` at end, only when any entry actually changed). The `bitDepth` spec's `IsPresent` is codec-aware via `CodecLacksBitDepth` — lossy formats (mp3/aac/opus/vorbis/ogg/wma/mpc) count as "complete at 0" so backfill doesn't loop. Same pattern applies to any future field that's structurally absent for some codecs. CSV gains a fourth column `backfilledFields` listing which fields were filled per entry. Status set: OK / BACKFILLED / REANALYZE_NEEDED / MISSING / ERROR.

## Duplicates report

`--duplicates [path]` (read-only, JSON-only — works on metadata mirrors) groups mbxmoods.json entries in two tiers: `exact` (byte-identical `audioStreamSha256`) and `probable` (quantized-feature candidate key over mfcc/bpm/key/mode/duration — cross-encode candidates, human confirms). Each group marks one recommended `keeper` — lossless > bitDepth > sampleRate > bitrate > **HasSmfm** > size > shortest path. The SMFM tiebreaker means when audio quality ties (typical for exact dupes), the Sony 12-TONE-tagged copy wins over an untagged duplicate — e.g. the newly-named Sony-organized file beats the old historical one. Per-member JSON fields: `path, artist, title, album, codec, bitrate, sampleRate, bitDepth` (omit-when-0/empty) + `smfm` (bool, `TrackFeatures.HasSmfm`) + `keeper` (true on the recommended). Console dump flags SMFM members with `[smfm]`.

Outputs (writer tail always runs, even at zero groups): console (same/cross-folder split per tier), `mbxmoods-duplicates.csv`, `mbxmoods-duplicates.json` (the machine contract, tolerant-reader pattern), plus three opt-in emitters:

- `--losers-m3u [path]` — non-keepers to an .m3u8 (path must end in .m3u/.m3u8; default `mbxmoods-duplicate-losers.m3u8`) from the *recommended* keepers, for review/removal inside MusicBee.
- `--manifest [path]` — the `kind:dupes` **review-surface manifest** (`WriteDuplicatesManifest`) that MBXHub's `review.html` renders directly, one class per tier×scope, one row per member, keeper flagged, `album`/`bitDepth` columns light up when present, `smfm` column always. truedat emits this itself (no offline PowerShell producer — that was the stale-manifest footgun). **No path → auto-locate:** derive the instance root from the moods file's `<root>\Library\` location, write to `<root>\AppData\MBXHub\review\dupes.json` (`ResolveManifestDest`, matching MBXHub.Shell's PluginLocator layout); multi-instance-safe (anchors to the scanned library, not whichever MusicBee the OS lists first); falls back to a running-process match, then next-to-moods. Explicit path always wins. The manifest is DISPLAY-ONLY — the hub page has no apply. `--manifest` also **co-emits the interactive `dupes.html`** (the `--html` page) into the same review folder and records `source.reviewHtml: "dupes.html"`, so the hub's read-only display can link straight to the truedat mark-and-build-playlist tool (the hub serves/links it; truedat just puts the file there and points at it).
- `--html [path]` — a **self-contained interactive review page** (`WriteDuplicatesHtml`, default `mbxmoods-duplicates.html`): embedded group data, inline JS/CSS, no external requests, opens offline in a browser. Chunk-friendly: groups start NOT included; the operator ticks groups to act on, confirms a keeper per included group (recommended pre-selected), then Build losers playlist downloads an .m3u8 of the non-keepers in the *included* groups. Include + keeper state persists in localStorage; `include all shown` + `only SMFM` filter for batching. This is the truedat-owned, offline "mark and act" tool; the MBXHub manifest path is the read-only fleet-wide view.

Truedat never deletes or modifies files — every dedupe output is a report or a playlist; removal happens in MusicBee.

## What an external launcher depends on (cross-repo surface)

MBXHub drives truedat as a managed external tool (its Tool Lifecycle Service; the contract
lives in **restfulbee** at `docs/reference/TOOL-LIFECYCLE-SERVICE.md` — read it there, do
not copy it here, it is theirs and a duplicate would drift). truedat is its first customer.
What matters on **our** side is that the following are a CONTRACT, not incidental behaviour.
Changing any of them is a cross-repo decision that needs a heads-up to restfulbee, not a
local tidy-up:

- **Exit codes on the scan paths.** The autoscan plugin pipes paths to `truedat --file-list -`
  and reads the exit code. A missing file is a *skip* (exit 0, ledgered to
  `mbxmoods-skipped.csv`), not a failure — changed 2026-07-26 to match MoodsMode; if anything
  ever moves back the other way the plugin's notion of "something went wrong" moves with it.
- **`--apply-exclusions` writes `apply-result.json`** beside the exclusion file on **every**
  path it reaches — success, not-found, unreadable, merge-error — and exits 1 on an invalid
  document *without writing the exclusion file*. The hub reads that file rather than stdout
  because its runner keeps only a bounded async tail, so a scraped summary can silently
  truncate. **Never make a path that skips writing it**: the caller cannot distinguish "no
  result" from "stale result from the previous run" without a freshness check, and it had to
  add one after we shipped a case where truedat could die before writing.
- **`--preview` emits both `preview.json` and the page**, and `preview.json` **is** the review
  manifest the hub serves (its filename stem is the route id). The hub's asset route serves
  only `.html` and no route serves an arbitrary sibling JSON, which is why the plan payload
  rides inside the manifest envelope rather than beside it.
- **`--preview` is not read-only in the filesystem sense.** It writes the manifest and the page
  into the review folder. What it never does is analyze, or write `mbxmoods.json`. Say the
  precise thing; "read-only" invites the wrong assumption.
- **There is no `purge` mode, and there must not be one.** Nothing in truedat removes a catalog
  entry on a classification — that whole class was deleted in the Heuristics → Evidence arc.
  `--fixup` is the only mode that can cost an operator an entry, and it does so on ground truth
  (`File.Exists` says the file is gone), not inference. Their contract briefly listed a `purge`
  mode for truedat; it was removed on our correction. Do not implement one to satisfy a doc.
- **Reason strings in `preview.json`'s `review[].reasons`** are rendered by the hub, not parsed
  by it — confirmed on their side. Enriching the text is safe; the shape is not.

## Conventions

- **Offline-first.** No runtime network calls. No CDN-fetched assets, no cloud dependencies. Truedat reads files, runs subprocess tools, writes files.
- **No new runtime dependencies** without discussion. `System.Text.Json` and `TagLibSharp` are the only NuGet refs; both merged into the single exe via ILRepack.
- **Never push.** Never change repo visibility. Never mark project status as "Production Ready" / "Stable" / etc. — those are user-only decisions.
- **Never add Co-Authored-By lines to commits.** This is a hard rule in the user's global CLAUDE.md.
- **Commit convention:** `feat(scope):`, `fix(scope):`, `docs:`, `build:` (for exe rebuilds). Keep messages short; body explains *why* not *what*.
- **Don't rebuild `truedat.exe`** unless the user asks — the solo-dev workflow handles rebuilds in separate "build: update truedat.exe" commits.
- **Local code reviews, plans, and other working notes go under the local `docs/` directory (gitignored).** Short console summary back to the user is fine. Never commit these.

## Things that aren't what they look like

- `ITunesParser.cs` reads iTunes `Music Library.xml` — that's the library input source, even though the output goes to MBXHub.
- `PathSanitizer.cs` is for the synthetic-library generation path (`--synthesize`), not the scan path.
- `essentia-build/` is a WSL2/MinGW cross-compile environment; don't try to run its scripts from PowerShell.
- `ReferenceCode/` (sibling repo) has legacy MBX code — read-only, don't modify.

## When in doubt

Read the local (gitignored) `docs/plans/` and `docs/reviews/` before touching the scan pipeline. Every non-trivial change in recent history has a plan or review doc capturing the design rationale.

## Authenticity sprint state (2026-05-18)

Phases 1–5 shipped — `bitDepth` + `encoder` in `fingerprint.v1` (Phase 1), `mp3LameTag` block (Phase 2), `bitUsage` block (Phase 2.5), `hfEnergyRatio` (Phase 3), `truedat.*` verdict block (Phase 4), `hfSpectralStructure` FFT-derived signal block (Phase 5 — adds Signal F at weight 0.35 to the hi-res vote, consolidates the Phase-3 two-ffmpeg pipeline into a single managed-FFT pipe). Current verdict method tag: **`truedat-v1-fft-corpus1-2026-05-18`**.

A local test corpus exists; details are recorded in the gitignored `docs/reviews/` tree. Phase 5 retune scorecard: 23/23 hi-res classifications correct (the 3 ffmpeg-upsampled-fake FLAC misses from Phase 4 are now suppressed or correctly classified).

Known signal gaps remaining (Phase 5+ work, NOT bugs):
- LAME-to-LAME re-encode chains verdict "no" (second LAME encode rewrites Xing tag) — needs cascade-encode artifact detection.
- `hfSpectralStructure.imagingSymmetry` is currently unused in the vote (Lanczos suppression neutralized it on corpus-1); the field is still emitted and tracked for future-corpus tuning.

**Opt-outs for slow scans.** `--no-bitusage` suppresses `ComputeBitUsage` (omits the `bitUsage` JSON block); `--no-hf-analysis` suppresses `ComputeHfAnalysis` (omits `hfEnergyRatio` / `hfEnergyMethod` / `hfSpectralStructure`). Each saves one ffmpeg subprocess + 30 s mid-track decode per track at all three fan-out sites. The `truedat` verdict block still emits but with a reduced signal set (Signal A lost with `--no-bitusage`; Signals B + F with `--no-hf-analysis`) — borderline cases more often return `"unknown"`. `truedat.method` carries the algorithm-version stamp regardless of which signals fed in. Both flags are orthogonal to staging — combine with `--no-stage` for forensic runs, or use alone to cut wall-clock without giving up source caching. The cache canary is **not** widened to recognise entries scanned with these flags as "partial" — the bitUsage / hf fields are explicitly optional, matching the existing ffmpeg-absent install pattern. Backfill these fields with `truedat --verify --backfill --backfill-level features`.

For session resume: read the latest `SESSION-RESUME-*.md` in the local (gitignored) `docs/` directory first.
