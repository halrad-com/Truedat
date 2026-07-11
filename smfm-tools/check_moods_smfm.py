#!/usr/bin/env python3
"""Report SMFM coverage inside a truedat mbxmoods.json.

Per audio extension: how many track entries have smfmScores populated vs not
(reads the legacy sensmeScores key as fallback for un-migrated files).
Writes the SMFM-container files (flac/mp3/wma) that LACK SMFM data — rescan
candidates — to smfm-missing.txt next to this script.

Unofficial tooling — see SMFM-KNOWLEDGE.md for what the values mean.
Usage: python check_moods_smfm.py <path\\to\\mbxmoods.json>
"""
import json, os, sys, collections

if len(sys.argv) < 2:
    sys.exit('Usage: check_moods_smfm.py <path/to/mbxmoods.json>')
MOODS = sys.argv[1]
OUT_MISSING = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'smfm-missing.txt')

SMFM_EXTS = {'.flac', '.mp3', '.wma'}

print(f"loading {MOODS} ...", flush=True)
with open(MOODS, 'r', encoding='utf-8') as f:
    data = json.load(f)

tracks = data.get('tracks', {})
print(f"trackCount header={data.get('trackCount')}  parsed={len(tracks)}", flush=True)

# per ext: [total, has_smfm, has_smfmbpm]
stat = collections.defaultdict(lambda: [0, 0, 0])
missing = []   # SMFM-container files lacking smfmScores
slot_hist = collections.Counter()

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
    elif ext in SMFM_EXTS:
        missing.append(path)
    if e.get('smfmBpm') is not None:
        s[2] += 1

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
print(f"Missing SMFM among SMFM-container entries: {len(missing)}")

# smfmChannel is the argmax SLOT of the 10 mood-model scores — NOT a SensMe
# channel (see SMFM-KNOWLEDGE.md); histogram is diagnostic only.
print(f"\nsmfmChannel (argmax slot) distribution:")
for slot in sorted(slot_hist, key=lambda x: (x is None, x)):
    print(f"  slot {slot}: {slot_hist[slot]:>7}")

with open(OUT_MISSING, 'w', encoding='utf-8') as f:
    for p in missing:
        f.write(p + '\n')
print(f"\nwrote {len(missing)} missing paths -> {OUT_MISSING}")
