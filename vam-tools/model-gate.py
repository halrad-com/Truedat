#!/usr/bin/env python3
"""model-gate.py — enforce "the next build must be better than the current build".

Scores an INCUMBENT model and a CANDIDATE model on the SAME labelled set and
fails (exit 1) unless the candidate is at least as good as the incumbent on
BOTH axes. This is the regression gate for a model bake: run it before copying a
new model into your MBXHub model path (default %AppData%\MBXHub\mood-model.json).

Why not just compare loocv_r from the model files: each model's loocv_r is
cross-validated on ITS OWN anchor set (146 vs 180 vs ...). Those are different,
differently-sized eval sets, so the numbers aren't comparable — a bigger/harder
label set can post a lower loocv_r while being a strictly better model. The only
fair comparison is both models scored on one fixed set, which is what this does.

Pure stdlib (no numpy/sklearn) so it runs anywhere, including a minimal CI shell.

  python model-gate.py \
      --incumbent path/to/current/mood-model.json \
      --candidate out/candidate.json \
      --moods train-moods.json \
      --labels <DATA>/mbxvam-labels.json \
      [--tol 0.0] [--eval-out out/gate-report.json]

--moods is the /vam/train/moods export (SHA-keyed, carries smfmValence/
smfmArousal/modeMajor). Raw mbxmoods.json lacks those keys and will understate
every model equally — still a valid relative comparison, but prefer the export.

--tol is the slack allowed on the incumbent (default 0.0 = candidate must be
>= incumbent exactly, no regression). A small positive tol (e.g. 0.005) treats
run-to-run noise as a tie instead of a fail.
"""
import argparse, io, json, math, sys


def load(path):
    with io.open(path, "r", encoding="utf-8-sig") as f:
        return json.load(f)


def build_resolver(moods):
    """Index the corpus by audioStreamSha256 AND path so a label resolves from
    either. The export is SHA-keyed; raw mbxmoods.json is path-keyed."""
    by_sha, by_path = {}, {}
    for key, e in moods.items():
        k = e.get("audioStreamSha256")
        if k:
            by_sha[k] = e
        p = e.get("path") or key
        if p:
            by_path[p] = e

    def resolve(*ids):
        for i in ids:
            if not i:
                continue
            e = by_sha.get(i) or by_path.get(i)
            if e is not None:
                return e
        return None

    return resolve


def feature_value(entry, fk, impute):
    v = entry.get(fk)
    if v is None and fk == "modeMajor":
        m = entry.get("mode")
        if isinstance(m, str):
            ml = m.strip().lower()
            if ml == "major":
                return 1.0
            if ml == "minor":
                return 0.0
        v = None
    if v is None:
        return impute.get(fk, 0.0)
    try:
        return float(v)
    except (TypeError, ValueError):
        return impute.get(fk, 0.0)


def predict(model, entry):
    """Reproduce MoodModelLoader.PredictAxis: standardise, dot with coef, add
    intercept, apply the center/stretch spread, clamp to [0,1]."""
    feats = model["features"]
    sc = model["scaler"]
    imp = model.get("impute", {})
    x = []
    for i, fk in enumerate(feats):
        raw = feature_value(entry, fk, imp)
        std = sc["std"][i] or 1.0
        x.append((raw - sc["mean"][i]) / std)
    out = {}
    for axis in ("valence", "arousal"):
        a = model[axis]
        val = a["intercept"] + sum(c * xi for c, xi in zip(a["coef"], x))
        if "center" in a and "stretch" in a:
            val = a["center"] + (val - a["center"]) * a["stretch"]
        out[axis] = min(1.0, max(0.0, val))
    return out


def pearson(xs, ys):
    n = len(xs)
    if n == 0:
        return 0.0
    mx = sum(xs) / n
    my = sum(ys) / n
    num = sum((a - mx) * (b - my) for a, b in zip(xs, ys))
    dx = math.sqrt(sum((a - mx) ** 2 for a in xs))
    dy = math.sqrt(sum((b - my) ** 2 for b in ys))
    return num / (dx * dy) if dx * dy else 0.0


def rmse(xs, ys):
    return math.sqrt(sum((a - b) ** 2 for a, b in zip(xs, ys)) / len(xs)) if xs else 0.0


def score(model, labels, resolve):
    pv, pa, tv, ta = [], [], [], []
    for lbl in labels:
        v, a = lbl.get("Valence"), lbl.get("Arousal")
        if v is None or a is None:
            continue
        e = resolve(lbl.get("Key"), lbl.get("Path"))
        if e is None:
            continue
        pr = predict(model, e)
        pv.append(pr["valence"]); tv.append(float(v))
        pa.append(pr["arousal"]); ta.append(float(a))
    return {
        "n": len(pv),
        "valence_r": pearson(pv, tv), "valence_rmse": rmse(pv, tv),
        "arousal_r": pearson(pa, ta), "arousal_rmse": rmse(pa, ta),
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--incumbent", required=True, help="currently-shipped model json")
    ap.add_argument("--candidate", required=True, help="new model json to gate")
    ap.add_argument("--moods", required=True, help="/vam/train/moods export (SHA-keyed)")
    ap.add_argument("--labels", required=True, help="mbxvam-labels.json (the fixed eval set)")
    ap.add_argument("--tol", type=float, default=0.0, help="slack on incumbent r (default 0.0)")
    ap.add_argument("--eval-out", default=None, help="optional: write the gate report json here")
    args = ap.parse_args()

    moods = load(args.moods)
    moods = moods.get("tracks", moods) if isinstance(moods, dict) else moods
    labels = load(args.labels)
    if isinstance(labels, dict):
        labels = labels.get("labels", [])
    resolve = build_resolver(moods)

    inc = score(load(args.incumbent), labels, resolve)
    cand = score(load(args.candidate), labels, resolve)

    def row(name, s):
        return "%-12s n=%3d  VAL r=%+.3f rmse=%.3f   ARO r=%+.3f rmse=%.3f" % (
            name, s["n"], s["valence_r"], s["valence_rmse"], s["arousal_r"], s["arousal_rmse"])

    print("Gate - both models scored on the same %d labels:" % cand["n"], file=sys.stderr)
    print("  " + row("incumbent", inc), file=sys.stderr)
    print("  " + row("candidate", cand), file=sys.stderr)
    dv = cand["valence_r"] - inc["valence_r"]
    da = cand["arousal_r"] - inc["arousal_r"]
    print("  delta        VAL r=%+.3f   ARO r=%+.3f  (tol=%.3f)" % (dv, da, args.tol), file=sys.stderr)

    val_ok = cand["valence_r"] >= inc["valence_r"] - args.tol
    aro_ok = cand["arousal_r"] >= inc["arousal_r"] - args.tol
    passed = val_ok and aro_ok

    report = {
        "passed": passed,
        "tol": args.tol,
        "incumbent": inc,
        "candidate": cand,
        "delta": {"valence_r": dv, "arousal_r": da},
        "axes": {"valence": "PASS" if val_ok else "FAIL", "arousal": "PASS" if aro_ok else "FAIL"},
    }
    if args.eval_out:
        with open(args.eval_out, "w", encoding="utf-8") as f:
            json.dump(report, f, indent=2)

    print("GATE: valence %s  arousal %s  -> %s" % (
        report["axes"]["valence"], report["axes"]["arousal"],
        "PASS" if passed else "FAIL - candidate regresses; do NOT ship"), file=sys.stderr)
    sys.exit(0 if passed else 1)


if __name__ == "__main__":
    main()
