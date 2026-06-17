#!/usr/bin/env python3
from __future__ import annotations
import argparse, datetime, io, json, os, sys

FEATURES = [
    "spectralCentroid", "spectralFlux", "spectralFlatness", "danceability",
    "dissonance", "pitchSalience", "chordsChangesRate", "spectralSkewness",
    "spectralEntropy", "spectralComplexity", "hpcpCrest", "hpcpEntropy",
    "hfc", "beatsLoudness", "chordsStrength", "bpm", "loudness",
    "onsetRate", "zeroCrossingRate", "spectralRms", "dynamicRange",
]

def load_json(path):
    with io.open(path, "r", encoding="utf-8-sig") as f:
        return json.load(f)

def extract_feature_vector(entry):
    vec = []
    for fk in FEATURES:
        v = entry.get(fk)
        if v is None:
            vec.append(0.0); continue
        try:
            vec.append(float(v))
        except (TypeError, ValueError):
            return None
    return vec

def join_labels(labels, moods):
    Xs, yV, yA = [], [], []
    for lbl in labels:
        p = lbl.get("Path")
        if not p or p not in moods: continue
        tv = lbl.get("Valence"); ta = lbl.get("Arousal")
        if tv is None or ta is None: continue
        v = extract_feature_vector(moods[p])
        if v is None: continue
        Xs.append(v); yV.append(float(tv)); yA.append(float(ta))
    return Xs, yV, yA

def loo_cv(X, y, alpha):
    import numpy as np
    from sklearn.linear_model import Ridge
    from sklearn.model_selection import LeaveOneOut
    from scipy.stats import pearsonr
    X = np.asarray(X, dtype=float); y = np.asarray(y, dtype=float)
    preds = np.zeros_like(y)
    for tr, te in LeaveOneOut().split(X):
        m = Ridge(alpha=alpha); m.fit(X[tr], y[tr]); preds[te] = m.predict(X[te])
    rmse = float(np.sqrt(np.mean((preds - y) ** 2)))
    r = float(pearsonr(preds, y)[0]) if y.std() > 1e-9 else 0.0
    return rmse, r

