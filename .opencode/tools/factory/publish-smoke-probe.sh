#!/usr/bin/env bash
# publish-smoke-probe.sh — single-file publish + no-runtime smoke probe for issue #126.
# Publishes the RID matrix (linux-x64/osx-arm64/win-x64, --rid all|<rid>) via the
# -p:PublishRID=<rid> property flow in src/Olaf.Cli/Olaf.Cli.csproj, gates
# single-file shape (one apphost, no *.dll beside it), then smokes the published
# apphost with dotnet stripped from PATH (--help/--version exit 0, npm-fixture
# offline generate exit 0 + JSON parse, --strict exit 1, missing-path + bad-format
# exit 2).
# Correctness notes (tester's verified run): apphost filename is `Olaf.Cli`
# (NOT lowercase `olaf`; win-x64 `Olaf.Cli.exe`); shell MUST be bash (zsh lacks
# shopt nullglob; release.yml run: default is bash); osx-arm64/win-x64 binaries
# cannot execute on linux (existence+size+single-file only, runtime CI-only).
# Rules: repo-relative, idempotent, no secrets, exit 0/1/2.
VERSION="0.1.0"
set -euo pipefail
shopt -s nullglob

# ---- Canonical root resolution (copy-paste; do not hardcode paths) ----
# Priority: --repo-root flag -> git top-level -> script-dir fallback.
# NOTE: promoted factory tools live at .opencode/tools/factory/ so repo root is
#   ../../.. from the script dir. Scratch prototypes live one level deeper
#   (.opencode/tools/factory/scratch/) so use ../../../.. there instead.
REPO_ROOT=""
RID="all"
TIMEOUT_SECS=600
WORKDIR=""
KEEP_TEMP=0
SMOKE_TIMEOUT=120
PHANTOM_PKG="this-package-definitely-does-not-exist-olaf-xyz"
PHANTOM_VER="9.9.9"

usage() {
  cat <<EOF
Usage: $(basename "$0") [options]

Single-file publish + no-runtime smoke: publishes the RID matrix via
-p:PublishRID=<rid>, gates single-file shape, smokes the linux-x64 apphost
with dotnet stripped from PATH.

Options:
  --rid <rid>           linux-x64 | osx-arm64 | win-x64 | all (default: all)
  --repo-root <dir>     Repo root (default: git top-level or script-relative fallback)
  --timeout <secs>      Per-publish timeout in seconds (default: 600)
  --workdir <dir>       Work dir for fixtures/logs (default: mktemp)
  --keep-temp           Keep work dir for debugging (default: remove)
  --help                Show this help and exit 0
  --version             Show version and exit 0

Examples:
  $(basename "$0") --rid linux-x64
  $(basename "$0") --rid all --repo-root /path/to/worktree

Exit codes: 0 PASS, 1 FAIL (probe assertion failed), 2 usage/environment error.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --help) usage; exit 0 ;;
    --version) echo "$(basename "$0") $VERSION"; exit 0 ;;
    --rid) RID="${2:-}"; shift 2 ;;
    --rid=*) RID="${1#*=}"; shift ;;
    --repo-root) REPO_ROOT="${2:-}"; shift 2 ;;
    --repo-root=*) REPO_ROOT="${1#*=}"; shift ;;
    --timeout) TIMEOUT_SECS="${2:-}"; shift 2 ;;
    --timeout=*) TIMEOUT_SECS="${1#*=}"; shift ;;
    --workdir) WORKDIR="${2:-}"; shift 2 ;;
    --workdir=*) WORKDIR="${1#*=}"; shift ;;
    --keep-temp) KEEP_TEMP=1; shift ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

case "$RID" in
  linux-x64|osx-arm64|win-x64|all) ;;
  *) echo "Invalid --rid '$RID': want linux-x64|osx-arm64|win-x64|all." >&2; exit 2 ;;
esac
if ! [[ "$TIMEOUT_SECS" =~ ^[0-9]+$ ]]; then
  echo "Invalid --timeout '$TIMEOUT_SECS': must be a positive integer." >&2
  exit 2
fi

# Resolve repo root: explicit flag > git top-level > script-relative fallback.
# Promoted depth ../../.. first (scratch depth ../../../.. second); verified
# against parser-coverage-probe.sh:11 which uses ../../...
if [[ -z "$REPO_ROOT" ]]; then
  if git rev-parse --show-toplevel >/dev/null 2>&1; then
    REPO_ROOT="$(git rev-parse --show-toplevel)"
  else
    SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
    if [[ -f "$SCRIPT_DIR/../../../olaf.slnx" ]]; then
      REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
    elif [[ -f "$SCRIPT_DIR/../../../../olaf.slnx" ]]; then
      REPO_ROOT="$(cd "$SCRIPT_DIR/../../../.." && pwd)"
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

for cmd in dotnet timeout python3; do
  if ! command -v "$cmd" >/dev/null 2>&1; then
    echo "Missing required command: $cmd" >&2
    exit 2
  fi
