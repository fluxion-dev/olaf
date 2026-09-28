#!/usr/bin/env bash
# factory-deleted-surface-gate.sh — deleted-symbol surface gate (#123 collapse).
# argv = symbol list (space-separated; `--flag` form and bare `Symbol` form
# may mix; e.g. `TemplateEngine --template --allow`). Single `dotnet build`,
# then per symbol:
#   (1) zero-refs grep per symbol over src/ + tests/ (*.cs): hits are split
#       into code-hits vs `^[[:space:]]*///` doc-comment lines — zero code-hits
#       with only doc-comment mentions passes as doc-comment-only; any
#       code-hit FAILs with the hit list (cap 10). Noglob-safe: symbols pass
#       via `-e` + `--` (leading `--` never parsed as a grep flag).
#   (2) deleted-flag rejection matrix via the built DLL: each `--flag` symbol
#       runs `dotnet "$DLL" generate <fixture> <flag> bogus` expecting exit 2
#       (unknown-option) + the flag token echoed on stderr. Non-flag symbols
#       skip the matrix.
#   (3) flag-parity table: SYMBOL | KIND | ZERO-REFS | MATRIX | VERDICT; rows
#       the matrix cannot exercise carry
#       `why-inapplicable: deleted, probe-pinned` (deleted surface stays
#       pinned by cli-ux-probe.sh's 16-flag matrix; this gate pins the rest).
# Caller tip: pass CLI flags WITH dashes (`--ecosystem`, not `ecosystem` —
# bare words collide with live identifiers like the Ecosystem enum).
# Exit 0 all PASS, 1 gate failure, 2 usage/environment error.
# Rules: repo-relative, idempotent (temp files cleaned), no secrets.
VERSION="0.1.0"
set -euo pipefail
set -f # noglob: --flag symbols stay literal even unquoted downstream

# ---- Canonical root resolution (copy-paste; do not hardcode paths) ----
# (from _template.sh 0.2.0; factory tools live at .opencode/tools/factory/ so
# repo root is ../../.. from the script dir.)
ROOT=""
if git rev-parse --show-toplevel >/dev/null 2>&1; then
  ROOT="$(git rev-parse --show-toplevel)"
else
  SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  if [[ -f "$SCRIPT_DIR/../../../olaf.slnx" ]]; then
    ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
  else
    echo "Cannot locate repo root (no git top-level, no olaf.slnx fallback)." >&2
    exit 2
  fi
fi

# ---- Canonical pass/fail (copy-paste) ----
fail=0
pass() { echo "PASS: $*"; }
fail_msg() { echo "FAIL: $*"; fail=1; }

# ---- Canonical run_gate (from _template.sh 0.2.11; live) ----
RC=0
run_gate() {
  local log="$1"; shift
  if [[ "${1:-}" == "--" ]]; then shift; fi
  set +e
  "$@" >"$log" 2>&1
  RC=$?
  set -e
}

TIMEOUT_SECS=60
FIXTURE_REL="tests/Olaf.Tests/Fixtures/npm"
PROJECT_REL="src/Olaf.Cli"

usage() {
  cat <<'EOF'
Usage: factory-deleted-surface-gate.sh [options] <symbol> [<symbol> ...]

Deleted-symbol surface gate (#123 collapse): zero-refs grep per symbol plus
deleted-flag rejection matrix via the built DLL.

Symbols may mix `--flag` form (flag-matrix exercised, exit 2) and bare
`Symbol` form (grep gate only; matrix column reads
`why-inapplicable: deleted, probe-pinned`).

Options:
  --fixture <dir>     Fixture for flag-matrix runs (default: tests/Olaf.Tests/Fixtures/npm)
  --timeout <secs>    Per-run timeout in seconds (default: 60)
  --help              Show this help and exit 0
  --version           Show version and exit 0

Exit codes: 0 all PASS, 1 gate failure, 2 usage/environment error.

Examples:
  .opencode/tools/factory/factory-deleted-surface-gate.sh TemplateEngine --template
  .opencode/tools/factory/factory-deleted-surface-gate.sh --allow --deny LicenseGrouper --group-by-license
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --help) usage; exit 0 ;;
    --version) echo "$(basename "$0") $VERSION"; exit 0 ;;
    --fixture) FIXTURE_REL="${2:-}"; shift 2 ;;
    --fixture=*) FIXTURE_REL="${1#*=}"; shift ;;
    --timeout) TIMEOUT_SECS="${2:-}"; shift 2 ;;
    --timeout=*) TIMEOUT_SECS="${1#*=}"; shift ;;
    --*) break ;; # a --symbol (flag-form gate entry), not an option
    *) break ;;
  esac
done

