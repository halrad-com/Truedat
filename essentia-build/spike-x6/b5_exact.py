#!/usr/bin/env python3
"""x7 B5 exactness gate: the outputs of the two algorithms B5 rewrote, and the
loudness value beside them, must be IDENTICAL to the .7 tree before B5 on every
row - not within a band.

    python -I b5_exact.py <before label> <after label> [<before> <after> ...] [--out <stem>]

Keys: rhythm.danceability, lowlevel.dynamic_complexity, lowlevel.average_loudness
(dfa is not written by the music extractor). A key absent from a profile's
output is reported as absent on both sides, not as a pass. Coverage: every row
of the before label's runs.tsv with rc 0 must have a JSON on both sides.
Writes b5-exact[-<stem>].txt; exit 1 on any difference or coverage gap."""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
KEYS = ["rhythm.danceability", "lowlevel.dynamic_complexity", "lowlevel.average_loudness"]


def ok_ids(label):
    ids = []
    with open(os.path.join(HERE, "out", label, "runs.tsv"), encoding="utf-8") as f:
        next(f)
        for line in f:
            p = line.rstrip("\n").split("\t")
            if p[1] == "0":
                ids.append(p[0])
    return ids


def value(doc, key):
    node = doc
    for part in key.split("."):
        if not isinstance(node, dict) or part not in node:
            return None
        node = node[part]
    return node


def load(label, rid):
    path = os.path.join(HERE, "out", label, rid + ".json")
    if not os.path.exists(path):
        return None
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def main(argv):
    stem = None
    if "--out" in argv:
        i = argv.index("--out")
        stem = argv[i + 1]
        argv = argv[:i] + argv[i + 2:]
    if not argv or len(argv) % 2:
        print(__doc__)
        return 2
    lines, bad = [], 0
    for before, after in zip(argv[0::2], argv[1::2]):
        same = absent = 0
        for rid in ok_ids(before):
            x, y = load(before, rid), load(after, rid)
            if x is None or y is None:
                lines.append("COVERAGE %s vs %s: %s missing on %s" % (before, after, rid, before if x is None else after))
                bad += 1
                continue
            for k in KEYS:
                vx, vy = value(x, k), value(y, k)
                if vx is None and vy is None:
                    absent += 1
                elif vx == vy:
                    same += 1
                else:
                    lines.append("DIFF %s vs %s: %s %s %r -> %r" % (before, after, rid, k, vx, vy))
                    bad += 1
        lines.append("%s vs %s: %d identical key values, %d absent on both sides" % (before, after, same, absent))
    lines.append("PASS" if bad == 0 else "FAIL: %d difference(s) or coverage gap(s)" % bad)
    text = "\n".join(lines) + "\n"
    with open(os.path.join(HERE, "b5-exact%s.txt" % ("-" + stem if stem else "")), "w", encoding="utf-8") as f:
        f.write(text)
    sys.stdout.write(text)
    return 0 if bad == 0 else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
