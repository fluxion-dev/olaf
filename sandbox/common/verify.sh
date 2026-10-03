#!/usr/bin/env bash
# Verification framework for one olaf sandbox ecosystem.
#
#   verify.sh --eco <name> --repo <git-url> [--min-packages N]
#             [--workdir /work] [--online|--offline] [--formats json|all]
#             [--olaf-bin /usr/local/bin/olaf] [--skip-clone]
#
# Steps:
#   1. git clone --depth 1 <repo> <workdir>/repo   (skipped with --skip-clone)
#   2. olaf generate <repo> --format json [--offline] --out report.json
#   3. validate.py report.json --eco <eco> --min-packages N
#   4. (with --formats all) re-run olaf for all 7 formats, assert exit 0 + non-empty
#
# Network (default): online resolution is the default because a *proper*
# licence file means actually resolved licences (status != Unknown). Pass
# --offline for hermetic/air-gap runs; the >=1-resolved gate is then relaxed
# (misses stay Unknown by design) but shape/total/eco gates still apply.
#
# Exit 0 = PASS, non-zero = FAIL. All progress goes to stdout with PASS:/FAIL: prefixes.
set -u

ECO=""
REPO=""
MIN_PACKAGES=1
WORKDIR="/work"
OFFLINE=""
FORMATS="json"
OLAF_BIN="${OLAF_BIN:-olaf}"
SKIP_CLONE=0
ALLOW_UNRESOLVED=0

usage() {
  echo "usage: verify.sh --eco <name> --repo <git-url> [--min-packages N] [--workdir DIR] [--offline|--online] [--formats json|all] [--olaf-bin PATH] [--skip-clone] [--allow-unresolved]" >&2
}

while [ $# -gt 0 ]; do
  case "$1" in
    --eco) ECO="$2"; shift 2 ;;
    --repo) REPO="$2"; shift 2 ;;
    --min-packages) MIN_PACKAGES="$2"; shift 2 ;;
    --workdir) WORKDIR="$2"; shift 2 ;;
    --offline) OFFLINE="--offline"; shift ;;
    --online) OFFLINE=""; shift ;;
    --formats) FORMATS="$2"; shift 2 ;;
    --olaf-bin) OLAF_BIN="$2"; shift 2 ;;
    --skip-clone) SKIP_CLONE=1; shift ;;
    --allow-unresolved) ALLOW_UNRESOLVED=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "FAIL: unknown arg: $1" >&2; usage; exit 2 ;;
  esac
done

if [ -z "$ECO" ] || [ -z "$REPO" ]; then
  echo "FAIL: --eco and --repo are required" >&2
  usage
  exit 2
fi

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
VALIDATE="$SCRIPT_DIR/validate.py"

command -v git >/dev/null 2>&1 || { echo "FAIL: git not found on PATH"; exit 1; }
command -v python3 >/dev/null 2>&1 || { echo "FAIL: python3 not found on PATH"; exit 1; }
if ! command -v "$OLAF_BIN" >/dev/null 2>&1 && [ ! -x "$OLAF_BIN" ]; then
  echo "FAIL: olaf binary not found: $OLAF_BIN"
  exit 1
fi

CLONE_DIR="$WORKDIR/repo"
REPORT_JSON="$WORKDIR/report.json"

if [ "$SKIP_CLONE" -eq 0 ]; then
  echo "== [$ECO] cloning $REPO"
  rm -rf "$CLONE_DIR"
  mkdir -p "$WORKDIR"
  if ! git clone --depth 1 "$REPO" "$CLONE_DIR" 2>&1 | tail -n 3; then
    echo "FAIL: [$ECO] git clone failed"
    exit 1
  fi
else
  echo "== [$ECO] skipping clone, using $CLONE_DIR"
  [ -d "$CLONE_DIR" ] || { echo "FAIL: [$ECO] clone dir missing: $CLONE_DIR"; exit 1; }
fi

echo "== [$ECO] olaf version"
"$OLAF_BIN" --version || { echo "FAIL: [$ECO] olaf --version failed"; exit 1; }

MODE="online"
[ -n "$OFFLINE" ] && MODE="offline"
echo "== [$ECO] olaf generate (json, $MODE)"
# shellcheck disable=SC2086
if ! "$OLAF_BIN" generate "$CLONE_DIR" --format json $OFFLINE --out "$REPORT_JSON" --force; then
  echo "FAIL: [$ECO] olaf generate json failed"
  exit 1
fi
[ -s "$REPORT_JSON" ] || { echo "FAIL: [$ECO] report.json empty/missing"; exit 1; }

echo "== [$ECO] validating report.json"
VALIDATE_EXTRA=""
if [ -n "$OFFLINE" ] || [ "$ALLOW_UNRESOLVED" -eq 1 ]; then
  VALIDATE_EXTRA="--allow-unresolved"
fi
# shellcheck disable=SC2086
if ! python3 "$VALIDATE" "$REPORT_JSON" --eco "$ECO" --min-packages "$MIN_PACKAGES" $VALIDATE_EXTRA; then
  echo "FAIL: [$ECO] report validation failed"
  exit 1
fi

if [ "$FORMATS" = "all" ]; then
  for fmt in yaml xml md cyclonedx-json cyclonedx-xml spdx-json; do
    out="$WORKDIR/report.$fmt"
    echo "== [$ECO] olaf generate ($fmt)"
    # shellcheck disable=SC2086
    if ! "$OLAF_BIN" generate "$CLONE_DIR" --format "$fmt" $OFFLINE --out "$out" --force; then
      echo "FAIL: [$ECO] olaf generate $fmt failed"
      exit 1
    fi
    [ -s "$out" ] || { echo "FAIL: [$ECO] report.$fmt empty/missing"; exit 1; }
    echo "PASS: [$ECO] format $fmt ok ($(wc -c <"$out") bytes)"
  done
fi

echo "PASS: [$ECO] sandbox verification succeeded"
