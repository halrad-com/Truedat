"""Export decoded SMFM analysis to a portable JSON file.

Unofficial, reverse-engineered tooling — see SMFM-FORMAT.md for the wire format
and SMFM-KNOWLEDGE.md for what the decoded values do (and do not) mean.

Two modes:

  tracks.db mode (default):
      python smfm_export.py [output_path]
      Reads Music Center tracks.db, follows twelveTone=1 flags, reads SMFM
      from embedded FLAC/MP3/WMA blocks or smfmf.bin sidecars.

  folder-scan mode:
      python smfm_export.py --scan <music_root> [output_path]
      Walks a folder (device drive, second machine share, etc.) and reads
      SMFM directly from every FLAC, MP3, and WMA found. No tracks.db needed.
      Use this for devices or machines where MC's tracks.db isn't available.

Output is keyed by file URI. Fields per track:
  mc_id, sha256(null), bpm, duration_ms, sample_rate, codec, smfm_source,
  smfm_scores[10], smfm_top_slot, smfm_top_score, energy_curve[40], highlight_cs

smfm_top_slot is the argmax of the 10 mood-model scores — it is NOT a SensMe
channel (see SMFM-KNOWLEDGE.md: channels are regions on a 2-D arousal/valence
canvas, not bins of the slots).

sha256 reserved for content-hash identity — null until a hashing pass runs.

Supported containers:
  FLAC  — APPLICATION block, id=SMFM
  MP3   — ID3v2 GEOB frame, mime=application/SMFMF, desc=USR_SMFMF
  WMA   — ASF Extended Content Description, attribute name=SMFMF
  other — sidecar smfmf.bin referenced by tracks.db record (tracks.db mode only)
"""
import json
import os
import struct
import sys
import uuid
from datetime import datetime, timezone
from typing import Optional

TRACKS_DB = os.path.join(os.environ.get('APPDATA', ''),
                         'Sony', 'Music Center', 'db', 'tracks.db')
DEFAULT_OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'smfm_library.json')

SCHEMA_VERSION = 1


# ---------------------------------------------------------------------------
# SMFM payload readers
# ---------------------------------------------------------------------------

def read_flac_smfm(path: str) -> Optional[bytes]:
    """Return SMFM sub-block payload from a FLAC APPLICATION block, or None."""
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


def _id3_frame_size(data: bytes, offset: int, syncsafe: bool) -> int:
    b = data[offset:offset + 4]
    if syncsafe:
        return (b[0] << 21) | (b[1] << 14) | (b[2] << 7) | b[3]
    return struct.unpack('>I', b)[0]


def read_mp3_smfm(path: str) -> Optional[bytes]:
    """Return SMFM sub-block payload from an ID3v2 GEOB frame, or None.

    Music Center writes: GEOB mime=application/SMFMF desc=USR_SMFMF.
    The GEOB object field starts directly at the first SMFM sub-block.
    Handles ID3v2.3 (big-endian frame sizes) and ID3v2.4 (syncsafe sizes).
    """
    try:
        with open(path, 'rb') as f:
            hdr = f.read(10)
        if hdr[:3] != b'ID3':
            return None
        version = hdr[3]
        if version not in (3, 4):
            return None
        syncsafe_frames = (version == 4)
        tag_size = (hdr[6] << 21) | (hdr[7] << 14) | (hdr[8] << 7) | hdr[9]
        with open(path, 'rb') as f:
            f.read(10)
            tag_data = f.read(tag_size)
    except OSError:
        return None

    p = 0
    while p + 10 <= len(tag_data):
        fid = tag_data[p:p + 4]
        if fid == b'\x00\x00\x00\x00':
            break
        fsize = _id3_frame_size(tag_data, p + 4, syncsafe_frames)
        if fsize <= 0 or p + 10 + fsize > len(tag_data):
            break
        payload = tag_data[p + 10:p + 10 + fsize]
        if fid == b'GEOB' and len(payload) > 1:
            # encoding(1) + mime\0 + filename\0 + description\0 + object
            try:
                mime_end = payload.index(b'\x00', 1)
                mime = payload[1:mime_end]
                if mime == b'application/SMFMF':
                    rest = payload[mime_end + 1:]
                    fname_end = rest.index(b'\x00')
                    after_fname = rest[fname_end + 1:]
                    desc_end = after_fname.index(b'\x00')
                    return after_fname[desc_end + 1:]
            except (ValueError, IndexError):
                pass
        p += 10 + fsize
    return None