if [[ $# -eq 0 ]]; then
  echo "Need at least one symbol (try --help)" >&2
  exit 2
fi
if ! [[ "$TIMEOUT_SECS" =~ ^[0-9]+$ ]]; then
  echo "Invalid --timeout '$TIMEOUT_SECS': must be a positive integer." >&2
  exit 2
fi

PROJECT="$ROOT/$PROJECT_REL"
FIXTURE="$ROOT/$FIXTURE_REL"
[[ -d "$PROJECT" ]] || { echo "CLI project not found: $PROJECT" >&2; exit 2; }
[[ -e "$FIXTURE" ]] || { echo "Fixture not found: $FIXTURE" >&2; exit 2; }
for cmd in dotnet timeout; do
  command -v "$cmd" >/dev/null 2>&1 || { echo "Missing required command: $cmd" >&2; exit 2; }
done

echo "== factory-deleted-surface-gate v$VERSION =="

echo "-- step 0: build CLI (one-time) --"
BUILD_LOG="$(mktemp "${TMPDIR:-/tmp}/deleted-gate-build-XXXXXX")"
run_gate "$BUILD_LOG" -- dotnet build "$PROJECT" --nologo -v minimal
rm -f "$BUILD_LOG"
if [[ "$RC" -ne 0 ]]; then
  echo "FAIL: dotnet build exited $RC." >&2
  exit 1
fi
pass "build: dotnet build OK"
CLI_DLL="$(find "$PROJECT/bin/Debug" -maxdepth 2 -name 'Olaf.Cli.dll' -print 2>/dev/null | head -1)"
if [[ -z "${CLI_DLL:-}" || ! -f "$CLI_DLL" ]]; then
  echo "FAIL: built DLL not found under $PROJECT/bin/Debug." >&2
  exit 1
fi
pass "build: DLL pinned: ${CLI_DLL#"$ROOT"/}"

WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/deleted-gate-XXXXXX")"
cleanup() { rm -rf "$WORKDIR"; }
trap cleanup EXIT

# Per-symbol verdict accumulators for the parity table.
SYMBOLS=()
REFS_VERDICTS=()
MATRIX_VERDICTS=()
OVERALLS=()

echo "-- step 1+2: zero-refs grep + flag-matrix per symbol --"
for sym in "$@"; do
  SYMBOLS+=("$sym")
  # (1) zero-refs grep (-e + -- keep leading---symbols out of option parsing).
  HITS="$(grep -rn --include='*.cs' -e "$sym" -- "$ROOT/src" "$ROOT/tests" 2>/dev/null || true)"
  if [[ -z "$HITS" ]]; then
    refs="zero-refs"
    echo "PASS [$sym]: zero-refs (no *.cs hits under src/ + tests/)"
  else
    CODE_HITS="$(echo "$HITS" | grep -v '^[[:space:]]*[^:]*:[0-9]*:[[:space:]]*///' || true)"
    if [[ -z "$CODE_HITS" ]]; then
      n="$(echo "$HITS" | grep -c . || true)"
      refs="doc-comment-only($n)"
      echo "PASS [$sym]: doc-comment-only ($n mention(s), zero code-hits)"
    else
      refs="CODE-HITS"
      fail_msg "$sym: $(( $(echo "$CODE_HITS" | grep -c . || true) )) code-hit(s) — deleted symbol still referenced:"
      echo "$CODE_HITS" | head -10 || true
    fi
  fi
  REFS_VERDICTS+=("$refs")
  # (2) deleted-flag rejection matrix (flag-form symbols only).
  if [[ "$sym" == --* ]]; then
    out="$WORKDIR/matrix.stdout"; err="$WORKDIR/matrix.stderr"
    set +e
    timeout "${TIMEOUT_SECS}s" dotnet "$CLI_DLL" generate "$FIXTURE" "$sym" bogus >"$out" 2>"$err"
    mrc=$?
    set -e
    if [[ "$mrc" -eq 2 ]] && grep -qF -- "$sym" "$err" 2>/dev/null; then
      mx="rejected-2"
      echo "PASS [$sym]: matrix exit 2 + token on stderr"
    elif [[ "$mrc" -eq 2 ]]; then
      mx="rejected-2(no-token)"
      fail_msg "$sym: matrix exit 2 but stderr lacks the flag token (tail: $(tail -c 150 "$err" | tr '\n' ' '))"
    else
      mx="exit-$mrc"
      fail_msg "$sym: matrix exit $mrc (want 2; stderr: $(tail -c 150 "$err" | tr '\n' ' '))"
    fi
  else
    mx="why-inapplicable: deleted, probe-pinned"
  fi
  MATRIX_VERDICTS+=("$mx")
  if [[ "$refs" == "CODE-HITS" || "$mx" == exit-* || "$mx" == rejected-2"("* ]]; then
    OVERALLS+=("FAIL")
  else
    OVERALLS+=("PASS")
  fi
done

echo "-- step 3: flag-parity table --"
printf '%-24s %-8s %-22s %-42s %s\n' "SYMBOL" "KIND" "ZERO-REFS" "MATRIX" "VERDICT"
i=0
for sym in "${SYMBOLS[@]}"; do
  if [[ "$sym" == --* ]]; then kind="flag"; else kind="code"; fi
  printf '%-24s %-8s %-22s %-42s %s\n' "$sym" "$kind" "${REFS_VERDICTS[$i]}" "${MATRIX_VERDICTS[$i]}" "${OVERALLS[$i]}"
  i=$((i + 1))
done

echo "== summary =="
if [[ "$fail" -eq 0 ]]; then
  echo "DELETED-SURFACE-GATE OK: $# symbol(s) clean."
  exit 0
else
  echo "DELETED-SURFACE-GATE FAILED: see FAIL lines above." >&2
  exit 1
fi
