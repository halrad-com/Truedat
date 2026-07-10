# Truedat - Music Mood Extractor & Fingerprinter

Truedat is a Windows .NET CLI that extracts **per-track mood** across a music library — the signal MBXHub's AutoQ engine uses for **mood-aware shuffle** — and writes it (alongside identity, authenticity, and metadata) into `mbxmoods.json` that MBXHub reads directly.

**Mood is the core job, and truedat captures it two independent ways:**

- **Essentia** — when [Essentia](https://essentia.upf.edu/) is present, truedat calls it to extract 55 acoustic features per track plus a valence/arousal mapping. The primary mood read. truedat provides a custom x64 build (see [Essentia Builds](#essentia-builds)) that handles large files better, but doesn't bundle it — it's a separate AGPL tool, invoked if found.
- **SMFM (Sony 12 TONE / SensMe)** — truedat doesn't add this; it reads Sony's own analysis when it's already embedded in a file — the SMFM block — yielding 10 STMO mood scores + BPM (the `smfm*` fields). MBXHub projects these to a **second (valence, arousal) opinion** on the same AutoQ mood map — an independent take alongside Essentia, most useful exactly where the two disagree.

Everything below is built **on top of** that mood signal — identity, authenticity, and library-scale plumbing:

- **Composite track identity** — `fingerprint.v1` (file size + path tail + audio properties + 64 KB head MD5 + MP3 LAME tag block), `audioStreamSha256` (SHA-256 over TagLib's invariant audio region; cross-machine portable, tag-edit resilient), and traditional file/audio MD5.
- **Authenticity signals** — `bitUsage` (ffmpeg-driven LSB walk that catches fake-hi-res 24-bit padding), `hfEnergyRatio` (bin-sharp energy above 22.05 kHz via managed FFT), `hfSpectralStructure` (flatness / peak-to-mean / mirror-imaging from the same FFT pass — catches ffmpeg-upsampled fakes the bit-level signals miss), MP3 LAME-tag parsing (pure-managed Xing/Info+LAME decoder for transcode detection).
- **`truedat.*` verdict block** — multi-signal voted hi-res / lossy-transcode verdicts (`yes` / `no` / `unknown` / `n/a`) computed inline at write time, so threshold changes ship without rescanning.
- **Library-scale operations** — incremental & cache-aware scanning across four cache tiers (path+mtime, path+sha, cross-md5, cross-sha), `--verify` / `--verify --backfill` (identity + features tiers), `--merge-moods`, deterministic multi-machine `--chunk M/N`, `--hash-only` NDJSON manifest mode, opus auto-retry, and a standalone `--transcode` utility.

The output (`mbxmoods.json`) carries all of this in a single per-track object that MBXHub reads directly.

Minor utility modes (`--synthesize`, `--seed-moods`) cover synthetic-library generation and AcousticBrainz-driven mood seeding for scale-testing MBXHub — see [Test Tooling](#test-tooling) at the bottom of this doc.

**Output:**

- `mbxmoods.json` - mood coordinates and raw audio features for every track
- `mbxhub-fingerprints.json` - Chromaprint perceptual fingerprints and audio MD5 hashes
- `mbxhub-details.json` - audio metadata from ffprobe (codec, bitrate, sample rate, etc.)
- `mbxmoods-errors.csv` - tracks that failed mood analysis (with error reason, file size, duration)
- `mbxmoods-skipped.csv` - tracks skipped at scan entry because the codec isn't supported (currently `.dsf` / `.dff` / `.dsd` DSD streams). Columns: `path,extension,reason,timestamp`.
- `mbxmoods-verify.csv` - per-entry status from `--verify` / `--verify --backfill` (OK / DRIFT / MISSING / NO_HASH / BACKFILLED / REANALYZE_NEEDED / ERROR, plus the list of fields filled per entry)
- `mbxhub-fingerprints-errors.csv` - tracks that failed fingerprinting (`--fingerprint` mode)
- `truedat.log` - full console output for diagnostics (when `--audit` is used)

## Download

[**truedat.zip**](https://halrad.com/truedat/truedat.zip) — full self-contained bundle (`truedat.exe` + Essentia + ffmpeg). Runs offline, no install. Unzip anywhere and run `truedat.exe`.

What's inside and what happens if you drop pieces: [dependency-surface notes](https://halrad.com/truedat/2026-05-23-dependency-surface.md).

Source tree and tagged snapshots are on [GitHub](https://github.com/halrad-com/Truedat).

## What It Does

Truedat reads an iTunes Music Library XML file (or a `--folder`, `--file-list`, or single `--analyze-file`) and runs each track through Essentia plus its own analyses. Per-track work is parallelized across cores; everything is cache-aware so re-runs only touch what changed.

### Default scan (analysis + identity + authenticity)

A single default invocation produces the full per-track record:

- **Mood features (Essentia)** — Valence (0-1, sad ↔ happy; 8 input features), Arousal (0-1, calm ↔ energetic; 7 input features), and 15 core + 40 extended Essentia features stored per track for runtime recomputation (extended set covers loudness envelope, silence profile, spectral shape, psychoacoustic bands, rhythm/tonal aggregates).
- **Identity (truedat-native, pure-managed)** — `fingerprint.v1` composite, `audioStreamSha256` (TagLib invariant audio region), `fileMd5`. All computed concurrently with the Essentia decode so they're effectively free.
- **Authenticity signals (truedat-native, ffmpeg-driven)** — `bitUsage` block (LSB walk for fake-hi-res detection on lossless 24-bit files), `hfEnergyRatio` + `hfSpectralStructure` (single managed-FFT pass over the same 30 s mid-track segment — bin-sharp HF energy ratio plus Wiener-entropy flatness, peak-to-mean, and mid↔HF mirror correlation; catches ffmpeg-upsampled fakes the bit-level signals miss), MP3 LAME-tag parsing (Xing/Info+LAME header decode for transcode detection).
- **`truedat.*` verdict block** — multi-signal voted hi-res / lossy-transcode verdicts computed inline at write time from the signals above. Threshold changes ship without rescanning.

MBXHub consumes the whole record: mood features drive AutoQ vibe selection; identity drives cross-cache matching; authenticity drives the hi-res / transcode classifiers.

### Fingerprint mode (`--fingerprint`) — legacy

The original identity pipeline, still available for compatibility:

- **Chromaprint** — Perceptual audio fingerprint (AcoustID). Identifies the same *song* regardless of encoding, bitrate, or format.
- **Audio MD5** — Hash of raw decoded audio data (ignores metadata tags). Identifies the exact same *audio data*.

The default scan's `fingerprint.v1` + `audioStreamSha256` have superseded this for new work; `--fingerprint` is kept for libraries that still depend on `mbxhub-fingerprints.json`.

### Verify & backfill (`--verify`, `--verify --backfill`)

Walks an existing `mbxmoods.json` and either reports drift (read-only) or repairs missing fields in place (no Essentia re-run). Two backfill tiers — identity (TagLib + cheap IO) and features (ffmpeg-driven `bitUsage` / `hfEnergyRatio` / `hfSpectralStructure`) — both gated by an SHA drift check so drifted entries are flagged for re-analysis rather than touched. See [Verify & Backfill](#large-libraries) for tier scoping via `--backfill-level`.

### Filename Check (`--check-filenames`)

Scans your library for filenames with characters that cause Essentia tools to fail. Reports three tiers:

- **Errors** — Fullwidth Unicode substitution characters (e.g. `⧸` `：` `＂`) that are known to break Essentia's ANSI argv parsing. These files will always fail analysis.
- **Warnings** — Other non-ASCII characters where 8.3 short path fallback is unavailable. These files may fail depending on system configuration.
- **Suspects** — Audio files under 50 KB that may be corrupt or truncated.

## Quick Start

```cmd
REM Mood analysis (default)
truedat.exe "iTunes Music Library.xml"

REM Fingerprint mode
truedat.exe "iTunes Music Library.xml" --fingerprint
```

Output: `mbxmoods.json` / `mbxhub-fingerprints.json` (next to the XML file)

**Auto-discovery:** if you omit the positional XML arg, truedat probes (first hit wins): `<exe-dir>\..\iTunes Music Library.xml` (the install-parent case — drop the truedat folder under your library directory and it just works), then `<exe-dir>\iTunes Music Library.xml`, then `.\iTunes Music Library.xml` (cwd). The error message lists the probed locations so a no-hit failure is self-diagnosing.

### Options

```
truedat.exe <path-to-iTunes-Music-Library.xml> [options]

  -p, --parallel <N>      Number of parallel threads (default: all cores)
  --fixup                 Validate and remap paths in mbxmoods.json without re-analyzing.
                          With --remap, performs a pure prefix swap (see --remap below).
  --remap <old>=<new>     With --fixup: wholesale prefix swap on mbxmoods.json keys.
                          Pass the moods file as the positional arg (no iTunes XML
                          needed). Case-insensitive prefix match; entries that don't
                          start with the old prefix pass through unchanged. Writes a
                          .bak.<timestamp> before atomic replace. Example:
                          truedat --fixup --remap "D:\Music\=\\nas\share\Music\" mbxmoods.json
  --verify                Recompute audioStreamSha256 per entry, report drift / missing
                          (read-only; writes mbxmoods-verify.csv next to the moods file)
  --verify --backfill     Fill in missing fields for entries whose audio bytes are
                          unchanged. Drifted entries are flagged as REANALYZE_NEEDED,
                          never modified. No Essentia re-run. Idempotent. Two tiers,
                          both run by default — scope down with --backfill-level:
                            identity tier (TagLib + cheap file IO; fast):
                              audioStreamSha256, fileMd5, whole fingerprint.v1, sub-fields
                              (bitDepth, encoder, ...), and the MP3 LAME tag block for
                              codec=mp3 entries.
                            features tier (ffmpeg-driven; slow — ~30s of decode per
                              applicable track):
                              bitUsage (lossless ∧ bitDepth >= 24 only),
                              hfEnergyRatio + hfSpectralStructure (single FFT pass;
                              sourceSampleRate > 44.1k only). Silently skipped when
                              ffmpeg is absent.
  --backfill-level <name> With --verify --backfill: scope which tier runs.
                            all       (default) identity + features
                            identity  fast tier only (TagLib + cheap file IO)
                            features  ffmpeg tier only (bitUsage / hfEnergyRatio / hfSpectralStructure)
  --duplicates [path]     Read-only duplicate-audio report over mbxmoods.json: exact groups
                          (byte-identical audioStreamSha256) plus probable cross-encode
                          candidates (quantized feature match), each with a recommended
                          keeper (lossless > bitDepth > sampleRate > bitrate > SMFM-tagged >
                          size > shortest path). Writes mbxmoods-duplicates.csv + .json;
                          per member: codec/bitrate/sampleRate/bitDepth/album + an smfm flag.
  --losers-m3u [path]     With --duplicates: write non-keeper members to an .m3u8 playlist
                          for review/removal inside MusicBee. Path must end in .m3u or .m3u8;
                          default is mbxmoods-duplicate-losers.m3u8 next to the moods file.
  --html [path]           With --duplicates: write a self-contained interactive review page
                          (offline, no server). Include duplicate groups in chunks, confirm
                          keepers, click Build losers playlist to download the .m3u8. Default
                          mbxmoods-duplicates.html next to the moods file.
  --manifest [path]       With --duplicates: emit the kind:dupes review-surface manifest that
                          MBXHub's review.html renders. No path auto-locates the running
                          MusicBee instance's <root>\AppData\MBXHub\review\dupes.json.
  --chunk M/N             Split scan across machines via deterministic hash-mod assignment
                          (output auto-suffixed: mbxmoods.<hostname>.json; combine via --merge-moods)
  --retry-errors          Re-attempt all previously failed files (clears error log)
  --migrate               Clean up mbxmoods.json: strip legacy valence/arousal fields,
                          rename SMFM keys (sensme*->smfm*), remove podcast entries (creates backup)
  --fingerprint           Run fingerprint mode (chromaprint + md5) → mbxhub-fingerprints.json
  --chromaprint-only      Fingerprint mode: only run chromaprint (skip md5)
  --md5-only              Fingerprint mode: only run audio md5 (skip chromaprint)
  --details               Use ffprobe → mbxhub-details.json (implies --fingerprint)
  --output <path>         --hash-only mode: append identity envelopes as NDJSON to <path>
  --hash-only             Identity-only mode (no Essentia). Requires --level, --file-list, --output
  --level <name>          With --hash-only: 'fingerprint' (cheap composite) or 'stream' (durable SHA-256)
  --audit                 Write all console output to truedat.log (for debugging)
  --self-test             Run inline FFT sanity checks and exit (no library scan)
  --analyze-file <path> Analyze a single audio file with Essentia (no iTunes XML needed)
  --file-list <path>    Analyze files listed in a text file (one path per line, UTF-8, # comments)
                        Mutually exclusive with --analyze-file; -p sets parallelism
  --check-filenames       Scan for filenames with characters that break Essentia tools
  --transcode <input>     Standalone: ffmpeg-transcode <input> to uncompressed FLAC.
                          Requires --transcode-out. Mutually exclusive with all scan modes.
  --transcode-out <path>  Output FLAC path for --transcode mode.
  --sample-rate <hz>      With --transcode: override output sample rate (default: match source).
  --bit-depth <16|24>     With --transcode: override output bit depth (default: match source).
  --no-stage              Disable source staging (UNC, mapped network drives, non-ASCII paths);
                          workers read source directly.
  --stage-dir <path>      Override staging dir (default %TEMP%\.truedat-stage).
  --max-duration <secs>   Max track length for Essentia analysis (default 12000 = 200 min —
                          the stock extractor's ChordsDetection buffer limit). Longer tracks
                          are skipped and logged. Raise only when running an extractor built
                          with the larger buffer.
  --no-quick-cache        Disable the tags-only quick cache tier (head-64k check);
                          mtime-drifted files always take the full audio-hash check.
  --no-bitusage           Suppress ComputeBitUsage (omits the bitUsage JSON block).
  --no-hf-analysis        Suppress ComputeHfAnalysis (omits hfEnergyRatio + hfSpectralStructure).
```

**After a mass tag edit:** rewriting tags across the library changes every file's mtime without touching audio. Truedat detects this per file at ~64 KB/track (a quick head-hash check) instead of re-reading each file in full, so a full-library rescan after a retag pass finishes in a fraction of the time and re-runs zero analysis. `--no-quick-cache` forces the full per-file audio-hash check instead, and `--verify` remains the full-integrity check against the durable `audioStreamSha256`.

**Network libraries:** when the source is on a UNC share (`\\server\share\…`), a mapped network drive (e.g. `Z:\` mapped to `\\server\share`), or a local path with non-ASCII characters, truedat stages each file once to a local temp copy and runs the 8-9 concurrent per-track workers (and the cache hierarchy's tier-2/3/4 body reads) against the local copy. Net: 1× full network read per track instead of ~3× full + ≥3× partial. Cache tier-1 (path + mtime equality) doesn't stage — already-cached tracks stay free. Local-ASCII paths read directly. Use `--no-stage` to opt out, or `--stage-dir` to relocate the staging directory (e.g. to a fast scratch volume when `%TEMP%` is on a small SSD). Per-track stage failures fall back to direct read with a one-line warning — scans never abort over a staging hiccup. End-of-scan summary reports `staging: N staged` (or `N staged, M direct-fallback`), with a stderr warning when >5% of attempted stages fell back so a wedged stage-dir is visible.

**Optional:** Place `ffmpeg.exe` and `ffprobe.exe` alongside `truedat.exe` (or on PATH) to enable:
- Auto-downmix of multi-channel (5.1+) audio files during scans (without ffmpeg, multi-channel files are skipped with a warning).
- Auto-retry of files essentia can't decode natively — e.g. `.opus`, which this essentia build lacks. The file is transcoded to a stereo WAV and essentia is re-run against it, all transparently.
- `--details` probe mode.
- Standalone `--transcode` mode for converting opus/etc. to uncompressed FLAC.

### Large Libraries

For large libraries (50K+ tracks), expect multi-day scans for mood analysis. Fingerprinting is much faster. Both modes are designed for this:

- **Incremental** - Skips tracks already processed (by file path + last-modified timestamp).
- **Tag-edit resilient** - When mtime drifts but the audio bytes are unchanged (e.g. tag editor rewrote a frame), the cache reuses Essentia features by recomputing only `audioStreamSha256` (~50ms managed SHA per file) and refreshing the tag-affected identity fields. No full re-extraction.
- **Cross-path resilient** - File moved or renamed? Cross-MD5 / cross-SHA fallbacks re-key the cached entry to the new path without re-analyzing.
- **Multi-machine chunking** - Two boxes pointed at the same library run `--chunk 1/2` and `--chunk 2/2` and produce hostname-suffixed shards (`mbxmoods.<host>.json`); merge later with `--merge-moods`. Hash-mod assignment means iTunes XMLs need not be identical between machines.
- **Resumable** - Stop and restart anytime. Progress is saved every 25 analyzed tracks.
- **Verifiable** - `truedat --verify` walks the moods file and confirms each entry's `audioStreamSha256` still matches the disk. Detail goes to `mbxmoods-verify.csv`; exit 1 on any drift / missing / error makes it CI-friendly. Add `--backfill` to repair missing fields in place without re-running Essentia. Two tiers run by default: identity (audioStreamSha256 / fileMd5 / fingerprint.v1 / bitDepth / encoder / MP3 LAME tag — TagLib-driven, fast) and features (bitUsage / hfEnergyRatio / hfSpectralStructure — ffmpeg-driven, ~30s per applicable lossless 24-bit track). Use `--backfill-level identity` to skip the slow ffmpeg tier on a first pass, or `--backfill-level features` to fill only the ffmpeg-tier fields on a library whose identity is already complete. All tiers are gated by the SHA drift check, so drifted entries are flagged as `REANALYZE_NEEDED` rather than touched.
- **ETA tracking** - Shows per-track rate and estimated completion time.
- **Error resilience** - Failed tracks logged to errors CSV, skipped on retry.

```cmd
REM First run - analyzes everything
truedat.exe "iTunes Music Library.xml" -p 4

REM Resume after interruption - picks up where it left off
truedat.exe "iTunes Music Library.xml" -p 4

REM Fix path separators without re-analyzing (e.g., after moving files)
truedat.exe "iTunes Music Library.xml" --fixup

REM Re-key mbxmoods.json from one root to another (e.g., scanned local copy of
REM a NAS mirror, need entries keyed by the UNC path). No iTunes XML needed.
truedat.exe --fixup --remap "D:\Music\=\\nas\share\Music\" mbxmoods.json

REM Generate fingerprints for the whole library
truedat.exe "iTunes Music Library.xml" --fingerprint

REM Only chromaprint (e.g., for duplicate detection)
truedat.exe "iTunes Music Library.xml" --chromaprint-only

REM Check for problematic filenames before scanning
truedat.exe "iTunes Music Library.xml" --check-filenames

REM Probe audio details (codec, bitrate, sample rate, etc.)
truedat.exe "iTunes Music Library.xml" --details

REM Analyze a single file (no iTunes XML needed)
truedat.exe --analyze-file "C:\Music\song.mp3" --json-output

REM Batch analyze files from a list, write entries to a moods file
truedat.exe --file-list files.txt --moods C:\Music\mbxmoods.json -p 4

REM Hash-only: cheap composite fingerprint, append to NDJSON manifest (ms per file)
truedat.exe --hash-only --level fingerprint --file-list files.txt --output manifest.ndjson -p 32

REM Hash-only: durable audioStreamSha256 (disk-bound; emits fingerprint.v1 too)
truedat.exe --hash-only --level stream --file-list files.txt --output manifest.ndjson -p 8

REM Verify the cache against disk (read-only — recomputes audioStreamSha256 per entry)
truedat.exe --verify --moods C:\Music\mbxmoods.json

REM Backfill ALL missing fields — identity (TagLib, fast) + features (ffmpeg, slow).
REM No Essentia re-run; drifted entries are flagged, not modified.
truedat.exe --verify --backfill --moods C:\Music\mbxmoods.json

REM Identity tier only — fast first pass that doesn't need ffmpeg
truedat.exe --verify --backfill --backfill-level identity --moods C:\Music\mbxmoods.json

REM Features tier only — fill ffmpeg-driven bitUsage / hfEnergyRatio / hfSpectralStructure on a library
REM whose identity is already complete (e.g., after the identity-only pass above)
truedat.exe --verify --backfill --backfill-level features --moods C:\Music\mbxmoods.json

REM Transcode opus (or any ffmpeg-readable input) to uncompressed FLAC at source rate/depth
truedat.exe --transcode "C:\Music\track.opus" --transcode-out "C:\Music\track.flac"

REM Same, but force a specific output rate and bit depth
truedat.exe --transcode "C:\Music\track.opus" --transcode-out "C:\Music\track.flac" --sample-rate 44100 --bit-depth 16

REM Find and clean duplicate audio
truedat.exe --duplicates "D:\MusicBee\Library" --losers-m3u
REM Review the report (exact = byte-identical, safe calls; probable = feature-match
REM candidates, confirm before acting), then load mbxmoods-duplicate-losers.m3u8 in
REM MusicBee to review and delete the redundant copies from inside the player.

REM Two-machine same-library scan: each box does its own deterministic shard
truedat.exe "iTunes Music Library.xml" --chunk 1/2     REM machine A
truedat.exe "iTunes Music Library.xml" --chunk 2/2     REM machine B
truedat.exe --merge-moods --merge-source mbxmoods.machineA.json --merge-source mbxmoods.machineB.json --merge-output mbxmoods.json
```

## Test Tooling

Two minor utility modes that exist to exercise MBXHub at scale or bootstrap mood data without running Essentia. Both depend on a catalog file built from [AcousticBrainz](https://acousticbrainz.org/) + [MusicBrainz](https://musicbrainz.org/) data dumps (`data/synthlib-catalog.jsonl.gz`). Build it once via `src/catalog-prep.py` (~21 GB of one-time downloads; see the script's docstring for the exact invocation).

### `--synthesize` — synthetic library

Generates stub MP3s (~12 KB each, 3 s of silence) with real ID3 metadata from MusicBrainz, organized as `{output}/{Artist}/{Album}/{NN} {Title}.mp3`. Used to scale-test MBXHub's AutoQ against 100k–500k tracks before real users hit those numbers. Every synthetic track is tagged with `Grouping = Synthetic` and `Comment = SYNTH:{seed}:{mbid}` for easy filtering. Add the output folder to MusicBee as a monitored library; remove it when done.

```cmd
REM Preview
truedat.exe --synthesize --catalog data\synthlib-catalog.jsonl.gz --count 100 --dry-run

REM Generate 100k tracks and merge their mood data into an existing moods file
truedat.exe --synthesize --catalog data\synthlib-catalog.jsonl.gz --synth-output D:\synthlib --count 100000 --synth-moods C:\Music\mbxmoods.json
```

Flags: `--synthesize`, `--catalog <path>`, `--synth-output <dir>`, `--count <n>` (default 430000), `--album-ratio <r>` (default 0.5), `--synth-moods <path>`, `--seed <n>` (default 42), `--dry-run`.

### `--seed-moods` — mood seeding from AcousticBrainz

Populates `mbxmoods.json` with pre-computed acoustic features matched by normalized artist+title — instant mood data for matched tracks without running Essentia. Seeded entries carry `_confidence: 0.6` / `_source: "ab-metadata"`; local Essentia analysis (`_confidence: 1.0`) is never overwritten.

```cmd
truedat.exe "iTunes Music Library.xml" --seed-moods --seed-catalog data\synthlib-catalog.jsonl.gz
```

Flags: `--seed-moods`, `--seed-catalog <path>`, `--seed-target <path>` (default: next to library XML).

## Installation

Place `truedat.exe` and the required tools in the same folder. No additional runtime needed on Windows 10+.

**Recommended layout** for use alongside MusicBee/iTunes: drop the truedat folder under your library directory (e.g. `<library>\truedat\`). The exe will auto-discover the iTunes XML one level up — no need to pass the XML path or `cd` anywhere first. Output (`mbxmoods.json` etc.) lands next to the XML.

### Dependencies

Truedat calls these tools as subprocesses. Place them alongside `truedat.exe` or on PATH.

| Tool | Enables | License | Source |
| ---- | ------- | ------- | ------ |
| [Essentia](https://essentia.upf.edu/) `essentia_streaming_extractor_music.exe` | Mood analysis (default mode) | AGPL-3.0 | [Essentia](https://github.com/MTG/essentia) / [x64 build](https://github.com/halrad-com/Truedat/tree/main/essentia-build/output-x64) / [dist](https://github.com/halrad-com/Truedat/tree/main/dist/truedat) |
| [Essentia](https://essentia.upf.edu/) `essentia_streaming_md5.exe` | `--fingerprint` and `--md5-only` modes only (decoded-PCM MD5). Not used by default mood scan. | AGPL-3.0 | [Essentia](https://github.com/MTG/essentia) / [x64 build](https://github.com/halrad-com/Truedat/tree/main/essentia-build/output-x64) / [dist](https://github.com/halrad-com/Truedat/tree/main/dist/truedat) |
| [Chromaprint](https://acoustid.org/chromaprint) `fpcalc.exe` | `--fingerprint` and `--quick-fingerprint` modes only (perceptual fingerprint). Not used by default mood scan. | LGPL-2.1+ | [AcoustID](https://acoustid.org/chromaprint) |
| [Essentia](https://essentia.upf.edu/) `essentia_standard_chromaprinter.exe` | `--fingerprint` mode (chromaprint persisted to `mbxhub-fingerprints.json`) | AGPL-3.0 | [Essentia](https://github.com/MTG/essentia) / [x64 build](https://github.com/halrad-com/Truedat/tree/main/essentia-build/output-x64) / [dist](https://github.com/halrad-com/Truedat/tree/main/dist/truedat) |
| [FFmpeg](https://ffmpeg.org/) `ffmpeg.exe` | Multi-channel audio downmix | GPL-3.0+ | [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) / [deps](https://github.com/halrad-com/Truedat/tree/truedat-deps) |
| [FFmpeg](https://ffmpeg.org/) `ffprobe.exe` | `--details` audio probing | GPL-3.0+ | [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) / [deps](https://github.com/halrad-com/Truedat/tree/truedat-deps) |

All tools are optional — truedat runs without them but the corresponding features are unavailable. Only install the tools for the modes you need. Bundle `essentia_streaming_md5.exe` and `fpcalc.exe` alongside `truedat.exe` so every analysis run emits the full identity tier set (`fileMd5` + `audioMd5` + `chromaprint`) without a separate `--fingerprint` pass. Custom x64 Essentia builds are in [`essentia-build/`](essentia-build/), ready to use from [`dist/truedat/`](https://github.com/halrad-com/Truedat/tree/main/dist/truedat).

### Essentia Builds

All Essentia tools are custom 64-bit builds from source. See [`essentia-build/`](essentia-build/) for build scripts and documentation. The x64 builds handle large files that exceed the 2 GB address space limit of 32-bit binaries.

### Building from Source

```cmd
build-truedat.cmd
```

Creates `dist/truedat/truedat.exe` (single file, ~1 MB). Requires .NET SDK 8.0+.

## Extracted Features

Truedat extracts 55 audio features per track from Essentia's output — 15 core features that feed valence/arousal, plus 40 extended descriptors that MBXHub persists for richer downstream scoring (sub-genre profiling, loudness normalisation, fingerprint-free clustering, etc.).

Extended features are emitted as nullable JSON fields — absent/NaN Essentia paths become omitted keys, not zeros. Older mbxmoods.json entries that pre-date the extended set round-trip cleanly as `null`s.

### Core — Arousal-related (energy/intensity)

| Feature            | Essentia Path                         | What It Measures                  |
| ------------------ | ------------------------------------- | --------------------------------- |
| BPM                | `rhythm.bpm`                          | Tempo in beats per minute         |
| Onset rate         | `rhythm.onset_rate`                   | Percussive events per second      |
| Loudness           | `lowlevel.loudness_ebu128.integrated` | Perceived loudness (EBU R128, dB) |
| Spectral flux      | `lowlevel.spectral_flux.mean`         | Rate of spectral change           |
| Spectral RMS       | `lowlevel.spectral_rms.mean`          | Raw energy level                  |
| Zero-crossing rate | `lowlevel.zerocrossingrate.mean`      | Noise/distortion indicator        |
| Danceability       | `rhythm.danceability`                 | Rhythmic regularity (0-1)         |

### Core — Valence-related (positivity/happiness)

| Feature            | Essentia Path                        | What It Measures                  |
| ------------------ | ------------------------------------ | --------------------------------- |
| Key                | `tonal.key_edma.key`                 | Musical key (C, D, E...)          |
| Mode               | `tonal.key_edma.scale`               | Major (bright) vs minor (dark)    |
| Spectral centroid  | `lowlevel.spectral_centroid.mean`    | Brightness/timbre (Hz)            |
| Spectral flatness  | `lowlevel.spectral_flatness_db.mean` | Tonal vs noise-like               |
| Dissonance         | `lowlevel.dissonance.mean`           | Harmonic tension                  |
| Pitch salience     | `lowlevel.pitch_salience.mean`       | Harmonic clarity (HNR proxy)      |
| Chord changes rate | `tonal.chords_changes_rate`          | Rate of harmonic movement         |
| MFCCs              | `lowlevel.mfcc.mean`                 | 13-coefficient timbre fingerprint |

### Extended — Loudness envelope (EBU R128)

| JSON key              | Essentia Path                                 | What It Measures                                       |
| --------------------- | --------------------------------------------- | ------------------------------------------------------ |
| `dynamicRange`        | `lowlevel.loudness_ebu128.loudness_range`     | EBU R128 loudness range (LRA) in LU — quiet↔loud span  |
| `loudnessMomentary`   | `lowlevel.loudness_ebu128.momentary.mean`     | Mean momentary loudness (400 ms gate), LU              |
| `loudnessShortTerm`   | `lowlevel.loudness_ebu128.short_term.mean`    | Mean short-term loudness (3 s gate), LU                |
| `replayGain`          | `metadata.audio_properties.replay_gain`       | ReplayGain adjustment for normalised playback, dB      |

### Extended — Silence profile

Fraction of analysis frames whose RMS falls below the given threshold — higher = sparser / more silent content.

| JSON key           | Essentia Path                      | Threshold |
| ------------------ | ---------------------------------- | --------- |
| `silenceRate20dB`  | `lowlevel.silence_rate_20dB.mean`  | -20 dB    |
| `silenceRate30dB`  | `lowlevel.silence_rate_30dB.mean`  | -30 dB    |
| `silenceRate60dB`  | `lowlevel.silence_rate_60dB.mean`  | -60 dB    |

### Extended — Spectral shape

| JSON key                | Essentia Path                                    | What It Measures                                            |
| ----------------------- | ------------------------------------------------ | ----------------------------------------------------------- |
| `spectralRolloff`       | `lowlevel.spectral_rolloff.mean`                 | Hz below which 85% of spectral energy sits (bright↔dark)    |
| `spectralComplexity`    | `lowlevel.spectral_complexity.mean`              | Count of significant spectral peaks per frame               |
| `spectralEntropy`       | `lowlevel.spectral_entropy.mean`                 | Shannon entropy of normalised spectrum (noise↔tone)         |
| `spectralKurtosis`      | `lowlevel.spectral_kurtosis.mean`                | Peakedness of spectral distribution                         |
| `spectralSkewness`      | `lowlevel.spectral_skewness.mean`                | Asymmetry of spectral distribution                          |
| `spectralSpread`        | `lowlevel.spectral_spread.mean`                  | 2nd-moment spread around centroid                           |
| `spectralStrongPeak`    | `lowlevel.spectral_strongpeak.mean`              | Prominence of dominant spectral peak                        |
| `spectralDecrease`      | `lowlevel.spectral_decrease.mean`                | Average slope; negative ⇒ energy concentrated at low freqs  |
| `spectralEnergy`        | `lowlevel.spectral_energy.mean`                  | Total spectral energy                                       |
| `spectralEnergyLow`     | `lowlevel.spectral_energyband_low.mean`          | Energy in low band (Essentia split)                         |
| `spectralEnergyMidLow`  | `lowlevel.spectral_energyband_middle_low.mean`   | Energy in mid-low band                                      |
| `spectralEnergyMidHigh` | `lowlevel.spectral_energyband_middle_high.mean`  | Energy in mid-high band                                     |
| `spectralEnergyHigh`    | `lowlevel.spectral_energyband_high.mean`         | Energy in high band                                         |
| `hfc`                   | `lowlevel.hfc.mean`                              | High-frequency content — cymbals, sibilance, brightness     |

### Extended — Psychoacoustic bands

Shape statistics over perceptually-spaced filterbanks. Same five statistics across three scales: **Bark** (27 critical bands), **ERB** (40 equivalent-rectangular-bandwidth bands), **Mel** (40 mel-scaled bands).

| JSON key (per scale)       | Essentia Path                              | What It Measures                                           |
| -------------------------- | ------------------------------------------ | ---------------------------------------------------------- |
| `{bark,erb,mel}Crest`      | `lowlevel.{bark,erb,mel}bands_crest.mean`  | Peak-to-mean ratio — high ⇒ tonal/peaky within the band    |
| `{bark,erb,mel}Flatness`   | `lowlevel.{bark,erb,mel}bands_flatness_db.mean` | Geometric/arithmetic mean ratio in dB (noisy↔tonal)    |
| `{bark,erb,mel}Kurtosis`   | `lowlevel.{bark,erb,mel}bands_kurtosis.mean` | Peakedness of the band distribution                      |
| `{bark,erb,mel}Skewness`   | `lowlevel.{bark,erb,mel}bands_skewness.mean` | Asymmetry of the band distribution                       |
| `{bark,erb,mel}Spread`     | `lowlevel.{bark,erb,mel}bands_spread.mean`  | Second-moment spread across the bands                     |

### Extended — Rhythm & tonal aggregates

| JSON key         | Essentia Path                  | What It Measures                                                    |
| ---------------- | ------------------------------ | ------------------------------------------------------------------- |
| `beatsLoudness`  | `rhythm.beats_loudness.mean`   | Mean loudness at beat positions — kick/snare intensity              |
| `chordsStrength` | `tonal.chords_strength.mean`   | Mean chord-detector confidence (0-1)                                |
| `hpcpCrest`      | `tonal.hpcp_crest.mean`        | Crest of 12-bin harmonic pitch class profile — tonal focus (dB)     |
| `hpcpEntropy`    | `tonal.hpcp_entropy.mean`      | Entropy of HPCP — high ⇒ atonal/chromatic, low ⇒ diatonic           |

## Output Format

`mbxmoods.json`:

```json
{
  "version": "1.0",
  "generatedAt": "2026-02-06T...",
  "trackCount": 70000,
  "tracks": {
    "C:\\Music\\Artist\\Song.mp3": {
      "trackId": 123,
      "artist": "Artist",
      "title": "Song",
      "album": "Album",
      "genre": "Rock",
      "bpm": 128.0,
      "key": "C",
      "mode": "major",
      "spectralCentroid": 2456.3,
      "spectralFlux": 0.2134,
      "loudness": -8.52,
      "danceability": 0.7821,
      "onsetRate": 3.45,
      "zeroCrossingRate": 0.0892,
      "spectralRms": 0.1245,
      "spectralFlatness": 0.2341,
      "dissonance": 0.3456,
      "pitchSalience": 0.6789,
      "chordsChangesRate": 0.8901,
      "mfcc": [-234.5, 45.2, -12.3, 8.7, -3.1, 1.2, -0.8, 0.5, -0.3, 0.2, -0.1, 0.1, -0.05],
      "dynamicRange": 7.3,
      "dynamicRangeSource": "essentia-lra",
      "loudnessMomentary": -11.82, "loudnessShortTerm": -10.47, "replayGain": -8.4,
      "silenceRate20dB": 0.02, "silenceRate30dB": 0.07, "silenceRate60dB": 0.21,
      "spectralRolloff": 4211.5, "spectralComplexity": 14.2, "spectralEntropy": 7.86,
      "spectralKurtosis": 21.1, "spectralSkewness": 3.42, "spectralSpread": 2.18e6,
      "spectralStrongPeak": 1.7, "spectralDecrease": -1.8e-8,
      "spectralEnergy": 0.0041, "spectralEnergyLow": 0.0015, "spectralEnergyMidLow": 0.0012,
      "spectralEnergyMidHigh": 0.0009, "spectralEnergyHigh": 0.0005,
      "hfc": 112.4,
      "barkCrest": 9.4, "barkFlatness": -9.1, "barkKurtosis": 7.3, "barkSkewness": 2.1, "barkSpread": 21.3,
      "erbCrest": 11.2, "erbFlatness": -8.7, "erbKurtosis": 5.8, "erbSkewness": 1.9, "erbSpread": 18.2,
      "melCrest": 10.6, "melFlatness": -8.4, "melKurtosis": 6.1, "melSkewness": 2.0, "melSpread": 19.5,
      "beatsLoudness": -14.1, "chordsStrength": 0.61, "hpcpCrest": 6.2, "hpcpEntropy": 2.87,
      "bitUsage": {
        "lowestNonZeroBit": 8,
        "bottomBitActivity": 0.4123,
        "effectiveBits": 19.47,
        "samplesAnalyzed": 1323000,
        "method": "ffmpeg-s32le-30s-mid-native"
      },
      "hfEnergyRatio": 0.000028,
      "hfEnergyMethod": "managed-fft-radix2-30s-mid-native",
      "hfSpectralStructure": {
        "flatness": 0.1326,
        "peakToMean": 41.8,
        "imagingSymmetry": -0.0408,
        "method": "managed-fft-radix2-30s-mid-native"
      },
      "lastModified": "2025-12-01T00:00:00.0000000Z",
      "analysisDuration": 4.2,
      "fileMd5": "d41d8cd98f00b204e9800998ecf8427e",
      "audioStreamSha256": "3a0be88f7ea54faa66f9e092ac40e0820197377d59a1b2c8c2d3a4b5c6d7e8f9",
      "fingerprint": {
        "v1": {
          "fileSize": 8421376,
          "pathTail": "Artist/Album/Song.flac",
          "durationMs": 245123,
          "sampleRate": 96000,
          "channels": 2,
          "bitDepth": 24,
          "codec": "flac",
          "codecRaw": "audio/flac",
          "bitrate": 2745,
          "encoder": "reference libFLAC 1.3.2 20170101",
          "encoderRaw": "reference libFLAC 1.3.2 20170101",
          "audioHead64kMd5": "ab12cd34ef56789012abcdef34567890"
        }
      },
      "truedat": {
        "hiresGenuine": "yes",
        "hiresConfidence": 1.0,
        "lossyTranscodeLikely": "n/a",
        "method": "truedat-v1-fft-corpus1-2026-05-18"
      },
      "smfmScores": [22, 41, 8, 130, 95, 12, 4, 7, 88, 33],
      "smfmChannel": 3,
      "smfmBpm": 128.004
    }
  }
}
```

For MP3 entries, `fingerprint.v1` also carries a nested `mp3LameTag` block when the file has a Xing/Info+LAME header:

```json
"fingerprint": {
  "v1": {
    "...": "...",
    "codec": "mp3",
    "encoder": "LAME3.100",
    "mp3LameTag": {
      "version": "LAME3.100",
      "vbrMethodCode": 4,
      "vbrMethod": "VBR Method 4 (Two Pass)",
      "lowpassHz": 19500,
      "encoderDelay": 576,
      "encoderPadding": 1656,
      "musicCrc": 12345,
      "infoTagRevision": 0
    }
  }
}
```

When the source file carries a Sony SMFM (12-TONE) block — embedded by Sony Music Center — truedat reads it header-only and emits `smfmScores` (10 raw STMO slot scores, 0–255), `smfmChannel` (the dominant raw slot index), and `smfmBpm` (Sony's tempo estimate). These are nullable and omitted when absent. The per-slot *channel names* are deliberately **not** emitted: a 2026-06-27 live device test refuted the old slot→name mapping (the device's mood channels are 2-D arousal×valence regions, not 1:1 with STMO slots), so any interpreted mood label is derived downstream by MBXHub, not by truedat. These keys were renamed from `sensme*` on 2026-06-28 — `--migrate` converts existing libraries (with backup), and the reader still accepts the old keys for un-migrated files.

Raw features are stored so MBXHub can compute valence/arousal at runtime with tunable weights — no re-scan needed to adjust the formulas. The 40 extended fields are persisted for future downstream scoring (sub-genre profiling, loudness normalisation, clustering). Every extended field is nullable; legacy entries produced before the extended set was added simply omit those keys rather than storing zeros. The `analysisDuration` field records how long Essentia took to analyze each track (in seconds).

`fileMd5` (MD5 of the file bytes), `fingerprint.v1` (cheap composite — TagLib parse + 64 KB invariant-region MD5; fields include `fileSize`, `pathTail`, `durationMs`, `sampleRate`, `channels`, `bitDepth`, `codec`, `bitrate`, `encoder`, `audioHead64kMd5`, plus the nested `mp3LameTag` block for MP3s), and `audioStreamSha256` (streaming SHA-256 over the audio invariant region — content-stable across tag edits and file moves) are all computed concurrently with the Essentia feature extraction in pure-managed code. No subprocesses, no path-escape exposure, no codepage drama. Wall-clock per track is roughly `max(analysis, slowest-hash)` rather than the sum, with Essentia dominating. `audioStreamSha256` is the primary content thumbprint — it survives file moves, renames, and tag edits, and works identically on NTFS, exFAT, and any path Windows can open.

The `bitDepth` and `encoder` sub-fields enable cross-checking a file's claimed format against its actual content — a 320 kbps MP3 whose `encoder` is `Lavc58.x` (ffmpeg transcoder) is almost certainly transcoded from a lossy source; a 24/96 FLAC whose `bitDepth=24` but whose `bitUsage.lowestNonZeroBit` lands at 16 is upsampled CD audio. The `truedat.*` verdict block (see below) consumes these fields plus the `bitUsage` / `hfEnergyRatio` / `hfSpectralStructure` signals to produce a per-track classification.

For MP3 specifically, the `mp3LameTag` block inside `fingerprint.v1` is populated when the file carries a Xing/Info+LAME header. Fields include `version` (e.g. `LAME3.100`), `vbrMethod` (CBR / ABR / VBR method N), `lowpassHz` (LAME's chosen low-pass cutoff — the **single strongest transcode-from-low-bitrate tell**: a "320 kbps" MP3 with `lowpassHz: 16000` was almost certainly transcoded from 128 kbps source), `encoderDelay` / `encoderPadding`, `musicCrc`, `infoTagRevision`. Parsed pure-managed from the first ~8 KB of the file; no subprocess. Files re-encoded by ffmpeg (`Lavc...`) typically have no LAME tag at all — its absence on a non-Xing MP3 is itself a soft signal.

For lossless containers claiming ≥24-bit depth, the `bitUsage` block carries a sub-second ffmpeg-piped PCM walk over 30 s mid-track that builds a trailing-zeros histogram of the s32le samples. Fields: `lowestNonZeroBit` (where signal actually starts in the 32-bit representation — true 24-bit lands at ~7–8 after ffmpeg's alignment, while 16-bit content padded to 24-bit lands at ~16), `bottomBitActivity` (fraction of non-zero samples at the resolution boundary), `effectiveBits` (continuous signal for confidence scoring, clipped to [0, 32] — the s32le sample-space ceiling), `samplesAnalyzed`, `method` (currently `ffmpeg-s32le-30s-mid-native`; the trailing `-native` records that the walk runs at the source's native sample rate rather than a forced 44.1k — earlier values without the suffix were biased by resample interpolation noise). The applicability gate runs as a ~5 ms TagLib peek **before** the ffmpeg decode so lossy / sub-24-bit files are skipped without spending 30 s of decode on data that would be meaningless. Populated only during fresh analysis or via `--verify --backfill --backfill-level features|all`. Null on ffmpeg-absent installs.

The orthogonal `hfEnergyRatio` signal is the fraction of audio energy above 22.05 kHz, measured at the source's native sample rate via a hand-rolled radix-2 FFT walk over 4096-sample Hann-windowed frames (50 % overlap). Only populated when `sourceSampleRate > 44100` (CD-rate files have no Nyquist headroom above 22 kHz, so the test isn't applicable). Catches an evasion that `bitUsage` can't: an upsampler that adds dither to 16/44.1 → 24/96 produces plausible-looking LSB activity, but it can't fabricate audio energy above the original Nyquist. Bin-sharp values run small (genuine 24/96 hi-res lands at ~1e-5; upsampled-from-44.1 content lands at literal 0 after Lanczos suppression). The `hfEnergyMethod` companion field carries the algorithm identifier (currently `managed-fft-radix2-30s-mid-native`).

The same FFT pass also emits `hfSpectralStructure: { flatness, peakToMean, imagingSymmetry, method }` — Wiener-entropy flatness over the HF band, peak-to-mean ratio of HF bins, and Pearson correlation of HF bins against their mirror partners in the source band. The Phase 5 signal catches ffmpeg-upsampled fake hi-res that the bit-level signals (`bitUsage`) miss entirely: upsampled content has very low flatness (energy in a few narrow imaging spikes against an otherwise-empty HF band) and high peak-to-mean (often 80–180), while genuine HF content lands either broadband (orchestral, flatness ~0.5) or peaky-but-uncorrelated-with-mid-band (synthesised cymbals, flatness ~0.01 but with one dominant harmonic). Together, `bitUsage`, `hfEnergyRatio`, and `hfSpectralStructure` are the three independent signals the verdict block weights to answer "is this 24/96 claim genuine?".

### Authenticity verdict (`truedat.*` block)

Each track in `mbxmoods.json` carries a nested `truedat` block with two verdicts and per-question confidence, plus a method tag:

```json
"truedat": {
  "hiresGenuine":            "yes" | "no" | "unknown" | "n/a",
  "hiresConfidence":         0.85,
  "lossyTranscodeLikely":    "yes" | "no" | "unknown" | "n/a",
  "lossyTranscodeConfidence": 0.92,
  "method":                  "truedat-v1-fft-corpus1-2026-05-18"
}
```

Four-string enum, **not** a bool — collapsing `"unknown"` into yes/no is exactly what produces false positives and negatives in the wild. `"unknown"` and `"n/a"` are first-class outcomes. `"n/a"` means the test wasn't applicable to this file (hi-res check on a 16-bit FLAC, transcode check on a FLAC, etc.); `"unknown"` means it was applicable but the signals are weak or disagreeing.

The block is **omitted entirely** when both questions would be `"n/a"` (legacy entries without `fingerprint.v1`, weird-codec files) OR when both would be `"unknown"` from lack of signal (legacy lossless 24-bit entries that predate the `bitUsage` / `hfEnergyRatio` work). Run `--verify --backfill` to populate those fields and pick up a real verdict on the next pass.

Multi-signal weighted voting per question. Hi-res verdict combines four signals: `bitUsage.lowestNonZeroBit` (0.40), `hfEnergyRatio` (0.40), `bitUsage.effectiveBits` (0.20), and `hfSpectralStructure` (Phase 5 — Signal F, 0.35) — total available weight 1.35 when all signals vote. Transcode verdict (MP3 only) combines encoder string, MP3 LAME tag lowpass, and LAME tag presence with weights 0.30 / 0.35 / 0.20. (An earlier `spectralRolloff` signal was dropped after corpus validation showed it produced false positives on naturally low-HF material.) ±0.7 **normalized**-score threshold (score / maxWeight) means signals must collectively cross 70% agreement for a yes/no verdict; one strong signal alone abstains as `"unknown"`. Signal F intentionally abstains in the middle band (`0.005 ≤ flatness ≤ 0.5` or `peakToMean ≤ 50`), reinforcing existing yes/no calls without driving them on its own — corpus-1 tuning showed this discipline avoided false flips on peaky-but-genuine cymbal content.

Computed inline at write time, not persisted in cache. Threshold changes ship without a rescan; the method tag bumps when thresholds change so consumers can detect algorithm drift. Per-signal vote+weight trace available via `--audit` for debugging.

**Current method tag: `truedat-v1-fft-corpus1-2026-05-18`** — Phase 5 calibration pass against the 23-file hand-labeled corpus (`docs/reviews/2026-05-18-phase4-corpus-validation.md`), incorporating the FFT-derived `hfSpectralStructure` signal. The corpus-1 retune closed the ffmpeg-upsampled-fake-hi-res gap (3/3 fakes now correctly suppressed or classified). One known gap remains for Phase 5+: LAME-to-LAME re-encode chains verdict `"no"` because the second LAME encode rewrites the Xing tag — needs cascade-encode artifact detection. Consumers should treat verdicts as high-confidence-but-not-perfect; the method tag will bump to `truedat-v1-…-YYYY-MM-DD` on each subsequent calibration pass.

### Fingerprint Output

`mbxhub-fingerprints.json`:

```json
{
  "version": "1.0",
  "generatedAt": "2026-02-14T...",
  "trackCount": 70000,
  "tracks": {
    "C:\\Music\\Artist\\Song.mp3": {
      "trackId": 123,
      "artist": "Artist",
      "title": "Song",
      "album": "Album",
      "genre": "Rock",
      "chromaprint": "AQADtNIyhZKo...",
      "duration": 245,
      "md5": "a1b2c3d4e5f6...",
      "lastModified": "2026-01-15T00:00:00.0000000Z"
    }
  }
}
```

Fields are omitted when the tool wasn't run (`--chromaprint-only` omits `md5`, `--md5-only` omits `chromaprint`/`duration`). Existing cached entries with both fields are preserved even when re-running with a subset flag.

### Details Output

`mbxhub-details.json` (generated by `--details`):

```json
{
  "version": "1.0",
  "generatedAt": "2026-02-14T...",
  "trackCount": 70000,
  "tracks": {
    "C:\\Music\\Artist\\Song.mp3": {
      "trackId": 123,
      "artist": "Artist",
      "title": "Song",
      "codec": "mp3",
      "channels": 2,
      "sampleRate": 44100,
      "bitRate": 320,
      "bitDepth": 0,
      "duration": 245.3,
      "format": "mp3",
      "sizeMb": 9.4,
      "lastProbed": "2026-02-14T00:00:00.0000000Z",
      "lastModified": "2026-01-15T00:00:00.0000000Z"
    }
  }
}
```

Audio metadata extracted via ffprobe. `bitRate` is in kbps, `bitDepth` is 0 for lossy codecs that don't have a fixed bit depth (e.g. MP3, AAC).

## How Mood Vectors Work

Based on Russell's circumplex model of emotion. Each track gets a 2D coordinate `[valence, arousal]`:

```
                    High Arousal
                         |
         Angry/Tense     |     Energetic/Happy
                         |
  Low Valence -----------+----------- High Valence
                         |
         Sad/Melancholy  |     Calm/Relaxed
                         |
                    Low Arousal
```

**Valence** = weighted combination of 8 features:
mode (0.25), dissonance (0.15), spectral centroid (0.15), spectral flatness (0.10), pitch salience (0.10), danceability (0.10), MFCC2 (0.10), chord changes (0.05)

**Arousal** = weighted combination of 7 features:
BPM (0.20), onset rate (0.15), spectral RMS (0.15), loudness (0.15), spectral flux (0.15), zero-crossing rate (0.10), danceability (0.10)

All weights are configurable in MBXHub's `autoQ.estimation` settings.

## Visualization

```cmd
python src/visualize.py mbxmoods.json
```

Scatter plot of your library's mood distribution.

## iTunes Music Library XML

Truedat uses the iTunes Music Library XML format as its input. MusicBee can export your library in this format:

1. In MusicBee, go to **Edit > Preferences > Library**
2. Enable **"iTunes Music Library.xml"** export
3. MusicBee writes `iTunes Music Library.xml` to your library folder, updating it automatically

This is a standard XML format originally from iTunes/Apple Music that many music players support as an export option.

## Integration with MBXHub

[Features - MBXHub](https://mbxhub.com/features.html#autoq)

[Download - MBXHub](https://mbxhub.com/download.html)

Truedat generates the mood data that MBXHub's AutoQ engine consumes. The workflow:

1. **Truedat** scans your library using the iTunes XML export and produces `mbxmoods.json`
2. **MBXHub** loads the file at startup and recomputes valence/arousal using its current weight settings
3. **AutoQ** uses mood vectors for mood-aware shuffle, reactions, and influence scoring

Place `mbxmoods.json` in your MusicBee Library folder (sibling to `AppData`) or in `%APPDATA%\MusicBee\MBXHub\`. MBXHub searches both locations automatically.

## License

- **truedat.exe**: MIT - Copyright (c) 2026 Halrad LLC
- **System.Text.Json**: MIT - Copyright (c) .NET Foundation (merged into exe)
- **TagLibSharp**: LGPL-2.1 - [TagLibSharp](https://github.com/mono/taglib-sharp) (merged into exe; used by fingerprint.v1, codec detection, identity backfill, bitUsage applicability peek, and synthetic-track metadata)
- **Essentia tools**: AGPL-3.0 - [Essentia](https://github.com/MTG/essentia) by Music Technology Group, Universitat Pompeu Fabra
- **FFmpeg tools**: GPL-3.0+ - [FFmpeg](https://ffmpeg.org/) (optional dependency)

See [LICENSE](LICENSE) for details.

## Acknowledgments

This software uses [Essentia](https://essentia.upf.edu/), an open-source C++ library for audio analysis developed by the Music Technology Group at Universitat Pompeu Fabra.

If you use this in academic work, please cite:

> Bogdanov, D., Wack N., Gomez E., Gulati S., Herrera P., Mayor O., et al. (2013).
> ESSENTIA: an Audio Analysis Library for Music Information Retrieval.
> International Society for Music Information Retrieval Conference (ISMIR'13).

- [Essentia on GitHub](https://github.com/MTG/essentia)
- [Essentia Documentation](https://essentia.upf.edu/documentation.html)
