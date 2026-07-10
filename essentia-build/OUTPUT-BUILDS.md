# output-x64 Build Variants

Two builds of the x64 Essentia extractor suite live side by side. Same source
tree (`essentia-src/`, forked from essentia-master v2.1-beta6-dev), same
toolchain (WSL2 Ubuntu, `x86_64-w64-mingw32-g++`, static link against the
prebuilt deps in `3rdparty-x64-deps.tar.gz`).

## output-x64.1 — original x64 port (2026-02-13)

The initial 64-bit cross-compile of the official (32-bit-only) extractors.
Unpatched upstream algorithm code. Streaming `ChordsDetection` uses
`BufferUsage::forMultipleFrames` (262,144-element output buffer), which caps
analyzable track length at ~12,172 s (~200 min) at the extractor's 44.1 kHz /
2048-hop analysis rate — longer tracks overflow the buffer and the extractor
dies mid-analysis. Truedat's matching pre-flight guard was
`MaxEssentiaDurationSecs = 12000`.

`essentia_streaming_extractor_music.exe`: MD5 `05c4c77bb204026e87b3a593e0d0ed2e`

## output-x64.2 — large-file patch (2026-07-10)

Identical to `.1` plus one local source patch:

- `essentia-src/src/algorithms/tonal/chordsdetection.cpp` — the `_chords` and
  `_strength` output buffers switched from `forMultipleFrames` (262,144
  elements) to `forLargeAudioStream` (1,048,576 elements, see
  `src/essentia/streaming/phantombuffer.h`).

This raises the length ceiling 4× to ~48,695 s (~13.5 h). Truedat's matching
pre-flight guard is the `--max-duration <secs>` flag: it **defaults to 12000**
(safe for the `.1` extractor shipped in dist), and should be passed as
`--max-duration 48000` (800 min) when scanning with the `.2` extractor. Raising
it while running the `.1` binary lets >200-min tracks through to an extractor
that still dies at ~12,172 s (logged as FAILED per track; the scan continues).

RAM note: the bigger buffers are allocated eagerly per extractor process —
about +36 MB each (1.31 M `std::string` slots for chords + floats for
strength). At truedat's default parallelism (cores−2 ≈ 20 workers) that is
under 1 GB system-wide during a scan; accepted as negligible.

`essentia_streaming_extractor_music.exe`: MD5 `f52e166e9063b23cdda538cdf8530ed4`
(byte size is identical to `.1` — the patch swaps an enum constant, which
doesn't change code layout — so distinguish builds by hash, not size).

Build log for `.2`: `build-chordsfix.log` (417 steps, 18m42s).
Rebuild recipe: extract deps (`tar xzf 3rdparty-x64-deps.tar.gz -C 3rdparty/`),
fix the `.pc` prefix paths to the current mount, then `bash build_essentia.sh`
(or `resume-build.sh` for an incremental run — it caps WAF at `-j8` because the
default `-j$(nproc)` OOM-kills mingw on a 15 GB WSL VM).
