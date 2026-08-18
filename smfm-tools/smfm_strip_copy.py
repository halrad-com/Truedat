#!/usr/bin/env python3
"""Copy an audio file WITHOUT its Sony SMFM (12-TONE) block.

The source is never modified -- this writes a new file and reads the original
read-only. truedat itself never edits audio files, and this tool does not
change that: it is opt-in sibling tooling, outside truedat.exe.

The audio frames are copied byte-for-byte, so the copy keeps the SAME
audioStreamSha256 as the source (truedat's FLAC hash is frame-anchored, and
its MP3 invariant region excludes the ID3v2 tag). truedat therefore treats the
stripped copy as the same track: a scan cross-SHA cache-hits and re-keys it
rather than re-analyzing. That property is what makes this usable as a control
copy for format work, and it is asserted after every write.

Containers:
  FLAC  APPLICATION block, id 'SMFM' -- dropped from the metadata chain, and
        the last-block flag moved if the dropped block was last.
  MP3   ID3v2.3/2.4 GEOB frame, mime 'application/SMFMF' -- dropped, later
        frames moved up, tail zero-padded so the tag size (and therefore the
        audio offset) is unchanged.

WMA (ASF Extended Content Description) and M4A (ID32 inside moov>udta>meta)
are readable by the sibling tools but not writable here -- rewriting those
containers means recomputing sizes that cascade up an object/box tree, which
is a different job with a different risk profile. They are refused by name
rather than silently passed through.

Unofficial, reverse-engineered tooling -- see SMFM-FORMAT.md for the wire
format and SMFM-KNOWLEDGE.md for what the values mean.

Usage:
  python smfm_strip_copy.py <src> <dest> [--force]
  python smfm_strip_copy.py --self-test
"""
import hashlib
import os
import struct
import sys

SUPPORTED = {'.flac', '.mp3'}
KNOWN_UNSUPPORTED = {'.wma': 'ASF Extended Content Description',
                     '.m4a': 'MP4 ID32 box', '.mp4': 'MP4 ID32 box'}


# ---------------------------------------------------------------------------
# FLAC
# ---------------------------------------------------------------------------

def flac_split(data):
    """-> (blocks, audio_offset). blocks = [(btype, is_last, payload), ...].

    Raises ValueError when the file is not a FLAC we understand — better than
    writing a 'copy' from a misparse.
    """
    if data[:4] != b'fLaC':
        raise ValueError('not a FLAC file (no fLaC magic)')
    blocks = []
    p = 4
    while True:
        if p + 4 > len(data):
            raise ValueError('truncated FLAC metadata chain')
        first = data[p]
        last = bool(first & 0x80)
        btype = first & 0x7F
        blen = (data[p + 1] << 16) | (data[p + 2] << 8) | data[p + 3]
        payload = data[p + 4:p + 4 + blen]
        if len(payload) != blen:
            raise ValueError('truncated FLAC metadata block')
        blocks.append((btype, last, payload))
        p += 4 + blen
        if last:
            break
    return blocks, p


def flac_strip(data):
    """-> (new_bytes, dropped_count). Audio frames copied verbatim."""
    blocks, audio_offset = flac_split(data)
    kept = [b for b in blocks if not (b[0] == 2 and b[2][:4] == b'SMFM')]
    dropped = len(blocks) - len(kept)
    if dropped == 0:
        return None, 0
    if not kept:
        raise ValueError('every metadata block was SMFM - refusing (STREAMINFO is mandatory)')

    out = bytearray(b'fLaC')
    for i, (btype, _, payload) in enumerate(kept):
        # The last-block flag belongs to whichever block is now last, not to
        # whichever block carried it before the drop.
        first = btype | (0x80 if i == len(kept) - 1 else 0x00)
        n = len(payload)
        out += bytes([first, (n >> 16) & 0xFF, (n >> 8) & 0xFF, n & 0xFF])
        out += payload
    out += data[audio_offset:]
    return bytes(out), dropped


def flac_audio_bytes(data):
    _, audio_offset = flac_split(data)
    return data[audio_offset:]


def flac_has_smfm(data):
    try:
        blocks, _ = flac_split(data)
    except ValueError:
        return False
    return any(b[0] == 2 and b[2][:4] == b'SMFM' for b in blocks)


# ---------------------------------------------------------------------------
# MP3 / ID3v2
# ---------------------------------------------------------------------------

def id3_frame_size(data, offset, syncsafe):
    b = data[offset:offset + 4]
    if syncsafe:
        return (b[0] << 21) | (b[1] << 14) | (b[2] << 7) | b[3]
    return struct.unpack('>I', b)[0]


def id3_tag_bounds(data):
    """-> (tag_size, syncsafe_frames) or None when there is no ID3v2.3/2.4 tag."""
    if data[:3] != b'ID3':
        return None
    version = data[3]
    if version not in (3, 4):
        return None
    tag_size = (data[6] << 21) | (data[7] << 14) | (data[8] << 7) | data[9]
    return tag_size, (version == 4)


