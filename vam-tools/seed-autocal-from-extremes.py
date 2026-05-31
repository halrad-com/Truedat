#!/usr/bin/env python3
"""Pick analytical Auto-Cal corner candidates from mbxmoods.json.

Companion to tools/calibrate-valence-arousal.py and tools/extract-sony-sensme.py.
Generates a quadrant-balanced set of "extreme" tracks for VAM Auto-Cal seeding
using raw Essentia features only — deliberately independent of the current
AutoQ formula and embedded model. The output is intended to drive a
hand-annotated v1.2 comparison run against the user's hand-picked v1.0 / v1.1
Auto-Cal sets.

Scoring composites (z-scored across the corpus):
  arousal  = z(loudness) + z(bpm) + z(onsetRate)
  valence  = mode_sign + z(chordsStrength) - z(dissonance)
             where mode_sign = +1 if mode == "major" else -1 if mode == "minor" else 0

Quadrants on composite signs:
  happy : v >= 0, a >= 0    corner = (+max, +max)
  angry : v <  0, a >= 0    corner = (-max, +max)
  calm  : v >= 0, a <  0    corner = (+max, -max)
  sad   : v <  0, a <  0    corner = (-max, -max)

Within each quadrant, rank by Euclidean distance from that quadrant's corner
(most extreme first) and artist-dedupe so a single artist can't dominate one
corner.

Outputs:
  * Summary to stdout (per-quadrant pool sizes, distance spread, top picks).
  * JSON to --out with the full picks list per quadrant.
"""

from __future__ import annotations
import argparse, io, json, math, os, re, statistics, sys
from collections import defaultdict


# Filters for tracks that have Essentia features but aren't real music.
# Test corpora, loops, and field recordings inflate the eligible pool with
# items the user would never want as anchors.
NON_MUSIC_GENRE_RE = re.compile(r"^(test|sample|loop|samples)$", re.IGNORECASE)
NON_MUSIC_PATH_FRAGMENTS = ("_truedat-corpus", "_test-corpus", "/samples/", "\\samples\\")
UNKNOWN_ARTIST_NAMES = {"", "unknown", "unknown artist", "various artists"}


# Features used for the two composites. Kept in two short lists so the
# pickers above are auditable at a glance.
AROUSAL_FEATURES = ("loudness", "bpm", "onsetRate")
VALENCE_NUMERIC_FEATURES = ("chordsStrength", "dissonance")

# Minimum playable duration for an Auto-Cal anchor. Excludes interludes,
# silence cuts, and very short voice memos that ride along in some libraries.
# 45 s matches the annotate-page lead-in window.
MIN_DURATION_MS = 45_000


def load_moods(path: str) -> dict:
    """Load mbxmoods.json; tolerate UTF-8 BOM the way the sibling scripts do."""
    with io.open(path, "r", encoding="utf-8-sig") as f:
        return json.load(f)


def coerce_float(v):
    if v is None:
        return None
    try:
        f = float(v)
    except (TypeError, ValueError):
        return None
    if math.isnan(f) or math.isinf(f):
        return None
    return f


def zscore_map(values):
    """Return (mean, stdev) of a finite-only sequence, or (0, 1) if degenerate.

    Degenerate stdev → returning 1.0 collapses z to (x - mean), which keeps
    the composite math sane without raising a divide-by-zero.
    """
    finite = [v for v in values if v is not None]
    if len(finite) < 2:
        return 0.0, 1.0
    m = statistics.fmean(finite)
    s = statistics.pstdev(finite)
    return m, (s if s > 1e-9 else 1.0)


def duration_ms(entry) -> int | None:
    """Pull duration from the chromaprint or fingerprint side-channel; both
    fields are commonly populated. Returns None if neither is available."""
    fp = entry.get("fingerprint.v1") or {}
    d = fp.get("durationMs")
    if isinstance(d, (int, float)) and d > 0:
        return int(d)
    cd = entry.get("chromaprintDuration")
    if isinstance(cd, (int, float)) and cd > 0:
        return int(cd * 1000)
    return None


def quadrant_of(v: float, a: float) -> str:
    if a >= 0:
        return "happy" if v >= 0 else "angry"
    return "calm" if v >= 0 else "sad"


