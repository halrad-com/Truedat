#!/usr/bin/env bash
set -e
. ../build_config.sh

rm -rf tmp
mkdir tmp
cd tmp

echo "Building fftw $FFTW_VERSION (x64)"

curl -SLO http://www.fftw.org/$FFTW_VERSION.tar.gz
tar -xf $FFTW_VERSION.tar.gz
cd $FFTW_VERSION

./configure \
    --host=$HOST \
    --prefix=$PREFIX \
    $FFTW_FLAGS \
    --with-windows-f77-mangling \
    --enable-threads \
    --with-combined-threads \
    --enable-portable-binary \
    $SHARED_OR_STATIC
make
make install

cd ../..
rm -r tmp
