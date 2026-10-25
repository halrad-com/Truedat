#!/usr/bin/env python3
"""x6 drop 2: compare ESPB v1 frames files (the edges call shape's output).

    python -I espb_compare.py band <label a> <label b> [<label> ...]
        Runs of the full extractor (e.g. x6ea, pea, sedges), every pair: per
        descriptor, the largest relative difference seen on any row and how
        many values moved. Writes espb-band.tsv - the frame-level run-to-run
        band; a row is exact only if no pair moved a value on it.

    python -I espb_compare.py gate <full label> <candidate label>
        The drop 2 edges gate: on every row with a .bin in both labels, the
        candidate's descriptor set, dtypes, counts and dims must equal the
        full run's; on rows where the full extractor repeats exactly (0 moved
        values in espb-band.tsv for that row) the files must be byte-identical;
        elsewhere each descriptor's max relative difference must be within 2x
        its band. Writes gate-edges-<full>-<candidate>.txt.

The parser follows essentia-build/FRAMES-BINARY-FORMAT.md (as spike-x5/
check_espb.py does). The header tag (the build id) may differ. Standard
library only."""
import math
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
DTYPES = {1: "f32_scalar", 2: "f32_vector", 3: "f32_matrix", 4: "string", 5: "string_vector"}


def read_espb(path):
    with open(path, "rb") as f:
        b = f.read()
    if b[:4] != b"ESPB":
        raise ValueError("%s: bad magic %r" % (path, b[:4]))
    ver, bom, count, payload_off = struct.unpack_from("<IIIQ", b, 4)
    if ver != 1 or bom != 0x04030201:
        raise ValueError("%s: bad version/byte-order %d/%08x" % (path, ver, bom))
    pos = 24
    (tl,) = struct.unpack_from("<I", b, pos)
    pos += 4
    tag = b[pos:pos + tl].decode("utf-8")
    pos += tl
    table = []
    for _ in range(count):
        (nl,) = struct.unpack_from("<I", b, pos)
        pos += 4
        name = b[pos:pos + nl].decode("utf-8")
        pos += nl
        dtype, flags, n, dims, off, ln = struct.unpack_from("<IIIIQQ", b, pos)
        pos += 32
        table.append((name, dtype, flags, n, dims, off, ln))
    out = {}
    for name, dtype, flags, n, dims, off, ln in table:
        raw = b[off:off + ln]
        if dtype in (1, 2, 3):
            vals = struct.unpack_from("<%df" % (n * (dims if dtype == 3 else 1)), raw)
        else:
            p, vals = 0, []
            for _ in range(n):
                (sl,) = struct.unpack_from("<I", raw, p)
                p += 4
                vals.append(raw[p:p + sl].decode("utf-8"))
                p += sl
            vals = tuple(vals)
        out[name] = (DTYPES[dtype], flags, n, dims, vals)
    return tag, out


def manifest_ids():
    with open(os.path.join(HERE, "manifest.tsv"), encoding="utf-8") as f:
        next(f)
        return [l.split("\t")[0] for l in f]


def bin_path(label, row_id):
    return os.path.join(HERE, "out", label, row_id + ".bin")