def id3_frames(data):
    """Yield (offset, fid, total_len, payload) over the frame area."""
    bounds = id3_tag_bounds(data)
    if bounds is None:
        return
    tag_size, syncsafe = bounds
    tag_data = data[10:10 + tag_size]
    p = 0
    while p + 10 <= len(tag_data):
        fid = tag_data[p:p + 4]
        if fid == b'\x00\x00\x00\x00':
            break
        fsize = id3_frame_size(tag_data, p + 4, syncsafe)
        if fsize <= 0 or p + 10 + fsize > len(tag_data):
            break
        yield p, fid, 10 + fsize, tag_data[p + 10:p + 10 + fsize]
        p += 10 + fsize


def is_smfm_geob(fid, payload):
    if fid != b'GEOB' or len(payload) < 2:
        return False
    try:
        mime_end = payload.index(b'\x00', 1)
    except ValueError:
        return False
    return payload[1:mime_end] == b'application/SMFMF'


def mp3_strip(data):
    """-> (new_bytes, dropped_count).

    The tag KEEPS its declared size: surviving frames move up and the freed
    tail becomes ID3 padding (zero bytes, which is what padding is). The audio
    offset is therefore unchanged, so the MPEG frames land at the same file
    position they started at — one less thing that can differ between source
    and copy.
    """
    bounds = id3_tag_bounds(data)
    if bounds is None:
        return None, 0
    tag_size, _ = bounds
    tag_data = data[10:10 + tag_size]

    kept = bytearray()
    dropped = 0
    for off, fid, total_len, payload in id3_frames(data):
        if is_smfm_geob(fid, payload):
            dropped += 1
            continue
        kept += tag_data[off:off + total_len]
    if dropped == 0:
        return None, 0

    if len(kept) > tag_size:
        raise ValueError('internal: rebuilt frame area grew')
    kept += b'\x00' * (tag_size - len(kept))
    return data[:10] + bytes(kept) + data[10 + tag_size:], dropped


def mp3_audio_bytes(data):
    bounds = id3_tag_bounds(data)
    if bounds is None:
        return data
    return data[10 + bounds[0]:]


def mp3_has_smfm(data):
    return any(is_smfm_geob(fid, payload) for _, fid, _, payload in id3_frames(data))


# ---------------------------------------------------------------------------
# driver
# ---------------------------------------------------------------------------

HANDLERS = {
    '.flac': (flac_strip, flac_audio_bytes, flac_has_smfm),
    '.mp3':  (mp3_strip,  mp3_audio_bytes,  mp3_has_smfm),
}


def strip_copy(src, dest, force=False):
    """Write a SMFM-free copy of src at dest. Returns True when one was written.

    Refuses rather than guesses: unknown container, dest already there, or a
    dest that resolves to the source.
    """
    ext = os.path.splitext(src)[1].lower()
    if ext in KNOWN_UNSUPPORTED:
        print(f"REFUSED {src}: {ext} SMFM lives in the {KNOWN_UNSUPPORTED[ext]}, "
              f"which this tool cannot rewrite (read-only support only)")
        return False
    if ext not in HANDLERS:
        print(f"REFUSED {src}: unsupported container {ext or '(none)'}")
        return False
    if os.path.abspath(src) == os.path.abspath(dest):
        print(f"REFUSED: dest is the source - this tool never edits in place")
        return False
    if os.path.exists(dest) and not force:
        print(f"REFUSED: {dest} exists (pass --force to overwrite)")
        return False

    strip, audio_of, has = HANDLERS[ext]
    with open(src, 'rb') as f:
        data = f.read()

    if not has(data):
        print(f"SKIP    {src}: no SMFM block - nothing to strip, no copy written")
        return False

    out, dropped = strip(data)
    if out is None:
        print(f"SKIP    {src}: no SMFM block - nothing to strip, no copy written")
        return False

    src_audio = hashlib.sha256(audio_of(data)).hexdigest()
    copy_audio = hashlib.sha256(audio_of(out)).hexdigest()
    if src_audio != copy_audio:
        raise AssertionError(f'audio frames changed ({src_audio[:12]} -> {copy_audio[:12]}) '
                             f'- refusing to write {dest}')
    if has(out):
        raise AssertionError(f'SMFM still present after strip - refusing to write {dest}')

    tmp = dest + '.partial'
    with open(tmp, 'wb') as f:
        f.write(out)
    os.replace(tmp, dest)

    saved = len(data) - len(out)
    print(f"OK      {src}\n     -> {dest}\n        dropped {dropped} SMFM block(s), "
          f"{saved} bytes; audio sha256 {src_audio[:16]} unchanged")
    return True


# ---------------------------------------------------------------------------
# self-test — synthetic containers, no fixtures on disk
# ---------------------------------------------------------------------------

