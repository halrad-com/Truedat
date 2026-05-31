#!/usr/bin/env python3
# extract-sony-sensme.py - extract 10-channel SensMe scores + BPM from Sony Music
# Center smfmf.bin sidecar files. Joins to mbxhub mood cache by filename and
# emits a JSON suitable as labeled training data for the AutoQ v4 model.
#
# Sony stores SMFM (12-TONE) analysis as binary TLV sub-blocks. STMO sub-block
# contains 10 channels x 4 segments x 4-byte records. Each record: 01 CH SG SC
# where CH=channel(0-9), SG=segment(0-3), SC=score(0-255).
#
# Usage:
#   python3 extract-sony-sensme.py --sony "C:/Users/scott/AppData/Roaming/Sony/Music Center" #     --moods P:/Library/mbxmoods.json --out sony-sensme-labels.json

from __future__ import annotations
import argparse, io, json, os, struct
from collections import defaultdict


def decode_smfm(path):
    out = {}
    try:
        with open(path, "rb") as f:
            data = f.read()
    except Exception:
        return out
    # GBPM: float32 BE BPM after 20-byte header
    i = data.find(b"GBPM")
    if i >= 0:
        try: out["bpm"] = struct.unpack(">f", data[i+20:i+24])[0]
        except: pass
    # STMO: 10 channels x 4 records each, 4 bytes per record
    i = data.find(b"STMO")
    if i >= 0:
        try:
            length = struct.unpack(">I", data[i+16:i+20])[0]
            payload = data[i+20:i+20+length]
            per_channel = defaultdict(list)
            for r in range(len(payload) // 4):
                rec = payload[r*4:r*4+4]
                per_channel[rec[1]].append(rec[3])
            ch_means = {ch: sum(scores)/len(scores) for ch, scores in per_channel.items()}
            out["sensme"] = [ch_means.get(ch, 0.0) for ch in range(10)]
            out["sensme_segments"] = {ch: per_channel[ch] for ch in sorted(per_channel.keys())}
        except Exception:
            pass
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sony", required=True, help="path to Sony Music Center root (AppData/Roaming/Sony/Music Center)")
    ap.add_argument("--moods", required=True, help="path to mbxhub mbxmoods.json (or empty for sidecar-only output)")
    ap.add_argument("--out", required=True, help="output JSON path")
    args = ap.parse_args()

    sc_db = os.path.join(args.sony, "db", "tracks.db")
    smfm_root = os.path.join(args.sony, "fringe", "audio")

    print(f"walking {smfm_root} for sidecars...")
    sidecars = {}
    for root, dirs, files in os.walk(smfm_root):
        if "smfmf.bin" in files:
            entry = os.path.basename(root)
            sony_id = entry[:16]
            sidecars[sony_id] = os.path.join(root, "smfmf.bin")
    print(f"  {len(sidecars)} sidecar files found")

    print(f"reading {sc_db}...")
    tracks = []
    with io.open(sc_db, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line: continue
            try: d = json.loads(line)
            except: continue
            if d.get("type") != 1: continue
            a = d.get("analysis") or {}
            if a.get("twelveTone") != 1: continue
            sid = d.get("_id")
            if sid not in sidecars: continue
            decoded = decode_smfm(sidecars[sid])
            if "sensme" not in decoded: continue
            uri = (d.get("file") or {}).get("uri", "")
            tracks.append({
                "sony_id": sid,
                "sony_uri": uri,
                "sensme": decoded["sensme"],
                "bpm": decoded.get("bpm"),
            })

    print(f"  {len(tracks)} tracks with decodable STMO + GBPM")

    # Match to mbxhub by filename if moods path provided
    matched = 0
    if args.moods and os.path.exists(args.moods):
        with io.open(args.moods, "r", encoding="utf-8-sig") as f:
            moods = json.load(f).get("tracks", {})
        mbx_by_bn = {}
        for path in moods.keys():
            mbx_by_bn.setdefault(os.path.basename(path).lower(), []).append(path)
        for t in tracks:
            bn = os.path.basename(t["sony_uri"]).lower()
            cand = mbx_by_bn.get(bn)
            if cand:
                t["mbx_path"] = cand[0]
                matched += 1

    print(f"  {matched} matched to mbxhub mood cache by filename")

    os.makedirs(os.path.dirname(args.out) or ".", exist_ok=True)
    with open(args.out, "w") as f:
        json.dump({
            "schema": 1,
            "sourceDb": sc_db,
            "trackCount": len(tracks),
            "matchedToMbxhub": matched,
            "sensmeChannels": ["ch0","ch1","ch2","ch3","ch4","ch5","ch6","ch7","ch8","ch9"],
            "tracks": tracks,
        }, f, indent=2)
    print(f"wrote {args.out}")


if __name__ == "__main__":
    main()