_ASF_HEADER_GUID = bytes.fromhex('3026B2758E66CF11A6D900AA0062CE6C')
_ASF_ECD_GUID = bytes(uuid.UUID('d2d0a440-e307-11d2-97f0-00a0c95ea850').bytes_le)


def read_wma_smfm(path: str) -> Optional[bytes]:
    """Return SMFM sub-block payload from an ASF/WMA Extended Content Description attribute.

    Music Center writes attribute name='SMFMF', value=raw sub-block chain.
    """
    try:
        with open(path, 'rb') as f:
            if f.read(16) != _ASF_HEADER_GUID:
                return None
            header_size = struct.unpack('<Q', f.read(8))[0]
            num_objects = struct.unpack('<I', f.read(4))[0]
            f.read(2)  # reserved
            for _ in range(num_objects):
                guid = f.read(16)
                obj_size = struct.unpack('<Q', f.read(8))[0]
                data = f.read(obj_size - 24)
                if guid != _ASF_ECD_GUID:
                    continue
                p = 0
                count = struct.unpack_from('<H', data, p)[0]; p += 2
                for _ in range(count):
                    name_len = struct.unpack_from('<H', data, p)[0]; p += 2
                    name = data[p:p + name_len].decode('utf-16-le', errors='replace').rstrip('\x00')
                    p += name_len
                    val_type = struct.unpack_from('<H', data, p)[0]; p += 2
                    val_len = struct.unpack_from('<H', data, p)[0]; p += 2
                    val = data[p:p + val_len]; p += val_len
                    if name == 'SMFMF' and val_type == 1:  # type 1 = byte array
                        return val
                return None
    except (OSError, struct.error):
        return None


_KNOWN_MAGICS = {b'STAEMMLW', b'STAESMML'}  # PC firmware vs Walkman firmware


def parse_subblocks(payload: bytes) -> dict:
    """Return {tag: payload_bytes} for each well-formed sub-block.

    Accepts both known magic variants:
      STAEMMLW — Music Center for PC / MMLib11
      STAESMML — Walkman device firmware (observed on NW-A series)
    """
    out = {}
    p = 0
    while p + 20 <= len(payload):
        tag = payload[p:p + 4]
        if payload[p + 4:p + 12] not in _KNOWN_MAGICS:
            break
        plen = struct.unpack('>I', payload[p + 16:p + 20])[0]
        if p + 20 + plen > len(payload):
            break
        out[tag.decode('ascii', errors='replace')] = payload[p + 20:p + 20 + plen]
        p += 20 + plen
    return out


# ---------------------------------------------------------------------------
# Sub-block decoders
# ---------------------------------------------------------------------------

def decode_gbpm(b: bytes) -> Optional[float]:
    if len(b) != 4:
        return None
    v = struct.unpack('>f', b)[0]
    return round(v, 3)


def decode_stsa(b: bytes) -> Optional[list]:
    """Return [in_cs, out_cs] or None if unset / identical-zero."""
    if len(b) != 8:
        return None
    a, c = struct.unpack('>II', b)
    if a == 0 and c == 0:
        return None
    return [a, c]


def decode_stmo(b: bytes) -> Optional[list]:
    """Return the 10 mood-model slot scores (0-255).

    Each slot appears 4x (sub-indices 0-3); the replicas are identical in all
    observed data (NOT temporal segments — see SMFM-KNOWLEDGE.md), so the
    average below is a no-op that just collapses the replication.
    """
    if len(b) != 160:
        return None
    by_slot: dict[int, list] = {}
    for i in range(0, 160, 4):
        rec = b[i:i + 4]
        if len(rec) != 4:
            continue
        _t, slot, _sub, val = rec
        by_slot.setdefault(slot, []).append(val)
    if not by_slot:
        return None
    return [round(sum(v) / len(v)) for _slot, v in sorted(by_slot.items())]