def compare(a, b):
    """-> (problems, {descriptor: (moved, max_rel)}). A problem is a shape
    difference or a finite/non-finite transition: relative arithmetic on a NaN
    or an infinity yields NaN, so such a change used to count as moved with a
    relative error of zero and pass any band (overwatch review V2, 2026-10-25).
    Two NaNs, or the same infinity, are equal."""
    problems = []
    diffs = {}
    for name in sorted(set(a) | set(b)):
        if name not in a or name not in b:
            problems.append("%s only in %s" % (name, "a" if name in a else "b"))
            continue
        ta, fa, na, da, va = a[name]
        tb, fb, nb, db, vb = b[name]
        if (ta, fa, na, da) != (tb, fb, nb, db):
            problems.append("%s shape a=%s b=%s" % (name, (ta, fa, na, da), (tb, fb, nb, db)))
            continue
        moved, max_rel, nonfinite = 0, 0.0, 0
        if ta.startswith("f32"):
            for x, y in zip(va, vb):
                if x == y or (math.isnan(x) and math.isnan(y)):
                    continue
                moved += 1
                if not (math.isfinite(x) and math.isfinite(y)):
                    nonfinite += 1
                    continue
                r = abs(x - y) / max(1e-12, abs(x), abs(y))
                if r > max_rel:
                    max_rel = r
        elif va != vb:
            moved = sum(1 for x, y in zip(va, vb) if x != y)
            max_rel = 1.0
        if nonfinite:
            problems.append("%s: %d finite/non-finite value(s)" % (name, nonfinite))
        diffs[name] = (moved, max_rel)
    return problems, diffs


def run_codes(label):
    """{row id: exit code} from out/<label>/runs.tsv (run-repeat.sh); {} if absent."""
    codes = {}
    path = os.path.join(HERE, "out", label, "runs.tsv")
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            next(f, None)
            for line in f:
                p = line.rstrip("\n").split("\t")
                if len(p) >= 2 and p[1].lstrip("-").isdigit():
                    codes[p[0]] = int(p[1])
    return codes


def stale(label, row_id):
    """True when out/<label>/started (run-repeat.sh's start stamp, written after it
    clears the label's old outputs) is newer than this row's frames file."""
    stamp = os.path.join(HERE, "out", label, "started")
    path = bin_path(label, row_id)
    return os.path.exists(stamp) and os.path.exists(path) and os.path.getmtime(path) < os.path.getmtime(stamp)


def rows_with_both(la, lb):
    return [i for i in manifest_ids() if os.path.exists(bin_path(la, i)) and os.path.exists(bin_path(lb, i))]


def band(labels):
    per_desc, per_row = {}, {}
    pairs = [(labels[i], labels[j]) for i in range(len(labels)) for j in range(i + 1, len(labels))]
    for la, lb in pairs:
        for i in rows_with_both(la, lb):
            _, a = read_espb(bin_path(la, i))
            _, b = read_espb(bin_path(lb, i))
            problems, diffs = compare(a, b)
            if problems:
                print("%s: SHAPE DIFFERS between same-binary runs (%s/%s): %s" % (i, la, lb, "; ".join(problems[:3])))
            per_row[i] = per_row.get(i, 0) + sum(m for m, _ in diffs.values())
            for name, (moved, r) in diffs.items():
                pm, pr = per_desc.get(name, (0, 0.0))
                per_desc[name] = (pm + moved, max(pr, r))
    with open(os.path.join(HERE, "espb-band.tsv"), "w", encoding="utf-8") as f:
        f.write("# same-binary frames band: %s, %d pair(s), over %d row(s)\n" % (" ".join(labels), len(pairs), len(per_row)))
        f.write("descriptor\tmoved_values\tmax_rel\n")
        for name in sorted(per_desc):
            f.write("%s\t%d\t%.6g\n" % (name, per_desc[name][0], per_desc[name][1]))
        f.write("#rows\n")
        for i in manifest_ids():
            if i in per_row:
                f.write("#row\t%s\t%d\n" % (i, per_row[i]))
    moved = sorted(per_desc.items(), key=lambda kv: -kv[1][1])
    print("espb band over %d rows: %d descriptors, %d moved anywhere" % (
        len(per_row), len(per_desc), sum(1 for _, (m, _) in moved if m)))
    for name, (m, r) in moved[:12]:
        print("  %-45s moved %8d  max rel %.3g" % (name, m, r))
    print("exact rows: %s" % ", ".join(i for i, m in per_row.items() if m == 0))
    return 0


