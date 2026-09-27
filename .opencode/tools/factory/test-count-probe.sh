#!/usr/bin/env bash
# test-count-probe: README Tests:N docs gate (docs mandatory request — README Tests:N stale 2+ issues).
# Compares the documented `Tests: N passing` count in README.md against a live
# `dotnet test tests/Olaf.Tests/Olaf.Tests.csproj --verbosity minimal` run.
# FAIL (exit 1) on mismatch — the message shows actual vs documented.
# No --update flag by design: wording belongs to the docs agent; this probe
# never edits docs. Exit 2 on usage/IO/build error.
VERSION="0.1.0"
set -euo pipefail

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

if [[ "${1:-}" == "--help" ]]; then
  echo "Usage: $(basename "$0") [--help] [--version]"
  echo "README Tests:N docs gate: greps the documented \`Tests: N passing\`"
  echo "count from README.md and compares it against a live"
  echo "\`dotnet test tests/Olaf.Tests/Olaf.Tests.csproj --verbosity minimal\`"
  echo "run (full run, ~15s, acceptable for a docs gate)."
  echo "Exit 0 = documented count matches live; 1 = mismatch (actual vs"
  echo "documented in message; probe never edits docs); 2 = usage/IO/build error."
  echo "Example: ./.opencode/tools/factory/test-count-probe.sh"
  exit 0
fi
if [[ "${1:-}" == "--version" ]]; then
  echo "$(basename "$0") $VERSION"
  exit 0
fi
if [[ -n "${1:-}" ]]; then
  echo "Unknown option: $1 (try --help)" >&2
  exit 2
fi

README="$ROOT/README.md"
CSPROJ="$ROOT/tests/Olaf.Tests/Olaf.Tests.csproj"
[[ -f "$README" ]] || { echo "Missing required file: $README" >&2; exit 2; }
[[ -f "$CSPROJ" ]] || { echo "Missing required file: $CSPROJ" >&2; exit 2; }

DOC_N="$(grep -oE 'Tests: [0-9]+' "$README" | grep -oE '[0-9]+' | head -1 || true)"
if [[ -z "${DOC_N:-}" ]]; then
  fail_msg "README has no 'Tests: N passing' line"
  echo "TEST-COUNT-PROBE FAIL"
  exit 1
fi
echo "README documents: Tests: $DOC_N passing"

OUT="$(mktemp "${TMPDIR:-/tmp}/test-count-XXXXXX")"
cleanup() { rm -f "$OUT"; }
trap cleanup EXIT

echo "Running: dotnet test tests/Olaf.Tests/Olaf.Tests.csproj --verbosity minimal ..."
if ! dotnet test "$CSPROJ" --verbosity minimal >"$OUT" 2>&1; then
  : # nonzero exit is expected when tests fail; parse the summary below anyway
fi

LAST="$(grep -E 'Passed!|Failed!' "$OUT" | tail -1 || true)"
if [[ -z "$LAST" ]]; then
  echo "FAIL: dotnet test produced no Passed!/Failed! summary (build error?)" >&2
  tail -20 "$OUT" >&2
  exit 2
fi
echo "Live summary: $(echo "$LAST" | tr -s ' ')"

if grep -q 'Passed!' <<<"$LAST"; then
  ACT_N="$(grep -oE 'Passed:[ ]*[0-9]+' <<<"$LAST" | grep -oE '[0-9]+' | head -1)"
  if [[ "$ACT_N" == "$DOC_N" ]]; then
    pass "README Tests:$DOC_N matches live passed count $ACT_N"
    echo "TEST-COUNT-PROBE OK"
    exit 0
  else
    fail_msg "README says Tests: $DOC_N passing but live dotnet test reports $ACT_N passed (docs fix belongs to docs agent — probe does not edit)"
    echo "TEST-COUNT-PROBE FAIL"
    exit 1
  fi
else
  fail_msg "live dotnet test did not pass: $(echo "$LAST" | tr -s ' ')"
  echo "TEST-COUNT-PROBE FAIL"
  exit 1
fi
