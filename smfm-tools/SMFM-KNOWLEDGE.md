# SMFM — State of Knowledge

Single source of truth for what is known, partially known, hypothesized, open, and **falsified**
about Sony's SMFM payload. When other docs here disagree with this file, this file wins.
Last updated 2026-07-10.

**Confidence tiers used throughout:** **KNOWN** (high confidence — confirmed by raw-byte
verification, live device testing, or a Sony patent) · **PARTIAL** (structure decoded, semantics
incomplete) · **HYPOTHESIS** (plausible, unverified) · **OPEN** (unknown) ·
**FALSIFIED** (previously believed, since disproven — listed so it can't resurface as folklore).

## 1. What SMFM is — KNOWN

- Sony's binary serialization of **12 TONE ANALYSIS / SensMe** results. Written by `MMLib11.dll`.
- Stored as the SAME payload in: FLAC `APPLICATION` block (id `SMFM`), MP3 ID3v2 `GEOB`
  (mime `application/SMFMF`, desc `USR_SMFMF`), WMA ASF Extended-Content-Description
  (attr `SMFMF`), or sidecar `%APPDATA%\Sony\Music Center\fringe\audio\<pfx>\<id>\smfmf.bin`.
- Flat TLV chain. Each sub-block: 4-byte tag + 8-byte magic (`STAEMMLW` PC / `STAESMML`
  Walkman) + 4-byte flags (`01 00 80 00`) + 4-byte big-endian length + payload.
  Full layout: `SMFM-FORMAT.md`.
- **"12 TONE" = the 12 chromatic pitch classes** (chroma), per Sony patents **US7601907B2 /
  US7649137B2** and VAIO Music Box materials. SensMe is the mood layer built on top. Both are
  Sony in-house; **distinct from Gracenote** (which supplies only text metadata). Channel names
  live only on devices / in Media Go — never in Music Center for PC's code.

## 2. Sub-blocks

| tag | size | confidence | what |
|-----|------|------------|------|
| `GBPM` | 4 | **KNOWN** | tempo, float32 BE |
| `STMO` | 160 | **KNOWN (structure)** | 10 mood-model SCORES × 4 sub-indices. Sub-indices are IDENTICAL in 100% of tracks (raw-byte-verified; NOT temporal, NOT per-audio-channel). Decode = take one replica (or average — a no-op) → 10 ints 0–255. |
| `STBF` | 36 | PARTIAL | NOT a 12-tone chroma (falsified). Fixed-axis spectral feature vector: 2 header scalars + 33-value body + constant `0xff` at offset 28. Largely redundant with open-source spectral descriptors — regression against Essentia features reaches R² up to 0.77 on characterized offsets. |
| `STHF` | 160 | PARTIAL | 40-bin energy curve **over the track = the within-track temporal arc** (the only temporal signal in SMFM). Structure decoded; meaning ~energy/intensity. |
| `STSA` | 8 | HYPOTHESIS | ZAPPIN highlight in/out (centiseconds?) |
| `STMM` | ~2700–4000 | HYPOTHESIS | beat grid / segment timeline; the GBPM float reappears at offset 0x208 |
| `STNM` | ? | OPEN | in Msc.dll's tag list, never captured/decoded |
| `GVNM` | ? | OPEN | in Msc.dll's tag list, never captured/decoded |

## 3. SensMe channels vs the STMO slots — KNOWN (corrected by device test)

- The 10 STMO slots are **per-slot mood scores over a low-dimensional internal model**
  (unsupervised decomposition of a library-scale sample: PC1 ≈ arousal/energy, PC2 ≈ valence).
  They are stable scores, not per-track-reshuffled.
- **Device SensMe channels are REGIONS on a 2-D arousal × valence canvas, NOT bins of the 10
  slots.** Channels overlap (a track appears in several). The STMO argmax does NOT predict the
  device channel. Established by a live 52-track test on a Walkman (NW-ZX300 class).
- That device shows **12 channels (no "Emotional")**: 7 mood (Energetic, Lounge, Dance, Extreme,
  Upbeat, Relax, Mellow) + 5 time-of-day (Morning, Daytime, Evening, Night, Midnight). Other
  Walkman models may differ.
- `0xFE` (254) in a slot = **degenerate/junk** value (observed on low-quality live recordings);
  the device leaves such tracks uncategorized — "analyzed ≠ categorized".
- The channel-region thresholds live in device firmware (encrypted; unrecoverable PC-side).

## 4. SMFM → (valence, arousal) — KNOWN (measured)

The 10 STMO scores project cleanly onto a 2-D (valence, arousal) space. We fitted such a
projection downstream via unsupervised PCA (standardize → project → scale to 0..1) and compared
it against an Essentia-feature-based V/A estimate over ~100 random library tracks:

- **Arousal:** the two agree well — correlation ≈ 0.75 — and both track energy proxies
  (spectral centroid ≈ 0.6, danceability ≈ 0.4). Absolute scale differs (SMFM spread ~0.23 vs
  ~0.10) — a calibration difference, not a disagreement.
- **Valence:** SMFM valence tracks major/minor mode at **+0.16**, where the Essentia-formula
  estimate managed only **+0.04** — SMFM carries real valence signal that a hand-weighted
  feature formula under-uses.

Full comparison, including weaknesses: `SMFM-VS-ESSENTIA.md`.

## 5. OPEN / UNKNOWN

- STBF body: which offset = which feature (only the highest-R² offsets characterized).
- STSA / STMM / STHF: full decode (structures known, semantics partial).
- STNM / GVNM: undecoded.
- Firmware channel-region thresholds (encrypted; unrecoverable PC-side).
- Whether other Walkman models expose different channel sets (e.g. "Emotional").
- Absolute calibration of the SMFM-derived (V,A) against ground truth (no DEAM-style labels yet).

## 6. FALSIFIED — do NOT trust these earlier claims

Listed explicitly so stale copies of them (old scripts, old notes) can't re-enter circulation:

- **Slot→channel name tables** (e.g. "slot 6 = Extreme", "slot 2 = Emotional") — REFUTED by the
  device test. No such mapping exists; channels are canvas regions, slots are model scores.
- **"STMO's 4 sub-indices = temporal segments / per-quarter values"** — FALSIFIED. Raw-byte
  verification across 362 tracks: all 4 replicas identical in 100% of cases.
- **"STBF = 12-tone chroma"** — FALSIFIED. Body argmax pins to fixed offsets regardless of
  musical key; it's a fixed-axis spectral feature vector.
- **"12 TONE = 12 feature dimensions" / "= the SensMe channels"** — wrong; it's the 12 chromatic
  pitch classes (patent-grounded).

## 7. Where the detail lives

- `SMFM-FORMAT.md` — wire format (hex layouts, examples, open questions).
- `SMFM-VS-ESSENTIA.md` — the comparison / competitive analysis vs Essentia extraction.
- The scripts in this directory — working decoders for all four carriers.
- The full dated research trail (device-test protocol, tracks.db structure cracking, STBF
  decomposition, corpus manifests) lives in a private research archive; findings that survived
  verification are all reflected here.
