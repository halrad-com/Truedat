---
name: truedat-exclusions
description: Scan exclusions — mbxmoods-exclude.json, exclusion playlists, the merge discipline, the speech-evidence taxonomy (no podcast detector), --preview and --check-filenames. Read before touching exclusions, speech heuristics or the preview surface.
---

<!-- Moved verbatim from the root CLAUDE.md on 2026-10-22 (Claude Code doctor, check 4) so it loads only when relevant. The root file keeps the universal rules and a pointer here. -->

## Scan exclusions

`mbxmoods-exclude.json` beside the moods file is the **only** authority over what a scan
skips for policy reasons (`ExclusionSet` parses and matches; `ExclusionStore` reads, writes
and merges). Three rule kinds — `folder` (pattern must end `**`; a leading-separator
fragment matches under any root, which is what makes rules portable across mirrored
libraries), `genre` (exact, case-insensitive, trimmed — deliberately **not** substring) and
`file` — each `exclude` or `include`, with **`include` always winning** regardless of order.

A `file` rule's optional `audioStreamSha256` is **report-only, never matching** (I-2, 2026-07-26).
Matching stays exact normalized path equality; the sha exists so that when the path is gone,
`PreviewPlanner.BuildRuleStats` can report `PreviewRuleStat.State` as `moved-or-deleted` and name
the catalog paths still holding that content in `Candidates` (emitted as `state` / `candidates[]`
in `preview.json`, rendered by `PreviewRuleNote` on the console and `ruleNote()` in the page).
Absent-from-disk but still-in-catalog is a third state, `path-unreachable` — the metadata-mirror
case; calling it "moved" would condemn every live rule on a mirror box, which is the same
inversion C-1 fixed. Do **not** make the sha match: one `audioStreamSha256` covers every copy of
the audio (it is what `--duplicates` groups on), so re-matching would silently widen a rule the
operator wrote narrowly. Candidate resolution deliberately does **not** use `BuildHashIndex` —
that index is first-wins on duplicate shas, so it would name an arbitrary copy; `BuildRuleStats`
scans the catalog once and lists all matches, sorted.

Layering is two-tier, and no heuristic sits in it any more: structural skips (missing /
video / stream URL / non-audio / DSD / over `--max-duration`) are "cannot analyze" and ignore
`include`; everything else is policy, decided solely by `ExclusionSet.IsExcluded` —
`include` > `exclude` > keep. A speech label, a genre string, an embedded file marker: none
of it filters anything by itself any more. The only way a track is kept out of analysis for
a policy reason is a rule the operator wrote into `mbxmoods-exclude.json`.

