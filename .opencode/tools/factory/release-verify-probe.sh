#!/usr/bin/env bash
# release-verify-probe.sh — tag-gated release verification probe for issue #128.
# Verifies the first v* tag exercised release.yml end-to-end on the GitHub Release:
#   Arm 1: poll the tag's release.yml workflow run to completion (gh run list lookup
#     by headBranch==tag + timeout-wrapped `gh run watch --exit-status`; timeout FAILs,
#     never infinite sleep).
#   Arm 2: `gh release view <tag>` asserts exactly 3 attached assets with the Step-1
#     names (olaf-linux-x64, olaf-osx-arm64, olaf-win-x64.exe), unique basenames,
#     nonzero sizes.
#   Arm 3: `gh release download <tag>` to temp dir + smoke the linux bundle with
#     dotnet stripped from PATH (--help→0, --version→0 containing tag-minus-v,
#     npm-fixture offline generate→0 + JSON parse; osx/win existence+size only).
# Smoke idioms reused from publish-smoke-probe.sh v0.2.0 (run_gate, dotnet-stripped
# PATH smoke, $TIMEOUT_BIN captured before PATH strip — bare timeout →127 under
# stripped PATH). Shell MUST be bash (zsh lacks shopt nullglob; GHA run: is bash).
# No-tag behavior (pre-tag DRY-RUN): each arm FAILs cleanly with a "no <thing> for
# tag" line (exit 1) — absence detection, never a forced green and never a set -e
# abort. Full green happens post-tag (plan Step 5).
# Rules: repo-relative, idempotent, no secrets, exit 0/1/2.
VERSION="0.1.0"
set -euo pipefail
shopt -s nullglob

# ---- Canonical root resolution (copy-paste; do not hardcode paths) ----
# Priority: --repo-root flag -> git top-level -> script-dir fallback.
# NOTE: promoted factory tools live at .opencode/tools/factory/ so repo root is
#   ../../.. from the script dir. Scratch prototypes live one level deeper
#   (.opencode/tools/factory/scratch/) so use ../../../.. there instead.
TAG=""
REPO_ROOT=""
TIMEOUT_SECS=1800
WORKDIR=""
KEEP_TEMP=0
SMOKE_TIMEOUT=120

usage() {
  cat <<EOF
Usage: $(basename "$0") --tag <v*> [options]

Tag-gated release verification: polls the tag's release.yml workflow run,
asserts exactly 3 RID release assets, downloads and smokes the linux bundle.

Options:
  --tag <v*>            Release tag to verify (required, must start with 'v')
  --repo-root <dir>     Repo root (default: git top-level or script-relative fallback)
  --timeout <secs>      Workflow-watch timeout in seconds (default: 1800)
  --workdir <dir>       Work dir for logs/downloads (default: mktemp)
  --keep-temp           Keep work dir for debugging (default: remove)
  --help                Show this help and exit 0
  --version             Show version and exit 0

Positional form '$(basename "$0") <v*>' is accepted as --tag.

Examples:
  $(basename "$0") --tag v0.1.3
  $(basename "$0") v0.1.3 --timeout 600 --keep-temp

Pre-tag DRY-RUN: no tag/release/run exists yet, so each arm FAILs cleanly
with a "no <thing>" line (exit 1) proving absence detection. Full green
happens post-tag.

Exit codes: 0 PASS, 1 FAIL (probe assertion failed), 2 usage/environment error.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --help) usage; exit 0 ;;
    --version) echo "$(basename "$0") $VERSION"; exit 0 ;;
    --tag) TAG="${2:-}"; shift 2 ;;
    --tag=*) TAG="${1#*=}"; shift ;;
    --repo-root) REPO_ROOT="${2:-}"; shift 2 ;;
    --repo-root=*) REPO_ROOT="${1#*=}"; shift ;;
    --timeout) TIMEOUT_SECS="${2:-}"; shift 2 ;;
    --timeout=*) TIMEOUT_SECS="${1#*=}"; shift ;;
    --workdir) WORKDIR="${2:-}"; shift 2 ;;
    --workdir=*) WORKDIR="${1#*=}"; shift ;;
    --keep-temp) KEEP_TEMP=1; shift ;;
    --*) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    *) if [[ -z "$TAG" ]]; then TAG="$1"; shift; else echo "Unexpected positional: $1" >&2; usage >&2; exit 2; fi ;;
  esac
