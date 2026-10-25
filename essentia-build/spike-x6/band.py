#!/usr/bin/env python3
"""x6 end-to-end acceptance: the same-binary repeat-run band, and cross-build
per-field tables judged against it.

    python -I band.py same <label> <label> [<label> ...]
        Same extractor, repeated runs (run-repeat.sh). Every pair of labels, on
        every manifest row all runs decoded, gives per-key relative deltas.
        Writes repeat-band.txt (summary) and repeat-band.tsv (every key that
        moved: its max relative delta pooled over files and pairs).

    python -I band.py cross <baseline label> <candidate label> [parity label]
        Baseline vs candidate on each row, judged against repeat-band.tsv two
        ways: STRICT (beyond 2x the key's own band; a key that never moved
        between same-binary runs may not move at all) and FAMILY (beyond 2x the
        largest same-binary delta of its statistic family pooled over all keys
        - the output-x64.5 method, OUTPUT-BUILDS.md). If a parity label is
        given, each row carries that run's PCM verdict (parity-<label>.txt), so
        movement can be attributed to recovered audio. Writes
        field-table-<baseline>-<candidate>.txt.

Relative delta = |a - b| / max(|a|, |b|, 1e-12). Key-set differences and
non-numeric differences are listed outright (except the allowed build id).
Standard library only."""
import itertools
import json
import math
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ALLOWED = {"metadata.version.extractor_build"}
STATS = {"mean", "var", "stdev", "median", "min", "max", "dmean", "dmean2", "dvar", "dvar2"}


def flatten(node, prefix, out):
    if isinstance(node, dict):
        for k, v in node.items():
            flatten(v, prefix + "." + k if prefix else k, out)
    elif isinstance(node, list):
        for i, v in enumerate(node):
            flatten(v, "%s[%d]" % (prefix, i), out)
    else:
        out[prefix] = node


def load(label, row_id):
    path = os.path.join(HERE, "out", label, row_id + ".json")
    if not os.path.exists(path):
        return None
    out = {}
    try:
        with open(path, encoding="utf-8") as f:
            flatten(json.load(f), "", out)
    except ValueError:
        return None
    return out


def is_num(v):
    return isinstance(v, (int, float)) and not isinstance(v, bool)


def family(key):
    last = re.sub(r"\[\d+\]$", "", key.rsplit(".", 1)[-1])
    return last if last in STATS else "value"


def rel_diffs(a, b):
    setp = sorted(k for k in set(a) ^ set(b) if k not in ALLOWED)
    nonnum, rels = [], {}
    for k in set(a) & set(b):
        va, vb = a[k], b[k]
        if is_num(va) and is_num(vb):
            if isinstance(va, float) and isinstance(vb, float) and math.isnan(va) and math.isnan(vb):
                continue
            r = abs(va - vb) / max(1e-12, abs(va), abs(vb))
            if r > 0:
                rels[k] = r
        elif va != vb and k not in ALLOWED:
            nonnum.append(k)
    return setp, sorted(nonnum), rels


def manifest_rows():
    with open(os.path.join(HERE, "manifest.tsv"), encoding="utf-8") as f:
        next(f)
        return [dict(zip(["id", "kind", "path", "expect", "note"], l.rstrip("\n").split("\t"))) for l in f]


def codec_of(row_id):
    try:
        with open(os.path.join(HERE, "corpus", "probe", row_id + ".json"), encoding="utf-8") as f:
            s = (json.load(f).get("streams") or [{}])[0]
        return "%s/%s/%sch" % (s.get("codec_name", "?"), s.get("sample_rate", "?"), s.get("channels", "?"))
    except (OSError, ValueError):
        return "?"


