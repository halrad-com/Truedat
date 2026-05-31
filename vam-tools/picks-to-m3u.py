#!/usr/bin/env python3
"""Convert seed-autocal-from-extremes.py output into a single auto-cal.m3u
playlist.

Closes the gap between picking corner candidates and loading them into a
player so you can audition + annotate them. The annotate / tune pages in
MBXHub drive playback through MusicBee (or whatever player has the file
queued), so the practical step is "give MusicBee a playlist of the picks".

Reads `auto-cal-seed.json` and writes `auto-cal.m3u` containing all four
quadrants in happy/angry/calm/sad order, grouped via #EXTGRP. Paths are
emitted verbatim from the picks file — UTF-8, absolute, separator as
recorded.

Usage:
  python picks-to-m3u.py --picks auto-cal-seed.json [--out auto-cal.m3u]
"""

from __future__ import annotations
import argparse, io, json, os, sys


QUADRANTS = ("happy", "angry", "calm", "sad")


def load_picks(path: str) -> dict:
    with io.open(path, "r", encoding="utf-8-sig") as f:
        return json.load(f)


def main():
    ap = argparse.ArgumentParser(
        description="Convert auto-cal-seed.json into a single auto-cal.m3u playlist."
    )
    ap.add_argument("--picks", required=True,
                    help="Path to auto-cal-seed.json (output of seed-autocal-from-extremes.py).")
    ap.add_argument("--out", default="auto-cal.m3u",
                    help="Output playlist path (default: auto-cal.m3u).")
    args = ap.parse_args()

    if not os.path.exists(args.picks):
        print(f"error: --picks file not found: {args.picks}", file=sys.stderr)
        sys.exit(2)

    data = load_picks(args.picks)
    picks = data.get("picks") or {}
    if not picks:
        print("error: input file has no 'picks' object", file=sys.stderr)
        sys.exit(1)

    out_dir = os.path.dirname(os.path.abspath(args.out))
    os.makedirs(out_dir, exist_ok=True)

    n_total = 0
    with io.open(args.out, "w", encoding="utf-8", newline="\n") as f:
        f.write("#EXTM3U\n")
        for q in QUADRANTS:
            rows = picks.get(q) or []
            if not rows:
                continue
            f.write(f"#EXTGRP:{q}\n")
            for r in rows:
                p = r.get("path")
                if not p:
                    continue
                dur = r.get("durationMs")
                dur_s = -1 if not isinstance(dur, (int, float)) or dur <= 0 else int(round(dur / 1000))
                artist = (r.get("artist") or "").strip() or "?"
                title = (r.get("title") or "").strip() or os.path.basename(p)
                f.write(f"#EXTINF:{dur_s},{artist} - {title}\n")
                f.write(f"{p}\n")
                n_total += 1
    print(f"wrote {n_total} tracks ({', '.join(q for q in QUADRANTS if picks.get(q))}) → {args.out}",
          file=sys.stderr)


if __name__ == "__main__":
    main()
