# The truedat store (preview, v2)

A SQLite database, `truedat.db`, that holds everything `mbxmoods.json` holds and more, keyed by
identity rather than by path. With a store in force it is the authority: every scan and every
catalog-changing verb writes the store, and `mbxmoods.json` — still exactly what MBXHub reads — is
published from it, with the sidecar and the edges companion. The store is also the source for
everything that is not in the catalog: provenance, the edge rows,
the identity history. Nothing in the store changes how a scan without `--store`
behaves, and nothing is installed: truedat uses the SQLite that ships with Windows (`winsqlite3.dll`,
3.40 or newer, checked at open with a plain refusal when missing).

**Store only.** `truedat --config publish false` (or `--no-publish` for one run) makes the store the
only output: no `mbxmoods.json`, sidecar or `mbxedges.v1.json` is written beside it. Files already
there are kept once in `mbxmoods.json.retired.<time>.zip` and removed. Edges still go into the store,
and `--store-export` / `--edges-export` still write files when asked. `--publish` turns publishing back
on for one run. Use it only with a hub that reads the store; a hub that reads the files sees nothing.

## Where it lives

- Beside a local catalog: `<library>\truedat.db`. When the catalog folder is a network share the store
  goes to a local per-library path under `%LOCALAPPDATA%\truedat\<library-id>\` instead; `--store-dir
  <dir>` (also a config key) relocates that layout. SQLite on SMB is refused unless you name it with an
  explicit `--store <path>`, and then it opens with exclusive locking.
- The store and edges are **opt-in**. A scan makes a store only when `--store`, `--store-dir` or config
  `store true` asks for one, and folds edges only under `--edges` or config `edges true`. On a 72,000-track
  library the pair cost about two hours on a full scan and about 2.7× the disk of the catalog and sidecar
  alone, and the edge data has no proven mixing benefit yet — so they are bought deliberately. The catch
  worth knowing: edges can only be folded while the extractor already has the audio open, so adding them
  later costs a full re-analysis.
- **A store that already exists is still used.** Off by default means truedat does not create one, not that
  it ignores one: an existing `truedat.db` is adopted and kept current on every scan. Edges do not ride
  along — an adopted store keeps gaining descriptions while its edge coverage stands still until `--edges`.
- When a store is asked for on a library that already has a catalog, that catalog is seeded into it first
  (an import: no audio read) and the catalog is published from it, so the store never holds only the tracks
  one run analysed — and nothing is re-analysed: tracks already in the catalog get edges only under an
  explicit `truedat --refresh`. Only a scan makes a store (never a read or maintenance verb), and never a
  `--chunk` shard or a per-file mode without `--moods`. `--no-store` / `--no-edges` turn them off for one
  run. Asking never makes a scan refuse: no SQLite on the machine or a failed seed means a scan without a
  store and a note; an extractor older than `.5` skips edges with a note.
- Beside the store: `store-result.json`, `store-import-result.json`, `store-info.json`.
- Not beside the store: the in-flight edge stashes. They are transient — written during a scan, consumed by
  the flush that commits their rows, deleted — so they live in `pending\` under the stage directory and move
  with `--stage-dir`. Leftovers from an interrupted scan are swept.

## What is in it

One row of file facts per copy, one audio description per media:

| Table | One row per | Holds |
|---|---|---|
| `file_entity` | media (UUIDv7) | the anchor that survives retag, move and re-encode |
| `identity_facet` | (entity, kind, value) | content hashes by algorithm, canonical paths; primary flag, scheme precedence |
| `library_root`, `machine` | root on a machine | which box can read which paths; roles primary / mirror / endpoint |
| `track_location` | copy | path (relative, canonical, display), the catalog's `lastModified`, present / missing / retired |
| `track_content` | media | every Essentia descriptor, bitUsage, the HF signals, the content key — once however many copies |
| `track_copy` | copy | tags as found in the copy, `fingerprint.v1`, `fileMd5`, SMFM, `analysisDuration` |
| `run` | attempt | producer + version, entity, content hash, location, outcome, duration, failed components, reason, source |
| `producer` | producer | key, current version, kind |
| `edge` | (content key, producer version, head / tail) | the typed edge row (below): scalars + the edge's HPCP; the opt-in arrays when captured. One version per track: when a fold at the current producer version lands, the track's rows at every other version (`edge`, `segment`, whole-track `value`) are pruned, and every scan sweeps the store for residue an earlier version bump left — the `run` rows are the history. Every library scan and `--folder` scan ends with a routine compaction (a plain VACUUM in WAL mode, safe with the hub attached), so the file shrinks on its own; per-file runs (`--file-list`, `--analyze-file`) leave it to the next one; `--store-vacuum` is for changing the page size |
| `value`, `segment` | content key (+ segment) | the edge's whole-track words (status, duration, extractor, head-vs-tail divergence) and windows; any future measurement that is a function of the audio |
| `verdict` | copy | the catalog's `truedat` block, derived from the stored rows (below) — a row iff the catalog writes the block |
| `artifact` | (content key, build) | where the compressed extractor output sits, bytes, sha |
| `work_item` | (entity, producer) | review records imported from the scan ledgers (review state, attempts) |
| `conflict`, `check_result` | event | drift between runs, sha collisions, split disagreements; checks |

Schema changes are forward-only scripts (`Truedat/Store/Schema/v1.sql` … `v4.sql`); an older store migrates
forward on first open by a newer build (backup first), and an older build refuses a newer store.

**Edges and verdicts (schema v4, 2026-10-16).** The edge is a typed `edge` row per edge. By default it holds
the scalars the readers use — status, window, key name / scale / code / strength / agreement / votes, bpm with
beats and regularity, start / end / mean LUFS, fade, silence and its source, RMS crest and transition, the
edge's three divergences, spectral centroid and dissonance — plus the edge's whole-summary HPCP (36 × float32);
values are the fold's float32 widened to `REAL`, nothing rounded on the way in. `--edge-arrays` (config key
`edge-arrays`, off by default) also keeps the arrays — the EBU series, beats, the 10 s sub-window summaries
and the MFCC / spectral vectors — in the row's `arrays` column as the `edge-row/1` blob (`arrays_dtype`).
Capture is sticky per track: a row that holds arrays keeps getting them on every later write whatever the
flag says, and only `--edge-arrays-drop [--dry-run]` removes them (the scalar row stays). The `verdict` table
is the catalog's `truedat` block, keyed per copy (the verdict reads the copy's codec, bit depth, sample rate
and LAME tag as well as the media's description), computed by the same function the catalog writer uses from
the rows as stored; the build's two algorithm tags are kept in `store_meta`, and when either differs at open
every row is re-derived, no audio read. A v3 store migrates on open: each packed `edge.row` becomes a typed
row with its blob kept as the arrays (a v3 store captured them), and the verdicts are derived.

**Precision and types (schema v3, 2026-10-15).** Every numeric field has one declared precision
(`FieldPrecision`: the decimal places the catalog writer renders), and the store holds it exactly at
that precision: a fixed-decimal scalar as a scaled `INTEGER` (`round(value × 10^dp)`, read back as the
double `Math.Round(value, dp)` produced), a fixed-decimal vector as an int32 `×10^dp` blob, and
`bpmHistogram` — about 94 % zeros — as a self-describing sparse blob (length, count, then bin + value per
non-zero). Integers stay integers. Nothing is rounded a second time: the former six-significant-figure
rule is gone, and `--store-export` writes the same bytes the scan writer would, so
`--store-roundtrip` is a byte comparison of the same writer on both sides. Two fields are held finer
than the writer renders them, because a scan sets them from an unrounded source: `analysisDuration`
(six places in the store, one in the catalog) and `smfmBpm` (six and three). A v2 store migrates on
first open by a v3 build — backup first (`truedat.db.bak.v2.<stamp>`, via `VACUUM INTO`, after a
free-space check), every value preserved exactly; a v2 value the typed column could not hold exactly
makes the migration refuse and roll back, naming the column, rather than round it.

## Verbs

| Verb | Does |
|---|---|
| `--store-import` with `--moods <catalog>` | loads the catalog and the ledgers beside it (errors, skipped, review, verify, receipts), idempotently, in batches; `--library-root <path>`, `--root-role primary|mirror|endpoint`, `--machine-adopt` |
| a scan, with a store in force | commits every entry it touched — analysed ones as producer `essentia@<build>` with one run row each, cache hits as copy facts — at each checkpoint and at the end, then publishes the catalog, sidecar and companion from the store; failures become failed runs. A stop (second Ctrl+C, console closed, logoff, shutdown) commits and publishes what has completed before the process ends |
| `--store-export <out.json>` (`--export-root <root>`) | a catalog from the store through the unchanged writer — the same bytes a scan would write; `key-moves.json` beside it when a content hash was superseded |
| `--store-roundtrip <catalog>` | per-entry byte equality of store against catalog, both through the catalog writer; exit 1 on any difference; `--audit` prints where the first difference is |
| `--store-info` | bytes per track, analysis speed, failure rate by component, coverage by wave, shared content, key moves, conflicts by kind and edges coverage — plus the **state**: the store's generation, the generation the published catalog carries, whether the files are behind it and why, how many descriptions hold the opt-in edge arrays, and the one command that closes the gap; `store-info.json` beside the store carries the same under `state` |
| `--snapshot [path]` with a store | the catalog, the manifest, the exclusion file **and the store** in one restorable ZIP — the store as a `VACUUM INTO` copy, so it is consistent even while a scan reads it |
| `--restore <archive>` with a store | restores the **store** from a snapshot that carries one, then republishes the catalog, sidecar and companion from it at a new generation; an archive without a store still refuses |
| `--merge-moods` with a store | merges catalogs **and other stores**: another store's live entries, read across every root, carry its descriptions, copy facts and the fields the catalog writer cuts — not its edge rows, identity aliases or run history. The merged result is written into the store exactly and the files republished from it |
| `--store-vacuum [--page-size N]` | repacks the file (16 KB pages by default) |
| `--stats --store`, `--duplicates --store` | the existing reports answering from the store instead of the catalog |

## Edges — the splicing layer (`--edges`, `--refresh`, `--edges-export`)

The hub splices tracks on their edges, so the store holds a compact *edge row* per media: the first
and last 30 s folded from a binary frames file the `output-x64.5` extractor writes beside its main
output (`--frames-bin`, a curated descriptor list; the file is deleted after the fold). Per edge: the
EBU momentary and short-term loudness series at 10 per second (int16 centi-dB), the beats inside the
window, tempo from their intervals, a key vote per edge and per 10 s sub-window (the extractor's own
Key algorithm over the window's chroma), MFCC and spectral means and spreads, fade and silence, and
from a short ffmpeg PCM pass the RMS crest and the transition into silence. The fold computes all of
it; the store keeps the typed default row (scalars + HPCP; measured about 1.6 KB per track in a vacuumed
store) and, only with `--edge-arrays`, the arrays as well (`edge-row/1`, documented in
`Truedat/Edges/EdgeStore.cs`; about 11.5 KB per track with the scalar row), with an `edges` run row
carrying the fold and PCM time. Producer version `edges@v3;e30;p10` carries the
algorithm generation and the settings; a fold-rule change is a new version and old rows stay
attributable. Rows under an older version (`v2`: the edge key estimated without the extractor's
preprocessing) are not served: they count as lacking, `--refresh` re-folds them from the audio, and
the companion carries only current-version tracks until then (`--store-info` and `--stats` show the
count).

- `--edges` on any scan with `--store`: fold every analysed track. Refuses plainly without `--store`,
  on an extractor older than `.5`, and in `--analyze-file` / `--file-list` without `--moods`.
- `--refresh` with the store and edges on: a track the store lists as lacking edge rows at the current
  version is stale to the scan's cache gate, so the scan re-analyses it with its own workers, progress and
  ETA, and the store hand-off writes the edges. The store saves at the scan's five-minute checkpoint and at
  the end. A failed fold is not retried until `--retry-errors`. `--budget 90s|30m|2h|1h30m` stops the
  library scan cleanly (save and exit) once that long has passed; run it again to continue.
- `mbxedges.v1.json` beside `mbxmoods.json`: the hub-facing companion, keyed by `audioStreamSha256`,
  scalars only (status, window, key, bpm, loudness, texture, divergences, rms), about 1.8 KB per track,
  compact. `texture` is written only for a track whose arrays were captured; a default-row track writes
  `texture: null`, which the v1 reader accepts. Rewritten after any `--edges` scan that added rows, and on demand by
  `--edges-export [path]`. Media without a sha are counted and omitted. Absent means not yet.

## Snapshot, transfer, restore

The catalog is derived; the store is the authority. So archiving the catalog and archiving the store
are different acts, and only the second one is a backup of the thing the files come from.

- **Never copy a live store as a file.** Under WAL the committed state lives across `truedat.db`,
  `truedat.db-wal` and `truedat.db-shm`; copying the first alone captures a torn read of whatever was
  in flight. Every copy truedat makes — the snapshot member, the pre-restore backup, the pre-migration
  backup — goes through SQLite's `VACUUM INTO`, which runs against a read snapshot and writes one
  consistent, already-compacted database. Use the same thing by hand:
  `sqlite3 -readonly truedat.db "VACUUM INTO 'truedat-copy.db';"`.
- **`--snapshot` with a store in force** puts that copy into the archive as `truedat.db` and records
  its schema, generation, live-location count and SHA-256 in `manifest.json` — so a transfer between
  machines is verifiable by hash the way the catalog already was. A store that cannot be copied
  consistently fails the whole snapshot rather than quietly producing a catalog-only archive under a
  store snapshot's name.
- **`--restore <archive>` with a store in force** restores the store when the archive carries one. It
  refuses *before* replacing anything if the extracted store does not open, fails
  `PRAGMA quick_check`, or comes from a newer schema than this build reads. The store being replaced
  is itself snapshotted first (`truedat.db.bak.<ts>.db`, rotated by `--keep-backups`), so undoing a
  restore is the same command. The replaced store's stale WAL pair is removed with it.
- **The generation advances on a restore.** A restore is a consumer-visible mutation even when the
  restored bytes are older than what the hub cached, so the catalog, sidecar and companion are
  republished from the restored store at a new generation through the one publication path — never
  installed from the archive.
- **An archive with no store cannot restore one.** That case still refuses, and names the archive that
  would have worked. To put a catalog's entries into a store, `--store-import` seeds or extends it;
  that is not a restore and it never removes store entries the catalog lacks.
- **Merging across machines** works on stores as well as catalogs: another store can be a
  `--merge-source`. Its live entries are read across *every* root — a store minted on another machine
  names its own, and reading only the primary root would drop exactly that machine's share — and
  rendered through the same catalog writer, so a store source and a catalog source merge identically;
  the fields the writer cuts come across from the store itself. What does not come across: the source
  store's edge rows, identity aliases, conflicts and run history — a store source carries descriptions
  and copy facts. The later source wins a path, and what it wins with is what it **carries**: a field
  the winning entry does not carry — absent, or JSON `null`, the same thing — never clears a stored
  value (a supplied zero or empty string is a value and lands); the fields the catalog writer cuts
  follow the winner by content identity (same `audioStreamSha256` and hash kind) and never cross
  identities; a merge that fails partway supplies nothing; a copy merged in from another library has
  no `trackId` (the ids are library-local), while an existing copy keeps its own. `--chunk` shards run without a store (a store-backed shard is refused), so a shard
  merge is a merge of catalogs.

## Looking inside

Any SQLite client of version 3.40 or newer opens the file. The store runs in WAL mode, so a read-only
browser can look while truedat runs (do not write to it from another tool while a scan is
running; truedat is the single writer by design).

- **DB Browser for SQLite** (sqlitebrowser.org): free, open source, Windows installer; open the file
  read-only, browse tables, run the queries below.
- **`sqlite3.exe`** (sqlite.org, the official shell): `sqlite3 -readonly truedat.db`.

Some questions and their queries:

```sql
-- descriptions, copies, media with several copies
SELECT (SELECT count(*) FROM track_content), (SELECT count(*) FROM track_copy),
       (SELECT count(*) FROM (SELECT entity_id FROM track_location GROUP BY entity_id HAVING count(*) > 1));

