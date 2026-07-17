#!/usr/bin/env python3
from __future__ import annotations
import argparse, datetime, io, json, os, sys

FEATURES = [
    "spectralCentroid", "spectralFlux", "spectralFlatness", "danceability",
    "dissonance", "pitchSalience", "chordsChangesRate", "spectralSkewness",
    "spectralEntropy", "spectralComplexity", "hpcpCrest", "hpcpEntropy",
    "hfc", "beatsLoudness", "chordsStrength", "bpm", "loudness",
    "onsetRate", "zeroCrossingRate", "spectralRms", "dynamicRange",
    # v3: Sony SMFM (SensMe) projected V/A as low-weight fusion features (validated 2026-07-06 —
    # AutoQ stays the authority; the fit down-weights these). Present ONLY in the enriched
    # /vam/train/moods export (the plugin owns the SMFM->V/A projection; raw mbxmoods.json has
    # no such keys). Missing values are MEAN-IMPUTED: the column mean over present values is
    # stored in the model's "impute" block and MBXHub's MoodModelLoader substitutes the SAME
    # number at serve time — train/serve parity. A library with no SMFM at all still trains:
    # the columns impute flat and Ridge zeroes their weight (see the essentiaOnly head below).
    "smfmValence", "smfmArousal",
    # Mode-aware valence prior: major=1.0,
    # minor=0.0, unknown=mean-imputed. The export emits numeric modeMajor; raw mbxmoods.json
    # carries the string "mode" key, which extract_feature_vector derives from directly.
    "modeMajor",
]

# Features whose missing values are mean-imputed (all others are required-numeric;
# a track missing one of those is dropped exactly as before).
IMPUTED_FEATURES = ("smfmValence", "smfmArousal", "modeMajor")
# Fallbacks when a column has NO present values anywhere in the corpus:
# smfm columns keep the legacy 0.0; modeMajor centers at 0.5.
IMPUTE_DEFAULTS = {"smfmValence": 0.0, "smfmArousal": 0.0, "modeMajor": 0.5}

def load_json(path):
    with io.open(path, "r", encoding="utf-8-sig") as f:
        return json.load(f)

def raw_feature(entry, fk):
    """Feature value or None. Derives modeMajor from the string 'mode' key when the
    numeric key is absent (raw mbxmoods.json compatibility)."""
    v = entry.get(fk)
    if v is None and fk == "modeMajor":
        m = entry.get("mode")
        if isinstance(m, str):
            ml = m.strip().lower()
            if ml == "major": return 1.0
            if ml == "minor": return 0.0
        return None
    if v is None:
        return None
    try:
        return float(v)
    except (TypeError, ValueError):
        return "BAD"

def compute_impute_means(moods_values):
    """Column mean over PRESENT values for each imputed feature (IMPUTE_DEFAULTS
    when a column is empty corpus-wide)."""
    sums = {fk: 0.0 for fk in IMPUTED_FEATURES}
    counts = {fk: 0 for fk in IMPUTED_FEATURES}
    for e in moods_values:
        for fk in IMPUTED_FEATURES:
            v = raw_feature(e, fk)
            if v is None or v == "BAD":
                continue
            sums[fk] += v; counts[fk] += 1
    return {fk: (sums[fk] / counts[fk] if counts[fk] else IMPUTE_DEFAULTS[fk])
            for fk in IMPUTED_FEATURES}

def extract_feature_vector(entry, impute):
    vec = []
    for fk in FEATURES:
        v = raw_feature(entry, fk)
        if v == "BAD":
            return None  # unparseable numeric -> drop the track (matches pre-wave behavior)
        if v is None:
            if fk in impute:
                vec.append(impute[fk])
                continue
            # Missing non-imputed feature -> 0.0-fill, exact parity with serve:
            # MoodModelLoader.LookupFeatureNullable "?? 0.0"-fills these same nullable
            # extended-essentia features at serve time. Dropping the track here would
            # desync the retrain corpus/scaler from what serve actually scores.
            vec.append(0.0)
            continue
        vec.append(v)
    return vec

