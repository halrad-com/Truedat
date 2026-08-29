# Truedat Backlog

Open work only. Shipped items live in `CHANGELOG.md`.

## Queued work — next sprint

1. **diffycat — cross-copy library reconcile** (spec-ready). Answer "what is actually
   different between the 5–6 `robocopy /MIR` mirror libraries" whose counts disagree
   (filesystem vs MusicBee vs iTunes XML vs `mbxmoods.json`). Read-only `--inventory` +
   `--reconcile`: a single ledger that explains every count gap, filesystem-anchored
   (canonical relpath + `audioStreamSha256`), an audio-identity + SMFM drift explainer
   (audio-identical metadata drift → transplant, SMFM outranks tags; vs real audio
   divergence), and a `reconcile-decisions.json` (exclude-file idiom) that converges the
   ledger to zero. Sync stays **outside** the binary (robocopy + SMFM transplant), proven by
   re-reconcile-to-zero. Guardrail: single ledger, offline/static files, no DB/federation.
   L0 filesystem + drift explainer = MVP; L1 tag-content diff deferred. Spec and resume doc
   in the local `docs/` tree. Next step: invoke writing-plans at sprint start.

2. **Tier 1 detection — spectral-ceiling + cliff detector** (`prov: up44` / `lossy`).
   The one new measurement that closes both the 44.1→48k upsample gap and the
   lossy-laundering gap (lossy history inside a lossless container). Accumulate mean mag²
   per FFT bin across windows (reusing the `ComputeHfAnalysis` loop), measure where the
   spectrum ends (**ceiling**) and how sharply (**cliff**): resampler and codec anti-alias
   filters are near-brickwall, naturally dark music rolls off gradually — the steepness gate
   is what answers the false-positive failure that killed Signal D. Must run on lossless at
   ANY rate/depth (44.1k FLAC is *the* laundering container and is exactly where HF analysis
   refuses today). Honest `unknown` expected on streaming-derived 48k material. The `prov`
   code plumbing shipped in v0.5.4.8-RC1 and is waiting for these codes. Needs operator go.

   **Two more discriminators, both content-independent** (2026-08-28) — ceiling position
   alone condemns dark masters:
   - **What lies above the ceiling.** Real recordings carry a noise floor to Nyquist
     (dither, ADC, hiss); decoded lossy audio has a dead band. A quiet ambient master still
     has its floor — the difference a rolloff reading cannot see.
   - **Whether the ceiling holds still.** A master's HF extent tracks the music; an imposed
     lowpass sits at one frequency every frame. Needs per-frame edge statistics, not the
     per-bin mean the current loop accumulates — plan the loop for it up front.

   Motivation: a forum report of ambient CD rips flagged as lossy transcodes by
   rolloff-based tooling — the false positive this must not reproduce. The 2026-08-28 rate
   gate made the 44.1 hole explicit, so this is the only thing that will ever cover a
   16/44.1 or 24/44.1 lossless file.

3. **v3 `.mbxs` fixture for restfulbee** (chore, small). Generate a small synthetic v3
   sidecar (178 B records) + matching mini-catalog so the hub-side v3 reader lands against a
   known-good file. Their lane after that: sidecar-only boot.

## Extractor build detection (parked 2026-07-30)

Truedat cannot tell which Essentia extractor build it is running — `.1`/`.2`/`.3` all report
the same library version string, but they differ in the ChordsDetection length ceiling
(~12,172 s small-buffer vs ~48,695 s large-buffer) and, since `.3`, in speed (-O2). With
`--max-duration` defaulting to 48000, a box still carrying the old `.1` extractor silently
feeds >200-min tracks to a binary that dies at ~12,172 s (per-track FAILED, sticky in the
errors CSV).

Proposed: hash the extractor exe once at scan start (~20 ms, MD5) against a small built-in
table of known builds (canonical list in `essentia-build/OUTPUT-BUILDS.md`); print the build
and its real ceiling in the scan header; warn or cap when the effective `--max-duration`
exceeds what the detected build survives. Unknown hash → say so and trust the flag. An
explicit `--max-duration` always wins.

