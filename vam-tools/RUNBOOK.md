# AutoCal — Retraining Runbook

**This guide is for you if you already have an `mbxmoods.json` for your catalog
and want to retrain how the engine uses it.**

It's an advanced guide — for people working *under the covers* of the V/A
(valence/arousal) model, not people using MBXHub. If you just want AutoQ to
play music, you're done already: it ships with a baseline model that works out
of the box, and nothing here is required. Come back when you've listened to
AutoQ on your library and decided its sense of "happy" or "intense" doesn't
match yours — that's the honest trigger for retraining.

It's a command-by-command path to a deployed, verified model trained on your own
library and your own ears. The [`README.md`](README.md) is the reference behind
it — what each file is, every flag, the model format. Read this to *do it*; read
the README to *understand it*.

A full library scan and a running MBXHub are assumed — in the normal case you
already have both (that's how you noticed retraining was warranted). The
Prerequisites and step 1 spell out setup anyway, so a genuine from-scratch
environment is also complete; skim past them if your library is already scanned.

Throughout, substitute your own values for these placeholders:

| Placeholder      | Meaning                                                            |
| ---------------- | ------------------------------------------------------------------ |
| `<HUB>`          | host:port of a running MBXHub, e.g. what you type into the browser |
| `<MOODS>`        | path to your library's `mbxmoods.json` (truedat's output)          |
| `<DATA>`         | MBXHub's data folder (holds `mbxvam-labels.json`); see step 5      |

---

## Prerequisites

Have these in place before step 0. Most of them you already have if you use
MBXHub day-to-day — the only piece unique to training is Python.

| You need | Why | How to confirm |
| --- | --- | --- |
| **A music library in MusicBee** | It's what you'll scan, annotate, and hear the result on. | You're already using it. |
| **MBXHub installed and running** | It hosts the annotate/tune pages, writes the label files, and produces the training export. The trainer can't run without an export from a live hub. | `curl http://<HUB>/vam/diag` returns JSON. |
| **AutoQ in use / a baseline model loaded** | Retraining *replaces* a model; you retrain because the current one doesn't fit your taste. That judgement comes from hearing AutoQ run first. | `/vam/diag` shows a `model` block. |
| **`truedat.exe`** (built, or from a release) | Produces `mbxmoods.json`, the feature cache the whole pipeline reads. | `truedat.exe --help` runs. |
| **A scanned library — `mbxmoods.json` exists** | Everything downstream reads it. In practice you already have this: MBXHub's AutoQ consumes it, so if AutoQ works, the scan has run. | The file exists and is tens of MB+. Step 1 covers scanning if not. |
| **Python 3.12+** | The trainer (`calibrate-valence-arousal.py`) is Python. `truedat.exe` itself is Python-free — this is opt-in tooling. | `python --version`. Step 0 sets up the venv. |
| **Network reach to `<HUB>`** | You fetch the training export over HTTP (step 4) and reload the model over HTTP (step 5). | Same `curl` as above. |

**Why the ordering — "scan, then decide you need to retrain."** The honest
trigger for this whole runbook is listening to AutoQ on your library and finding
it doesn't match your taste. That presupposes a scanned library and a running
hub. So in the normal case steps "Prerequisites" and step 1 are already
satisfied by the time you're reading this; they're spelled out so a genuine
from-nothing setup is also complete.

---

## The shape of it

```
0. install deps         (one-time)
1. scan library    ──►  mbxmoods.json          (truedat.exe)
2. seed corners    ──►  auto-cal-seed.json      (seed-autocal-from-extremes.py)
   picks → playlist ──► auto-cal.m3u            (picks-to-m3u.py)
3. annotate        ──►  mbxvam-labels.json      (MBXHub /pages/annotate.html)
   (optional pairs) ──► mbxtune-pairs.json      (MBXHub /pages/tune.html)
4. fetch export    ──►  train-moods.json        (GET /vam/train/moods)
   train           ──►  my-model-v1.json        (calibrate-valence-arousal.py)
5. deploy          ──►  mood-model.json         (copy + POST /vam/model/reload)
6. verify                                       (listen; check loocv_r)
```

