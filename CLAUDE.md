# Truedat — LLM Session Context

This file is loaded automatically by Claude Code / Claude Agent sessions scoped to this repo. Keep it short and decision-making-focused; deep reference material belongs in `README.md` and `SBOM.md`. Internal design notes and session journals live in the local `docs/` directory, which is gitignored — never reference them from any file that ships in the repo.

## What this project is

Truedat is a Windows .NET command-line tool that analyses a music library's audio and writes `mbxmoods.json` (mood + Essentia features), optionally `mbxhub-fingerprints.json` (Chromaprint + audio MD5), and `mbxhub-details.json` (ffprobe stream details). Output files are consumed by MBXHub's AutoQ engine (separate repo).

- Build: `build-truedat.cmd` (requires .NET SDK 8.0+). Single-file output at `dist/truedat/truedat.exe` (~1 MB, ILRepack-merged).
- Runtime deps: `essentia_streaming_extractor_music.exe` (required for default scan), plus optional `ffmpeg.exe` (multi-channel downmix, `Unsupported codec` retry — e.g. `.opus`, since this essentia build lacks libopus — and the standalone `--transcode` utility) and `ffprobe.exe` (audio-property probe for `--details` and for matching source rate/depth in `--transcode`). `essentia_streaming_md5.exe` and `fpcalc.exe` are **legacy-mode only** — used by `--fingerprint`, `--md5-only`, `--quick-fingerprint`. Default scan no longer runs them.
- Framework: **.NET Framework 4.8**. No .NET 6/8 APIs, no `ValueTask`, no `init` setters on public types. Use `System.Text.Json` (merged via ILRepack).

## mbxmoods.json schema (current)

Per-track JSON object under `"tracks"[path]`. **55 numeric feature fields** (15 core + 40 extended, all extended are nullable — missing means the key is omitted, not written as `null`), plus metadata/identity fields.

