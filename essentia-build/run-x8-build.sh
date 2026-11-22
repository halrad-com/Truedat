#!/usr/bin/env bash
# One-shot wrapper for the output-x64.8 extractor build: the shipped sources and
# flags, built PROFILE-GUIDED with LINK-TIME OPTIMIZATION. Launched via a Windows
# scheduled task like run-x6-build.sh (long builds die under the agent Bash
# tool's 600 s timeout); the final log line is EXIT=<code>.
#
#   run-x8-build.sh <dest dir> <training list | profile.tar.gz> [<log file>]
#       <dest dir>, <log file>: relative to essentia-build/ (log default build-x8.log)
#       <training list>: a text file, one audio file per line (blank lines and
#           lines starting with # ignored). A path may be absolute (C:/... or
#           /mnt/c/...) or relative to the list file's own folder. The list and
#           the audio are local - they are not part of the repository.
#       <profile.tar.gz>: the counters a previous run recorded (it writes them to
#           <dest dir>/profile-gcda.tar.gz). Given one, steps 1 and 2 are skipped
#           and step 3 is built from it - which is how the build is REPEATED
#           without the audio. The committed output-x64.8/profile-gcda.tar.gz is
#           the profile the shipped binary was built from. Counters only fit the
#           sources and compiler they were recorded with: after a source change,
#           or on another gcc, record a new profile instead.
#       The build tree records which profile its optimized objects were compiled
#       from (the MD5 of the counter archive, in <tree>/.x8-profile-md5). waf
#       tracks sources and flags but not the counters, so a build handed a
#       DIFFERENT profile than the tree was built from removes the whole tree
#       first and compiles everything again; the same profile reuses it. A
#       different <dest dir> alone changes nothing - it is only where the
#       extractor is copied.
#   X8_OUT=<name>   build in essentia-src/build/<name> instead of x8 (a plain
#                   folder name; the profile rule above applies to it too)
#
# What it does, in one out dir (essentia-src/build/x8, with its own WAFLOCK, so
# the plain `python3 waf` path that run-x6-build.sh uses is never reconfigured
# under it):
#   1. INSTRUMENTED build   -fprofile-generate
#   2. TRAINING run         the instrumented extractor over every listed file, in
#                           truedat's scan-with-edges call shape, so the recorded
#                           profile is of the code a scan actually runs
#   3. OPTIMIZED build      -fprofile-use -fprofile-correction -flto
#                           (-Wno-missing-profile: code the training never reached
#                           is simply compiled as before)
# and copies the extractor to <dest dir>.
#
# What it does NOT change: -O2 -ffp-contract=off, baseline x86-64, no -march, no
# -ffast-math. The profile and LTO move code around and inline across files; they
# do not license the compiler to reorder floating-point arithmetic, and the third-
# party libraries (FFmpeg, FFTW, TagLib, ...) are the prebuilt static ones, not
# recompiled. The build is still gated on values before it is accepted - see
# OUTPUT-BUILDS.md - because "should not change values" is a claim, not a result.
#
# The same refusal as run-x6-build.sh: a destination whose extractor is committed
# to git is not overwritten (EXIT=4).
#
# How the extra flags get in without editing the wscript: waf appends $CXXFLAGS /
# $CFLAGS / $LINKFLAGS / $LDFLAGS during ctx.load('compiler_cxx'), which runs
# AFTER the wscript's mingw branch assigns the shipped list, so the shipped flags
# stay exactly as they are and these follow them. LDFLAGS matters: the static-
# example feature in src/examples/wscript assigns LINKFLAGS = ['-static'] for the
# extractor, so a link flag carried only in LINKFLAGS never reaches its link line
# (an instrumented build then fails with every __gcov_* symbol undefined).
#
# Where the profile counters land: gcc records each object's absolute build path
# and looks for its .gcda beside the object. The instrumented extractor is a
# WINDOWS program, where the build's /mnt/... path does not exist, so the training
# run points GCOV_PREFIX at a folder (passed through WSLENV) and the counters are
# copied from there onto the build tree. Counters left by an earlier training run
# are removed first: an optimized build must never read a stale profile.
#
#   -j8: mingw g++ peaks around 0.5 GB per process on this tree; X8_JOBS overrides.
cd "$(dirname "$0")" || exit 1
ROOT="$(pwd)"
LOG="$ROOT/${3:-build-x8.log}"
fail() { echo "run-x8-build.sh: $1" | tee -a "$LOG"; echo "EXIT=${2:-4}" >> "$LOG"; exit "${2:-4}"; }
: > "$LOG"
[ -n "${1:-}" ] && [ -n "${2:-}" ] || fail "usage: run-x8-build.sh <dest dir> <training list | profile.tar.gz> [<log file>]"
DEST="$1"
LIST="$2"
[ -f "$LIST" ] || fail "training list or profile archive not found: $LIST"
LISTDIR="$(cd "$(dirname "$LIST")" && pwd)"
LIST="$LISTDIR/$(basename "$LIST")"
PROFILE_TAR=""
case "$LIST" in *.tar.gz|*.tgz) PROFILE_TAR="$LIST" ;; esac
EXE="$DEST/essentia_streaming_extractor_music.exe"
if [ -e "$EXE" ]; then
  if command -v git >/dev/null; then git ls-files --error-unmatch "$EXE" >/dev/null 2>&1; tracked=$?; else tracked=127; fi
  [ "$tracked" -eq 1 ] || fail "$EXE is committed, or git could not say (exit $tracked) - refusing to overwrite it; build to a new dir"