def decode_sthf(b: bytes) -> Optional[list]:
    """Return 40-bin energy curve as list of ints."""
    if len(b) != 160:
        return None
    return [b[i + 2] for i in range(0, 160, 4)]


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def _make_track_record(uri: str, payload: bytes, source: str, mc_id: str = '',
                        duration_ms=None, sample_rate=None, codec: str = '') -> Optional[dict]:
    blocks = parse_subblocks(payload)
    bpm = decode_gbpm(blocks.get('GBPM', b''))
    if bpm is None:
        return None
    stmo = decode_stmo(blocks.get('STMO', b''))
    top_slot = stmo.index(max(stmo)) if stmo else None
    top_score = max(stmo) if stmo else None
    return {
        'mc_id':          mc_id,
        'sha256':         None,
        'bpm':            bpm,
        'duration_ms':    duration_ms,
        'sample_rate':    sample_rate,
        'codec':          codec,
        'smfm_source':    source,
        'smfm_scores':    stmo,
        'smfm_top_slot':  top_slot,
        'smfm_top_score': top_score,
        'energy_curve':   decode_sthf(blocks.get('STHF', b'')),
        'highlight_cs':   decode_stsa(blocks.get('STSA', b'')),
    }


def scan_folder(root: str) -> tuple[dict, dict]:
    """Walk root recursively, read SMFM from every FLAC, MP3, and WMA found."""
    tracks = {}
    stats = {'scanned': 0, 'from_flac': 0, 'from_mp3': 0, 'from_wma': 0, 'no_smfm': 0, 'failed': 0}
    for dirpath, _, filenames in os.walk(root):
        for fn in filenames:
            ext = os.path.splitext(fn)[1].lower()
            if ext not in ('.flac', '.mp3', '.wma'):
                continue
            path = os.path.join(dirpath, fn)
            stats['scanned'] += 1
            payload = None
            source = None
            if ext == '.flac':
                payload = read_flac_smfm(path)
                source = 'flac'
            elif ext == '.mp3':
                payload = read_mp3_smfm(path)
                source = 'mp3'
            elif ext == '.wma':
                payload = read_wma_smfm(path)
                source = 'wma'
            if payload is None:
                stats['no_smfm'] += 1
                continue
            rec = _make_track_record(path, payload, source)
            if rec is None:
                stats['failed'] += 1
            else:
                stats['from_' + source] += 1
                tracks[path] = rec
    return tracks, stats


def scan_tracks_db(db_path: str = TRACKS_DB,
                   uri_strip: str = '', uri_prefix: str = '') -> tuple[dict, dict]:
    """Read tracks.db, follow twelveTone=1 flags, decode SMFM payloads.

    uri_strip / uri_prefix: rewrite URIs when the DB was written on a different
    machine. Strip uri_strip from the start of each URI, then prepend uri_prefix.
    Example: uri_strip='D:\\Music\\', uri_prefix='D:\\'
    """
    if not os.path.exists(db_path):
        sys.exit(f'tracks.db not found: {db_path}')
    tracks = {}
    stats = {'total': 0, 'from_flac': 0, 'from_mp3': 0, 'from_wma': 0, 'from_sidecar': 0, 'flag_only': 0, 'failed': 0}
    seen_uris: dict[str, dict] = {}
    with open(db_path, 'rb') as f:
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
            uri = rec.get('file', {}).get('uri', '')
            seen_uris[uri] = rec  # keep latest record per URI

    for uri, rec in seen_uris.items():
        stats['total'] += 1
        mc_id = rec.get('_id', '')
        # Rewrite URI if this DB came from a different machine
        file_path = uri
        if uri_strip and file_path.startswith(uri_strip):
            file_path = uri_prefix + file_path[len(uri_strip):]
        elif uri_prefix and not file_path.startswith(uri_prefix):
            file_path = uri_prefix + file_path

        payload = None
        source = None
        sidecar = rec.get('smfmf')
        if sidecar and os.path.exists(sidecar):
            try:
                payload = open(sidecar, 'rb').read()
                source = 'sidecar'
            except OSError:
                pass
        ext = os.path.splitext(file_path)[1].lower()
        if payload is None and ext == '.flac':
            payload = read_flac_smfm(file_path)
            if payload is not None:
                source = 'flac'
        if payload is None and ext == '.mp3':
            payload = read_mp3_smfm(file_path)
            if payload is not None:
                source = 'mp3'
        if payload is None and ext == '.wma':
            payload = read_wma_smfm(file_path)
            if payload is not None:
                source = 'wma'
        if payload is None:
            stats['flag_only'] += 1
            continue
        track = _make_track_record(
            uri, payload, source, mc_id,
            duration_ms=rec.get('duration'),
            sample_rate=rec.get('file', {}).get('samplingFrequency'),
            codec=rec.get('file', {}).get('mimeType', ''),
        )
        if track is None:
            stats['failed'] += 1
        else:
            stats['from_' + source] += 1
            tracks[uri] = track
    return tracks, stats


