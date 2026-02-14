#!/usr/bin/env python3
"""
Truedat Analyzer - Audio feature extractor for mood vectors
Uses Essentia to compute valence/arousal mood vectors.

Usage: python analyze.py <audio_file_path>
Output: JSON with BPM, key, mode, valence, arousal, spectral features

This software uses Essentia (https://essentia.upf.edu/), licensed under AGPL-3.0.
Copyright (c) Music Technology Group, Universitat Pompeu Fabra.

Citation:
    Bogdanov, D., Wack N., Gómez E., Gulati S., Herrera P., Mayor O., et al. (2013).
    ESSENTIA: an Audio Analysis Library for Music Information Retrieval.
    International Society for Music Information Retrieval Conference (ISMIR'13).
"""

import essentia.standard as es
import json
import numpy as np
import sys


def normalize(x, min_val, max_val):
    """Normalize value to [0, 1] range."""
    return (x - min_val) / (max_val - min_val + 1e-9)


def clamp(x, min_val=0.0, max_val=1.0):
    """Clamp value to range."""
    return max(min_val, min(max_val, x))


def compute_valence(mode, spectral_centroid, valence_model, mfccs):
    """
    Compute valence (positivity/happiness) from audio features.

    Formula:
    valence = 0.35·mode_major + 0.25·spectral_centroid + 0.20·valence_model + 0.20·mfcc_brightness
    """
    mode_flag = 1 if mode == "major" else 0
    mfcc_brightness = np.mean(mfccs[:5])

    return clamp(
        0.35 * mode_flag +
        0.25 * spectral_centroid +
        0.20 * valence_model +
        0.20 * mfcc_brightness
    )


def compute_arousal(bpm, loudness, spectral_flux, energy_model):
    """
    Compute arousal (energy/intensity) from audio features.

    Formula:
    arousal = 0.4·BPM + 0.3·loudness + 0.2·spectral_flux + 0.1·energy_model
    """
    return clamp(
        0.4 * bpm +
        0.3 * loudness +
        0.2 * spectral_flux +
        0.1 * energy_model
    )


def analyze(path):
    """
    Analyze an audio file and extract mood features (valence, arousal).

    Returns dict with:
    - bpm, loudness, spectral_centroid, spectral_flux
    - key, mode (major/minor)
    - valence (0-1), arousal (0-1)
    """
    # Load audio
    loader = es.MonoLoader(filename=path)
    audio = loader()

    # Core rhythm features
    rhythm = es.RhythmExtractor2013()(audio)
    bpm = rhythm[0]

    # Loudness and spectral features
    loudness = es.Loudness()(audio)
    spectral_centroid = es.Centroid()(audio)
    spectral_flux = es.Flux()(audio)

    # MFCCs for timbre
    mfcc = es.MFCC()(audio)[1]

    # Key and mode (major/minor)
    key, scale, strength = es.KeyExtractor()(audio)

    # Essentia high-level models (if available)
    try:
        music_extractor = es.MusicExtractor()
        high = music_extractor(path)
        energy_model = high['lowlevel.spectral_energy']
        valence_model = high['highlevel.valence.all']['mean']
    except:
        # Fallback if high-level models not available
        energy_model = 0.5
        valence_model = 0.5

    # Normalize features to [0, 1]
    bpm_n = normalize(bpm, 60, 200)
    loud_n = normalize(loudness, -60, 0)
    cent_n = normalize(spectral_centroid, 500, 5000)
    flux_n = normalize(spectral_flux, 0, 1)

    # Compute mood vector
    arousal = compute_arousal(bpm_n, loud_n, flux_n, energy_model)
    valence = compute_valence(scale, cent_n, valence_model, mfcc)

    return {
        "bpm": round(float(bpm), 1),
        "loudness": round(float(loudness), 2),
        "spectral_centroid": round(float(spectral_centroid), 1),
        "spectral_flux": round(float(spectral_flux), 3),
        "key": key,
        "mode": scale,
        "valence": round(float(valence), 3),
        "arousal": round(float(arousal), 3)
    }


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python analyze.py <audio_file_path>", file=sys.stderr)
        sys.exit(1)

    path = sys.argv[1]
    try:
        result = analyze(path)
        print(json.dumps(result))
    except Exception as e:
        print(json.dumps({"error": str(e)}), file=sys.stderr)
        sys.exit(1)