# Corner = sign of the quadrant. Distance is measured in normalized
# (z-score) space already, so corner coordinates are just unit signs that
# the score is being pushed toward.
CORNER = {
    "happy": (+1.0, +1.0),
    "angry": (-1.0, +1.0),
    "calm":  (+1.0, -1.0),
    "sad":   (-1.0, -1.0),
}


def is_non_music(path: str, artist: str, genre: str) -> str | None:
    """Return a reason string when the row should be dropped, else None.
    Reasons surface in the per-quadrant drop tally so a user can audit
    which filters did what."""
    if (artist or "").strip().lower() in UNKNOWN_ARTIST_NAMES:
        return "unknown-artist"
    if genre and NON_MUSIC_GENRE_RE.match(genre.strip()):
        return "non-music-genre"
    p = (path or "").lower()
    for frag in NON_MUSIC_PATH_FRAGMENTS:
        if frag in p:
            return "non-music-path"
    return None


def build_population(tracks):
    """Extract feature vectors for every track that has the required fields,
    along with the metadata we'll need for output. Tracks missing any
    composite input are dropped (no fallback — they couldn't be ranked).
    Test / loop / unknown-artist rows are filtered up front so they can't
    eat picks downstream.
    """
    rows = []
    drops = defaultdict(int)
    for path, entry in tracks.items():
        feats = {}
        ok = True
        for k in AROUSAL_FEATURES + VALENCE_NUMERIC_FEATURES:
            v = coerce_float(entry.get(k))
            if v is None:
                ok = False; break
            feats[k] = v
        if not ok:
            drops["missing-feature"] += 1
            continue
        mode = entry.get("mode")
        if mode == "major":
            mode_sign = +1.0
        elif mode == "minor":
            mode_sign = -1.0
        else:
            mode_sign = 0.0
        dur = duration_ms(entry)
        if dur is not None and dur < MIN_DURATION_MS:
            drops["short-duration"] += 1
            continue
        artist = entry.get("artist") or ""
        genre = entry.get("genre") or ""
        reason = is_non_music(path, artist, genre)
        if reason is not None:
            drops[reason] += 1
            continue
        rows.append({
            "path": path,
            "artist": artist,
            "title": entry.get("title") or "",
            "album": entry.get("album") or "",
            "genre": genre,
            "mode": mode or "",
            "duration_ms": dur,
            "feats": feats,
            "mode_sign": mode_sign,
        })
    return rows, drops


def score_population(rows):
    """Compute z-scored composites for every row. Returns rows in place with
    `arousal` and `valence` keys added."""
    if not rows:
        return rows
    norm = {}
    for k in AROUSAL_FEATURES + VALENCE_NUMERIC_FEATURES:
        norm[k] = zscore_map(r["feats"][k] for r in rows)
    for r in rows:
        za = 0.0
        for k in AROUSAL_FEATURES:
            m, s = norm[k]
            za += (r["feats"][k] - m) / s
        zv = r["mode_sign"]
        for k, sign in (("chordsStrength", +1.0), ("dissonance", -1.0)):
            m, s = norm[k]
            zv += sign * (r["feats"][k] - m) / s
        r["arousal"] = za
        r["valence"] = zv
        r["quadrant"] = quadrant_of(zv, za)
    return rows


def pick_corners(rows, per_quadrant: int, artist_dedupe: bool):
    """Per quadrant: sort by distance from the quadrant corner in
    composite-z space, descending (farthest from origin in the quadrant
    direction first), apply artist-dedupe, take per_quadrant rows."""
    buckets = defaultdict(list)
    for r in rows:
        buckets[r["quadrant"]].append(r)

    picks = {}
    for q in ("happy", "angry", "calm", "sad"):
        # Rank by extremeness in BOTH axes. Quadrant membership already
        # locks the signs, so |v| * |a| is the natural score — a pick has
        # to be far from origin on BOTH axes to score well, not just one.
        # Earlier dot-product rank let single-axis extremes (e.g. very
        # positive valence + near-zero arousal) sit at the top; geometric
        # rank penalizes that and demands true corner-ness.
        scored = sorted(
            buckets[q],
            key=lambda r: abs(r["valence"]) * abs(r["arousal"]),
            reverse=True,
        )
        kept = []
        seen_artists = set()
        for r in scored:
            if artist_dedupe:
                key = (r["artist"] or "").strip().lower()
                if key and key in seen_artists:
                    continue
                if key:
                    seen_artists.add(key)
            kept.append(r)
            if len(kept) >= per_quadrant:
                break
        picks[q] = kept
    return buckets, picks


