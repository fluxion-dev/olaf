#!/usr/bin/env bash
# resolve-reliability-probe.sh — unknown-package reliability probe for issue #2.
# Builds CLI, scans a temp dir containing a definitely-missing npm package,
# asserts: non-strict exit 0 + "Unknown" in stdout; --strict exit 1.
# Issue #123: collapsed to `generate <DIR>` (legacy --input path removed).
# Rules: repo-relative, idempotent, no secrets, exit 0/1/2.
VERSION="0.2.0"
set -euo pipefail

PKG_NAME="this-package-definitely-does-not-exist-olaf-xyz"
PKG_VERSION="9.9.9"
TIMEOUT_SECS=60
REPO_ROOT=""
KEEP_TEMP=0

usage() {
  cat <<EOF
Usage: $(basename "$0") [options]

Reliability probe: unknown npm package resolves to Unknown (non-strict OK, strict FAIL).

Options:
  --repo-root <dir>   Repo root (default: git top-level or script-relative fallback)
  --timeout <secs>    Per-scan timeout in seconds (default: 60)
  --package <name>    Phantom npm package name (default: $PKG_NAME)
  --package-version <v> Phantom package version (default: $PKG_VERSION)
  --keep-temp         Keep temp fixture dir for debugging (default: remove)
  --help              Show this help and exit 0
  --version           Show version and exit 0

Examples:
  $(basename "$0")
  $(basename "$0") --timeout 30 --keep-temp

Exit codes: 0 PASS, 1 FAIL (probe assertion failed), 2 usage/environment error.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --help) usage; exit 0 ;;
    --version) echo "$(basename "$0") $VERSION"; exit 0 ;;
    --repo-root) REPO_ROOT="${2:-}"; shift 2 ;;
    --repo-root=*) REPO_ROOT="${1#*=}"; shift ;;
    --timeout) TIMEOUT_SECS="${2:-}"; shift 2 ;;
    --timeout=*) TIMEOUT_SECS="${1#*=}"; shift ;;
    --package) PKG_NAME="${2:-}"; shift 2 ;;
    --package=*) PKG_NAME="${1#*=}"; shift ;;
    --package-version) PKG_VERSION="${2:-}"; shift 2 ;;
    --package-version=*) PKG_VERSION="${1#*=}"; shift ;;
    --keep-temp) KEEP_TEMP=1; shift ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

if ! [[ "$TIMEOUT_SECS" =~ ^[0-9]+$ ]]; then
  echo "Invalid --timeout '$TIMEOUT_SECS': must be a positive integer." >&2
  exit 2
fi

# Resolve repo root: explicit flag > git top-level > script-relative fallback.
if [[ -z "$REPO_ROOT" ]]; then
  if git rev-parse --show-toplevel >/dev/null 2>&1; then
    REPO_ROOT="$(git rev-parse --show-toplevel)"
  else
    SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
    if [[ -f "$SCRIPT_DIR/../../../olaf.slnx" ]]; then
      REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
    else
      echo "Cannot locate repo root (no git top-level, no olaf.slnx fallback)." >&2
      exit 2
    fi
  fi
fi
if [[ ! -f "$REPO_ROOT/olaf.slnx" ]]; then
  echo "Repo root has no olaf.slnx: $REPO_ROOT" >&2
  exit 2
fi

for cmd in dotnet timeout; do
  if ! command -v "$cmd" >/dev/null 2>&1; then
    echo "Missing required command: $cmd" >&2
    exit 2
  fi
done

echo "== resolve-reliability-probe v$VERSION =="
echo "repo: $REPO_ROOT"
echo "phantom: ${PKG_NAME}@${PKG_VERSION}"

echo "-- step 1: build CLI --"
if ! dotnet build "$REPO_ROOT/src/Olaf.Cli" --nologo -v minimal; then
  echo "FAIL: dotnet build failed." >&2
  exit 1
fi

FIXTURE_DIR="$(mktemp -d -t olaf-reliability-XXXXXX)"
cleanup() {
  if [[ "$KEEP_TEMP" -eq 1 ]]; then
    echo "keeping temp dir: $FIXTURE_DIR"
  else
    rm -rf "$FIXTURE_DIR"
  fi
}
trap cleanup EXIT

# ---- Canonical mkphantom (from _template.sh 0.2.7; vars adapted to PKG_NAME/PKG_VERSION) ----
mkphantom() {
  local d="$1"
  mkdir -p "$d"
  printf '{\n  "name": "olaf-reliability-probe",\n  "version": "1.0.0",\n  "dependencies": {\n    "%s": "%s"\n  }\n}\n' "$PKG_NAME" "$PKG_VERSION" > "$d/package.json"
}

mkphantom "$FIXTURE_DIR"
echo "fixture: $FIXTURE_DIR/package.json"

# NOTE: scan exit codes captured explicitly with `set +e` (timeout/dotnet non-zero expected).
echo "-- step 2: scan WITHOUT --strict (expect exit 0 + 'Unknown') --"
set +e
timeout "$TIMEOUT_SECS" dotnet run --project "$REPO_ROOT/src/Olaf.Cli" --no-build -- generate "$FIXTURE_DIR" >"$FIXTURE_DIR/out-plain.txt" 2>"$FIXTURE_DIR/out-plain.stderr"
PLAIN_CODE=$?
set -e
echo "plain exit: $PLAIN_CODE"
PASS_PLAIN_CODE=0; PASS_PLAIN_UNKNOWN=0
if [[ "$PLAIN_CODE" -eq 0 ]]; then PASS_PLAIN_CODE=1; fi
if grep -q "Unknown" "$FIXTURE_DIR/out-plain.txt" 2>/dev/null; then PASS_PLAIN_UNKNOWN=1; fi
if [[ "$PASS_PLAIN_CODE" -eq 1 ]]; then echo "PASS: non-strict exit 0"; else echo "FAIL: non-strict exit $PLAIN_CODE (want 0)"; fi
if [[ "$PASS_PLAIN_UNKNOWN" -eq 1 ]]; then echo "PASS: non-strict stdout contains Unknown"; else echo "FAIL: non-strict stdout missing Unknown"; fi

echo "-- step 3: scan WITH --strict (expect exit 1) --"
set +e
timeout "$TIMEOUT_SECS" dotnet run --project "$REPO_ROOT/src/Olaf.Cli" --no-build -- generate "$FIXTURE_DIR" --strict >"$FIXTURE_DIR/out-strict.txt" 2>"$FIXTURE_DIR/out-strict.stderr"
STRICT_CODE=$?
set -e
echo "strict exit: $STRICT_CODE"
PASS_STRICT=0
if [[ "$STRICT_CODE" -eq 1 ]]; then PASS_STRICT=1; fi
if [[ "$PASS_STRICT" -eq 1 ]]; then echo "PASS: strict exit 1"; else echo "FAIL: strict exit $STRICT_CODE (want 1)"; fi

echo "== summary =="
if [[ "$PASS_PLAIN_CODE" -eq 1 && "$PASS_PLAIN_UNKNOWN" -eq 1 && "$PASS_STRICT" -eq 1 ]]; then
  echo "PASS: reliability probe (Unknown without strict; exit 1 with strict)"
  exit 0
else
  echo "FAIL: reliability probe (plain_code=$PLAIN_CODE unknown=$PASS_PLAIN_UNKNOWN strict_code=$STRICT_CODE)" >&2
  exit 1
fi
