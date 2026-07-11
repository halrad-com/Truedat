"""Walk Music Center's tracks.db, collect every reachable SMFM payload
(embedded in source FLAC or via fringe smfmf.bin sidecar), parse sub-blocks,
and emit a CSV for cross-track analysis.

Unofficial, reverse-engineered tooling — see SMFM-FORMAT.md for the wire format.
Usage: python smfm_index.py [output.csv]
"""
import csv, json, os, struct, sys
from typing import Optional

TRACKS_DB = os.path.join(os.environ.get('APPDATA', ''),
                         'Sony', 'Music Center', 'db', 'tracks.db')
OUT_CSV = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
    os.path.dirname(os.path.abspath(__file__)), 'smfm_index.csv')


def read_flac_smfm(path: str) -> Optional[bytes]:
    """Return the SMFM application payload (sub-blocks only, no app-id)."""
    try:
        with open(path, 'rb') as f:
            if f.read(4) != b'fLaC':
                return None
            while True:
                hdr = f.read(4)
                if len(hdr) < 4:
                    return None
                last = bool(hdr[0] & 0x80)
                btype = hdr[0] & 0x7F
                blen = (hdr[1] << 16) | (hdr[2] << 8) | hdr[3]
                data = f.read(blen)
                if btype == 2 and data[:4] == b'SMFM':
                    return data[4:]
                if last:
                    return None
    except OSError:
        return None


def parse_subblocks(payload: bytes) -> dict:
    """Return {tag_str: payload_bytes} for each well-formed sub-block."""
    out = {}
    p = 0
    while p + 20 <= len(payload):
        tag = payload[p:p+4]
        magic = payload[p+4:p+12]
        if magic != b'STAEMMLW':
            break
        plen = struct.unpack('>I', payload[p+16:p+20])[0]
        if p + 20 + plen > len(payload):
            break
        out[tag.decode('ascii', errors='replace')] = payload[p+20:p+20+plen]
        p += 20 + plen
    return out


def decode_gbpm(b: bytes) -> Optional[float]:
    if len(b) != 4:
        return None
    return struct.unpack('>f', b)[0]


def decode_stsa(b: bytes) -> tuple:
    if len(b) != 8:
        return (None, None)
    a, c = struct.unpack('>II', b)
    return (a, c)


def decode_stmo(b: bytes) -> list:
    """Return the 10 mood-model slot scores (0-255).

    Each slot appears 4x (sub-indices, identical in all observed data — NOT
    temporal segments, see SMFM-KNOWLEDGE.md); the average is a no-op that
    collapses the replication.
    """
    if len(b) != 160:
        return []
    by_slot = {}
    for i in range(0, 160, 4):
        rec = b[i:i+4]
        if len(rec) != 4:
            continue
        _t, slot, _sub, val = rec
        by_slot.setdefault(slot, []).append(val)
    return [round(sum(v)/len(v)) for slot, v in sorted(by_slot.items())]


def decode_sthf(b: bytes) -> list:
    if len(b) != 160:
        return []
    return [b[i+2] for i in range(0, 160, 4)]


def main():
    if not os.path.exists(TRACKS_DB):
        sys.exit(f'tracks.db not found: {TRACKS_DB}')

    rows = []
    stats = {'total': 0, 'flag_only': 0, 'from_flac': 0, 'from_sidecar': 0, 'failed': 0}

    with open(TRACKS_DB, 'rb') as f:
        for raw in f:
            line = raw.decode('utf-8', errors='replace').strip()
            if not line:
                continue
            try:
                rec = json.loads(line)
            except json.JSONDecodeError:
                continue
            analysis = rec.get('analysis') or {}
            if analysis.get('twelveTone') != 1:
                continue
            stats['total'] += 1

            uri = rec.get('file', {}).get('uri', '')
            payload = None
            source = None

            sidecar = rec.get('smfmf')
            if sidecar and os.path.exists(sidecar):
                try:
                    payload = open(sidecar, 'rb').read()
                    source = 'sidecar'
                except OSError:
                    pass

            if payload is None and uri.lower().endswith('.flac'):
                payload = read_flac_smfm(uri)
                if payload is not None:
                    source = 'flac'

            if payload is None:
                stats['flag_only'] += 1
                continue

            blocks = parse_subblocks(payload)
            if 'GBPM' not in blocks:
                stats['failed'] += 1
                continue

            stats['from_' + ('sidecar' if source == 'sidecar' else 'flac')] += 1

            stsa_a, stsa_b = decode_stsa(blocks.get('STSA', b''))
            stmo = decode_stmo(blocks.get('STMO', b''))
            sthf = decode_sthf(blocks.get('STHF', b''))

            rows.append({
                '_id': rec.get('_id', ''),
                'source': source,
                'codec': rec.get('file', {}).get('mimeType', ''),
                'sample_rate': rec.get('file', {}).get('samplingFrequency', ''),
                'duration_ms': rec.get('duration', ''),
                'title': rec.get('title', ''),
                'genre': rec.get('_genre', ''),
                'payload_bytes': len(payload),
                'sub_tags': '|'.join(blocks.keys()),
                'gbpm': round(decode_gbpm(blocks['GBPM']) or 0, 3),
                'stsa_a': stsa_a,
                'stsa_b': stsa_b,
                'stmo_scores': stmo,
                'stmo_max_slot': stmo.index(max(stmo)) if stmo else '',
                'stmo_max_val': max(stmo) if stmo else '',
                'stbf_hex': blocks.get('STBF', b'').hex(),
                'sthf_min': min(sthf) if sthf else '',
                'sthf_max': max(sthf) if sthf else '',
                'stmm_bytes': len(blocks.get('STMM', b'')),
                'uri': uri,
            })

    with open(OUT_CSV, 'w', encoding='utf-8', newline='') as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0].keys())) if rows else None
        if w:
            w.writeheader()
            for r in rows:
                w.writerow(r)

    print(f'Tracks flagged twelveTone=1   : {stats["total"]}')
    print(f'  payload from FLAC SMFM block : {stats["from_flac"]}')
    print(f'  payload from sidecar bin     : {stats["from_sidecar"]}')
    print(f'  flag-only, no payload found  : {stats["flag_only"]}')
    print(f'  parse failures               : {stats["failed"]}')
    print(f'Wrote: {OUT_CSV}  ({len(rows)} rows)')


if __name__ == '__main__':
    main()
