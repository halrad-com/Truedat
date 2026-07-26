# Truedat Roadmap

The over-arching direction. Not a task list — that's [`BACKLOG.md`](BACKLOG.md).
Shipped detail lives in [`CHANGELOG.md`](CHANGELOG.md).

## What Truedat is

A Windows command-line tool that analyses a music library's audio and writes
`mbxmoods.json` (mood + Essentia features + identity hashes). Read-only over the
audio: it reports and produces sidecar files; it never deletes, moves, or modifies
audio. Output is consumed by a companion web UI (MBXHub) and by Truedat's own
offline review pages.

## Arc

**1. Extraction & identity — mature.**
Essentia feature extraction, `fingerprint.v1`, `audioStreamSha256` content identity,
cache tiers, cross-machine `--chunk`.

**2. Authenticity — shipped, calibrating.**
`bitUsage`, `hfEnergyRatio`, `hfSpectralStructure`, and the `truedat.*` hi-res /
lossy-transcode verdict. Next: broaden the labeled corpus so the verdict thresholds
tune from data, and reconcile the standalone verdict with the dedupe-context
fake-hi-res signal (which is provable when a lower-rate twin exists).

**3. Deduplication & curation — active.**
Exact + probable detection, keeper recommendation (quality + SMFM + genuine-over-fake),
and an offline interactive review page (folder-pair rollup, side-by-side compare,
audition, build-losers-playlist). The near-term push is making review *great*: faster
triage (keyboard, reviewed-state, search, sort-by-reclaimable-space), better keeper
signals (prefer in-library copy, playcount/rating), and tighter probable-tier
confidence so genuinely-different recordings don't surface as candidates.

**4. Library reconciliation.**
`--fixup` / `--verify` keep the catalog honest against disk reality (existence,
hash drift, hash-resolve of deleted duplicates). Direction: metadata-level
reconciliation across library copies (e.g. treating enriched tags like SMFM as a
syncable field) rather than re-copying audio — read/detect in Truedat, apply outside
the read-only binary.

**5. Scan policy — shipped, expanding.**
Truedat used to guess which files not to analyse: a podcast label, an embedded marker,
a genre string. Every guess was wrong for someone, and two of them were walked back
after they cost real music its catalog entries. That whole class is gone.
`mbxmoods-exclude.json` — typed rules (`folder`/`genre`/`file`, `exclude`/`include`,
include always wins) — is now the **only** thing that keeps a file out of a scan, and
nothing removes a catalog entry on a verdict. The signals survive as *evidence* on the
review surface: `--preview` reports what a scan would do without doing it, and emits a
self-contained review page for turning that into rules. Next: a fourth rule kind,
`speech`, with analyse-once-then-excluded semantics — a classification-derived rule
cannot gate a track nobody has analysed yet, and that constraint is the design, not a
limitation to engineer around.

**6. Speech — classification shipped, interpretation ahead.**
`speechLikely` is a write-time verdict over stored features, so a threshold change is
retroactive across the catalog with no rescan. It classifies a **genus**: audiobook,
comedy, lecture, news, interview and talk-dominant are one class. It deliberately does
not attempt the species — podcast vs audiobook is *provenance*, and provenance is not
in the audio; a file that lost its feed registration lost that fact permanently. The
open direction is transcription, for which detection is the selector. That inverts the
tuning: today the thresholds favour precision because a false positive once drove a
destructive prune; as a transcription selector a false negative costs more, so the
gates want revisiting deliberately — with a method-tag bump, against a labelled set,
not by drift.

## Principles (unchanging)

- **Read-only over audio.** Apply/removal never lives in `truedat.exe`.
- **Offline-first.** No runtime network calls; self-contained outputs.
- **The JSON is the contract.** Tolerant-reader boundary to any consumer; no shared lib.
- **Report, don't decide.** Truedat surfaces the evidence; a human (or a downstream
  tool) acts. This is the one that took the longest to actually mean: heuristics that
  quietly decided what not to scan were removed twice before the principle was enforced
  in the code rather than stated in a doc. A signal may inform a decision; it may not be
  the decision.
- **Every build better than the last.** No release regresses the suite, the replay
  gates, or a documented behaviour. Coverage may shrink only when the behaviour it
  pinned is deliberately removed, and the removal is named.
