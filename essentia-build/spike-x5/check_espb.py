"""ESPB v1 checker for the output-x64.5 frames writer (see FRAMES-BINARY-FORMAT.md).

  python -I check_espb.py <frames.bin> [<frames.json>] [--expect prefix ...]

Parses the binary per the format doc, lists its descriptors, and (optionally)
cross-checks every descriptor against the extractor's `_frames` JSON for the same
track: names, dims, float values within 1e-6 relative, strings equal. With
--expect, every descriptor in the binary must match one of the prefixes (exact
name or prefix followed by a dot). Exit 1 on any failure."""
import json, math, struct, sys

DTYPES = {1: "f32_scalar", 2: "f32_vector", 3: "f32_matrix", 4: "string", 5: "string_vector"}

def read_espb(path):
    with open(path, "rb") as f:
        b = f.read()
    if b[:4] != b"ESPB": sys.exit("bad magic %r" % b[:4])
    ver, bom, count, payload_off = struct.unpack_from("<IIIQ", b, 4)
    if ver != 1 or bom != 0x04030201: sys.exit("bad version/byte-order %d/%08x" % (ver, bom))
    pos = 24
    (tl,) = struct.unpack_from("<I", b, pos); pos += 4
    tag = b[pos:pos + tl].decode("utf-8"); pos += tl
    table = []
    for _ in range(count):
        (nl,) = struct.unpack_from("<I", b, pos); pos += 4
        name = b[pos:pos + nl].decode("utf-8"); pos += nl
        dtype, flags, n, dims, off, ln = struct.unpack_from("<IIIIQQ", b, pos); pos += 32
        table.append((name, dtype, flags, n, dims, off, ln))
    if pos != payload_off: sys.exit("table ends at %d but payloadOffset says %d" % (pos, payload_off))
    out, expect_end = {}, payload_off
    for name, dtype, flags, n, dims, off, ln in table:
        if off != expect_end: sys.exit("%s: offset %d, expected %d (payloads must be contiguous)" % (name, off, expect_end))
        expect_end = off + ln
        raw = b[off:off + ln]
        if dtype in (1, 2):
            val = list(struct.unpack_from("<%df" % n, raw)); val = val[0] if dtype == 1 else val
            assert ln == 4 * n * dims
        elif dtype == 3:
            flat = struct.unpack_from("<%df" % (n * dims), raw)
            val = [list(flat[r * dims:(r + 1) * dims]) for r in range(n)]
        else:
            p, strs = 0, []
            for _ in range(n):
                (sl,) = struct.unpack_from("<I", raw, p); p += 4
                strs.append(raw[p:p + sl].decode("utf-8")); p += sl
            if p != ln: sys.exit("%s: string payload length mismatch" % name)
            val = strs[0] if dtype == 4 else strs
        out[name] = (DTYPES[dtype], flags, n, dims, val)
    if expect_end != len(b): sys.exit("file is %d bytes, layout says %d" % (len(b), expect_end))
    return tag, out

def flatten(node, prefix, out):
    if isinstance(node, dict):
        for k, v in node.items(): flatten(v, prefix + "." + k if prefix else k, out)
    else:
        out[prefix] = node

def close(a, b, tol=1e-6):
    if isinstance(a, float) and isinstance(b, float) and math.isnan(a) and math.isnan(b): return 0.0, 0.0
    d = abs(a - b); return d, d / max(1.0, abs(b))

def main(argv):
    if "--expect" in argv:
        i = argv.index("--expect"); expect, argv = argv[i + 1:], argv[:i]
    else:
        expect = None
    tag, bin_ = read_espb(argv[0])
    print("ESPB v1  tag=%r  descriptors=%d" % (tag, len(bin_)))
    for name in sorted(bin_):
        t, flags, n, dims, _ = bin_[name]
        print("  %-55s %-13s flags=%d count=%-6d dims=%d" % (name, t, flags, n, dims))
    failures = 0
    if len(argv) > 1:
        js = {}; flatten(json.load(open(argv[1], encoding="utf-8")), "", js)
        if expect is not None:  # the JSON holds everything; only its filtered subset must be in the binary
            js = {k: v for k, v in js.items() if any(k == p or k.startswith(p + ".") for p in expect)}
        max_abs = max_rel = 0.0; compared = 0
        for name, jv in sorted(js.items()):
            if name not in bin_: print("MISSING in binary: %s" % name); failures += 1; continue
            t, flags, n, dims, bv = bin_[name]
            if isinstance(jv, (int, float)) and isinstance(bv, float): pairs = [(bv, float(jv))]
            elif isinstance(jv, str): pairs = []; failures += (bv != jv); compared += 1
            elif jv and isinstance(jv[0], list):
                if len(jv) != n or len(jv[0]) != dims: print("SHAPE %s json %dx%d bin %dx%d" % (name, len(jv), len(jv[0]), n, dims)); failures += 1; continue
                pairs = [(x, float(y)) for row_b, row_j in zip(bv, jv) for x, y in zip(row_b, row_j)]
            elif jv and isinstance(jv[0], str): pairs = []; failures += (bv != jv); compared += 1
            else:
                if len(jv) != n: print("COUNT %s json %d bin %d" % (name, len(jv), n)); failures += 1; continue
                pairs = [(x, float(y)) for x, y in zip(bv if isinstance(bv, list) else [bv], jv)]
            bad = []  # indices (flat) of values beyond tolerance, so a run-to-run frame difference is located, not just flagged
            for idx, (x, y) in enumerate(pairs):
                a, r = close(x, y); max_abs = max(max_abs, a); max_rel = max(max_rel, r); compared += 1
                if r > 1e-6: bad.append(idx)
            if bad:
                failures += 1
                per_row = dims if (jv and isinstance(jv, list) and isinstance(jv[0], list)) else 1
                frames = sorted(set(i // per_row for i in bad))
                print("DIFF %-45s %d/%d values differ in %d frames (first %d, last %d); first: bin=%r json=%r"
                      % (name, len(bad), len(pairs), len(frames), frames[0], frames[-1], pairs[bad[0]][0], pairs[bad[0]][1]))
        extra = sorted(set(bin_) - set(js))
        for name in extra: print("EXTRA in binary (not in json): %s" % name); failures += 1
        print("compared %d values/strings across %d descriptors; max abs diff %.3g, max rel diff %.3g" % (compared, len(js), max_abs, max_rel))
    if expect is not None:
        bad = [k for k in bin_ if not any(k == p or k.startswith(p + ".") for p in expect)]
        for k in bad: print("UNEXPECTED (fails framesInclude): %s" % k)
        failures += len(bad)
        print("framesInclude check: %d descriptors, %d outside the %d prefixes" % (len(bin_), len(bad), len(expect)))
    print("RESULT: %s" % ("PASS" if failures == 0 else "FAIL (%d)" % failures))
    return 1 if failures else 0

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
