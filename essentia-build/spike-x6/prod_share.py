#!/usr/bin/env python3
"""x7 step 0: what a shared decode could recover, on production-shaped input.

    python -I prod_share.py <label> [<label> ...]

Reads out/<label>/profile.tsv (x6profile, run-repeat.sh .prof.*.prod) and each
row's out/<label>/<id>.json (audio length), and writes step0-decode.txt.

The extractor decodes the file four times, once per phase:
  2 audio_metadata  AudioLoader (stereo, packet MD5) -> StereoDemuxer -> Resample x2 -> StereoMuxer -> StereoTrimmer -> EBU R128
  3 replay_gain     AudioLoader -> MonoMixer -> Resample -> (Trimmer, Scale) -> EqualLoudness -> ReplayGain
  4 network1        AudioLoader -> MonoMixer -> Resample -> Trimmer -> Scale(replay gain) -> the descriptor graph
  5 network2        the same loader chain as network1, again -> beats loudness, tonal
A shared decode keeps ONE decode (phase 2's, which also hashes the packets) and
ONE mono mix + resample (phase 3's), spools them, and has the other passes read
the spool. So the GROSS recoverable work is, per row:
  AudioLoader of phases 3, 4, 5 + MonoMixer and Resample of phases 4, 5
  + Trimmer and Scale of phase 5 (network2's loader chain is network1's, same
    parameters; phase 3's Trimmer/Scale are not counted - its gain differs)
EqualLoudness is NOT counted: phase 3 filters the un-gained mono signal and
network1 the replay-gain-scaled one, so the inputs differ and a shared result
would not be byte-identical. The phases' scheduler remainder is not counted
either (unattributable from these tables).
NET = gross minus the spool read-back: the three passes that would read the
spool are priced at the loader's own elapsed per audio-second on the rows whose
input already IS a 44.1 kHz f32 WAV (the hi-res rows after truedat's
conversion) - a stereo f32 read through the loader, an upper bound for a mono
spool read. Standard library only."""
import json
import os
import sys
from collections import OrderedDict

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)   # -I drops the script dir; profile_table is this dir's own
import profile_table as pt  # noqa: E402

LOADER = "AudioLoader"
PHASES = ["2 audio_metadata", "3 replay_gain", "4 network1", "5 network2"]
SHOW = ["AudioLoader", "MonoMixer", "Resample", "Trimmer", "Scale", "EqualLoudness",
        "StereoDemuxer", "StereoMuxer", "StereoTrimmer"]


def secs(table, phase, name):
    return sum(s for (p, n), (_, _, s) in table.items() if p == phase and n == name)


def phase_total(table, phase):
    return sum(s for (p, n), (_, _, s) in table.items() if p == phase and n == "[step] total")


def gross(table):
    g = sum(secs(table, p, LOADER) for p in PHASES[1:])
    for p in PHASES[2:]:
        g += secs(table, p, "MonoMixer") + secs(table, p, "Resample")
    g += secs(table, "5 network2", "Trimmer") + secs(table, "5 network2", "Scale")
    return g


def length_of(label, rid):
    with open(os.path.join(HERE, "out", label, rid + ".json"), encoding="utf-8") as f:
        return float(json.load(f)["metadata"]["audio_properties"]["length"])


def notes_by_id():
    m = {}
    with open(os.path.join(HERE, "manifest-prod.tsv"), encoding="utf-8") as f:
        next(f)
        for line in f:
            p = line.rstrip("\n").split("\t")
            m[p[0]] = p[4]
    return m


def codec_of(rid):
    probe = os.path.join(HERE, "corpus", "probe", rid + ".json")
    try:
        with open(probe, encoding="utf-8") as f:
            return json.load(f)["streams"][0]["codec_name"]
    except (OSError, ValueError, KeyError, IndexError):
        return "?"


def analyse(label):
    ids = pt.id_by_path()
    notes = notes_by_id()
    rows = []
    for audio, wall, cpu, table, peak in pt.read_runs(label):
        rid = pt.row_id(ids, audio)
        total = sum(phase_total(table, p) for p in set(p for (p, _) in table))
        rows.append(OrderedDict(
            id=rid, real=rid.startswith("r_"), converted=notes[rid].startswith("prod: converted"),
            codec=codec_of(rid), length=length_of(label, rid), cpu=cpu, total=total,
            gross=gross(table), table=table))
    # the spool read price: loader elapsed per audio-second, pooled over the
    # rows whose extractor input is the converted 44.1k f32 WAV, all four phases
    conv = [r for r in rows if r["converted"]]
    read_s = sum(secs(r["table"], p, LOADER) for r in conv for p in PHASES)
    read_len = 4 * sum(r["length"] for r in conv)
    price = read_s / read_len if read_len else 0.0
    for r in rows:
        r["net"] = r["gross"] - 3 * price * r["length"]
    return rows, price


