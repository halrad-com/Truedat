# Truedat - Music Mood Extractor & Fingerprinter

Truedat is a Windows .NET CLI that extracts **per-track mood** across a music library — the signal [MBXHub's ](https://mbxhub.com/download.htm) - [AutoQ](https://mbxhub.com/features.html#autoq)  engine uses for **mood-aware shuffle** — and writes it (alongside identity, authenticity, and metadata) into `mbxmoods.json` that MBXHub reads directly.

**Mood is the core job, and truedat captures it two independent ways:**

- **Essentia** — when [Essentia](https://essentia.upf.edu/) is present, truedat calls it to extract the per-track acoustic feature set (see [Extracted Features](#extracted-features) for the exact counts). The primary mood read. Truedat writes features, not an interpretation: the valence/arousal mapping is made downstream by the consumer (MBXHub/AutoQ) from these fields. truedat provides a custom x64 build (see [Essentia Builds](#essentia-builds)) that handles large files better, but doesn't bundle it — it's a separate AGPL tool, invoked if found.
- **SMFM (Sony 12 TONE / SensMe)** — truedat doesn't add this; it reads Sony's own analysis when it's already embedded in a file — the SMFM block — yielding 10 STMO mood scores + BPM (the `smfm*` fields). MBXHub projects these to a **second (valence, arousal) opinion** on the same AutoQ mood map — an independent take alongside Essentia, most useful exactly where the two disagree.

Everything below is built **on top of** that mood signal — identity, authenticity, and library-scale plumbing:

- **Composite track identity** — `fingerprint.v1` (file size + path tail + audio properties + 64 KB head MD5 + MP3 LAME tag block), `audioStreamSha256` (SHA-256 over the audio region — frame-anchored for FLAC, TagLib's invariant region otherwise; cross-machine portable, tag-edit resilient for MP3/WMA/FLAC alike), and an optional whole-file MD5 (`--file-md5`).
- **Authenticity signals** — `bitUsage` (ffmpeg-driven LSB walk that catches fake-hi-res 24-bit padding), `hfEnergyRatio` (bin-sharp energy above 22.05 kHz via managed FFT), `hfSpectralStructure` (flatness / peak-to-mean / mirror-imaging from the same FFT pass — catches ffmpeg-upsampled fakes the bit-level signals miss), MP3 LAME-tag parsing (pure-managed Xing/Info+LAME decoder for transcode detection).
- **`truedat.*` verdict block** — multi-signal voted hi-res / lossy-transcode verdicts (`yes` / `no` / `unknown` / `n/a`) computed inline at write time, so threshold changes ship without rescanning.
- **Library-scale operations** — incremental & cache-aware scanning across four cache tiers (path+mtime, head-64k quick, path+sha, cross-sha), `--verify` / `--verify --backfill` (identity + features tiers), `--merge-moods`, deterministic multi-machine `--chunk M/N`, `--hash-only` NDJSON manifest mode, opus auto-retry, and a standalone `--transcode` utility.

The output (`mbxmoods.json`) carries all of this in a single per-track object that MBXHub reads directly.

Minor utility modes (`--synthesize`, `--seed-moods`) cover synthetic-library generation and AcousticBrainz-driven mood seeding for scale-testing MBXHub — see [Test Tooling](#test-tooling) at the bottom of this doc.

**Output:**

- `mbxmoods.json` - mood coordinates and raw audio features for every track
- `mbxmoods-errors.csv` - tracks that failed analysis, with the error reason, file size, duration, and a `FailedComponents` column naming exactly what failed (e.g. `decode-27%;fingerprint;sha`). Failure is broader than "Essentia crashed": a track that analyzes but can't be hashed/identified (corrupt header), whose tags are unreadable (per-file modes), whose decode covers less than 95% of its claimed duration (truncated file — the features would describe a fraction of the track), or whose applicable bitUsage/HF signal died, is a **fail** — no catalog entry is written, and the file is skipped on later scans until fixed (`--retry-errors` re-attempts). Legitimately-absent signals (bitUsage on lossy files or silence, HF at 44.1 kHz, no ffmpeg, no SMFM) are never failures.
- `mbxmoods-skipped.csv` - every track dropped before analysis, with the reason: remote stream URLs (un-downloaded podcast-feed episodes whose XML location is an `http(s)://` URL — not files, never scannable), unsupported codec (`.dsf` / `.dff` / `.dsd` DSD streams), video files, playlist/redirector entries, missing files (`file not found`, with the length called out when the path exceeds the Windows 260-char MAX_PATH), and any track matched by an `exclude` rule in `mbxmoods-exclude.json` (reason `excluded (rule: …)` — see [Excluding files from analysis](#excluding-files-from-analysis)). Speech labels and embedded file markers are **not** a skip reason any more — they are review evidence on `--preview`'s output, not an analysis filter; see the upgrade note below if you relied on the old auto-skip. Columns: `path,extension,reason,timestamp` (rows append per run). `--audit` additionally lists each dropped track on the console. Over-MAX_PATH paths whose files *do* exist are not skipped — the scan falls back to `\\?\` extended-length IO and analyzes them through a staged copy.
- `mbxmoods-verify.csv` - per-entry status from `--verify` / `--verify --backfill` (OK / DRIFT / MISSING / NO_HASH / BACKFILLED / REANALYZE_NEEDED / ERROR, plus the list of fields filled per entry)
- `truedat.log` - full console output for diagnostics (when `--audit` is used)

## Download

[**truedat.zip**](https://halrad.com/truedat/truedat.zip) — full self-contained bundle (`truedat.exe` + Essentia + ffmpeg). Runs offline, no install. Unzip anywhere and run `truedat.exe`.

What's inside and what happens if you drop pieces: [dependency-surface notes](https://halrad.com/truedat/dependency-surface.md).

Source tree and tagged snapshots are on [GitHub](https://github.com/halrad-com/Truedat).

## What It Does

Truedat reads an iTunes Music Library XML file (or a `--folder`, `--file-list`, or single `--analyze-file`) and runs each track through Essentia plus its own analyses. Per-track work is parallelized across cores; everything is cache-aware so re-runs only touch what changed.

### Default scan (analysis + identity + authenticity)

A single default invocation produces the full per-track record:

- **Mood features (Essentia)** — Valence (0-1, sad ↔ happy; 8 input features), Arousal (0-1, calm ↔ energetic; 7 input features), and 15 core + 40 extended Essentia features stored per track for runtime recomputation (extended set covers loudness envelope, silence profile, spectral shape, psychoacoustic bands, rhythm/tonal aggregates).
- **Identity (truedat-native, pure-managed)** — `fingerprint.v1` composite, `audioStreamSha256` (TagLib invariant audio region), and optionally `fileMd5` (whole-file MD5, maintained only with `--file-md5`). All computed concurrently with the Essentia decode so they're effectively free.
- **Authenticity signals (truedat-native, ffmpeg-driven)** — `bitUsage` block (LSB walk for fake-hi-res detection on lossless 24-bit files), `hfEnergyRatio` + `hfSpectralStructure` (single managed-FFT pass over the same 30 s mid-track segment — bin-sharp HF energy ratio plus Wiener-entropy flatness, peak-to-mean, and mid↔HF mirror correlation; catches ffmpeg-upsampled fakes the bit-level signals miss), MP3 LAME-tag parsing (Xing/Info+LAME header decode for transcode detection).
- **`truedat.*` verdict block** — multi-signal voted hi-res / lossy-transcode verdicts computed inline at write time from the signals above. Threshold changes ship without rescanning.

MBXHub consumes the whole record: mood features drive AutoQ vibe selection; identity drives cross-cache matching; authenticity drives the hi-res / transcode classifiers.

### Verify & backfill (`--verify`, `--verify --backfill`)

Walks an existing `mbxmoods.json` and either reports drift (read-only) or repairs missing fields in place (no Essentia re-run). Two backfill tiers — identity (TagLib + cheap IO) and features (ffmpeg-driven `bitUsage` / `hfEnergyRatio` / `hfSpectralStructure`) — both gated by an SHA drift check so drifted entries are flagged for re-analysis rather than touched. See [Verify & Backfill](#large-libraries) for tier scoping via `--backfill-level`.

### Filename Check (`--check-filenames`)

Scans your library for filenames with characters that cause Essentia tools to fail. Reports three tiers:

- **Errors** — Fullwidth Unicode substitution characters (e.g. `⧸` `：` `＂`) that are known to break Essentia's ANSI argv parsing. These files will always fail analysis.
- **Warnings** — Other non-ASCII characters where 8.3 short path fallback is unavailable. These files may fail depending on system configuration.
- **Suspects** — Audio files under 50 KB that may be corrupt or truncated.
- **Long paths** — Paths at or over the Windows 260-char MAX_PATH. The scan handles these automatically (`\\?\` long-path fallback through a staged copy), but other tools may not — consider shortening.

## Quick Start

```cmd
REM Mood analysis (default)
truedat.exe "iTunes Music Library.xml"
```

Output: `mbxmoods.json` (next to the XML file)

**Auto-discovery:** if you omit the positional XML arg, truedat probes (first hit wins): `<exe-dir>\..\iTunes Music Library.xml` (the install-parent case — drop the truedat folder under your library directory and it just works), then `<exe-dir>\iTunes Music Library.xml`, then `.\iTunes Music Library.xml` (cwd). The error message lists the probed locations so a no-hit failure is self-diagnosing.

### Options

`truedat --help` shows the short everyday page; `truedat --help all` lists every
option. Unknown flags fail with a "did you mean" suggestion instead of being
ignored. The reference below covers the commonly used options; `--help all` is
the complete and authoritative list.

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
                              audioStreamSha256, fileMd5 (with --file-md5), whole fingerprint.v1, sub-fields
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
  --stats [path]          Read-only catalog summary: Essentia-analyzed count, hash coverage
                          per kind, and SMFM track count. Path defaults to ./mbxmoods.json.
                          Also printed at end of every scan. With --audit, written to the log.
                          Reports ONE speech count — the union of the label (stored genre
                          == "Podcast") and the acoustic verdict (speechLikely == yes), each
                          entry counted once — pointing at review + an exclusion rule
                          (never --migrate, which doesn't touch entries either way). Separately,
                          a Recommended section maps detected gaps (missing tonal/rhythm wave,
                          missing fingerprint.v1, stray fileMd5) to the exact command that
                          closes each — truedat never prompts interactively.
  --stats-detail N        List per-file status when a catalog has < N tracks (default 5).
  --list-speech [path]    Read-only: list the entries whose verdict is speechLikely=yes —
                          candidates for an exclusion rule, not a --migrate prune (--migrate
                          never removes entries). Writes mbxmoods-speech.csv (path, artist,
                          title, album, genre, codec, confidence, method) next to the moods
                          file and prints a count + preview. The speech verdict is still
                          -untuned, so real music with talk-like shape (ambient, field
                          recordings, spoken intros) can appear here; review before excluding.
  --list-missing-smfm [path]
                          Read-only: list catalog entries carrying no Sony SMFM (12-TONE)
                          data, with overall coverage (present / total / %). Writes
                          mbxmoods-smfm-missing.csv (path, artist, title, album, codec).
                          SMFM is embedded in the file by Sony tooling and only READ by
                          truedat — a rescan will not add it, so this is a "which files
                          still need Sony tagging" report, not a truedat gap. For that
                          reason it is deliberately absent from the --stats Recommended
                          block, which only ever names gaps a truedat command can close.
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
  --migrate               Clean up mbxmoods.json: strip legacy fields (valence/arousal,
                          audioMd5, chromaprint) and fileMd5 (kept with --file-md5), rename
                          SMFM keys (sensme*->smfm*) (creates backup). Field cleanup only —
                          never removes a catalog entry, on any verdict.
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
  --stage-dir <path>      Override the scratch dir used for staged source copies and
                          multi-channel downmixes (default %TEMP%\.truedat-stage). Also the
                          isolation lever if you ever need two truedats on one machine —
                          see "Run one truedat at a time" below.
  --max-duration <secs>   Max track length for Essentia analysis (default 48000 = 800 min —
                          the large-buffer extractor's ChordsDetection ceiling; pass 12000
                          if running the old small-buffer .1 extractor). Longer tracks
                          are a structural skip: ledgered to mbxmoods-skipped.csv, NOT
                          counted as failures. (They used to land in the errors CSV, which
                          made the next run skip them as previous errors — a legitimate
                          "too long for this extractor" turned into a permanent silent skip
                          that no longer explained itself.) Raise only when running an
                          extractor built with the larger buffer.
  --no-quick-cache        Disable the tags-only quick cache tier (head-64k check);
                          mtime-drifted files always take the full audio-hash check.
  --no-bitusage           Suppress ComputeBitUsage (omits the bitUsage JSON block).
  --no-hf-analysis        Suppress ComputeHfAnalysis (omits hfEnergyRatio + hfSpectralStructure).
  --file-md5              Maintain the whole-file fileMd5 field (default off). Off means it is
                          never written: fresh analysis omits it, cache-tier refreshes drop it,
                          the --backfill fileMd5 fill is skipped, and --migrate strips stored
                          values. The durable audio identity is audioStreamSha256 either way;
                          enable this only if you compare whole-file MD5s with external tools.
  --pause                 Hold the console open at exit (press any key). For double-click
                          launches; no effect on redirected/scheduled runs.
  --allow-sleep           Let the machine sleep during scans. By default, work-bearing
                          runs (scan/verify/transcode) hold the system awake while on AC
                          power — see "Power & sleep" below. Display sleep is unaffected
                          either way.
  --exclusions <path>     Use this exclusion file instead of mbxmoods-exclude.json beside
                          the moods file.
  --no-exclusions         Ignore the exclusion file for this run. Prints a warning; for
                          diagnosing whether a rule is what kept a file out.
  --apply-exclusions <path>
                          Merge a decisions delta into the exclusion file. Backs up the
                          previous version and reports added / removed / already-set /
                          not-present counts. Exits 1 without writing if the document or
                          the existing file cannot be parsed. The write is staged to a
                          .tmp sibling and swapped atomically, so an interrupted or
                          out-of-disk apply can never truncate your rules; the merge holds
                          a zero-byte mbxmoods-exclude.json.lock sidecar across read and
                          write, so two applies at once cannot lose one author's rules
                          (a text editor saving the file at the same moment is outside
                          any lock — that is what the .bak is for).
  --preview [path]        Analyzes nothing, writes no mbxmoods.json, and never touches the
                          exclusion file. It DOES write two files: the scan work plan and the
                          review-candidate list go to preview.json plus mbxmoods-preview.html
                          in the MBXHub review folder of the instance that owns the library you
                          pointed it at (creating that folder if needed) — so this is read-only
                          over your catalog, not over the disk. When no MusicBee/MBXHub instance
                          owns the library they land beside the moods file as
                          mbxmoods-preview.json instead; never in some other instance's folder.
                          Pass [path] to choose the destination yourself. Auto-discovers the
                          iTunes XML the same way a normal scan does (exe dir, its parent, then
                          the cwd), so it needs no positional argument when run from inside the
                          library folder. [path] is an OUTPUT and is only claimed when it looks
                          like one (a directory separator or a .json suffix), so the library
                          path can still be positional; and it refuses, with exit 1 and nothing
                          written, to overwrite an .xml or an existing mood catalog.
  --long-track-mins N     Duration that flags a track for review in --preview (default 30).
                          A review *prompt* only: it decides what a human is asked to look
                          at and never excludes anything by itself.
```

**After a mass tag edit:** rewriting tags across the library changes every file's mtime without touching audio. Truedat detects this per file at ~64 KB/track (a quick head-hash check) instead of re-reading each file in full, so a full-library rescan after a retag pass finishes in a fraction of the time and re-runs zero analysis. `--no-quick-cache` forces the full per-file audio-hash check instead, and `--verify` remains the full-integrity check against the durable `audioStreamSha256`.

**Network libraries:** when the source is on a UNC share (`\\server\share\…`), a mapped network drive (e.g. `Z:\` mapped to `\\server\share`), or a local path with non-ASCII characters, truedat stages each file once to a local temp copy and runs the 8-9 concurrent per-track workers (and the cache hierarchy's tier-2/4 body reads) against the local copy. Net: 1× full network read per track instead of ~3× full + ≥3× partial. Cache tier-1 (path + mtime equality) doesn't stage — already-cached tracks stay free. Local-ASCII paths read directly. Use `--no-stage` to opt out, or `--stage-dir` to relocate the staging directory (e.g. to a fast scratch volume when `%TEMP%` is on a small SSD). Per-track stage failures fall back to direct read with a one-line warning — scans never abort over a staging hiccup. End-of-scan summary reports `staging: N staged` (or `N staged, M direct-fallback`), with a stderr warning when >5% of attempted stages fell back so a wedged stage-dir is visible.

**Run one truedat at a time.** Truedat is single-instance by design. At startup it sweeps
leftover scratch files from previous runs — staged copies and downmix WAVs under the staging
dir, and `.truedat-tmp` hardlinks on each drive — and it cannot tell a *dead* run's leftovers
from a *live* sibling's working files. Two guards make that safe rather than merely documented:
a second truedat process on the machine skips the sweep entirely with a warning, and any file
touched in the last 60 minutes is left alone regardless. If you genuinely need two runs at once,
give each its own `--stage-dir` (or pass `--no-stage`); note the per-drive `.truedat-tmp`
hardlink area cannot be relocated, because a hardlink has to sit on the same volume as its
source, so it leans on those two guards. To scale across *machines*, use `--chunk M/N` and
`--merge-moods` rather than concurrent runs against one library.

**Optional:** Place `ffmpeg.exe` and `ffprobe.exe` alongside `truedat.exe` (or on PATH) to enable:

- Auto-downmix of multi-channel (5.1+) audio files during scans (without ffmpeg, multi-channel files are skipped with a warning).
- Auto-retry of files essentia can't decode natively — e.g. `.opus`, which this essentia build lacks. The file is transcoded to a stereo WAV and essentia is re-run against it, all transparently.
- Standalone `--transcode` mode for converting opus/etc. to uncompressed FLAC.

### Large Libraries

For large libraries (50K+ tracks), expect multi-day scans for mood analysis. The scan is designed for this:

- **Incremental** - Skips tracks already processed (by file path + last-modified timestamp).
- **Tag-edit resilient** - When mtime drifts but the audio bytes are unchanged (e.g. tag editor rewrote a frame), the cache reuses Essentia features by recomputing only `audioStreamSha256` (~50ms managed SHA per file) and refreshing the tag-affected identity fields. No full re-extraction.
- **Cross-path resilient** - File moved or renamed? The cross-SHA fallback re-keys the cached entry to the new path without re-analyzing.
- **Multi-machine chunking** - Two boxes pointed at the same library run `--chunk 1/2` and `--chunk 2/2` and produce hostname-suffixed shards (`mbxmoods.<host>.json`); merge later with `--merge-moods`. Hash-mod assignment means iTunes XMLs need not be identical between machines. Each shard's `mbxmoods-skipped.<host>.csv` holds only that shard's own tracks, so the ledgers add up to the library instead of repeating it N times; per-rule exclusion hit counts are the deliberate exception — they are library-wide and identical on every machine, which is what makes a `0 matched` reading trustworthy, and the console says so under `--chunk`.
- **Resumable** - Stop and restart anytime. Progress is saved every 25 analyzed tracks.
- **Verifiable** - `truedat --verify` walks the moods file and confirms each entry's `audioStreamSha256` still matches the disk. FLAC entries hashed by pre-2026-07 builds are recognized via a legacy-region compare in the same read and upgraded in place under `--backfill` (`audioStreamSha256-upgraded`) — no re-analysis; FLACs whose tags were rewritten *before* upgrading are re-keyed **automatically during a normal scan** (props-gated, no re-analysis — the `FLAC re-key` summary line reports it); `--verify --backfill --accept-flac-tag-drift` applies the same rule in a verify pass without running a scan. Detail goes to `mbxmoods-verify.csv`; exit 1 on any drift / missing / error makes it CI-friendly. Add `--backfill` to repair missing fields in place without re-running Essentia. Two tiers run by default: identity (audioStreamSha256 / fileMd5 with `--file-md5` / fingerprint.v1 / bitDepth / encoder / MP3 LAME tag — TagLib-driven, fast) and features (bitUsage / hfEnergyRatio / hfSpectralStructure — ffmpeg-driven, ~30s per applicable lossless 24-bit track). Use `--backfill-level identity` to skip the slow ffmpeg tier on a first pass, or `--backfill-level features` to fill only the ffmpeg-tier fields on a library whose identity is already complete. All tiers are gated by the SHA drift check, so drifted entries are flagged as `REANALYZE_NEEDED` rather than touched.
- **ETA tracking** - The progress line shows estimated completion, running average per track, and live throughput as `Nx realtime` — audio-seconds analyzed per wall-second over a trailing window, with the raw MB/s as a footnote. Duration is the honest unit (a FLAC and a low-bitrate MP3 of the same content cost the same to scan; MB/s can't compare across codecs), and the numerator is what was actually decoded. The end-of-scan summary leads with the scan-wide realtime factor the same way. The estimate is two-class aware: tracks new to the catalog are costed by **audio duration** (the iTunes XML `Total Time` field) at the measured analysis real-time-factor — duration is what Essentia cost actually scales with, so a queue of 3-hour episodes prices correctly even when the first completions were short tracks — while already-cataloged tracks are costed at the measured cache-hit average. No ETA is shown until something has actually been measured. The end-of-scan summary breaks down count and average cost per outcome class (analyzed vs each cache tier vs skips).
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

REM Check for problematic filenames before scanning
truedat.exe "iTunes Music Library.xml" --check-filenames

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

### Power & sleep

While a scan, verify, or transcode is running **on AC power**, truedat holds a
Windows power request so the machine will not idle into sleep mid-run (the
display still sleeps; closing a laptop lid still sleeps the machine). The hold
is visible in `powercfg /requests` (admin console) as
"truedat: library scan in progress" and is released automatically when the run
ends — including on crashes, since Windows frees it with the process.

- On **battery**, normal sleep always applies — plug in for overnight runs.
- `--allow-sleep` disables the hold entirely for operators whose machine
  policy should win.
- On Windows 11, truedat also opts itself and its analysis subprocesses out of
  efficiency-mode throttling (EcoQoS) so hybrid-CPU boxes do not silently park
  a scan on efficiency cores — except under `--background`/`--cpu-limit`,
  which exist to yield.

Two things no process can override, worth setting on a dedicated scan box:
Windows Update restarts (set active hours) and the Windows 11 power-mode
slider (prefer Balanced or better while scanning).

## Excluding files from analysis

Truedat decides what to skip from one file: `mbxmoods-exclude.json`, beside `mbxmoods.json`.
Nothing else excludes a track for policy reasons — metadata signals like genre are evidence
you can act on, never an instruction the scanner infers on its own.

### Exclusion playlists — the easy way to author excludes

The easiest way to build an exclusion list is in MusicBee itself: make a playlist named
**`mbxmoods-exclude`** and put every track you don't want scanned into it. Every scan
looks for `mbxmoods-exclude.m3u8` (or `.m3u`) beside the library XML, then in its
`Playlists` folder, and treats every entry as a file exclude rule for that run — edit
the playlist, rescan, the exclusions follow. Nothing is written to the JSON; the playlist
itself is the durable list. The scan header names the playlist in force. You can also
name one explicitly with `--exclude-playlist <path.m3u8>` (wins over discovery), or make
the entries permanent JSON rules with `truedat --apply-exclusions <playlist.m3u8>`
(entries become `file`/`exclude` rules, with backup and `apply-result.json` as usual).
`include` rules in `mbxmoods-exclude.json` still win over playlist excludes, and
`--no-exclusions` bypasses the playlist along with the file. Stream URLs in the playlist
are skipped (they're never scannable); comment lines are ignored; relative entries
resolve against the playlist's folder. A playlist that exists but yields no usable
entries stops the scan rather than silently excluding nothing.

### Upgrading: speech (podcasts, talk) is no longer skipped automatically

Older builds guessed which files were speech — podcasts, talk — and skipped them. Nothing guesses now —
the only thing that keeps a file out of analysis is a rule you wrote. But **the order matters**, and it
is counter-intuitive: to keep talk *out of what AutoQ plays*, **analyze once, then exclude** — do not
exclude first.

Here is why. AutoQ picks from your MusicBee library, not from `mbxmoods.json`, and it keeps talk out by
reading each track's `speechLikely` verdict — which only exists once the track has been analyzed. So
excluding speech from scanning *before it has ever been analyzed* removes the very signal AutoQ needs: no
analysis → no verdict → AutoQ's speech gate can't fire → the track stays fully pickable. **Excluding
speech from scanning makes AutoQ more likely to play it, not less.**

The recommended order:

1. **Scan once** — let Essentia analyze everything, talk included, so every track gets its `speechLikely`
   verdict. AutoQ's speech gate (on by default) now keeps talk out of your queues.
2. `truedat --preview` — see what a scan would do and which tracks are worth a look.
3. `truedat --list-speech` — review the tracks the acoustic verdict flagged as talk.
4. For the ones you never want *re-analyzed*, write a decisions delta and apply it:
   `{"schemaVersion":1,"kind":"exclusion-decisions","add":[{"kind":"folder","action":"exclude","pattern":"\\Podcasts\\**"}],"remove":[]}`
   then `truedat --apply-exclusions decisions.json` (a `genre` rule for `Podcast` works too). This saves
   Essentia time on every *future* scan, and the AutoQ gate keeps working because the verdict already exists.

Excluding a folder you genuinely never want analyzed — a bulk podcast archive you never play — is still
legitimate and saves real scan time up front. Just know the trade: you skip the scan cost, but those tracks
get no `speechLikely` signal, so AutoQ has nothing to keep them out of a queue. Skip-to-save only when you
never want it played *and* never want it analyzed; analyze-first when you want AutoQ to keep it out for you.

`--include-podcasts` is gone; it was all-or-nothing, which is the problem the rule file solves.
`--migrate` no longer removes speech-labelled or speech-likely entries either — pruning an entry
for a file that is still in your library is self-undoing, because the next scan re-analyzes it.

```jsonc
{
  "schemaVersion": 1,
  "rules": [
    { "kind": "folder", "action": "exclude", "pattern": "\\Podcasts\\**" },
    { "kind": "folder", "action": "include", "pattern": "\\Podcasts\\KEXP Song of the Day\\**" },
    { "kind": "genre",  "action": "exclude", "value": "Podcast" },
    { "kind": "file",   "action": "exclude", "path": "D:\\Music\\setlist-2019.mp3",
      "note": "many songs in one file, so one average describes nothing" }
  ]
}
```

Three rule kinds, each `exclude` or `include`:

- **`folder`** — `pattern` must end in `**` (the subtree). Write it as a *fragment* starting
  with a separator (`\Podcasts\**`) and it matches under any root, which is what you want
  when the same library is mounted on more than one drive. An absolute pattern
  (`D:\Music\Podcasts\**`) matches that root only. Fragments are boundary-aligned, so
  `\Podcasts\**` does not match `\MyPodcastsBackup\`.
- **`genre`** — exact match, case-insensitive, trimmed. Not a substring match: `Podcast`
  does not match `Comedy Podcast`.
- **`file`** — one exact path. Matching is always that exact path, nothing else. Optional
  `audioStreamSha256` records the audio's durable content identity: when the path no longer
  exists, `--preview` reports the rule as **moved or deleted** rather than leaving it looking
  like a stale rule you should delete, and names the catalog paths that still hold that content
  so you can re-point the rule. It is a report and an offer — the rule never starts matching
  those paths on its own, because one `audioStreamSha256` routinely covers several copies of
  the same audio (that is exactly what `--duplicates` groups on) and a rule must not quietly
  widen. Optional `note` records why you decided.

**`include` always wins.** If any include rule matches, the track is analyzed — regardless of
rule order. That is the escape hatch for music a label or an embedded marker misclassifies.

`include` does **not** override structural skips — a missing file, a video, a stream URL, a
DSD file or a track over `--max-duration` still can't be analyzed, and a rule can't change that.

`--no-exclusions` bypasses the whole exclusion file for this run, include rules and all — a
track an include rule was rescuing reverts to whatever the remaining rules say (or nothing, if
none apply) for the duration of that run. Either way, no existing `mbxmoods.json` entry is
ever pruned by this — exclusion is purely a future-analysis switch (see below).

### Speech signals are evidence, not a filter

Nothing in truedat decides a track is speech any more. The iTunes-XML labels
(`Podcast=true`, `Genre=Podcast`) and an embedded file-marker sniff still run, but only to
populate `reasons` on `--preview`'s review candidates — a human reads them and, if warranted,
writes an exclusion rule. The file-marker evidence is **graded**, because the markers don't
all assert the same thing:

- **strong** — ID3v2 `PCST` / MP4 `pcst`: an app asserting "this IS a podcast".
- **provenance** — ID3v2 `WFED` / `TGID`, MP4 `purl`: "this came from a feed", which says
  nothing about content — music gets distributed by RSS too.
- **genre text** — ID3v2 `TCON` exactly `Podcast` (trimmed, case-insensitive; **not** a
  substring match, so `Comedy Podcast` / `Podcasts` / `Podcast Rock` don't count).

An explicit `PCST` flag is stronger evidence than a `WFED` feed URL for exactly that reason.

**There are three independent sources, and they answer different questions.** The two above are
*declared* evidence — someone wrote a label or a tag. The third is *measured*: the
[`speechLikely` verdict](#truedat-verdict-block) is computed from the audio's own features
(danceability, chord strength, silence rate, zero-crossing rate, tempo-peak weight, key strength)
and is the only one that can see a file nobody labelled. That matters because the two classes of
talk content behave differently: a **registered** podcast — subscribed in MusicBee, carrying a feed
URL and a genre — is already identified by its labels, while a **downloaded or copied archive** of
the same show carries nothing at all and is invisible to every label and marker. The acoustic
verdict is what finds the second kind.

**What identification deliberately does not do.** It classifies the *genus* — is this
speech-dominant? — and stops there. Audiobook, comedy, lecture, news, interview and talk-dominant
are one class, and truedat does not try to tell them apart, because the thing that separates them
is provenance (was this distributed as an episodic feed?) and provenance is not in the audio. A
file that lost its feed registration lost that information permanently; no amount of signal
processing recovers it. Species-level questions are answered by things that do carry provenance —
folder layout, genre, feed registration — which is to say, by rules you write.

Every exclusion is ledgered in `mbxmoods-skipped.csv` with the rule that caused it, on every
scan mode (`--analyze-file`, `--file-list`/`--folder`, and the iTunes-XML library scan alike).
The iTunes-XML library scan additionally reports per-rule hit counts, so a rule matching zero
tracks is visible rather than silent — a single-file invocation has nothing to count, so the
per-file modes skip that summary but still print a warning for any rule that failed to parse:

```
  Exclusions: 2 rule(s) from D:\MusicBee\Library\mbxmoods-exclude.json
  Excluded 374 track(s) by rule — listed in mbxmoods-skipped.csv (--audit lists each)
    genre=Podcast: 374 matched
    folder=\Old Shows\**: 0 matched   (stale rule?)
```

`(stale rule?)` is a prompt to delete the rule, so `--preview` never prints it for a `file` rule
that has merely *moved* — deleting that rule would re-admit the file you rejected. Such a rule is
reported instead as `(moved — content now at: …)` when it recorded an `audioStreamSha256` the
catalog still holds, `(moved or deleted — no catalog entry holds this content)` when it did not,
and `(file not reachable from here — still in the catalog, rule is live)` when you are previewing
from a machine that cannot see the audio (a metadata mirror, an unplugged drive, a share that is
down). In `preview.json` these are the `state` and `candidates[]` fields on each rule.

Excluding a file does not remove an existing `mbxmoods.json` entry — it only stops future
analysis, so the decision is reversible.

Edit the file by hand, or merge changes into it:

```
truedat --apply-exclusions decisions.json
```

where `decisions.json` is a delta — `{"schemaVersion":1,"kind":"exclusion-decisions","add":[…],"remove":[…]}`.
Merging rather than overwriting is deliberate: the file has several legitimate authors, and a
whole-file write would discard whichever one went second. The previous version is backed up
before any change. If the file can't be parsed, truedat **refuses to scan** rather than
quietly analyzing everything you thought was excluded — pass `--no-exclusions` if you want to
bypass it on purpose.

### Previewing a scan

`truedat --preview` answers "what would a scan do?" without doing it. Like a normal scan, it
finds the iTunes library XML on its own — the exe's directory, its parent, then the current
directory — so running it bare from inside the library folder works with no positional
argument:

```
Scan preview (nothing was analyzed):
  Library:    71,520 tracks
  Analyzed:   62,187   New: 503
  Estimate:   11h27m (catalog-rtf)
  Skipped:     8,516  streamUrl  (structural — cannot be analyzed)
  Skipped:       789  video      (structural — cannot be analyzed)
  Skipped:        42  missing    (structural — cannot be analyzed)
  Skipped:        11  dsd        (structural — cannot be analyzed)
  Excluded:      374  by rule
    genre=Podcast: 374 matched
    folder=\Old Shows\**: 0 matched   (stale rule?)
  To review:  1,204  (listing the first 500)
  Ceiling:    800 min (default)   long-track prompt: 30 min
```

(Illustrative numbers, not a measurement — a real run reports your own library's counts, and
the estimate is derived from your own catalog, never a canned figure.)

**What "preview" does and does not touch.** It analyzes nothing, writes no `mbxmoods.json`, and
never modifies your exclusion file — those are the guarantees. It is *not* read-only over the
disk: it writes `preview.json` (the same data as a machine-readable manifest, which is what
MBXHub's review surface renders) and `mbxmoods-preview.html` (a self-contained review page you
can open offline). Both go into the MBXHub review folder of the instance that owns the library
you pointed it at — `<library root>\AppData\MBXHub\review\`, created if it does not exist, which
is a live application's data directory. The destination is anchored to the *scanned library*, the
same way `--duplicates --manifest` is, so several MusicBee instances on one machine cannot
receive each other's plans. When no instance owns the library, both land beside the moods file
(`mbxmoods-preview.json`) instead — never in some other instance's folder. Pass an explicit
`--preview <path>` to choose the destination yourself.

`New` counts only the tracks a scan would actually hand to the analyser, and the estimate is
built from those tracks alone — so the structural skips and the rule-excluded tracks are
*outside* it. A speech label alone does **not** move a track out of `New` any more — only a
matching exclusion rule does — so a speech-labelled track with no rule against it shows up
inside `New` (and in `--preview`'s review list, flagged `speech-labelled`, for you to judge).
That is the point of the mode: the `Excluded` line shows what your rules save you, and it
would be meaningless if the saving were still sitting inside the cost.

Two distinctions worth internalising:

- **Structural skips versus rules.** A missing file, a video, a stream URL, a playlist file, a
  DSD file or a track over the duration ceiling *cannot* be analyzed, so they are counted and
  never offered for review — no decision changes them. Everything else is policy, and policy is
  reviewable.
- **The long-track threshold is a prompt, not a rule.** It surfaces long files so you can judge
  them; it excludes nothing. That matters because *long* is not the same class as *bad to
  analyze*: an hours-long ambient piece or a DJ set is one coherent thing and analyzes fine,
  while a radio show or a setlist rip is many different pieces of music that would collapse into
  one meaningless average. Only a human can tell those apart from the metadata, which is the
  whole reason this surface exists.

The estimate comes from your own catalog: stored per-track analysis times against known track
lengths, median-averaged (reported as `catalog-rtf`). With nothing analyzed yet there is nothing
to learn from, so the estimate is omitted rather than guessed.

`--apply-exclusions` additionally writes `apply-result.json` beside the exclusion file, carrying
the same counts as the console output plus any error — on failure as well as success — so a tool
driving truedat reads a file instead of parsing console text.

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

| Tool                                                                           | Enables                                                                                                | License  | Source                                                                                                                                                                                                      |
| ------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------ | -------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [Essentia](https://essentia.upf.edu/) `essentia_streaming_extractor_music.exe` | Mood analysis (default mode)                                                                           | AGPL-3.0 | [Essentia](https://github.com/MTG/essentia) / [x64 build](https://github.com/halrad-com/Truedat/tree/main/essentia-build/output-x64) / [dist](https://github.com/halrad-com/Truedat/tree/main/dist/truedat) |
| [FFmpeg](https://ffmpeg.org/) `ffmpeg.exe`                                     | Multi-channel downmix, opus decode retry, `bitUsage` / HF-analysis authenticity signals, `--transcode` | GPL-3.0+ | [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) / [deps](https://github.com/halrad-com/Truedat/tree/truedat-deps)                                                                                           |
| [FFmpeg](https://ffmpeg.org/) `ffprobe.exe`                                    | `--transcode` source-property matching                                                                 | GPL-3.0+ | [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) / [deps](https://github.com/halrad-com/Truedat/tree/truedat-deps)                                                                                           |

All tools are optional — truedat runs without them but the corresponding features are unavailable. The scan's identity fields (`fingerprint.v1`, `audioStreamSha256`, and `fileMd5` with `--file-md5`) are computed in pure-managed code with no subprocess. Custom x64 Essentia builds are in [`essentia-build/`](essentia-build/), ready to use from [`dist/truedat/`](https://github.com/halrad-com/Truedat/tree/main/dist/truedat).

### Essentia Builds

All Essentia tools are custom 64-bit builds from source. See [`essentia-build/`](essentia-build/) for build scripts and documentation. The x64 builds handle large files that exceed the 2 GB address space limit of 32-bit binaries.

### Building from Source

```cmd
build-truedat.cmd
```

Creates `dist/truedat/truedat.exe` (single file, ~1 MB). Requires .NET SDK 8.0+.

## Extracted Features

Truedat writes **features, not an interpretation**. It extracts descriptors from Essentia's output and stores them; the valence/arousal mapping and any mood scoring are made downstream by the consumer (MBXHub/AutoQ) from these fields. Nothing in `mbxmoods.json` is a mood coordinate — `valence` and `arousal` keys written by much older builds are stripped by `--migrate` as legacy.

A fully analyzed track carries:

| Group        | Count     | What                                                                                                        |
| ------------ | --------- | ----------------------------------------------------------------------------------------------------------- |
| Core         | 15        | Always present. 12 numeric, plus `key` and `mode` (strings) and `mfcc[]` (13 values).                        |
| Extended     | 40        | Nullable, omit-when-missing. Loudness envelope, silence, spectral shape, Bark/ERB/Mel band statistics, rhythm/tonal. |
| Tonal/rhythm | 15        | Nullable, Essentia-derived. `keyVotes` (3 profiles), tempo-histogram peaks, chords, tuning, `averageLoudness`. |
| Authenticity | 3 blocks  | `bitUsage`, `hfEnergyRatio` + method, `hfSpectralStructure`. Populated only where the codec and sample rate make them meaningful. |

That is **70 named feature fields**. Counting leaf *values* rather than fields — `mfcc[]` is 13 numbers, `chordsHistogram[]` is 24, each `keyVotes` profile carries its own strength — a complete entry holds **111 numbers**, of which **74 are scalar feature values** written as named keys.

These counts are measured from the JSON write surface, not maintained by hand: analyse one file and count the numeric leaves in the result, minus the nine that are catalog/identity/housekeeping (`trackCount`, `trackId`, `analysisDuration`, and the six `fingerprint.v1` audio properties). Prefer re-measuring to trusting this table — an earlier figure of "55" survived here for months after the tonal/rhythm wave was added, because a hand-maintained count has nothing to check it.

The 40 extended descriptors are what MBXHub persists for richer downstream scoring — sub-genre profiling, loudness normalisation, fingerprint-free clustering.

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

| JSON key            | Essentia Path                              | What It Measures                                      |
| ------------------- | ------------------------------------------ | ----------------------------------------------------- |
| `dynamicRange`      | `lowlevel.loudness_ebu128.loudness_range`  | EBU R128 loudness range (LRA) in LU — quiet↔loud span |
| `loudnessMomentary` | `lowlevel.loudness_ebu128.momentary.mean`  | Mean momentary loudness (400 ms gate), LU             |
| `loudnessShortTerm` | `lowlevel.loudness_ebu128.short_term.mean` | Mean short-term loudness (3 s gate), LU               |
| `replayGain`        | `metadata.audio_properties.replay_gain`    | ReplayGain adjustment for normalised playback, dB     |

### Extended — Silence profile

Fraction of analysis frames whose RMS falls below the given threshold — higher = sparser / more silent content.

| JSON key          | Essentia Path                     | Threshold |
| ----------------- | --------------------------------- | --------- |
| `silenceRate20dB` | `lowlevel.silence_rate_20dB.mean` | -20 dB    |
| `silenceRate30dB` | `lowlevel.silence_rate_30dB.mean` | -30 dB    |
| `silenceRate60dB` | `lowlevel.silence_rate_60dB.mean` | -60 dB    |

### Extended — Spectral shape

| JSON key                | Essentia Path                                   | What It Measures                                           |
| ----------------------- | ----------------------------------------------- | ---------------------------------------------------------- |
| `spectralRolloff`       | `lowlevel.spectral_rolloff.mean`                | Hz below which 85% of spectral energy sits (bright↔dark)   |
| `spectralComplexity`    | `lowlevel.spectral_complexity.mean`             | Count of significant spectral peaks per frame              |
| `spectralEntropy`       | `lowlevel.spectral_entropy.mean`                | Shannon entropy of normalised spectrum (noise↔tone)        |
| `spectralKurtosis`      | `lowlevel.spectral_kurtosis.mean`               | Peakedness of spectral distribution                        |
| `spectralSkewness`      | `lowlevel.spectral_skewness.mean`               | Asymmetry of spectral distribution                         |
| `spectralSpread`        | `lowlevel.spectral_spread.mean`                 | 2nd-moment spread around centroid                          |
| `spectralStrongPeak`    | `lowlevel.spectral_strongpeak.mean`             | Prominence of dominant spectral peak                       |
| `spectralDecrease`      | `lowlevel.spectral_decrease.mean`               | Average slope; negative ⇒ energy concentrated at low freqs |
| `spectralEnergy`        | `lowlevel.spectral_energy.mean`                 | Total spectral energy                                      |
| `spectralEnergyLow`     | `lowlevel.spectral_energyband_low.mean`         | Energy in low band (Essentia split)                        |
| `spectralEnergyMidLow`  | `lowlevel.spectral_energyband_middle_low.mean`  | Energy in mid-low band                                     |
| `spectralEnergyMidHigh` | `lowlevel.spectral_energyband_middle_high.mean` | Energy in mid-high band                                    |
| `spectralEnergyHigh`    | `lowlevel.spectral_energyband_high.mean`        | Energy in high band                                        |
| `hfc`                   | `lowlevel.hfc.mean`                             | High-frequency content — cymbals, sibilance, brightness    |

### Extended — Psychoacoustic bands

Shape statistics over perceptually-spaced filterbanks. Same five statistics across three scales: **Bark** (27 critical bands), **ERB** (40 equivalent-rectangular-bandwidth bands), **Mel** (40 mel-scaled bands).

| JSON key (per scale)     | Essentia Path                                   | What It Measures                                        |
| ------------------------ | ----------------------------------------------- | ------------------------------------------------------- |
| `{bark,erb,mel}Crest`    | `lowlevel.{bark,erb,mel}bands_crest.mean`       | Peak-to-mean ratio — high ⇒ tonal/peaky within the band |
| `{bark,erb,mel}Flatness` | `lowlevel.{bark,erb,mel}bands_flatness_db.mean` | Geometric/arithmetic mean ratio in dB (noisy↔tonal)     |
| `{bark,erb,mel}Kurtosis` | `lowlevel.{bark,erb,mel}bands_kurtosis.mean`    | Peakedness of the band distribution                     |
| `{bark,erb,mel}Skewness` | `lowlevel.{bark,erb,mel}bands_skewness.mean`    | Asymmetry of the band distribution                      |
| `{bark,erb,mel}Spread`   | `lowlevel.{bark,erb,mel}bands_spread.mean`      | Second-moment spread across the bands                   |

### Extended — Rhythm & tonal aggregates

| JSON key         | Essentia Path                | What It Measures                                                |
| ---------------- | ---------------------------- | --------------------------------------------------------------- |
| `beatsLoudness`  | `rhythm.beats_loudness.mean` | Mean loudness at beat positions — kick/snare intensity          |
| `chordsStrength` | `tonal.chords_strength.mean` | Mean chord-detector confidence (0-1)                            |
| `hpcpCrest`      | `tonal.hpcp_crest.mean`      | Crest of 12-bin harmonic pitch class profile — tonal focus (dB) |
| `hpcpEntropy`    | `tonal.hpcp_entropy.mean`    | Entropy of HPCP — high ⇒ atonal/chromatic, low ⇒ diatonic       |

### Extended — Tonal/rhythm wave (2026-07-22)

All nullable, populated on fresh analysis only (legacy entries lack them until re-analyzed). To fill an existing catalog, run scans with **`--refresh`** — entries missing these fields re-analyze, everything else stays cached; resumable (progress saves every 25 tracks), so run it in sessions until coverage is complete.

| field                                                                                      | Essentia source                        | meaning                                                                                                                                                  |
| ------------------------------------------------------------------------------------------ | -------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `keyVotes`                                                                                 | `tonal.key_{krumhansl,temperley,edma}` | Nested block: all three key-profile votes, each `{key, scale, strength}` — confidence + agreement for harmonic mixing (flat `key`/`mode` come from edma) |
| `bpmFirstPeak` / `bpmFirstPeakWeight`                                                      | `rhythm.bpm_histogram_first_peak_*`    | Dominant tempo-histogram peak + its weight                                                                                                               |
| `bpmSecondPeak` / `bpmSecondPeakWeight` / `bpmSecondPeakSpread`                            | `rhythm.bpm_histogram_second_peak_*`   | Secondary peak — half/double-time ambiguity evidence                                                                                                     |
| `chordsKey` / `chordsScale`                                                                | `tonal.chords_key` / `chords_scale`    | Chord-level tonality (vs the key-profile view)                                                                                                           |
| `chordsHistogram`                                                                          | `tonal.chords_histogram`               | 24-bin chord distribution                                                                                                                                |
| `chordsNumberRate`                                                                         | `tonal.chords_number_rate`             | Chord vocabulary richness                                                                                                                                |
| `tuningFrequency`                                                                          | `tonal.tuning_frequency`               | Reference tuning in Hz (~440)                                                                                                                            |
| `tuningEqualTemperedDeviation` / `tuningDiatonicStrength` / `tuningNontemperedEnergyRatio` | `tonal.tuning_*`                       | Temperament deviation / diatonic strength / non-tempered energy                                                                                          |
| `averageLoudness`                                                                          | `lowlevel.average_loudness`            | Simple 0..1 loudness (distinct from the LUFS envelope)                                                                                                   |

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

`--list-missing-smfm [path]` reports which catalog entries carry no SMFM block, with overall coverage, to `mbxmoods-smfm-missing.csv`. Because truedat only *reads* SMFM and never writes it, a missing block means the file has not been through Sony's tagger — a rescan cannot close that gap, which is why this report is a standalone mode rather than an entry in the `--stats` **Recommended** block.

The SMFM format itself is documented in [`smfm-tools/`](smfm-tools/) — an unofficial wire-format spec, a confidence-tiered state of knowledge (including the claims device testing refuted), an SMFM-vs-Essentia comparison, and standalone Python tooling for extracting SMFM data outside truedat.

Raw features are stored so MBXHub can compute valence/arousal at runtime with tunable weights — no re-scan needed to adjust the formulas. The 40 extended fields are persisted for future downstream scoring (sub-genre profiling, loudness normalisation, clustering). Every extended field is nullable; legacy entries produced before the extended set was added simply omit those keys rather than storing zeros. The `analysisDuration` field records how long Essentia took to analyze each track (in seconds).

`fileMd5` (MD5 of the file bytes; maintained only with `--file-md5` — nothing downstream consumes it), `fingerprint.v1` (cheap composite — TagLib parse + 64 KB invariant-region MD5; fields include `fileSize`, `pathTail`, `durationMs`, `sampleRate`, `channels`, `bitDepth`, `codec`, `bitrate`, `encoder`, `audioHead64kMd5`, plus the nested `mp3LameTag` block for MP3s), and `audioStreamSha256` (streaming SHA-256 over the audio invariant region — content-stable across tag edits and file moves) are all computed concurrently with the Essentia feature extraction in pure-managed code. No subprocesses, no path-escape exposure, no codepage drama. Wall-clock per track is roughly `max(analysis, slowest-hash)` rather than the sum, with Essentia dominating. `audioStreamSha256` is the primary content thumbprint — it survives file moves, renames, and tag edits, and works identically on NTFS, exFAT, and any path Windows can open.

The `bitDepth` and `encoder` sub-fields enable cross-checking a file's claimed format against its actual content — a 320 kbps MP3 whose `encoder` is `Lavc58.x` (ffmpeg transcoder) is almost certainly transcoded from a lossy source; a 24/96 FLAC whose `bitDepth=24` but whose `bitUsage.lowestNonZeroBit` lands at 16 is upsampled CD audio. The `truedat.*` verdict block (see below) consumes these fields plus the `bitUsage` / `hfEnergyRatio` / `hfSpectralStructure` signals to produce a per-track classification.

For MP3 specifically, the `mp3LameTag` block inside `fingerprint.v1` is populated when the file carries a Xing/Info+LAME header. Fields include `version` (e.g. `LAME3.100`), `vbrMethod` (CBR / ABR / VBR method N), `lowpassHz` (LAME's chosen low-pass cutoff — the **single strongest transcode-from-low-bitrate tell**: a "320 kbps" MP3 with `lowpassHz: 16000` was almost certainly transcoded from 128 kbps source), `encoderDelay` / `encoderPadding`, `musicCrc`, `infoTagRevision`. Parsed pure-managed from the first ~8 KB of the file; no subprocess. Files re-encoded by ffmpeg (`Lavc...`) typically have no LAME tag at all — its absence on a non-Xing MP3 is itself a soft signal.

For lossless containers claiming ≥24-bit depth, the `bitUsage` block carries a sub-second ffmpeg-piped PCM walk over 30 s mid-track that builds a trailing-zeros histogram of the s32le samples. Fields: `lowestNonZeroBit` (where signal actually starts in the 32-bit representation — true 24-bit lands at ~7–8 after ffmpeg's alignment, while 16-bit content padded to 24-bit lands at ~16), `bottomBitActivity` (fraction of non-zero samples at the resolution boundary), `effectiveBits` (continuous signal for confidence scoring, clipped to [0, 32] — the s32le sample-space ceiling), `samplesAnalyzed`, `method` (currently `ffmpeg-s32le-30s-mid-native`; the trailing `-native` records that the walk runs at the source's native sample rate rather than a forced 44.1k — earlier values without the suffix were biased by resample interpolation noise). The applicability gate runs as a ~5 ms TagLib peek **before** the ffmpeg decode so lossy / sub-24-bit files are skipped without spending 30 s of decode on data that would be meaningless. Populated only during fresh analysis or via `--verify --backfill --backfill-level features|all`. Null on ffmpeg-absent installs.

The orthogonal `hfEnergyRatio` signal is the fraction of audio energy above 22.05 kHz, measured at the source's native sample rate via a hand-rolled radix-2 FFT walk over 4096-sample Hann-windowed frames (50 % overlap). Only populated when `sourceSampleRate > 44100` (CD-rate files have no Nyquist headroom above 22 kHz, so the test isn't applicable). Catches an evasion that `bitUsage` can't: an upsampler that adds dither to 16/44.1 → 24/96 produces plausible-looking LSB activity, but it can't fabricate audio energy above the original Nyquist. Bin-sharp values run small (genuine 24/96 hi-res lands at ~1e-5; upsampled-from-44.1 content lands at literal 0 after Lanczos suppression). The `hfEnergyMethod` companion field carries the algorithm identifier (currently `managed-fft-radix2-30s-mid-native`).

The same FFT pass also emits `hfSpectralStructure: { flatness, peakToMean, imagingSymmetry, method }` — Wiener-entropy flatness over the HF band, peak-to-mean ratio of HF bins, and Pearson correlation of HF bins against their mirror partners in the source band. The Phase 5 signal catches ffmpeg-upsampled fake hi-res that the bit-level signals (`bitUsage`) miss entirely: upsampled content has very low flatness (energy in a few narrow imaging spikes against an otherwise-empty HF band) and high peak-to-mean (often 80–180), while genuine HF content lands either broadband (orchestral, flatness ~0.5) or peaky-but-uncorrelated-with-mid-band (synthesised cymbals, flatness ~0.01 but with one dominant harmonic). Together, `bitUsage`, `hfEnergyRatio`, and `hfSpectralStructure` are the three independent signals the verdict block weights to answer "is this 24/96 claim genuine?".

### Authenticity verdict (`truedat.*` block)

Each track in `mbxmoods.json` carries a nested `truedat` block with two authenticity verdicts, a talk-vs-music verdict, and per-question confidence:

```json
"truedat": {
  "hiresGenuine":            "yes" | "no" | "unknown" | "n/a",
  "hiresConfidence":         0.85,
  "lossyTranscodeLikely":    "yes" | "no" | "unknown" | "n/a",
  "lossyTranscodeConfidence": 0.92,
  "speechLikely":            "yes" | "no" | "unknown" | "n/a",
  "speechConfidence":        0.78,
  "speechMethod":            "truedat-speech-v1-untuned-2026-07-22",
  "method":                  "truedat-v1-fft-corpus1-2026-05-18"
}
```

Four-string enum, **not** a bool — collapsing `"unknown"` into yes/no is exactly what produces false positives and negatives in the wild. `"unknown"` and `"n/a"` are first-class outcomes. `"n/a"` means the test wasn't applicable to this file (hi-res check on a 16-bit FLAC, transcode check on a FLAC, etc.); `"unknown"` means it was applicable but the signals are weak or disagreeing.

The block is **omitted entirely** when none of the three verdicts reached a decided `"yes"`/`"no"` — i.e. `hiresGenuine`, `lossyTranscodeLikely`, and `speechLikely` are all `"unknown"` or `"n/a"` (legacy entries without `fingerprint.v1`, weird-codec files, or entries lacking enough signal). Run `--verify --backfill` to populate the authenticity fields and pick up a real verdict on the next pass; `speechLikely` has no backfill path — it's computed from features already present, so it fills in for the whole catalog automatically the next time each entry is saved (any scan, cache hit or miss).

Multi-signal weighted voting per question. Hi-res verdict combines four signals: `bitUsage.lowestNonZeroBit` (0.40), `hfEnergyRatio` (0.40), `bitUsage.effectiveBits` (0.20), and `hfSpectralStructure` (Phase 5 — Signal F, 0.35) — total available weight 1.35 when all signals vote. Transcode verdict (MP3 only) combines encoder string, MP3 LAME tag lowpass, and LAME tag presence with weights 0.30 / 0.35 / 0.20. (An earlier `spectralRolloff` signal was dropped after corpus validation showed it produced false positives on naturally low-HF material.) ±0.7 **normalized**-score threshold (score / maxWeight) means signals must collectively cross 70% agreement for a yes/no verdict; one strong signal alone abstains as `"unknown"`. Signal F intentionally abstains in the middle band (`0.005 ≤ flatness ≤ 0.5` or `peakToMean ≤ 50`), reinforcing existing yes/no calls without driving them on its own — corpus-1 tuning showed this discipline avoided false flips on peaky-but-genuine cymbal content.

`speechLikely` classifies talk content (podcasts, audiobooks, spoken-word — all analyzed by default like anything else now, or any music-library track that's actually speech) vs. music, voting over `danceability`, `chordsStrength`, `silenceRate30dB`, `zeroCrossingRate`, `bpmFirstPeakWeight`, and tonal `keyVotes` strength. `"yes"` additionally requires the zero-crossing signal to fire on its own — a sine-tone or ambient bed can share talk's shape on the other signals but sits low on zcr, and without this gate it would wrongly reach `"yes"`; tone/ambient content demotes to `"unknown"` instead. A second gate requires `danceability < 0.50`: sparse, live and free-form *instrumental* music craters on every other signal exactly like speech does, and danceability is the one that separates them (genuine speech measures 0.00; real-music false positives ran 0.66–1.10). `"yes"` is a candidate for an exclusion rule, never a `--migrate` prune — `--migrate` never removes entries. Review the `"yes"` set with `--list-speech`, then write a rule via a decisions delta + `--apply-exclusions` if warranted. `speechMethod` carries its own tag (`truedat-speech-v1.2-untuned-2026-07-22`), independent of the authenticity `method` tag, since the two verdict families tune on separate schedules.

Computed inline at write time, not persisted in cache. Threshold changes ship without a rescan; the method tags bump when thresholds change so consumers can detect algorithm drift. Per-signal vote+weight trace available via `--audit` for debugging.

**Current method tag: `truedat-v1-fft-corpus1-2026-05-18`** — Phase 5 calibration pass against the 23-file hand-labeled corpus (`docs/reviews/2026-05-18-phase4-corpus-validation.md`), incorporating the FFT-derived `hfSpectralStructure` signal. The corpus-1 retune closed the ffmpeg-upsampled-fake-hi-res gap (3/3 fakes now correctly suppressed or classified). One known gap remains for Phase 5+: LAME-to-LAME re-encode chains verdict `"no"` because the second LAME encode rewrites the Xing tag — needs cascade-encode artifact detection. Consumers should treat verdicts as high-confidence-but-not-perfect; the method tag will bump to `truedat-v1-…-YYYY-MM-DD` on each subsequent calibration pass.

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
