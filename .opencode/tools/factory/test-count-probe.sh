#!/usr/bin/env bash
# test-count-probe: README Tests:N docs gate (docs mandatory request — README Tests:N stale 2+ issues).
# Compares the documented `Tests: N passing` count in README.md against a live
# `dotnet test tests/Olaf.Tests/Olaf.Tests.csproj --verbosity minimal` run.
# Dry-run default: FAIL (exit 1) on mismatch — the message shows actual vs
# documented and never edits docs. `--update`: on mismatch, rewrite ONLY the
# single `Tests: N passing` line to the live total and exit 0 (mandatory per
# 8-issue Tests:N repeat — the manual docs-agent round-trip was the cost
# center). Exit 2 on usage/IO/build error.
# --fqn <Area> reporter (QA proposal): emits the (FQN-count, text-census,
# README N) triple in one call. FQN-count via `dotnet test --list-tests
# --filter FullyQualifiedName~<Area>` is authoritative; text census
# (`[Fact]`/`[Theory]` grep with bin/obj excluded) is informational only —
# Theory expansion means FQN >= census; README N is informational only.
VERSION="0.3.0"
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

FQN_AREA=""
UPDATE=0

if [[ "${1:-}" == "--help" ]]; then
  echo "Usage: $(basename "$0") [--help] [--version] [--fqn <Area>] [--update]"
  echo "README Tests:N docs gate: greps the documented \`Tests: N passing\`"
  echo "count from README.md and compares it against a live"
  echo "\`dotnet test tests/Olaf.Tests/Olaf.Tests.csproj --verbosity minimal\`"
  echo "run (full run, ~15s, acceptable for a docs gate)."
  echo "Exit 0 = documented count matches live (or --update rewrote it);"
  echo "1 = mismatch dry-run (actual vs documented in message; no edit);"
  echo "2 = usage/IO/build error."
  echo "Example: ./.opencode/tools/factory/test-count-probe.sh"
  echo ""
  echo "--update (additive; default gate unchanged): on mismatch, rewrite ONLY"
  echo "the single \`Tests: N passing\` line in README.md to the live total"
  echo "(first match only, via sed 0,/re/ address) and exit 0. Dry-run (no"
  echo "flag) still FAILs without editing. Cannot be combined with --fqn."
  echo "Example: ./.opencode/tools/factory/test-count-probe.sh --update"
  echo ""
  echo "--fqn <Area> reporter (additive; default gate unchanged): emits the"
  echo "(FQN-count, text-census, README N) triple in one call, e.g.:"
  echo "  FQN=227 CENSUS=219 README=674 AREA=Parsers"
  echo "FQN-count (authoritative) = \`dotnet test --list-tests"
  echo "--filter FullyQualifiedName~<Area>\` lines; text-census (informational)"
  echo "= \`grep -r [Fact]/[Theory]\` under tests/Olaf.Tests/<Area>/ (or the"
  echo "whole tree when <Area> is not a test dir); README N (informational) ="
  echo "documented \`Tests: N passing\`. Theory/InlineData expansion explains"
  echo "FQN >= census. <Area> = all lists the whole suite unfiltered."
  echo "Reporter always exits 0 on a successful triple (NOTE when zero FQNs"
  echo "match); 2 on usage/list failure."
  echo "Example: ./.opencode/tools/factory/test-count-probe.sh --fqn Parsers"
  exit 0
fi
if [[ "${1:-}" == "--version" ]]; then
  echo "$(basename "$0") $VERSION"
  exit 0
fi
while [[ $# -gt 0 ]]; do
  case "$1" in
    --fqn)
      [[ -n "${2:-}" ]] || { echo "Missing value for --fqn <Area> (try --help)" >&2; exit 2; }
      FQN_AREA="$2"
      shift 2 ;;
    --fqn=*)
      FQN_AREA="${1#*=}"
      [[ -n "$FQN_AREA" ]] || { echo "Missing value for --fqn=<Area> (try --help)" >&2; exit 2; }
      shift ;;
    --update) UPDATE=1; shift ;;
    *) echo "Unknown option: $1 (try --help)" >&2; exit 2 ;;
  esac