Operator-parked; do not build without an explicit go.

## Compressed live catalog — stretch

The backup/snapshot half shipped 2026-08-09 (`CatalogArchive`, compressed rotated backups,
`--snapshot`, `--restore`). The live-catalog half is unbuilt.

**It is mutually exclusive with MBXHub's catalog span index (2026-08-21).** The hub boots
its mood cache from a byte-offset index into the live catalog (`CatalogSpanIndex`): ~20 bytes
per track, seeking one record on demand. You cannot seek to an arbitrary byte in a DEFLATE
stream, so compressing the live file does not degrade that index — it deletes it, and hands
the hub back the whole-catalog memory cost this stretch was never meant to touch. The gate is
therefore a **trade, not a permission**: taking it means first replacing the span index with
something that survives compression (a per-record-framed container, or spans as decompressed
offsets plus a framing index). Recorded on the MBXHub side too.

If it is ever taken, the consumer-side constraints restfulbee set (2026-08-09) stand:

1. **All consumers, not just the hub** — the AutoQ model tooling reads `mbxmoods.json`
   directly and would break silently at the next retrain.
2. **Sniff must tolerate BOM + leading whitespace** — `PK` → ZIP, `1F 8B` → gz, else skip
   optional BOM/whitespace and expect `{`.
3. **Decompress as a Stream, not a string/byte[]** — the hub parses with a streaming
   `Utf8JsonReader` inside MusicBee's process; a decompress-to-string trades the disk win
   for a multi-hundred-MB allocation spike.
4. **Atomicity becomes load-bearing** — a truncated ZIP fails at the container layer with no
   partial-data fallback, worse than a half-written plain JSON.

Deliberately not: delta/diff archives, zstd/brotli (not in net48).

## Verify checkpoints (approach B) — unbuilt, unauthorized

Per-entry `lastVerified` timestamps so verify auto-resumes over the un-verified remainder
without manual sharding. Its costs stand: it changes the locked schema and makes *plain*
verify a writer (saving the whole catalog to record timestamps), turning a read-only audit
into a full write. `--chunk M/N` on `--verify` (shipped 2026-08-16) covers the driving case;
only revisit this if manual sharding proves too clumsy in the field.

## Scan policy — next items

- **`speech` as a fourth exclusion rule kind.** `{"kind":"speech","action":"exclude"}` beside
  `folder`/`genre`/`file`, parsed and matched by `ExclusionSet`, merged through the existing
  `--apply-exclusions`, under the same include-always-wins layering. **The constraint is the
  design:** exclusions are evaluated against the XML work list *before* analysis, but
  `speechLikely` derives from features that exist only *after* it — so a speech rule cannot
  gate a track nobody has analysed. Honest semantics are **analyse once, classify, excluded
  thereafter**: the rule set and the catalog co-evolve and the work shrinks every pass.
  Rejected alternative: gating on a cheap pre-analysis proxy (duration + codec) — that is a
  heuristic deciding, which is exactly what the Heuristics → Evidence arc removed.
- **`--log [path]`** — tee console output to a file *without* `--audit`'s per-track verbosity.
  Today the only way to keep a run's output is `--audit`, which couples "persist my output" to
  "tell me about every skipped track". `TeeWriter` and the plumbing already exist; `--audit`
  would become "verbose, implies `--log`".
- **`--apply-exclusions` concurrency.** The write is atomic and takes a cross-process lock, so
  truedat cannot lose another truedat's rules; nothing can serialise it against a text editor
  saving over the file, and the `.bak` remains the recovery path. Recorded so the gap is known
  rather than assumed closed.

## Speech → transcription

`speechLikely` classifies a **genus** — audiobook, comedy, lecture, news, interview and
talk-dominant are one class — and deliberately does not attempt the species, because
podcast-vs-audiobook is provenance and provenance is not in the audio. **There will be no
`podcastLikely` verdict** (see `CLAUDE.md`).

