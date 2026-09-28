#!/usr/bin/env bash
# factory-test-census.sh — touched-file test census for the factory loop.
# argv = touched-file list (zero or more paths, relative or absolute; empty =
# whole suite under tests/Olaf.Tests). Emits:
#   (a) canonical attribute census (Fact/Theory split, noglob-safe)
#   (b) live `dotnet test --list-tests` total MINUS 1 header line
#   (c) per-file FQN-filter table (single --list-tests run, grep per class)
#   (d) pre/post grep-c + insertions-only verdict
# (a) counts attribute-anchored lines ONLY (`^[[:space:]]*\[(Fact|Theory)`,
# ERE, single-quoted + `set -f` + `-e`/`--` so `[Fact]` never glob-expands;
# `/// [Fact]` doc-comment mentions excluded by the anchor). Theory/InlineData
# expansion means live FQN >= text census; README N is out of scope here (see
# test-count-probe.sh). Exit 0 all PASS, 1 verdict failure, 2 usage/env error.
# Rules: repo-relative, idempotent (read-only; ephemeral logs under
# ${TMPDIR:-/tmp} only — permitted mktemp exception), no secrets, executable.
VERSION="0.1.0"
set -euo pipefail
set -f # noglob: [Fact]/[Theory] patterns stay literal even unquoted downstream

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

usage() {
  cat <<'EOF'
Usage: factory-test-census.sh [touched-file ...]

Touched-file test census: text attribute census vs live --list-tests total.

  (a) attribute census: anchored [Fact]/[Theory] grep (Fact/Theory split,
      noglob-safe: single-quoted ERE + set -f + -e/--, doc-comment excluded)
      over the touched test files, or the whole tests/Olaf.Tests tree
      (bin/obj excluded) when no files are given.
  (b) live total: ONE `dotnet test --list-tests` run; total = non-blank lines
      AFTER the `The following Tests are available:` header (header + build
      lines excluded = MINUS 1 header line); every post-header line must match
      the FQN pattern or it is listed as a STRAY (FAIL).
  (c) per-file table: FILE | CLASS | FQN-COUNT via `.<Class>.` substring over
      the single (b) output (no extra dotnet runs). Non-.cs / non-test files
      get `why-inapplicable: non-test-file`. Bare-substring collisions are
      possible (TemplateHolders exemplar) — counts informational per file,
      authoritative per suite. No argv = table over every test class found.
  (d) verdict: PRE=text census (a) vs POST=live FQN (b). POST >= PRE passes as
      insertions-only (Theory/InlineData expansion explains the surplus);
      POST < PRE FAILs (live cases missing vs text).

Options:
  --help              Show this help and exit 0
  --version           Show version and exit 0

Exit codes: 0 all PASS, 1 verdict failure, 2 usage/environment error.

Examples:
  .opencode/tools/factory/factory-test-census.sh
  .opencode/tools/factory/factory-test-census.sh tests/Olaf.Tests/Cli/GenerateCliTests.cs
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --help) usage; exit 0 ;;
    --version) echo "$(basename "$0") $VERSION"; exit 0 ;;
    --*) echo "Unknown option: $1 (try --help)" >&2; exit 2 ;;
    *) break ;;
  esac
done

CSPROJ="$ROOT/tests/Olaf.Tests/Olaf.Tests.csproj"
TESTS_DIR="$ROOT/tests/Olaf.Tests"
[[ -f "$CSPROJ" ]] || { echo "Missing required file: $CSPROJ" >&2; exit 2; }
[[ -d "$TESTS_DIR" ]] || { echo "Missing required dir: $TESTS_DIR" >&2; exit 2; }
command -v dotnet >/dev/null 2>&1 || { echo "Missing required command: dotnet" >&2; exit 2; }

