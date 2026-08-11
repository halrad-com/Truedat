# mbxmoods.json field catalog

**Producer / consumer reference.** Truedat writes `mbxmoods.json` during a library scan; MBXHub's
AutoQ engine reads it. This lists every field truedat produces, its source, type, presence, and — at a
high level — what MBXHub uses it for.

- **Producers.** Most fields are **Essentia**-computed from the audio during a scan (grouped by wave
  below). The **Sony SMFM (12-TONE)** block is *read from embedded file tags* — truedat never computes
  it, it reads it when present. Identity/housekeeping fields come from TagLib and managed hashing.
- **Presence.** `always` = every analyzed track; `nullable` = omit-when-null (legacy entries,
  ffmpeg-absent installs, or content that doesn't produce it — the key is omitted, never written `null`).
- **Emission policy.** Which fields are written is governed by the field-policy registry
  `mbxmoods-schema.json` (`required` / `extended` / `excluded`; unlisted fields default to emitted). The
  raw BPM-vote histogram is **not** stored — only the peak summaries (`bpmFirstPeak*` / `bpmSecondPeak*`).
- **Consumer column.** A plain-language category of what MBXHub uses the field for. It is intentionally
  high-level; the exact scoring lives in MBXHub.

---

## Core features (always present)

| Field | Extractor source | Type | MBXHub use |
|---|---|---|---|
| `bpm` | `rhythm.bpm` | num | tempo matching |
| `key` | `tonal.key_edma.key` | str | harmonic (Camelot) matching |
| `mode` | `tonal.key_edma.scale` | str | harmonic (Camelot) matching |
| `spectralCentroid` | `lowlevel.spectral_centroid.mean` | num | acoustic-timbre feature |
| `spectralFlux` | `lowlevel.spectral_flux.mean` | num | acoustic-timbre feature |
| `loudness` | `lowlevel.loudness.mean` | num | level feature |
| `danceability` | `rhythm.danceability` | num | rhythm feature; speech-detection input |
| `onsetRate` | `rhythm.onset_rate` | num | rhythm feature |
| `zeroCrossingRate` | `lowlevel.zerocrossingrate.mean` | num | speech-detection input |
| `spectralRms` | `lowlevel.spectral_rms.mean` | num | level feature |
| `spectralFlatness` | derived: mean of `{bark,erb,mel}bands_flatness_db.mean` | num | tonal-vs-noisy (flatness) feature |
| `dissonance` | `lowlevel.dissonance.mean` | num | tonal-tension feature |
| `pitchSalience` | `lowlevel.pitch_salience.mean` | num | tonal feature |
| `chordsChangesRate` | `tonal.chords_changes_rate` | num | chord-density feature |
| `mfcc[]` | `lowlevel.mfcc.mean` (13) | num[] | acoustic-timbre feature |

## Tonal / rhythm wave (2026-07-22, nullable)

| Field | Extractor source | Type | MBXHub use |
|---|---|---|---|
| `keyVotes.{krumhansl,temperley,edma}` | `tonal.key_*` `{key,scale,strength}` | obj | key confidence + agreement |
| `bpmFirstPeak` / `bpmFirstPeakWeight` / `bpmFirstPeakSpread` | `rhythm.bpm_histogram_first_peak_*` | num | tempo; speech-detection input |
| `bpmSecondPeak` / `bpmSecondPeakWeight` / `bpmSecondPeakSpread` | `rhythm.bpm_histogram_second_peak_*` | num | tempo half/double disambiguation |
| `chordsKey` / `chordsScale` | `tonal.chords_key` / `chords_scale` | str | chord feature |
| `chordsHistogram[24]` | `tonal.chords_histogram` | num[] | harmonic concentration/entropy (chord-tightness) |
| `chordsNumberRate` | `tonal.chords_number_rate` | num | chord feature |
| `tuningFrequency` (+3 temperament scalars) | `tonal.tuning_*` | num | tuning feature |
| `averageLoudness` | `lowlevel.average_loudness` | num | level feature; re-scan wave marker |

## Harmonic-texture + capture-once wave (2026-08-09, v0.5.4.7, nullable)

| Field | Extractor source | Type | MBXHub use |
|---|---|---|---|
| `hpcp12[12]` | `tonal.hpcp.mean` folded 36→12, unit-max | num[] | harmonic-texture matching (chroma) |
| `dynamicComplexity` | `lowlevel.dynamic_complexity` | num | dynamics feature; re-scan wave marker |
| `thpcp12[12]` | `tonal.thpcp` folded 36→12, unit-max | num[] | harmonic-texture (key-invariant chroma) |
| `beatsIntervalMean/Stdev/Min/Max` | diffs of `rhythm.beats_position` (s) | num | beat-regularity feature |
| `spectralCentroidStdev` | `lowlevel.spectral_centroid.stdev` | num | within-track variability |
| `spectralEnergyStdev` | `lowlevel.spectral_energy.stdev` | num | within-track variability |
| `dissonanceStdev` | `lowlevel.dissonance.stdev` | num | within-track variability |
| `pitchSalienceStdev` | `lowlevel.pitch_salience.stdev` | num | within-track variability |
| `hpcpEntropyStdev` | `tonal.hpcp_entropy.stdev` | num | within-track variability |
| `zeroCrossingRateStdev` | `lowlevel.zerocrossingrate.stdev` | num | speech-detection feature (ZCR variability) |
| `mfccStdev[13]` | √diag(`lowlevel.mfcc.cov`) | num[] | speech/timbre feature (MFCC variability) |

## Extended features (nullable)

| Field | Extractor source | Type | MBXHub use |
|---|---|---|---|
| `dynamicRange` (+`dynamicRangeSource`) | LRA | num | mood (valence/arousal) model input |
| `loudnessMomentary` | loudness envelope | num | level feature; base re-scan canary |
| `loudnessShortTerm`, `replayGain` | loudness envelope | num | level features |
| `silenceRate20dB/30dB/60dB` | `lowlevel.silence_rate_*` | num | structure feature; `silenceRate30dB` is a speech-detection input |
| `spectralRolloff/Complexity/Entropy/Kurtosis/Skewness/Spread/StrongPeak/Decrease/Energy` + 4 `spectralEnergyBand*` | `lowlevel.spectral_*` | num | spectral-timbre features |
| `spectralContrastCoeffs[]` / `spectralContrastValleys[]` | `lowlevel.spectral_contrast_*` | num[] | timbre features; `Coeffs` is a re-scan wave marker |
| `gfcc[]` | `lowlevel.gfcc.mean` | num[] | gammatone timbre feature (MFCC complement) |
| `hfc` | `lowlevel.hfc.mean` | num | timbre feature |
| Bark/ERB/Mel band stats (crest/flatness/kurtosis/skewness/spread × 3 = 15) | `lowlevel.{barkbands,erbbands,melbands}_*` | num | spectral-timbre features |
| `beatsLoudnessBandRatio[]` | `rhythm.beats_loudness_band_ratio.mean` | num[] | per-beat spectral balance feature |
| `beatsLoudness`, `chordsStrength`, `hpcpCrest`, `hpcpEntropy` | rhythm / tonal | num | timbre/tonal features; `chordsStrength`/`hpcpEntropy` are speech-detection inputs |

## Authenticity (nullable — fake-hi-res / transcode signals)

| Field | Source | MBXHub use |
|---|---|---|
| `bitUsage{lowestNonZeroBit,bottomBitActivity,effectiveBits,samplesAnalyzed,method}` | ffmpeg PCM walk | fake-hi-res verdict input |
| `hfEnergyRatio` (+`hfEnergyMethod`) | managed FFT | fake-hi-res verdict input |
| `hfSpectralStructure{flatness,peakToMean,imagingSymmetry}` | managed FFT | fake-hi-res verdict input |

## Verdict — nested `truedat` object (computed at write time, not persisted in the cache)

| Field | Meaning | MBXHub use |
|---|---|---|
| `hiresGenuine` / `hiresConfidence` | fake-hi-res verdict | authenticity signal |
| `lossyTranscodeLikely` / `lossyTranscodeConfidence` | transcode verdict | authenticity signal |
| `speechLikely` / `speechConfidence` / `speechMethod` | talk-vs-music classification | tracks classified `speechLikely == "yes"` are excluded from AutoQ picking |
| `method` | algorithm/threshold tag | provenance |

## Sony SMFM (12-TONE) (nullable — read from file tags, not computed)

| Field | Meaning | MBXHub use |
|---|---|---|
| `smfmScores[10]` | raw STMO slot scores | downstream valence/arousal derivation |
| `smfmChannel` | dominant raw STMO slot (argmax) | downstream |
| `smfmChannelName` | always null (device-refuted) | not used |
| `smfmBpm` | Sony GBPM | tempo reference |

## Identity / housekeeping

| Field | Meaning | MBXHub use |
|---|---|---|
| `audioStreamSha256` | content identity (frame-anchored for FLAC) | cross-system content key / dedup |
| `fingerprint.v1{...}` | composite identity (codec/props/LAME tag) | dedup, verify; authenticity verdict input |
| `fileMd5` | whole-file MD5 (only written under `--file-md5`) | none (MBXHub indexes `audioStreamSha256`) |
| `lastModified`, `analysisDuration` | housekeeping | scan cache / ETA |

---

*Producer columns (field, source, type, presence) maintained by truedat. The MBXHub-use column is a
high-level summary; the authoritative emission list is `mbxmoods-schema.json`.*
