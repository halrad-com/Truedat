# truedat-deps

Third-party binary dependencies for [Truedat](https://github.com/halrad-com/Truedat). Place these alongside `truedat.exe`.

This is the `truedat-deps` branch of the Truedat repo (binaries via Git LFS), kept out of `main`'s history and checked out locally as a sibling folder of the source checkout.

## Essentia

`essentia_streaming_extractor_music.exe` is the audio feature extractor Truedat invokes for a default scan. It is **built from source** (`essentia-build/` in the main repo) as a static x86-64 MinGW cross-compile — the official Essentia builds are 32-bit only and hit a 2 GB memory ceiling on large audio files. The former 32-bit `essentia_streaming_extractor_music_i686.exe` is no longer carried.

| File | SHA-256 |
|------|---------|
| `essentia_streaming_extractor_music.exe` | `6441e29e5e6ed180d0d6ba78f4ee94240571eb38a657210a83a76756892e7229` |

- **Version**: music extractor `music 2.0`, Essentia 2.1-beta6-dev
- **Architecture**: PE32+ x86-64 (static)
- **License**: AGPL-3.0 (commercial licensing available from UPF — https://essentia.upf.edu/licensing_information.html)
- **Upstream**: [Essentia](https://essentia.upf.edu/) by Music Technology Group, Universitat Pompeu Fabra
- **Build notes**: `essentia-build/bringup.md` in the main repo

## FFmpeg

Optional. Enables multi-channel downmixing, the `Unsupported codec` decode retry (e.g. `.opus`), the bitUsage / HF-analysis authenticity signals, and the standalone `--transcode` utility. Pre-built binary downloaded from the project below — not built by Truedat.

| File | SHA-256 |
|------|---------|
| `ffmpeg.exe` | `9c6b03ec0c5b5efc6c471ec81d3bdc99b50d88c0cdefbfe0cb6e1b55e28cd331` |
| `ffprobe.exe` | `307a1f169d1e3c0c099aa3eb384a5de53b1a489ccb9b9a4c2d3082b8bf1d2188` |
| `ffplay.exe` | `8c268f0046fe9f1c31e55bd37d038907823221c92f90986b653d60d5a8ba14c3` |

- **Version**: `2026-08-20-git-7d77562d2a-full_build-www.gyan.dev`
- **Compiler**: gcc 16.1.0 (Rev2, MSYS2)
- **License**: GPL-3.0+ (`--enable-gpl --enable-version3`)
- **Download**: https://www.gyan.dev/ffmpeg/builds/ — "git master full" build (`ffmpeg-<date>-git-<hash>-full_build.7z`)
- **Note**: Truedat uses `ffmpeg.exe` (downmix / retry / authenticity / `--transcode`) and `ffprobe.exe` (`--transcode` source-property matching). `ffplay.exe` ships with the FFmpeg distribution and is unused.