def read_espb_band():
    per_desc, exact_rows = {}, set()
    with open(os.path.join(HERE, "espb-band.tsv"), encoding="utf-8") as f:
        for line in f:
            if line.startswith("#row\t"):
                _, i, m = line.rstrip("\n").split("\t")
                if int(m) == 0:
                    exact_rows.add(i)
            elif line.startswith("#") or line.startswith("descriptor\t"):
                continue
            else:
                name, m, r = line.rstrip("\n").split("\t")
                per_desc[name] = float(r)
    return per_desc, exact_rows


def gate(full, cand):
    per_desc, exact_rows = read_espb_band()
    out = ["x6 drop 2 edges gate: %s (full) vs %s (candidate), ESPB frames files" % (full, cand),
           "shape (descriptors, dtype, flags, count, dims) must match; exact rows byte-identical;"
           " other rows within 2x the descriptor's same-binary band (espb-band.tsv)",
           "", "%-26s %-6s %5s %9s %9s %7s  %s" % ("id", "exact", "descs", "moved", "max_rel", "beyond", "note")]
    fails = compared = 0
    codes = run_codes(cand)
    # coverage first (overwatch review V1): every row the FULL run wrote a frames
    # file for must have a fresh one from the candidate, with exit 0
    for i in manifest_ids():
        have_a, have_b = os.path.exists(bin_path(full, i)), os.path.exists(bin_path(cand, i))
        why = ""
        if have_a and not have_b:
            why = "candidate frames file MISSING"
        elif have_b and not have_a:
            why = "baseline frames file missing - cannot judge the candidate"
        elif have_a and codes.get(i, 0) != 0:
            why = "candidate exit %d" % codes[i]
        elif have_a and stale(cand, i):
            why = "candidate frames file STALE (older than the label's start stamp)"
        if why:
            fails += 1
            out.append("%-26s COVERAGE FAIL: %s" % (i, why))
    for i in rows_with_both(full, cand):
        if codes.get(i, 0) != 0 or stale(cand, i):
            continue   # already failed on coverage
        compared += 1
        pa, pb = bin_path(full, i), bin_path(cand, i)
        exact = i in exact_rows
        with open(pa, "rb") as fa, open(pb, "rb") as fb:
            identical = fa.read() == fb.read()
        _, a = read_espb(pa)
        _, b = read_espb(pb)
        problems, diffs = compare(a, b)
        moved = sum(m for m, _ in diffs.values())
        max_rel = max([r for _, r in diffs.values()] or [0.0])
        beyond = [(r, n) for n, (m, r) in diffs.items() if m and r > 2 * per_desc.get(n, 0.0)]
        ok = not problems and (identical if exact else not beyond)
        # the header tag carries the build id: both are output-x64.6 here, so an
        # exact row must be byte-identical, tag included
        fails += 0 if ok else 1
        note = "; ".join(problems[:2]) if problems else ("" if ok else "; ".join("%s %.2g" % (n, r) for r, n in sorted(beyond, reverse=True)[:3]))
        if exact and not identical and not problems and moved == 0:
            note = "exact row, 0 values moved, but bytes differ (header?)"
        out.append("%-26s %-6s %5d %9d %9.3g %7d  %s%s" % (
            i, "yes" if exact else "band", len(a), moved, max_rel, len(beyond), "" if ok else "FAIL ", note))
    out += ["", "%d row(s) compared, %d failure(s)" % (compared, fails),
            "PASS" if fails == 0 and compared > 0 else ("FAIL: no row compared" if not compared else "FAIL")]
    text = "\n".join(out) + "\n"
    with open(os.path.join(HERE, "gate-edges-%s-%s.txt" % (full, cand)), "w", encoding="utf-8") as f:
        f.write(text)
    sys.stdout.write(text)
    return 1 if fails or not compared else 0


def main(argv):
    if len(argv) >= 3 and argv[0] == "band":
        return band(argv[1:])
    if len(argv) == 3 and argv[0] == "gate":
        return gate(argv[1], argv[2])
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