def _pop_flag(args: list, flag: str, default=None):
    """Remove --flag value from args list and return value, or default."""
    try:
        i = args.index(flag)
        val = args[i + 1]
        del args[i:i + 2]
        return val
    except (ValueError, IndexError):
        return default


def main():
    args = list(sys.argv[1:])
    folder_mode = len(args) >= 1 and args[0] == '--scan'

    db_path    = _pop_flag(args, '--db', TRACKS_DB)
    uri_strip  = _pop_flag(args, '--uri-strip', '')
    uri_prefix = _pop_flag(args, '--uri-prefix', '')

    if folder_mode:
        args.pop(0)  # remove '--scan'
        if not args:
            sys.exit('Usage: smfm_export.py --scan <music_root> [output_path]')
        music_root = args[0]
        out_path = args[1] if len(args) >= 2 else os.path.join(
            os.path.dirname(os.path.abspath(__file__)), 'smfm_device.json')
        if not os.path.isdir(music_root):
            sys.exit(f'Not a directory: {music_root}')
        print(f'Scanning folder: {music_root}')
        tracks, stats = scan_folder(music_root)
        print(f'Files scanned           : {stats["scanned"]}')
        print(f'  FLAC with SMFM        : {stats["from_flac"]}')
        print(f'  MP3 with SMFM         : {stats["from_mp3"]}')
        print(f'  WMA with SMFM         : {stats["from_wma"]}')
        print(f'  No SMFM found         : {stats["no_smfm"]}')
        print(f'  Parse failures        : {stats["failed"]}')
    else:
        out_path = args[0] if args else DEFAULT_OUT
        tracks, stats = scan_tracks_db(db_path, uri_strip, uri_prefix)
        print(f'Tracks with twelveTone=1  : {stats["total"]}')
        print(f'  from FLAC APPLICATION   : {stats["from_flac"]}')
        print(f'  from MP3 GEOB frame     : {stats["from_mp3"]}')
        print(f'  from WMA ASF attr       : {stats["from_wma"]}')
        print(f'  from sidecar smfmf.bin  : {stats["from_sidecar"]}')
        print(f'  flag-only, no payload   : {stats["flag_only"]}')
        print(f'  parse failures          : {stats["failed"]}')
        if uri_strip or uri_prefix:
            print(f'  URI rewrite: strip={uri_strip!r} prefix={uri_prefix!r}')

    doc = {
        'schema_version': SCHEMA_VERSION,
        'generated_utc':  datetime.now(timezone.utc).isoformat(),
        'track_count':    len(tracks),
        'tracks':         tracks,
    }
    with open(out_path, 'w', encoding='utf-8') as f:
        json.dump(doc, f, ensure_ascii=False, separators=(',', ':'))
    print(f'Written ({len(tracks)} tracks): {out_path}')
    print(f'File size: {os.path.getsize(out_path):,} bytes')


if __name__ == '__main__':
    main()
