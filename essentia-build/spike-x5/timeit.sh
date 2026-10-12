#!/usr/bin/env bash
# Runs the given command and prints its wall-clock seconds on the last line.
# Used for the .4 / .5 timing comparison on the spike WAV (bash's `time` is a
# keyword, not a command, so it does not fit a one-command allowlist shape).
s=${EPOCHREALTIME/./}   # microseconds as an integer (no bc in Git Bash)
"$@"
rc=$?
e=${EPOCHREALTIME/./}
ms=$(( (e - s) / 1000 ))
printf 'ELAPSED=%d.%03d s  rc=%d\n' $((ms / 1000)) $((ms % 1000)) "$rc"
exit $rc
