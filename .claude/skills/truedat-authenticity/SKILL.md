---
name: truedat-authenticity
description: Authenticity sprint state — fingerprint.v1, bitUsage, HF analysis, the verdict's known signal gaps and the --no-bitusage / --no-hf-analysis opt-outs. Read before touching the hi-res or transcode verdict.
---

<!-- Moved verbatim from the root CLAUDE.md on 2026-10-22 (Claude Code doctor, check 4) so it loads only when relevant. The root file keeps the universal rules and a pointer here. -->

## Authenticity sprint state (2026-05-18)

Phases 1–5 shipped — `bitDepth` + `encoder` in `fingerprint.v1` (Phase 1), `mp3LameTag` block (Phase 2), `bitUsage` block (Phase 2.5), `hfEnergyRatio` (Phase 3), `truedat.*` verdict block (Phase 4), `hfSpectralStructure` FFT-derived signal block (Phase 5 — adds Signal F at weight 0.35 to the hi-res vote, consolidates the Phase-3 two-ffmpeg pipeline into a single managed-FFT pipe). Current verdict method tag: **`truedat-v1-fft-rategate-2026-08-28`**.

A local test corpus exists; details are recorded in the gitignored `docs/reviews/` tree. Phase 5 retune scorecard: 23/23 hi-res classifications correct (the 3 ffmpeg-upsampled-fake FLAC misses from Phase 4 are now suppressed or correctly classified).

Known signal gaps remaining (Phase 5+ work, NOT bugs):
- **No authenticity coverage for 44.1 kHz lossless** — the commonest file in a ripped-CD library. Both ffmpeg passes decline it (`ComputeBitUsage` needs ≥24-bit, `ComputeHfAnalysis` needs >44.1 kHz), the transcode axis is MP3/AAC-only, and the hi-res axis now abstains. A 16/44.1 FLAC is *unanalysed*, not "analysed and passed". Closes with BACKLOG item 2 (spectral-ceiling + cliff detector) — the gate change exposed this gap, it did not create it.
- LAME-to-LAME re-encode chains verdict "no" (second LAME encode rewrites Xing tag) — needs cascade-encode artifact detection.
- `hfSpectralStructure.imagingSymmetry` is currently unused in the vote (Lanczos suppression neutralized it on corpus-1); the field is still emitted and tracked for future-corpus tuning.

**Opt-outs for slow scans.** `--no-bitusage` suppresses `ComputeBitUsage` (omits the `bitUsage` JSON block); `--no-hf-analysis` suppresses `ComputeHfAnalysis` (omits `hfEnergyRatio` / `hfEnergyMethod` / `hfSpectralStructure`). Each saves one ffmpeg subprocess + 30 s mid-track decode per track at all three fan-out sites. The `truedat` verdict block still emits but with a reduced signal set (Signal A lost with `--no-bitusage`; Signals B + F with `--no-hf-analysis`) — borderline cases more often return `"unknown"`. `truedat.method` carries the algorithm-version stamp regardless of which signals fed in. Both flags are orthogonal to staging — combine with `--no-stage` for forensic runs, or use alone to cut wall-clock without giving up source caching. The cache canary is **not** widened to recognise entries scanned with these flags as "partial" — the bitUsage / hf fields are explicitly optional, matching the existing ffmpeg-absent install pattern. Backfill these fields with `truedat --verify --backfill --backfill-level features`.

For session resume: read the latest `SESSION-RESUME-*.md` in the local (gitignored) `docs/` directory first.