Transcription is the direction detection points at. Not started, and it changes the tuning
question: today the thresholds favour precision because a `"yes"` once drove a destructive
prune. As a *transcription selector* the cost matrix inverts — a false positive wastes one
transcode, a false negative means a recording is never transcribed — so the gates want
loosening, but deliberately: a method-tag bump against a labelled set, not drift. Binding
constraints: offline-first (never cloud ASR) and the three-tier Python rule, so it ships as an
opt-in sibling tool on the `vam-tools/` / `smfm-tools/` precedent, never a runtime dependency
of the scanner. Segment-level speech/music boundaries would need real extraction — i.e. a
rescan — unlike the write-time verdict.

## Duplicate review — make it great

Detection, keeper recommendation, the offline interactive page, the folder-pair rollup and the
`--manifest` companion all shipped (5.3.9). Remaining:

- **Triage speed:** keyboard nav; reviewed-state + "unreviewed only" filter with progress;
  search box; sort by reclaimable space; per-group and running wasted-space totals.
- **Keeper signals:** prefer the in-library copy and higher playcount / rating; prefer
  canonical folder structure over `download/` / `tmp/`; configurable keeper order; flag
  ambiguous keepers.
- **Detection quality:** similarity/confidence score on probable groups; split strong-vs-loose
  probable; guard against live-vs-studio false pairs (duration/dynamics delta); whole-album
  duplicate rollup.
- **Safety:** pre-build dry-run summary (what the `.m3u8` will contain + size); keeper
  exists-on-disk check; cross-group safety (a keeper is never a loser elsewhere).
- **SMFM:** show channel/GBPM; flag groups where no copy carries SMFM.

## Cross-encode duplicate precision — segmented MFCC

