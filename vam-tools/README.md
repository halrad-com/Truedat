# vam-tools

Optional Python toolset that ships alongside truedat for users who want to
retrain the V/A (valence/arousal) mood model from their own library. **You
do not need any of this to use truedat normally** — `truedat.exe` produces
`mbxmoods.json` standalone, and MBXHub's AutoQ engine ships with a baseline
model that works out of the box.

Reach for `vam-tools/` only when both of these are true:

1. You have already run truedat over your library and have an `mbxmoods.json`.
2. You want to replace the baseline V/A model with one trained against your
   own taste — either because the baseline doesn't fit your library, or
   because you want a model trained from your hand annotations.

## Why Python (not C#)

The retrain script uses `numpy` + `scikit-learn` + `scipy` (Ridge regression,
PCA, leave-one-out CV). Reimplementing those in C# adds engineering cost
without producing a better model, so the trainer stays in Python and ships
as a sibling tool, not as part of the single-binary `truedat.exe`. The "no
runtime Python in truedat" invariant still applies — `truedat.exe` itself
is Python-free; this is opt-in tooling for power users who already have a
Python install.

## Setup (one-time)

```
cd vam-tools
python -m venv .venv
.venv\Scripts\activate            # Windows
# or: source .venv/bin/activate    # *nix
pip install -r requirements.txt
```

`requirements.txt` pins `numpy==2.1.3`, `scikit-learn==1.5.2`, `scipy==1.14.1`.
Tested under Python 3.12+.

Verify:

```
python -c "import numpy, sklearn, scipy; print(numpy.__version__, sklearn.__version__, scipy.__version__)"
```

## Building your baseline — overview

A V/A baseline is a pair of files **you** produce against **your** library:

| File | Produced by | Purpose |
|---|---|---|
| `my-baseline-labels-vN.json` | You, by hand-annotating in step 3 below | Source of truth — your V/A scores for a representative set of your tracks. Keep this; it's the input that lets you retrain whenever the model needs updating. |
| `my-baseline-model-vN.json`  | `calibrate-valence-arousal.py` (step 4) | Trained Ridge/PCA model. Drop into MBXHub at the AutoQ-expected path. Regenerable from the labels file at any time. |

Versioning is your call — bump `vN` whenever you re-annotate or change
training parameters. The trainer stamps the model with `trainedAt` and
`anchorsUsed` so you can always trace a model back to a label set.

## End-to-end workflow

```
┌─────────────────┐    ┌────────────────────────────┐    ┌──────────────────┐    ┌──────────────────────────┐    ┌────────────────────────┐
│ truedat.exe     │ →  │ seed-autocal-from-extremes │ →  │ annotation       │ →  │ calibrate-valence-arousal│ →  │ my-baseline-model-vN   │
│ (already run)   │    │ .py                        │    │ (mbxhub UI)      │    │ .py                      │    │ .json                  │
│ mbxmoods.json   │    │ auto-cal-seed.json         │    │ my-baseline-     │    │ Ridge + PCA regression   │    │ → drop into MBXHub     │
│                 │    │ (corner candidates)        │    │ labels-vN.json   │    │                          │    │   AutoQ resources path │
└─────────────────┘    └────────────────────────────┘    └──────────────────┘    └──────────────────────────┘    └────────────────────────┘
   step 1                  step 2                            step 3                  step 4                          step 5 (deploy)
```

### Step 1 — produce `mbxmoods.json` with truedat

If you've already run truedat over your library you have this already.
See the top-level README for invocation.

### Step 2 — pick quadrant-balanced extreme tracks for annotation

```
python seed-autocal-from-extremes.py \
    --moods path/to/mbxmoods.json \
    --per-quadrant 25 \
    --out auto-cal-seed.json
```

Scores every track on z(loudness)+z(bpm)+z(onsetRate) (arousal proxy) and
mode_sign + z(chordsStrength) - z(dissonance) (valence proxy), buckets into
four quadrants (happy/angry/calm/sad), and writes 25 most-extreme tracks
per quadrant. Artist-deduped by default so a single artist can't dominate
one corner. Non-music / test / loop / unknown-artist rows filtered up
front.

Output: `auto-cal-seed.json` (a list of paths to annotate, per quadrant).

Flags: `--per-quadrant N` (default 25), `--no-artist-dedupe`, `--show-top N`
(echo top picks per quadrant to stdout).

### Step 3 — hand-annotate those tracks (V/A scores)

**This is the gap in the toolset.** The interactive annotation surface
currently lives in the MBXHub server (`/annotate` page in a running MBXH
instance), not in `vam-tools/`. To produce a labels file you currently
need to:

