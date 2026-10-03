#!/usr/bin/env bash
# Local smoke test without docker: same verification logic as the containers,
# but clones into $SANDBOX_WORK (default /tmp/opencode/olaf-sandbox) and runs
# olaf from source via `dotnet run` (or $OLAF_BIN when set).
#
#   sandbox/smoke-local.sh [--eco <name>] [--formats json|all] [--offline|--online]
#
# Exit 0 iff every selected ecosystem PASSes.
set -u

ECOS=""
FORMATS="json"
OFFLINE_FLAG="--online"
OLAF_BIN="${OLAF_BIN:-}"

while [ $# -gt 0 ]; do
  case "$1" in
    --eco) ECOS="$2"; shift 2 ;;
    --formats) FORMATS="$2"; shift 2 ;;
    --offline) OFFLINE_FLAG="--offline"; shift ;;
    --online) OFFLINE_FLAG="--online"; shift ;;
    -h|--help)
      echo "usage: smoke-local.sh [--eco <name>] [--formats json|all] [--offline|--online]"
      exit 0 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SANDBOX_WORK="${SANDBOX_WORK:-/tmp/opencode/olaf-sandbox}"
ALL_ECOS="npm nuget pip go cargo maven gradle composer bundler swift cocoapods vcpkg conan"
ECOS="${ECOS:-$ALL_ECOS}"

# Resolve olaf runner: explicit $OLAF_BIN wins, else build once and reuse the DLL host binary.
if [ -z "$OLAF_BIN" ]; then
  echo "== building olaf (Release) once"
  dotnet build "$ROOT/src/Olaf.Cli" -c Release --verbosity minimal || exit 1
  OLAF_BIN="$(ls -d "$ROOT"/src/Olaf.Cli/bin/Release/net*/Olaf.Cli 2>/dev/null | head -n 1)"
  [ -n "$OLAF_BIN" ] && [ -x "$OLAF_BIN" ] || { echo "FAIL: built binary not found" >&2; exit 1; }
  export OLAF_BIN
  echo "== OLAF_BIN=$OLAF_BIN"
fi

PASS=0
FAIL=0
FAILED_LIST=""

for eco in $ECOS; do
  repo="$(python3 -c "import json;print(json.load(open('$ROOT/sandbox/repos.json'))['ecosystems']['$eco']['repo'])")"
  allow_unresolved="$(python3 -c "import json;print(json.load(open('$ROOT/sandbox/repos.json'))['ecosystems']['$eco'].get('allowUnresolved', False))")"
  extra=""
  [ "$allow_unresolved" = "True" ] && extra="--allow-unresolved"
  workdir="$SANDBOX_WORK/$eco"
  echo "===== [$eco] $repo -> $workdir ====="
  # shellcheck disable=SC2086
  if OLAF_BIN="$OLAF_BIN" bash "$ROOT/sandbox/common/verify.sh" \
      --eco "$eco" --repo "$repo" --min-packages 1 \
      --workdir "$workdir" "$OFFLINE_FLAG" --formats "$FORMATS" --olaf-bin "$OLAF_BIN" $extra; then
    PASS=$((PASS+1))
  else
    FAIL=$((FAIL+1)); FAILED_LIST="$FAILED_LIST $eco"
  fi
done

echo "----------------------------------------"
echo "local smoke results: PASS=$PASS FAIL=$FAIL$([ -n "$FAILED_LIST" ] && echo " (failed:$FAILED_LIST)")"
[ "$FAIL" -eq 0 ]
