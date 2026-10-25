#!/usr/bin/env python3
"""Reduces essentia_standard_x7_hpcp_bench output (run-hpcp-bench.sh) to the
two numbers the x7 HPCP item needs, per row and pooled:

  fused   = the shared walk ("walk": pow + band choice + log2 per in-range peak
            and harmonic position) - what a fused chords+tuning HPCP would no
            longer compute twice, as a share of chords and of all three HPCPs
  buffers = the per-call hpcp_LO/hpcp_HI allocation of chords (36) + tuning
            (120) - what member buffers would remove

Each "hpcp" time has the bench's input copy ("copy") subtracted, since the
extractor's streaming wrapper copies differently. Standard library only.
    python -I hpcp_summary.py <raw file>"""
import sys
from collections import OrderedDict


def parse(path):
    rows = OrderedDict()
    cur = None
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            if line.startswith("row="):
                cur = rows.setdefault(line[4:], {})
                continue
            if cur is None:
                continue
            if line.startswith("input="):
                kv = dict(p.split("=", 1) for p in line.split(" ") if "=" in p)
                cur["seconds"] = float(kv["seconds"])
                cur["frames"] = int(kv["frames"])
                cur["in_range"] = int(kv["peaks_in_range"])
                continue
            head, _, rest = line.partition(" ")
            kv = dict(p.split("=", 1) for p in rest.split(" ") if "=" in p)
            if head == "hpcp":
                cur["hpcp_" + kv["config"]] = float(kv["seconds"])
            elif head == "copy":
                cur["copy"] = float(kv["seconds"])
            elif head == "walk":
                cur["walk"] = float(kv["seconds"])
                cur["contributions"] = int(kv["contributions"])
            elif head == "alloc":
                cur["alloc_" + kv["size"]] = float(kv["seconds"])
            elif head == "fused":   # x7: HPCPFused against chords + tuning
                cur["fused"] = float(kv["seconds"])
                cur["fused_diff"] = int(kv["frames_differing_36"]) + int(kv["frames_differing_120"])
    return rows


def main(argv):
    if len(argv) != 1:
        print(__doc__)
        return 2
    rows = parse(argv[0])
    keys = ("hpcp_key", "hpcp_chords", "hpcp_tuning", "copy", "walk", "alloc_36", "alloc_120")
    out = ["x7 step 0 - HPCP sizing bench (essentia_standard_x7_hpcp_bench), real rows of manifest-prod.tsv",
           "times are the minimum over the repeats, seconds per pass over every frame; hpcp_* minus the input copy",
           "",
           "%-14s %8s %6s %7s %9s %9s %9s %9s %9s %9s  %8s %8s" % (
               "id", "audio s", "frames", "pk/frm", "key", "chords", "tuning", "walk", "alloc36", "alloc120",
               "fused%ch", "buf%3")]
    tot = dict((k, 0.0) for k in keys)
    tot_frames = 0
    for rid, r in rows.items():
        if not all(k in r for k in keys + ("frames",)):
            out.append("%-14s incomplete: %s" % (rid, ", ".join(k for k in keys if k not in r)))
            continue
        c = r["copy"]
        key, ch, tu = r["hpcp_key"] - c, r["hpcp_chords"] - c, r["hpcp_tuning"] - c
        three = key + ch + tu
        out.append("%-14s %8.1f %6d %7.1f %9.4f %9.4f %9.4f %9.4f %9.4f %9.4f  %7.1f%% %7.1f%%" % (
            rid, r["seconds"], r["frames"], r["in_range"] / max(1, r["frames"]), key, ch, tu, r["walk"],
            r["alloc_36"], r["alloc_120"], 100 * r["walk"] / ch if ch else 0,
            100 * (r["alloc_36"] + r["alloc_120"]) / three if three else 0))
        for k in keys:
            tot[k] += r[k]
        tot_frames += r["frames"]
    if not tot_frames:
        out.append("no complete rows")
        sys.stdout.write("\n".join(out) + "\n")
        return 1
    c = tot["copy"]
    key, ch, tu = tot["hpcp_key"] - c, tot["hpcp_chords"] - c, tot["hpcp_tuning"] - c
    three = key + ch + tu
    out += ["",
            "pooled: key %.4f s  chords %.4f s  tuning %.4f s  (three %.4f s; tuning / chords = %.2f)" % (key, ch, tu, three, tu / ch if ch else 0),
            "  fused chords+tuning saves the shared walk once: %.4f s = %.1f%% of chords, %.1f%% of the three HPCPs" % (
                tot["walk"], 100 * tot["walk"] / ch if ch else 0, 100 * tot["walk"] / three if three else 0),
            "  member LO/HI buffers save the per-call allocation: %.4f s (36) + %.4f s (120) = %.1f%% of the three HPCPs" % (
                tot["alloc_36"], tot["alloc_120"], 100 * (tot["alloc_36"] + tot["alloc_120"]) / three if three else 0),
            "  frames %d; per frame: chords %.2f us, tuning %.2f us, walk %.2f us" % (
                tot_frames, 1e6 * ch / tot_frames, 1e6 * tu / tot_frames, 1e6 * tot["walk"] / tot_frames)]
    fused_rows = [r for r in rows.values() if "fused" in r]
    if fused_rows:
        fz = sum(r["fused"] for r in fused_rows) - sum(r["copy"] for r in fused_rows)
        sep = sum(r["hpcp_chords"] + r["hpcp_tuning"] - 2 * r["copy"] for r in fused_rows)
        diff = sum(r["fused_diff"] for r in fused_rows)
        out.append("  HPCPFused (x7) on %d row(s): frames differing from HPCP chords/tuning: %d (%s); time %.4f s vs %.4f s separate = %.1f%% saved" % (
            len(fused_rows), diff, "BIT-IDENTICAL" if diff == 0 else "MISMATCH", fz, sep, 100 * (sep - fz) / sep if sep else 0))
    out += ["",
            "To put these on the extractor's scale, multiply each share by the HPCP rows of the same input's",
            "x6profile table (step0-decode.txt: HPCP[size=36] is key + chords together, HPCP[size=120] is tuning)."]
    sys.stdout.write("\n".join(out) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
