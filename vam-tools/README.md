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

**Just want the steps?** See **[RUNBOOK.md](RUNBOOK.md)** — a command-by-command
walkthrough from nothing to a deployed, verified model. This README is the
reference behind it (what each file is, every flag, the model format).

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

A V/A baseline is a model **you** produce by training against your own
hand-annotations. The pipeline produces a few intermediate files; **you
do not construct any of them by hand** — the tools and MBXHub do all
the file-shaped work, you just listen and tap keys.

| File                        | Produced by                                                     | Purpose                                                                                                                                            |
| --------------------------- | --------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------- |
| `mbxmoods.json`             | `truedat.exe`                                                   | Feature cache for your library (Essentia + identity).                                                                                              |
| `auto-cal-seed.json`        | `seed-autocal-from-extremes.py` (step 2)                        | 100 quadrant-balanced extreme-track picks chosen analytically from `mbxmoods.json`.                                                                |
| `auto-cal.m3u`              | `picks-to-m3u.py` (step 2.5)                                    | Playlist of the picks — load this in MusicBee so MBXHub's annotate / tune pages have something to drive playback over.                             |
| `mbxvam-labels.json`        | MBXHub `/pages/annotate.html` (step 3)                          | Scalar V/A scores per track. **MBXHub writes this automatically** as you annotate — appears in MBXHub's AppData storage path.                      |
| `mbxtune-pairs.json`        | MBXHub `/pages/tune.html` (step 3, optional)                    | Pairwise A/B comparisons for V/A. **MBXHub writes this automatically.** Optional supplemental signal for the trainer.                              |
| `train-moods.json`          | MBXHub `GET /vam/train/moods` (step 4)                          | **The trainer's actual `--moods` input.** `mbxmoods.json` enriched with the plugin's SMFM→V/A projection (`smfmValence` / `smfmArousal` / `modeMajor`), keyed by `audioStreamSha256`. Raw `mbxmoods.json` lacks these keys. |
| `my-baseline-model-vN.json` | `calibrate-valence-arousal.py` (step 4)                         | Trained Ridge model (raw standardised features; the PCA path was dropped 2026-06-16). Drop into `%AppData%\MBXHub\mood-model.json` (default; defer to MBXHub README for the canonical path). Regenerable from labels + pairs at any time. |

Versioning is your call — bump `vN` on the model whenever you re-train.
The trainer stamps every model with `trainedAt` and `anchorsUsed` so
you can always trace a model back to a label set.

## End-to-end workflow

```
┌─────────────────┐  ┌────────────────────────────┐  ┌──────────────────┐  ┌────────────────────────────┐  ┌──────────────────────────┐  ┌──────────────────────┐
│ truedat.exe     │→ │ seed-autocal-from-extremes │→ │ picks-to-m3u.py  │→ │ MBXHub /pages/annotate     │→ │ calibrate-valence-arousal│→ │ my-baseline-model-vN │
│ (already run)   │  │ .py                        │  │                  │  │  + /pages/tune (browser)   │  │ .py                      │  │ .json                │
│ mbxmoods.json   │  │ auto-cal-seed.json         │  │ auto-cal.m3u     │  │ MBXHub auto-writes:        │  │ raw-Ridge regression     │  │ → drop into MBXHub   │
│                 │  │ (corner candidates)        │  │ load in MusicBee │  │  mbxvam-labels.json        │  │ inputs:                  │  │   AutoQ resources    │
│                 │  │                            │  │                  │  │  mbxtune-pairs.json (opt)  │  │  train-moods.json ◄──┐   │  │   path               │
└─────────────────┘  └────────────────────────────┘  └──────────────────┘  └────────────────────────────┘  │  mbxvam-labels.json  │   │  └──────────────────────┘
   step 1                step 2                          step 2.5             step 3                        │  mbxtune-pairs.json  │      step 5 (deploy)
                                                                                                            └──────────────────────┘
                                          MBXHub GET /vam/train/moods ─────────────────────────────────────► train-moods.json (step 4)
                                          (mbxmoods.json + SMFM→V/A projection, SHA-keyed)
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

### Step 2.5 — turn the picks into a playlist you can load

```
python picks-to-m3u.py --picks auto-cal-seed.json --out auto-cal.m3u
```

Emits a single `auto-cal.m3u` containing all 100 picks (25 per quadrant)
in happy / angry / calm / sad order, grouped via `#EXTGRP`. UTF-8,
absolute paths emitted verbatim from the picks file, with `#EXTINF`
lines carrying duration + artist + title for player display.

Load `auto-cal.m3u` in MusicBee (or whichever player you'll annotate
against). MBXHub's annotate and tune pages drive playback through your
player, so a queued playlist is the practical hand-off between picks
and annotation.

