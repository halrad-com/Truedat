#!/usr/bin/env bash
# Builds and runs the x7 shared-decode spool tests (src/examples/
# test_audioloader_spool.cpp, header-only code) natively under AddressSanitizer
# and UndefinedBehaviorSanitizer. Run in WSL:
#   wsl.exe bash essentia-build/spike-x6/asan/run-spool-asan.sh
# Output: spike-x6/spool-test.txt (the test's lines + the sanitizer verdict).
set -u
cd "$(dirname "$0")/.." || exit 1
src=../essentia-src/src/examples/test_audioloader_spool.cpp
mkdir -p asan/work
out=spool-test.txt
{
  echo "x7 spool tests, native, -fsanitize=address,undefined ($(g++ --version | head -n 1))"
  g++ -std=c++11 -O1 -g -fno-omit-frame-pointer -fsanitize=address,undefined -fno-sanitize-recover=all \
      -Wall -o asan/work/test_audioloader_spool "$src" 2>&1
  echo "build rc=$?"
  ASAN_OPTIONS=detect_leaks=1:abort_on_error=1 asan/work/test_audioloader_spool 2>&1
  echo "run rc=$?"
} > "$out"
cat "$out"
