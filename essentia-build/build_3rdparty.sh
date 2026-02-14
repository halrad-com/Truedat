#!/usr/bin/env bash
# Build all Essentia x64 third-party dependencies
# Run from WSL: cd /mnt/c/Users/scott/source/repos/MBX/truedat/essentia-build && bash build_3rdparty.sh
set -e

BASEDIR=$(dirname $0)
cd $BASEDIR/3rdparty
rm -rf bin dynamic include lib share

echo "============================================"
echo " Building Essentia x64 dependencies"
echo "============================================"
echo ""

echo "[1/9] Eigen3 (header-only)..."
./build_eigen3.sh

echo "[2/9] FFTW3..."
./build_fftw3.sh

echo "[3/9] LAME..."
./build_lame.sh

echo "[4/9] FFmpeg..."
./build_ffmpeg.sh

echo "[5/9] libsamplerate..."
./build_libsamplerate.sh

echo "[6/9] zlib..."
./build_zlib.sh

echo "[7/9] TagLib..."
./build_taglib.sh

echo "[8/9] libyaml..."
./build_yaml.sh

echo "[9/9] Chromaprint..."
./build_chromaprint.sh

rm -rf bin dynamic share

echo ""
echo "============================================"
echo " All x64 dependencies built successfully"
echo " Installed to: $(pwd)"
echo "============================================"
