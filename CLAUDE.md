# Truedat — LLM Session Context

This file is loaded automatically by Claude Code / Claude Agent sessions scoped to this repo. Keep it short and decision-making-focused; deep reference material belongs in `README.md` and `SBOM.md`. Internal design notes and session journals live in the local `docs/` directory, which is gitignored — never reference them from any file that ships in the repo.

## What this project is

Truedat is a Windows .NET command-line tool that analyses a music library's audio and writes `mbxmoods.json` (mood + Essentia features + identity + authenticity). The output file is consumed by MBXHub's AutoQ engine (separate repo).

- Build: `build-truedat.cmd` (requires .NET SDK 8.0+). Single-file output at `dist/truedat/truedat.exe` (~1 MB, ILRepack-merged).
- Runtime deps: `essentia_streaming_extractor_music.exe` (required for default scan), plus optional `ffmpeg.exe` (multi-channel downmix, `Unsupported codec` retry — e.g. `.opus`, since this essentia build lacks libopus — the bitUsage/HF authenticity signals, and the standalone `--transcode` utility) and `ffprobe.exe` (matching source rate/depth in `--transcode`). The legacy fingerprint pipeline (`--fingerprint` / `--md5-only` / `--quick-fingerprint` / `--details` and the `essentia_streaming_md5.exe` / `fpcalc.exe` binaries) was removed 2026-07-11 (v5.4.0) — identity is pure-managed.
- Framework: **.NET Framework 4.8**. No .NET 6/8 APIs, no `ValueTask`, no `init` setters on public types. Use `System.Text.Json` (merged via ILRepack).
- The v2 SQLite store (preview; store and edges are **opt-in** again since 2026-10-26 — operator: "lets stop opting into the store… so for now lets default them off". They were on by default from 2026-10-18 on one argument: edges can only be folded while the extractor already has the audio open, so deferring them costs a full re-analysis — about 2 hours during a scan against 15 hours after it. What that argument never had was a measured benefit. Measured on a 72k library, same build and same 32 workers, the default cost **+2h04m per full scan** (13h14m → 15h18m), **2.7× the disk** (285 MB → 767 MB) and +2.5 GB peak memory, while the edge path is still behind a hub switch with no A/B and no listening test; the deferral asymmetry only argues for paying now if the capability is wanted, and unproven it inverts. **Off means do not CREATE one, never do not USE one** — an existing store is still adopted and kept current, so no instance silently stops tracking its catalog. `EnsureDefaultStore` runs at each SCAN's entry — never in the arg block — so no read or maintenance verb can create a store: no store FILE **and** `--store` / `--store-dir` / config `store true` asked for one → make one; an existing catalog is SEEDED first (`ImportCommand`, no audio read) and the catalog published from it (`StoreScan.Publish`, which rebinds a catalog an earlier, now-deleted store published — without it T18i refuses "another store state"); nothing is re-analysed, so existing tracks get edges only under an explicit `--refresh`. Never under `--chunk` or a per-file mode without `--moods`; `--no-store`/`--no-edges` or config `store false`/`edges false` turn it off; SQLite unavailable or a failed seed = a scan without a store and a note, never a refusal, and a failed seed deletes its half-built files so the next run cannot adopt them. The 2026-10-13 off ruling was about a store ~15× the catalog — the `main` artifact and untyped columns, both gone; it is ~2× now. `ShouldAdoptExistingStore` still adopts a store already at the resolved location — but edges no longer ride along with it: `--edges` or config `edges true` is the only thing that folds them, so an adopted store keeps being written while its edge coverage stops growing until someone asks. The advisor follows the same rule (operator: "if its defaulting to false — as the default — it does not recommend the option to be run"): a scan or `--stats` on an instance that never opted in says nothing about stores or edges at all. The scan no longer writes the `main` artifact; `--store`, `--edges`, `--edges-export`; `--refresh` fills missing edges) is documented in `STORE.md` — tables, location rules, the typed columns (schema v3, 2026-10-15: every numeric field held at the precision `FieldPrecision` declares — scaled INTEGER, int32 vectors, a sparse histogram — no six-figure rewrite anywhere on the store path; a v2 store migrates on open with a backup first and refuses rather than round), the typed `edge` table and the per-copy `verdict` projection (schema v4, 2026-10-16: default edge row = scalars + HPCP; the arrays only under `--edge-arrays`, sticky per track, removed only by `--edge-arrays-drop`; verdict rows derived by the catalog writer's own function from the stored rows, a row iff the block, re-derived at open when either algorithm tag changes), and how to open the file. The scan's catalog writer and `mbxmoods.json` bytes are untouched by it, and a store export writes the same bytes the scan would.
  **With a store in force the store is the authority, so every state-moving verb moves the store, not the derived files** (2026-10-16): all nine catalog-changing verbs run through one write path (`RunStoreBackedVerb` — publish from the store, run the verb unchanged, write its result into the store exactly in one transaction, republish); `--snapshot` bundles the store as a `VACUUM INTO` copy (never a file copy — a live WAL store's committed state spans three files) with its schema/generation/SHA-256 in the manifest; `--restore` of such an archive restores the STORE (validating it opens, passes `quick_check` and is not from a newer schema before replacing anything, snapshotting the one it replaces) and republishes the triple from it at a NEW generation, because a restore is a consumer-visible mutation even when the restored bytes are older; a catalog-only archive still refuses. `--merge-moods` takes another store as a `--merge-source` (a store minted on another machine; a `--chunk` shard itself runs without a store — a store-backed shard is refused — so a shard's input is its catalog), read across **every** root — a store from another box names its own, so reading only the primary would drop that machine's share. The merge's null rule (2026-10-24): the later source wins a path with what it *carries* — absent and `null` never clear a stored value, a supplied zero or empty string lands, the writer-cut fields follow the winner by content identity, a failed merge supplies nothing. `--store-info` reports the state the hub's OOBE half needs: generation, the published catalog's generation, whether the files are behind it and why, edge-array capture, and the one command that closes the gap — and that command is a SCAN (`truedat`), never `--store-export` / `--edges-export`: every scan publishes the catalog, sidecar and companion from the store at its checkpoints and at the end, analysed entries or not (operator 2026-10-20: "it happens automatically"); `StoreScan.ScanPublishes` is the one remedy string every files-behind message carries, and the export verbs stay only for a deliberate export elsewhere.
  **Routine maintenance rides the scan (operator 2026-10-22: "prune, we don't want to keep dead wood")**: a fold at the current edge producer version prunes the track's rows at every other version (`EdgeStore.PruneSupersededVersions`; the run rows stay as history), every scan sweeps the store for the residue an earlier version bump left, and after the final publication a library scan or `--folder` scan compacts the file with a plain WAL-mode VACUUM every time (`VacuumCommand.Compact` — not `--file-list` / `--analyze-file`, which would rewrite the whole store per autoscan call; no free-page gate, which missed the half-empty pages that hold most reclaimable space — never the verb's journal-mode switch, which fails while the hub holds the store). The store summary and `store-result.json` account for every entry in exactly one outcome bucket (described / re-keyed / edited / remeasured / confirmed / drift / replaced / filled / copy facts only / unchanged / stale / unrooted / unstorable; `ImportReport.Accounted == Entries`, pinned); a fresh measurement from a different producer that agrees within noise *confirms* the description (fresh values, the measuring run), so a seeded description becomes the scan's once the scan has measured it.

