#!/usr/bin/env python3
"""Report SMFM coverage inside a truedat mbxmoods.json - all three states.

An entry is in exactly one state, matching truedat's own ClassifySmfm so the
two reporters cannot disagree about what "has SMFM" means:

  data     block present, at least one non-zero score - usable
  no-data  block present, every score zero - Sony wrote the block and scored
           nothing. NOT an untagged file: re-running the tagger is the fix for
           no-smfm and is exactly what has already been tried here
  no-smfm  no block at all - never through Sony's tagger

Reads the legacy sensmeScores/sensmeChannel keys as a fallback for
un-migrated catalogs.

Writes three CSVs next to this script (or into <out_dir> if given), one per
state, each: path,ext,artist,title,album,smfmBpm,smfmChannel,topScore,scores

  smfm-data.csv     smfm-no-data.csv     smfm-no-smfm.csv

Path is column 1 in all three, so `cut -d, -f1` gives a plain path list
(this replaces the old smfm-missing.txt).

By default the no-smfm list counts only SMFM-CONTAINER extensions
(flac/mp3/wma) - the formats Sony's tagger can actually write into. Pass
--all to list every entry without a block regardless of container.

truedat only READS SMFM; nothing in it writes SMFM into a file. So a no-smfm
row is a Sony-tagging gap, not a truedat gap - rescanning will not close it.

The exe reports the same split in one file: truedat --list-smfm

Unofficial tooling - see SMFM-KNOWLEDGE.md for what the values mean.
Usage: python check_moods_smfm.py <path/to/mbxmoods.json> [out_dir] [--all]
"""
import csv, json, os, sys, collections

DATA, NO_DATA, NO_SMFM = 'data', 'no-data', 'no-smfm'


def classify(scores):
    """Mirror of truedat's ClassifySmfm. Keep these two in step."""
    if not isinstance(scores, list) or len(scores) == 0:
        return NO_SMFM
    return DATA if any(v for v in scores) else NO_DATA


def self_test():
    failures = []

    def check(cond, name):
        print(f"  {'PASS' if cond else 'FAIL'}  {name}")
        if not cond:
            failures.append(name)

    check(classify(None) == NO_SMFM, 'classify: None -> no-smfm')
    check(classify([]) == NO_SMFM, 'classify: empty list -> no-smfm')
    check(classify([0, 0, 0]) == NO_DATA, 'classify: all-zero -> no-data')
    check(classify([0, 0, 1]) == DATA, 'classify: a non-zero anywhere -> data')
    check(classify('nope') == NO_SMFM, 'classify: a non-list is treated as absent')
    print()
    if failures:
        print(f'{len(failures)} FAILED')
        return 1
    print('All self-tests passed.')
    return 0


if '--self-test' in sys.argv:
    sys.exit(self_test())

args = [a for a in sys.argv[1:] if not a.startswith('--')]
flags = {a for a in sys.argv[1:] if a.startswith('--')}
if not args:
    sys.exit('Usage: check_moods_smfm.py <path/to/mbxmoods.json> [out_dir] [--all]')
MOODS = args[0]
OUT_DIR = args[1] if len(args) > 1 else os.path.dirname(os.path.abspath(__file__))
ALL_EXTS = '--all' in flags

SMFM_EXTS = {'.flac', '.mp3', '.wma'}

print(f"loading {MOODS} ...", flush=True)
with open(MOODS, 'r', encoding='utf-8') as f:
    data = json.load(f)

tracks = data.get('tracks', {})
print(f"trackCount header={data.get('trackCount')}  parsed={len(tracks)}", flush=True)

# per ext: [total, data, no_data, no_smfm]
stat = collections.defaultdict(lambda: [0, 0, 0, 0])
rows = {DATA: [], NO_DATA: [], NO_SMFM: []}
slot_hist = collections.Counter()
skipped_non_container = 0

for path, e in tracks.items():
    ext = os.path.splitext(path)[1].lower()
    sc = e.get('smfmScores')
    if sc is None:
        sc = e.get('sensmeScores')            # legacy key, pre --migrate
    slot = e.get('smfmChannel')
    if slot is None:
        slot = e.get('sensmeChannel')         # legacy key
    state = classify(sc)

    s = stat[ext]
    s[0] += 1
    s[1 + [DATA, NO_DATA, NO_SMFM].index(state)] += 1

    if state == NO_SMFM and not (ALL_EXTS or ext in SMFM_EXTS):
        skipped_non_container += 1
        continue
    if state == DATA:
        slot_hist[slot] += 1

    rows[state].append((
        path, ext, e.get('artist') or '', e.get('title') or '', e.get('album') or '',
        e.get('smfmBpm'), slot,
        max(sc) if state != NO_SMFM else None,
        ' '.join(str(v) for v in sc) if state != NO_SMFM else '',
    ))

for k in rows:
    rows[k].sort(key=lambda r: r[0].lower())

print(f"\n{'ext':<8}{'entries':>9}{'data':>9}{'no-data':>9}{'no-smfm':>9}")
pt = pd = 0
for ext in sorted(stat):
    tot, d, nd, ns = stat[ext]
    tag = ' (SMFM container)' if ext in SMFM_EXTS else ''
    if ext in SMFM_EXTS:
        pt += tot; pd += d
    print(f"{ext:<8}{tot:>9}{d:>9}{nd:>9}{ns:>9}{tag}")

total = len(tracks)
n_data, n_nodata = len(rows[DATA]), len(rows[NO_DATA])
n_nosmfm = len(rows[NO_SMFM])
print(f"\nSony SMFM (12-TONE): {total} catalog entries")
print(f"  with data     {n_data:>9}   block present, scored")
print(f"  without data  {n_nodata:>9}   block present, every score zero")
print(f"  without SMFM  {n_nosmfm:>9}   no block"
      + (f" ({skipped_non_container} non-container entries not listed; --all includes them)"
         if skipped_non_container else ""))
print(f"\nSMFM-container entries (flac+mp3+wma) with data: {pd}/{pt}"
      f" = {(pd/pt*100 if pt else 0):.1f}%")

# smfmChannel is the argmax SLOT of the 10 mood-model scores - NOT a SensMe
# channel (see SMFM-KNOWLEDGE.md); histogram is diagnostic only.
print(f"\nsmfmChannel (argmax slot) distribution, scored entries only:")
for slot in sorted(slot_hist, key=lambda x: (x is None, x)):
    print(f"  slot {slot}: {slot_hist[slot]:>7}")

HEADER = ['path', 'ext', 'artist', 'title', 'album',
          'smfmBpm', 'smfmChannel', 'topScore', 'scores']

print()
for state, name in ((DATA, 'smfm-data.csv'), (NO_DATA, 'smfm-no-data.csv'),
                    (NO_SMFM, 'smfm-no-smfm.csv')):
    out = os.path.join(OUT_DIR, name)
    with open(out, 'w', encoding='utf-8', newline='') as fh:
        w = csv.writer(fh)
        w.writerow(HEADER)
        w.writerows(rows[state])
    print(f"wrote {len(rows[state]):>7} rows -> {out}")