`--duplicates`' probable tier quantizes the stored *aggregate* (mean) MFCC, so two different
songs sharing mean timbre, length, key and tempo can collide: great recall, imperfect
precision, hence review-and-confirm rather than blind delete. The precision fix is **segmented
MFCC** — N equal-*fraction* segments (~8 → 104 numbers, vs chromaprint's thousands) —
recovering chromaprint's temporal discrimination at a fraction of the size, alignment-tolerant.
It needs a new extraction and a nullable schema field (`mfccSegments`), so it is the upgrade
path only if cross-encode dedup goes first-class.

Facts that drive the design, so they are not re-derived: MFCC comes from decoded PCM, so
lossless↔lossless transcodes produce identical features and are **invisible to
`audioStreamSha256`** (which hashes the compressed bytes) — the real win unique to a feature
hash. Lossy transcodes shift MFCC slightly; quantization absorbs that, and its granularity is
the precision/recall knob. Never hash the authenticity/HF/`bitUsage`/loudness fields — those
are designed to differ between a lossless file and its lossy copy.

## Authenticity — remaining detection gaps

Phases 1–5 shipped; current method tag `truedat-v1-fft-rategate-2026-08-28`, scorecard 23/23
hi-res and 21/23 transcode on corpus-1. What is left, roughly by value/cost:

1. **Encoder string whitelist/blacklist** (helps LP rips, EAC, dBpoweramp).
2. **ML weight tuning spike** — once enough hand-reviewed tracks accumulate (~200+ per
   question), run a logistic regression against the verdict-input features and use the learned
   coefficients as **better starting thresholds for the explicit voter**. Keep the explicit
   voting algorithm (debuggable, tunable, no retraining loop); same output schema, no consumer
   change. Full ML inference in production stays deferred — the explicit voter is the
   labeled-data accumulator first.
3. **AAC ESDS-box encoder fingerprint** — the MP3 LAME-tag equivalent for AAC/M4A.
4. **Verdict-only re-emit mode** — tune thresholds on a 70k library without a rescan.
5. **LAME→LAME re-encode chain detection** — the remaining transcode gap: a second LAME encode
   rewrites the Xing tag and masks the source, so both chain cases verdict "no".
6. **`hfSpectralStructure.imagingSymmetry` is emitted but unused in the vote** — Lanczos
   suppression neutralized it on corpus-1; kept for future-corpus tuning.

### Detection challenges that must inform any new signal

| Case | Why naive detectors fail | What is needed |
|---|---|---|
| **False positive on CDDA / "Fakin the Funk"-style rips** | Naive spectral rolloff sees the ~15 kHz ceiling and flags it, ignoring that the rip is genuinely from a low-HF source (pre-1985 recording, dark master) | LAME-tag absence on a non-Xing MP3 **plus** rolloff, never either alone. A genuine rip with a real LAME tag and `lowpassHz: 19500` must not be flagged because the *music* lacks 16 kHz content. |
| **False negative on 320→320 transcode** | The second encoder preserves the first's lowpass character; spectral evidence is gone | Encoder fingerprint is the only handle — original 320k from LAME has `Mp3LameTag.version: "LAME3.x"`; a transcode typically has no LAME tag (ffmpeg) or a different version. Music CRC drift is a secondary tell. |
| **False negative on 128→320 transcode** | The second encoder can add HF noise above what the source had, making rolloff look wide enough | Same handle. Also: a surviving source LAME tag reading `lowpassHz: 16000` on a "320k" file is definitive regardless of spectral measurement. |
| **Genuine 16-bit FLAC with low HF** | Spectral-only detection sees a 15 kHz ceiling and calls it fake | Never apply hi-res detection below a claimed `bitDepth` of 24. The detection is about the *claim*, not generic spectral analysis. |
| **Upsampled CD with dither added** | Dither in the upsample chain restores LSB activity, defeating a naive `lowestNonZeroBit ≥ 16` | A real evasion, so detection is probabilistic: `bitUsage.bottomBitActivity` distribution (real 24-bit is more uniform) + content above 22 kHz (dither cannot fabricate it) + encoder forensics. |

Standing rules these produce: vote and weight over signals rather than a single threshold;
refuse to verdict on weak or contradictory signals (`unknown`, never a guess); gate by codec so
hi-res checks only touch claimed-lossless ≥24-bit and transcode checks only MP3/AAC; and treat
naturally-low-HF corpora as a legitimate class, not a defect.

## Mood axes still parked

| Axis | Status |
|---|---|
| **Section-aware mood** | Parked — a structural schema change (per-section arrays vs scalars), needs MBXHub-side consumer changes and a new validation corpus. |
| **VADER lyrical sentiment** | Parked indefinitely — runtime lyric fetching is off-limits under offline-first, and library coverage is whatever fraction carries tagged USLT/SYLT (low). Revisit only if a lyrics-curation flow exists upstream. |

## DSD / non-PCM format support

`.dsf` / `.dff` / `.dsd` are caught cleanly at scan entry today — a row in
`mbxmoods-skipped.csv` with reason `unsupported codec: DSD`, no catalog entry and no error
row — so there are no user-visible failures. Full DSD-to-PCM support (likely an ffmpeg
`dsd2pcm` bridge) is still unbuilt and P4.

## SpeechTagSniffer's MaxId3Walk evidence ceiling

`Truedat/SpeechTagSniffer.cs` caps the ID3v2 walk at `MaxId3Walk = 128 KB`, so a marker frame
(`PCST`/`WFED`/`TGID`/`TCON`) positioned after that point is never reached. Since the
Heuristics → Evidence rewrite the sniffer no longer skips or prunes anything — informing a
human via `--preview` is its only job — so a cap that silently truncates evidence deserves a
deliberate decision rather than the accident it currently is.

Proven live: an XTC MP3 in the test corpus carries a 703 KB `APIC` frame that trips the
malformed-frame bailout at ~132 KB; any marker after it is unreachable. That file is a clean
negative, so nothing is known to be missed today, but the ceiling will bind on some future
file with large artwork ahead of a genuine marker.

Raise with the operator whether to grow the cap, skip over an oversized non-text frame instead
of bailing, or leave it. Do not change it without that decision.

## Scan-summary perf diagnostics (low priority)

Add CPU + disk-IO lines to the end-of-scan summary so a run self-reports whether it was
CPU-bound or IO-bound. Motivated by a 2026-07-14 storage benchmark: analysis time was flat at
~59 s/track across WiFi / LAN / USB HDD / SSD, pointing at an on-box bottleneck — the scan runs
`ProcessorCount-2` uncapped Essentia subprocesses, each a heavy multi-pass FFT workload.

```
  CPU:        avg 96%   peak 100%          (all cores)
  Disk read:  avg 18 MB/s  peak 140 MB/s   (2.9 GB total)
  Disk write: avg 0.3 MB/s peak 12 MB/s    (72 MB total)
```

- **Existing Windows counters only** — `System.Diagnostics.PerformanceCounter` (in net48, no
  P/Invoke, no new deps): `Processor(_Total)\% Processor Time`, `PhysicalDisk(_Total)\Disk Read
  Bytes/sec`, `Disk Write Bytes/sec`. Sample ~1 s on a background thread, track avg + peak;
  total bytes falls out of integrating the rate.
- Use `_Total`, **not** per-process: the real disk IO happens inside the Essentia/ffmpeg child
  processes, so a `Process\truedat` counter reads ~0 and would mislead. System-wide numbers are
  fine on a dedicated box and noisy on a busy workstation — worth saying so in the output.
- Wrap counter creation in try/catch and degrade to `unavailable`; a diagnostic must never
  abort a scan. Discard the first `.NextValue()`.

Interpretation: CPU ≈ 100% with low disk read ⇒ CPU-bound (scale out via `--chunk`); low CPU
with high disk ⇒ IO-bound. Observability only, hence low.

## Diagnostics: name the subject, measure the cost

Motivated by a measured case: a `--fixup` over a wireless link spent **13m 9s of a
13m 46s run** in the existence sweep — 72,144 `File.Exists` calls at ~10.9 ms each — and
nothing in the output said so. The same catalog sweeps in ~10s on local disk. Meanwhile the
run log was 11 MB, essentially all of it per-track `--audit` verdict tracing.

Shipped: per-root latency profile at the fixup root check (`SampleStatLatencyMs`,
`MedianMs`, `SweepConcurrencyFor`). Everything below is not.

### Parallel metadata sweep, sized from measured latency

The sweep is serial while the rest of the scan runs at parallelism 6. Metadata calls carry
~0 bytes, so throughput is `concurrency / latency` and the link cannot saturate — the
opposite regime to reading audio, where one sequential stream beats N parallel ones and
staging already does the right thing. Predicted 13m 9s -> ~1m 39s at concurrency 8.

Take the concurrency from the measured p50, not a constant: the correct value differs by
an order of magnitude between local disk and a wireless share. Cap it — SMB credits and a
modest NAS CPU flatten the curve early, and the same share is usually streaming audio to
the player while this runs.

### Per-operation instrumentation

Every operation that can fail or be slow records its **subject** (which file), its
**duration**, and its **outcome**. Failure attribution then falls out for free rather than
being a separate feature: a failure is an operation whose cost ended in an error.

### Demote per-track verdict tracing

`--audit` currently emits the full per-signal vote trace for every track including the
overwhelming majority that decide `no`. It buries everything else. Give it its own switch.

### Storage profile with a stored baseline

Report per root: ops/sec (latency regime) and bytes/sec (bandwidth regime). The ratio says
which side of the sequential-vs-parallel line a phase sits on, without the operator needing
to know the transport. A fixed-sample benchmark writes a baseline; later runs diff against
it, so comparing two environments is a measurement rather than two anecdotes.

### Advisor must not recommend commands that cannot clear the count

`--stats` cascades: it recommends `--retry-errors`, which **deletes the error ledger**, so
the same entries reclassify as merely stale and are then recommended for `--refresh`, which
cannot help them either. The ledger has to survive the retry, and a count that no command
can act on should be reported as a fact, not as pending work.

### One review file for everything the catalog does not hold

Failures, policy skips and exclusions in one file with reasons, replacing the separate
errors/skipped CSVs. Nothing is retried implicitly; retry is explicit and updates records
rather than erasing them.