Five gotchas this runbook exists to keep you out of — each has cost someone a
wasted retrain:

1. **Train against `train-moods.json`, not `mbxmoods.json`.** The two SMFM
   features that carry valence performance exist only in the export.
2. **Labels are 0..1, not -1..+1.** 0.5 is neutral on each axis.
3. **The annotate/tune files live on the box running MBXHub**, not necessarily
   the box you're sitting at. Ask the running process where they are (step 5).
4. **Drive corner coverage, not the middle.** A linear model's accuracy is set
   by the extremes.
5. **A model missing its baked blocks loads but pins everything to ~0.5.** The
   trainer bakes them; don't hand-edit them out.

---

## Step 0 — install the trainer's dependencies (one-time)

The trainer is Python (numpy + scikit-learn + scipy). `truedat.exe` itself is
Python-free; this is opt-in tooling.

```
cd vam-tools
python -m venv .venv
.venv\Scripts\activate            # Windows
# or: source .venv/bin/activate    # *nix
pip install -r requirements.txt
python -c "import numpy, sklearn, scipy; print('deps ok', numpy.__version__)"
```

---

## Step 1 — scan your library with truedat

Produces `mbxmoods.json` — the Essentia feature cache the whole pipeline reads.
If you already run truedat for MBXHub, you have this; skip ahead.

```
truedat.exe                        # zero-arg: auto-discovers the iTunes XML, full scan
```

See the top-level README for other invocations. The scan is cache-aware — a
re-run only pays full Essentia cost on new/changed tracks.

**Checkpoint:** `<MOODS>` exists and is large (tens of MB for a real library).

---

## Step 2 — pick corner tracks to annotate

The model learns its slope from **extreme** tracks — loud-and-angry,
quiet-and-sad, and so on. Tracks near the middle teach it almost nothing. This
script scores every track on arousal/valence proxies and hands you the most
extreme, quadrant-balanced picks.

```
python seed-autocal-from-extremes.py --moods <MOODS> --per-quadrant 25 --out auto-cal-seed.json
python picks-to-m3u.py --picks auto-cal-seed.json --out auto-cal.m3u
```

Load `auto-cal.m3u` in MusicBee. The annotate page drives playback through your
player, so a queued playlist is the practical hand-off.

**The playlist is a starter, not a contract.** Add tracks you specifically want
labelled, drop ones you have no opinion on, swap freely. What gets annotated is
whatever ends up in the playlist you bring to the annotate page.

---

## Step 3 — annotate (and optionally tune)

Open MBXHub in a browser. You never build a file by hand here — both pages
persist automatically.

**`<HUB>/pages/annotate.html`** — score valence (0..1) and arousal (0..1) per
track. **0.5 is neutral.** Accumulates to `mbxvam-labels.json`. This is the
trainer's primary input.

**`<HUB>/pages/tune.html`** (optional) — pairwise A/B: "which is more positive?"
/ "more intense?" Accumulates to `mbxtune-pairs.json`. The trainer blends these
as extra rows **only if they improve cross-validation**, so supplying them can
never make the model worse.

### What actually matters while annotating

- **Corner coverage beats volume.** Aim for ~10–12 anchors in each of the four
  true corners (very-high/very-low on each axis). If you have an "AutoQ ↔ SMFM
  disagreement" targeting toggle, it over-samples the ambiguous *middle* — turn
  it **off** while building the backbone; it's for hunting model errors later,
  not for training a linear model.
- **Consistency beats count.** Rate similar tracks similarly; don't drift
  mid-session. Stop before fatigue erodes this.
- **A smaller, well-placed, consistent set wins.** The trainer needs ≥10
  joinable labels; everything above that is quality, not quantity. A few dozen
  well-spread anchors already trains a good model.

**Checkpoint:** the annotate page's counter shows your anchor count climbing,
and its self-consistency figure (from re-rates) stays high (>0.9).

---

## Step 4 — fetch the enriched export, then train

**This is the step with the sharpest edge. Do not train against `mbxmoods.json`.**