**`auto-cal.m3u` is the input to the annotation pass — that's all it
is.** Add tracks you specifically want labeled, swap ones out, drop
the ones you don't have an opinion on, or build the playlist from
scratch in MusicBee. The script's quadrant-balanced picks are a
ready-made starter; what actually gets annotated is whatever ends up
in the playlist you bring to MBXHub.

### Step 3 — annotate via MBXHub (annotate page + tune page)

Open MBXHub in a browser. **You don't construct any files by hand here**;
both pages persist their output automatically into MBXHub's AppData
storage path:

- **`/pages/annotate.html`** — scalar V/A annotation. For each track you
  score valence (0..1) and arousal (0..1), 0.5 neutral. Verdicts accumulate to
  `mbxvam-labels.json`. This is the primary input to the trainer.

- **`/pages/tune.html`** — pairwise A/B comparisons (optional). MBXHub
  picks two tracks and asks which is more positive (valence) or more
  intense (arousal); use keys 1/2/3/0 (or A/B to listen). Verdicts
  accumulate to `mbxtune-pairs.json` and become a Bradley-Terry-ranked
  supplemental signal the trainer blends in if it improves cross-
  validation.

More verdicts — annotations or tune-page pairs — is **not** necessarily
better. What matters in both surfaces is **diversity** (coverage of the
full V/A space, which is exactly what the quadrant-balanced picks give
you) and **consistency** (rate similar tracks similarly, judge similar
A/B pairs the same way, don't drift mid-session). The 100 picks are
placed at the extremes precisely so a smaller, well-rated set is enough
— annotate or tune as many as you can judge confidently, then stop
before fatigue erodes consistency. The trainer requires at least 10
joinable labels; everything above that is about quality, not count.

When you're done, locate `mbxvam-labels.json` (and optionally
`mbxtune-pairs.json`) in MBXHub's AppData folder — those are the
inputs to step 4. A standalone annotator may land in `vam-tools/`
later; until then, MBXHub is the annotation surface.

### Step 4 — train your baseline model

**Use the enriched export as `--moods`, not raw `mbxmoods.json`.** Fetch it
from a running MBXHub:

```
curl http://<mbxhub-host>:8080/vam/train/moods -o train-moods.json
```

```
python calibrate-valence-arousal.py \
    --moods  train-moods.json \
    --labels path/to/mbxvam-labels.json \
    --pairs  path/to/mbxtune-pairs.json     # optional — pairs from /pages/tune.html, used to further define + tune the model
    --out    path/to/my-baseline-model-v1.json \
    [--alpha-v 1.0] [--alpha-a 10.0] [--pca-k 6]
```

Two of the 24 input features — `smfmValence` and `smfmArousal` — are the
plugin's projection of Sony SMFM data into V/A space, and they exist **only in
the `/vam/train/moods` export**. Raw `mbxmoods.json` has no such keys. Training
against the raw file still *works* (those columns mean-impute flat and Ridge
zeroes their weight) but it silently discards the strongest valence signal you
have, and the resulting model file looks perfectly valid. On a real 180-anchor
set the difference measured **valence LOO-CV r = 0.86 with the SMFM columns vs
0.32 without** — the script prints the latter every run as the `[essentia-only]`
line, so you can always see what you'd be giving up.

Joins labels to feature vectors by `audioStreamSha256` with a path fallback,
fits a StandardScaler + Ridge regression for both valence and arousal (raw
standardised features on both axes). Reports leave-one-out cross-validation
RMSE and Pearson r per axis. Optionally blends a Bradley-Terry-derived pairwise
comparison file (`--pairs`) as additional training rows; the blend wins only if
it improves LOO-CV r, so passing it can't make the model worse. `--pca-k` is
still accepted but currently unused on the valence path.

The 24 features used as inputs are listed in `FEATURES` at the top of
the script. Adding/removing features means regenerating models.

The trainer also bakes three blocks the plugin's `MoodModelLoader` reads at
serve time — a model file without them loads but misbehaves:

- **`impute`** — column means for the mean-imputed features. The loader
  substitutes the *same* numbers at serve time (train/serve parity).
- **`center` / `stretch`** per axis — a monotonic output spread-stretch. A Ridge
  fit regresses to the mean, so raw output is compressed to roughly half the
  human-rated spread; without this the model piles every track up near 0.5.
  Ranking is unchanged.
- **`essentiaOnly`** — a second head trained without the two SMFM columns,
  served for tracks that have no SMFM so a sparse-SMFM library degrades
  gracefully instead of collapsing to centre.

