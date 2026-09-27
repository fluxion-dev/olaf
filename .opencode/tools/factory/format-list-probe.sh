#!/usr/bin/env bash
# format-list-probe.sh — Supported-format list drift probe (format-list class).
# Verifies the canonical 9-token format list stays consistent across:
#   1. src/Olaf.Cli/Program.cs SupportedFormats const (canonical source)
#   2. FormatterRegistry error string (must equal const) + per-token switch
#      arms (plus the `markdown` alias arm, which is NOT in the const)
#   3. live `dotnet run -- --help` runtime text (must print const verbatim)
#   4. README anchored lines (Supported-formats, Flags --format, --help
#      excerpt): 8 primary tokens in order (FAIL on missing/misorder); an
#      absent `cyclonedx` alias token is WARN when an alias parenthetical on
#      the same line documents it, else FAIL (all three carry the full
#      9-token list on the accurate tree; the WARN branch covers future
#      alias-parenthetical-only wording)
#   5. alias notes README-wide (markdown->md, cyclonedx->cyclonedx-json,
#      cyclonedx-xml has no alias)
#   6. format-matrix-dump.sh FORMATS allowlist (8 primaries in order; the
#      `cyclonedx` alias omission is WARN — the alias duplicates the
#      cyclonedx-json branch) + --format case acceptance per token
# Rules: repo-relative, read-only (never edits src/docs), idempotent
# (stdout only), no secrets. No temp files: live --help is captured in a
# shell variable, so no --workdir/--keep-temp flags.
# Template-based (_template 0.2.2 blocks: root resolution + pass/fail_msg).
VERSION="0.1.0"
set -euo pipefail

# ---- Canonical root resolution (copy-paste; do not hardcode paths) ----
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
warn=0
warn_msg() { echo "WARN: $*"; warn=1; }

TIMEOUT_SECS=180

usage() {
  echo "Usage: $(basename "$0") [--timeout <secs>] [--help] [--version]"
  echo ""
  echo "Supported-format list drift probe (read-only; never edits src/docs)."
  echo "Verifies the canonical 9-token format list agrees across:"
  echo "  1. src/Olaf.Cli/Program.cs SupportedFormats const (canonical source)"
  echo "  2. FormatterRegistry error string + per-token switch arms (+ markdown alias arm)"
  echo "  3. live '--help' runtime text (must print the const verbatim)"
  echo "  4. README anchored lines (Supported-formats, Flags --format, --help excerpt):"
  echo "     8 primary tokens in order (FAIL); missing 'cyclonedx' alias token is"
  echo "     WARN when an alias parenthetical documents it, else FAIL"
  echo "  5. alias notes (markdown->md, cyclonedx->cyclonedx-json, cyclonedx-xml no alias)"
  echo "  6. format-matrix-dump.sh FORMATS allowlist (8 primaries in order; alias WARN)"
  echo ""
  echo "Options:"
  echo "  --timeout <n>  per-scan timeout in seconds for live --help (default: 180)"
  echo "  --help         show this help and exit 0"
  echo "  --version      print VERSION and exit 0"
  echo ""
  echo "Exit codes: 0 probe passed (WARNs allowed), 1 check failure, 2 usage/environment error."
  echo "Examples:"
  echo "  $(basename "$0")"
  echo "  $(basename "$0") --timeout 60"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --timeout)
      [[ $# -lt 2 ]] && { echo "Missing value for --timeout." >&2; exit 2; }
      [[ "$2" =~ ^[0-9]+$ ]] || { echo "Invalid --timeout '$2': expected integer seconds." >&2; exit 2; }
      TIMEOUT_SECS="$2"
      shift 2
      ;;
    --help)
      usage
      exit 0
      ;;
    --version)
      echo "$(basename "$0") $VERSION"
      exit 0
      ;;
    *)
      echo "Unknown argument '$1'. See --help." >&2
      exit 2
      ;;
  esac
done

