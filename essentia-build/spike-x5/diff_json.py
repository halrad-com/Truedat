"""Main-output JSON diff for the .4 -> .5 additive check.

  python -I diff_json.py <a.json> <b.json> [--noise <n1.json> <n2.json>]

Flattens both documents (dotted keys), then reports: keys only in a, keys only in
b, non-numeric values that differ, and numeric values that differ with their
relative difference. With --noise, the two noise files (two runs of the SAME
binary on the SAME audio) set the yardstick: a value in a-vs-b counts as "beyond
noise" when its relative difference exceeds 2x the largest relative difference
seen anywhere in the noise pair. Exit 1 if any key is missing/added (other than
metadata.version.extractor_build) or any value is beyond noise."""
import json, math, sys

ALLOWED_NEW = {"metadata.version.extractor_build"}

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

def rel(a, b):
    if is_num(a) and is_num(b):
        if (isinstance(a, float) and math.isnan(a)) and (isinstance(b, float) and math.isnan(b)): return 0.0
        return abs(a - b) / max(1e-12, abs(a), abs(b))
    return None

def compare(a, b):
    """-> (only_a, only_b, nonnum_diffs, [(key, va, vb, rel)])"""
    only_a = sorted(set(a) - set(b)); only_b = sorted(set(b) - set(a))
    nonnum, num = [], []
    for k in sorted(set(a) & set(b)):
        va, vb = a[k], b[k]
        r = rel(va, vb)
        if r is None:
            if va != vb: nonnum.append((k, va, vb))
        elif r > 0: num.append((k, va, vb, r))
    return only_a, only_b, nonnum, num

def main(argv):
    noise = None
    if "--noise" in argv:
        i = argv.index("--noise"); n1, n2 = load(argv[i + 1]), load(argv[i + 2]); argv = argv[:i]
        oa, ob, nn, nd = compare(n1, n2)
        noise = {k: r for k, _, _, r in nd}  # per-key relative noise seen between two runs of one binary
        print("NOISE baseline (%d values differ, max rel %.3g; %d keys differ in set, %d non-numeric diffs)"
              % (len(nd), max(noise.values(), default=0.0), len(oa) + len(ob), len(nn)))
    a, b = load(argv[0]), load(argv[1])
    only_a, only_b, nonnum, num = compare(a, b)
    print("keys: a=%d b=%d shared=%d" % (len(a), len(b), len(a) - len(only_a)))
    for k in only_a: print("  ONLY IN A: %s" % k)
    for k in only_b: print("  ONLY IN B: %s%s" % (k, "   (allowed)" if k in ALLOWED_NEW else ""))
    for k, va, vb in nonnum: print("  NON-NUMERIC DIFF: %s  a=%r  b=%r" % (k, va, vb))
    maxr = max([r for _, _, _, r in num], default=0.0)
    print("numeric values differing: %d, max rel diff %.3g" % (len(num), maxr))
    failures = len(only_a) + len([k for k in only_b if k not in ALLOWED_NEW]) + len(nonnum)
    if noise is not None:
        # per key: allowed = 2x the noise that key showed between two runs of the same
        # binary; a key the noise pair left identical must stay identical (1e-7 floor
        # absorbs only a last-digit print difference).
        beyond = [t for t in num if t[3] > max(2 * noise.get(t[0], 0.0), 1e-7)]
        quiet = [t for t in num if t[0] not in noise]
        print("values beyond per-key noise: %d  (differing keys the noise pair never moved: %d)" % (len(beyond), len(quiet)))
        for k, va, vb, r in sorted(beyond, key=lambda t: -t[3])[:15]:
            print("  BEYOND: %s  a=%r  b=%r  rel=%.3g  noise=%.3g" % (k, va, vb, r, noise.get(k, 0.0)))
        failures += len(beyond)
    else:
        for k, va, vb, r in sorted(num, key=lambda t: -t[3])[:10]:
            print("  top diff: %s  a=%r  b=%r  rel=%.3g" % (k, va, vb, r))
    print("RESULT: %s" % ("PASS" if failures == 0 else "FAIL (%d)" % failures))
    return 1 if failures else 0

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
