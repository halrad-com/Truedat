---
name: truedat-chunk-verify-backup
description: Chunked scanning across machines, --verify and --backfill tiers with coverage receipts, catalog backups, --snapshot and --restore. Read before touching --chunk, --verify or CatalogArchive.
---

<!-- Moved verbatim from the root CLAUDE.md on 2026-10-22 (Claude Code doctor, check 4) so it loads only when relevant. The root file keeps the universal rules and a pointer here. -->

## Chunked scanning across machines

`--chunk M/N` uses hash-mod assignment: `(PathComparer.GetHashCode(path) & 0x7FFFFFFF) % N == M-1`, in **one** helper — `ChunkOwns` — used by both the work filter and the ledger scope, so what a shard does and what it records cannot diverge. `PathComparer`'s hash is FNV-1a, deterministic across processes and machines, separator-normalized, case-folded. Same path → same bucket on every box, **regardless of XML differences**. Asymmetric libraries are tolerated. Output filenames auto-suffix with hostname to prevent shard collisions when machines write to a shared library directory. Combine with `--merge-moods`.

**Evaluate library-wide, ledger shard-scoped** (I-4). The four pre-scan filters (`FilterRemoteUrls` / `FilterExclusions` / `FilterVideoFiles` / `FilterNonAudio`) deliberately run BEFORE the chunk filter, so `IsExcluded` sees every track and per-rule `MatchCount` is library-wide and identical on every machine — that cross-machine agreement is what makes the stale-rule signal trustworthy, and it must not be "fixed" by moving the chunk filter up. But the ledger is a per-run artifact: `_ledgerScope` (set from `ChunkOwns` in MoodsMode, `_ => true` otherwise) gates the `mbxmoods-skipped.csv` row, the `--audit` line and the printed count in all four filters, so the union of N hostname-suffixed ledgers is the library rather than N copies of it. Removal from the work list stays library-wide — a video in another shard's bucket is not this shard's work either way. Under `--chunk` the printed counts carry ` (this chunk)` and a line states that rule counts are library-wide while the ledger is the shard's share, because the two numbers legitimately disagree. `--merge-moods` still reconciles **only** catalogs, never the CSV ledgers — it is a catalog merger and the ledgers no longer need reconciling.

Mutually exclusive with `--analyze-file` / `--file-list` / `--folder` / `--migrate` / `--fixup` / `--merge-moods` / `--synthesize` / `--seed-moods` / `--hash-only`.

**Verify is resilient to the source going away, and says so in a receipt.** `File.Exists` cannot
distinguish a deleted file from an offline volume (NAS down, WiFi/ethernet dropped, external
drive unplugged or spun out), so an unguarded verify *completes* with every entry MISSING — and
with coverage receipts in play it would certify a slice it never read, which is worse than
failing because it manufactures assurance. So verify probes each distinct library root before
walking (refuse + exit 3 if any is unreachable) and **re-probes on the first MISSING**, stopping
the walk when a volume drops mid-pass. On either path nothing is written: no CSV, no receipt, no
backfill save. This is the guard `--fixup` already had; verify was the gap. **Do not "optimize"
the mid-walk re-probe away** — it only fires on a MISSING result, so a healthy run pays nothing,
and it is the only thing standing between an 18-hour pass and a confidently wrong result.

Each completed run writes `<catalog>-verify[.<host>.<M>of<N>].receipt.json`: the slice it walked,
an order-independent XOR fold of those paths, and the same fold over the whole catalog.
`--verify-coverage` combines the receipts and proves the pass covered the catalog — every shard
present exactly once, all against one catalog state, folds XOR-ing to the catalog's own. The
fold is what makes it a proof rather than a tally: a skipped shard leaves a residue, a
double-counted shard cancels itself. It attests to the **process**, not the audio.