1. Run MBXHub locally.
2. Load the picks from `auto-cal-seed.json` into the annotate page.
3. Score each track on valence (-1..+1) and arousal (-1..+1).
4. Export the resulting labels as JSON.

A standalone CLI/local-web annotator may land here later; until then,
MBXHub is the annotation tool.

### Step 4 — train your baseline model

```
python calibrate-valence-arousal.py \
    --moods path/to/mbxmoods.json \
    --labels path/to/my-baseline-labels-v1.json \
    --out   path/to/my-baseline-model-v1.json \
    [--pairs path/to/pairs.json] \
    [--alpha-v 1.0] [--alpha-a 10.0] [--pca-k 6]
```

Joins labels to feature vectors via track path, fits a StandardScaler +
PCA + Ridge regression for valence and a StandardScaler + Ridge for
arousal. Reports leave-one-out cross-validation RMSE and Pearson r per
axis. Optionally blends a Bradley-Terry-derived pairwise comparison file
(`--pairs`) as additional training rows; the blend wins if it improves
LOO-CV r.

The 21 features used as inputs are listed in `FEATURES` at the top of
the script. Adding/removing features means regenerating models.

Output: `my-baseline-model-v1.json` — drop into MBXHub at the path
AutoQ expects (`MBXH/Core/Resources/mood-model.json` in the current
MBXH layout). The next MBXHub restart picks it up.

### Re-training later

When your library grows or your taste shifts:

1. Re-run truedat to refresh `mbxmoods.json` (cache means only new/changed
   tracks pay full Essentia cost).
2. Either re-run `seed-autocal-from-extremes.py` for fresh candidates,
   or add more annotations directly to your existing `my-baseline-labels-vN.json`.
3. Re-run `calibrate-valence-arousal.py` with the updated labels and
   write to a new versioned output (`my-baseline-model-v2.json`, etc.).
4. Watch `loocv_r` (Pearson r) in the model JSON — higher is better.
   If a new label set makes LOO-CV r worse, your new labels disagree
   with the old ones; reconcile before deploying.

### Step 5 (sidecar) — Sony SensMe extraction

`extract-sony-sensme.py` is an unrelated single-purpose tool: it walks a
local Sony Music Center installation, decodes the binary `smfmf.bin`
sidecar files (10-channel SensMe scores + BPM), and joins them to
`mbxmoods.json` by filename. Output is a separate JSON useful as
auxiliary labeled training data.

Note: truedat itself now ships a C# Sony SensMe / SMFM reader wired into
the scan path (commit `6283ce6` and follow-ups), which populates SensMe
fields directly into `mbxmoods.json` per-track during a normal scan. The
Python script remains useful for one-shot bulk extraction outside the
scan pipeline.

```
python extract-sony-sensme.py \
    --sony "C:/Users/<you>/AppData/Roaming/Sony/Music Center" \
    --moods path/to/mbxmoods.json \
    --out sony-sensme-labels.json
```

## What's not in this repo

This toolset deliberately does NOT ship:

- **A trained baseline model.** Each user trains against their own library.
  MBXHub ships its own embedded baseline that works without retraining.
- **A sample labels file.** Personal labels carry library file paths and
  taste; we don't publish either. Label file format is documented below.

## Labels file format

The `--labels` input to `calibrate-valence-arousal.py` is a JSON file:
either a top-level array of label objects, or `{"labels": [...]}`. Each
label object:

```
{
  "Key": "<audioStreamSha256>",
  "Path": "<filesystem path>",
  "Valence": -1..+1,
  "Arousal": -1..+1,
  "Confidence": "sure" | "unsure",
  "IsAnchor": true | false,
  "RatedAt": "<ISO-8601>",
  "SessionId": "<opaque>",
  "Retest": [ ... ]
}
```

`Path` is what's used to join into `mbxmoods.json`. `Key` and the other
fields are bookkeeping the trainer ignores.

## Model file format

`calibrate-valence-arousal.py` writes:

```
{
  "version": 3,
  "trainedAt": "<ISO-8601>",
  "anchorsUsed": N,
  "features": [ ... 21 feature names ... ],
  "scaler": { "mean": [...], "std": [...] },
  "valence": {
    "transform": "pca",
    "pca": { "components": [[...]], "mean": [...] },
    "coef": [...], "intercept": float,
    "alpha": float, "loocv_rmse": float, "loocv_r": float
  },
  "arousal": {
    "transform": "raw",
    "coef": [...], "intercept": float,
    "alpha": float, "loocv_rmse": float, "loocv_r": float
  }
}
```

MBXHub consumes this at the path documented in the MBXHub README.