-- what ran, by producer and outcome
SELECT producer_key, producer_version, outcome, count(*) FROM run GROUP BY 1, 2, 3 ORDER BY 1, 2, 3;

-- edges coverage by version (the current version is what --store-info prints)
SELECT producer_version, txt, count(*) FROM value
 WHERE producer_key = 'edges' AND field = 'edges.status' AND segment_id = 0 GROUP BY 1, 2;

-- review records imported from the scan ledgers, by state
SELECT state, count(*) FROM work_item GROUP BY state;

-- a track's edge windows
SELECT kind, status, start_ms, end_ms, key_code, bpm, mean_lufs, arrays IS NOT NULL AS has_arrays FROM edge
 WHERE content_hash = '<audioStreamSha256>' ORDER BY kind;

-- open conflicts by kind
SELECT kind, count(*) FROM conflict WHERE state = 'open' GROUP BY kind;
```

The edge rows' scalars are plain columns of `edge`; the opt-in arrays' binary layout is `edge-row/1`
(`arrays_dtype`), and `--edges-export` renders the scalars.

## What stays frozen

`mbxmoods.json` and `.mbxs` bytes, every existing flag and exit code, `apply-result.json`,
`preview.json`, `ruleId`. The store is additive: a box without `--store` runs exactly as before.
