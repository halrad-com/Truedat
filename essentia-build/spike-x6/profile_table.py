#!/usr/bin/env python3
"""x6 per-phase / per-algorithm ELAPSED-time table from x6profile output.

Every per-algorithm and per-phase number is elapsed time (QueryPerformanceCounter
around single-threaded process() calls), which includes any I/O wait and
preemption. The one CPU measurement is the per-run "process CPU"
(GetProcessTimes, user + kernel), reported per file and summed; a CPU-reduction
claim cites that column, the elapsed table says where the time goes.

    python -I profile_table.py <label> [<label> ...]

Reads out/<label>/profile.tsv - one "# x6profile <audio path> wall=<s> cpu=<s>"
block per extractor run (run-repeat.sh with the p / pe aliases) - and writes
profile-<label>.txt: per-file wall/CPU and phase totals, then the pooled table:
each phase's share of the corpus total, each algorithm's share within its phase,
and the phase remainder (phase total minus its algorithms' process() time =
scheduler, pool storage and setup). With several labels, a side-by-side of the
pooled phase and algorithm seconds (profile-<a>-vs-<b>.txt), which is the
before/after table a cut is judged by.

Pooled seconds are summed over every file, so long real files dominate - the
per-file section shows the same split per file. Standard library only."""
import os
import sys
from collections import OrderedDict

HERE = os.path.dirname(os.path.abspath(__file__))


def read_runs(label):
    """-> list of (audio, wall, cpu, {(phase, name): (instances, calls, seconds)}, peakws)

    peakws is the run's peak working set in MiB (drop 3 header field
    peakws_mib=), or None for a profile written before the field existed."""
    runs = []
    path = os.path.join(HERE, "out", label, "profile.tsv")
    with open(path, encoding="utf-8", errors="replace") as f:
        cur = None
        for line in f:
            line = line.rstrip("\n")
            if line.startswith("# x6profile "):
                head = line[len("# x6profile "):]
                audio, _, rest = head.rpartition(" wall=")
                fields = dict(kv.split("=", 1) for kv in ("wall=" + rest).split(" ") if "=" in kv)
                peak = fields.get("peakws_mib")
                cur = (audio, float(fields["wall"]), float(fields["cpu"]), OrderedDict(),
                       float(peak) if peak is not None else None)
                runs.append(cur)
            elif cur is not None and line and not line.startswith("phase\t"):
                phase, name, inst, calls, secs = line.split("\t")
                cur[3][(phase, name)] = (int(inst), int(calls), float(secs))
    return runs


def norm_path(p):
    """One spelling for a path the manifest writes MSYS-style (/c/<dir>/...) and
    the extractor echoes Windows-style (C:/<dir>/... or with backslashes)."""
    p = p.replace("\\", "/")
    if len(p) > 2 and p[0] == "/" and p[2] == "/" and p[1].isalpha():
        p = p[1].upper() + ":" + p[2:]
    return p.lower()


def id_by_path():
    """manifest row id by normalised path. Every table row is labelled by its
    corpus id: the real-file rows' paths are local and never leave this box
    (manifest.tsv and real-files.tsv are gitignored), so an unmatched path is an
    error here rather than a label."""
    m = {}
    for name in ("manifest.tsv", "manifest-prod.tsv"):   # -prod: make-prod-corpus.sh's converted rows
        path = os.path.join(HERE, name)
        if not os.path.exists(path):
            continue
        with open(path, encoding="utf-8") as f:
            next(f)
            for line in f:
                parts = line.rstrip("\n").split("\t")
                m[norm_path(parts[2])] = parts[0]   # relative rows match the relative label run-repeat.sh passes
    return m


def row_id(ids, audio):
    key = norm_path(audio)
    if key not in ids:
        raise SystemExit("profile_table: no manifest row for %r - the table would carry a local path" % audio)
    return ids[key]


def pooled(runs):
    """-> OrderedDict phase -> {'total': s, 'algos': {name: (inst, calls, s)}}"""
    out = OrderedDict()
    for _, _, _, table, _ in runs:
        for (phase, name), (inst, calls, secs) in table.items():
            p = out.setdefault(phase, {"total": 0.0, "algos": OrderedDict()})
            if name == "[step] total":
                p["total"] += secs
            else:
                i, c, s = p["algos"].get(name, (0, 0, 0.0))
                p["algos"][name] = (max(i, inst), c + calls, s + secs)
    return out


def phase_lines(pool, grand):
    lines = []
    for phase in sorted(pool):
        p = pool[phase]
        algo_sum = sum(s for n, (_, _, s) in p["algos"].items() if not n.startswith("[step] "))
        lines.append("")
        lines.append("%-20s %10.3f s  %5.1f%% of total   (algorithms %.3f s, remainder %.3f s)" % (
            phase, p["total"], 100.0 * p["total"] / grand if grand else 0, algo_sum, p["total"] - algo_sum))
        lines.append("  %-40s %4s %9s %10s %7s %7s" % ("algorithm", "inst", "calls", "elapsed s", "%phase", "%total"))
        for name, (inst, calls, secs) in sorted(p["algos"].items(), key=lambda kv: -kv[1][2]):
            lines.append("  %-40s %4d %9d %10.3f %6.1f%% %6.1f%%" % (
                name, inst, calls, secs, 100.0 * secs / p["total"] if p["total"] else 0,
                100.0 * secs / grand if grand else 0))
    return lines


