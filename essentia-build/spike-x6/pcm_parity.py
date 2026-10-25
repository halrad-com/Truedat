#!/usr/bin/env python3
"""x6 B1 PCM parity: the AudioLoader's decoded PCM (pcm/<label>/) against an
ffmpeg reference (pcm/ref-<ref>/, see run-parity.sh), for every row of
manifest.tsv. Writes parity-<label>.txt (ref "same", the gate) or
parity-<label>-<ref>.txt beside this script and prints it.

    python -I pcm_parity.py <label> [same|shipped]

A row passes when
    expect=decode       the loader exited 0 and its f32 file is BYTE-IDENTICAL
                        to ffmpeg's: same byte length (so the same number of
                        sample frames, and no trailing partial sample) and the
                        same bytes. Float differences are reported as a
                        diagnostic only; +0/-0 or NaN-vs-NaN pairs with
                        different bytes FAIL, as bytes do.
    expect=unsupported  the loader refused the file (exit != 0): the extractor's
                        FFmpeg has no decoder for it

Exit 0 iff every row passes. Standard library only.
"""
import array
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
CHUNK = 1 << 20  # floats per comparison chunk


def read_manifest():
    rows = []
    with open(os.path.join(HERE, 'manifest.tsv'), encoding='utf-8') as f:
        next(f)
        for line in f:
            parts = line.rstrip('\n').split('\t')
            rows.append(dict(zip(['id', 'kind', 'path', 'expect', 'note'], parts)))
    return rows


def probe(row_id):
    path = os.path.join(HERE, 'corpus', 'probe', row_id + '.json')
    try:
        with open(path, encoding='utf-8') as f:
            p = json.load(f)
    except (OSError, ValueError):
        return {'codec': '?', 'container': '?', 'rate': '?', 'ch': 0, 'fmt': '?'}
    s = (p.get('streams') or [{}])[0]
    fm = p.get('format', {})
    return {'codec': s.get('codec_name', '?'), 'container': fm.get('format_name', '?').split(',')[0],
            'rate': s.get('sample_rate', '?'), 'ch': int(s.get('channels') or 0),
            'fmt': s.get('sample_fmt', '?')}


def loader_info(label, row_id):
    path = os.path.join(HERE, 'pcm', label, row_id + '.info')
    if not os.path.exists(path):
        return None
    with open(path, encoding='utf-8', errors='replace') as f:
        lines = f.read().splitlines()
    rc = int(lines[0][3:]) if lines and lines[0].startswith('rc=') else -1
    fields = {}
    if len(lines) > 1:
        for kv in lines[1].split():
            if '=' in kv:
                k, v = kv.split('=', 1)
                fields[k] = v
    return {'rc': rc, 'fields': fields, 'msg': ' | '.join(l for l in lines[2:] if l)[:100]}


def compare(ref_path, out_path, ch):
    """(ref bytes, out bytes, first differing frame or None, max |float diff|).

    The verdict is on BYTES: first is the frame holding the first byte that
    differs (None when the overlap is byte-identical); the byte lengths decide
    the rest. maxabs is a diagnostic over the differing floats."""
    first = None
    maxabs = 0.0
    with open(ref_path, 'rb') as fa, open(out_path, 'rb') as fb:
        pos = 0
        while True:
            ba = fa.read(CHUNK * 4)
            bb = fb.read(CHUNK * 4)
            n = min(len(ba), len(bb))
            if n == 0:
                break
            if ba[:n] != bb[:n]:
                for i in range(n):
                    if ba[i] != bb[i]:
                        if first is None:
                            first = (pos + i) // 4 // ch
                        break
                m = n // 4 * 4
                xa = array.array('f')
                xa.frombytes(ba[:m])
                xb = array.array('f')
                xb.frombytes(bb[:m])
                for i in range(m // 4):
                    if xa[i] != xb[i]:
                        d = abs(xa[i] - xb[i])
                        if d > maxabs:
                            maxabs = d
            pos += n
    return os.path.getsize(ref_path), os.path.getsize(out_path), first, maxabs


def main():
    if len(sys.argv) not in (2, 3) or (len(sys.argv) == 3 and sys.argv[2] not in ('same', 'shipped')):
        print(__doc__)
        return 2
    label = sys.argv[1]
    ref_name = sys.argv[2] if len(sys.argv) == 3 else 'same'
    ref_dir = os.path.join(HERE, 'pcm', 'ref-' + ref_name)
    version_path = os.path.join(ref_dir, 'ffmpeg-version.txt')
    version = open(version_path, encoding='utf-8').readline().strip() if os.path.exists(version_path) else '?'

    lines = ['x6 PCM parity: loader "%s" vs reference "%s" = %s' % (label, ref_name, version),
             'f32le, native rate and channel count on both sides; frames = samples per channel;'
             ' PASS = byte-identical file (same length, same bytes); max|d| is a float diagnostic',
             '',
             '%-4s %-26s %-6s %-8s %-6s %-2s %-5s %10s %10s %9s %10s %9s  %s' % (
                 '', 'id', 'kind', 'codec', 'rate', 'ch', 'fmt', 'ref', 'loader', 'lost',
                 'first diff', 'max|d|', 'note')]
    fails = 0
    for row in read_manifest():
        p = probe(row['id'])
        inf = loader_info(label, row['id'])
        ref = os.path.join(ref_dir, row['id'] + '.f32')
        out = os.path.join(HERE, 'pcm', label, row['id'] + '.f32')
        cols = ['', '', '', '', '', '']
        note = row['note']
        if inf is None:
            ok, note = False, 'NOT RUN'
        elif row['expect'] == 'unsupported':
            ok = inf['rc'] != 0
            note = ('refused: ' if ok else 'DECODED (expected a refusal): ') + inf['msg']
        elif not os.path.exists(ref):
            ok, note = False, 'no reference PCM'
        elif inf['rc'] != 0 or not os.path.exists(out):
            ok, note = False, 'loader rc=%d %s' % (inf['rc'], inf['msg'])
        else:
            ch = int(inf['fields'].get('channels', p['ch']) or p['ch'] or 1)
            b_ref, b_out, first, maxabs = compare(ref, out, ch)
            ok = b_ref == b_out and first is None
            frame = 4 * ch
            cols = [str(b_ref // frame), str(b_out // frame), str((b_ref - b_out) // frame),
                    '-' if first is None else str(first), '%.3g' % maxabs if first is not None else '0',
                    '']
            if b_ref % frame or b_out % frame:
                note = (note + ' ' if note else '') + 'PARTIAL FRAME: %d / %d bytes' % (b_ref, b_out)
                ok = False
            elif first is not None and maxabs == 0:
                note = (note + ' ' if note else '') + 'bytes differ with equal float values (+0/-0 or NaN)'
        fails += 0 if ok else 1
        lines.append('%-4s %-26s %-6s %-8s %-6s %-2s %-5s %10s %10s %9s %10s %9s  %s' % (
            'PASS' if ok else 'FAIL', row['id'], row['kind'], p['codec'], p['rate'], p['ch'], p['fmt'],
            cols[0], cols[1], cols[2], cols[3], cols[4], note))
    lines += ['', '%d row(s), %d failure(s)' % (len(lines) - 4, fails)]
    text = '\n'.join(lines) + '\n'
    name = 'parity-%s.txt' % label if ref_name == 'same' else 'parity-%s-%s.txt' % (label, ref_name)
    with open(os.path.join(HERE, name), 'w', encoding='utf-8') as f:
        f.write(text)
    sys.stdout.write(text)
    return 1 if fails else 0


if __name__ == '__main__':
    sys.exit(main())