**`--verify` is the deliberate exception (2026-08-16)** — there, `--chunk` is what makes an
interrupted pass restartable rather than a way to spread work across machines. A ~6 TB / 156k
library over WiFi is an 18-24 hour walk, and one-shot means a NAS blip costs all of it. Each
shard verifies its `ChunkOwns` slice and writes its own
`mbxmoods-verify.<host>.<M>of<N>.csv`, so a completed shard is durably done. **The shard filters
the WALK ONLY, never `allTracks`** — under `--backfill` the save is
`SaveResults(moodsPath, allTracks)` over the whole dictionary, so a filtered dictionary would
write a catalog containing just that shard and truncate the file to 1/N. Do not "simplify" it by
filtering the load. `--backfill` under `--chunk` additionally warns that shards must run
sequentially: each save rewrites the whole catalog from its own loaded copy, so sequential shards
accumulate while concurrent ones silently drop each other's backfills (consistent with truedat
being single-writer by design).

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

## Catalog backups & snapshots

`mbxmoods.json` is ~900 MB of repetitive text and DEFLATE-compresses ~8–10× (~120 MB),
so the accumulating full-copy backups the mutating modes drop were the real footprint pain.
`CatalogArchive` (own file) is the one compress/decompress helper: **ZIP on write** (Windows
opens `.zip` natively; a ZIP is a container so a snapshot bundles multiple files) and a
**format-sniffing read** that accepts zip (`PK`), gz (`1F 8B`) and plain (`{`, tolerating a
leading BOM) so older plain backups still restore. Streaming throughout (`ZipArchive` entry
stream ↔ `FileStream`), O(1) memory like `SaveResults`; net48 ships `ZipArchive`/`GZipStream`
so **zero new dependencies** (only a framework `<Reference Include="System.IO.Compression" />` —
`GZipStream` lives in `System.dll` and already worked, but the ZIP container types don't).

- **The live `mbxmoods.json` stays PLAIN JSON — and as of 2026-08-21 that is a hard constraint,
  not a sequencing gate.** MBXHub boots its mood cache from a byte-offset index into the live
  catalog (`CatalogSpanIndex`), keeping ~20 bytes per track and seeking one record on demand.
  A DEFLATE stream has no addressable byte N, so compressing the live file does not slow that
  index down, it removes it. The BACKLOG stretch therefore is not "gated on restfulbee,
  consumer-first" — that phrasing asked whether the hub *could read* a compressed catalog (it
  could), which is the wrong question. Taking the stretch means first replacing the span index
  with something that survives compression. This half is backups + snapshots only, which
  nothing seeks into. Do not compress the live file here.
- **Backups (automatic).** The four mutating modes (`--fixup`, `--remap`, `--merge-moods`,
  `--migrate`) call `BackupCatalogCompressed` instead of `File.Copy`: it writes
  `mbxmoods.json.bak.<ts>.zip` and rotates to keep the newest `--keep-backups N` (default 5,
  **rotation never truncation** — `0` keeps all; same ruling as `mbxmoods-skipped.csv`).
  Rotation matches `<name>.bak.` (trailing dot), so it never touches the transient
  `SaveResults` `.bak` swap file or the live catalog.
- **`--snapshot [path]`** (read-only over the live catalog) streams the catalog into
  `mbxmoods-snapshot-<ts>.zip` bundling `mbxmoods.json` + `manifest.json` (`trackCount`,
  `generatedAt`, `contentSha256`, `catalogBytes`) + `mbxmoods-exclude.json` when it sits beside
  the catalog — a complete, self-verifying, restorable state in one file. The SHA-256 is
  computed in the same single read pass that streams the catalog into the entry; `trackCount`
  comes from a bounded streaming read of the leading window (`TryReadTrackCountFromPrefix`),
  not a full-DOM load.
- **`--restore <archive>`** rebuilds `mbxmoods.json` from a snapshot / backup / `.gz` / plain
  catalog (sniffed). It backs up any existing destination (compressed + rotated) **before** the
  atomic swap, so a restore is itself reversible, and it restores the **catalog only** — a
  snapshot's bundled exclusion file is left in the archive (blast radius = one file; extract by
  hand to roll policy back too). Path resolution mirrors `--stats`/`--verify` (`--moods`/positional/
  cwd via `ResolveMoodsCatalog`).

Deliberately NOT built: delta/diff archives, zstd/brotli (not in net48), and the compressed
live catalog (needs restfulbee coordination — see BACKLOG).
