#!/usr/bin/env python3
"""For one row, the numeric keys whose values differ across a set of run
labels (repeat-noise.sh, or any out/<label>/<row>.json), with each label's
value and the spread (max relative difference between any two labels), so a
cross-build movement can be set against the same-binary spread.
    python -I noise_keys.py <row id> <label> <label> [<label> ...] [--keys k1,k2] [--out <stem>]
Writes noise-<stem or row id>.txt. Standard library only."""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import band  # noqa: E402  (flatten, num_delta)


def main(argv):
    keys = None
    if "--keys" in argv:
        i = argv.index("--keys")
        keys = set(argv[i + 1].split(","))
        argv = argv[:i] + argv[i + 2:]
    stem = None
    if "--out" in argv:
        i = argv.index("--out")
        stem = argv[i + 1]
        argv = argv[:i] + argv[i + 2:]
    if len(argv) < 3:
        print(__doc__)
        return 2
    row, labels = argv[0], argv[1:]
    runs = {}
    for l in labels:
        p = os.path.join(HERE, "out", l, row + ".json")
        out = {}
        with open(p, encoding="utf-8") as f:
            band.flatten(json.load(f), "", out)
        runs[l] = out
    common = set.intersection(*(set(v) for v in runs.values()))
    lines = ["%s: %d labels: %s" % (row, len(labels), " ".join(labels)), ""]
    moved = 0
    for k in sorted(common):
        if keys is not None and k not in keys:
            continue
        vals = [runs[l][k] for l in labels]
        if not all(band.is_num(v) for v in vals):
            continue
        spread = 0.0
        for i in range(len(vals)):
            for j in range(i + 1, len(vals)):
                d = band.num_delta(vals[i], vals[j])
                spread = float("inf") if d == band.NONFINITE else max(spread, d)
        if spread > 0 or keys is not None:
            moved += 1
            lines.append("%-55s spread %.3g  %s" % (k, spread, "  ".join("%s=%.10g" % (l, v) for l, v in zip(labels, vals))))
    lines += ["", "%d key(s) listed" % moved]
    text = "\n".join(lines) + "\n"
    with open(os.path.join(HERE, "noise-%s.txt" % (stem or row)), "w", encoding="utf-8") as f:
        f.write(text)
    sys.stdout.write(text)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