## Schema invariants (always loaded)

The field-by-field schema is the `truedat-schema` skill. These four rules hold whatever you touch:

Four I/O surfaces must stay in sync: `AnalyzeWithEssentiaCore` (extract), `WriteTrackEntry` (write), `LoadExistingMoods` (read via `ParseTrackFeaturesFromJson`), and `RebuildCacheEntryCore` (cache-reuse copy). Adding a field means touching all four.

**A fifth surface decides whether the field reaches the file at all: the emission registry.** `FieldPolicy` (`Truedat/FieldPolicy.cs`) loads `mbxmoods-schema.json` from beside the exe — exclude-default, so anything NOT listed is emitted — and a field in its `excluded` block is skipped by the writer AND stripped from existing catalogs by `--fixup`. One registry shared by both so scan-time emission and cleanup cannot drift. A built-in default matching the committed schema applies when the file is missing, so a cut stays deterministic. **Cut today: `bpmHistogram`** (~94% zeros, write-only — the bpm peaks are separate fields) **and `trackId`** (iTunes-XML-local numbering, no consumer — the hub skips it — churns on every XML re-export and is meaningless across merged libraries). Do not document either as part of the output; a current scan emits neither. **Cutting is cheap, un-cutting is a multi-day re-analysis** — the raw extractor JSON is deleted at parse, so there is no parse-only route back.

`fingerprint.v1` is **one flat property name containing a dot**, not a `fingerprint` object with a `v1` child (`WriteFingerprintV1`, `Program.cs:21199`). MBXHub reads it that way too (`MoodCache.ReadTrackEntry`, `CatalogSpanIndex`). The README drew it nested for months and nobody noticed, because the only consumer was written against the bytes rather than the doc. Legacy `audioMd5` / `chromaprint` / `chromaprintDuration` keys in old files are ignored on read and stripped by `--migrate`.

## Rounding convention (extended features)

`Opt(v, int dp = 4)` / `OptN(root, path, int dp = 4)` — default 4 dp. Overrides:

- 2 dp: dB/LU values (`loudnessMomentary`, `loudnessShortTerm`, `replayGain`, `beatsLoudness`, `hpcpCrest`, `hfc`), `spectralComplexity`.
- 1 dp: Hz (`spectralRolloff`).
- 6 dp: tiny spectral values (`spectralEnergy*`).
- 12 dp: `spectralDecrease` — it lives at ~1e-9, so 6 dp rounded **every track to 0.000000**
  from 2026-02-13 until `8c06ec2`. Do not "tidy" it back into the 6-dp group; the extra
  places are the whole signal.

