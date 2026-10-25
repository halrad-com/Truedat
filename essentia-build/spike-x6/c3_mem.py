#!/usr/bin/env python3
"""x7 C3 memory and write gate: the in-RAM spool's cost per track, measured.

    python -I c3_mem.py <spool-on label> <spool-off label> <cap bytes> [--out <stem>]

Both labels are production-shaped profiled passes of the SAME binary
(run-x7-c3-passes.sh: <b>pa / <b>ca with the spool, <b>na without). For each
real row: the peak working set with and without the spool, the difference,
and the spool the loader should have held - 352,800 B per audio-second for
stereo, half for mono, at the extractor's 44.1 kHz input (production-shaped
rows are all 44.1 kHz) - when the track's estimate (length x rate x channels
x 4 x 1.05) fits the cap, else 0 (an over-cap track decodes as before). And
written_mib with and without: the spool writes nothing, so the difference is
only the one log line the spool adds to the extractor's stdout.
Writes c3-mem[-<stem>].txt. Standard library only."""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import profile_table as pt  # noqa: E402


def headers(label):
    """{row id: {wall, cpu, peakws_mib, written_mib}} from out/<label>/profile.tsv headers"""
    ids = pt.id_by_path()
    out = {}
    with open(os.path.join(HERE, "out", label, "profile.tsv"), encoding="utf-8", errors="replace") as f:
        for line in f:
            if not line.startswith("# x6profile "):
                continue
            head = line[len("# x6profile "):].rstrip("\n")
            audio, _, rest = head.rpartition(" wall=")
            fields = dict(kv.split("=", 1) for kv in ("wall=" + rest).split(" ") if "=" in kv)
            out[pt.row_id(ids, audio)] = dict((k, float(v)) for k, v in fields.items())
    return out


def channels(rid):
    with open(os.path.join(HERE, "corpus", "probe", rid + ".json"), encoding="utf-8") as f:
        return int(json.load(f)["streams"][0]["channels"])


def main(argv):
    stem = None
    if "--out" in argv:
        i = argv.index("--out")
        stem = argv[i + 1]
        argv = argv[:i] + argv[i + 2:]
    if len(argv) != 3:
        print(__doc__)
        return 2
    on, off, cap = argv[0], argv[1], float(argv[2])
    a, b = headers(on), headers(off)
    lines = ["x7 C3 spool memory / writes: %s (spool) vs %s (no spool), cap %.0f bytes (%.1f MiB)" % (on, off, cap, cap / 1048576.0),
             "", "%-14s %3s %8s %9s %9s %9s %9s %9s  %s" % (
                 "id", "ch", "len s", "peak on", "peak off", "delta", "expected", "resid", "written on-off (bytes)")]
    worst = 0.0
    for rid in sorted(set(a) & set(b)):
        if not rid.startswith("r_"):
            continue
        with open(os.path.join(HERE, "out", on, rid + ".json"), encoding="utf-8") as f:
            length = float(json.load(f)["metadata"]["audio_properties"]["length"])
        ch = channels(rid)
        estimate = length * 44100 * ch * 4 * 1.05
        expected = (length * 44100 * ch * 4) / 1048576.0 if estimate <= cap else 0.0
        delta = a[rid]["peakws_mib"] - b[rid]["peakws_mib"]
        wdiff = (a[rid].get("written_mib", -1) - b[rid].get("written_mib", -1)) * 1048576.0
        worst = max(worst, abs(wdiff))
        lines.append("%-14s %3d %8.1f %9.1f %9.1f %+9.1f %9.1f %+9.1f  %+.0f" % (
            rid, ch, length, a[rid]["peakws_mib"], b[rid]["peakws_mib"], delta, expected, delta - expected, wdiff))
    lines += ["", "largest |written on - off|: %.0f bytes (the spool's own log line on stdout is ~60-90 bytes)" % worst]
    text = "\n".join(lines) + "\n"
    with open(os.path.join(HERE, "c3-mem%s.txt" % ("-" + stem if stem else "")), "w", encoding="utf-8") as f:
        f.write(text)
    sys.stdout.write(text)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
