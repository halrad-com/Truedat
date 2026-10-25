#!/usr/bin/env python3
"""Projected peak working set of N extractor workers together, with and
without the .7 RAM spool, on a box of R GiB, using the measured per-worker
model (mem-fit.txt: peak = 106.6 MiB + 0.4141 MiB per audio-second on drop 2)
and the cap truedat will pass, min(256 MiB, 25% of physical RAM / N).

A track is spooled only when its spool (352,800 B per audio-second, 44.1 kHz
stereo f32) fits the cap, so the spool's worst case is a track just under the
cap's length; longer tracks are not spooled and cost exactly what they cost
today. Standard library only.
    python -I ram_tiers.py"""
import sys

INTERCEPT, SLOPE = 106.6, 0.4141           # MiB, MiB per audio-second (mem-fit.txt)
SPOOL = 352800 / 1048576.0                 # MiB per audio-second, 44.1 kHz stereo f32


def cap_mib(ram_gib, workers):
    return min(256.0, 0.25 * ram_gib * 1024.0 / workers)


def per_worker(length_s, cap):
    spool = SPOOL * length_s
    return INTERCEPT + SLOPE * length_s + (spool if spool <= cap * (1 + 1e-9) else 0.0)   # the cap-length track IS spooled


def main():
    out = ["peak working set, all workers together (MiB), model from mem-fit.txt; cap = min(256 MiB, 25% RAM / N)",
           "worst spooled track = the longest that still fits the cap; 5-min track = typical; drop 2 = no spool", ""]
    out.append("%5s %7s %8s %9s | %22s %22s | %22s" % (
        "RAM", "workers", "cap MiB", "cap len s", "worst spooled: .7 (d2)", "5-min track: .7 (d2)", "share of RAM, worst .7"))
    for ram in (8, 16, 32):
        for n in (4, 8, 20):
            cap = cap_mib(ram, n)
            lcap = cap / SPOOL
            worst7 = n * per_worker(lcap, cap)
            worst2 = n * (INTERCEPT + SLOPE * lcap)
            five7 = n * per_worker(300.0, cap)
            five2 = n * (INTERCEPT + SLOPE * 300.0)
            out.append("%3d GB %7d %8.1f %9.0f | %10.0f (%9.0f) %10.0f (%9.0f) | %20.0f%%" % (
                ram, n, cap, lcap, worst7, worst2, five7, five2, 100.0 * worst7 / (ram * 1024.0)))
    # an alternative that bounds the worst case itself: the worst spooled track
    # costs INTERCEPT + SLOPE*L + SPOOL*L per worker with L = cap / SPOOL, so
    # N of them stay within 60% of RAM when cap <= (0.6*RAM/N - INTERCEPT) / (1 + SLOPE/SPOOL)
    out += ["", "alternative cap = min(256, max(0, (60%% RAM / N - %.1f) / (1 + %.4f / %.4f))) - the worst spooled track, all workers, <= 60%% of RAM:" % (
                INTERCEPT, SLOPE, SPOOL),
            "%5s %7s %8s %9s | %22s | %22s" % ("RAM", "workers", "cap MiB", "cap len s", "worst spooled: .7", "share of RAM, worst .7")]
    for ram in (8, 16, 32):
        for n in (4, 8, 20):
            cap = min(256.0, max(0.0, (0.6 * ram * 1024.0 / n - INTERCEPT) / (1.0 + SLOPE / SPOOL)))
            lcap = cap / SPOOL
            worst7 = n * max(per_worker(lcap, cap), INTERCEPT + SLOPE * 300.0)
            out.append("%3d GB %7d %8.1f %9.0f | %22.0f | %20.0f%%" % (
                ram, n, cap, lcap, worst7, 100.0 * worst7 / (ram * 1024.0)))
    out += ["",
            "Not in the model: the OS, truedat.exe itself, and tracks longer than the cap's length -",
            "those are unspooled and grow at 0.414 MiB per audio-second exactly as today (a 60-min",
            "track = %.0f MiB per worker on .6 and .7 alike)." % (INTERCEPT + SLOPE * 3600)]
    sys.stdout.write("\n".join(out) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
