#!/usr/bin/env python3
"""
Visualize the mood map of your library (Energy and Vibe).

Usage: python visualize.py [mbxmoods.json]
"""

import json
import sys
import matplotlib.pyplot as plt


def load_trackdat(path):
    """Load mbxmoods.json and extract valence/arousal."""
    with open(path, "r") as f:
        data = json.load(f)

    tracks = []
    for file_path, features in data.get("tracks", {}).items():
        tracks.append({
            "artist": features.get("artist", ""),
            "title": features.get("title", ""),
            "valence": features.get("valence", 0.5),
            "arousal": features.get("arousal", 0.5)
        })
    return tracks


def plot_mood_map(tracks):
    """Plot the 2D mood map."""
    valence = [t["valence"] for t in tracks]
    arousal = [t["arousal"] for t in tracks]
    labels = [f'{t["artist"]} - {t["title"]}' for t in tracks]

    plt.figure(figsize=(10, 10))
    plt.scatter(valence, arousal, alpha=0.6, c=arousal, cmap='RdYlGn')

    plt.xlabel("Mood (dark ← → bright)")
    plt.ylabel("Vibe (chill ← → hype)")
    plt.title("Mood Map of Energy and Vibe")

    # Add quadrant labels
    plt.text(0.1, 0.9, "Angry/Tense", fontsize=10, alpha=0.5)
    plt.text(0.8, 0.9, "Excited/Happy", fontsize=10, alpha=0.5)
    plt.text(0.1, 0.1, "Sad/Bored", fontsize=10, alpha=0.5)
    plt.text(0.8, 0.1, "Calm/Relaxed", fontsize=10, alpha=0.5)

    # Annotate some points
    step = max(1, len(labels) // 30)
    for i in range(0, len(labels), step):
        plt.annotate(labels[i], (valence[i], arousal[i]), fontsize=6, alpha=0.7)

    plt.xlim(0, 1)
    plt.ylim(0, 1)
    plt.grid(True, alpha=0.3)
    plt.colorbar(label="Vibe")
    plt.tight_layout()
    plt.show()


if __name__ == "__main__":
    path = sys.argv[1] if len(sys.argv) > 1 else "mbxmoods.json"
    tracks = load_trackdat(path)
    print(f"Loaded {len(tracks)} tracks")
    plot_mood_map(tracks)
