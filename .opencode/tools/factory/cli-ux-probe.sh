#!/usr/bin/env bash
# cli-ux-probe.sh — CLI UX probe for issue #123 (collapsed generate-only CLI).
# Wraps: generate --help, exit-code matrix, stdout-vs-stderr split.
# Build-once + DLL-direct (0.7.0): single `dotnet build` at step 0, then every
# per-case run is `dotnet "$CLI_DLL" generate …` (no per-case `dotnet run`
# rebuild check). Timeout + --help + exit-code arms identical to 0.6.0.
# Matrix (all via DLL-direct `dotnet "$CLI_DLL" …` after the step-0 build):
#   help (generate --help)               -> 0 + <=6 flags + 7-format list
#   deleted-flags (each of 16 + --input) -> 2 (unknown option)
#   bad-format (generate --format bogus) -> 2 + Unsupported-format stderr
#   out-exists (--out existing, no force)-> 2
#   nested-out (missing parents created) -> 0 + file exists
#   strict phantom (Unknown + --strict)  -> 1
#   generate-default (empty dir)         -> 0 + empty SBOM (total 0)
#   generate-fixture (npm fixture)       -> 0 + total matches offline re-run
#   generate-missing (nonexistent path)  -> 2 + Input-not-found stderr
#   Split: successful --out run must have empty stdout (report -> file only).
# Rules: repo-relative, idempotent (temp files cleaned), no secrets.
VERSION="0.7.0"
set -euo pipefail

TIMEOUT_SECS=60
FIXTURE_REL="tests/Olaf.Tests/Fixtures/npm"
PROJECT_REL="src/Olaf.Cli"
REPO_ROOT=""
KEEP_TEMP=0
PHANTOM_PKG="this-package-definitely-does-not-exist-olaf-xyz"
PHANTOM_VER="9.9.9"
DELETED_FLAGS="--input --template --cache-dir --no-cache --refresh-cache --cache-ttl-days --ecosystem --max-image-mb --verbose --quiet --allow --deny --rules --direct-only --include-transitive --group-by-license"

