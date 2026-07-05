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

## Principles (unchanging)

- **Read-only over audio.** Apply/removal never lives in `truedat.exe`.
- **Offline-first.** No runtime network calls; self-contained outputs.
- **The JSON is the contract.** Tolerant-reader boundary to any consumer; no shared lib.
- **Report, don't decide.** Truedat surfaces the evidence; a human (or a downstream
  tool) acts.