done
if (( UPDATE )) && [[ -n "$FQN_AREA" ]]; then
  echo "--update cannot be combined with --fqn (reporter never edits)" >&2
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

# ---- --fqn <Area> reporter (additive; default gate below unchanged) ----
if [[ -n "$FQN_AREA" ]]; then
  echo "== test-count-probe v$VERSION (--fqn $FQN_AREA) =="
  LIST_OUT="$(mktemp "${TMPDIR:-/tmp}/test-count-fqn-XXXXXX")"
  CENSUS_OUT="$(mktemp "${TMPDIR:-/tmp}/test-count-census-XXXXXX")"
  cleanup_fqn() { rm -f "$LIST_OUT" "$CENSUS_OUT"; }
  trap cleanup_fqn EXIT
  # 1. FQN-count (authoritative): --list-tests never executes tests.
  if [[ "$FQN_AREA" == "all" ]]; then
    FILTER_ARGS=()
  else
    FILTER_ARGS=(--filter "FullyQualifiedName~$FQN_AREA")
  fi
  echo "Running: dotnet test ... --list-tests ${FILTER_ARGS[*]:-} ..."
  if ! dotnet test "$CSPROJ" --list-tests "${FILTER_ARGS[@]}" >"$LIST_OUT" 2>&1; then
    echo "FAIL: dotnet test --list-tests failed (build error?)" >&2
    tail -20 "$LIST_OUT" >&2
    exit 2
  fi
  FQN_N="$(grep -cE '^[[:space:]]+Olaf\.Tests\.' "$LIST_OUT" || true)"
  FQN_N="${FQN_N:-0}"
  # 2. text-census (informational): Theory/InlineData expansion explains FQN >= census.
  CENSUS_DIR="$ROOT/tests/Olaf.Tests"
  if [[ "$FQN_AREA" != "all" && -d "$CENSUS_DIR/$FQN_AREA" ]]; then
    CENSUS_DIR="$CENSUS_DIR/$FQN_AREA"
  fi
  grep -rh --exclude-dir=bin --exclude-dir=obj --include='*.cs' -e '\[Fact\]' -e '\[Theory\]' "$CENSUS_DIR" >"$CENSUS_OUT" || true
  CENSUS_N="$(grep -c . "$CENSUS_OUT" || true)"
  CENSUS_N="${CENSUS_N:-0}"
  # 3. README N (informational): already parsed as DOC_N above.
  echo "FQN=$FQN_N CENSUS=$CENSUS_N README=$DOC_N AREA=$FQN_AREA"
  if [[ "$FQN_N" == "0" ]]; then
    echo "NOTE: no FQNs matched area '$FQN_AREA' (check spelling; try 'all')"
  else
    pass "FQN-count $FQN_N (area $FQN_AREA, authoritative)"
  fi
  pass "text-census $CENSUS_N under $CENSUS_DIR (informational)"
  pass "README N $DOC_N (informational)"
  echo "TEST-COUNT-PROBE FQN OK"
  exit 0
fi

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
    if (( UPDATE )); then
      sed -i "0,/Tests: [0-9][0-9]* passing/s//Tests: $ACT_N passing/" "$README"
      NEW_N="$(grep -oE 'Tests: [0-9]+' "$README" | grep -oE '[0-9]+' | head -1 || true)"
      if [[ "$NEW_N" == "$ACT_N" ]]; then
        pass "README Tests:N updated $DOC_N -> $ACT_N (--update, single-line rewrite)"
        echo "TEST-COUNT-PROBE OK (--update)"
        exit 0
      else
        fail_msg "--update rewrite failed (README still $NEW_N, want $ACT_N)"
        echo "TEST-COUNT-PROBE FAIL"
        exit 1
      fi
    fi
    fail_msg "README says Tests: $DOC_N passing but live dotnet test reports $ACT_N passed (re-run with --update to rewrite, or leave the docs fix to the docs agent — dry-run never edits)"
    echo "TEST-COUNT-PROBE FAIL"
    exit 1
  fi
else
  fail_msg "live dotnet test did not pass: $(echo "$LAST" | tr -s ' ')"
  echo "TEST-COUNT-PROBE FAIL"
  exit 1
fi