def pooled(rows, sel):
    rs = [r for r in rows if sel(r)]
    t = sum(r["total"] for r in rs)
    return (len(rs), t, sum(r["cpu"] for r in rs), sum(r["gross"] for r in rs), sum(r["net"] for r in rs))


def main(labels):
    stem = None   # --out <stem>: write decode-share-<stem>.txt instead of step0-decode.txt
    if "--out" in labels:
        i = labels.index("--out")
        stem = labels[i + 1]
        labels = labels[:i] + labels[i + 2:]
    if not labels:
        print(__doc__)
        return 2
    out = ["x7 step 0 - shared-decode recoverable share on production-shaped input",
           "input: manifest-prod.tsv (rows above 44.1 kHz converted with truedat's own ffmpeg soxr command first, as a scan does)",
           "call: scan-edges profile + --frames-bin, ESSENTIA_PROFILE on; values are ELAPSED per process() call, 'cpu' is process CPU",
           "rule (see prod_share.py): gross = AudioLoader x3 + MonoMixer/Resample x2 + network2's Trimmer/Scale; net = gross - 3 spool reads",
           ""]
    for label in labels:
        rows, price = analyse(label)
        out.append("== %s: %d rows; spool read priced at %.5f s per audio-second (loader on the converted f32 WAV rows) ==" % (
            label, len(rows), price))
        out.append("%-26s %-12s %4s %8s %9s %9s %9s %9s %7s %7s" % (
            "id", "codec", "conv", "len s", "cpu s", "total s", "gross s", "net s", "gross%", "net%"))
        for r in rows:
            out.append("%-26s %-12s %4s %8.2f %9.3f %9.3f %9.3f %9.3f %6.2f%% %6.2f%%" % (
                r["id"], r["codec"][:12], "yes" if r["converted"] else "", r["length"], r["cpu"], r["total"],
                r["gross"], r["net"], 100 * r["gross"] / r["total"] if r["total"] else 0,
                100 * r["net"] / r["total"] if r["total"] else 0))
        out.append("")
        out.append("pooled (share of the pooled phase totals):")
        for name, sel in (("all rows", lambda r: True), ("real rows", lambda r: r["real"]),
                          ("real rows, 44.1k as is", lambda r: r["real"] and not r["converted"]),
                          ("rows converted from hi-res", lambda r: r["converted"]),
                          ("synthetic rows", lambda r: not r["real"])):
            n, t, c, g, nt = pooled(rows, sel)
            if n:
                out.append("  %-28s n=%2d total %9.3f s  cpu %9.3f s  gross %8.3f s = %5.2f%%  net %8.3f s = %5.2f%%" % (
                    name, n, t, c, g, 100 * g / t, nt, 100 * nt / t))
        out.append("")
        out.append("per phase, pooled over all rows (elapsed s):")
        out.append("  %-18s " % "algorithm" + " ".join("%16s" % p for p in PHASES))
        for name in SHOW:
            vals = [sum(secs(r["table"], p, name) for r in rows) for p in PHASES]
            if any(vals):
                out.append("  %-18s " % name + " ".join("%16.3f" % v for v in vals))
        out.append("  %-18s " % "[phase total]" + " ".join("%16.3f" % sum(phase_total(r["table"], p) for r in rows) for p in PHASES))
        out.append("")
        # the tonal items: the three HPCP instances, and the price of the three
        # tuning-system fields (the 120-bin HPCP exists only for them; their
        # pool storage is in network2's PoolStorage row and not separable here)
        grand = sum(r["total"] for r in rows)
        real = [r for r in rows if r["real"]]
        greal = sum(r["total"] for r in real)
        out.append("tonal (network2), elapsed s and share of the pooled total - all rows | real rows:")
        for name in ("HPCP[size=36]", "HPCP[size=120]", "[step] computeTuningSystemFeatures"):
            a = sum(secs(r["table"], "5 network2", name) for r in rows)
            b = sum(secs(r["table"], "5 network2", name) for r in real)
            out.append("  %-36s %8.3f s %5.2f%% | %8.3f s %5.2f%%" % (name, a, 100 * a / grand, b, 100 * b / greal))
        a = sum(secs(r["table"], "5 network2", "HPCP[size=120]") + secs(r["table"], "5 network2", "[step] computeTuningSystemFeatures") for r in rows)
        b = sum(secs(r["table"], "5 network2", "HPCP[size=120]") + secs(r["table"], "5 network2", "[step] computeTuningSystemFeatures") for r in real)
        out.append("  %-36s %8.3f s %5.2f%% | %8.3f s %5.2f%%" % ("tuning-system fields (120-bin + step)", a, 100 * a / grand, b, 100 * b / greal))
        out.append("")
    text = "\n".join(out) + "\n"
    with open(os.path.join(HERE, "decode-share-%s.txt" % stem if stem else "step0-decode.txt"), "w", encoding="utf-8") as f:
        f.write(text)
    sys.stdout.write(text)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
