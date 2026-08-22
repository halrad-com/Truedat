# Truedat - External Dependencies

Binary dependencies not built by this project. Place alongside `truedat.exe` in `dist/truedat/`.

FFmpeg binaries are `.gitignore`d — stored on the [`truedat-deps`](https://github.com/halrad-com/Truedat/tree/truedat-deps) branch via Git LFS.

## FFmpeg (Optional)

Enables multi-channel audio downmixing, the `Unsupported codec` decode retry (e.g. `.opus`), the `bitUsage` / HF-analysis authenticity signals, and the standalone `--transcode` utility (`ffprobe.exe` matches source rate/depth for `--transcode`).

| File | Size | SHA-256 |
|------|------|---------|
| `ffmpeg.exe` | 224 MB | `9c6b03ec0c5b5efc6c471ec81d3bdc99b50d88c0cdefbfe0cb6e1b55e28cd331` |
| `ffprobe.exe` | 224 MB | `307a1f169d1e3c0c099aa3eb384a5de53b1a489ccb9b9a4c2d3082b8bf1d2188` |
| `ffplay.exe` | 225 MB | `8c268f0046fe9f1c31e55bd37d038907823221c92f90986b653d60d5a8ba14c3` |

- **Version**: `2026-08-20-git-7d77562d2a-full_build-www.gyan.dev`
- **Compiler**: gcc 16.1.0 (Rev2, MSYS2)
- **License**: GPL-3.0+ (`--enable-gpl --enable-version3`)
- **Download**: https://www.gyan.dev/ffmpeg/builds/ — "git master full" build (`ffmpeg-<date>-git-<hash>-full_build.7z`)
- **Branch**: [`truedat-deps`](https://github.com/halrad-com/Truedat/tree/truedat-deps)
- **Note**: Only `ffmpeg.exe` and `ffprobe.exe` are used by truedat. `ffplay.exe` ships with the FFmpeg distribution but is not needed.

## Essentia

The x64 extractor built from `essentia-build/` (see `bringup.md`) is carried on the [`truedat-deps`](https://github.com/halrad-com/Truedat/tree/truedat-deps) branch as well as in [`dist/truedat/`](https://github.com/halrad-com/Truedat/tree/main/dist/truedat) — the two copies are byte-identical.

| File | SHA-256 |
|------|---------|
| `essentia_streaming_extractor_music.exe` | `6441e29e5e6ed180d0d6ba78f4ee94240571eb38a657210a83a76756892e7229` |

### i686 (superseded)

The original 32-bit build was removed from the `truedat-deps` branch tip; the official Essentia builds are 32-bit only and hit a 2 GB memory ceiling on large audio files. It remains recoverable from that branch's history at commit `e0aef1c`.

| File | SHA-256 |
|------|---------|
| `essentia_streaming_extractor_music_i686.exe` | `c7847bd4c6e1c3a737b8cd5f94e7889792b086e368a6d71e2890229733b8bda1` |

- **Build date**: Feb 13, 2026
