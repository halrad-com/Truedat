# Truedat — LLM Session Context

This file is loaded automatically by Claude Code / Claude Agent sessions scoped to this repo. Keep it short and decision-making-focused; deep reference material belongs in `README.md`, `SBOM.md`, and `docs/`.

## What this project is

Truedat is a Windows .NET command-line tool that analyses a music library's audio and writes `mbxmoods.json` (mood + Essentia features), optionally `mbxhub-fingerprints.json` (Chromaprint + audio MD5), and `mbxhub-details.json` (ffprobe stream details). Output files are consumed by MBXHub's AutoQ engine (separate repo).

- Build: `build-truedat.cmd` (requires .NET SDK 8.0+). Single-file output at `dist/truedat/truedat.exe` (~1 MB, ILRepack-merged).
- Runtime deps: `essentia_streaming_extractor_music.exe` (required for default scan), plus optional `ffmpeg.exe` (multi-channel downmix, `Unsupported codec` retry — e.g. `.opus`, since this essentia build lacks libopus — and the standalone `--transcode` utility) and `ffprobe.exe` (audio-property probe for `--details` and for matching source rate/depth in `--transcode`). `essentia_streaming_md5.exe` and `fpcalc.exe` are **legacy-mode only** — used by `--fingerprint`, `--md5-only`, `--quick-fingerprint`. Default scan no longer runs them.
- Framework: **.NET Framework 4.8**. No .NET 6/8 APIs, no `ValueTask`, no `init` setters on public types. Use `System.Text.Json` (merged via ILRepack).

## mbxmoods.json schema (current)

Per-track JSON object under `"tracks"[path]`. **55 numeric feature fields** (15 core + 40 extended, all extended are nullable — missing means the key is omitted, not written as `null`), plus metadata/identity fields.

- **Core features** (always present): `bpm`, `key`, `mode`, `spectralCentroid`, `spectralFlux`, `loudness`, `danceability`, `onsetRate`, `zeroCrossingRate`, `spectralRms`, `spectralFlatness`, `dissonance`, `pitchSalience`, `chordsChangesRate`, `mfcc[]`.
- **Extended** (nullable, omit-when-missing): `dynamicRange` + `dynamicRangeSource`; loudness envelope (`loudnessMomentary`, `loudnessShortTerm`, `replayGain`); silence (`silenceRate20dB/30dB/60dB`); spectral shape (rolloff, complexity, entropy, kurtosis, skewness, spread, strongPeak, decrease, energy + 4 energybands); `hfc`; Bark/ERB/Mel band stats (crest, flatness, kurtosis, skewness, spread × 3 scales = 15); rhythm/tonal (`beatsLoudness`, `chordsStrength`, `hpcpCrest`, `hpcpEntropy`).
- **Identity** (nullable, omit-when-missing): `fileMd5`, `audioMd5`, `fingerprint.v1` (composite, written via `WriteFingerprintV1` — fields: `fileSize`, `pathTail`, `durationMs`, `sampleRate`, `channels`, `bitDepth` (omit-when-0), `codec`, `codecRaw`, `bitrate`, `encoder` (omit-when-empty), `encoderRaw` (omit-when-empty), `mp3LameTag` (Phase 2 nested object, populated when codec=mp3 and the file has a Xing/Info+LAME header — fields: `version`, `vbrMethodCode`, `vbrMethod`, `lowpassHz`, `encoderDelay`, `encoderPadding`, `musicCrc`, `infoTagRevision`), `audioHead64kMd5` + source flag), `audioStreamSha256` (ride-along in default Essentia mode via `ComputeAudioStreamSha256FromFile`; emitted in all three scan-path modes — MoodsMode, `--file-list`, `--analyze-file` — plus `--hash-only --level stream`).
- **Housekeeping**: `lastModified`, `analysisDuration`.