Two of the trainer's 24 input features — `smfmValence` and `smfmArousal` — are
MBXHub's projection of Sony SMFM data into V/A space, and they exist **only** in
the `/vam/train/moods` export. Raw `mbxmoods.json` has no such keys. Training
against the raw file still *runs* (those columns impute flat, Ridge zeroes their
weight) and produces a valid-looking model — but it throws away the strongest
valence signal you have. The gap is not subtle: on a real ~180-anchor set,
valence cross-validation measured **r ≈ 0.86 with the SMFM columns vs ≈ 0.32
without**.

Fetch the export from a running hub:

```
curl http://<HUB>/vam/train/moods -o train-moods.json
```

Then train (from `vam-tools/`, venv active):

```
python calibrate-valence-arousal.py \
    --moods  train-moods.json \
    --labels <DATA>/mbxvam-labels.json \
    --pairs  <DATA>/mbxtune-pairs.json \     # optional; safe to omit or include
    --out    my-model-v1.json
```

### Read the trainer's output — it tells you everything

```
  joined 180 of 180                                 <- want this near 100%
  valence LOO-CV (raw Ridge): RMSE=... r=+0.862      <- your headline number
  arousal LOO-CV (raw Ridge): RMSE=... r=+0.977
  valence pair concordance: 3 / 4 = 75.0%            <- pairs joined & scored
  [essentia-only] valence LOO-CV r=+0.324            <- what you'd get WITHOUT SMFM
```

- **`joined N of M`** — if this is well below your label count, your labels
  aren't matching the corpus. The trainer joins by `audioStreamSha256` first,
  path second; a large shortfall usually means a stale library. It should be
  near-total.
- **`loocv_r` (Pearson r)** per axis — higher is better; this is what you track
  across retrains.
- **`[essentia-only] ... r`** — the model's fallback head, trained without the
  SMFM columns (served for tracks that have no SMFM). Its valence r is roughly
  what an SMFM-free library gets. Seeing it far below the main number is the
  SMFM features earning their place.

**Checkpoint:** `joined` is near-total and both `loocv_r` values printed.

---

## Step 5 — deploy the model

**Gate it first — the new model must beat the one it replaces.** `loocv_r` alone
can't prove this: each model's `loocv_r` is cross-validated on its *own* anchor
set, so a bigger/harder label set can post a lower number while being a better
model. The only fair test scores both models on one fixed label set:

```
python model-gate.py \
    --incumbent path/to/current/mood-model.json \
    --candidate my-model-v2.json \
    --moods train-moods.json \
    --labels <DATA>/mbxvam-labels.json \
    --eval-out gate-report.json
```

Exit 0 = the candidate matches or beats the incumbent on **both** axes — safe to
promote. Exit 1 = it regresses on at least one axis; don't ship it. Keep
`gate-report.json` with the generation (see Archiving). Only on a pass do you
continue below.

The model file drops into MBXHub's data folder as `mood-model.json`, overriding
the model embedded in the plugin.

**Find the real data folder from the running process** — don't guess, and be
aware it's on whichever machine runs MBXHub, which may not be the one you're on:

```
curl http://<HUB>/vam/diag
```

The response reports the exact paths it reads for `mbxmoods`, `mbxvam-labels`,
and `mbxtune-pairs` — the labels path's directory is `<DATA>`, and
`mood-model.json` goes in the same folder. `/vam/diag` also shows the currently
active model and its LOO metrics, so you can confirm what's live before and after.

Copy the model in, then pick it up live (no restart needed):

```
copy my-model-v1.json <DATA>\mood-model.json
curl -X POST http://<HUB>/vam/model/reload
```

Or just restart MusicBee — the plugin loads it at startup.

**Checkpoint:** `curl http://<HUB>/vam/diag` now reports your `trainedAt` /
`anchorsUsed` / `loocv_r` as the active model.

---

## Step 6 — verify with your ears

The cross-validation number is a sanity check, not the goal. The goal is that
"give me something angry" returns something angry.

- Play a few stations or auto-queues you have strong expectations for.
- Spot-check the corners: does a track you'd call joyful read high-valence,
  high-arousal? A mournful one, low-valence, low-arousal?
- If everything feels compressed toward "medium" — every track landing near the
  middle — the model's spread-stretch blocks may be missing (see Troubleshooting).