def pair_concordance(pairs, lookup, axis):
    n_t = 0; n_c = 0
    for pp in pairs:
        if pp.get("Axis") != axis: continue
        ya = lookup.get(pp.get("A")); yb = lookup.get(pp.get("B"))
        if ya is None or yb is None: continue
        vd = pp.get("Verdict")
        if vd == "a":
            n_c += int(ya > yb)
        elif vd == "b":
            n_c += int(yb > ya)
        else:
            continue
        n_t += 1
    return n_c, n_t

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--moods", required=True)
    ap.add_argument("--labels", required=True)
    ap.add_argument("--pairs", default=None)
    ap.add_argument("--out", required=True)
    ap.add_argument("--alpha-v", type=float, default=1.0)
    ap.add_argument("--alpha-a", type=float, default=10.0)
    ap.add_argument("--pca-k", type=int, default=6)
    args = ap.parse_args()

    import numpy as np
    from sklearn.linear_model import Ridge
    from sklearn.preprocessing import StandardScaler
    from sklearn.decomposition import PCA

    print("loading moods from", args.moods, file=sys.stderr)
    moods = load_json(args.moods).get("tracks", {})
    print(" ", len(moods), "feature entries", file=sys.stderr)

    print("loading labels from", args.labels, file=sys.stderr)
    labels = load_json(args.labels)
    if isinstance(labels, dict): labels = labels.get("labels", [])
    print(" ", len(labels), "labels", file=sys.stderr)

    X, yV, yA = join_labels(labels, moods)
    if len(X) < 10:
        print("FATAL: only", len(X), "anchors", file=sys.stderr); sys.exit(2)
    print("  joined", len(X), "of", len(labels), file=sys.stderr)
    X = np.asarray(X); yV = np.asarray(yV); yA = np.asarray(yA)

    print("fitting StandardScaler + PCA on full mood cache", file=sys.stderr)
    full_X = [v for v in (extract_feature_vector(e) for e in moods.values()) if v is not None]
    full_X = np.asarray(full_X)
    scaler = StandardScaler().fit(full_X)
    full_Xs = scaler.transform(full_X)
    pca = PCA(n_components=args.pca_k).fit(full_Xs)
    print("  scaler on", len(full_X), "rows; PCA var=" + format(pca.explained_variance_ratio_.sum()*100, ".1f") + "%", file=sys.stderr)

    Xs_l = scaler.transform(X)
    Zs_l = pca.transform(Xs_l)

    # Raw-Ridge valence (skips the PCA-K=6 bottleneck; match arousal path).
    # 2026-06-16: bake-off showed LOO-CV r 0.358 (PCA+Ridge) → 0.404 (raw) on
    # 386 anchors. GBR / RF tested too — both underperform Ridge at this N.
    rmse_v, r_v = loo_cv(Xs_l, yV, args.alpha_v)
    print("  valence LOO-CV (raw Ridge): RMSE=" + format(rmse_v, ".3f") + " r=" + format(r_v, "+.3f"), file=sys.stderr)
    mv = Ridge(alpha=args.alpha_v); mv.fit(Xs_l, yV)

    rmse_a, r_a = loo_cv(Xs_l, yA, args.alpha_a)
    print("  arousal LOO-CV (raw Ridge): RMSE=" + format(rmse_a, ".3f") + " r=" + format(r_a, "+.3f"), file=sys.stderr)
    ma = Ridge(alpha=args.alpha_a); ma.fit(Xs_l, yA)

    # Bradley-Terry score derivation (matches MBXH/Core/TunePairStore.cs:ComputeScores).
    # Returns dict url -> z-normalised log-score for tracks in the pair set on
    # the given axis. Used to convert ordinal pair signal into pseudo-scalar
    # labels we can blend into the regression as additional point targets.
    def bt_scores(pairs_axis):
        from collections import defaultdict
        wins = defaultdict(float); counts = defaultdict(int); matchups = defaultdict(list)
        for p in pairs_axis:
            verdict = p.get("Verdict")
            if verdict == "skip" or not p.get("A") or not p.get("B"): continue
            wA = 1.0 if verdict == "a" else (0.5 if verdict == "same" else 0.0)
            wins[p["A"]] += wA; counts[p["A"]] += 1; matchups[p["A"]].append(p["B"])
            wins[p["B"]] += (1.0 - wA); counts[p["B"]] += 1; matchups[p["B"]].append(p["A"])
        scores = {u: 1.0 for u in wins}
        for it in range(200):
            max_delta = 0.0
            new_s = dict(scores)
            for u in list(scores):
                w = wins[u]
                if w <= 0:
                    new_s[u] = scores[u] * 0.5; continue
                denom = sum(1.0 / (scores[u] + scores[opp]) for opp in matchups[u])
                if denom > 0:
                    upd = w / denom
                    max_delta = max(max_delta, abs(upd - scores[u]))
                    new_s[u] = upd
            total = sum(new_s.values())
            if total > 0:
                factor = len(new_s) / total
                for k in new_s: new_s[k] *= factor
            scores = new_s
            if max_delta < 1e-6: break
        import math
        logs = {u: math.log(max(1e-9, s)) for u, s in scores.items()}
        if not logs: return {}
        m = sum(logs.values()) / len(logs)
        sd = (sum((v - m) ** 2 for v in logs.values()) / len(logs)) ** 0.5 or 1.0
        return {u: (v - m) / sd for u, v in logs.items()}

    pair_info = None
    if args.pairs and os.path.exists(args.pairs):
        pairs = load_json(args.pairs)
        if isinstance(pairs, list):
            relevant = set()
            for pp in pairs:
                if pp.get("A"): relevant.add(pp["A"])
                if pp.get("B"): relevant.add(pp["B"])
            preds_v = {}; preds_a = {}
            for path in relevant:
                e = moods.get(path)
                if e is None: continue
                v = extract_feature_vector(e)
                if v is None: continue
                x = scaler.transform([v])[0]
                # Both V and A use raw standardized features now (no PCA).
                preds_v[path] = float(mv.predict([x])[0])
                preds_a[path] = float(ma.predict([x])[0])
            v_c, v_t = pair_concordance(pairs, preds_v, "valence")
            a_c, a_t = pair_concordance(pairs, preds_a, "arousal")
            v_pct = (v_c / v_t * 100) if v_t else 0
            a_pct = (a_c / a_t * 100) if a_t else 0
            pair_info = {
                "valence_concordance": (v_c / v_t) if v_t else None,
                "valence_pairs": v_t,
                "arousal_concordance": (a_c / a_t) if a_t else None,
                "arousal_pairs": a_t,
            }
            print("  valence pair concordance:", v_c, "/", v_t, "=", format(v_pct, ".1f") + "%", file=sys.stderr)
            print("  arousal pair concordance:", a_c, "/", a_t, "=", format(a_pct, ".1f") + "%", file=sys.stderr)

            # Blend Bradley-Terry derived labels as extra training rows.
            import math as _m
            def _sig(z): return 1.0 / (1.0 + _m.exp(-0.5 * z))
            v_bt = bt_scores([p for p in pairs if p.get("Axis") == "valence"])
            a_bt = bt_scores([p for p in pairs if p.get("Axis") == "arousal"])
            X_v_extra = []; y_v_extra = []
            X_a_extra = []; y_a_extra = []
            for url, z in v_bt.items():
                e = moods.get(url)
                if e is None: continue
                fv = extract_feature_vector(e)
                if fv is None: continue
                X_v_extra.append(fv); y_v_extra.append(_sig(z))
            for url, z in a_bt.items():
                e = moods.get(url)
                if e is None: continue
                fv = extract_feature_vector(e)
                if fv is None: continue
                X_a_extra.append(fv); y_a_extra.append(_sig(z))

            if X_v_extra:
                X_v_all = np.vstack([X, np.array(X_v_extra)])
                y_v_all = np.concatenate([yV, np.array(y_v_extra)])
                Xs_v_all = scaler.transform(X_v_all)
                # Raw-Ridge (no PCA) to match the main valence path above.
                rmse_v2, r_v2 = loo_cv(Xs_v_all, y_v_all, args.alpha_v)
                mv2 = Ridge(alpha=args.alpha_v); mv2.fit(Xs_v_all, y_v_all)
                print("  valence LOO-CV (anchors + BT, n=" + str(len(y_v_all)) + "): RMSE=" + format(rmse_v2, ".3f") + " r=" + format(r_v2, "+.3f"), file=sys.stderr)
                if r_v2 > r_v:
                    print("  -> BLENDED valence wins by " + format(r_v2 - r_v, "+.3f") + " r; switching", file=sys.stderr)
                    mv = mv2; rmse_v = rmse_v2; r_v = r_v2
                else:
                    print("  -> blended valence not better; keeping anchors-only", file=sys.stderr)

            if X_a_extra:
                X_a_all = np.vstack([X, np.array(X_a_extra)])
                y_a_all = np.concatenate([yA, np.array(y_a_extra)])
                Xs_a_all = scaler.transform(X_a_all)
                rmse_a2, r_a2 = loo_cv(Xs_a_all, y_a_all, args.alpha_a)
                ma2 = Ridge(alpha=args.alpha_a); ma2.fit(Xs_a_all, y_a_all)
                print("  arousal LOO-CV (anchors + BT, n=" + str(len(y_a_all)) + "): RMSE=" + format(rmse_a2, ".3f") + " r=" + format(r_a2, "+.3f"), file=sys.stderr)
                if r_a2 > r_a:
                    print("  -> BLENDED arousal wins by " + format(r_a2 - r_a, "+.3f") + " r; switching", file=sys.stderr)
                    ma = ma2; rmse_a = rmse_a2; r_a = r_a2
                else:
                    print("  -> blended arousal not better; keeping anchors-only", file=sys.stderr)

    model = {
        "version": 3,
        "trainedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z"),
        "anchorsUsed": len(X),
        "features": FEATURES,
        "scaler": {"mean": scaler.mean_.tolist(), "std": scaler.scale_.tolist()},
        "valence": {
            "transform": "raw",
            "coef": mv.coef_.tolist(),
            "intercept": float(mv.intercept_),
            "alpha": args.alpha_v,
            "loocv_rmse": rmse_v, "loocv_r": r_v,
        },
        "arousal": {
            "transform": "raw",
            "coef": ma.coef_.tolist(),
            "intercept": float(ma.intercept_),
            "alpha": args.alpha_a,
            "loocv_rmse": rmse_a, "loocv_r": r_a,
        },
    }
    if pair_info: model["pairEval"] = pair_info

    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(model, f, indent=2)
    print("wrote", args.out, file=sys.stderr)

if __name__ == "__main__":
    main()
