"""Is .5's main output distinguishable from re-running .4? (real-audio additive check, step v)

  python -I noise_table.py <label> [more .4 outputs of the same audio ...]

Loads real/<label>.x4.json, real/<label>.x5.json and real/<label>.x5fb.json (the
.5 run that also wrote --frames-bin), plus any extra .4 outputs given, and
compares every pair. Same-binary pairs (.4 vs .4, .5 vs .5) define the run-to-run
noise; cross pairs (.4 vs .5) are judged against it PER STATISTIC FAMILY (the
suffix: mean/var/stdev/median/min/max/dmean/dmean2/dvar/dvar2, or 'value' for a
plain field), because a single noise pair cannot bound one key's noise but the
family's spread is stable. A cross value is "beyond noise" when its relative
difference exceeds 2x the largest same-binary difference seen in its family.
Any key missing/added (other than metadata.version.extractor_build) or any
non-numeric value differing fails outright. Exit 1 on failure."""
import itertools, json, math, os, re, sys

ALLOWED_NEW = {"metadata.version.extractor_build"}
STATS = {"mean", "var", "stdev", "median", "min", "max", "dmean", "dmean2", "dvar", "dvar2"}

def flatten(node, prefix, out):
    if isinstance(node, dict):
        for k, v in node.items(): flatten(v, prefix + "." + k if prefix else k, out)
    elif isinstance(node, list):
        for i, v in enumerate(node): flatten(v, "%s[%d]" % (prefix, i), out)
    else:
        out[prefix] = node

def load(path):
    out = {}; flatten(json.load(open(path, encoding="utf-8")), "", out); return out

def is_num(v): return isinstance(v, (int, float)) and not isinstance(v, bool)

def family(key):
    last = re.sub(r"\[\d+\]$", "", key.rsplit(".", 1)[-1])
    return last if last in STATS else "value"

def diffs(a, b):
    """-> (set problems, nonnum problems, {key: rel})"""
    setp = [k for k in (set(a) ^ set(b)) if k not in ALLOWED_NEW]
    nonnum, rels = [], {}
    for k in set(a) & set(b):
        va, vb = a[k], b[k]
        if is_num(va) and is_num(vb):
            if isinstance(va, float) and isinstance(vb, float) and math.isnan(va) and math.isnan(vb): continue
            r = abs(va - vb) / max(1e-12, abs(va), abs(vb))
            if r > 0: rels[k] = r
        elif va != vb:
            nonnum.append(k)
    return setp, nonnum, rels

def main(argv):
    # labels first, then any extra .4 outputs (attached to the FIRST label's audio)
    labels = [a for a in argv if not a.endswith(".json")]
    extras = [a for a in argv if a.endswith(".json")]
    here = os.path.dirname(os.path.abspath(__file__))
    files = {}  # label -> (runs4, runs5)
    for label in labels:
        runs4 = {"x4": load(os.path.join(here, "real", label + ".x4.json"))}
        if label == labels[0]:
            for i, p in enumerate(extras): runs4["x4_%d" % (i + 1)] = load(p)
        runs5 = {"x5": load(os.path.join(here, "real", label + ".x5.json")),
                 "x5fb": load(os.path.join(here, "real", label + ".x5fb.json"))}
        files[label] = (runs4, runs5)
    failures = 0
    fam_noise, fam_cross = {}, {}   # pooled over every file: noise is a property of the pipeline, not of one track
    cross_rows = []                 # (label, a, b, rels, runs)
    print("%-6s %-14s %6s %10s   %s" % ("file", "pair", "n_diff", "max_rel", "set/nonnum problems"))
    for label, (runs4, runs5) in files.items():
        same = list(itertools.combinations(runs4, 2)) + [("x5", "x5fb")]
        cross = [(a, b) for a in runs4 for b in runs5]
        allruns = dict(runs4); allruns.update(runs5)
        for kind, pairs in (("same", same), ("cross", cross)):
            for a, b in pairs:
                setp, nonnum, rels = diffs(allruns[a], allruns[b])
                print("%-6s %-14s %6d %10.3g   set=%d nonnum=%d  [%s]" % (label, a + "/" + b, len(rels), max(rels.values(), default=0.0), len(setp), len(nonnum), kind))
                for k in setp: print("   SET PROBLEM: %s" % k); failures += 1
                for k in nonnum: print("   NON-NUMERIC DIFF: %s" % k); failures += 1
                target = fam_noise if kind == "same" else fam_cross
                for k, r in rels.items():
                    f = family(k); target[f] = max(target.get(f, 0.0), r)
                if kind == "cross": cross_rows.append((label, a, b, rels, allruns))
    print("\nfamily         same-binary max rel   cross max rel   (pooled over %d files)" % len(files))
    for f in sorted(set(fam_noise) | set(fam_cross)):
        print("%-14s %20.3g %15.3g%s" % (f, fam_noise.get(f, 0.0), fam_cross.get(f, 0.0),
              "   <-- never moves between same-binary runs" if f not in fam_noise else ""))
    print("\ncross values beyond 2x pooled family noise:")
    total_beyond = 0
    for label, a, b, rels, allruns in cross_rows:
        beyond = [(r, k) for k, r in rels.items() if r > 2 * fam_noise.get(family(k), 0.0)]
        total_beyond += len(beyond)
        print("  %-6s %-10s %d" % (label, a + "/" + b, len(beyond)))
        for r, k in sorted(beyond, reverse=True)[:10]:
            print("     BEYOND %s a=%r b=%r rel=%.3g (family %s noise %.3g)" % (k, allruns[a][k], allruns[b][k], r, family(k), fam_noise.get(family(k), 0.0)))
    failures += total_beyond
    print("RESULT: %s" % ("PASS" if failures == 0 else "FAIL (%d)" % failures))
    return 1 if failures else 0

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
