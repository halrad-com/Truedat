# The truedat store (preview, v2)

A SQLite database, `truedat.db`, that holds everything `mbxmoods.json` holds and more, keyed by
identity rather than by path. `mbxmoods.json` stays exactly what MBXHub reads; the store is a second
sink beside it and the source for everything that is not in the catalog: provenance, the edge rows,
the work queue, the aggregate artifacts. Nothing in the store changes how a scan without `--store`
behaves, and nothing is installed: truedat uses the SQLite that ships with Windows (`winsqlite3.dll`,
3.40 or newer, checked at open with a plain refusal when missing).

## Where it lives

- Beside a local catalog: `<library>\truedat.db`. When the catalog folder is a network share the store
  goes to a local per-library path under `%LOCALAPPDATA%\truedat\<library-id>\` instead; `--store-dir
  <dir>` (also a config key) relocates that layout. SQLite on SMB is refused unless you name it with an
  explicit `--store <path>`, and then it opens with exclusive locking.
- With nothing set, the store and edges are on when the library has no catalog yet (a fresh install) or a
  store already sits beside the catalog; an existing catalog without a store stays off until
  `--store-import` creates one, so a scan never builds a store holding only the tracks it analysed.
  `--store` / `--store <path>` per run, or `truedat --config store true|false` (likewise `edges`) per
  instance, overrides that; `--no-store` / `--no-edges` override for one run. A default never makes a
  plain command refuse (a per-file mode without `--moods` skips the store, an extractor older than `.5`
  skips edges, each with a note).
- Beside the store: `artifacts\<build>\<2>\<key>.main.json.z` (the extractor's compressed main output,
  one per content key), `artifacts\pending\` (in-flight stashes, swept), `store-result.json`,
  `store-import-result.json`, `store-info.json`.

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
| `value`, `segment` | content key (+ segment) | the edge rows (below); any future measurement that is a function of the audio |
| `artifact` | (content key, build) | where the compressed extractor output sits, bytes, sha |
| `work_item` | (entity, producer) | review records imported from the scan ledgers (review state, attempts) |
| `conflict`, `check_result` | event | drift between runs, sha collisions, split disagreements; checks |

Schema changes are forward-only scripts (`Truedat/Store/Schema/v1.sql`, `v2.sql`); a v1 store migrates
to v2 on first open by a v2 build, and an older build refuses a newer store.

**Precision.** Every floating-point feature enters the store, and leaves it on export, at **six
significant figures**, written exactly (no trailing zeros, no exponent). Essentia computes in float32,
which carries at most seven significant decimal digits and guarantees six, so nothing past the sixth
figure is measurement. The scan's own catalog writer is untouched (its bytes are the hub's contract);
the rule applies to the store, `--store-export` and `--store-roundtrip` only. Rounding a REAL saves
nothing in SQLite (eight bytes regardless); the savings are in the exported text.

## Verbs

| Verb | Does |
|---|---|
| `--store-import` with `--moods <catalog>` | loads the catalog and the ledgers beside it (errors, skipped, review, verify, receipts), idempotently, in batches; `--library-root <path>`, `--root-role primary|mirror|endpoint`, `--machine-adopt` |
| a scan with `--store` | writes the entries it analysed this run (never cache hits) after the catalog is saved, as producer `essentia@<build>`, one run row per entry; failures become failed runs |
| `--store-export <out.json>` (`--export-root <root>`) | a catalog from the store through the unchanged writer, numbers at the store's precision; `key-moves.json` beside it when a content hash was superseded |
| `--store-roundtrip <catalog>` | per-entry equality of store against catalog at the store's precision; exit 1 on any difference; `--audit` prints where the first difference is |
| `--store-info` | bytes per track and the 156k projection, analysis speed, failure rate by component, coverage by wave, shared content, key moves, conflicts by kind, edges coverage and the work queue; `store-info.json` beside the store |
| `--store-vacuum [--page-size N]` | repacks the file (16 KB pages by default) |
| `--stats --store`, `--duplicates --store` | the existing reports answering from the store instead of the catalog |

## Edges — the splicing layer (`--edges`, `--refresh`, `--edges-export`)

The hub splices tracks on their edges, so the store holds a compact *edge row* per media: the first
and last 30 s folded from a binary frames file the `output-x64.5` extractor writes beside its main
output (`--frames-bin`, a curated descriptor list; the file is deleted after the fold). Per edge: the
EBU momentary and short-term loudness series at 10 per second (int16 centi-dB), the beats inside the
window, tempo from their intervals, a key vote per edge and per 10 s sub-window (the extractor's own
Key algorithm over the window's chroma), MFCC and spectral means and spreads, fade and silence, and
from a short ffmpeg PCM pass the RMS crest and the transition into silence. One packed binary row per
edge (`edge-row/v1`, documented in `Truedat/Edges/EdgeStore.cs`), about 6.4 KB per track, with an
`edges` run row carrying the fold and PCM time. Producer version `edges@v2;e30;p10` carries the
algorithm generation and the settings; a fold-rule change is a new version and old rows stay
attributable.

- `--edges` on any scan with `--store`: fold every analysed track. Refuses plainly without `--store`,
  on an extractor older than `.5`, and in `--analyze-file` / `--file-list` without `--moods`.
- `--refresh` with the store and edges on: a track the store lists as lacking edge rows at the current
  version is stale to the scan's cache gate, so the scan re-analyses it with its own workers, progress and
  ETA, and the store hand-off writes the edges. The store saves at the scan's five-minute checkpoint and at
  the end. A failed fold is not retried until `--retry-errors`. `--budget 90s|30m|2h|1h30m` stops the
  library scan cleanly (save and exit) once that long has passed; run it again to continue.
- `mbxedges.v1.json` beside `mbxmoods.json`: the hub-facing companion, keyed by `audioStreamSha256`,
  scalars only (status, window, key, bpm, loudness, texture, divergences, rms), about 1.8 KB per track,
  compact. Rewritten after any `--edges` scan that added rows, and on demand by
  `--edges-export [path]`. Media without a sha are counted and omitted. Absent means not yet.

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

-- the work queue
SELECT state, count(*) FROM work_item GROUP BY state;

-- a track's edge windows
SELECT s.kind, s.start_ms, s.end_ms, length(v.vec) AS bytes FROM segment s JOIN value v ON v.segment_id = s.id
 WHERE s.content_hash = '<audioStreamSha256>' ORDER BY s.kind;

-- open conflicts by kind
SELECT kind, count(*) FROM conflict WHERE state = 'open' GROUP BY kind;
```

The edge rows' binary layout is `edge-row/v1`; the SQL above shows their presence and size, truedat's
`--edges-export` renders their scalars.

## What stays frozen

`mbxmoods.json` and `.mbxs` bytes, every existing flag and exit code, `apply-result.json`,
`preview.json`, `ruleId`. The store is additive: a box without `--store` runs exactly as before.
