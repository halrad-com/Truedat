# Truedat — LLM Session Context

This file is loaded automatically by Claude Code / Claude Agent sessions scoped to this repo. Keep it short and decision-making-focused; deep reference material belongs in `README.md`, `SBOM.md`, and `docs/`.

## What this project is

Truedat is a Windows .NET command-line tool that analyses a music library's audio and writes `mbxmoods.json` (mood + Essentia features), optionally `mbxhub-fingerprints.json` (Chromaprint + audio MD5), and `mbxhub-details.json` (ffprobe stream details). Output files are consumed by MBXHub's AutoQ engine (separate repo).

- Build: `build-all.cmd` (requires .NET SDK 8.0+). Single-file output at `dist/truedat/truedat.exe` (~1 MB, ILRepack-merged).
- Runtime deps: `essentia_streaming_extractor_music.exe` (required), plus optional `essentia_streaming_md5.exe`, `fpcalc.exe`, `ffmpeg.exe`, `ffprobe.exe` — all placed alongside the exe.
- Framework: **.NET Framework 4.8**. No .NET 6/8 APIs, no `ValueTask`, no `init` setters on public types. Use `System.Text.Json` (merged via ILRepack).

## mbxmoods.json schema (current)

Per-track JSON object under `"tracks"[path]`. **55 numeric feature fields** (15 core + 40 extended, all extended are nullable — missing means the key is omitted, not written as `null`), plus metadata/identity fields.

- **Core features** (always present): `bpm`, `key`, `mode`, `spectralCentroid`, `spectralFlux`, `loudness`, `danceability`, `onsetRate`, `zeroCrossingRate`, `spectralRms`, `spectralFlatness`, `dissonance`, `pitchSalience`, `chordsChangesRate`, `mfcc[]`.
- **Extended** (nullable, omit-when-missing): `dynamicRange` + `dynamicRangeSource`; loudness envelope (`loudnessMomentary`, `loudnessShortTerm`, `replayGain`); silence (`silenceRate20dB/30dB/60dB`); spectral shape (rolloff, complexity, entropy, kurtosis, skewness, spread, strongPeak, decrease, energy + 4 energybands); `hfc`; Bark/ERB/Mel band stats (crest, flatness, kurtosis, skewness, spread × 3 scales = 15); rhythm/tonal (`beatsLoudness`, `chordsStrength`, `hpcpCrest`, `hpcpEntropy`).
- **Identity** (nullable, omit-when-missing): `fileMd5`, `audioMd5`. Also posted to MetaServer but not persisted in `mbxmoods.json`: `fingerprint.v1` (composite) and, in `--hash-only --level stream` only, `audioStreamSha256`.
- **Housekeeping**: `lastModified`, `analysisDuration`.

All five I/O surfaces must stay in sync: `AnalyzeWithEssentiaCore` (extract), `WriteTrackEntry` (write), `LoadExistingMoods` (read), `PostToMetaServer` (identity + features body), and both cache-reuse branches (`MoodsMode` path-cache around `Program.cs:1140` and cross-MD5 around `:1227`). Adding a field means touching all five.

## Rounding convention (extended features)

`Opt(v, int dp = 4)` / `OptN(root, path, int dp = 4)` — default 4 dp. Overrides:

- 2 dp: dB/LU values (`loudnessMomentary`, `loudnessShortTerm`, `replayGain`, `beatsLoudness`, `hpcpCrest`, `hfc`), `spectralComplexity`.
- 1 dp: Hz (`spectralRolloff`).
- 6 dp: tiny spectral values (`spectralDecrease`, `spectralEnergy*`).

Don't regress to a uniform 6 dp — it inflates JSON size without analytic value.

## Concurrent hashing in mood analysis

Each worker runs Essentia + `ComputeFileMd5` + `RunMd5` + `RunFpcalc` + `ComputeFingerprintV1` concurrently via `Task.Run` + `Task.WaitAll`. Wall-clock is ~`max(analysis, slowest-hash)` per track. When any hash tool is absent, the corresponding task returns an empty result and the field stays `null` — don't add fallback logic. `ComputeFingerprintV1` (~5 ms warm) is a pure-managed task so it has no tool-availability gate.

## Phase 2 hash-only mode

`--hash-only --level fingerprint|stream --file-list <path> --meta-server <url>` runs identity-only passes without Essentia:

- `fingerprint`: TagLib parse + 64 KB MD5 at `InvariantStartPosition`. Sub-10 ms warm. Posts `identity.fingerprint.v1` composite (pathTail + fileSize + audio props + audioHead64kMd5). This is the ms-scale peer-pull ping primitive.
- `stream`: streaming SHA-256 over `[InvariantStartPosition, InvariantEndPosition)`. Disk-bound. Superset — emits `fingerprint.v1` *and* `audioStreamSha256`.

Wire contract frozen at `docs/reference/identity-wire-format.md` (consumed by the MetaServer side, Phase 2 Track B). `PathTail` convention matches MetaServer's `GetPathTail` byte-for-byte.

## Cache re-extract gate

Path-cache and cross-MD5 cache branches re-extract (skip cache reuse) when any of these are missing:

- `DynamicRange` — pre-LRA builds.
- `LoudnessMomentary` — pre-extended-feature builds (canary for the 40-extended set).
- `AudioMd5` **only when** `md5Exe != null` — otherwise every track re-extracts forever on installs without the MD5 tool.

When adding a new always-extracted field, decide whether to add it to the canary. If yes, guard on the tool availability the way `audioMd5` does.

## Conventions

- **Offline-first.** No runtime network calls except the optional `--meta-server` POST. No CDN-fetched assets, no cloud dependencies.
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