def _synth_flac(with_smfm=True, smfm_last=False):
    def block(btype, payload, last):
        n = len(payload)
        return bytes([btype | (0x80 if last else 0), (n >> 16) & 0xFF,
                      (n >> 8) & 0xFF, n & 0xFF]) + payload
    streaminfo = block(0, b'\x00' * 34, last=False)
    smfm = block(2, b'SMFM' + b'GBPM' + b'\x00' * 20, last=smfm_last)
    vorbis = block(4, b'\x20\x00\x00\x00' + b'fake' + b'\x00' * 4, last=not smfm_last)
    frames = b'\xff\xf8' + b'AUDIOFRAMES' * 20
    if not with_smfm:
        return b'fLaC' + streaminfo + block(4, vorbis[4:], last=True) + frames
    chain = (streaminfo + smfm + vorbis) if not smfm_last else (streaminfo + vorbis + smfm)
    return b'fLaC' + chain + frames


def _synth_mp3(with_smfm=True):
    def frame(fid, payload):
        return fid + struct.pack('>I', len(payload)) + b'\x00\x00' + payload
    tit2 = frame(b'TIT2', b'\x00Test Title')
    geob = frame(b'GEOB', b'\x00' + b'application/SMFMF\x00' + b'\x00'
                 + b'USR_SMFMF\x00' + b'GBPM' + b'\x00' * 16)
    talb = frame(b'TALB', b'\x00Test Album')
    body = tit2 + (geob if with_smfm else b'') + talb
    body += b'\x00' * 64  # padding, as a real tag has
    size = len(body)
    hdr = b'ID3\x03\x00\x00' + bytes([(size >> 21) & 0x7F, (size >> 14) & 0x7F,
                                      (size >> 7) & 0x7F, size & 0x7F])
    return hdr + body + b'\xff\xfb' + b'MPEGFRAMES' * 20


def self_test():
    failures = []

    def check(cond, name):
        """cond may be a bool or a callable — a callable that RAISES is a FAIL,
        not a crash. A malformed rewrite fails the parser rather than an
        equality, and a stack trace at that point tells you far less than the
        assertion's own name does."""
        try:
            ok = bool(cond() if callable(cond) else cond)
            why = ''
        except Exception as e:
            ok, why = False, f'  ({type(e).__name__}: {e})'
        print(f"  {'PASS' if ok else 'FAIL'}  {name}{why}")
        if not ok:
            failures.append(name)

    for smfm_last in (False, True):
        tag = 'SMFM last' if smfm_last else 'SMFM mid-chain'
        src = _synth_flac(with_smfm=True, smfm_last=smfm_last)
        check(flac_has_smfm(src), f'flac ({tag}): fixture carries SMFM')
        out, dropped = flac_strip(src)
        check(dropped == 1, f'flac ({tag}): one block dropped (got {dropped})')
        check(lambda: not flac_has_smfm(out), f'flac ({tag}): SMFM gone from the copy')
        check(lambda: flac_audio_bytes(out) == flac_audio_bytes(src),
              f'flac ({tag}): audio frames byte-identical')
        check(len(out) < len(src), f'flac ({tag}): copy is smaller')
        check(lambda: sum(1 for b in flac_split(out)[0] if b[1]) == 1 and flac_split(out)[0][-1][1],
              f'flac ({tag}): exactly one last-block flag, on the final block')
        check(lambda: flac_split(out)[0][0][0] == 0, f'flac ({tag}): STREAMINFO survives and stays first')

    clean = _synth_flac(with_smfm=False)
    check(not flac_has_smfm(clean), 'flac: a clean file reports no SMFM')
    check(flac_strip(clean) == (None, 0), 'flac: nothing to strip -> no copy')

    src = _synth_mp3(with_smfm=True)
    check(mp3_has_smfm(src), 'mp3: fixture carries an SMFM GEOB frame')
    out, dropped = mp3_strip(src)
    check(dropped == 1, f'mp3: one GEOB frame dropped (got {dropped})')
    check(not mp3_has_smfm(out), 'mp3: SMFM gone from the copy')
    check(mp3_audio_bytes(out) == mp3_audio_bytes(src), 'mp3: MPEG frames byte-identical')
    check(len(out) == len(src), 'mp3: tag size preserved, so the file size is unchanged')
    check(mp3_audio_bytes(src) == src[len(src) - len(mp3_audio_bytes(src)):],
          'mp3: audio offset unchanged (frames end the file in both)')
    kept_ids = [fid for _, fid, _, _ in id3_frames(out)]
    check(b'TIT2' in kept_ids and b'TALB' in kept_ids,
          f'mp3: the other frames survive (got {kept_ids})')

    clean = _synth_mp3(with_smfm=False)
    check(not mp3_has_smfm(clean), 'mp3: a clean file reports no SMFM')
    check(mp3_strip(clean) == (None, 0), 'mp3: nothing to strip -> no copy')

    print()
    if failures:
        print(f"{len(failures)} FAILED")
        return 1
    print('All self-tests passed.')
    return 0


def main(argv):
    if '--self-test' in argv:
        return self_test()
    args = [a for a in argv if not a.startswith('--')]
    if len(args) != 2:
        print(__doc__)
        return 1
    try:
        wrote = strip_copy(args[0], args[1], force='--force' in argv)
    except (ValueError, AssertionError) as e:
        print(f"ERROR: {e}")
        return 1
    return 0 if wrote else 2


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
