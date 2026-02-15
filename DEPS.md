# Truedat - External Dependencies

Binary dependencies not built by this project. Place alongside `truedat.exe` in `dist/truedat/`.

FFmpeg binaries are `.gitignore`d — stored on the [`truedat-deps`](https://github.com/halrad-com/Truedat/tree/truedat-deps) branch via Git LFS.

## FFmpeg (Optional)

Enables multi-channel audio downmixing and `--details` audio probing.

| File | Size | SHA-256 |
|------|------|---------|
| `ffmpeg.exe` | 222 MB | `bd6ebaf8c2d35e7bcabbcda85a13fe2dba02c30e4ebf7e9efe63803b026e4b8a` |
| `ffprobe.exe` | 222 MB | `54834a3a0ddbf1871f59faf4eb2267b63ee845da0cceea8d35a8e7370741b77d` |
| `ffplay.exe` | 224 MB | `02a12d11c873380dbb1a497484510406021879203d1d392d24bbaa7549907cdf` |

- **Version**: `2026-02-09-git-9bfa1635ae-full_build-www.gyan.dev`
- **Compiler**: gcc 15.2.0 (Rev11, MSYS2)
- **License**: GPL-3.0+ (`--enable-gpl --enable-version3`)
- **Download**: https://www.gyan.dev/ffmpeg/builds/ — "release full" build
- **Branch**: [`truedat-deps`](https://github.com/halrad-com/Truedat/tree/truedat-deps)
- **Note**: Only `ffmpeg.exe` and `ffprobe.exe` are used by truedat. `ffplay.exe` ships with the FFmpeg distribution but is not needed.

## Essentia i686 (Archived)

Original 32-bit build, superseded by the x64 builds in [`dist/truedat/`](https://github.com/halrad-com/Truedat/tree/main/dist/truedat). Archived on the [`truedat-deps`](https://github.com/halrad-com/Truedat/tree/truedat-deps) branch.

| File | SHA-256 |
|------|---------|
| `essentia_streaming_extractor_music_i686.exe` | `c7847bd4c6e1c3a737b8cd5f94e7889792b086e368a6d71e2890229733b8bda1` |

- **Build date**: Feb 13, 2026
- **Source**: `essentia-build/` in this repo (see `bringup.md`)
