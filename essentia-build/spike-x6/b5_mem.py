#!/usr/bin/env python3
"""x7 B5 memory gate: what streaming Danceability and DynamicComplexity save.

    python -I b5_mem.py <before label> <after label> [--out <stem>]

Both labels are production-shaped profiled passes with the SAME profile, on
the .7 tree before B5 (c3na / c3pa) and after it (b5na / b5pa). Before B5,
each of the two algorithms stored the whole mono 44.1 kHz signal in a pool
(4 bytes a sample, in a vector that grows by doubling), so the pair held at
least 352,800 B per audio-second at the end of a track; after it they hold a
10 ms frame, a 0.2 s frame and their small per-frame series. For each real row:
the peak working set before and after, the saving, and the floor the old
pools implied (352,800 B/s). Then a least-squares fit of peak against length
for each label: the slope is what truedat's per-track spool cap (Program.
SpoolCapBytes) budgets per audio-second, the intercept its base.
Writes b5-mem[-<stem>].txt. Standard library only."""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import c3_mem  # noqa: E402

PAIR_MIB_PER_S = 352800.0 / 1048576


def length_of(label, rid):
    with open(os.path.join(HERE, "out", label, rid + ".json"), encoding="utf-8") as f:
        return float(json.load(f)["metadata"]["audio_properties"]["length"])


def fit(pts):
    n = len(pts)
    mx = sum(p[0] for p in pts) / n
    my = sum(p[1] for p in pts) / n
    slope = sum((p[0] - mx) * (p[1] - my) for p in pts) / sum((p[0] - mx) ** 2 for p in pts)
    return slope, my - slope * mx


def main(argv):
    stem = None
    if "--out" in argv:
        i = argv.index("--out")
        stem = argv[i + 1]
        argv = argv[:i] + argv[i + 2:]
    if len(argv) != 2:
        print(__doc__)
        return 2
    before, after = argv
    a, b = c3_mem.headers(before), c3_mem.headers(after)
    rows = []
    for rid in sorted(set(a) & set(b)):
        if not rid.startswith("r_"):
            continue
        length = length_of(after, rid)
        if abs(length - length_of(before, rid)) > 1e-6:
            raise SystemExit("%s: length differs between %s and %s" % (rid, before, after))
        rows.append((rid, length, a[rid]["peakws_mib"], b[rid]["peakws_mib"]))
    rows.sort(key=lambda r: r[1])
    lines = ["x7 B5 memory: %s (before B5) vs %s (after), same profile, production-shaped input" % (before, after),
             "", "%-14s %8s %10s %10s %9s %9s" % ("id", "len s", "peak bef", "peak aft", "saved", "floor")]
    for rid, length, pb, pa in rows:
        lines.append("%-14s %8.1f %10.1f %10.1f %+9.1f %9.1f" % (rid, length, pb, pa, pb - pa, PAIR_MIB_PER_S * length))
    lines.append("")
    for name, idx in ((before, 2), (after, 3)):
        slope, icpt = fit([(r[1], r[idx]) for r in rows])
        lines.append("fit %-12s rows %d  slope %.4f MiB per audio-second (%.3f GiB per audio-hour)  intercept %.1f MiB" % (
            name, len(rows), slope, slope * 3600 / 1024, icpt))
    text = "\n".join(lines) + "\n"
    with open(os.path.join(HERE, "b5-mem%s.txt" % ("-" + stem if stem else "")), "w", encoding="utf-8") as f:
        f.write(text)
    sys.stdout.write(text)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
