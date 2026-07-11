"""Dump FLAC metadata blocks, focusing on the Sony SMFM application block.

Unofficial, reverse-engineered tooling — see SMFM-FORMAT.md for the wire format.
Usage: python flac_smfm_dump.py <file.flac>
"""
import sys, struct

BLOCK_TYPES = {0:"STREAMINFO",1:"PADDING",2:"APPLICATION",3:"SEEKTABLE",
               4:"VORBIS_COMMENT",5:"CUESHEET",6:"PICTURE"}

def fourcc(b):
    return "".join(chr(x) if 32<=x<127 else "?" for x in b)

def hexdump(data, base=0, width=16, max_bytes=None):
    out = []
    if max_bytes is not None:
        data = data[:max_bytes]
    for i in range(0, len(data), width):
        chunk = data[i:i+width]
        hexpart = " ".join(f"{b:02x}" for b in chunk)
        asciipart = "".join(chr(b) if 32<=b<127 else "." for b in chunk)
        out.append(f"{base+i:08x}  {hexpart:<{width*3}}  {asciipart}")
    return "\n".join(out)

def scan_tlv(payload, start=4):
    """Look for repeating 4cc + 4-byte-len (BE & LE) patterns starting after the 4-byte app ID."""
    candidates = []
    p = start
    while p + 8 <= len(payload):
        tag = payload[p:p+4]
        if all(32<=b<127 for b in tag):
            be = struct.unpack(">I", payload[p+4:p+8])[0]
            le = struct.unpack("<I", payload[p+4:p+8])[0]
            candidates.append((p, fourcc(tag), be, le))
        p += 1
    return candidates

def parse_flac(path):
    with open(path, "rb") as f:
        magic = f.read(4)
        if magic != b"fLaC":
            print(f"Not a FLAC file (magic={magic!r})")
            return
        print(f"File: {path}")
        print(f"Magic: fLaC\n")
        blocks = []
        while True:
            hdr = f.read(4)
            if len(hdr) < 4:
                break
            first = hdr[0]
            last = bool(first & 0x80)
            btype = first & 0x7F
            blen = (hdr[1]<<16)|(hdr[2]<<8)|hdr[3]
            data = f.read(blen)
            blocks.append((btype, blen, data, last))
            tname = BLOCK_TYPES.get(btype, f"RESERVED({btype})")
            extra = ""
            if btype == 2 and len(data) >= 4:
                extra = f" app_id={fourcc(data[:4])} ({data[:4].hex()})"
            print(f"BLOCK type={btype:>2} {tname:<16} len={blen:>8}{extra}{' [LAST]' if last else ''}")
            if last:
                break
        print()
        for btype, blen, data, _ in blocks:
            if btype == 2:
                appid = data[:4]
                payload = data[4:]
                print("="*80)
                print(f"APPLICATION block: id={fourcc(appid)} ({appid.hex()})  payload={len(payload)} bytes")
                print("="*80)
                print(hexdump(payload, max_bytes=4096))
                if len(payload) > 4096:
                    print(f"... ({len(payload)-4096} more bytes)")
                print()
                # Look for GBPM and other plausible TLV tags
                print("--- Candidate 4-char ASCII tokens in payload ---")
                seen = set()
                for off, tag, belen, lelen in scan_tlv(payload, start=0):
                    if tag in seen: continue
                    if any(c=="?" for c in tag): continue
                    # filter to uppercase/printable likely-tags
                    if not all(c.isalnum() or c in "_ " for c in tag): continue
                    seen.add(tag)
                    plausible = ""
                    if 0 < belen < len(payload):
                        plausible += f" be_len_ok={belen}"
                    if 0 < lelen < len(payload):
                        plausible += f" le_len_ok={lelen}"
                    print(f"  off={off:>5}  tag={tag}  be_len={belen:>10}  le_len={lelen:>10}{plausible}")

if __name__ == "__main__":
    parse_flac(sys.argv[1])
