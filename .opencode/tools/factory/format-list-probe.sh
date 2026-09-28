#!/usr/bin/env bash
# format-list-probe.sh — Supported-format list drift probe (format-list class).
# Issue #123: canonical 7-token format list, zero aliases.
# 0.3.1: ScanRunner.CLI const may alias FormatterRegistry.SupportedFormats
#   (single source of truth) instead of repeating the literal — both PASS.
# Verifies the canonical 7-token format list stays consistent across:
#   1. FormatterRegistry SupportedFormats const (canonical source) vs
#      Registry error string (must equal const) + per-token switch arms
#      (deleted aliases markdown/cyclonedx must be ABSENT)
#   2. live `generate --help` runtime text (must print const verbatim)
#   3. README anchored lines (WARN-only until the Step 7 rewrite lands)
#   4. format-matrix-dump.sh FORMATS allowlist (7 tokens in order)
# Rules: repo-relative, read-only (never edits src/docs), idempotent
# (stdout only), no secrets. No temp files: live --help is captured in a
# shell variable, so no --workdir/--keep-temp flags.
# Template-based (_template 0.2.2 blocks: root resolution + pass/fail_msg).
VERSION="0.3.1"
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
  echo "Verifies the canonical 7-token format list (zero aliases) agrees across:"
  echo "  1. FormatterRegistry SupportedFormats const (canonical source)"
  echo "  2. Registry error string + per-token switch arms (aliases absent)"
  echo "  3. live 'generate --help' runtime text (must print the const verbatim)"
  echo "  4. README anchored lines (WARN-only until Step 7 rewrite)"
  echo "  5. format-matrix-dump.sh FORMATS allowlist (7 tokens in order)"
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

REGISTRY="$ROOT/src/Olaf.Formatters/FormatterRegistry.cs"
SCANRUNNER="$ROOT/src/Olaf.Cli/ScanRunner.cs"
README="$ROOT/README.md"
MATRIX="$ROOT/.opencode/tools/factory/format-matrix-dump.sh"
[[ -f "$REGISTRY" ]] || { echo "Not found: $REGISTRY" >&2; exit 2; }
[[ -f "$SCANRUNNER" ]] || { echo "Not found: $SCANRUNNER" >&2; exit 2; }
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

CANON7="json|yaml|xml|md|cyclonedx-json|cyclonedx-xml|spdx-json"

echo "--- Check 1 (FAIL): Registry const vs CLI const vs error string vs switch arms ---"
CANON="$(grep -oE 'SupportedFormats = "[^"]+"' "$REGISTRY" | head -n 1 | sed 's/.*= *"//; s/"$//' || true)"
[[ -n "${CANON:-}" ]] || fail_msg "SupportedFormats const missing in FormatterRegistry.cs"
if [[ -n "${CANON:-}" && "$CANON" != "$CANON7" ]]; then
  fail_msg "registry const [$CANON] != canonical 7 [$CANON7]"
elif [[ -n "${CANON:-}" ]]; then
  pass "registry const == canonical 7 [$CANON]"
fi
CLICONST="$(grep -oE 'SupportedFormats = "[^"]+"' "$SCANRUNNER" | head -n 1 | sed 's/.*= *"//; s/"$//' || true)"
if [[ -n "${CLICONST:-}" && "$CLICONST" == "$CANON" ]]; then
  pass "ScanRunner const == registry const"
elif grep -qE 'SupportedFormats = FormatterRegistry\.SupportedFormats' "$SCANRUNNER"; then
  pass "ScanRunner const aliases FormatterRegistry.SupportedFormats (single source of truth)"
else
  fail_msg "ScanRunner const [${CLICONST:-<missing>}] != registry const [$CANON]"
fi
REGERR="$(grep -oE 'Supported: [a-z|][a-z|-]*\.' "$REGISTRY" | head -n 1 | sed 's/^Supported: //; s/\.$//' || true)"
if [[ -z "${REGERR:-}" ]] && grep -qF 'Supported: {SupportedFormats}' "$REGISTRY"; then
  # Interpolated const: error string equals CANON by construction.
  REGERR="$CANON"
  pass "registry error string interpolates SupportedFormats const"
fi
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
  for alias in markdown cyclonedx txt html; do
    if grep -qE "\"$alias\" =>" "$REGISTRY"; then
      fail_msg "registry still carries deleted alias/format arm: $alias"
    else
      pass "deleted arm absent: $alias"
    fi
  done
fi
HELP_TEXT="$(timeout "${TIMEOUT_SECS}s" dotnet run --project "$ROOT/src/Olaf.Cli" -- generate --help 2>&1)" || { echo "live generate --help failed (exit $?) — environment error." >&2; exit 2; }
if [[ -n "${CANON:-}" ]]; then
  if grep -qF "$CANON" <<<"$HELP_TEXT"; then
    pass "live generate --help prints canonical token set in order"
  else
    SEEN="$(grep -oE 'Output format: [a-z][a-z|-]*' <<<"$HELP_TEXT" | head -n 1 | sed 's/^Output format: //' || true)"
    fail_msg "live generate --help missing canonical list [$CANON]; seen [${SEEN:-<none>}]"
  fi
fi

echo "--- Check 2 (WARN-only): README anchors (Step 7 rewrite pending) ---"
warn_msg "README format lists rewrite in Step 7 (docs-only lock); src is authoritative"
for anchor in "Supported formats:" "Flags:.*--format" "Output format:"; do
  line="$(grep -E "$anchor" "$README" | head -n 1 || true)"
  if [[ -z "${line:-}" ]]; then
    warn_msg "README anchor missing: $anchor"
    continue
  fi
  run="$(grep -oE '[a-z][a-z-]*(\|[a-z][a-z-]+)+' <<<"$line" | grep -F "json" | head -n 1 || true)"
  echo "INFO: README [$anchor] pipe-list: [${run:-<none>}]"
done

echo "--- Check 3 (FAIL): format-matrix-dump.sh allowlist ---"
MFORMATS="$(grep -E '^FORMATS=' "$MATRIX" | head -n 1 | sed 's/^FORMATS="//; s/"$//' || true)"
CANON7_SP="${CANON7//|/ }"
if [[ -z "${MFORMATS:-}" ]]; then
  fail_msg "FORMATS= line missing in format-matrix-dump.sh"
elif [[ "$MFORMATS" == "$CANON7_SP" ]]; then
  pass "matrix FORMATS == canonical 7 in order"
elif tokens_in_order "$MFORMATS" "$CANON7_SP"; then
  warn_msg "matrix FORMATS covers canonical 7 but differs textually [$MFORMATS]"
else
  fail_msg "matrix FORMATS missing/misordered tokens [$MFORMATS] want [$CANON7_SP]"
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