**Exclusion playlists (2026-07-30):** a playlist named `mbxmoods-exclude` (`.m3u8` then
`.m3u`) is discovered beside the XML/moods file or in its `Playlists\` subfolder (or named
explicitly via `--exclude-playlist`, which wins) and layered onto the loaded set per run via
`ExclusionSet.Combine` — base set first, so an `include` rule in the JSON file still beats a
playlist exclude. The playlist is the durable artifact (maintained in MusicBee; nothing is
persisted to JSON); `--apply-exclusions` also accepts a `.m3u/.m3u8` directly and converts
entries to permanent `file`/`exclude` rules through the same merge machinery. `PlaylistReader`
parses (comments/URLs skipped, relative entries resolved against the playlist dir, deduped)
and rule objects are built through `ExclusionSet.FromJson` so normalization cannot drift.
Fail-closed: explicit-but-missing path, or any present-but-unusable playlist, refuses to
scan. `--no-exclusions` bypasses the playlist along with the file. File rules use the JSON
key `path` (genre uses `value`, folder uses `pattern`) — a `value`-keyed file rule silently
parses as invalid.

A **missing** file excludes nothing (not an error). A **present but unparseable** file makes
truedat exit 1 rather than scan — analyzing everything while the operator believes rules are
in force is the silent failure the whole design exists to remove. Invalid individual rules are
skipped, counted and diagnosed (tolerant-reader convention). A rule never prunes a catalog
entry **as a side effect** — scanning and pruning are separate acts, and only the dedicated
`--prune-excluded` verb (below) retires entries, never a scan. Every skip is ledgered in `mbxmoods-skipped.csv` on all scan modes; the
iTunes-XML library scan additionally prints per-rule hit counts so stale rules surface (the
per-file modes have nothing to count, but still print a warning per ignored/invalid rule
diagnostic). `--exclusions`
overrides the location, `--no-exclusions` bypasses for one run, `--apply-exclusions <delta>`
merges (with backup) and is the **single** implementation of the merge semantics — MBXHub is
expected to invoke it rather than write the file itself.

The merge is the one write in the system over an artefact that **cannot be regenerated** — it is
pure operator judgement — so it gets the strongest discipline: `ExclusionStore.Write` stages to
`mbxmoods-exclude.json.tmp` in the **same directory** (`TempWritePath`; `File.Replace` is
same-volume) and swaps via `Program.AtomicReplace`, matching `SaveResults` / `--migrate` /
`--merge-moods`; the tmp is deleted on failure so a half-written sibling never sits beside the
policy file. `Merge`'s whole load-modify-write runs under `AcquireWriteLock` — a zero-byte
`mbxmoods-exclude.json.lock` sidecar opened `FileShare.None`, 10 × 100 ms then refuse. The lock
file is **deliberately never deleted** (deleting it would let a third process create a fresh one
while a second still held the old handle, and both would believe they held the lock). Scope is
honest: it serialises truedat against truedat, which is the lost-update case; it cannot serialise
truedat against a text editor saving the file, and the `.bak` remains the recovery path there.
Because every refusal path returns before touching the file and the write is atomic, the failure
message is `The exclusion file was not modified.` — the old `Nothing was written.` was false both
for a mid-write failure and for the `apply-result.json` the same run does write.

The `Episode Date` podcast heuristic is **deleted** (2026-07-24) and must not return:
MusicBee maps ID3v2.4 `TDRL` (a *release* date) into that key, so it rides on ordinary music.
Explicit labels only — `Podcast=true` or `Genre=Podcast` — and even those are evidence now,
not a filter (see below).

**Speech heuristics do not decide anything, and must not be reinstated as a filter.**
`ITunesParser` still sets `IsSpeech`/`SpeechReason` on parse (from the explicit XML labels
only — the `Episode Date`/Publisher/duration vote is gone too, see above), and
`SpeechTagSniffer` still sniffs embedded file markers, but both are purely evidence for the
`--preview` review surface (`reasons` on a review candidate; graded `SpeechMarkers.Strong`
/ `.Provenance` / `.GenreText` — strong = ID3 `PCST`/MP4 `pcst` asserting "this IS a
podcast", provenance = ID3 `WFED`/`TGID`/MP4 `purl` asserting only "this came from a feed",
genre-text = ID3 `TCON` exactly `Podcast` trimmed/case-insensitive, **not** substring).
Nothing reads either signal to skip
a file or prune a catalog entry — `FilterPodcasts` and the MoodsMode prune that consumed it
are deleted; `SeedCommand.cs`'s own `library.RemoveAll(t => t.IsSpeech)` work-list filter
(an independent open-coded copy that never called `FilterPodcasts`, which is why the plan's
original file map missed it) is deleted too, and the cache-miss embedded-marker skip in the
three per-file scan paths is deleted as well (the sniffer now runs only inside `--preview`). `--migrate`
strips legacy fields only (`valence`/`arousal`, `audioMd5`, `chromaprint`; `fileMd5` unless
`--file-md5`; renames `sensme*`→`smfm*`) and never removes an entry on any verdict —
speech-labelled, speech-likely, or otherwise. `speechLikely` is advisory only; **AutoQ (MBXHub)
is its downstream consumer**, via the read side of `mbxmoods.json`. Inside truedat, only the
read-only review surfaces (`--list-speech`, the `--stats` speech count) recompute and
report it — neither one skips a file or removes an entry. `--include-podcasts` and
`ITunesTrack.ExclusionIncluded` are retired outright (hard cut, 2026-07-25) — the file
`mbxmoods-exclude.json` is the only override, via its own `include` rules.

**Taxonomy — the class is SPEECH, and there will be no podcast detector** (operator ruling,
2026-07-25). Audiobook, comedy, lecture, news, interview and talk-dominant are ONE GENUS: speech.
"Podcast" is never a class — it is the name of a particular piece of *evidence* (the `Genre=Podcast`
or `Podcast=true` label, a `PCST` marker) or an external key, and those literals keep their real
names precisely because they belong to other systems. Identification therefore has three sources
answering two different questions: the XML labels and the embedded markers are **declared**
evidence, and `speechLikely` is the **measured** one — the only source that can see a *downloaded
or copied archive* of a show, which carries no feed registration and is invisible to every label
and marker. That archive class is the entire reason the acoustic verdict exists.
**Do not add a `podcastLikely` verdict.** Content analysis can answer "is this speech-dominant"
and nothing more; podcast-vs-audiobook-vs-lecture is *provenance* — was this distributed as an
episodic feed — and provenance is not present in the audio. A file that lost its feed registration
lost that fact permanently, and no signal processing recovers it. Species-level questions are
answered by folder layout, genre and feed registration, i.e. by operator-written rules. Proposing
an audio-derived podcast classifier means proposing to assert something the evidence cannot
support, which is the same category error the whole Heuristics → Evidence arc removed.
This has been walked back **twice** now (the `Episode Date` vote in 2026-07-24, then the
label/marker skip + the two `--migrate` purges + `--include-podcasts` in this phase) —
a third heuristic-decides-something attempt should have to argue against this paragraph in
writing, not rediscover why the first two didn't work.

`--check-filenames` (`RunCheckFilenames`) deliberately does **not** apply `_exclusions` —
it reports filesystem problems (over-length paths, zero-byte files) that are worth
surfacing whether or not a track is excluded from analysis ("check is check, not
exclude"); it filters only `FilterRemoteUrls` / `FilterVideoFiles` / `FilterNonAudio`
(structural) and never calls `FilterExclusions`, so don't "fix" that by wiring exclusions
in without a deliberate decision to do so.

`--preview` builds the same picture without scanning. **It is read-only over the CATALOG, not
over the filesystem** — say "never writes `mbxmoods.json`, never analyzes", because unqualified
"read-only" is false and already misled one reviewer (I-6): with no explicit path it *creates*
`<library root>\AppData\MBXHub\review\` and writes `preview.json` + `mbxmoods-preview.html`
into it, i.e. into a running application's data directory. Those files are regenerable and the
destination is anchored to the library truedat was pointed at, so this is by design — but it is a
write and the docs must not imply otherwise. `PreviewPlanner` computes a `PreviewPlan` from the
parsed XML + existing catalog + `ExclusionSet`, and `PreviewWriter` emits `preview.json` into the
MBXHub review folder via `ResolveReviewDir` — the **same** function `ResolveManifestDest` uses for
the duplicates manifest, so both derive the instance root from the moods file's `<root>\Library\`
location and neither can drift to "whichever MusicBee the OS lists first". Do not write a second
resolver; the self-tests pin that preview and the manifest share one anchor and two filenames. **`preview.json` IS the review-surface manifest** — MBXHub's asset route serves only
`.html` and no route serves an arbitrary sibling JSON, so the payload rides inside the envelope;
`source.reviewHtml` stays absent until the Phase 2b page exists. Preview touches audio in exactly
one place — a bounded header sniff over review *candidates* only — and reports `sniffedCount` so
that is visible rather than implied. The ETA comes from the catalog's stored `analysisDuration`
against known track lengths (median), reported as `catalog-rtf`; with no history it is omitted,
never guessed. `--long-track-mins` (default 30) is a review **prompt**, not a rule.
`--apply-exclusions` writes `apply-result.json` beside the exclusion file on both success and
failure, because MBXHub's stdout capture is a bounded async tail and must not be parsed.