def fmt_pick(r):
    return (
        f"  v={r['valence']:+5.2f} a={r['arousal']:+5.2f}  "
        f"{r['artist'] or '(unknown)'} — {r['title'] or '(untitled)'}  "
        f"[{r['genre'] or '?'}]"
    )


def main():
    ap = argparse.ArgumentParser(
        description="Pick quadrant-balanced extreme tracks from mbxmoods.json "
                    "for VAM Auto-Cal seeding (analytical / model-independent)."
    )
    ap.add_argument("--moods", required=True,
                    help="Path to mbxmoods.json (truedat output).")
    ap.add_argument("--per-quadrant", type=int, default=25,
                    help="Picks per quadrant. Default 25 (=100 total).")
    ap.add_argument("--no-artist-dedupe", action="store_true",
                    help="Do not dedupe by artist inside a quadrant. "
                         "Off by default so one artist can't dominate a corner.")
    ap.add_argument("--out", default="auto-cal-seed.json",
                    help="Output JSON path (default: auto-cal-seed.json).")
    ap.add_argument("--show-top", type=int, default=5,
                    help="How many picks per quadrant to echo to stdout.")
    args = ap.parse_args()

    if not os.path.exists(args.moods):
        print(f"error: --moods file not found: {args.moods}", file=sys.stderr)
        sys.exit(2)

    print(f"Loading {args.moods} …", file=sys.stderr)
    data = load_moods(args.moods)
    tracks = data.get("tracks") or {}
    print(f"Loaded {len(tracks):,} entries (manifest trackCount={data.get('trackCount')}).",
          file=sys.stderr)

    rows, drops = build_population(tracks)
    print(f"Eligible (music, full features, duration ≥ "
          f"{MIN_DURATION_MS/1000:.0f}s): {len(rows):,}", file=sys.stderr)
    if drops:
        print("Drops:", file=sys.stderr)
        for k, n in sorted(drops.items(), key=lambda kv: -kv[1]):
            print(f"  {k:>18s}  {n:>6,}", file=sys.stderr)

    if not rows:
        print("error: no eligible tracks; nothing to pick.", file=sys.stderr)
        sys.exit(1)

    score_population(rows)
    buckets, picks = pick_corners(
        rows,
        per_quadrant=args.per_quadrant,
        artist_dedupe=not args.no_artist_dedupe,
    )

    # ── Summary ──
    print()
    print("Quadrant pool sizes (eligible candidates per corner):")
    for q in ("happy", "angry", "calm", "sad"):
        bucket_n = len(buckets.get(q, []))
        kept_n = len(picks.get(q, []))
        print(f"  {q:6s}  pool={bucket_n:>6,}  picked={kept_n}")

    if args.show_top > 0:
        for q in ("happy", "angry", "calm", "sad"):
            print()
            print(f"-- {q} (top {min(args.show_top, len(picks[q]))} of {len(picks[q])}) --")
            for r in picks[q][: args.show_top]:
                print(fmt_pick(r))

    # ── Write JSON ──
    out_obj = {
        "generatedFrom": os.path.abspath(args.moods),
        "manifestTrackCount": data.get("trackCount"),
        "eligibleCount": len(rows),
        "perQuadrant": args.per_quadrant,
        "artistDedupe": not args.no_artist_dedupe,
        "minDurationMs": MIN_DURATION_MS,
        "composites": {
            "arousalFeatures": list(AROUSAL_FEATURES),
            "valenceNumericFeatures": list(VALENCE_NUMERIC_FEATURES),
            "valenceModeSign": "+1 major / -1 minor / 0 unknown",
        },
        "picks": {
            q: [
                {
                    "path": r["path"],
                    "artist": r["artist"],
                    "title": r["title"],
                    "album": r["album"],
                    "genre": r["genre"],
                    "mode": r["mode"],
                    "durationMs": r["duration_ms"],
                    "valence": round(r["valence"], 4),
                    "arousal": round(r["arousal"], 4),
                }
                for r in picks[q]
            ]
            for q in ("happy", "angry", "calm", "sad")
        },
    }
    with io.open(args.out, "w", encoding="utf-8") as f:
        json.dump(out_obj, f, ensure_ascii=False, indent=2)
    print()
    print(f"Wrote {sum(len(v) for v in picks.values())} picks to {args.out}",
          file=sys.stderr)


if __name__ == "__main__":
    main()
