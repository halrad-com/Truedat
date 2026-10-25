#!/usr/bin/env python3
"""What TODAY's production-shaped extractor call writes to truedat's stage
directory, per audio-hour - the SSD traffic a scan already causes.

    python -I stage_bytes.py <label> [<profile yaml>]

<label> is a run-repeat.sh pass in the production call shape (manifest-prod
rows, scan-edges profile, --frames-bin), e.g. step 0's p0a on drop 2. For each
real row it counts the files a scan writes into the stage dir for that call:
  - the extractor's temp JSON           out/<label>/<id>.json
  - the frames file (--frames-bin)      out/<label>/<id>.bin
  - the profile truedat writes per call  the profile file's size
  - the resample-once 44.1 kHz f32 WAV   out/prod/<id>.wav, hi-res sources only
and, separately, the staging COPY of the source file that truedat makes for a
network (UNC / mapped) library - one full file per track - which depends on
where the library lives. Audio length is the extractor's own
metadata.audio_properties.length. Writes stage-bytes.txt. Standard library only."""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))


def manifest(name):
    rows = {}
    with open(os.path.join(HERE, name), encoding="utf-8") as f:
        next(f)
        for line in f:
            p = line.rstrip("\n").split("\t")
            rows[p[0]] = p
    return rows


def size(path):
    return os.path.getsize(path) if os.path.exists(path) else 0


def main(argv):
    if not argv:
        print(__doc__)
        return 2
    label = argv[0]
    profile = argv[1] if len(argv) > 1 else os.path.join(HERE, "scan-edges-profile.yaml")
    prod, orig = manifest("manifest-prod.tsv"), manifest("manifest.tsv")
    psize = size(profile)
    lines = ["stage-dir bytes per scan call, label %s (production call shape), profile file %d B" % (label, psize),
             "", "%-14s %9s %10s %11s %11s %13s %13s" % ("id", "audio s", "json B", "frames B", "wav B", "per audio-h", "source B")]
    tot = {"len": 0.0, "json": 0, "bin": 0, "wav": 0, "prof": 0, "src": 0, "len_hires": 0.0}
    for rid, p in prod.items():
        if p[1] != "real" or p[3] != "decode":
            continue
        j = os.path.join(HERE, "out", label, rid + ".json")
        if not os.path.exists(j):
            continue
        with open(j, encoding="utf-8") as f:
            length = float(json.load(f)["metadata"]["audio_properties"]["length"])
        js, bs = size(j), size(os.path.join(HERE, "out", label, rid + ".bin"))
        converted = p[4].startswith("prod: converted")
        ws = size(os.path.join(HERE, "out", "prod", rid + ".wav")) if converted else 0
        src_path = orig[rid][2]
        if len(src_path) > 2 and src_path[0] == "/" and src_path[2] == "/":
            src_path = src_path[1].upper() + ":" + src_path[2:]
        ss = size(src_path)
        per_h = (js + bs + psize + ws) * 3600.0 / length if length else 0
        lines.append("%-14s %9.1f %10d %11d %11d %10.1f MB %10.1f MB" % (rid, length, js, bs, ws, per_h / 1e6, ss / 1e6))
        tot["len"] += length
        tot["json"] += js
        tot["bin"] += bs
        tot["wav"] += ws
        tot["prof"] += psize
        tot["src"] += ss
        if converted:
            tot["len_hires"] += length
    h = tot["len"] / 3600.0
    lines += ["",
              "pooled over %.2f audio-hours of real rows:" % h,
              "  temp JSON      %8.1f MB per audio-hour" % (tot["json"] / h / 1e6),
              "  frames file    %8.1f MB per audio-hour" % (tot["bin"] / h / 1e6),
              "  profile file   %8.3f MB per audio-hour" % (tot["prof"] / h / 1e6),
              "  44.1k WAV      %8.1f MB per audio-hour of HI-RES source (%.1f MB per audio-hour pooled over these rows)" % (
                  tot["wav"] / (tot["len_hires"] / 3600.0) / 1e6 if tot["len_hires"] else 0, tot["wav"] / h / 1e6),
              "  => every 44.1k track: %.1f MB per audio-hour; a hi-res track adds the WAV" % ((tot["json"] + tot["bin"] + tot["prof"]) / h / 1e6),
              "  staging copy of the source (network libraries only): %.1f MB per audio-hour at these rows' bit rates" % (tot["src"] / h / 1e6)]
    text = "\n".join(lines) + "\n"
    with open(os.path.join(HERE, "stage-bytes.txt"), "w", encoding="utf-8") as f:
        f.write(text)
    sys.stdout.write(text)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
