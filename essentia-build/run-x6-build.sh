#!/usr/bin/env bash
# One-shot wrapper for the extractor build (output-x64.6 onward), launched via
# a Windows scheduled task: long builds die under the agent Bash tool's 600 s
# timeout. Incremental on the existing essentia-src/build object cache (no
# reconfigure, no clean) like .5 was on .4. The final log line is EXIT=<code>
# of the WAF build (or of the copy, if that failed).
#   run-x6-build.sh <dest dir> [<log file>]   (both relative to essentia-build/)
#     <dest dir> is REQUIRED: the source tree's build id moves on (.6 -> .7 ...)
#     while a frozen output dir stays the fallback, so a default destination
#     would overwrite it. A dest whose extractor is committed to git is refused
#     (EXIT=4) before anything is built; spike dirs (gitignored) are reused freely.
#     log default: build-x6.log
# Copies the extractor to <dest dir> (the shipped artifact) and the x6/x7
# verification tools (essentia_standard_audioloader_dump,
# essentia_test_audioloader_decodeloop, essentia_standard_x7_hpcp_bench) to
# spike-x6/out/tools-<dest basename>/, which is gitignored - they are evidence
# tooling, not part of the build.
#   -j6: uncapped -j$(nproc) OOM-kills mingw g++ (~1 GB/process) on a 15 GB WSL VM.
cd "$(dirname "$0")" || exit 1
LOG="$(pwd)/${2:-build-x6.log}"
if [ -z "$1" ]; then
  echo "run-x6-build.sh: a destination dir is required (e.g. spike-x6/out/build-<name>)" | tee "$LOG"
  echo "EXIT=4" >> "$LOG"
  exit 4
fi
DEST="$1"
EXE="$DEST/essentia_streaming_extractor_music.exe"
# git missing or failing counts as committed: the unsafe direction is the one to refuse
if [ -e "$EXE" ] && { ! command -v git >/dev/null || git ls-files --error-unmatch "$EXE" >/dev/null 2>&1; }; then
  echo "run-x6-build.sh: $EXE is committed (or git is unavailable to say otherwise) - refusing to overwrite it; build to a new dir" | tee "$LOG"
  echo "EXIT=4" >> "$LOG"
  exit 4
fi
echo "START $(date) dest=$DEST" > "$LOG"
export PKG_CONFIG_PATH="$(pwd)/3rdparty/lib/pkgconfig"
cd essentia-src || { echo "EXIT=2" >> "$LOG"; exit 2; }
python3 waf -j6 >> "$LOG" 2>&1
rc=$?
echo "WAF_EXIT=$rc" >> "$LOG"
if [ $rc -eq 0 ]; then
  TOOLS="../spike-x6/out/tools-$(basename "$DEST")"
  mkdir -p "../$DEST" "$TOOLS"
  cp build/src/examples/essentia_streaming_extractor_music.exe "../$DEST/" >> "$LOG" 2>&1 || rc=3
  for exe in essentia_standard_audioloader_dump essentia_test_audioloader_decodeloop essentia_standard_x7_hpcp_bench essentia_test_audioloader_spool essentia_test_b5_streaming; do
    cp "build/src/examples/$exe.exe" "$TOOLS/" >> "$LOG" 2>&1 || rc=3
  done
  echo "COPIED extractor to $DEST, tools to $TOOLS (rc $rc)" >> "$LOG"
  md5sum "../$DEST/"*.exe "$TOOLS/"*.exe >> "$LOG" 2>&1
fi
echo "END $(date)" >> "$LOG"
echo "EXIT=$rc" >> "$LOG"
exit "$rc"   # the caller (schtasks) must see a failed build as failed, not the echo's 0