Don't regress to a uniform 6 dp — it inflates JSON size without analytic value.

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
  entry on a **classification** — that whole class was deleted in the Heuristics → Evidence arc.
  **Exactly two modes can cost an operator an entry, and each acts on a different ground truth.**
  `--prune-excluded` (2026-08-15) acts on **operator-written rules**: it removes entries matching
  `mbxmoods-exclude.json`, closing the gap that a rule gates future scanning but never retired the
  entries scanned before it existed. That is emphatically **not** the deleted class — it prunes on
  the same authored authority as any `include`/`exclude` rule, `include` still wins, and no verdict,
  genre heuristic or embedded marker is consulted. Its guardrails are `--dry-run` (exact removal
  list, writes nothing), a compressed rotated backup before the atomic swap, sidecar regeneration,
  and a refusal (not obedience) when `--no-exclusions` is passed, since that would bypass the very
  authority it acts on. Deliberately a separate verb rather than a branch of `--fixup`: fixup needs
  the XML and reconciles paths, this needs neither, and growing a second removal class onto an
  existing destructive verb would change what `--fixup` costs without the operator typing anything
  new. **If a future request is "prune the speech-likely entries" or "prune by genre heuristic",
  that is the deleted class wearing this verb's clothes — the answer is to write a rule and run
  this, not to teach it to classify.**
  `--fixup` is the other, and it does so on filesystem ground truth
  (`File.Exists` says the file is gone), not inference — and only once it has proven the file's
  *volume* is reachable. A bare `File.Exists`/`Directory.Exists` false cannot tell "file deleted"
  from "share offline", so before reconciling, `--fixup` probes each distinct library root once
  with `ProbeRootReachable` (`File.GetAttributes`, which surfaces the OS error a downed share
  throws instead of swallowing it to false) and **refuses the whole run** (exit 3, nothing written,
  no backup churned) if any root is unreachable — an unmounted NAS must never read as a mass
  deletion. The probe is **root-level, not file-level, on purpose**: network-down is a volume-wide
  condition, so one probe of the share answers "is the NAS up right now" for every file on it, and
  a healthy root always answers (so *any* error = unreachable, free of the file-level
  deleted-vs-offline ambiguity). It also **re-probes the root at each gone-decision** so a NAS that
  drops *mid-reconcile* aborts on the first file it touches after the drop, not just one that was
  offline at start. A `MassOrphanImplausible` backstop refuses when a run would orphan more than
  `MassOrphanFraction` (0.20) of the catalog above a floor (50) — the flaky-link / bulk-delete case
  a per-volume probe can't see. `--force-clean` is the sole, deliberate override for all three
  (unreachable-at-start, unreachable-mid-run, mass-orphan), for purging entries on a volume the
  operator KNOWS is gone. Their contract briefly listed a `purge` mode for truedat; it was removed
  on our correction. Do not implement one to satisfy a doc — `--force-clean` acts on ground truth
  (the volume is unreachable *and* the operator asserted it), not on a classification.
- **Reason strings in `preview.json`'s `review[].reasons`** are rendered by the hub, not parsed
  by it — confirmed on their side. Enriching the text is safe; the shape is not.
- **`ruleId` in `mbxmoods-review.json` is a KEY, and its format is a contract.** 64-bit
  FNV-1a over `ExclusionRule.Identity()`, rendered as **16 lowercase hex chars**. The hub
  pins it, persists it, and keys an irreversible action (undo-an-exclusion) on it, and it has
  deliberately declined to re-normalize on its side — a second normalization is how two
  components come to disagree about the same key, and the one gating an unrecoverable action
  is the worst place for that. So: **lowercase-hex-only is load-bearing, and changing the
  width or the encoding is a breaking change that needs a heads-up to restfulbee, not a
  silent bump.** There is deliberately no mapping between id widths — a consumer holding an
  old one re-reads, never translates. The id is derived, never stored, so
  `mbxmoods-exclude.json` is untouched by any of this; that is the property that made
  widening from 32 to 64 bits cost a rebuild instead of a migration, and it is worth keeping.
  Note and sha stay out of `Identity()` so annotating a rule does not renumber it; changing
  kind, action or target does, because that is a different rule.

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

## Deep-dive skills (lazy-loaded)

The long reference sections that used to live here are skills under `.claude/skills/` — loaded only when the task touches that
area, so a session pays for them only when it needs them (moved verbatim 2026-10-22; the invariants and "never" rules stayed here):

- `truedat-schema` — the mbxmoods.json field waves, nullability, SMFM, the verdict block.
- `truedat-scan-pipeline` — worker fan-out, resample-once, staging, hash-only, cache tiers + re-extract gate, scan health.
- `truedat-exclusions` — exclusions, playlists, the merge discipline, the speech taxonomy (no podcast detector), --preview.
- `truedat-chunk-verify-backup` — --chunk, --verify / --backfill, receipts, backups, --snapshot / --restore.
- `truedat-duplicates` — the --duplicates report and its emitters.
- `truedat-catalog-discovery` — which catalog / library a bare command means.
- `truedat-authenticity` — the authenticity sprint state and the verdict's known gaps.