fi
JOBS="${X8_JOBS:-8}"
# X8_OUT names another out dir under essentia-src/build/ (default x8). A bare folder
# name only: a tree built from another profile is removed whole (below), so the name
# must never reach outside essentia-src/build/.
NAME="${X8_OUT:-x8}"
case "$NAME" in *[!A-Za-z0-9._-]*|.|..) fail "X8_OUT must be a plain folder name under essentia-src/build/, not: $NAME" ;; esac
OUT="build/$NAME"
BUILD="$ROOT/essentia-src/$OUT"
TRAIN="$BUILD-train"
GCOV="$BUILD-gcov"
# Which profile the optimized objects in the tree were compiled from. waf decides
# "up to date" from sources and flags; the .gcda counters are neither, so a rebuild
# from another profile into a tree a previous run used would find every object up to
# date and link the previous profile's code. The stamp is what closes that gap.
STAMP="$BUILD/.x8-profile-md5"
export PKG_CONFIG_PATH="$ROOT/3rdparty/lib/pkgconfig"
export WAFLOCK=".lock-waf-$(basename "$OUT")"
echo "START $(date) dest=$DEST list=$LIST" >> "$LOG"

build() {   # build <step name> <compile flags> <link flags>
  echo "== $1: CXXFLAGS+=$2 | LDFLAGS+=$3" >> "$LOG"
  ( cd "$ROOT/essentia-src" || exit 2
    export CXXFLAGS="$2" CFLAGS="$2" LINKFLAGS="$3" LDFLAGS="$3"
    python3 waf configure --out="$OUT" --with-static-examples --cross-compile-mingw32 \
        --pkg-config-path="$PKG_CONFIG_PATH" --prefix="$BUILD/prefix" >> "$LOG" 2>&1 || exit 5
    grep -m1 "^CXXFLAGS" "$OUT/c4che/_cache.py" >> "$LOG" 2>&1
    # one target: configure lists every example, each a static link of its own
    python3 waf -j"$JOBS" --targets=essentia_streaming_extractor_music >> "$LOG" 2>&1 ) || {
      rc=$?; echo "$1 FAILED (rc $rc)" >> "$LOG"; echo "EXIT=$rc" >> "$LOG"; exit "$rc"; }
  echo "$1 ok $(date)" >> "$LOG"
}

if [ -n "$PROFILE_TAR" ]; then
# ---- recorded profile given: no instrumented build, no training
PROFILE_MD5="$(md5sum < "$PROFILE_TAR" | cut -d' ' -f1)"
BUILT_FROM="$(cat "$STAMP" 2>/dev/null)"
if [ -d "$BUILD" ] && [ "$BUILT_FROM" != "$PROFILE_MD5" ]; then
  # missing stamp included: a tree whose profile is not known is not trusted
  echo "build tree $OUT was built from profile ${BUILT_FROM:-(not recorded)}, not $PROFILE_MD5: removing it, every object is compiled again" >> "$LOG"
  rm -rf "$BUILD" && [ ! -e "$BUILD" ] || fail "cannot remove the build tree $BUILD" 6
elif [ -d "$BUILD" ]; then
  echo "build tree $OUT was built from this profile: its objects are reused" >> "$LOG"
fi
mkdir -p "$BUILD"
echo "stale counters removed: $(find "$BUILD" -name '*.gcda' -print -delete | wc -l)" >> "$LOG"
tar -xzf "$PROFILE_TAR" -C "$BUILD" >> "$LOG" 2>&1 || { echo "cannot unpack $PROFILE_TAR" >> "$LOG"; echo "EXIT=6" >> "$LOG"; exit 6; }
echo "$PROFILE_MD5" > "$STAMP"
echo "profile: $(basename "$PROFILE_TAR") md5 $PROFILE_MD5; counters in the build tree: $(find "$BUILD" -name '*.gcda' | wc -l)" >> "$LOG"
else
# ---- 1. instrumented
rm -f "$STAMP"   # the tree's objects are about to come from no recorded profile
build instrumented "-fprofile-generate" "-fprofile-generate"
INSTR="$BUILD/src/examples/essentia_streaming_extractor_music.exe"