done

if [[ -z "$TAG" ]]; then
  echo "Missing required --tag <v*>." >&2; usage >&2; exit 2
fi
case "$TAG" in
  v*) ;;
  *) echo "Invalid --tag '$TAG': must start with 'v' (e.g. v0.1.3)." >&2; exit 2 ;;
esac
if ! [[ "$TIMEOUT_SECS" =~ ^[0-9]+$ ]] || [[ "$TIMEOUT_SECS" -eq 0 ]]; then
  echo "Invalid --timeout '$TIMEOUT_SECS': must be a positive integer." >&2; exit 2
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

for cmd in gh timeout python3; do
  if ! command -v "$cmd" >/dev/null 2>&1; then
    echo "Missing required command: $cmd" >&2
    exit 2
  fi
done
# Absolute path: Arm-3 smoke runs execute under a dotnet-stripped PATH where bare
# `timeout` would not resolve (exit 127). Watch/poll runs keep the full PATH.
TIMEOUT_BIN="$(command -v timeout)"

NPMFIX="$REPO_ROOT/tests/Olaf.Tests/Fixtures/npm"
if [[ ! -f "$NPMFIX/package.json" ]]; then
  echo "Missing npm fixture: $NPMFIX/package.json" >&2
  exit 2
fi

if [[ -z "$WORKDIR" ]]; then
  WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/release-verify-XXXXXX")"
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
# Advisory WARNs never count in PASS totals (probe PASS-vs-WARN pin).
fail=0
npass=0
nwarn=0
pass() { echo "PASS: $*"; npass=$((npass + 1)); }
fail_msg() { echo "FAIL: $*"; fail=1; }
warn() { echo "WARN: $*"; nwarn=$((nwarn + 1)); }

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

# Expected Step-1 asset basenames (release.yml R1 rename arm).
WANT_LINUX="olaf-linux-x64"
WANT_OSX="olaf-osx-arm64"
WANT_WIN="olaf-win-x64.exe"

echo "== release-verify-probe v$VERSION =="
echo "tag: $TAG"

# ---- Arm 1: poll the tag's release.yml workflow run to completion ----
echo "-- Arm 1: workflow run for tag --"
RUN_ID=""
run_gate "$WORKDIR/run-list.log" -- gh run list --workflow release.yml --limit 20 \
  --json databaseId,headBranch,status,conclusion,url
if [[ "$RC" -ne 0 ]]; then
  fail_msg "Arm 1: gh run list exited $RC (log: $WORKDIR/run-list.log)"
else
  RUN_ID="$(python3 - "$WORKDIR/run-list.log" "$TAG" <<'PY' 2>/dev/null
import json, sys
log, want = sys.argv[1], sys.argv[2]
try:
    runs = json.load(open(log))
except Exception:
    sys.exit(1)
for r in runs:
    if isinstance(r, dict) and r.get("headBranch") == want:
        print(r.get("databaseId", ""))
        break
PY
)"
  if [[ -z "$RUN_ID" ]]; then
    fail_msg "Arm 1: no release.yml workflow run found for tag $TAG (pre-tag: run never triggered)"
  else
    pass "Arm 1: found release.yml run $RUN_ID for tag $TAG"
    run_gate "$WORKDIR/run-watch.log" -- "$TIMEOUT_BIN" "$TIMEOUT_SECS" \
      gh run watch "$RUN_ID" --exit-status
    if [[ "$RC" -eq 124 ]]; then
      fail_msg "Arm 1: gh run watch timed out after ${TIMEOUT_SECS}s (run $RUN_ID)"
    elif [[ "$RC" -ne 0 ]]; then
      fail_msg "Arm 1: gh run watch exited $RC — run $RUN_ID did not succeed (log: $WORKDIR/run-watch.log)"
    else
      pass "Arm 1: run $RUN_ID completed successfully"
    fi
  fi