usage() {
  cat <<EOF
Usage: $(basename "$0") [options]

CLI UX probe (issue #123): generate --help, exit-code matrix, stdout/stderr split.
Build-once + DLL-direct: single 'dotnet build', then every per-case run is
'dotnet <built-Olaf.Cli.dll> generate …' (no per-case rebuild check).

Options:
  --repo-root <dir>   Repo root (default: git top-level or CWD)
  --fixture <dir>     Fixture for non-strict runs (default: $FIXTURE_REL)
  --timeout <secs>    Per-run timeout in seconds (default: 60)
  --keep-temp         Keep temp dirs for debugging (default: remove)
  --help              Show this help and exit 0
  --version           Show version and exit 0

Checks:
  help exit 0 (+ <=6 flags + 7-format list)
  deleted-flags->2 (each unknown option) | bad-format->2 | out-exists->2
  nested-out->0 (+ file created) | strict-phantom->1
  generate-default->0 (+ empty SBOM total 0) | generate-fixture->0 (+ total match)
  generate-missing->2 (+ Input-not-found stderr)
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

# ---- Canonical run_gate (from _template.sh 0.2.11; mechanical back-port) ----
# run_gate <logfile> -- <cmd…>: redirect-to-file gate with RC capture.
# `set +e` precedes the command; always returns 0 so bare calls stay safe
# under `set -euo pipefail`; RC carries the verdict. run_cli below keeps its
# own split-stdout/stderr variant of the same skeleton (run_gate cannot split
# streams) — same set+e/capture/set-e order, cited here as canonical.
RC=0
run_gate() {
  local log="$1"; shift
  if [[ "${1:-}" == "--" ]]; then shift; fi
  set +e
  "$@" >"$log" 2>&1
  RC=$?
  set -e
}

# run_cli <outfile-prefix> <expected-exit> -- <cli args...>
# DLL-direct: `timeout dotnet "$CLI_DLL" …` (CLI_DLL pinned at step 0 after
# the single build). Timeout + exit-code arms identical to 0.6.0 `dotnet run`.
run_cli() {
  local tag="$1" want="$2"; shift 2
  if [[ "${1:-}" == "--" ]]; then shift; fi
  local out="$WORKDIR/$tag.stdout" err="$WORKDIR/$tag.stderr"
  set +e
  timeout "${TIMEOUT_SECS}s" dotnet "$CLI_DLL" "$@" >"$out" 2>"$err"
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

# ---- Canonical mkphantom (from _template.sh 0.2.7; guard-free: no python3 dep) ----
mkphantom() {
  local d="$1"
  mkdir -p "$d"
  printf '{\n  "name": "olaf-ux-strict-probe",\n  "version": "1.0.0",\n  "dependencies": {\n    "%s": "%s"\n  }\n}\n' "$PHANTOM_PKG" "$PHANTOM_VER" > "$d/package.json"
}

echo "== cli-ux-probe v$VERSION =="
echo "repo: $REPO_ROOT"
echo "project: $PROJECT_REL | fixture: $FIXTURE_REL"

echo "-- step 0: build CLI (one-time, keeps per-run timeouts meaningful) --"
run_gate "$WORKDIR/build.log" -- dotnet build "$PROJECT" --nologo -v minimal
if [[ "$RC" -ne 0 ]]; then
  echo "FAIL [build]: dotnet build exited $RC." >&2
  tail -20 "$WORKDIR/build.log" >&2
  exit 1
fi
pass "build" "dotnet build OK"
# Pin the built DLL: every per-case run below is DLL-direct (no rebuild check).
CLI_DLL="$(find "$PROJECT/bin/Debug" -maxdepth 2 -name 'Olaf.Cli.dll' -print 2>/dev/null | head -1)"
if [[ -z "${CLI_DLL:-}" || ! -f "$CLI_DLL" ]]; then
  echo "FAIL [build]: built DLL not found under $PROJECT/bin/Debug." >&2
  exit 1
fi
pass "build" "DLL pinned: ${CLI_DLL#"$REPO_ROOT"/}"

echo "-- step 1: generate --help (<=6 flags + 7-format list) --"
run_cli "help" 0 generate --help || true
HELP_TEXT="$(cat "$WORKDIR/help.stdout" "$WORKDIR/help.stderr" 2>/dev/null)"
FLAG_COUNT="$(grep -oE '^  --[a-z-]+' "$WORKDIR/help.stdout" 2>/dev/null | sort -u | wc -l | tr -d ' ')"
if [[ "$FLAG_COUNT" -le 6 ]]; then
  pass "help" "flag count $FLAG_COUNT (want <=6)"
else
  fail "help" "flag count $FLAG_COUNT (want <=6)"
fi
if echo "$HELP_TEXT" | grep -q "json|yaml|xml|md|cyclonedx-json|cyclonedx-xml|spdx-json"; then
  pass "help" "carries 7-format list"
else
  fail "help" "missing 7-format list"
fi

echo "-- step 2: deleted flags are unknown-option exit 2 --"
for flag in $DELETED_FLAGS; do
  # shellcheck disable=SC2086
  run_cli "deleted-$flag" 2 generate "$FIXTURE" $flag bogus || true
done

echo "-- step 3: exit-code matrix --"
run_cli "bad-format" 2 generate "$FIXTURE" --format bogus || true
if grep -q "Unsupported format" "$WORKDIR/bad-format.stderr" 2>/dev/null; then
  pass "bad-format" "stderr mentions Unsupported format"
else
  fail "bad-format" "stderr missing Unsupported format (tail: $(tail -c 200 "$WORKDIR/bad-format.stderr" | tr '\n' ' '))"
fi

# out-exists: pre-create file, run without --force -> 2
echo "pre-existing" > "$WORKDIR/exists.json"
run_cli "out-exists" 2 generate "$FIXTURE" --out "$WORKDIR/exists.json" || true

# nested-out: parents missing -> 0 + file created
NESTED="$WORKDIR/a/b/c/out.json"
run_cli "nested-out" 0 generate "$FIXTURE" --offline --out "$NESTED" || true
if [[ -s "$NESTED" ]]; then
  pass "nested-out" "file created ($(wc -c <"$NESTED" | tr -d ' ') bytes)"
else
  fail "nested-out" "file missing/empty: $NESTED"
fi

# strict phantom: guaranteed Unknown offline -> 1
PHANTOM_DIR="$WORKDIR/phantom"
mkphantom "$PHANTOM_DIR"
run_cli "strict-fixture" 1 generate "$PHANTOM_DIR" --offline --strict || true

echo "-- step 4: stdout-vs-stderr split (--out run must have empty stdout) --"
SPLIT_OUT="$WORKDIR/split.json"
# Split-stream variant of the canonical run_gate skeleton (run_gate cannot
# split stdout/stderr): same set+e/capture/set-e order, DLL-direct.
set +e
timeout "${TIMEOUT_SECS}s" dotnet "$CLI_DLL" generate "$FIXTURE" --offline --out "$SPLIT_OUT" >"$WORKDIR/split.stdout" 2>"$WORKDIR/split.stderr"
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

echo "-- step 5: generate default/missing arms --"
# generate-default: manifest-less empty dir -> 0 + valid empty SBOM (total 0, licenses [])
mkdir -p "$WORKDIR/gen-empty"
run_cli "generate-default" 0 generate "$WORKDIR/gen-empty" || true
if grep -q '"total":0' "$WORKDIR/generate-default.stdout" 2>/dev/null; then
  pass "generate-default" "empty SBOM total 0"
else
  fail "generate-default" "stdout missing total 0 (tail: $(tail -c 200 "$WORKDIR/generate-default.stdout" | tr '\n' ' '))"
fi
if grep -q '"licenses":\[\]' "$WORKDIR/generate-default.stdout" 2>/dev/null; then
  pass "generate-default" "empty SBOM licenses []"
else
  fail "generate-default" "stdout missing empty licenses array"
fi

# generate-fixture: offline run -> 0 + total matches a second offline re-run
# (re-run rides run_cli: DLL-direct, exit-0 arm identical, output at
# generate-parity.stdout for the total cross-check below).
run_cli "generate-fixture" 0 generate "$FIXTURE" --offline || true
run_cli "generate-parity" 0 generate "$FIXTURE" --offline || true
GEN_TOTAL="$(grep -o '"total":[0-9]*' "$WORKDIR/generate-fixture.stdout" 2>/dev/null | head -1)"
LEG_TOTAL="$(grep -o '"total":[0-9]*' "$WORKDIR/generate-parity.stdout" 2>/dev/null | head -1)"
if [[ -n "$GEN_TOTAL" && "$GEN_TOTAL" == "$LEG_TOTAL" ]]; then
  pass "generate-fixture" "parity total $GEN_TOTAL"
else
  fail "generate-fixture" "total mismatch generate=${GEN_TOTAL:-?} rerun=${LEG_TOTAL:-?}"
fi

# generate-missing-path: nonexistent path -> 2 + Input-not-found stderr
run_cli "generate-missing-path" 2 generate "$WORKDIR/does-not-exist-olaf-xyz" || true
if grep -q "Input not found" "$WORKDIR/generate-missing-path.stderr" 2>/dev/null; then
  pass "generate-missing-path" "stderr mentions Input not found"
else
  fail "generate-missing-path" "stderr missing Input not found (tail: $(tail -c 200 "$WORKDIR/generate-missing-path.stderr" | tr '\n' ' '))"
fi

echo "== summary =="
if [[ "$FAIL" -eq 0 ]]; then
  echo "UX-PROBE OK: all checks passed."
  exit 0
else
  echo "UX-PROBE FAILED: see FAIL lines above." >&2
  exit 1
fi
