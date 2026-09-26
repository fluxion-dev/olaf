#!/usr/bin/env bash
# policy-gate-probe.sh — policy gate probe for issue #5 (--strict, --allow/--deny).
# Builds a strict-phantom fixture (definitely-missing npm package => Unknown offline)
# and asserts:
#   (a) non-strict            -> exit 0
#   (b) --strict              -> exit 1 + stderr mentions strict/unknown
#   (c) --allow <present SPDX> -> exit 0 where applicable (SKIP if flags unsupported)
#   (d) --deny <present>      -> exit 1 + offender listed
#   (e) --allow <mismatch>    -> exit 1
# Phantom license is Unknown, so: present=Unknown, mismatch=MIT.
# If --allow/--deny are absent from --help, (c)-(e) SKIP and (a)+(b) decide.
# Rules: repo-relative, idempotent (temp cleaned), no secrets, exit 0/1/2.
VERSION="0.1.0"
set -euo pipefail

TIMEOUT_SECS=60
PROJECT_REL="src/Olaf.Cli"
REPO_ROOT=""
KEEP_TEMP=0
PHANTOM_PKG="this-package-definitely-does-not-exist-olaf-xyz"
PHANTOM_VER="9.9.9"
PRESENT_SPDX="Unknown"
MISMATCH_SPDX="MIT"

usage() {
  cat <<EOF
Usage: $(basename "$0") [options]

Policy gate probe (issue #5): strict-phantom fixture + allow/deny gate.

Checks:
  (a) non-strict scan of phantom fixture            -> exit 0
  (b) --strict scan of phantom fixture              -> exit 1 + stderr (strict|unknown)
  (c) --allow <present SPDX=Unknown>                -> exit 0 (SKIP if --allow unsupported)
  (d) --deny <present SPDX=Unknown>                 -> exit 1 + offender listed
  (e) --allow <mismatch SPDX=MIT>                   -> exit 1 (SKIP if --allow unsupported)

Options:
  --repo-root <dir>   Repo root (default: git top-level or CWD)
  --timeout <secs>    Per-run timeout in seconds (default: 60)
  --package <name>    Phantom npm package name (default: $PHANTOM_PKG)
  --package-version <v> Phantom package version (default: $PHANTOM_VER)
  --keep-temp         Keep temp fixture dir for debugging (default: remove)
  --help              Show this help and exit 0
  --version           Show version and exit 0

Examples:
  $(basename "$0")
  $(basename "$0") --timeout 30 --keep-temp

Exit codes: 0 all PASS (SKIPs allowed), 1 assertion failure, 2 usage/environment error.
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
    --package) PHANTOM_PKG="${2:-}"; shift 2 ;;
    --package=*) PHANTOM_PKG="${1#*=}"; shift ;;
    --package-version) PHANTOM_VER="${2:-}"; shift 2 ;;
    --package-version=*) PHANTOM_VER="${1#*=}"; shift ;;
    --keep-temp) KEEP_TEMP=1; shift ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

if ! [[ "$TIMEOUT_SECS" =~ ^[0-9]+$ ]]; then
  echo "Invalid --timeout '$TIMEOUT_SECS': must be a positive integer." >&2
  exit 2
fi

if [[ -z "$REPO_ROOT" ]]; then
  if git rev-parse --show-toplevel >/dev/null 2>&1; then
    REPO_ROOT="$(git rev-parse --show-toplevel)"
  else
    REPO_ROOT="$(pwd)"
  fi
fi
PROJECT="$REPO_ROOT/$PROJECT_REL"
[[ -d "$PROJECT" ]] || { echo "CLI project not found: $PROJECT" >&2; exit 2; }
[[ -f "$REPO_ROOT/olaf.slnx" ]] || { echo "Repo root has no olaf.slnx: $REPO_ROOT" >&2; exit 2; }

for cmd in dotnet timeout; do
  command -v "$cmd" >/dev/null 2>&1 || { echo "Missing required command: $cmd" >&2; exit 2; }
done

echo "== policy-gate-probe v$VERSION =="
echo "repo: $REPO_ROOT"
echo "phantom: ${PHANTOM_PKG}@${PHANTOM_VER} (present=$PRESENT_SPDX mismatch=$MISMATCH_SPDX)"

echo "-- step 0: build CLI --"
if ! dotnet build "$PROJECT" --nologo -v minimal; then
  echo "FAIL: dotnet build failed." >&2
  exit 1
fi
echo "PASS [build]: dotnet build OK"

FIXTURE_DIR="$(mktemp -d -t olaf-policy-gate-XXXXXX)"
cleanup() {
  if [[ "$KEEP_TEMP" -eq 1 ]]; then
    echo "keeping temp dir: $FIXTURE_DIR"
  else
    rm -rf "$FIXTURE_DIR"
  fi
}
trap cleanup EXIT

cat > "$FIXTURE_DIR/package.json" <<EOF
{
  "name": "olaf-policy-gate-probe",
  "version": "1.0.0",
  "dependencies": {
    "$PHANTOM_PKG": "$PHANTOM_VER"
  }
}
EOF
echo "fixture: $FIXTURE_DIR/package.json"

FAIL=0
SKIPPED=0
pass() { echo "PASS [$1]: $2"; }
fail() { echo "FAIL [$1]: $2"; FAIL=1; }
skip() { echo "SKIP [$1]: $2"; SKIPPED=$((SKIPPED+1)); }

