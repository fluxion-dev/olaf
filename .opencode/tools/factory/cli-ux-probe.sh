#!/usr/bin/env bash
# cli-ux-probe.sh — CLI UX probe for issue #4 (CLI UX polish).
# Wraps: --help, --version, exit-code matrix, stdout-vs-stderr split.
# Matrix (all via `dotnet run --project src/Olaf.Cli -- ...`):
#   missing-input (no --input)            -> 2
#   bare-path (positional path, no flag)  -> 1 + Unrecognized-command stderr
#   bad-format (--format bogus)           -> 2
#   bad-ecosystem (--ecosystem bogus)     -> 2
#   out-exists (--out existing, no force) -> 2
#   nested-out (missing parents created)  -> 0 + file exists
#   strict phantom (Unknown + --strict)   -> 1
#   template-missing (--template nofile)  -> 2 + Template-not-found stderr
#   template-badsyntax (unclosed {{#each}})-> 2 + line-N stderr
# Split: successful --out run must have empty stdout (report -> file only).
# Help: --help output mentions --template (additive, --input check untouched).
# Rules: repo-relative, idempotent (temp files cleaned), no secrets.
VERSION="0.3.0"
set -euo pipefail

TIMEOUT_SECS=60
FIXTURE_REL="tests/Olaf.Tests/Fixtures/npm"
PROJECT_REL="src/Olaf.Cli"
REPO_ROOT=""
KEEP_TEMP=0
PHANTOM_PKG="this-package-definitely-does-not-exist-olaf-xyz"
PHANTOM_VER="9.9.9"

usage() {
  cat <<EOF
Usage: $(basename "$0") [options]

CLI UX probe (issue #4): --help/--version, exit-code matrix, stdout/stderr split.

Options:
  --repo-root <dir>   Repo root (default: git top-level or CWD)
  --fixture <dir>     Fixture for non-strict runs (default: $FIXTURE_REL)
  --timeout <secs>    Per-run timeout in seconds (default: 60)
  --keep-temp         Keep temp dirs for debugging (default: remove)
  --help              Show this help and exit 0
  --version           Show version and exit 0

Checks:
  help exit 0 (+ mentions --input, --template) | version exit 0 (non-empty)
  missing-input->2 | bare-path->1 (+ Unrecognized-command stderr)
  bad-format->2 | bad-ecosystem->2 | out-exists->2
  nested-out->0 (+ file created) | strict-phantom->1
  template-missing->2 (+ Template-not-found stderr)
  template-badsyntax->2 (+ line-N stderr)
  split: --out run has empty stdout

Exit codes: 0 all PASS, 1 assertion failure, 2 usage/environment error.

Examples:
  $(basename "$0")
  $(basename "$0") --timeout 30 --keep-temp
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --help) usage; exit 0 ;;
    --version) echo "$(basename "$0") $VERSION"; exit 0 ;;
    --repo-root) REPO_ROOT="${2:-}"; shift 2 ;;
    --repo-root=*) REPO_ROOT="${1#*=}"; shift ;;
    --fixture) FIXTURE_REL="${2:-}"; shift 2 ;;
    --fixture=*) FIXTURE_REL="${1#*=}"; shift ;;
    --timeout) TIMEOUT_SECS="${2:-}"; shift 2 ;;
    --timeout=*) TIMEOUT_SECS="${1#*=}"; shift ;;
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
FIXTURE="$REPO_ROOT/$FIXTURE_REL"
[[ -d "$PROJECT" ]] || { echo "CLI project not found: $PROJECT" >&2; exit 2; }
[[ -e "$FIXTURE" ]] || { echo "Fixture not found: $FIXTURE" >&2; exit 2; }

for cmd in dotnet timeout; do
  command -v "$cmd" >/dev/null 2>&1 || { echo "Missing required command: $cmd" >&2; exit 2; }
done

WORKDIR="$(mktemp -d -t olaf-ux-probe-XXXXXX)"
cleanup() {
  if [[ "$KEEP_TEMP" -eq 1 ]]; then
    echo "keeping temp dir: $WORKDIR"
  else
    rm -rf "$WORKDIR"
  fi
}
trap cleanup EXIT

FAIL=0
pass() { echo "PASS [$1]: $2"; }
fail() { echo "FAIL [$1]: $2"; FAIL=1; }

# run_cli <outfile-prefix> <expected-desc...> -- <cli args...>
# Captures exit code, stdout, stderr separately. Asserts exit code.
run_cli() {
  local tag="$1" want="$2"; shift 2
  # remaining args after -- are CLI args
  if [[ "${1:-}" == "--" ]]; then shift; fi
  local out="$WORKDIR/$tag.stdout" err="$WORKDIR/$tag.stderr"
  set +e
  timeout "${TIMEOUT_SECS}s" dotnet run --project "$PROJECT" -- "$@" >"$out" 2>"$err"
  local rc=$?
  set -e
  if [[ "$rc" -eq 124 ]]; then
    fail "$tag" "timed out after ${TIMEOUT_SECS}s"
    return 1
  fi
  if [[ "$rc" -eq "$want" ]]; then
    pass "$tag" "exit $rc (want $want)"
  else
    fail "$tag" "exit $rc (want $want; stderr: $(tail -c 200 "$err" | tr '\n' ' '))"
    return 1
  fi
  return 0
}

echo "== cli-ux-probe v$VERSION =="
echo "repo: $REPO_ROOT"
echo "project: $PROJECT_REL | fixture: $FIXTURE_REL"