def write_label(label, only=None):
    runs = read_runs(label)
    if only is not None:
        runs = [r for r in runs if r[0] in only]   # the rows every compared label has
    ids = id_by_path()
    pool = pooled(runs)
    grand = sum(p["total"] for p in pool.values())
    phases = sorted(pool)
    peaks = [r[4] for r in runs if r[4] is not None]
    lines = ["x6 ELAPSED profile: label %s, %d run(s)%s (phase/algorithm values are elapsed; 'process CPU' is the CPU measurement)" % (
                 label, len(runs), " - rows common to the compared labels only" if only is not None else ""),
             "pooled total of phase totals %.3f s elapsed; wall %.3f s; process CPU %.3f s" % (
                 grand, sum(r[1] for r in runs), sum(r[2] for r in runs))]
    if peaks:
        top = max(runs, key=lambda r: r[4] if r[4] is not None else -1)
        lines.append("peak working set (MiB): max %.1f (%s), sum over runs %.1f" % (
            top[4], row_id(ids, top[0]), sum(peaks)))
    # the peak working set column only once a profile carries it (drop 3), so the
    # earlier tables keep their layout
    if peaks:
        lines += ["", "per file (seconds): wall, process CPU (GetProcessTimes), peak working set MiB, then each phase's elapsed total",
                  "%-26s %8s %8s %8s " % ("id", "wall", "cpu", "peakMiB") + " ".join("%12s" % p[:12] for p in phases)]
    else:
        lines += ["", "per file (seconds): wall, process CPU (GetProcessTimes), then each phase's elapsed total",
                  "%-26s %8s %8s " % ("id", "wall", "cpu") + " ".join("%12s" % p[:12] for p in phases)]
    for audio, wall, cpu, table, peak in runs:
        per = OrderedDict((p, 0.0) for p in phases)
        for (phase, name), (_, _, secs) in table.items():
            if name == "[step] total":
                per[phase] += secs
        head = "%-26s %8.3f %8.3f " % (row_id(ids, audio)[:26], wall, cpu)
        if peaks:
            head += "%8s " % ("%.1f" % peak if peak is not None else "-")
        lines.append(head + " ".join("%12.3f" % per[p] for p in phases))
    lines += ["", "pooled over all runs"] + phase_lines(pool, grand)
    text = "\n".join(lines) + "\n"
    with open(os.path.join(HERE, "profile-%s.txt" % label), "w", encoding="utf-8") as f:
        f.write(text)
    return pool, grand, text, (max(peaks) if peaks else None)


def write_compare(labels, pools, grands, peaks):
    lines = ["x6 ELAPSED profile, pooled seconds side by side: " + "  ".join(labels),
             "delta = last minus first; negative is time removed (elapsed; see each label's process CPU in its own table)", ""]
    names = OrderedDict()
    for pool in pools:
        for phase in sorted(pool):
            names.setdefault((phase, "[phase total]"), None)
            for n in pool[phase]["algos"]:
                names.setdefault((phase, n), None)
    lines.append("%-20s %-40s " % ("phase", "algorithm") + " ".join("%12s" % l[:12] for l in labels) + " %12s" % "delta")
    for phase, name in names:
        vals = []
        for pool in pools:
            p = pool.get(phase)
            if p is None:
                vals.append(0.0)
            elif name == "[phase total]":
                vals.append(p["total"])
            else:
                vals.append(p["algos"].get(name, (0, 0, 0.0))[2])
        lines.append("%-20s %-40s " % (phase, name) + " ".join("%12.3f" % v for v in vals) + " %+12.3f" % (vals[-1] - vals[0]))
    lines += ["", "%-20s %-40s " % ("ALL", "grand total") + " ".join("%12.3f" % g for g in grands) + " %+12.3f" % (grands[-1] - grands[0])]
    if all(p is not None for p in peaks):
        lines.append("%-20s %-40s " % ("ALL", "peak working set MiB, max over runs") + " ".join("%12.1f" % p for p in peaks) + " %+12.1f" % (peaks[-1] - peaks[0]))
    text = "\n".join(lines) + "\n"
    with open(os.path.join(HERE, "profile-%s-vs-%s.txt" % (labels[0], labels[-1])), "w", encoding="utf-8") as f:
        f.write(text)
    return text


def main(argv):
    if not argv:
        print(__doc__)
        return 2
    pools, grands, peaks = [], [], []
    only = None
    if len(argv) > 1:   # compare like with like: only the rows every label ran
        sets = [set(r[0] for r in read_runs(label)) for label in argv]
        only = set.intersection(*sets)
    for label in argv:
        pool, grand, text, peak = write_label(label, only)
        pools.append(pool)
        grands.append(grand)
        peaks.append(peak)
        sys.stdout.write(text)
    if len(argv) > 1:
        sys.stdout.write(write_compare(argv, pools, grands, peaks))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