If it's wrong in a specific, repeatable way, that's your next annotation target:
go back to step 3 and add corner anchors where the model disagrees with you,
then retrain to `my-model-v2.json`.

---

## Retraining later

When your library grows or your taste shifts:

1. Re-run truedat to refresh `mbxmoods.json` (cache keeps it cheap).
2. Add more annotations (step 3) — or re-seed corners (step 2) for fresh candidates.
3. **Re-fetch the export** (`curl http://<HUB>/vam/train/moods -o train-moods.json`)
   so it reflects the refreshed library, then retrain (step 4) to a new versioned
   output (`my-model-v2.json`, …).
4. Compare `loocv_r` against the prior version. **If a new label set makes r
   worse, your new labels disagree with your old ones** — reconcile before
   deploying, don't just ship the bigger set.

Versioning is your call; the trainer stamps `trainedAt` and `anchorsUsed` into
every model so you can always trace one back to a label set.

---

## Archiving each generation

Keep every model generation so a shipped model can always be traced back to the
data that produced it. One folder per generation, named `<version>_<anchors>_<MMDD>`
(e.g. `0.5.4.2_180_717` — the consuming app's version, 180 anchors, baked 7/17).
Preserve original filenames inside; don't rename, just group.

**Archive these — the human-made inputs and the model output (all small, unique):**

| Artifact | I/O | What it is / why keep it |
| --- | --- | --- |
| `mbxvam-labels*.json` | input | The annotations (valence/arousal 0..1 per track). The training target. Unique per generation — the whole point of the archive. |
| `mbxtune-pairs.json` | input | Pairwise A/B judgments. Supplemental signal. Small; keep if it existed for this gen. |
| `auto-cal-seed.json` | input | The seed corner picks that drove annotation. Records how the label set was sampled. Optional. |
| `<model>.json` | output | The trained model. Embeds its own scaler + impute means + coefficients, so the file carries the fitted state — no external files needed to serve or inspect it. |
| `gate-report.json` | output | `model-gate.py --eval-out` result: incumbent-vs-candidate scores that justified promoting this generation. Proof it cleared "better than the last build." Small; keep it. |

**Do NOT archive `mbxmoods.json` or the `/vam/train/moods` export.** They're
derived artifacts — regenerable any time by re-scanning the library and re-fetching
the export. Re-scanning reproduces the features to within rounding for a given
truedat/Essentia build (insignificant to a model whose coefficients are already
fixed); the only thing that shifts features beyond rounding is a different Essentia
version, so the provenance worth noting per generation is the **truedat/Essentia
build**, not the multi-hundred-MB file.

---

## Troubleshooting

| Symptom | Likely cause | Fix |
| --- | --- | --- |
| `FATAL: only N anchors` (N<10) | Labels didn't join to the corpus | You trained against the wrong `--moods`, or labels/library are from different machines. Use the `/vam/train/moods` export; check the `joined N of M` line. |
| Valence r much lower than expected (~0.3–0.4) | Trained without the SMFM features | You used raw `mbxmoods.json`. Re-fetch `/vam/train/moods` and retrain. The `[essentia-only]` line shows this exact number by design. |
| `pair concordance: 0 / 0` | Pairs didn't join | Corpus/pair identifier mismatch. Ensure you're on the current trainer and passing the real `mbxtune-pairs.json`. |
| Every track reads ~0.5 / "medium" after deploy | Model missing `center`/`stretch` (or a hand-edited/old-format file) | Retrain with the current trainer, which bakes them. Don't strip blocks from the model JSON. |
| Can't find `mbxvam-labels.json` | Looking on the wrong machine / folder | `curl http://<HUB>/vam/diag` reports the exact path the running hub uses. |

---

## What good looks like

- **Corners covered:** ~10+ anchors in each of the four extreme quadrants; the
  middle can be sparse.
- **`joined`** near-total in the trainer output.
- **`loocv_r`** you're happy with and can reproduce across runs (small run-to-run
  wobble is normal at a few hundred anchors — treat ±0.03 as noise).
- **The ear test passes:** corner requests return corner music.