Four I/O surfaces must stay in sync: `AnalyzeWithEssentiaCore` (extract), `WriteTrackEntry` (write), `LoadExistingMoods` (read via `ParseTrackFeaturesFromJson`), and the cross-MD5 cache-reuse branch in `MoodsMode`. Adding a field means touching all four.

## Rounding convention (extended features)

`Opt(v, int dp = 4)` / `OptN(root, path, int dp = 4)` — default 4 dp. Overrides:

- 2 dp: dB/LU values (`loudnessMomentary`, `loudnessShortTerm`, `replayGain`, `beatsLoudness`, `hpcpCrest`, `hfc`), `spectralComplexity`.
- 1 dp: Hz (`spectralRolloff`).
- 6 dp: tiny spectral values (`spectralDecrease`, `spectralEnergy*`).

Don't regress to a uniform 6 dp — it inflates JSON size without analytic value.

## Concurrent hashing in mood analysis

Each worker runs Essentia + `ComputeFileMd5` + `ComputeFingerprintV1` + `ComputeAudioStreamSha256FromFile` concurrently via `Task.Run` + `Task.WaitAll`. Wall-clock is ~`max(analysis, slowest-hash)` per track. All hashes are pure-managed (no subprocess), so there's no tool-availability gate and no path-escape exposure. The `audioStreamSha256` task hashes the TagLib invariant audio region with SHA-256 — content-stable (tag edits don't change it), portable (managed `FileStream` over Unicode path), fast (SHA-NI hardware accel).

The legacy `essentia_streaming_md5.exe` and `fpcalc.exe` subprocesses are **no longer in the default codepath**. They're invoked only by `--fingerprint`, `--md5-only`, and `--quick-fingerprint`, where they still run via `RunTool` (with the `SafePath`/`TryCreateHardlink` path-escape fallback).

## Hash-only mode (offline NDJSON manifest)

`--hash-only --level fingerprint|stream --file-list <paths.txt> --output <manifest.ndjson>` runs identity-only passes without Essentia and appends one envelope per file to an NDJSON manifest. Used by the determinism rig at `tools/verify-audiosha-determinism.ps1`.

- `fingerprint`: TagLib parse + 64 KB MD5 at `InvariantStartPosition`. Sub-10 ms warm. Envelope carries the `fingerprint.v1` composite (pathTail + fileSize + audio props + audioHead64kMd5).
- `stream`: streaming SHA-256 over `[InvariantStartPosition, InvariantEndPosition)`. Disk-bound. Superset — envelope carries `fingerprint.v1` *and* `audioStreamSha256`.

Envelope shape is defined by `BuildIdentityOnlyEnvelope` in `Program.cs`. It has no external consumers — when the rig and the format need to evolve, evolve them together.

## Cache hierarchy & re-extract gate

All four scan modes — `MoodsMode` (default iTunes-XML), `--folder`, `--file-list`, `--analyze-file` — check the cache in this order before falling through to Essentia:

1. **Path + mtime** (`allTracks[t.Location]`, `TruncateToSeconds` equality) — the fastest path. Returns `(cached)` / `[CACHED]`.
2. **Path + audioStreamSha256** (`(cached·sha)` / `[CACHED·sha]`) — when path matches but mtime drifted, recompute `audioStreamSha256` (~50ms managed SHA-NI). If it matches the cached value, audio bytes are unchanged (just tags). Reuse Essentia features, refresh `lastModified` + `fileMd5` + `fingerprint.v1` (the tag-affected fields).
3. **Cross-MD5** (`moodMd5Index`, `(cached·md5)` / `[CACHED·md5]`) — different path, byte-identical file (covers paths-changed-but-mtimes-preserved-on-copy and clean moves). Re-keys old → new path.
4. **Cross-SHA** (`moodShaIndex`, `(cached·sha)` / `[CACHED·sha]`) — different path AND tag-edited (file bytes differ but invariant audio region matches). Re-keys old → new path; recomputes `fileMd5` + `fingerprint.v1`.

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