done
# Absolute path: smoke runs execute under a dotnet-stripped PATH where bare
# `timeout` would not resolve (exit 127). Publish runs keep the full PATH.
TIMEOUT_BIN="$(command -v timeout)"

NPMFIX="$REPO_ROOT/tests/Olaf.Tests/Fixtures/npm"
if [[ ! -f "$NPMFIX/package.json" ]]; then
  echo "Missing npm fixture: $NPMFIX/package.json" >&2
  exit 2
fi

if [[ -z "$WORKDIR" ]]; then
  WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/publish-smoke-XXXXXX")"
  MADE_TMP=1
else
  mkdir -p "$WORKDIR"
  MADE_TMP=0
fi
cleanup() {
  if (( ! KEEP_TEMP )) && (( MADE_TMP )) && [[ -d "$WORKDIR" ]]; then
    rm -rf "$WORKDIR"
  fi
}
trap cleanup EXIT

# ---- Canonical pass/fail (copy-paste) ----
fail=0
npass=0
pass() { echo "PASS: $*"; npass=$((npass + 1)); }
fail_msg() { echo "FAIL: $*"; fail=1; }
warn() { echo "WARN: $*"; }

# ---- Canonical run_gate (from _template.sh 0.2.11; RC carries the verdict) ----
RC=0
run_gate() {
  local log="$1"; shift
  if [[ "${1:-}" == "--" ]]; then shift; fi
  set +e
  "$@" >"$log" 2>&1
  RC=$?
  set -e
}

# ---- Canonical mkphantom (from _template.sh 0.2.7; PHANTOM_PKG/PHANTOM_VER) ----
mkphantom() {
  local d="$1"
  mkdir -p "$d"
  printf '{\n  "name": "olaf-phantom-fixture",\n  "version": "1.0.0",\n  "dependencies": {\n    "%s": "%s"\n  }\n}\n' "$PHANTOM_PKG" "$PHANTOM_VER" > "$d/package.json"
  if command -v python3 >/dev/null 2>&1; then
    if ! python3 -c "import json,sys;json.load(open(sys.argv[1]))" "$d/package.json" 2>/dev/null; then
      fail_msg "synth fixture invalid JSON: $d/package.json"
    fi
  fi
}

# dotnet lives in /usr/bin on this box and Fedora usrmerges /bin -> /usr/bin,
# so the no-runtime PATH is the publish dir ONLY (apphost is absolute-pathed).
prove_no_dotnet() {
  local stripped="$1"
  if env "PATH=$stripped" sh -c 'command -v dotnet' >/dev/null 2>&1; then
    fail_msg "no-runtime PATH still resolves dotnet (PATH=$stripped)"
    return 1
  fi
  pass "no-runtime PATH resolves no dotnet"
  return 0
}

