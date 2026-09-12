#!/bin/sh
# data-gate - refuse commits and pushes that carry generated data or a local user-profile path.
#
# Truedat is a public repository. Scan output (mbxmoods-*.json, mbxmoods-*.csv) records absolute
# paths from the machine it ran on, and a committed review file once published an account name
# that way. .gitignore keeps new output out of `git add .`, but it cannot stop a forced add, a file
# that is already tracked, or an old local commit that only leaves the machine when it is pushed.
# This gate covers those, at commit time and again at push time.
#
# Usage:
#   data-gate.sh staged           check what is staged                          (pre-commit)
#   data-gate.sh commits <revs>   check every commit in <revs>, rev-list style  (pre-push)
#
# Exit 0 = clean, 1 = blocked, 2 = usage. Enable once per clone:
#   git config core.hooksPath .githooks

set -u

fail=0

report() {
  printf 'data-gate: %s\n' "$1" >&2
  fail=1
}

# Generated scan output. The schema is documentation, and the tracked mbxmoods.json fixture has no
# dash, so neither is output.
is_data_file() {
  case "$1" in
    mbxmoods-schema.json|*/mbxmoods-schema.json) return 1 ;;
    mbxmoods-*.json|*/mbxmoods-*.json|mbxmoods-*.csv|*/mbxmoods-*.csv) return 0 ;;
  esac
  return 1
}

# Vendored third-party source keeps its upstream contents.
is_exempt() {
  case "$1" in
    essentia-build/essentia-src/*) return 0 ;;
  esac
  return 1
}

# A drive-letter user-profile path, with one or more backslashes or slashes as separators so the
# escaped form inside JSON is caught too. Placeholder account names are how docs write example
# paths: they are removed from a line FIRST and only the remainder is tested, so a real path that
# shares a line with a placeholder is still caught. Never exclude whole lines by content.
sep='(\\|/)+'
profile="(^|[^A-Za-z])[A-Za-z]:${sep}Users${sep}"
placeholder="${profile}(you|<you>|builder|user|<user>|USERNAME|%USERNAME%|Public|Default)([^A-Za-z0-9_]|\$)"

# $1 = where (for messages), $2 = repo path, remaining args = a git command printing that path's patch.
check_path() {
  where=$1
  path=$2
  shift 2

  is_exempt "$path" && return 0

  if is_data_file "$path"; then
    report "$where: $path is generated scan output and records local paths; keep it out of the repo"
    return 0
  fi

  hits=$("$@" | sed -n 's/^+//p' | grep -v '^++' | sed -E "s#${placeholder}##g" | grep -E "$profile" | head -n 3)
  if [ -n "$hits" ]; then
    report "$where: $path adds a local user-profile path:"
    printf '%s\n' "$hits" | sed 's/^/    /' >&2
  fi
}

newline='
'

case "${1:-}" in
  staged)
    IFS=$newline
    for path in $(git diff --cached --name-only --diff-filter=ACMR); do
      check_path "staged" "$path" git diff --cached -U0 --no-color -- "$path"
    done
    ;;
  commits)
    shift
    IFS=$newline
    for commit in $(git rev-list "$@"); do
      short=$(git rev-parse --short "$commit")
      # Every commit on its own: a data file added and later deleted inside the push still ships.
      for path in $(git diff-tree --root --no-commit-id --name-only -r --diff-filter=ACMR "$commit"); do
        check_path "commit $short" "$path" git show -U0 --no-color --format= "$commit" -- "$path"
      done
    done
    ;;
  patch)
    # Check a patch read from stdin as if it changed <path>. Lets the rules be tested without
    # making commits: data-gate.sh patch some/file.md < change.patch
    if [ -z "${2:-}" ]; then
      echo "usage: data-gate.sh patch <path> < patch" >&2
      exit 2
    fi
    check_path "patch" "$2" cat
    ;;
  *)
    echo "usage: data-gate.sh staged | commits <rev-list arguments> | patch <path>" >&2
    exit 2
    ;;
esac

if [ "$fail" -ne 0 ]; then
  echo "data-gate: blocked. Untrack data with 'git rm --cached <file>', or write the path with a placeholder such as <you>." >&2
  echo "data-gate: if you are certain it is not data, bypass this one time with --no-verify." >&2
  exit 1
fi
exit 0
