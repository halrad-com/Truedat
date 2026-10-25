#!/usr/bin/env python3
"""Least-squares fit of peak working set (MiB) against audio length (s) over the
real rows of one profile label: reads spike-x6/out/<label>/profile.tsv headers
and the matching <id>.json's metadata.audio_properties.length.
    python -I mem_fit.py <spike-x6 dir> <profile label> <json label>"""
import json, os, sys

here, plabel, jlabel = sys.argv[1], sys.argv[2], sys.argv[3]
ids = {}
with open(os.path.join(here, "manifest.tsv"), encoding="utf-8") as f:
    next(f)
    for line in f:
        p = line.rstrip("\n").split("\t")
        q = p[2].replace("\\", "/")
        if len(q) > 2 and q[0] == "/" and q[2] == "/":
            q = q[1].upper() + ":" + q[2:]
        ids[q.lower()] = p[0]
pts = []
with open(os.path.join(here, "out", plabel, "profile.tsv"), encoding="utf-8", errors="replace") as f:
    for line in f:
        if not line.startswith("# x6profile "):
            continue
        head = line[len("# x6profile "):].rstrip("\n")
        audio, _, rest = head.rpartition(" wall=")
        fields = dict(kv.split("=", 1) for kv in ("wall=" + rest).split(" ") if "=" in kv)
        rid = ids[audio.replace("\\", "/").lower()]
        if not rid.startswith("r_"):
            continue
        with open(os.path.join(here, "out", jlabel, rid + ".json"), encoding="utf-8") as j:
            length = json.load(j)["metadata"]["audio_properties"]["length"]
        pts.append((rid, float(length), float(fields["peakws_mib"])))
n = len(pts)
mx = sum(p[1] for p in pts) / n
my = sum(p[2] for p in pts) / n
slope = sum((p[1] - mx) * (p[2] - my) for p in pts) / sum((p[1] - mx) ** 2 for p in pts)
icpt = my - slope * mx
print("rows %d  slope %.4f MiB per audio-second (%.3f GiB per audio-hour)  intercept %.1f MiB" % (
    n, slope, slope * 3600 / 1024, icpt))
for rid, length, peak in sorted(pts, key=lambda p: p[1]):
    print("  %-14s length %8.2f s  peak %7.1f MiB  fit %7.1f  resid %+6.1f" % (
        rid, length, peak, icpt + slope * length, peak - (icpt + slope * length)))
