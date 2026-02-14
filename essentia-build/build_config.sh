#!/bin/sh
# Essentia x64 Windows cross-compile config
# Forked from essentia-master/packaging/build_config.sh
# Changed: i686 -> x86_64, removed i686-specific FFTW hacks

HOST=x86_64-w64-mingw32
# Resolve PREFIX relative to this script's location, not pwd.
# Individual build scripts source this from 3rdparty/ via ../build_config.sh,
# so $(pwd) varies. BASH_SOURCE always points here.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ -z "${PREFIX}" ]; then
  PREFIX="$SCRIPT_DIR/3rdparty"
fi
echo Installing to: $PREFIX

SHARED_OR_STATIC="
--disable-shared
--enable-static
"

EIGEN_VERSION=3.3.7
FFMPEG_VERSION=ffmpeg-7.1.1
LAME_VERSION=3.100
TAGLIB_VERSION=taglib-1.11.1
ZLIB_VERSION=zlib-1.2.12
FFTW_VERSION=fftw-3.3.2
LIBSAMPLERATE_VERSION=libsamplerate-0.1.8
LIBYAML_VERSION=yaml-0.1.5
CHROMAPRINT_VERSION=1.5.1

FFMPEG_AUDIO_FLAGS="
    --disable-programs
    --disable-doc
    --disable-debug

    --disable-avdevice
    --disable-swresample
    --disable-swscale
    --disable-postproc
    --disable-avfilter
    --enable-swresample

    --disable-network
    --disable-indevs
    --disable-outdevs
    --disable-muxers
    --disable-demuxers
    --disable-encoders
    --disable-decoders
    --disable-bsfs
    --disable-filters
    --disable-parsers
    --disable-protocols
    --disable-hwaccels

    --enable-protocol=file
    --enable-protocol=pipe

    --disable-sdl2
    --disable-lzma
    --disable-zlib
    --disable-xlib
    --disable-bzlib
    --disable-libxcb

    --enable-demuxer=image2
    --enable-demuxer=aac
    --enable-demuxer=ac3
    --enable-demuxer=aiff
    --enable-demuxer=ape
    --enable-demuxer=asf
    --enable-demuxer=au
    --enable-demuxer=avi
    --enable-demuxer=flac
    --enable-demuxer=flv
    --enable-demuxer=matroska
    --enable-demuxer=mov
    --enable-demuxer=m4v
    --enable-demuxer=mp3
    --enable-demuxer=mpc
    --enable-demuxer=mpc8
    --enable-demuxer=ogg
    --enable-demuxer=pcm_alaw
    --enable-demuxer=pcm_mulaw
    --enable-demuxer=pcm_f64be
    --enable-demuxer=pcm_f64le
    --enable-demuxer=pcm_f32be
    --enable-demuxer=pcm_f32le
    --enable-demuxer=pcm_s32be
    --enable-demuxer=pcm_s32le
    --enable-demuxer=pcm_s24be
    --enable-demuxer=pcm_s24le
    --enable-demuxer=pcm_s16be
    --enable-demuxer=pcm_s16le
    --enable-demuxer=pcm_s8
    --enable-demuxer=pcm_u32be
    --enable-demuxer=pcm_u32le
    --enable-demuxer=pcm_u24be
    --enable-demuxer=pcm_u24le
    --enable-demuxer=pcm_u16be
    --enable-demuxer=pcm_u16le
    --enable-demuxer=pcm_u8
    --enable-demuxer=rm
    --enable-demuxer=shorten
    --enable-demuxer=tak
    --enable-demuxer=tta
    --enable-demuxer=wav
    --enable-demuxer=wv
    --enable-demuxer=xwma

    --enable-decoder=aac
    --enable-decoder=aac_latm
    --enable-decoder=ac3
    --enable-decoder=alac
    --enable-decoder=als
    --enable-decoder=ape
    --enable-decoder=atrac1
    --enable-decoder=atrac3
    --enable-decoder=eac3
    --enable-decoder=flac
    --enable-decoder=gsm
    --enable-decoder=gsm_ms
    --enable-decoder=mp1
    --enable-decoder=mp1float
    --enable-decoder=mp2
    --enable-decoder=mp2float
    --enable-decoder=mp3
    --enable-decoder=mp3float
    --enable-decoder=mp3adu
    --enable-decoder=mp3adufloat
    --enable-decoder=mp3on4
    --enable-decoder=mp3on4float
    --enable-decoder=mpc7
    --enable-decoder=mpc8
    --enable-decoder=ra_144
    --enable-decoder=ra_288
    --enable-decoder=ralf
    --enable-decoder=shorten
    --enable-decoder=tak
    --enable-decoder=truehd
    --enable-decoder=tta
    --enable-decoder=vorbis
    --enable-decoder=wavpack
    --enable-decoder=wmalossless
    --enable-decoder=wmapro
    --enable-decoder=wmav1
    --enable-decoder=wmav2
    --enable-decoder=wmavoice

    --enable-decoder=pcm_alaw
    --enable-decoder=pcm_bluray
    --enable-decoder=pcm_dvd
    --enable-decoder=pcm_f32be
    --enable-decoder=pcm_f32le
    --enable-decoder=pcm_f64be
    --enable-decoder=pcm_f64le
    --enable-decoder=pcm_lxf
    --enable-decoder=pcm_mulaw
    --enable-decoder=pcm_s8
    --enable-decoder=pcm_s8_planar
    --enable-decoder=pcm_s16be
    --enable-decoder=pcm_s16be_planar
    --enable-decoder=pcm_s16le
    --enable-decoder=pcm_s16le_planar
    --enable-decoder=pcm_s24be
    --enable-decoder=pcm_s24daud
    --enable-decoder=pcm_s24le
    --enable-decoder=pcm_s24le_planar
    --enable-decoder=pcm_s32be
    --enable-decoder=pcm_s32le
    --enable-decoder=pcm_s32le_planar
    --enable-decoder=pcm_u8
    --enable-decoder=pcm_u16be
    --enable-decoder=pcm_u16le
    --enable-decoder=pcm_u24be
    --enable-decoder=pcm_u24le
    --enable-decoder=pcm_u32be
    --enable-decoder=pcm_u32le

    --enable-parser=aac
    --enable-parser=aac_latm
    --enable-parser=ac3
    --enable-parser=cook
    --enable-parser=dca
    --enable-parser=flac
    --enable-parser=gsm
    --enable-parser=mlp
    --enable-parser=mpegaudio
    --enable-parser=tak
    --enable-parser=vorbis
    --enable-parser=vp3
    --enable-parser=vp8
"

FFMPEG_AUDIO_FLAGS_MUXERS="
    --enable-libmp3lame
    --enable-muxer=wav
    --enable-muxer=aiff
    --enable-muxer=mp3
    --enable-muxer=ogg
    --enable-muxer=flac
    --enable-encoder=pcm_s16le
    --enable-encoder=pcm_s16be
    --enable-encoder=libmp3lame
    --enable-encoder=vorbis
    --enable-encoder=flac
"

# FFTW flags for x64
# Removed --with-incoming-stack-boundary=2 (i686-only, fixes 32-bit stack alignment)
# x86_64 ABI guarantees 16-byte stack alignment and malloc returns 16-byte aligned
# --with-our-malloc16: FFTW's own aligned allocator. Required because cross-compile
#   configure can't run test programs to detect MinGW's _aligned_malloc.
FFTW_FLAGS="
    --enable-float
    --enable-sse2
    --enable-avx
    --with-our-malloc16
"

LIBSAMPLERATE_FLAGS="
    --disable-fftw
    --disable-sndfile
"