# ---- 2. training
rm -rf "$TRAIN" "$GCOV"
mkdir -p "$TRAIN" "$GCOV"
# The profile truedat hands the extractor on a scan with edges
# (Truedat/Edges/EdgeProducer.cs: ProfileText() + ScanProfileKeys). A copy that has
# fallen behind that file costs optimization quality, never correctness.
cat > "$TRAIN/profile.yaml" <<'YAML'
outputFrames: 1
outputFormat: json
framesInclude: [lowlevel.loudness_ebu128, lowlevel.mfcc, tonal.hpcp, rhythm.beats_position, rhythm.beats_loudness, lowlevel.spectral_centroid, lowlevel.spectral_flux, lowlevel.spectral_rolloff, lowlevel.spectral_entropy, lowlevel.spectral_complexity, lowlevel.dissonance, lowlevel.pitch_salience, lowlevel.zerocrossingrate, lowlevel.spectral_rms, lowlevel.spectral_energy, lowlevel.spectral_strongpeak, metadata.audio_properties]
lowlevel.stats: [mean, stdev]
tonal.stats: [mean, stdev]
rhythm.stats: [mean]
lowlevel.mfccStats: [mean, cov]
lowlevel.gfccStats: [mean]
x6.melbands128: 0
x6.bandsStorage: 0
x6.md5: 1
YAML
export GCOV_PREFIX="$(wslpath -w "$GCOV")"
export GCOV_PREFIX_STRIP=0
export WSLENV="GCOV_PREFIX:GCOV_PREFIX_STRIP${WSLENV:+:$WSLENV}"
n=0; ok=0
while IFS= read -r line || [ -n "$line" ]; do
  line="${line%$'\r'}"
  case "$line" in ''|'#'*) continue ;; esac
  case "$line" in
    /*)        src="$(wslpath -w "$line")" ;;
    [A-Za-z]:*) src="$line" ;;
    *)         src="$(wslpath -w "$LISTDIR/$line")" ;;
  esac
  n=$((n + 1))
  # relative output paths: the extractor opens them from its (Windows) working
  # directory, which is this folder
  ( cd "$TRAIN" && "$INSTR" "$src" "t$n.json" profile.yaml --frames-bin "t$n.bin" > "t$n.log" 2>&1 < /dev/null )
  rc=$?
  [ $rc -eq 0 ] && ok=$((ok + 1))
  echo "train $n rc=$rc $line" >> "$LOG"
  rm -f "$TRAIN/t$n.json" "$TRAIN/t$n.bin"
done < "$LIST"
unset GCOV_PREFIX GCOV_PREFIX_STRIP
echo "training: $ok of $n files ran with exit 0" >> "$LOG"
[ "$ok" -gt 0 ] || { echo "no training file ran - the optimized build would have no profile" >> "$LOG"; echo "EXIT=6" >> "$LOG"; exit 6; }
SRC="$GCOV$BUILD"          # GCOV_PREFIX + the absolute build path the objects carry
[ -d "$SRC" ] || { echo "no counters under $SRC" >> "$LOG"; find "$GCOV" -maxdepth 6 -type d >> "$LOG" 2>&1; echo "EXIT=6" >> "$LOG"; exit 6; }
echo "stale counters removed: $(find "$BUILD" -name '*.gcda' -print -delete | wc -l)" >> "$LOG"
cp -a "$SRC/." "$BUILD/" >> "$LOG" 2>&1 || { echo "EXIT=6" >> "$LOG"; exit 6; }
echo "counters in the build tree: $(find "$BUILD" -name '*.gcda' | wc -l)" >> "$LOG"
# keep the profile with the binary it produced: sorted names and fixed metadata,
# so the same counters always make the same archive
mkdir -p "$ROOT/$DEST"
( cd "$BUILD" && find . -name '*.gcda' | LC_ALL=C sort | tar --no-recursion --mtime='2000-01-01 00:00:00' --owner=0 --group=0 --numeric-owner -cf - -T - | gzip -n > "$ROOT/$DEST/profile-gcda.tar.gz" ) >> "$LOG" 2>&1
PROFILE_MD5="$(md5sum < "$ROOT/$DEST/profile-gcda.tar.gz" | cut -d' ' -f1)"
echo "$PROFILE_MD5" > "$STAMP"   # the optimized build below compiles from this profile
echo "profile kept: $DEST/profile-gcda.tar.gz md5 $PROFILE_MD5" >> "$LOG"
fi

# ---- 3. optimized
build optimized "-fprofile-use -fprofile-correction -Wno-missing-profile -flto=$JOBS -ffat-lto-objects" \
                "-fprofile-use -fprofile-correction -flto=$JOBS -O2 -ffp-contract=off"

mkdir -p "$ROOT/$DEST"
rc=0
cp "$BUILD/src/examples/essentia_streaming_extractor_music.exe" "$ROOT/$DEST/" >> "$LOG" 2>&1 || rc=3
echo "COPIED extractor to $DEST (rc $rc)" >> "$LOG"
md5sum "$ROOT/$DEST/essentia_streaming_extractor_music.exe" >> "$LOG" 2>&1
rm -rf "$TRAIN" "$GCOV"
echo "END $(date)" >> "$LOG"
echo "EXIT=$rc" >> "$LOG"
exit "$rc"   # the caller (schtasks) must see a failed build as failed, not the echo's 0