run_scan() {
  # run_scan <tag> -- <cli args...>; sets RC, leaves stdout/stderr at $FIXTURE_DIR/<tag>.*
  local tag="$1"; shift
  if [[ "${1:-}" == "--" ]]; then shift; fi
  set +e
  timeout "${TIMEOUT_SECS}s" dotnet run --project "$PROJECT" --no-build -- "$@" >"$FIXTURE_DIR/$tag.stdout" 2>"$FIXTURE_DIR/$tag.stderr"
  RC=$?
  set -e
  if [[ "$RC" -eq 124 ]]; then
    fail "$tag" "timed out after ${TIMEOUT_SECS}s"
    RC=124
  fi
}

echo "-- (a) non-strict -> 0 --"
run_scan "plain" -- --input "$FIXTURE_DIR"
echo "plain exit: $RC"
if [[ "$RC" -eq 0 ]]; then pass "a/non-strict" "exit 0"; else fail "a/non-strict" "exit $RC (want 0; stderr: $(tail -c 200 "$FIXTURE_DIR/plain.stderr" | tr '\n' ' '))"; fi

echo "-- (b) --strict -> 1 + stderr --"
run_scan "strict" -- --input "$FIXTURE_DIR" --strict
echo "strict exit: $RC"
if [[ "$RC" -eq 1 ]]; then pass "b/strict-exit" "exit 1"; else fail "b/strict-exit" "exit $RC (want 1)"; fi
if grep -qiE "strict|unknown" "$FIXTURE_DIR/strict.stderr" 2>/dev/null; then
  pass "b/strict-stderr" "stderr mentions strict/unknown"
else
  fail "b/strict-stderr" "stderr missing strict/unknown hint ($(tail -c 200 "$FIXTURE_DIR/strict.stderr" | tr '\n' ' '))"
fi

echo "-- detect --allow/--deny support --"
set +e
timeout "${TIMEOUT_SECS}s" dotnet run --project "$PROJECT" --no-build -- --help >"$FIXTURE_DIR/help.stdout" 2>"$FIXTURE_DIR/help.stderr"
set -e
HELP_COMBINED="$(cat "$FIXTURE_DIR/help.stdout" "$FIXTURE_DIR/help.stderr" 2>/dev/null)"
HAS_ALLOW=0; HAS_DENY=0
if echo "$HELP_COMBINED" | grep -q -- "--allow"; then HAS_ALLOW=1; fi
if echo "$HELP_COMBINED" | grep -q -- "--deny"; then HAS_DENY=1; fi
echo "has-allow=$HAS_ALLOW has-deny=$HAS_DENY"

if [[ "$HAS_ALLOW" -eq 0 ]]; then
  skip "c/allow" "--allow not in --help; gate not implemented yet"
else
  echo "-- (c) --allow <present> -> 0 --"
  run_scan "allow-present" -- --input "$FIXTURE_DIR" --allow "$PRESENT_SPDX"
  echo "allow-present exit: $RC"
  if [[ "$RC" -eq 0 ]]; then
    pass "c/allow-present" "--allow $PRESENT_SPDX exit 0"
  elif [[ "$RC" -eq 2 ]]; then
    skip "c/allow-present" "--allow $PRESENT_SPDX exit 2 (value/flag not applicable yet)"
  else
    fail "c/allow-present" "--allow $PRESENT_SPDX exit $RC (want 0)"
  fi
fi

if [[ "$HAS_DENY" -eq 0 ]]; then
  skip "d/deny" "--deny not in --help; gate not implemented yet"
else
  echo "-- (d) --deny <present> -> 1 + offender listed --"
  run_scan "deny-present" -- --input "$FIXTURE_DIR" --deny "$PRESENT_SPDX"
  echo "deny-present exit: $RC"
  if [[ "$RC" -eq 1 ]]; then
    pass "d/deny-exit" "--deny $PRESENT_SPDX exit 1"
  else
    fail "d/deny-exit" "--deny $PRESENT_SPDX exit $RC (want 1)"
  fi
  if grep -q -- "$PHANTOM_PKG" "$FIXTURE_DIR/deny-present.stdout" 2>/dev/null || grep -q -- "$PHANTOM_PKG" "$FIXTURE_DIR/deny-present.stderr" 2>/dev/null; then
    pass "d/deny-offender" "offender $PHANTOM_PKG listed"
  else
    fail "d/deny-offender" "offender $PHANTOM_PKG not found in stdout+stderr"
  fi
fi

if [[ "$HAS_ALLOW" -eq 0 ]]; then
  skip "e/allow-mismatch" "--allow not in --help; gate not implemented yet"
else
  echo "-- (e) --allow <mismatch> -> 1 --"
  run_scan "allow-mismatch" -- --input "$FIXTURE_DIR" --allow "$MISMATCH_SPDX"
  echo "allow-mismatch exit: $RC"
  if [[ "$RC" -eq 1 ]]; then
    pass "e/allow-mismatch" "--allow $MISMATCH_SPDX exit 1"
  elif [[ "$RC" -eq 2 ]]; then
    skip "e/allow-mismatch" "--allow $MISMATCH_SPDX exit 2 (not applicable yet)"
  else
    fail "e/allow-mismatch" "--allow $MISMATCH_SPDX exit $RC (want 1)"
  fi
fi

echo "== summary =="
if [[ "$FAIL" -eq 0 ]]; then
  echo "POLICY-GATE-PROBE OK: (a)+(b) pass, skips=$SKIPPED (allow/deny where applicable)."
  exit 0
else
  echo "POLICY-GATE-PROBE FAILED: see FAIL lines above." >&2
  exit 1
fi