echo "-- step 0: build CLI (one-time, keeps per-run timeouts meaningful) --"
if ! dotnet build "$PROJECT" --nologo -v minimal; then
  echo "FAIL [build]: dotnet build failed." >&2
  exit 1
fi
pass "build" "dotnet build OK"

echo "-- step 1: --help / --version --"
run_cli "help" 0 --help || true
if grep -q -- "--input" "$WORKDIR/help.stdout" 2>/dev/null || grep -q -- "--input" "$WORKDIR/help.stderr" 2>/dev/null; then
  pass "help" "mentions --input"
else
  fail "help" "output missing --input"
fi
if grep -q -- "--template" "$WORKDIR/help.stdout" 2>/dev/null || grep -q -- "--template" "$WORKDIR/help.stderr" 2>/dev/null; then
  pass "help" "mentions --template"
else
  fail "help" "output missing --template"
fi
run_cli "version" 0 --version || true
if [[ -s "$WORKDIR/version.stdout" ]]; then
  pass "version" "non-empty stdout ($(tr -d '\n' <"$WORKDIR/version.stdout" | head -c 80))"
else
  fail "version" "empty stdout"
fi

echo "-- step 2: exit-code matrix --"
run_cli "missing-input" 2 || true
# bare-path: positional fixture path without --input (e.g. tests/Olaf.Tests/Fixtures/npm) -> 1
run_cli "bare-path" 1 "$FIXTURE" || true
if grep -q "Unrecognized command" "$WORKDIR/bare-path.stderr" 2>/dev/null; then
  pass "bare-path" "stderr mentions Unrecognized command"
else
  fail "bare-path" "stderr missing Unrecognized command (tail: $(tail -c 200 "$WORKDIR/bare-path.stderr" | tr '\n' ' '))"
fi
run_cli "bad-format" 2 --input "$FIXTURE" --format bogus || true
run_cli "bad-ecosystem" 2 --input "$FIXTURE" --ecosystem bogus || true

# out-exists: pre-create file, run without --force -> 2
echo "pre-existing" > "$WORKDIR/exists.json"
run_cli "out-exists" 2 --input "$FIXTURE" --out "$WORKDIR/exists.json" || true

# nested-out: parents missing -> 0 + file created
NESTED="$WORKDIR/a/b/c/out.json"
run_cli "nested-out" 0 --input "$FIXTURE" --out "$NESTED" || true
if [[ -s "$NESTED" ]]; then
  pass "nested-out" "file created ($(wc -c <"$NESTED" | tr -d ' ') bytes)"
else
  fail "nested-out" "file missing/empty: $NESTED"
fi

# strict phantom: guaranteed Unknown offline -> 1
PHANTOM_DIR="$WORKDIR/phantom"
mkdir -p "$PHANTOM_DIR"
cat > "$PHANTOM_DIR/package.json" <<EOF
{
  "name": "olaf-ux-strict-probe",
  "version": "1.0.0",
  "dependencies": {
    "$PHANTOM_PKG": "$PHANTOM_VER"
  }
}
EOF
run_cli "strict-fixture" 1 --input "$PHANTOM_DIR" --strict || true

echo "-- step 3: stdout-vs-stderr split (--out run must have empty stdout) --"
SPLIT_OUT="$WORKDIR/split.json"
set +e
timeout "${TIMEOUT_SECS}s" dotnet run --project "$PROJECT" -- --input "$FIXTURE" --out "$SPLIT_OUT" >"$WORKDIR/split.stdout" 2>"$WORKDIR/split.stderr"
SPLIT_RC=$?
set -e
if [[ "$SPLIT_RC" -ne 0 ]]; then
  fail "split" "scan --out exited $SPLIT_RC (want 0)"
else
  pass "split" "scan --out exit 0"
  if [[ -s "$SPLIT_OUT" ]]; then
    pass "split" "report file written ($(wc -c <"$SPLIT_OUT" | tr -d ' ') bytes)"
  else
    fail "split" "report file missing/empty: $SPLIT_OUT"
  fi
  if [[ ! -s "$WORKDIR/split.stdout" ]]; then
    pass "split" "stdout empty (report went to file only)"
  else
    fail "split" "stdout NOT empty ($(wc -c <"$WORKDIR/split.stdout" | tr -d ' ') bytes leak to stdout)"
  fi
fi

echo "-- step 4: template exit-code arms (issue #73, additive) --"
run_cli "template-missing" 2 --input "$FIXTURE" --template "$WORKDIR/does-not-exist.scriban" || true
if grep -q "Template not found" "$WORKDIR/template-missing.stderr" 2>/dev/null; then
  pass "template-missing" "stderr mentions Template not found"
else
  fail "template-missing" "stderr missing Template not found (tail: $(tail -c 200 "$WORKDIR/template-missing.stderr" | tr '\n' ' '))"
fi
printf 'header\n{{#each licenses}}\nno-close\n' > "$WORKDIR/bad-template.scriban"
run_cli "template-badsyntax" 2 --input "$FIXTURE" --template "$WORKDIR/bad-template.scriban" || true
if grep -qE "line [0-9]+" "$WORKDIR/template-badsyntax.stderr" 2>/dev/null; then
  pass "template-badsyntax" "stderr carries line number"
else
  fail "template-badsyntax" "stderr missing line number (tail: $(tail -c 200 "$WORKDIR/template-badsyntax.stderr" | tr '\n' ' '))"
fi

echo "== summary =="
if [[ "$FAIL" -eq 0 ]]; then
  echo "UX-PROBE OK: all checks passed."
  exit 0
else
  echo "UX-PROBE FAILED: see FAIL lines above." >&2
  exit 1
fi
