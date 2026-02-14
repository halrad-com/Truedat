#!/usr/bin/env bash
set -e
. ../build_config.sh

rm -rf tmp
mkdir tmp
cd tmp

echo "Building libsamplerate $LIBSAMPLERATE_VERSION (x64)"

curl -SLO http://www.mega-nerd.com/SRC/$LIBSAMPLERATE_VERSION.tar.gz
tar -xf $LIBSAMPLERATE_VERSION.tar.gz
cd $LIBSAMPLERATE_VERSION

./configure \
    --host=$HOST \
    --prefix=$PREFIX \
    --disable-fftw \
    --disable-sndfile \
    $SHARED_OR_STATIC
make
make install

cd ../..
rm -r tmp
