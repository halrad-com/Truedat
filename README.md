# truedat-deps

Third-party binary dependencies for [Truedat](https://dev.azure.com/halrad/MBX). Place these alongside `truedat.exe`.

These are pre-built binaries downloaded from their respective projects — not built by Truedat.

## FFmpeg

Enables multi-channel audio downmixing and `--details` audio probing in Truedat.

| File | SHA-256 |
|------|---------|
| `ffmpeg.exe` | `bd6ebaf8c2d35e7bcabbcda85a13fe2dba02c30e4ebf7e9efe63803b026e4b8a` |
| `ffprobe.exe` | `54834a3a0ddbf1871f59faf4eb2267b63ee845da0cceea8d35a8e7370741b77d` |
| `ffplay.exe` | `02a12d11c873380dbb1a497484510406021879203d1d392d24bbaa7549907cdf` |

- **Version**: `2026-02-09-git-9bfa1635ae-full_build-www.gyan.dev`
- **Compiler**: gcc 15.2.0 (Rev11, MSYS2)
- **License**: GPL-3.0+ (`--enable-gpl --enable-version3`)
- **Download**: https://www.gyan.dev/ffmpeg/builds/ — "release full" build
- **Note**: Only `ffmpeg.exe` and `ffprobe.exe` are used by Truedat. `ffplay.exe` ships with the FFmpeg distribution.