- **Core features** (always present): `bpm`, `key`, `mode`, `spectralCentroid`, `spectralFlux`, `loudness`, `danceability`, `onsetRate`, `zeroCrossingRate`, `spectralRms`, `spectralFlatness`, `dissonance`, `pitchSalience`, `chordsChangesRate`, `mfcc[]`.
- **Extended** (nullable, omit-when-missing): `dynamicRange` + `dynamicRangeSource`; loudness envelope (`loudnessMomentary`, `loudnessShortTerm`, `replayGain`); silence (`silenceRate20dB/30dB/60dB`); spectral shape (rolloff, complexity, entropy, kurtosis, skewness, spread, strongPeak, decrease, energy + 4 energybands); `hfc`; Bark/ERB/Mel band stats (crest, flatness, kurtosis, skewness, spread × 3 scales = 15); rhythm/tonal (`beatsLoudness`, `chordsStrength`, `hpcpCrest`, `hpcpEntropy`); Phase 2.5 `bitUsage` block (`lowestNonZeroBit`, `bottomBitActivity`, `effectiveBits`, `samplesAnalyzed`, `method`) — populated during fresh analysis via `ComputeBitUsage` (ffmpeg → s32le mono 30 s mid-track → trailing-zeros histogram). Null on legacy entries, ffmpeg-absent installs, and silent / undecodable files. **Detection-only valid for codec ∈ {flac, alac, wav, aiff} claiming `bitDepth ≥ 24`** — lossy decode floods the LSBs with reconstruction noise so the metric is meaningless for MP3/AAC/etc. Phase 3+5 `hfEnergyRatio` + `hfEnergyMethod` and `hfSpectralStructure` block — derived from a **single** ffmpeg-piped managed-FFT pass (`ComputeHfAnalysis`) over 30 s mid-track at the source's native sample rate. `hfEnergyRatio` is the fraction of audio energy above 22.05 kHz (Parseval-style energy-weighted across windows). `hfSpectralStructure` carries three FFT-derived discriminators: `flatness` (Wiener entropy of HF bins, range [0,1] — broadband = real, peaky = imaging artifact), `peakToMean` (max/mean HF mag², ratio — catches narrow spikes), `imagingSymmetry` (Pearson r of HF bins with their mirror about the original 22.05 kHz Nyquist, range [-1,1] — currently unused in the verdict; Lanczos suppression made it useless on corpus-1, kept for future tuning). Only populated when `sourceSampleRate > 44100` — no Nyquist headroom otherwise. Method tag `managed-fft-radix2-30s-mid-native`. Together with `bitUsage`, these are the independent fake-hi-res signals that dither evasion can't fabricate.
- **Identity** (nullable, omit-when-missing): `fileMd5`, `audioMd5`, `fingerprint.v1` (composite, written via `WriteFingerprintV1` — fields: `fileSize`, `pathTail`, `durationMs`, `sampleRate`, `channels`, `bitDepth` (omit-when-0), `codec`, `codecRaw`, `bitrate`, `encoder` (omit-when-empty), `encoderRaw` (omit-when-empty), `mp3LameTag` (Phase 2 nested object, populated when codec=mp3 and the file has a Xing/Info+LAME header — fields: `version`, `vbrMethodCode`, `vbrMethod`, `lowpassHz`, `encoderDelay`, `encoderPadding`, `musicCrc`, `infoTagRevision`), `audioHead64kMd5` + source flag), `audioStreamSha256` (ride-along in default Essentia mode via `ComputeAudioStreamSha256FromFile`; emitted in all three scan-path modes — MoodsMode, `--file-list`, `--analyze-file` — plus `--hash-only --level stream`).
- **Housekeeping**: `lastModified`, `analysisDuration`.
- **Sony SMFM (12-TONE)** (nullable, omit-when-missing; read fresh every scan via `SmfmReader`, never cached): `smfmScores` (raw STMO slot scores, `int[10]`, 0-255), `smfmChannel` (dominant raw STMO slot index = argmax — **NOT a mood channel**), `smfmChannelName` (**always null** — the slot→name labels were **device-refuted** on 2026-06-27: SMFM slots are not 1:1 with Sony's mood channels, which are 2-D arousal×valence regions; kept as a back-compat property), `smfmBpm` (Sony GBPM). **Renamed from the former `sensme*` keys on 2026-06-28** — the read path keeps a `sensme*` old-key fallback for un-migrated libraries, and `--migrate` rewrites existing files `sensme*→smfm*` (with backup). The interpreted `(arousal, valence)` signal is derived **downstream** (MBXHub/AutoQ), not in truedat.
- **Phase 4 verdict** (nested `truedat` object): `hiresGenuine` + `hiresConfidence`, `lossyTranscodeLikely` + `lossyTranscodeConfidence`, `method`. Computed inline at write time by `ComputeTruedatVerdict` from the already-extracted features; **not persisted in TrackEntry / cache** so threshold changes ship without rescan. Four-string enum (`"yes"` / `"no"` / `"unknown"` / `"n/a"`) — explicitly NOT a bool, because collapsing "unknown" into yes/no is what produces the false-positive/negative failures the verdict block was designed to avoid (see BACKLOG "Known detection challenges"). Block omitted entirely when both questions are `"n/a"` (legacy entries without fingerprint.v1, weird-codec files). Method tag identifies the algorithm/threshold generation — currently `truedat-v1-untuned-2026-05-18`, flips to `truedat-v1-YYYY-MM-DD` once a labeled test corpus calibrates the thresholds (Phase 4 corpus is the gating step in BACKLOG before the verdict can be considered tuned). Per-signal vote+weight trace emitted to stderr under `--audit`.

Four I/O surfaces must stay in sync: `AnalyzeWithEssentiaCore` (extract), `WriteTrackEntry` (write), `LoadExistingMoods` (read via `ParseTrackFeaturesFromJson`), and the cross-MD5 cache-reuse branch in `MoodsMode`. Adding a field means touching all four.

## Rounding convention (extended features)

`Opt(v, int dp = 4)` / `OptN(root, path, int dp = 4)` — default 4 dp. Overrides:

- 2 dp: dB/LU values (`loudnessMomentary`, `loudnessShortTerm`, `replayGain`, `beatsLoudness`, `hpcpCrest`, `hfc`), `spectralComplexity`.
- 1 dp: Hz (`spectralRolloff`).
- 6 dp: tiny spectral values (`spectralDecrease`, `spectralEnergy*`).

Don't regress to a uniform 6 dp — it inflates JSON size without analytic value.

## Concurrent hashing in mood analysis

The per-track fan-out lives in **`RunSourceWorkers`** — one shared helper called from all three scan paths (MoodsMode, `--file-list`, `--analyze-file`). It launches Essentia + `ComputeFileMd5` + `ComputeFingerprintV1` + `ComputeAudioStreamSha256FromFile` + `ComputeBitUsage` (Phase 2.5) + `ComputeHfAnalysis` (Phase 3+5, produces both `hfEnergyRatio` and `hfSpectralStructure` from one ffmpeg pipe) + SMFM concurrently via `Task.Run` + `Task.WaitAll`, returns a `WorkerResults` bundle. Wall-clock is ~`max(analysis, slowest-task)` per track. The `extractTags` parameter controls whether `ExtractFileTags` runs (false for MoodsMode — iTunes XML supplies metadata; true for `--file-list` / `--analyze-file`). `knownDurationSec > 0` short-circuits the bitUsage TagLib re-parse for callers that already have duration. All workers read from `src.Path` (the staged copy if staging happened, the original otherwise). All managed hashes are pure-managed (no subprocess), no path-escape exposure. The `audioStreamSha256` task hashes the TagLib invariant audio region with SHA-256 — content-stable, portable, fast (SHA-NI hardware accel). `ComputeBitUsage` and `ComputeHfAnalysis` are the subprocess-bearing tasks (each is a single ffmpeg pipe) — both silently return null when ffmpeg.exe is absent, so the worker still completes. Phase 5 consolidated the prior two-ffmpeg `hfEnergyRatio` pipeline into a single ffmpeg pipe feeding a managed-FFT loop, netting −1 subprocess per applicable track vs Phase 3.

The legacy `essentia_streaming_md5.exe` and `fpcalc.exe` subprocesses are **no longer in the default codepath**. They're invoked only by `--fingerprint`, `--md5-only`, and `--quick-fingerprint`, where they still run via `RunTool` (with the `SafePath`/`TryCreateHardlink` path-escape fallback).

## Source staging

`OpenStagedSource` copies the file once to `%TEMP%\.truedat-stage\<guid>.<ext>` and points every worker at the local copy when the source is on a remote share or has a non-ASCII path: UNC (`\\server\share\…`, method `staged`), mapped network drive (drive letter where `DriveInfo.DriveType == Network`, method `staged`), or local non-ASCII path (method `staged-fallback`). Local-ASCII paths pass through directly. Net: 1× full network read per track instead of ~3× full + ≥3× partial. The GUID name is always ASCII (extension sanitized to `.bin` when the source ext isn't ASCII printable), so the non-ASCII / 8.3-disabled footgun disappears for free. `--no-stage` opts out; `--stage-dir <path>` overrides the staging directory (default `%TEMP%\.truedat-stage`). Steady-state disk footprint = at most `parallel_worker_count` staged files (per-track `using var src` / `try/finally` cleans up); orphans from killed scans are swept at the next startup by `CleanupOrphanedFiles` (single-instance only — concurrent invocations sharing a stage-dir would race the sweep; cross-machine scaling is `--chunk M/N`, not concurrent same-library). The original source path stays the cache key, JSON output path, and errors-CSV identifier — the GUID staging name never leaks out of the worker fan-out.

Mapped-drive detection is per-root memoized in `_networkDriveCache` (ConcurrentDictionary, FNV root → bool, failures cached as false). `SourceHandle.SourceLastWriteUtc` snapshots the source mtime inside `OpenStagedSource` immediately after `File.Copy` — the scan modes record `TrackEntry.LastModified` from this snapshot so a tag touch between copy and persist can't make the recorded mtime newer than the analyzed audio. End-of-scan `EmitStagingSummary` prints `staging: N staged [, M direct-fallback]` and warns to stderr when >5% of attempted stages fell back to direct read (wedged stage-dir surfaces visibly instead of silently slow).

## Hash-only mode (offline NDJSON manifest)

`--hash-only --level fingerprint|stream --file-list <paths.txt> --output <manifest.ndjson>` runs identity-only passes without Essentia and appends one envelope per file to an NDJSON manifest. Used by the determinism rig at `tools/verify-audiosha-determinism.ps1`.

- `fingerprint`: TagLib parse + 64 KB MD5 at `InvariantStartPosition`. Sub-10 ms warm. Envelope carries the `fingerprint.v1` composite (pathTail + fileSize + audio props + audioHead64kMd5).
- `stream`: streaming SHA-256 over `[InvariantStartPosition, InvariantEndPosition)`. Disk-bound. Superset — envelope carries `fingerprint.v1` *and* `audioStreamSha256`.

Envelope shape is defined by `BuildIdentityOnlyEnvelope` in `Program.cs`. It has no external consumers — when the rig and the format need to evolve, evolve them together.

## Cache hierarchy & re-extract gate

All four scan modes — `MoodsMode` (default iTunes-XML), `--folder`, `--file-list`, `--analyze-file` — check the cache in this order before falling through to Essentia:

1. **Path + mtime** (`allTracks[t.Location]`, `TruncateToSeconds` equality) — the fastest path. Returns `(cached)` / `[CACHED]`. **Does NOT open a staging handle** — no body read happens, so a UNC tier-1 hit is free.
1.5. **Path + head-64k quick tier** (`(cached·head)` / `[CACHED·head]`, default ON, `--no-quick-cache` opts out) — when path matches but mtime drifted, recompute `fingerprint.v1` (TagLib parse + 64 KB head hash, ~1 ms, on the **original** path — no staging, which would copy the whole file). `IsTagsOnlyChange` accepts when the fresh and stored fingerprints agree on `audioHead64kMd5` (measured from `InvariantStartPosition`, so a tag write can't move it) plus codec/sampleRate/channels/bitDepth and durationMs within 500 ms. On a hit: reuse Essentia features, refresh `fingerprint.v1`, and **clear `fileMd5`** (the whole-file MD5 is stale after a tag write and recomputing it needs the full read this tier exists to avoid — `--verify --backfill --backfill-level identity` refills it). Any mismatch falls through to tier 2. This is the fast path for a post-retag rescan: ~64 KB/track instead of a full audio-hash read. Residual risk: an in-place audio edit past the first 64 KB that preserves duration and codec props slips through — deliberate tampering, which `--verify` (full SHA) still catches; the durable `audioStreamSha256` in the JSON is untouched.
2. **Path + audioStreamSha256** (`(cached·sha)` / `[CACHED·sha]`) — when path matches but mtime drifted and the quick tier didn't fire (or `--no-quick-cache`), recompute `audioStreamSha256` (SHA256Cng / SHA-NI). If it matches the cached value, audio bytes are unchanged (just tags). Reuse Essentia features, refresh `lastModified` + `fileMd5` + `fingerprint.v1` (the tag-affected fields).
3. **Cross-MD5** (`moodMd5Index`, `(cached·md5)` / `[CACHED·md5]`) — different path, byte-identical file (covers paths-changed-but-mtimes-preserved-on-copy and clean moves). Re-keys old → new path.
4. **Cross-SHA** (`moodShaIndex`, `(cached·sha)` / `[CACHED·sha]`) — different path AND tag-edited (file bytes differ but invariant audio region matches). Re-keys old → new path; recomputes `fileMd5` + `fingerprint.v1`.

Tiers 2/3/4 and the cache-miss worker fan-out all route body reads through one lazily-opened staged copy via `EnsureStagedSrc()` — opened on first body read, reused for every subsequent read in the tier walk and the workers, disposed in `finally`. Within a walk the whole-file MD5 and invariant-region SHA-256 are computed in a **single read pass** (`ComputeFileMd5AndAudioSha`, memoized per track as `EnsureBodyHashes()`): tier-2 no longer re-reads the file for the refreshed `fileMd5`, tier-3 reuses that MD5, tier-4 reuses both — worst-case tier fall-through is one full read, not three. So a tier-2/3/4 hit on a UNC track copies once and amortizes that copy across the tier's hashes + the worker fan-out, instead of re-reading the same source N times across the network. `audioStreamSha256` uses `SHA256Cng` (SHA-NI hardware accel) — `SHA256.Create()` resolves to the managed implementation on net48 (no SHA-NI, ~5-10× slower).

`MoodsMode` uses `RebuildCacheEntry` (takes an `ITunesTrack` for metadata); `--folder` / `--file-list` / `--analyze-file` use `RebuildCacheEntryFromTags` (takes TagLib-derived metadata). Both delegate to `RebuildCacheEntryCore` — single source of truth for the 55-feature copy. Adding a new `TrackFeatures` field means updating one helper, not 7 inline call sites.

The plugin-driven autoscan workflow (MBXHub plugin pipes new file paths to `truedat --file-list -` or `truedat --folder ... --moods ...` after MusicBee detects new files) depends on these tiers — without them, every poll re-runs Essentia on the entire library.

Re-extract (cache miss → full Essentia) when any of these are missing:

- `DynamicRange` — pre-LRA builds.
- `LoudnessMomentary` — pre-extended-feature builds (canary for the 40-extended set).

When adding a new always-extracted field, decide whether to add it to the canary. Avoid canary conditions that depend on optional external tools — those caused infinite re-extraction loops on installs without the tool, which is why the old `audioMd5`-presence canary was removed when the binary left the default codepath.

## Chunked scanning across machines

`--chunk M/N` uses hash-mod assignment: `(PathComparer.GetHashCode(path) & 0x7FFFFFFF) % N == M-1`. `PathComparer`'s hash is FNV-1a, deterministic across processes and machines, separator-normalized, case-folded. Same path → same bucket on every box, **regardless of XML differences**. Asymmetric libraries are tolerated. Output filenames auto-suffix with hostname to prevent shard collisions when machines write to a shared library directory. Combine with `--merge-moods`.

Mutually exclusive with `--analyze-file` / `--file-list` / `--folder` / `--migrate` / `--fixup` / `--verify` / `--merge-moods` / `--synthesize` / `--seed-moods` / `--hash-only`.

## Verify mode

`--verify` (read-only) walks `mbxmoods.json`, recomputes `audioStreamSha256` per entry, and writes `mbxmoods-verify.csv` (status: OK / DRIFT / MISSING / NO_HASH / ERROR). Exit 1 on any drift / missing / error so it's CI-friendly. Pure diagnostic — no auto-repair (the right action depends on cause: tag edit → rescan, re-encode → reanalyze, file gone → drop).

`--verify --backfill` extends the same walker to populate missing fields in-place. Two tiers, both gated by the SHA drift check (drifted entries become `REANALYZE_NEEDED` and are NOT modified):

**Identity tier — TagLib + cheap file IO, no audio decode:**

- **Tier A** — entry-level: `audioStreamSha256` (computed if absent), `fileMd5` (computed if absent).
- **Tier B** — whole `fingerprint.v1` block, when null (legacy 2012-era entries). Calls `ComputeFingerprintV1` directly; a fresh fingerprint contains every IdentityField so Tier C is moot for that entry.
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
