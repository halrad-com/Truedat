"""Diff the SMFM payloads of two FLAC files, sub-block by sub-block.

Unofficial, reverse-engineered tooling — see SMFM-FORMAT.md for the wire format.
Note: expects the PC-written magic (STAEMMLW); Walkman-written payloads
(STAESMML) will report MAGIC MISMATCH on the first sub-block.
Usage: python flac_smfm_diff.py <a.flac> <b.flac>
"""
import sys, struct

def extract_smfm(path):
    with open(path, "rb") as f:
        if f.read(4) != b"fLaC":
            raise SystemExit(f"{path}: not FLAC")
        while True:
            hdr = f.read(4)
            if len(hdr) < 4: break
            last = bool(hdr[0] & 0x80)
            btype = hdr[0] & 0x7F
            blen = (hdr[1]<<16)|(hdr[2]<<8)|hdr[3]
            data = f.read(blen)
            if btype == 2 and data[:4] == b"SMFM":
                return data[4:]
            if last: break
    raise SystemExit(f"{path}: no SMFM block")

def parse_subblocks(payload):
    """Walk TLV: 4-byte tag, 8-byte 'STAEMMLW', 4-byte flags, 4-byte BE length, payload."""
    blocks = []
    p = 0
    while p + 20 <= len(payload):
        tag = payload[p:p+4]
        magic = payload[p+4:p+12]
        flags = payload[p+12:p+16]
        plen = struct.unpack(">I", payload[p+16:p+20])[0]
        if magic != b"STAEMMLW":
            blocks.append((p, tag, magic, flags, plen, payload[p+20:p+20+plen], "MAGIC MISMATCH"))
            break
        blocks.append((p, tag, magic, flags, plen, payload[p+20:p+20+plen], "ok"))
        p += 20 + plen
    return blocks

def main(a, b):
    pa = extract_smfm(a)
    pb = extract_smfm(b)
    print(f"A: {a}  ({len(pa)} bytes)")
    print(f"B: {b}  ({len(pb)} bytes)")
    print()
    if pa == pb:
        print(">>> SMFM payloads are BYTE-IDENTICAL <<<")
    else:
        print(">>> SMFM payloads DIFFER <<<")
        diff_bytes = sum(1 for x,y in zip(pa,pb) if x!=y)
        print(f"    {diff_bytes} differing bytes (of {min(len(pa),len(pb))} common)")
    print()
    ba = parse_subblocks(pa)
    bb = parse_subblocks(pb)
    print(f"{'tag':<6} {'len':>6}  {'A==B':<5}  {'first-diff':<10}")
    print("-"*60)
    for (oa,ta,_,fa,la,da,sa), (ob,tb,_,fb,lb,db,sb) in zip(ba, bb):
        same = "YES" if da == db else "no"
        first_diff = ""
        if da != db:
            for i,(x,y) in enumerate(zip(da,db)):
                if x != y:
                    first_diff = f"off+{i} {x:02x}->{y:02x}"
                    break
        tag = ta.decode("ascii", errors="replace")
        flag_note = "" if fa == fb else f" flags A={fa.hex()} B={fb.hex()}"
        print(f"{tag:<6} {la:>6}  {same:<5}  {first_diff:<10}{flag_note}")
    # Per-block byte-by-byte for differing blocks
    print()
    for (_,ta,_,_,_,da,_), (_,_,_,_,_,db,_) in zip(ba, bb):
        if da == db: continue
        tag = ta.decode("ascii", errors="replace")
        diff_count = sum(1 for x,y in zip(da,db) if x!=y)
        print(f"--- {tag} differs in {diff_count}/{min(len(da),len(db))} bytes ---")
        # show first 64 bytes side by side
        for i in range(0, min(len(da), len(db), 256), 16):
            ha = " ".join(f"{b:02x}" for b in da[i:i+16])
            hb = " ".join(f"{b:02x}" for b in db[i:i+16])
            mark = "".join("." if x==y else "X" for x,y in zip(da[i:i+16], db[i:i+16]))
            print(f"  {i:04x}  A {ha}")
            print(f"        B {hb}")
            print(f"        D {' '.join('  ' if c=='.' else 'XX' for c in mark)}")
            print()

if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
