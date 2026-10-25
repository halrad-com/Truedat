# spike-x6

Verification tooling and evidence for the `output-x64.6` extractor build. The build
record is the `.6` section of `../OUTPUT-BUILDS.md`. Scripts and small text evidence are
tracked; generated audio, decoded PCM, extractor outputs and build trees are not
(`.gitignore`) and regenerate from the scripts below.

## Layout

| Path | What it is |
|---|---|
| `make-corpus.sh` | Builds the corpus: synthetic rows generated with `dist/truedat/ffmpeg.exe` (deterministic, byte-identical on regeneration — `corpus-md5.txt`), plus real files listed in the local, gitignored `real-files.tsv` (`id`, absolute path, `decode`/`unsupported`, note). Writes `manifest.tsv` (local) and `corpus/`. |
| `ref/build-ffmpeg-ref.sh` | Cross-builds `ref/ffmpeg-7.1.1-mingw.exe` (gitignored) from the extractor's own FFmpeg version, configure flags and toolchain: the PCM reference. `references.txt` pins both references and the known differences of the shipped 8.x `ffmpeg.exe`. |
| `run-parity.sh`, `pcm_parity.py` | Decode every row with a reference and with `essentia_standard_audioloader_dump` (the `AudioLoader`'s own output); a row passes when the two f32 files are byte-identical. Tables: `parity-<label>[-shipped].txt`. |
| `asan/` | Native FFmpeg 7.1.1 built with `--toolchain=gcc-asan --disable-asm`, and a harness around the loader's decode header. `asan-report.txt` (the loader), `asan-legacy-report.txt` (the `.5` conversion, as the instrument check). |
| `fixtures-*.txt` | `essentia_test_audioloader_decodeloop` against the `.5` and `.6` loops, native and MinGW. |
| `run-repeat.sh` | Runs an extractor over the corpus in truedat's call shapes (`<build>.<mode>...`; see the header), with the `x6profile` timing on or off. |
| `band.py` | `same`: the same-binary repeat-run band (`repeat-band.*`). `cross`: a per-field table of two builds against it (`field-table-*.txt`). `scan`: the reduced-profile gate against `scan-allowlist.txt` (`gate-scan-*.txt`). |
| `espb_compare.py` | The same two steps for the frames file of the edges call shape (`espb-band.tsv`, `gate-edges-*.txt`). |
| `profile_table.py` | Reduces `x6profile` output to per-phase / per-algorithm elapsed tables and side-by-side deltas (`profile-*.txt`), pooling only rows common to the compared labels; the per-run process CPU is the CPU figure. `cpu-baseline.txt` is the full extractor's table before any cut (with its correction), `cpu-table.txt` the per-cut table on the drop 2 binary. |
| `scan-allowlist.txt`, `cut_candidates.py` | The extractor keys truedat reads, and the full output split against them (`cut-candidates.txt`). |
| `scan-profile.yaml`, `scan-edges-profile.yaml`, `profiles/` | The reduced profiles, the full profile spelled out, and one profile per cut for measuring each alone. |
| `run-drop2-passes.sh`, `gitbash.cmd` | Wrappers that run the measurement and gate passes from a one-shot scheduled task. |
| `run-drop3-passes.sh` | The drop 3 stage passes (`m1`/`m2` builds) against the drop 2 outputs; the stages are folded into `.7` (`.6` is frozen at drop 2). |
| `mem_fit.py`, `mem-fit.txt` | Peak working set (the `peakws_mib` field of the `x6profile` header) against audio length over the real rows: the per-audio-second memory slope. |
| `make-prod-corpus.sh`, `prod_share.py`, `step0-decode.txt` | `.7` step 0: the production-shaped corpus (rows above 44.1 kHz converted with truedat's own pre-resample command first, `manifest-prod.tsv`, local) run through `run-repeat.sh <build>.prof.scanedges.prod`, and what a shared decode could recover from it. |
| `run-hpcp-bench.sh`, `hpcp_summary.py`, `hpcp-bench.txt` | `.7` step 0: `essentia_standard_x7_hpcp_bench` (`src/examples/standard_x7_hpcp_bench.cpp`) on the real rows - the three HPCP configurations, the walk a fused chords+tuning HPCP would share, and the per-call buffer allocation. |
| `test_ape_regression.py`, `ape-regression.md` | Standalone APE regression (existing binaries, offline): loader byte-exact, full extraction, 48 kHz analysis case; the pinned fixture is `corpus/ape_luckynight.ape`. |
| `*-v1.txt` | Earlier versions of a table, kept beside the current one where a tool's rule changed. |

## Method notes

- Reference: `ffmpeg -i <in> -map 0:a:0 -c:a pcm_f32le -f f32le`, no `-ar`/`-ac`, because the
  loader converts sample format only. Encoder delay and end padding are applied inside
  libavcodec/libavformat on both sides; nothing is trimmed by the scripts.
- Acceptance: byte equality wherever a transformation is deterministic (PCM; extractor
  outputs on rows where the full extractor repeats exactly); otherwise the same-binary band,
  per key first — the pooled family band cannot fail for a family whose band exceeds 1.
- Timing: `x6profile` values are elapsed per `process()` call; measurement passes run one
  at a time with nothing else on the machine.

## Regenerate

```
bash make-corpus.sh
wsl.exe bash ref/build-ffmpeg-ref.sh                      # scheduled task
bash run-parity.sh <label> <..._audioloader_dump.exe> [same|shipped]
python -I pcm_parity.py <label> [same|shipped]
gitbash.cmd run-repeat.sh <spec> <label> [<label>...]     # scheduled task
python -I band.py same|cross|scan ...
python -I espb_compare.py band|gate <a> <b>
python -I profile_table.py <label> [<label>...]
wsl.exe bash asan/build-ffmpeg-asan.sh                    # scheduled task
wsl.exe bash asan/run-asan.sh [--legacy-capacity]
```
