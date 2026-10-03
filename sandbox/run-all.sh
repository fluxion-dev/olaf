#!/usr/bin/env bash
# Build + run every olaf sandbox (or one with --eco <name>).
# Each container clones its smoke repo, runs `olaf generate`, and validates
# the licence report via /opt/olaf-verify/verify.sh. Host-side git/docker required.
#
#   sandbox/run-all.sh [--eco <name>] [--no-build] [--formats json|all]
#
# Exit 0 iff every selected ecosystem PASSes.
set -u

ECOS=""
NO_BUILD=0
FORMATS_OVERRIDE=""

while [ $# -gt 0 ]; do
  case "$1" in
    --eco) ECOS="$2"; shift 2 ;;
    --no-build) NO_BUILD=1; shift ;;
    --formats) FORMATS_OVERRIDE="$2"; shift 2 ;;
    -h|--help)
      echo "usage: run-all.sh [--eco <name>] [--no-build] [--formats json|all]"
      exit 0 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ALL_ECOS="npm nuget pip go cargo maven gradle composer bundler swift cocoapods vcpkg conan"
ECOS="${ECOS:-$ALL_ECOS}"

PASS=0
FAIL=0
FAILED_LIST=""

for eco in $ECOS; do
  img="olaf-sandbox-$eco"
  df="$ROOT/sandbox/docker/$eco.Dockerfile"
  [ -f "$df" ] || { echo "FAIL: [$eco] missing $df"; FAIL=$((FAIL+1)); FAILED_LIST="$FAILED_LIST $eco"; continue; }

  if [ "$NO_BUILD" -eq 0 ]; then
    echo "===== [$eco] docker build ====="
    if ! docker build -f "$df" -t "$img" "$ROOT"; then
      echo "FAIL: [$eco] build failed"
      FAIL=$((FAIL+1)); FAILED_LIST="$FAILED_LIST $eco"
      continue
    fi
  fi

  echo "===== [$eco] docker run (smoke) ====="
  if docker run --rm "$img"; then
    echo "PASS: [$eco]"
    PASS=$((PASS+1))
  else
    echo "FAIL: [$eco] container verification failed"
    FAIL=$((FAIL+1)); FAILED_LIST="$FAILED_LIST $eco"
  fi
done

echo "----------------------------------------"
echo "sandbox results: PASS=$PASS FAIL=$FAIL$([ -n "$FAILED_LIST" ] && echo " (failed:$FAILED_LIST)")"
[ "$FAIL" -eq 0 ]
