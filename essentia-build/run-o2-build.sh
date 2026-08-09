#!/usr/bin/env bash
# One-shot wrapper for the output-x64.3 (-O2) extractor build, launched via a
# Windows scheduled task (long builds die under the agent Bash tool's timeout).
# Logs to build-o2.log; final line records the exit code.
cd "$(dirname "$0")" || exit 1
bash build_essentia.sh > build-o2.log 2>&1
echo "EXIT=$?" >> build-o2.log