`--verify --backfill` extends the same walker to populate missing identity fields in-place. **No Essentia re-run, no audio decode** — TagLib + cheap file IO only. Three tiers per entry, all gated by the SHA drift check (drifted entries become `REANALYZE_NEEDED` and are NOT modified):

- **Tier A** — entry-level: `audioStreamSha256` (computed if absent), `fileMd5` (computed if absent).
- **Tier B** — whole `fingerprint.v1` block, when null (legacy 2012-era entries). Calls `ComputeFingerprintV1` directly; a fresh fingerprint contains every IdentityField so Tier C is moot for that entry.
- **Tier C** — sub-fields inside an existing `fingerprint.v1`, driven by the `IdentityFields[]` spec list (one entry per field). Adding a future TagLib-readable identity field = one new `IdentityFieldSpec` + one new class field + matching read/write — same four-surfaces rule.
- **Tier C/Phase 2 — MP3 LAME tag (FileBytesShallow)**: when `codec=="mp3"` and the LAME-tag fields are unpopulated, `ApplyBackfillIdentity` invokes `Mp3LameTagParser.TryParse` (pure-managed, reads ~8 KB from file start, skips any ID3v2, finds first MPEG frame, locates Xing/Info magic, decodes the appended LAME tag). Off the `IdentityFields[]` list because it doesn't use TagLib — kept as a separate guarded branch to preserve the spec list's "TagLib-only" contract.

Backfill is idempotent (re-runs do zero IO) and atomic (single `SaveResults` at end, only when any entry actually changed). The `bitDepth` spec's `IsPresent` is codec-aware via `CodecLacksBitDepth` — lossy formats (mp3/aac/opus/vorbis/ogg/wma/mpc) count as "complete at 0" so backfill doesn't loop. Same pattern applies to any future field that's structurally absent for some codecs. CSV gains a fourth column `backfilledFields` listing which fields were filled per entry. Status set: OK / BACKFILLED / REANALYZE_NEEDED / MISSING / ERROR. See `docs/plans/2026-05-18-data-plumbing-phase1.md` for the per-entry decision loop, merge invariant, and Phase 2 hooks; `docs/plans/2026-05-18-data-plumbing-phase2.md` for the LAME tag spec, deferred items (`bitUsage`, `spectralCeilingHz`), and Phase 3 scope.

## Conventions

- **Offline-first.** No runtime network calls. No CDN-fetched assets, no cloud dependencies. Truedat reads files, runs subprocess tools, writes files.
- **No new runtime dependencies** without discussion. `System.Text.Json` and `TagLibSharp` are the only NuGet refs; both merged into the single exe via ILRepack.
- **Never push.** Never change repo visibility. Never mark project status as "Production Ready" / "Stable" / etc. — those are user-only decisions.
- **Never add Co-Authored-By lines to commits.** This is a hard rule in the user's global CLAUDE.md.
- **Commit convention:** `feat(scope):`, `fix(scope):`, `docs:`, `build:` (for exe rebuilds). Keep messages short; body explains *why* not *what*.
- **Don't rebuild `truedat.exe`** unless the user asks — the solo-dev workflow handles rebuilds in separate "build: update truedat.exe" commits.
- **Local code reviews go to `docs/reviews/YYYY-MM-DD-<topic>.md`**, not console. Short console summary is fine.
- **Implementation plans go to `docs/plans/YYYY-MM-DD-<topic>.md`** for non-trivial features.

## Things that aren't what they look like

- `ITunesParser.cs` reads iTunes `Music Library.xml` — that's the library input source, even though the output goes to MBXHub.
- `PathSanitizer.cs` is for the synthetic-library generation path (`--synthesize`), not the scan path.
- `essentia-build/` is a WSL2/MinGW cross-compile environment; don't try to run its scripts from PowerShell.
- `ReferenceCode/` (sibling repo) has legacy MBX code — read-only, don't modify.

## When in doubt

Read `docs/plans/` and `docs/reviews/` before touching the scan pipeline. Every non-trivial change in recent history has a plan or review doc capturing the design rationale.
