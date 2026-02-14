#!/usr/bin/env bash
# Build Essentia streaming_extractor_music for Windows x64
# Run from WSL: cd /mnt/c/Users/scott/source/repos/MBX/truedat/essentia-build && bash build_essentia.sh
#
# Prerequisites:
#   1. Run build_3rdparty.sh first
#   2. sudo apt-get install g++-mingw-w64-x86-64 python3
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SRC_DIR="$SCRIPT_DIR/essentia-src"
DEPS_DIR="$SCRIPT_DIR/3rdparty"
OUTPUT_DIR="$SCRIPT_DIR/output-x64"

echo "============================================"
echo " Building Essentia x64 extractor"
echo "============================================"
echo " Source:  $SRC_DIR"
echo " Deps:   $DEPS_DIR"
echo " Output: $OUTPUT_DIR"
echo ""

# Verify dependencies were built
if [ ! -d "$DEPS_DIR/lib" ]; then
    echo "ERROR: Dependencies not found. Run build_3rdparty.sh first."
    exit 1
fi

# Verify cross-compiler is installed
if ! command -v x86_64-w64-mingw32-g++ &> /dev/null; then
    echo "ERROR: x86_64-w64-mingw32-g++ not found."
    echo "Install with: sudo apt-get install g++-mingw-w64-x86-64"
    exit 1
fi

cd "$SRC_DIR"

# Clean previous build
rm -rf build

# The wscript hardcodes i686-w64-mingw32-{gcc,g++,ar} for --cross-compile-mingw32.
# Patch the source to use x86_64 BEFORE configure, so find_program() succeeds.
echo "Patching wscript for x86_64 cross-compiler..."
sed -i 's/i686-w64-mingw32-gcc/x86_64-w64-mingw32-gcc/g' wscript
sed -i 's/i686-w64-mingw32-g++/x86_64-w64-mingw32-g++/g' wscript
sed -i 's/i686-w64-mingw32-ar/x86_64-w64-mingw32-ar/g' wscript

# Also patch the default pkg-config path to point to our deps
sed -i "s|packaging/win32_3rdparty/lib/pkgconfig|$DEPS_DIR/lib/pkgconfig|g" wscript

# Configure with WAF
# --with-static-examples: builds extractors as static executables
# --cross-compile-mingw32: enables MinGW cross-compilation
export PKG_CONFIG_PATH="$DEPS_DIR/lib/pkgconfig"

python3 waf configure \
    --with-static-examples \
    --cross-compile-mingw32 \
    --pkg-config-path="$DEPS_DIR/lib/pkgconfig" \
    --prefix="$OUTPUT_DIR"

# Build
python3 waf

# Copy extractor to output
mkdir -p "$OUTPUT_DIR"
find build/src/examples -name "*.exe" -exec cp {} "$OUTPUT_DIR/" \;

echo ""
echo "============================================"
echo " Build complete!"
echo " Extractor(s) in: $OUTPUT_DIR/"
echo "============================================"
ls -lh "$OUTPUT_DIR/"*.exe 2>/dev/null || echo "(no .exe files found - check build output)"
