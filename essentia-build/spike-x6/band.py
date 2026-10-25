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

    python -I band.py scan <full label> <scan label> [parity label]
        The drop 2 gate for a reduced profile: on every row, each key in
        scan-allowlist.txt [main] must be PRESENT in the scan output and equal
        the full output - exactly on rows where the full extractor is itself
        run-to-run exact (synthetic rows: 0 keys in repeat-band.tsv moved
        there), within the band otherwise (STRICT and FAMILY as above). Keys the
        scan output drops that are NOT allowlisted are the intended cut and are
        counted, not judged. Writes gate-scan-<full>-<scan>.txt.

    --band <name>  (any mode) read or write <name>-band.tsv/.txt instead of
                   repeat-band.tsv/.txt, so the .5 band and a later build's
                   same-binary band can coexist.

Relative delta = |a - b| / max(|a|, |b|, 1e-12). Key-set differences and
non-numeric differences are listed outright (except the allowed build id).
Standard library only."""
import itertools
import json
import math
import os
import re
import sys
from collections import OrderedDict

HERE = os.path.dirname(os.path.abspath(__file__))
ALLOWED = {"metadata.version.extractor_build"}
BAND = "repeat-band"   # file stem; --band <name> makes it <name>-band
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
    key_band, fam_band, key_files, row_moved = {}, {}, {}, OrderedDict()
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
        row_moved[row["id"]] = len(moved_here)

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
    with open(os.path.join(HERE, BAND + ".txt"), "w", encoding="utf-8") as f:
        f.write(text)
    with open(os.path.join(HERE, BAND + ".tsv"), "w", encoding="utf-8") as f:
        f.write("key\tfamily\tmax_rel\trows_moved\n")
        for k in sorted(key_band):
            f.write("%s\t%s\t%.6g\t%d\n" % (k, family(k), key_band[k], key_files[k]))
        # per-row: how many keys moved between the same-binary runs of THAT row;
        # 0 = the extractor repeats exactly there, so a candidate must too
        f.write("#rows\n")
        for i, n in row_moved.items():
            f.write("#row\t%s\t%d\n" % (i, n))
    sys.stdout.write("\n".join(head) + "\n")
    return 1 if problems else 0


def read_band(with_rows=False):
    key_band, fam_band, exact_rows = {}, {}, set()
    with open(os.path.join(HERE, BAND + ".tsv"), encoding="utf-8") as f:
        next(f)
        for line in f:
            if line.startswith("#row\t"):
                _, i, n = line.rstrip("\n").split("\t")
                if int(n) == 0:
                    exact_rows.add(i)
                continue
            if line.startswith("#"):
                continue
            k, fam, r, _ = line.rstrip("\n").split("\t")
            key_band[k] = float(r)
            fam_band[fam] = max(fam_band.get(fam, 0.0), float(r))
    return (key_band, fam_band, exact_rows) if with_rows else (key_band, fam_band)


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
    weak = sorted(f for f, b in fam_band.items() if 2 * b >= 1.0)
    out = ["x6 per-field table: %s (baseline) vs %s (candidate)" % (base, cand),
           "judged against repeat-band.tsv: STRICT = beyond 2x the key's own same-binary band (primary);"
           " FAMILY = beyond 2x its family's pooled band (secondary)",
           "FAMILY is uninformative for a family whose 2x band reaches the metric's ceiling (rel diff <= 2):"
           " %s" % (", ".join("%s (2x band %.3g)" % (f, 2 * fam_band[f]) for f in weak) if weak else "none"),
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


def read_allowlist():
    keys, section = set(), None
    with open(os.path.join(HERE, "scan-allowlist.txt"), encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            if line.startswith("["):
                section = line
            elif section == "[main]":
                keys.add(line)
    return keys


def strip_index(key):
    return re.sub(r"\[\d+\]", "", key)


def scan_gate(full, scan, parity_label):
    key_band, fam_band, exact_rows = read_band(with_rows=True)
    allowed = read_allowlist()
    parity = read_parity(parity_label) if parity_label else {}
    out = ["x6 drop 2 scan gate: %s (full) vs %s (reduced profile)" % (full, scan),
           "every allowlisted key must be present in the reduced output and equal the full one:"
           " exactly where the full extractor repeats exactly, else within 2x the key's / family's band",
           "",
           "%-26s %-18s %-14s %7s %7s %6s %6s %6s %7s  %s" % (
               "id", "codec", "pcm", "allowed", "missing", "exact", "strict", "family", "cut", "note")]
    tot_missing = tot_strict = tot_family = tot_nonnum = 0
    for row in manifest_rows():
        a, b = load(full, row["id"]), load(scan, row["id"])
        if a is None or b is None:
            out.append("%-26s %-18s (missing: %s)" % (row["id"], codec_of(row["id"]),
                       ",".join(l for l, v in ((full, a), (scan, b)) if v is None)))
            continue
        allowed_keys = [k for k in a if strip_index(k) in allowed]
        missing = [k for k in allowed_keys if k not in b]
        cut = [k for k in a if k not in b and strip_index(k) not in allowed]
        exact_row = row["id"] in exact_rows   # the full extractor repeats exactly on this row
        strict, fam, nonnum, exact_diff = [], [], [], []
        for k in allowed_keys:
            if k in missing:
                continue
            va, vb = a[k], b[k]
            if is_num(va) and is_num(vb):
                if isinstance(va, float) and isinstance(vb, float) and math.isnan(va) and math.isnan(vb):
                    continue
                r = abs(va - vb) / max(1e-12, abs(va), abs(vb))
                if r == 0:
                    continue
                if exact_row:
                    exact_diff.append(k)
                if r > 2 * key_band.get(k, 0.0):
                    strict.append(k)
                if r > 2 * fam_band.get(family(k), 0.0):
                    fam.append((r, k))
            elif va != vb and k not in ALLOWED:
                nonnum.append(k)
        tot_missing += len(missing)
        tot_strict += len(strict) + len(exact_diff)
        tot_family += len(fam) + len(exact_diff)
        tot_nonnum += len(nonnum)
        note = ""
        if missing:
            note = "MISSING " + ",".join(missing[:3])
        elif exact_diff:
            note = "DIFF on an exact row: " + ",".join(exact_diff[:3])
        elif fam:
            note = "; ".join("%s %.2g" % (k, r) for r, k in sorted(fam, reverse=True)[:3])
        elif strict:
            note = "strict: " + ", ".join(
                "%s %.2g (band %.2g)" % (k, abs(a[k] - b[k]) / max(1e-12, abs(a[k]), abs(b[k])), key_band.get(k, 0.0))
                for k in strict[:3])
        if nonnum:
            note += " NON-NUMERIC " + ",".join(nonnum[:3])
        out.append("%-26s %-18s %-14s %7d %7d %6s %6d %6d %7d  %s" % (
            row["id"], codec_of(row["id"]), parity.get(row["id"], "")[:14], len(allowed_keys), len(missing),
            "yes" if exact_row else "band", len(strict), len(fam), len(cut), note))
    out += ["", "totals: allowlisted missing %d, strict/exact-row diffs %d, family-beyond %d, non-numeric %d"
            % (tot_missing, tot_strict, tot_family, tot_nonnum),
            "PASS" if tot_missing == 0 and tot_strict == 0 and tot_family == 0 and tot_nonnum == 0 else "FAIL"]
    text = "\n".join(out) + "\n"
    with open(os.path.join(HERE, "gate-scan-%s-%s.txt" % (full, scan)), "w", encoding="utf-8") as f:
        f.write(text)
    sys.stdout.write(text)
    return 0 if out[-1] == "PASS" else 1


def main(argv):
    global BAND
    if "--band" in argv:
        i = argv.index("--band")
        BAND = argv[i + 1] + "-band"
        argv = argv[:i] + argv[i + 2:]
    if len(argv) >= 3 and argv[0] == "same":
        return same(argv[1:])
    if len(argv) in (3, 4) and argv[0] == "cross":
        return cross(argv[1], argv[2], argv[3] if len(argv) == 4 else None)
    if len(argv) in (3, 4) and argv[0] == "scan":
        return scan_gate(argv[1], argv[2], argv[3] if len(argv) == 4 else None)
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