fi

# ---- Arm 2: exactly 3 RID assets attached to the release ----
echo "-- Arm 2: release assets --"
ARM2_OK=0
run_gate "$WORKDIR/release-view.json" -- gh release view "$TAG" --json tagName,assets
if [[ "$RC" -ne 0 ]]; then
  fail_msg "Arm 2: gh release view $TAG exited $RC — no release for tag (pre-tag: tag never pushed)"
else
  ASSETS="$(python3 - "$WORKDIR/release-view.json" <<'PY' 2>/dev/null
import json, sys
try:
    data = json.load(open(sys.argv[1]))
except Exception:
    sys.exit(1)
for a in data.get("assets", []):
    if isinstance(a, dict):
        print(f"{a.get('name', '')}\t{a.get('size', 0)}")
PY
)"
  if [[ -z "$ASSETS" ]]; then
    fail_msg "Arm 2: release $TAG carries zero assets (want exactly 3)"
  else
    COUNT="$(printf '%s\n' "$ASSETS" | wc -l | tr -d ' ')"
    if [[ "$COUNT" -ne 3 ]]; then
      fail_msg "Arm 2: release $TAG carries $COUNT assets (want exactly 3): $(printf '%s' "$ASSETS" | cut -f1 | tr '\n' ' ')"
    else
      pass "Arm 2: release $TAG carries exactly 3 assets"
    fi
    NAMES="$(printf '%s\n' "$ASSETS" | cut -f1)"
    for want in "$WANT_LINUX" "$WANT_OSX" "$WANT_WIN"; do
      if printf '%s\n' "$NAMES" | grep -qxF -- "$want"; then
        pass "Arm 2: asset present: $want"
      else
        fail_msg "Arm 2: asset missing: $want (have: $(printf '%s' "$NAMES" | tr '\n' ' '))"
      fi
    done
    UNIQ="$(printf '%s\n' "$NAMES" | sort -u | wc -l | tr -d ' ')"
    if [[ "$UNIQ" -ne 3 ]]; then
      fail_msg "Arm 2: asset basenames not unique ($UNIQ unique of 3)"
    else
      pass "Arm 2: 3 unique basenames"
    fi
    ZEROS="$(printf '%s\n' "$ASSETS" | awk -F'\t' '$2+0==0 {print $1}')"
    if [[ -n "$ZEROS" ]]; then
      fail_msg "Arm 2: zero-size assets: $(printf '%s' "$ZEROS" | tr '\n' ' ')"
    else
      pass "Arm 2: all assets nonzero size"
    fi
    echo "SIZE-TABLE:"
    printf '%s\n' "$ASSETS" | while IFS="$(printf '\t')" read -r name size; do
      echo "SIZE: $name $size bytes"
    done
    if [[ "$COUNT" -eq 3 && "$UNIQ" -eq 3 && -z "$ZEROS" ]] && \
       printf '%s\n' "$NAMES" | grep -qxF -- "$WANT_LINUX" && \
       printf '%s\n' "$NAMES" | grep -qxF -- "$WANT_OSX" && \
       printf '%s\n' "$NAMES" | grep -qxF -- "$WANT_WIN"; then
      ARM2_OK=1
    fi
  fi
fi

# ---- Arm 3: download + smoke the attached linux bundle ----
echo "-- Arm 3: download + smoke attached linux bundle --"
DLDIR="$WORKDIR/dl"
mkdir -p "$DLDIR"
run_gate "$WORKDIR/release-download.log" -- gh release download "$TAG" --dir "$DLDIR"
if [[ "$RC" -ne 0 ]]; then
  fail_msg "Arm 3: gh release download $TAG exited $RC — no downloadable assets (pre-tag: release absent)"
