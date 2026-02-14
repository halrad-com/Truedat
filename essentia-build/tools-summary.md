# Essentia x64 Tools Summary

All 53 tools built from `essentia-src/src/examples/`. Each is a standalone static x64 Windows binary.

## Rhythm & Beat Detection

| Tool | Purpose |
|------|---------|
| streaming_beattracker_multifeature_mirex2013 | Beat tracking with MIREX output format |
| streaming_rhythmextractor_multifeature | Full rhythm analysis — BPM, beats, tempo changes |
| standard_beatsmarker | Adds audible beeps at beat positions (test/demo) |
| standard_onsetrate | Extracts onset rate and onset times |
| streaming_onsetrate | Streaming version of onset rate |
| standard_rhythmtransform | Rhythm pattern analysis using mel bands |

## Tonal & Key Detection

| Tool | Purpose |
|------|---------|
| streaming_key | Musical key detection (key, scale, strength) |
| streaming_tuningfrequency | Tuning frequency deviation from A=440Hz |

## Pitch Detection

| Tool | Purpose |
|------|---------|
| standard_pitchdemo | Multi-algorithm pitch detection (6 methods) |
| streaming_predominantpitchmelodia | Predominant melody extraction |
| streaming_pitchyinfft | YinFFT pitch detection |
| streaming_yinprobabilistic | Probabilistic pitch tracking |
| streaming_vibrato | Vibrato detection in monophonic audio |
| standard_vibrato | Standard version of vibrato detection |

## Cover Song / Similarity

| Tool | Purpose |
|------|---------|
| standard_coversongsimilarity | Full cover song detection (Smith-Waterman) |
| streaming_coversongsimilarity | Streaming version |
| standard_chromacrosssimilarity | Chroma-based cross-similarity matrix |
| streaming_chromacrosssimilarity | Streaming version |
| standard_crosssimilaritymatrix | Generic feature cross-similarity |

## Audio Fingerprinting

| Tool | Purpose |
|------|---------|
| standard_chromaprinter | Chromaprint/AcoustID fingerprint generation |
| streaming_md5 | MD5 hash of undecoded audio payload |

## Comprehensive Extractors

| Tool | Purpose |
|------|---------|
| **streaming_extractor_music** | **Primary music feature extractor** — 100+ features including mood, arousal, valence, genre, BPM, key, timbre. This is what Truedat uses. |
| streaming_extractor_freesound | Freesound-optimized feature extraction |

## Spectral Analysis

| Tool | Purpose |
|------|---------|
| standard_mfcc | Mel-frequency cepstral coefficients |
| streaming_mfcc | Streaming MFCC |
| streaming_gfcc | Gammatone-frequency cepstral coefficients |
| streaming_spectrogram | Magnitude, mel, and MFCC spectrograms (binary or YAML) |
| streaming_stft | Short-time Fourier transform |
| standard_stft | Standard STFT |
| standard_spectralcontrast | Spectral contrast and valley features |
| standard_welch | Power spectral density (Welch's method) |

## Audio Quality / Defect Detection

| Tool | Purpose |
|------|---------|
| standard_extractor_la-cupula | Comprehensive audio quality analyzer — clicks, hum, saturation, gaps, noise bursts, true peaks, false stereo, SNR, EBU R128 |
| standard_discontinuitydetector | Audio discontinuity/click detection |
| standard_gapsdetector | Silence gap detection |
| standard_saturationdetector | Audio clipping/saturation detection |
| standard_humdetector | Electrical hum detection (50/60Hz) |
| streaming_humdetector | Streaming hum detection |
| standard_fadedetection | Fade-in/fade-out region detection |
| standard_snr | Signal-to-noise ratio estimation |

## Loudness

| Tool | Purpose |
|------|---------|
| standard_loudnessebur128 | EBU R128 loudness measurement |
| standard_loudnessebur128_double_input | EBU R128 with double-precision input |

## Machine Learning

| Tool | Purpose |
|------|---------|
| standard_tempocnn | CNN-based tempo/BPM detection |

## Signal Modeling (SMS)

| Tool | Purpose |
|------|---------|
| standard_harmonicmodel | Harmonic/sinusoidal model analysis and synthesis |
| standard_hprmodel | Harmonic + residual model |
| standard_hpsmodel | Harmonic + percussive + stochastic model |
| standard_sinemodel | Sinusoidal model analysis/synthesis |
| standard_sinesubtraction | Sinusoidal component subtraction |
| standard_sprmodel | Sinusoidal + residual model |
| standard_spsmodel | Sinusoidal + percussive + stochastic model |
| standard_stochasticmodel | Stochastic residual modeling |

## Other

| Tool | Purpose |
|------|---------|
| standard_pca | Principal component analysis |
| standard_predominantmask | Predominant source isolation via masking |
| streaming_panning | Stereo panning analysis |
| streaming_beatsmarker | Streaming beat marker |

## Most Relevant to MBX

1. **streaming_extractor_music** — Primary tool. Extracts mood/arousal/valence used by MoodEstimator.
2. **streaming_rhythmextractor_multifeature** — BPM and beat detection.
3. **streaming_key** — Musical key detection.
4. **standard_chromaprinter** — Audio fingerprinting for duplicate detection.
5. **streaming_md5** — Fast duplicate detection via audio hash.
6. **standard_coversongsimilarity** — Cover/duplicate song detection.
7. **standard_tempocnn** — Deep learning BPM detection (alternative to rhythm extractor).
8. **standard_extractor_la-cupula** — Audio quality analysis (defect detection).
