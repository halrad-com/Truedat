---
name: truedat-catalog-discovery
description: Which catalog or library a bare command means — the shared LibrarySearchDirs ladder, marker-based library discovery, the cwd-catalog guard for rewriting verbs. Read before touching ResolveMoodsCatalog or ResolveITunesXml.
---

<!-- Moved verbatim from the root CLAUDE.md on 2026-10-22 (Claude Code doctor, check 4) so it loads only when relevant. The root file keeps the universal rules and a pointer here. -->

## Which catalog a bare command means

Two resolvers, and they must agree on probe order. `ResolveITunesXml` finds the library for a
bare scan; `ResolveMoodsCatalog` finds `mbxmoods.json` for every catalog mode. Both iterate
**`LibrarySearchDirs`** — one shared ladder, so they cannot discover a library from different
places — then fall back to the cwd. Order: exe-dir's parent, exe-dir, each of those two's
`Library\` subfolder, then successive ancestors (bounded at `LibrarySearchMaxAncestors`) bare and
`\Library`, then **libraries found by marker** (below), then each ancestor's `AppData\*` children,
and last `%APPDATA%\MusicBee`. Parent before
exe-dir is load-bearing and pinned: a catalog inside the tool folder is the accident, the library's
is the one meant. `<root>\Library` beats `AppData\*` (it is the canonical home and
`ResolveReviewDir`'s anchor), and `%APPDATA%\MusicBee` is last because a tool inside a *portable*
instance must resolve to that instance, never to the user's default one — that ordering is pinned
by a test on ladder position, not just membership.

**Libraries are not all called `Library` — that is only the DEFAULT library's name.** MusicBee
names the folder after the library, so every name-based rung above misses a renamed or rebuilt one
(etlap, 2026-08-21..23: the live library was `<root>\Remote`, its 106 MB XML sat in plain sight two
rungs from the exe, and discovery reported it missing — read by the operator as "MusicBee is not
writing the XML"). So `NamedLibraryDirs` stops guessing the name and looks for the **marker**:
`MusicBeeLibrarySettings.ini`, which MusicBee drops in every library folder. Scan is bounded twice over: depth
(`LibraryScanMaxDepth`, 2) under each ancestor level, and total directories examined
(`LibraryScanMaxDirs`, 400). Depth alone is NOT enough — the ancestor walk reaches a volume
root, and depth 2 under `X:\` enumerates every top-level folder and each one's children, which
on a music volume is thousands of probes on the MISS path. The result is memoized per process;
a library past the cap is not found, and the positional path is the answer there.

**Use the marker's EXISTENCE, never the path it records.** `MusicBeeLibrarySettings.ini` carries a
fully drive-qualified `<Path>`, and `AppData\MusicBee3Settings.ini` carries `ENV_LibPath` — and
*both are stale on any mirrored or moved instance*. Measured: `X:\Library` and `P:\Library` both
mirror `D:\MusicBee` and both record `D:\MusicBee\Library\`; `ENV_LibPath` is additionally
drive-letter-STRIPPED, so rejoining it against the current drive yields `X:\MusicBee\Library\`,
which does not exist. Local evidence beats a recorded path — that is why **no ini is parsed here**,
and re-adding an ini read would reintroduce exactly this bug.

Multiple libraries rank by **freshest `MusicBeeLibrary.mbl`** (live truth; a settings flush lags
mid-session), ties broken by name so two runs can never silently disagree. Folders with no `.mbl`
(a first-run stub) sort last. There is deliberately **no selection flag and no prompt**: the
positional XML path already wins outright, so a caller that knows which library it means — MBXHub
always does, and passes it — never reaches this rung. It only has to answer the bare-command case.
Per-library catalogs need no extra machinery: `ResolveMoodsCatalog` puts `mbxmoods.json` beside the
library that produced it, so a local library and a NAS one get separate catalogs for free.

**The authoritative answer lives in the plugin, not here.** MusicBee tells it the data folder via
`Setting_GetPersistentStoragePath()`; truedat is a standalone CLI and can only infer. So this
ladder is the best-effort path for a hand-typed command, and it should not keep growing rungs to
chase cases the hub can simply *tell* us — the hub already knows the folder when it launches
truedat (`TruedatLauncher`), so passing the catalog explicitly is strictly better than another
guess. truedat must never call the hub to find out: it has to work standalone, on a metadata
mirror, and with the hub not running. `--apply-exclusions` is a third caller and routes
through the same discovery. If a fourth surface ever needs to answer "which library did they
mean", it joins them; it does not invent a fourth answer.

The `\Library` arm and the upward walk exist because **operators run truedat from MusicBee's hub
data folder**, `<root>\AppData\MBXHub\`, where the library is two levels up and across at
`<root>\Library`. That is the exact inverse of `ResolveReviewDir`, which maps a library to its hub
folder; the two encode the same MusicBee layout read in opposite directions, so if that layout ever
changes they both change together. The walk is bounded on purpose — an unbounded one eventually
finds *someone's* library — and nearest-first, so a catalog in the folder you are running from
always wins over one further up.

They diverged once and it shipped: the scan auto-discovered while every catalog verb went
straight to `<cwd>\mbxmoods.json`, so on the recommended layout `truedat` worked and
`truedat --stats` reported the catalog missing — and, because `ExclusionStore.Resolve` derives
the exclusion file from the catalog path, the operator's rules went missing with it. Field-reported
2026-08-15. `ProbeCatalogBesideExe` takes the exe directory as a parameter precisely so the
self-test drives a real temp layout rather than wherever the test binary lives; probing for the
CATALOG rather than deriving it from a discovered XML is deliberate, because these modes are
documented to run on a metadata mirror, which has a catalog and no library XML.

The cwd fallback survives only for modes that cannot destroy anything. A mode that REWRITES the
catalog (`--compact`, `--prettify`, `--restore`, `--prune-excluded`) calls `IsBareCwdCatalog` and
**refuses** when nothing anchors the target — no `--moods`, no positional, and nothing discovered
beside the exe — naming the path it would have hit. It acts on whatever file happens to be in the
directory you are standing in, with no existence check and no evidence it is a real catalog: a bare
reformat run from the repo root once found the checked-in test fixture, rewrote it, and left a
`.bak.zip` and a `.mbxs` behind (2026-08-13, caught only by the build's dirty-stamp). Note the
guard keys on *discovery came up empty*, not *no arguments were given* — keying it on the arguments
alone would refuse a correct drop-in install, which is a different way of failing the same operator.