def build_lookup(moods):
    """Index the corpus by BOTH audioStreamSha256 and path, so a track resolves from
    either identifier regardless of how the corpus is keyed.

    Raw mbxmoods.json is keyed by track PATH, which is box-specific: labels rated against a
    library mounted at one drive/root won't match a corpus keyed at another. The
    /vam/train/moods export is keyed by SHA instead, so a bare moods.get(<path>) resolves
    nothing at all against it — silently, with no error. Every consumer of the corpus
    (labels AND tune pairs) must go through this index, not moods.get()."""
    by_sha, by_path = {}, {}
    for key, e in moods.items():
        k = e.get("audioStreamSha256")
        if k: by_sha[k] = e
        p = e.get("path") or key
        if p: by_path[p] = e
    def resolve(*ids):
        """First id that hits, tried against sha then path. Callers pass whatever they
        hold: labels have (Key, Path); tune pairs hold a bare path in A/B."""
        for i in ids:
            if not i: continue
            e = by_sha.get(i) or by_path.get(i)
            if e is not None: return e
        return None
    return resolve

def join_labels(labels, moods, impute, resolve):
    # Join by audioStreamSha256 (the label's Key) — box-independent audio-bytes identity —
    # and fall back to Path when the Key is stale (a re-scan/re-encode after rating changes
    # the sha). Path-only joins have been measured dropping a third of an anchor set.
    Xs, yV, yA = [], [], []
    for lbl in labels:
        tv = lbl.get("Valence"); ta = lbl.get("Arousal")
        if tv is None or ta is None: continue
        e = resolve(lbl.get("Key"), lbl.get("Path"))
        if e is None: continue
        v = extract_feature_vector(e, impute)
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

    impute = compute_impute_means(moods.values())
    print("  impute means:", {k: round(v, 4) for k, v in impute.items()}, file=sys.stderr)

    resolve = build_lookup(moods)
    X, yV, yA = join_labels(labels, moods, impute, resolve)
    if len(X) < 10:
        print("FATAL: only", len(X), "anchors", file=sys.stderr); sys.exit(2)
    print("  joined", len(X), "of", len(labels), file=sys.stderr)
    X = np.asarray(X); yV = np.asarray(yV); yA = np.asarray(yA)

    print("fitting StandardScaler + PCA on full mood cache", file=sys.stderr)
    full_X = [v for v in (extract_feature_vector(e, impute) for e in moods.values()) if v is not None]
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
                # Pairs hold a bare path in A/B; the corpus may be SHA-keyed (the
                # /vam/train/moods export) — resolve, never moods.get().
                e = resolve(path)
                if e is None: continue
                v = extract_feature_vector(e, impute)
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
                e = resolve(url)
                if e is None: continue
                fv = extract_feature_vector(e, impute)
                if fv is None: continue
                X_v_extra.append(fv); y_v_extra.append(_sig(z))
            for url, z in a_bt.items():
                e = resolve(url)
                if e is None: continue
                fv = extract_feature_vector(e, impute)
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

    # Output spread-stretch calibration. A weak Ridge fit regresses to the mean, so
    # raw output is compressed to ~half the human-rated spread and piles up near the
    # intercept (the "everything pinned to 0.5" symptom). Bake a per-axis stretch that
    # the loader (MoodModelLoader.PredictAxis) applies: out = clamp(center + (raw-center)*stretch).
    # center = mean raw output over the FULL cache (matches the served population);
    # stretch = human-target-std / model-output-std, floored at 1.0 (never compress) and
    # capped at 2.0 (avoid over-railing). Monotonic — ranking is unchanged.
    def axis_calib(model_fit, target_y):
        raw = model_fit.predict(full_Xs)
        pred = np.clip(raw, 0.0, 1.0)
        pstd = pred.std() if pred.std() > 1e-9 else 1.0
        center = float(np.mean(raw))
        stretch = float(min(2.0, max(1.0, target_y.std() / pstd)))
        return round(center, 4), round(stretch, 4)
    v_center, v_stretch = axis_calib(mv, yV)
    a_center, a_stretch = axis_calib(ma, yA)
    print("  valence stretch: center=" + format(v_center, ".4f") + " stretch=" + format(v_stretch, ".3f"), file=sys.stderr)
    print("  arousal stretch: center=" + format(a_center, ".4f") + " stretch=" + format(a_stretch, ".3f"), file=sys.stderr)

    # --- Essentia-only fallback head -------------------------------------
    # Same anchors, drop the 2 SMFM columns so Ridge restores loudness/onset/
    # bpm weights (no SMFM shrinkage). Served for tracks with no SMFM so a
    # low-SMFM library degrades gracefully instead of collapsing to center.
    ESSENTIA_FEATURES = [f for f in FEATURES if f not in ("smfmValence", "smfmArousal")]
    ess_idx = [FEATURES.index(f) for f in ESSENTIA_FEATURES]
    full_X_ess = full_X[:, ess_idx]
    scaler_ess = StandardScaler().fit(full_X_ess)
    full_Xs_ess = scaler_ess.transform(full_X_ess)
    Xs_ess = scaler_ess.transform(X[:, ess_idx])

    rmse_v_e, r_v_e = loo_cv(Xs_ess, yV, args.alpha_v)
    rmse_a_e, r_a_e = loo_cv(Xs_ess, yA, args.alpha_a)
    print("  [essentia-only] valence LOO-CV r=" + format(r_v_e, "+.3f") +
          "  arousal LOO-CV r=" + format(r_a_e, "+.3f"), file=sys.stderr)
    mv_e = Ridge(alpha=args.alpha_v); mv_e.fit(Xs_ess, yV)
    ma_e = Ridge(alpha=args.alpha_a); ma_e.fit(Xs_ess, yA)

    def axis_calib_ess(m, target_y):
        raw = m.predict(full_Xs_ess)
        pstd = raw.std() or 1e-9
        return round(float(np.mean(raw)), 4), round(float(min(2.0, max(1.0, target_y.std() / pstd))), 4)
    v_center_e, v_stretch_e = axis_calib_ess(mv_e, yV)
    a_center_e, a_stretch_e = axis_calib_ess(ma_e, yA)
    impute_ess = {k: round(float(v), 6) for k, v in impute.items() if k in ESSENTIA_FEATURES}

    model = {
        "version": 3,
        "trainedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z"),
        "anchorsUsed": len(X),
        "features": FEATURES,
        "impute": {k: round(float(v), 6) for k, v in impute.items()},
        "scaler": {"mean": scaler.mean_.tolist(), "std": scaler.scale_.tolist()},
        "valence": {
            "transform": "raw",
            "coef": mv.coef_.tolist(),
            "intercept": float(mv.intercept_),
            "center": v_center, "stretch": v_stretch,
            "alpha": args.alpha_v,
            "loocv_rmse": rmse_v, "loocv_r": r_v,
        },
        "arousal": {
            "transform": "raw",
            "coef": ma.coef_.tolist(),
            "intercept": float(ma.intercept_),
            "center": a_center, "stretch": a_stretch,
            "alpha": args.alpha_a,
            "loocv_rmse": rmse_a, "loocv_r": r_a,
        },
        "essentiaOnly": {
            "features": ESSENTIA_FEATURES,
            "scaler": {"mean": scaler_ess.mean_.tolist(), "std": scaler_ess.scale_.tolist()},
            "impute": impute_ess,
            "valence": {
                "transform": "raw", "coef": mv_e.coef_.tolist(), "intercept": float(mv_e.intercept_),
                "center": v_center_e, "stretch": v_stretch_e, "alpha": args.alpha_v,
                "loocv_rmse": rmse_v_e, "loocv_r": r_v_e,
            },
            "arousal": {
                "transform": "raw", "coef": ma_e.coef_.tolist(), "intercept": float(ma_e.intercept_),
                "center": a_center_e, "stretch": a_stretch_e, "alpha": args.alpha_a,
                "loocv_rmse": rmse_a_e, "loocv_r": r_a_e,
            },
        },
    }
    if pair_info: model["pairEval"] = pair_info

    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(model, f, indent=2)
    print("wrote", args.out, file=sys.stderr)

if __name__ == "__main__":
    main()