Output: `my-baseline-model-v1.json` — drop into MBXHub. Default path
is `%AppData%\MBXHub\mood-model.json`, which supersedes the model
embedded in `mb_MBXHub.dll` when present (defer to the MBXHub README
for the canonical / current path). Pick it up live with
`POST /vam/model/reload`, or it loads at the next plugin startup.

### Re-training later

When your library grows or your taste shifts:

1. Re-run truedat to refresh `mbxmoods.json` (cache means only new/changed
   tracks pay full Essentia cost).
2. Either re-run `seed-autocal-from-extremes.py` for fresh candidates,
   or add more annotations directly to your existing `my-baseline-labels-vN.json`.
3. Re-fetch the enriched export (`GET /vam/train/moods -o train-moods.json`) so
   it reflects the refreshed library, then re-run `calibrate-valence-arousal.py`
   with the updated `--moods` + labels and write to a new versioned output
   (`my-baseline-model-v2.json`, etc.).
4. Watch `loocv_r` (Pearson r) in the model JSON — higher is better.
   If a new label set makes LOO-CV r worse, your new labels disagree
   with the old ones; reconcile before deploying.

### Step 5 (sidecar) — Sony SensMe extraction

`extract-sony-sensme.py` is an unrelated single-purpose tool: it walks a
local Sony Music Center installation, decodes the binary `smfmf.bin`
sidecar files (10 raw STMO slot scores + BPM), and joins them to
`mbxmoods.json` by filename. Output is a separate JSON useful as
auxiliary labeled training data.

Note: truedat itself now ships a C# Sony SMFM (12-TONE) reader wired into
the scan path (commit `6283ce6` and follow-ups), which populates the
`smfm*` fields (`smfmScores` / `smfmChannel` / `smfmBpm`) directly into
`mbxmoods.json` per-track during a normal scan. (The per-slot channel
*names* were device-refuted on 2026-06-27 and are no longer emitted —
slots are raw STMO scores, not mood channels.) The Python script remains
useful for one-shot bulk extraction outside the scan pipeline.

The SMFM wire format, the full state of knowledge (including falsified
claims), and an SMFM-vs-Essentia comparison are documented in
[`../smfm-tools/`](../smfm-tools/), which also ships broader extraction
tooling (FLAC/MP3/WMA carriers, folder scan, dump/diff).

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
  "Valence": 0.0..1.0,
  "Arousal": 0.0..1.0,
  "Confidence": "sure" | "unsure",
  "IsAnchor": true | false,
  "RatedAt": "<ISO-8601>",
  "SessionId": "<opaque>",
  "Retest": [ ... ]
}
```

`Valence` and `Arousal` are **0..1**, not -1..+1 — 0.5 is neutral on each axis.
(The annotate page's plot is drawn with a centre origin, which reads as a
-1..+1 space; the persisted scalars are not.)

The trainer joins on `Key` (`audioStreamSha256`) **first**, falling back to
`Path` only when the Key doesn't resolve. Prefer the Key: it's box-independent
audio-bytes identity, so labels rated on one machine still join against a corpus
built on another. `Path` joins break the moment the library is mounted at a
different root, and the `/vam/train/moods` export isn't path-keyed at all. The
remaining fields are bookkeeping the trainer ignores.

## Model file format

`calibrate-valence-arousal.py` writes:

```
{
  "version": 3,
  "trainedAt": "<ISO-8601>",
  "anchorsUsed": N,
  "features": [ ... 24 feature names ... ],
  "impute": { "smfmValence": float, "smfmArousal": float, "modeMajor": float },
  "scaler": { "mean": [...], "std": [...] },
  "valence": {
    "transform": "raw",
    "coef": [...], "intercept": float,
    "center": float, "stretch": float,
    "alpha": float, "loocv_rmse": float, "loocv_r": float
  },
  "arousal": {
    "transform": "raw",
    "coef": [...], "intercept": float,
    "center": float, "stretch": float,
    "alpha": float, "loocv_rmse": float, "loocv_r": float
  },
  "essentiaOnly": {
    "features": [ ... 22 names — FEATURES minus the 2 SMFM columns ... ],
    "scaler": { ... }, "impute": { ... },
    "valence": { ... }, "arousal": { ... }
  },
  "pairEval": { ... }        // only when --pairs was supplied
}
```

Valence used to ride a PCA(K=6) latent bottleneck; as of 2026-06-16 the
trainer fits raw-Ridge on both axes (the PCA path degraded LOO-CV vs raw
on a 386-anchor bake-off). MBXHub's MoodModelLoader handles either
transform — older `transform=pca` model files still load.

MBXHub consumes this at the path documented in the MBXHub README.
