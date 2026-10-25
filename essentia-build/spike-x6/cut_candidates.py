#!/usr/bin/env python3
"""x6 B2: which keys does the full extractor emit that truedat never reads?

    python -I cut_candidates.py <label> [<id> ...]

Flattens out/<label>/<id>.json (default: every row of manifest.tsv the label
decoded), strips array indices, and splits the key set against
scan-allowlist.txt [main]. Writes cut-candidates.txt: the read keys grouped by
descriptor with the statistics read vs emitted (the requested-statistic cuts),
the descriptors no key of which is read (the branch cuts), and any allowlisted
key the full output does not carry. Standard library only."""
import json
import os
import re
import sys
from collections import OrderedDict

HERE = os.path.dirname(os.path.abspath(__file__))
STATS = ("mean", "var", "stdev", "median", "min", "max", "dmean", "dmean2", "dvar", "dvar2", "cov", "icov")


def flatten(node, prefix, out):
    if isinstance(node, dict):
        for k, v in node.items():
            flatten(v, prefix + "." + k if prefix else k, out)
    elif isinstance(node, list):
        for v in node:
            flatten(v, prefix, out)   # indices dropped: one key per array
    else:
        out.add(prefix)


def allowlist():
    keys, section = set(), None
    with open(os.path.join(HERE, "scan-allowlist.txt"), encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            if line.startswith("["):
                section = line
                continue
            if section == "[main]":
                keys.add(line)
    return keys


def split(key):
    """-> (descriptor, statistic or '')"""
    parts = key.rsplit(".", 1)
    if len(parts) == 2 and parts[1] in STATS:
        return parts[0], parts[1]
    return key, ""


def main(argv):
    if not argv:
        print(__doc__)
        return 2
    label, ids = argv[0], argv[1:]
    if not ids:
        with open(os.path.join(HERE, "manifest.tsv"), encoding="utf-8") as f:
            next(f)
            ids = [l.split("\t")[0] for l in f]
    emitted = set()
    used = 0
    for i in ids:
        path = os.path.join(HERE, "out", label, i + ".json")
        if not os.path.exists(path):
            continue
        with open(path, encoding="utf-8") as f:
            try:
                flatten(json.load(f), "", emitted)
                used += 1
            except ValueError:
                pass
    allowed = allowlist()

    by_desc = OrderedDict()
    for k in sorted(emitted):
        d, s = split(k)
        by_desc.setdefault(d, {"emitted": [], "read": []})
        by_desc[d]["emitted"].append(s or "(value)")
        if k in allowed:
            by_desc[d]["read"].append(s or "(value)")

    lines = ["x6 B2 cut candidates: full output of label %s over %d file(s) vs scan-allowlist.txt [main]" % (label, used),
             "emitted keys %d, read keys %d, emitted-not-read %d" % (
                 len(emitted), len(emitted & allowed), len(emitted - allowed)),
             "", "== descriptors read (statistics read / emitted; the difference is the requested-statistic cut) =="]
    for d, v in by_desc.items():
        if v["read"]:
            cut = [s for s in v["emitted"] if s not in v["read"]]
            lines.append("%-48s read %-22s cut %s" % (d, ",".join(v["read"]), ",".join(cut) if cut else "-"))
    lines += ["", "== descriptors NOT read at all (branch cut candidates; check the native graph before cutting) =="]
    for d, v in by_desc.items():
        if not v["read"]:
            lines.append("%-48s emitted %s" % (d, ",".join(v["emitted"])))
    missing = sorted(k for k in allowed if k not in emitted)
    lines += ["", "== allowlisted keys the full output does not carry (%d) ==" % len(missing)] + missing
    text = "\n".join(lines) + "\n"
    with open(os.path.join(HERE, "cut-candidates.txt"), "w", encoding="utf-8") as f:
        f.write(text)
    sys.stdout.write(text)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