def same(labels):
    key_band, fam_band, key_files = {}, {}, {}
    lines, problems, used = [], 0, 0
    lines.append("%-26s %-18s %-12s %6s %10s  %s" % ("id", "codec", "pair", "n_diff", "max_rel", "set/nonnum"))
    for row in manifest_rows():
        runs = {l: load(l, row["id"]) for l in labels}
        if any(v is None for v in runs.values()):
            lines.append("%-26s %-18s (not decoded by every run: skipped)" % (row["id"], codec_of(row["id"])))
            continue
        used += 1
        moved_here = set()
        for a, b in itertools.combinations(labels, 2):
            setp, nonnum, rels = rel_diffs(runs[a], runs[b])
            problems += len(setp) + len(nonnum)
            lines.append("%-26s %-18s %-12s %6d %10.3g  set=%d nonnum=%d%s" % (
                row["id"], codec_of(row["id"]), a + "/" + b, len(rels), max(rels.values(), default=0.0),
                len(setp), len(nonnum), (" " + ",".join((setp + nonnum)[:3])) if setp or nonnum else ""))
            for k, r in rels.items():
                key_band[k] = max(key_band.get(k, 0.0), r)
                fam_band[family(k)] = max(fam_band.get(family(k), 0.0), r)
                moved_here.add(k)
        for k in moved_here:
            key_files[k] = key_files.get(k, 0) + 1

    nkeys = len(load(labels[0], next(r["id"] for r in manifest_rows() if load(labels[0], r["id"])) ) or {})
    head = ["x6 same-binary repeat-run band: labels %s" % " ".join(labels),
            "rows used: %d; pairs per row: %d; keys per output: ~%d; keys that moved anywhere: %d"
            % (used, len(labels) * (len(labels) - 1) // 2, nkeys, len(key_band)),
            "set/non-numeric problems between same-binary runs: %d" % problems, "",
            "family     band (max rel, pooled over every key, row and pair)"]
    for f in sorted(fam_band):
        head.append("%-10s %.3g" % (f, fam_band[f]))
    head.append("")
    text = "\n".join(head + lines) + "\n"
    with open(os.path.join(HERE, "repeat-band.txt"), "w", encoding="utf-8") as f:
        f.write(text)
    with open(os.path.join(HERE, "repeat-band.tsv"), "w", encoding="utf-8") as f:
        f.write("key\tfamily\tmax_rel\trows_moved\n")
        for k in sorted(key_band):
            f.write("%s\t%s\t%.6g\t%d\n" % (k, family(k), key_band[k], key_files[k]))
    sys.stdout.write("\n".join(head) + "\n")
    return 1 if problems else 0


def read_band():
    key_band, fam_band = {}, {}
    with open(os.path.join(HERE, "repeat-band.tsv"), encoding="utf-8") as f:
        next(f)
        for line in f:
            k, fam, r, _ = line.rstrip("\n").split("\t")
            key_band[k] = float(r)
            fam_band[fam] = max(fam_band.get(fam, 0.0), float(r))
    return key_band, fam_band


def read_parity(label):
    verdicts = {}
    path = os.path.join(HERE, "parity-%s.txt" % label)
    if not os.path.exists(path):
        return verdicts
    with open(path, encoding="utf-8") as f:
        for line in f:
            parts = line.split()
            if len(parts) > 2 and parts[0] in ("PASS", "FAIL"):
                lost = parts[9] if len(parts) > 9 else ""
                verdicts[parts[1]] = parts[0] + ("" if parts[0] == "PASS" else " lost=" + lost)
    return verdicts


def cross(base, cand, parity_label):
    key_band, fam_band = read_band()
    parity = read_parity(parity_label) if parity_label else {}
    out = ["x6 per-field table: %s (baseline) vs %s (candidate)" % (base, cand),
           "judged against repeat-band.tsv: STRICT = beyond 2x the key's own same-binary band;"
           " FAMILY = beyond 2x its family's pooled band",
           "pcm = %s" % ("parity-%s.txt verdict for the baseline loader" % parity_label if parity_label else "(not given)"),
           "",
           "%-26s %-18s %-24s %6s %7s %7s %5s  %s" % ("id", "codec", "pcm", "moved", "strict", "family", "set", "top family-beyond keys")]
    tot_strict = tot_family = tot_set = 0
    for row in manifest_rows():
        a, b = load(base, row["id"]), load(cand, row["id"])
        if a is None or b is None:
            out.append("%-26s %-18s %-24s (missing: %s)" % (
                row["id"], codec_of(row["id"]), parity.get(row["id"], ""),
                ",".join(l for l, v in ((base, a), (cand, b)) if v is None)))
            continue
        setp, nonnum, rels = rel_diffs(a, b)
        strict = [k for k, r in rels.items() if r > 2 * key_band.get(k, 0.0)]
        fam = sorted(((r, k) for k, r in rels.items() if r > 2 * fam_band.get(family(k), 0.0)), reverse=True)
        tot_strict += len(strict)
        tot_family += len(fam)
        tot_set += len(setp) + len(nonnum)
        out.append("%-26s %-18s %-24s %6d %7d %7d %5d  %s" % (
            row["id"], codec_of(row["id"]), parity.get(row["id"], ""), len(rels), len(strict), len(fam),
            len(setp) + len(nonnum), "; ".join("%s %.2g" % (k, r) for r, k in fam[:4])))
        for k in (setp + nonnum)[:5]:
            out.append("    SET/NON-NUMERIC: %s" % k)
    out += ["", "totals: strict-beyond %d, family-beyond %d, set/non-numeric %d" % (tot_strict, tot_family, tot_set)]
    text = "\n".join(out) + "\n"
    with open(os.path.join(HERE, "field-table-%s-%s.txt" % (base, cand)), "w", encoding="utf-8") as f:
        f.write(text)
    sys.stdout.write(text)
    return 0


def main(argv):
    if len(argv) >= 3 and argv[0] == "same":
        return same(argv[1:])
    if len(argv) in (3, 4) and argv[0] == "cross":
        return cross(argv[1], argv[2], argv[3] if len(argv) == 4 else None)
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
