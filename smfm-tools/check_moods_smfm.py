#!/usr/bin/env python3
"""Report SMFM coverage inside a truedat mbxmoods.json — BOTH directions.

Per audio extension: how many track entries have smfmScores populated vs not
(reads the legacy sensmeScores key as fallback for un-migrated files).

Writes two CSVs next to this script (or into <out_dir> if given):

  smfm-present.csv  the files that HAVE SMFM, with the decoded values
                    (path,ext,artist,title,album,smfmBpm,smfmChannel,
                     topScore,scores)
  smfm-missing.csv  the files that LACK it — rescan / Sony-tagging candidates
                    (path,ext,artist,title,album)

Path is column 1 in both, so `cut -d, -f1` still gives a plain path list
(this replaces the old smfm-missing.txt).

By default `missing` counts only SMFM-CONTAINER extensions (flac/mp3/wma) —
the formats Sony's tagger can actually write into. Pass --all to list every
entry without SMFM regardless of container.

truedat only READS SMFM; nothing in it writes SMFM into a file. So a missing
row is a Sony-tagging gap, not a truedat gap — rescanning will not close it.

Unofficial tooling — see SMFM-KNOWLEDGE.md for what the values mean.
Usage: python check_moods_smfm.py <path\\to\\mbxmoods.json> [out_dir] [--all]
"""
import csv, json, os, sys, collections

args = [a for a in sys.argv[1:] if not a.startswith('--')]
flags = {a for a in sys.argv[1:] if a.startswith('--')}
if not args:
    sys.exit('Usage: check_moods_smfm.py <path/to/mbxmoods.json> [out_dir] [--all]')
MOODS = args[0]
OUT_DIR = args[1] if len(args) > 1 else os.path.dirname(os.path.abspath(__file__))
ALL_EXTS = '--all' in flags

OUT_PRESENT = os.path.join(OUT_DIR, 'smfm-present.csv')
OUT_MISSING = os.path.join(OUT_DIR, 'smfm-missing.csv')

SMFM_EXTS = {'.flac', '.mp3', '.wma'}

print(f"loading {MOODS} ...", flush=True)
with open(MOODS, 'r', encoding='utf-8') as f:
    data = json.load(f)

tracks = data.get('tracks', {})
print(f"trackCount header={data.get('trackCount')}  parsed={len(tracks)}", flush=True)

# per ext: [total, has_smfm, has_smfmbpm]
stat = collections.defaultdict(lambda: [0, 0, 0])
present = []   # files carrying SMFM, with decoded values
missing = []   # files lacking smfmScores (containers only unless --all)
slot_hist = collections.Counter()


def meta(e):
    """artist/title/album are top-level keys on a truedat track entry."""
    return (e.get('artist') or '', e.get('title') or '', e.get('album') or '')


for path, e in tracks.items():
    ext = os.path.splitext(path)[1].lower()
    s = stat[ext]
    s[0] += 1
    sm = e.get('smfmScores')
    if sm is None:
        sm = e.get('sensmeScores')  # legacy key, pre --migrate
    has_smfm = isinstance(sm, list) and len(sm) > 0 and any(v for v in sm)
    if has_smfm:
        s[1] += 1
        slot = e.get('smfmChannel')
        if slot is None:
            slot = e.get('sensmeChannel')  # legacy key
        slot_hist[slot] += 1
        artist, title, album = meta(e)
        present.append((path, ext, artist, title, album,
                        e.get('smfmBpm'), slot, max(sm),
                        ' '.join(str(v) for v in sm)))
    elif ALL_EXTS or ext in SMFM_EXTS:
        artist, title, album = meta(e)
        missing.append((path, ext, artist, title, album))
    if e.get('smfmBpm') is not None:
        s[2] += 1

present.sort(key=lambda r: r[0].lower())
missing.sort(key=lambda r: r[0].lower())

print(f"\n{'ext':<8}{'entries':>9}{'smfm':>9}{'cover%':>9}{'smfmBpm':>9}")
pt = ps = 0
for ext in sorted(stat):
    tot, smfm, bpm = stat[ext]
    tag = ' (SMFM container)' if ext in SMFM_EXTS else ''
    if ext in SMFM_EXTS:
        pt += tot; ps += smfm
    cov = (smfm/tot*100) if tot else 0
    print(f"{ext:<8}{tot:>9}{smfm:>9}{cov:>8.1f}%{bpm:>9}{tag}")

print(f"\nSMFM-container entries (flac+mp3+wma): {ps}/{pt} have SMFM = {(ps/pt*100 if pt else 0):.1f}%")
print(f"Have SMFM (all extensions): {len(present)}")
print(f"Missing SMFM ({'all extensions' if ALL_EXTS else 'SMFM containers only'}): {len(missing)}")

# smfmChannel is the argmax SLOT of the 10 mood-model scores — NOT a SensMe
# channel (see SMFM-KNOWLEDGE.md); histogram is diagnostic only.
print(f"\nsmfmChannel (argmax slot) distribution:")
for slot in sorted(slot_hist, key=lambda x: (x is None, x)):
    print(f"  slot {slot}: {slot_hist[slot]:>7}")


def write_csv(path, header, rows):
    with open(path, 'w', encoding='utf-8', newline='') as fh:
        w = csv.writer(fh)
        w.writerow(header)
        w.writerows(rows)
    print(f"wrote {len(rows):>7} rows -> {path}")


print()
write_csv(OUT_PRESENT,
          ['path', 'ext', 'artist', 'title', 'album',
           'smfmBpm', 'smfmChannel', 'topScore', 'scores'],
          present)
write_csv(OUT_MISSING,
          ['path', 'ext', 'artist', 'title', 'album'],
          missing)