PROGRAM="$ROOT/src/Olaf.Cli/Program.cs"
REGISTRY="$ROOT/src/Olaf.Formatters/FormatterRegistry.cs"
README="$ROOT/README.md"
MATRIX="$ROOT/.opencode/tools/factory/format-matrix-dump.sh"
[[ -f "$PROGRAM" ]] || { echo "Not found: $PROGRAM" >&2; exit 2; }
[[ -f "$REGISTRY" ]] || { echo "Not found: $REGISTRY" >&2; exit 2; }
[[ -f "$README" ]] || { echo "Not found: $README" >&2; exit 2; }
[[ -f "$MATRIX" ]] || { echo "Not found: $MATRIX" >&2; exit 2; }
command -v dotnet >/dev/null 2>&1 || { echo "dotnet not on PATH." >&2; exit 2; }

# tokens_in_order <haystack> <needle> (space-separated): exit 0 iff needle
# is an ordered subsequence of haystack.
tokens_in_order() {
  python3 - "$1" "$2" <<'PY'
import sys
hay, needle = sys.argv[1].split(), sys.argv[2].split()
it = iter(hay)
sys.exit(0 if all(any(t == n for t in it) for n in needle) else 1)
PY
}

echo "--- Check 1 (FAIL): Program.cs const vs Registry error string vs live --help ---"
CANON="$(grep -oE 'SupportedFormats = "[^"]+"' "$PROGRAM" | head -n 1 | sed 's/.*= *"//; s/"$//' || true)"
[[ -n "${CANON:-}" ]] || fail_msg "SupportedFormats const missing in src/Olaf.Cli/Program.cs"
REGERR="$(grep -oE 'Supported: [a-z][a-z|-]*\.' "$REGISTRY" | head -n 1 | sed 's/^Supported: //; s/\.$//' || true)"
[[ -n "${REGERR:-}" ]] || fail_msg "Supported: error string missing in FormatterRegistry.cs"
if [[ -n "${CANON:-}" && -n "${REGERR:-}" ]]; then
  if [[ "$REGERR" == "$CANON" ]]; then
    pass "const == registry error string [$CANON]"
  else
    fail_msg "const [$CANON] != registry error string [$REGERR]"
  fi
fi
if [[ -n "${CANON:-}" ]]; then
  missing_arms=""
  for tok in ${CANON//|/ }; do
    if ! grep -qE "\"$tok\" =>" "$REGISTRY"; then
      missing_arms="$missing_arms $tok"
    fi
  done
  if [[ -z "${missing_arms// }" ]]; then
    pass "registry switch arm per canonical token"
  else
    fail_msg "registry missing switch arm(s):$missing_arms"
  fi
  if grep -qE '"markdown" =>' "$REGISTRY"; then
    pass "registry markdown alias arm present"
  else
    fail_msg "registry markdown alias arm missing"
  fi
fi
HELP_TEXT="$(timeout "${TIMEOUT_SECS}s" dotnet run --project "$ROOT/src/Olaf.Cli" -- --help 2>&1)" || { echo "live --help failed (exit $?) — environment error." >&2; exit 2; }
if [[ -n "${CANON:-}" ]]; then
  if grep -qF "$CANON" <<<"$HELP_TEXT"; then
    pass "live --help prints canonical token set in order"
  else
    SEEN="$(grep -oE 'Output format: [a-z][a-z|-]*' <<<"$HELP_TEXT" | head -n 1 | sed 's/^Output format: //' || true)"
    fail_msg "live --help missing canonical list [$CANON]; seen [${SEEN:-<none>}]"
  fi
fi

echo "--- Check 2 (FAIL/WARN): README anchored lines carry tokens in order ---"
PRIMARY="json|yaml|xml|html|txt|md|cyclonedx-json|cyclonedx-xml"
PRIMARY_SP="${PRIMARY//|/ }"
for anchor in "Supported formats:" "Flags:.*--format" "Output format:"; do
  line="$(grep -E "$anchor" "$README" | head -n 1 || true)"
  if [[ -z "${line:-}" ]]; then
    fail_msg "README anchor missing: $anchor"
    continue
  fi
  run="$(grep -oE '[a-z][a-z-]*(\|[a-z][a-z-]+)+' <<<"$line" | grep -F "json" | head -n 1 || true)"
  if [[ -z "${run:-}" ]]; then
    fail_msg "README [$anchor] has no json pipe-list"
    continue
  fi
  run_sp="${run//|/ }"
  if tokens_in_order "$run_sp" "$PRIMARY_SP"; then
    pass "README [$anchor] 8 primaries in order [$run]"
    if grep -qE '(^|\|)cyclonedx(\||$)' <<<"$run"; then
      if [[ "$run" == *"cyclonedx-json|cyclonedx|cyclonedx-xml"* ]]; then
        pass "README [$anchor] cyclonedx alias token in canonical position"
      else
        fail_msg "README [$anchor] cyclonedx alias token misordered [$run]"
      fi
    else
      if grep -qiE 'cyclonedx.{0,30}alias|alias.{0,30}cyclonedx' <<<"$line"; then
        warn_msg "README [$anchor] omits 'cyclonedx' alias token from pipe-list but documents alias parenthetically"
      else
        fail_msg "README [$anchor] missing 'cyclonedx' token with no alias note [$run]"
      fi
    fi
  else
    fail_msg "README [$anchor] primary-token missing/misordered [$run] want [$PRIMARY]"
  fi
done

echo "--- Check 3 (FAIL): alias notes present README-wide ---"
if grep -qE 'markdown.{0,30}alias.{0,10}md' "$README"; then
  pass "markdown->md alias note present"
else
  fail_msg "markdown->md alias note missing from README"
fi
if grep -qE 'cyclonedx.{0,30}alias.{0,30}cyclonedx-json' "$README"; then
  pass "cyclonedx->cyclonedx-json alias note present"
else
  fail_msg "cyclonedx->cyclonedx-json alias note missing from README"
fi
if grep -qE 'cyclonedx-xml.{0,30}no alias' "$README"; then
  pass "cyclonedx-xml no-alias note present"
else
  fail_msg "cyclonedx-xml no-alias note missing from README"
fi

echo "--- Check 4 (FAIL/WARN): format-matrix-dump.sh allowlist ---"
MFORMATS="$(grep -E '^FORMATS=' "$MATRIX" | head -n 1 | sed 's/^FORMATS="//; s/"$//' || true)"
if [[ -z "${MFORMATS:-}" ]]; then
  fail_msg "FORMATS= line missing in format-matrix-dump.sh"
else
  if [[ "$MFORMATS" == "$PRIMARY_SP" ]]; then
    pass "matrix FORMATS == 8 primaries in order"
  elif tokens_in_order "$MFORMATS" "$PRIMARY_SP"; then
    warn_msg "matrix FORMATS covers primaries but differs textually [$MFORMATS]"
  else
    fail_msg "matrix FORMATS missing/misordered primaries [$MFORMATS] want [$PRIMARY_SP]"
  fi
  if grep -qE '(^| )cyclonedx( |$)' <<<"$MFORMATS"; then
    pass "matrix FORMATS carries cyclonedx alias token"
  else
    warn_msg "matrix FORMATS omits 'cyclonedx' alias token (expected: alias duplicates cyclonedx-json branch)"
  fi
  case_missing=""
  for tok in $MFORMATS; do
    if ! grep -E '\|' "$MATRIX" | grep -qF "$tok"; then
      case_missing="$case_missing $tok"
    fi
  done
  if [[ -z "${case_missing// }" ]]; then
    pass "matrix --format case accepts all FORMATS tokens"
  else
    fail_msg "matrix --format case missing token(s):$case_missing"
  fi
fi

if (( fail == 0 )); then
  if (( warn == 0 )); then
    echo "FORMAT-LIST-PROBE OK"
  else
    echo "FORMAT-LIST-PROBE OK with warnings"
  fi
  exit 0
else
  echo "FORMAT-LIST-PROBE FAIL" >&2
  exit 1
fi
