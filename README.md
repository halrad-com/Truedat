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
| `ffmpeg.exe` | `59c8a17f012f148bdce12b93f8cba2d37f281a3e0e856d505608ca5eaa4520ed` |
| `ffprobe.exe` | `2fac541ccf404a600c62c9f7be14600dd98ae5679defb31f5ed2fe71e3a388a8` |
| `ffplay.exe` | `21f5d5880eddfc5dab0f0dedb1d21a0c3ce1dba62ae8776d227739a3521d25f6` |

- **Version**: `2026-10-08-git-ec420ba161-full_build-www.gyan.dev`
- **Compiler**: gcc 16.2.0 (Rev4, MSYS2)
- **License**: GPL-3.0+ (`--enable-gpl --enable-version3`)
- **Download**: [GyanD release 2026-10-08-git-ec420ba161](https://github.com/GyanD/codexffmpeg/releases/tag/2026-10-08-git-ec420ba161) — "git master full" build (`ffmpeg-2026-10-08-git-ec420ba161-full_build.7z`)
- **Note**: Truedat uses `ffmpeg.exe` (downmix / retry / authenticity / `--transcode`) and `ffprobe.exe` (`--transcode` source-property matching). `ffplay.exe` ships with the FFmpeg distribution and is unused.
