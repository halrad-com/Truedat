# SMFM vs Essentia — a SWOT of two mood-analysis lineages

truedat sits on two independent analysis lineages for the same audio. **Essentia** — open
source, documented, reproducible — supplies the ~55 named features truedat extracts per track
(bpm, key/mode, spectral shape, MFCCs, loudness, …). **SMFM** — Sony's proprietary 12 TONE /
SensMe payload, reverse-engineered here — supplies 10 mood-model scores plus BPM, read
if already present in the file or its sidecar. Two engines, two eras, zero shared code: when
they agree, that's real signal; where they differ, one of them is telling you something.

This is the honest comparison. Every claim is tagged:
**[measured]** — quantified on real library data · **[structural]** — follows from what each
system *is* · **[inference]** — our judgement, not yet verified.

Measurement basis (see method note at bottom): ~100-track correlation battery, 362-track
raw-byte STMO verification, 52-track live device test.

## Strengths (SMFM)

- **Real valence signal.** SMFM-derived valence tracks major/minor mode at **+0.16** correlation
  where a hand-weighted linear formula over Essentia features managed only **+0.04** (nearly
  flat). Valence is the notoriously hard half of mood estimation, and SMFM demonstrably carries
  signal a feature formula under-uses. **[measured]**
- **Independent arousal confirmation.** SMFM-derived arousal correlates ≈ **0.75** with the
  Essentia-feature-based estimate, and both track the same energy proxies (spectral centroid
  ≈ 0.6, danceability ≈ 0.4). Two unrelated codebases agreeing this closely is strong mutual
  validation. **[measured]**
- **Zero marginal cost.** The data is already embedded in the files (or sidecars) by Sony's
  tooling; reading it is a header-only parse — no decode, no subprocess, no analysis time.
  truedat reads it on every scan for free. **[structural]**
- **Patent-grounded foundation.** 12 TONE's chroma basis is documented in Sony patents
  US7601907B2 / US7649137B2 — the analysis lineage is real engineering, not a black box of
  unknown provenance. **[structural]**
- **Independent BPM cross-check.** `GBPM` gives a second opinion on tempo against Essentia's
  known-noisy bpm field. **[structural]**

## Weaknesses (SMFM)

- **Proprietary and undocumented.** Every decode in these docs is reverse-engineered inference,
  confidence-tiered in `SMFM-KNOWLEDGE.md`. There is no spec to appeal to. **[structural]**
- **Non-reproducible coverage.** SMFM exists only where Sony's toolchain analyzed the track.
  It is a historical artifact of a library's Sony era — you cannot (practically) regenerate it
  for new music without running Music Center for PC. **[structural]**
- **10 opaque dimensions vs ~55 named ones.** Essentia features have names, units, and
  literature behind them; STMO gives ten unlabeled scores over an internal model. **[structural]**
- **Unknown absolute calibration.** The SMFM-derived (V,A) spread (~0.23) differs from the
  Essentia-formula estimate (~0.10); rank order is comparable but the scales don't line up, and
  neither has been calibrated against ground-truth labels. **[measured]**
- **Degenerate values.** `0xFE` slot scores appear on some tracks (esp. low-quality live
  recordings) — analyzed does not mean usable; consumers must filter. **[measured]**
- **Most of the payload is still dark.** STBF/STSA/STMM are partially decoded at best;
  STNM/GVNM not at all. What we can *use* today is STMO + GBPM (+ STHF structurally).
  **[structural]**

## Opportunities

- **STHF: a temporal arc Essentia doesn't have.** truedat's Essentia output is whole-track
  aggregates; SMFM's STHF block is a 40-bin energy curve *across* the track — the only
  within-track temporal signal in either lineage as shipped. If within-track mood/energy
  progression is ever wanted, STHF is the ready-made source. **[structural]**
- **De-biasing human annotation.** SMFM's (V,A) is a genuinely independent second opinion —
  useful for flagging tracks where a human anchor and the Essentia-formula estimate disagree,
  and re-scoring with fresh eyes. Reference, not ground truth. **[inference]**
- **Ground-truth recalibration.** The 10-score → (V,A) projection was fitted unsupervised; a
  re-fit against real labels (e.g. DEAM's 1,802 continuously-annotated songs) would fix the
  absolute-scale weakness for both lineages at once. **[inference]**
- **STBF as a decode key.** STBF regresses against Essentia descriptors at R² up to 0.77 on
  characterized offsets — the redundancy that makes STBF unexciting as a *signal* makes it
  crackable as a *format*: Essentia features are the labels for decoding the remaining offsets.
  **[measured]**

## Threats

- **The writer is a single point of failure.** Sony can drop or change 12 TONE in any Music
  Center update; the format has already survived five hosts (SonicStage → x-APPLICATION →
  Media Go → VAIO MusicBox → Music Center), but nothing obliges a sixth. The embedded payloads
  survive in the files either way. **[inference]**
- **Coverage decay.** Every track added to a library without a Sony analysis pass widens the
  SMFM-less fraction; the signal slowly becomes an archive of the past. **[structural]**
- **Stale folklore.** Earlier reverse-engineering claims — slot→channel name tables, the
  temporal-segments reading of STMO — were refuted by device testing and raw-byte verification,
  but old copies of them (including earlier comments in our own tools) can mislead new
  consumers. `SMFM-KNOWLEDGE.md` §6 is the antidote; check claims against it. **[measured]**

## When to prefer which

| Need | Use |
| ---- | --- |
| Mood / V-A estimation | Both — Essentia-derived as primary, SMFM as the independent reference |
| Valence specifically | SMFM currently carries more mode-tracking signal than a linear feature formula |
| Reproducible pipeline, new music | Essentia only — SMFM can't be regenerated on demand |
| Within-track temporal arc | STHF is the only candidate in either lineage |
| Tempo | Essentia primary; `GBPM` as cross-check |
| Authenticity / fake-hi-res forensics | Essentia-side signals only (bitUsage, HF analysis) — SMFM has nothing here |

## Method note

The `[measured]` numbers compare SMFM against *a specific downstream V/A formula built on
Essentia features* — not against Essentia itself. Essentia supplies features; turning features
into valence/arousal is a separate modeling step, and the +0.04 valence flatness is a property
of that formula, not of the features. Sample sizes: ~100 random library tracks for the
correlation battery; 362 tracks for the raw-byte STMO sub-index verification; 52 tracks for the
live Walkman channel test. All measurements are single-library and should be treated as strong
indications, not universal constants.