publish_rid() {
  local rid="$1"
  local pubdir="$REPO_ROOT/src/Olaf.Cli/bin/Release/net10.0/$rid/publish"
  local apphost="$pubdir/Olaf.Cli"
  if [[ "$rid" == "win-x64" ]]; then
    apphost="$pubdir/Olaf.Cli.exe"
  fi
  echo "-- publish $rid --"
  run_gate "$WORKDIR/publish-$rid.log" -- timeout "$TIMEOUT_SECS" dotnet publish "$REPO_ROOT/src/Olaf.Cli/Olaf.Cli.csproj" -c Release -p:PublishRID="$rid" --nologo -v minimal
  if [[ "$RC" -ne 0 ]]; then
    fail_msg "publish $rid exited $RC (log: $WORKDIR/publish-$rid.log)"
    return
  fi
  pass "publish $rid exit 0"
  # R1 tripwire: single-file analyzer warnings must be zero (compiler-code
  # shape only, so the "0 Warning(s)" summary line never matches).
  if grep -Eq 'warning (CS|IL|SYSLIB|NETSDK)[0-9]+' "$WORKDIR/publish-$rid.log"; then
    fail_msg "publish $rid emitted analyzer warnings (R1 tripwire)"
  else
    pass "publish $rid zero analyzer warnings"
  fi
  # Single-file gate: exactly the apphost exists, no *.dll beside it.
  if [[ ! -f "$apphost" ]]; then
    fail_msg "publish $rid missing apphost $apphost"
    return
  fi
  pass "publish $rid apphost present ($(basename "$apphost"))"
  local dlls=("$pubdir"/*.dll)
  if (( ${#dlls[@]} > 0 )); then
    fail_msg "publish $rid not single-file: ${#dlls[@]} *.dll beside apphost"
  else
    pass "publish $rid single-file (no *.dll)"
  fi
  local bytes
  bytes="$(stat -c %s "$apphost")"
  echo "SIZE: $rid $bytes bytes ($apphost)"
  if [[ "$bytes" -eq 0 ]]; then
    fail_msg "publish $rid apphost zero-size"
  else
    pass "publish $rid apphost non-empty ($bytes bytes)"
  fi
  if (( bytes < 10485760 )); then
    warn "publish $rid apphost <10MiB ($bytes bytes) — suspiciously small for self-contained"
  fi
}

smoke_linux() {
  local rid="linux-x64"
  local pubdir="$REPO_ROOT/src/Olaf.Cli/bin/Release/net10.0/$rid/publish"
  local apphost="$pubdir/Olaf.Cli"
  echo "-- smoke $rid (no-runtime, dotnet stripped from PATH) --"
  if [[ ! -f "$apphost" ]]; then
    fail_msg "smoke $rid: apphost missing (publish first)"
    return
  fi
  prove_no_dotnet "$pubdir" || return
  chmod +x "$apphost"

  run_gate "$WORKDIR/smoke-help.log" -- env "PATH=$pubdir" "$TIMEOUT_BIN" "$SMOKE_TIMEOUT" "$apphost" --help
  if [[ "$RC" -ne 0 ]]; then fail_msg "smoke --help exited $RC (want 0)"; else pass "smoke --help exit 0"; fi

  run_gate "$WORKDIR/smoke-version.log" -- env "PATH=$pubdir" "$TIMEOUT_BIN" "$SMOKE_TIMEOUT" "$apphost" --version
  if [[ "$RC" -ne 0 ]]; then fail_msg "smoke --version exited $RC (want 0)"; else pass "smoke --version exit 0"; fi

  run_gate "$WORKDIR/smoke-generate.log" -- env "PATH=$pubdir" "$TIMEOUT_BIN" "$SMOKE_TIMEOUT" "$apphost" generate "$NPMFIX" --format json --offline
  if [[ "$RC" -ne 0 ]]; then
    fail_msg "smoke npm-fixture offline generate exited $RC (want 0)"
  else
    pass "smoke npm-fixture offline generate exit 0"
    if python3 -c "import json,sys;json.load(open(sys.argv[1]))" "$WORKDIR/smoke-generate.log" 2>/dev/null; then
      pass "smoke npm-fixture offline generate stdout parses as JSON"
    else
      fail_msg "smoke npm-fixture offline generate stdout is not JSON"
    fi
  fi

  local phantom="$WORKDIR/phantom"
  mkphantom "$phantom"
  run_gate "$WORKDIR/smoke-strict.log" -- env "PATH=$pubdir" "$TIMEOUT_BIN" "$SMOKE_TIMEOUT" "$apphost" generate "$phantom" --format json --offline --strict
  if [[ "$RC" -ne 1 ]]; then
    fail_msg "smoke phantom --strict exited $RC (want 1)"
  else
    pass "smoke phantom --strict exit 1"
  fi

  run_gate "$WORKDIR/smoke-missing.log" -- env "PATH=$pubdir" "$TIMEOUT_BIN" "$SMOKE_TIMEOUT" "$apphost" generate "$WORKDIR/does-not-exist-olaf" --format json --offline
  if [[ "$RC" -ne 2 ]]; then
    fail_msg "smoke missing-path exited $RC (want 2)"
  elif grep -q "Input not found" "$WORKDIR/smoke-missing.log"; then
    pass "smoke missing-path exit 2 + Input-not-found"
  else
    fail_msg "smoke missing-path exit 2 but no Input-not-found on output"
  fi

  run_gate "$WORKDIR/smoke-badformat.log" -- env "PATH=$pubdir" "$TIMEOUT_BIN" "$SMOKE_TIMEOUT" "$apphost" generate "$NPMFIX" --format bogus --offline
  if [[ "$RC" -ne 2 ]]; then
    fail_msg "smoke bad-format exited $RC (want 2)"
  elif grep -q "Unsupported format" "$WORKDIR/smoke-badformat.log"; then
    pass "smoke bad-format exit 2 + Unsupported-format"
  else
    fail_msg "smoke bad-format exit 2 but no Unsupported-format on output"
  fi
}

echo "== publish-smoke-probe v$VERSION =="
echo "repo: $REPO_ROOT"
echo "rid: $RID"

RIDS=()
if [[ "$RID" == "all" ]]; then
  RIDS=(linux-x64 osx-arm64 win-x64)
else
  RIDS=("$RID")
fi

for r in "${RIDS[@]}"; do
  publish_rid "$r"
  if [[ "$r" != "linux-x64" ]]; then
    echo "NOTE: $r runtime smoke skipped on linux (existence+size+single-file only, runtime CI-only)"
  fi
done

if [[ "$RID" == "all" || "$RID" == "linux-x64" ]]; then
  smoke_linux
else
  echo "NOTE: runtime smoke skipped (--rid $RID is not linux-x64)"
fi

echo "== summary =="
if [[ "$fail" -eq 0 ]]; then
  echo "PUBLISH-SMOKE-PROBE OK: $npass PASS (rid=$RID)"
  exit 0
else
  echo "PUBLISH-SMOKE-PROBE FAIL: $npass PASS, see FAIL lines above (rid=$RID)" >&2
  exit 1
fi