# Resolve argv to repo-relative test .cs files (non-test args kept for the
# why-inapplicable rows in section (c); scope for (a) = test files or tree).
SCOPE_FILES=()
OTHER_ARGS=()
for a in "$@"; do
  p="$a"
  [[ "$p" = /* ]] || p="$ROOT/$p"
  if [[ -f "$p" && "$p" == "$TESTS_DIR"* && "$p" == *.cs ]]; then
    SCOPE_FILES+=("$p")
  else
    OTHER_ARGS+=("$a")
  fi
done

ATTR_RE='^[[:space:]]*\[(Fact|Theory)'
FACT_RE='^[[:space:]]*\[Fact'
THEORY_RE='^[[:space:]]*\[Theory'

echo "== factory-test-census v$VERSION =="

# ---- (a) canonical attribute census ----
echo "-- (a) attribute census --"
if (( ${#SCOPE_FILES[@]} )); then
  SCOPE_LABEL="${#SCOPE_FILES[@]} touched file(s)"
  FACT_N="$(grep -hc -e "$FACT_RE" -- "${SCOPE_FILES[@]}" 2>/dev/null | awk -F: '{s+=$NF} END {print s+0}')"
  THEORY_N="$(grep -hc -e "$THEORY_RE" -- "${SCOPE_FILES[@]}" 2>/dev/null | awk -F: '{s+=$NF} END {print s+0}')"
else
  SCOPE_LABEL="whole tree $TESTS_DIR (bin/obj excluded)"
  FACT_N="$(grep -rh --exclude-dir=bin --exclude-dir=obj --include='*.cs' -e "$FACT_RE" -- "$TESTS_DIR" 2>/dev/null | grep -c . || true)"
  THEORY_N="$(grep -rh --exclude-dir=bin --exclude-dir=obj --include='*.cs' -e "$THEORY_RE" -- "$TESTS_DIR" 2>/dev/null | grep -c . || true)"
fi
FACT_N="${FACT_N:-0}"
THEORY_N="${THEORY_N:-0}"
CENSUS_N=$((FACT_N + THEORY_N))
echo "FACT=$FACT_N THEORY=$THEORY_N CENSUS=$CENSUS_N SCOPE=($SCOPE_LABEL)"
pass "attribute census FACT=$FACT_N THEORY=$THEORY_N CENSUS=$CENSUS_N"

# ---- (b) live --list-tests total MINUS 1 header line ----
echo "-- (b) live --list-tests total --"
LIST_LOG="$(mktemp "${TMPDIR:-/tmp}/test-census-list-XXXXXX")"
cleanup() { rm -f "$LIST_LOG"; }
trap cleanup EXIT
echo "Running: dotnet test tests/Olaf.Tests/Olaf.Tests.csproj --list-tests ..."
run_gate "$LIST_LOG" -- dotnet test "$CSPROJ" --list-tests
if [[ "$RC" -ne 0 ]]; then
  fail_msg "dotnet test --list-tests exited $RC (build error?)"
  tail -20 "$LIST_LOG" >&2 || true
  echo "TEST-CENSUS FAILED"
  exit 1
fi
HEADER='The following Tests are available:'
if ! grep -qF -- "$HEADER" "$LIST_LOG"; then
  fail_msg "no list-tests header (build error?)"
  tail -20 "$LIST_LOG" >&2 || true
  echo "TEST-CENSUS FAILED"
  exit 1
fi
SECTION="$(mktemp "${TMPDIR:-/tmp}/test-census-section-XXXXXX")"
cleanup2() { rm -f "$LIST_LOG" "$SECTION"; }
trap cleanup2 EXIT
awk -v hdr="$HEADER" 'found{print} index($0,hdr){found=1}' "$LIST_LOG" | grep . >"$SECTION" || true
TOTAL_N="$(grep -c . "$SECTION" || true)"
TOTAL_N="${TOTAL_N:-0}"
FQN_RE='^[[:space:]]*Olaf\.Tests\.'
FQN_N="$(grep -c -e "$FQN_RE" -- "$SECTION" || true)"
FQN_N="${FQN_N:-0}"
STRAYS_N=$((TOTAL_N - FQN_N))
echo "TOTAL=$TOTAL_N FQN=$FQN_N STRAYS=$STRAYS_N (post-header non-blank lines)"
if (( STRAYS_N > 0 )); then
  fail_msg "$STRAYS_N post-header line(s) do not match FQN pattern:"
  grep -v -e "$FQN_RE" -- "$SECTION" | head -10 || true
else
  pass "live total $TOTAL_N (header excluded, 0 strays)"
fi

# ---- (c) per-file FQN-filter table ----
echo "-- (c) per-file FQN-filter table --"
printf '%-60s %-32s %s\n' "FILE" "CLASS" "FQN-COUNT"
if (( ${#SCOPE_FILES[@]} )) || (( ${#OTHER_ARGS[@]} )); then
  for f in "${SCOPE_FILES[@]:-}"; do
    [[ -n "${f:-}" ]] || continue
    rel="${f#"$ROOT"/}"
    cls="$(basename "$f" .cs)"
    n="$(grep -c -e "\.${cls}\." -- "$SECTION" || true)"
    printf '%-60s %-32s %s\n' "$rel" "$cls" "${n:-0}"
  done
  for o in "${OTHER_ARGS[@]:-}"; do
    [[ -n "${o:-}" ]] || continue
    printf '%-60s %-32s %s\n' "$o" "-" "why-inapplicable: non-test-file"
  done
else
  while IFS= read -r cls; do
    [[ -n "${cls:-}" ]] || continue
    n="$(grep -c -e "\.${cls}\." -- "$SECTION" || true)"
    printf '%-60s %-32s %s\n' "(suite)" "$cls" "${n:-0}"
  done < <(grep -o -e "$FQN_RE[A-Za-z0-9_.]*" -- "$SECTION" | sed -e 's/^ *Olaf\.Tests\.//' -e 's/(.*//' | awk -F. 'NF>=3{print $1"."$2} NF==2{print $1}' | sort -u)
fi

# ---- (d) pre/post grep-c + insertions-only verdict ----
echo "-- (d) pre/post verdict --"
echo "PRE(census)=$CENSUS_N POST(live-FQN)=$FQN_N"
if (( FQN_N >= CENSUS_N )); then
  pass "insertions-only: live FQN $FQN_N >= text census $CENSUS_N (+$((FQN_N - CENSUS_N)) Theory/InlineData expansion)"
else
  fail_msg "live FQN $FQN_N < text census $CENSUS_N (missing $((CENSUS_N - FQN_N)) cases?)"
fi

echo "== summary =="
if [[ "$fail" -eq 0 ]]; then
  echo "TEST-CENSUS OK: census=$CENSUS_N (fact=$FACT_N/theory=$THEORY_N) live=$FQN_N."
  exit 0
else
  echo "TEST-CENSUS FAILED: see FAIL lines above." >&2
  exit 1
fi
