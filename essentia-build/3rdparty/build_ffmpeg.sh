#!/usr/bin/env bash
set -e
. ../build_config.sh

echo "Building FFmpeg $FFMPEG_VERSION (x64)"

mux=$1
if test "$1" = "--no-muxers"; then
    echo Building FFmpeg without muxers
    FFMPEG_AUDIO_FLAGS_MUXERS=""
fi

rm -rf tmp
mkdir tmp
cd tmp

curl -SLO https://ffmpeg.org/releases/$FFMPEG_VERSION.tar.gz
tar xf $FFMPEG_VERSION.tar.gz
cd $FFMPEG_VERSION

# Changed: --arch=x86_64 (was x86_32), removed --enable-memalign-hack (i686 only)
./configure \
    $FFMPEG_AUDIO_FLAGS \
    $FFMPEG_AUDIO_FLAGS_MUXERS \
    --prefix=$PREFIX \
    --enable-cross-compile \
    --cross-prefix=$HOST- \
    --arch=x86_64 \
    --target-os=mingw32 \
    --extra-cflags="-I$PREFIX/include" \
    --extra-ldflags="-L$PREFIX/lib" \
    $SHARED_OR_STATIC

make
make install

cd ../..
rm -r tmp