else
  pass "Arm 3: gh release download $TAG exit 0"
  LINUX_BIN="$(find "$DLDIR" -name "$WANT_LINUX" -type f | head -1)"
  if [[ -z "$LINUX_BIN" ]]; then
    fail_msg "Arm 3: downloaded linux bundle missing ($WANT_LINUX not under $DLDIR)"
  else
    pass "Arm 3: downloaded linux bundle present ($LINUX_BIN)"
    chmod +x "$LINUX_BIN"
    BINDIR="$(dirname "$LINUX_BIN")"
    # no-runtime proof: dotnet must not resolve under the stripped PATH.
    if env "PATH=$BINDIR" sh -c 'command -v dotnet' >/dev/null 2>&1; then
      fail_msg "Arm 3: no-runtime PATH still resolves dotnet (PATH=$BINDIR)"
    else
      pass "Arm 3: no-runtime PATH resolves no dotnet"
    fi
    run_gate "$WORKDIR/dl-help.log" -- env "PATH=$BINDIR" "$TIMEOUT_BIN" "$SMOKE_TIMEOUT" "$LINUX_BIN" --help
    if [[ "$RC" -ne 0 ]]; then fail_msg "Arm 3: attached --help exited $RC (want 0)"; else pass "Arm 3: attached --help exit 0"; fi
    run_gate "$WORKDIR/dl-version.log" -- env "PATH=$BINDIR" "$TIMEOUT_BIN" "$SMOKE_TIMEOUT" "$LINUX_BIN" --version
    if [[ "$RC" -ne 0 ]]; then
      fail_msg "Arm 3: attached --version exited $RC (want 0)"
    else
      pass "Arm 3: attached --version exit 0"
      WANT_VER="${TAG#v}"
      if grep -qF -- "$WANT_VER" "$WORKDIR/dl-version.log"; then
        pass "Arm 3: attached --version contains $WANT_VER (== tag minus v)"
      else
        fail_msg "Arm 3: attached --version lacks $WANT_VER (got: $(head -1 "$WORKDIR/dl-version.log"))"
      fi
    fi
    run_gate "$WORKDIR/dl-generate.log" -- env "PATH=$BINDIR" "$TIMEOUT_BIN" "$SMOKE_TIMEOUT" "$LINUX_BIN" generate "$NPMFIX" --format json --offline
    if [[ "$RC" -ne 0 ]]; then
      fail_msg "Arm 3: attached npm-fixture offline generate exited $RC (want 0)"
    else
      pass "Arm 3: attached npm-fixture offline generate exit 0"
      if python3 -c "import json,sys;json.load(open(sys.argv[1]))" "$WORKDIR/dl-generate.log" 2>/dev/null; then
        pass "Arm 3: attached generate stdout parses as JSON"
      else
        fail_msg "Arm 3: attached generate stdout is not JSON"
      fi
    fi
  fi
  # osx/win: existence + size only (cannot execute on linux; runtime CI-only).
  for want in "$WANT_OSX" "$WANT_WIN"; do
    f="$(find "$DLDIR" -name "$want" -type f | head -1)"
    if [[ -z "$f" ]]; then
      fail_msg "Arm 3: downloaded bundle missing: $want"
    elif [[ ! -s "$f" ]]; then
      fail_msg "Arm 3: downloaded bundle zero-size: $want"
    else
      pass "Arm 3: $want present non-empty ($(stat -c %s "$f") bytes)"
    fi
  done
fi

echo "== summary =="
if [[ "$fail" -eq 0 ]]; then
  echo "RELEASE-VERIFY-PROBE OK: $npass PASS + $nwarn WARN (tag=$TAG)"
  exit 0
else
  echo "RELEASE-VERIFY-PROBE FAIL: $npass PASS + $nwarn WARN, see FAIL lines above (tag=$TAG)" >&2
  exit 1
fi
