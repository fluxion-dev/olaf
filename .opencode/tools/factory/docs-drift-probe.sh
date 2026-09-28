#!/usr/bin/env bash
# docs-drift-probe.sh — README docs-drift ASSERTS for the #126 single-file
# publish surface (Tool install section). ASSERTS ONLY, never rewrites: every
# arm parses README.md + live state and emits PASS/FAIL/WARN lines.
# Arms:
#   Tests:N — `Tests: (\d+)` in README.md vs live `dotnet test` Passed count
#     (authoritative summary line, NEVER grep -c Fact|Theory — Theories expand
#     at runtime: 579 attributes vs 664 cases foot-gun). FAIL prints the exact
#     replacement line.
#   size-table — the 3 RID rows (linux-x64|osx-arm64|win-x64) exist in order
#     with byte+MiB columns; MiB-range tolerance +-5 MiB, NEVER exact bytes
#     (SDK-volatile by design). Publish output is a gitignored build artifact:
#     a missing publish dir is WARN/SKIP for that RID's range check (row
#     existence+order still asserted), never FAIL.
#   no-tagged-release — contradiction gate: FAIL if case-insensitive
#     `no tagged release yet` coexists with the `v* tag builds` paragraph.
#   pin-consistency — all `v*`/--version pins in README resolve to a single
#     tag and equal src/Olaf.Cli/Olaf.Cli.csproj <Version> (normalized
#     leading-v stripped; FAIL lists the distinct set).
#   asset-URL identity — README `releases/download/<tag>/…` URLs
#     byte-identical to `gh release view <tag> --json assets` names+URLs
#     (tag derived from pin-consistency); FAIL on bare `Olaf.Cli[.exe]`
#     asset names. Repo is PRIVATE: `gh release view` (API path), never
#     anonymous curl (returns 404). Reuses release-verify-probe Arm-2
#     patterns (run_gate + python3 JSON parse) without reinvention.
#   private-note — if direct download URLs present, require an adjacent
#     `gh release download` command + private-404 note (presence + order +
#     20-line adjacency window above the first URL line).
# Rules: repo-relative, idempotent, no secrets, exit 0/1/2.
VERSION="0.2.0"
set -euo pipefail

# ---- Canonical root resolution (copy-paste; do not hardcode paths) ----
# Priority: --repo-root flag -> git top-level -> script-dir fallback.
# NOTE: promoted factory tools live at .opencode/tools/factory/ so repo root is
#   ../../.. from the script dir. Scratch prototypes live one level deeper
#   (.opencode/tools/factory/scratch/) so use ../../../.. there instead.
REPO_ROOT=""
TIMEOUT_SECS=900
WORKDIR=""
KEEP_TEMP=0

usage() {
  cat <<EOF
Usage: $(basename "$0") [options]

README docs-drift asserts for the single-file publish surface (ASSERTS ONLY,
never rewrites): Tests:N vs live dotnet test, 3-RID size-table rows
(existence+order+MiB +-5, missing publish dirs SKIP), no-tagged-release
contradiction gate, pin-consistency (README v*/--version pins == csproj
<Version>), asset-URL identity (README download URLs == gh release assets),
private-note (gh-download + private-404 adjacency).

Options:
  --repo-root <dir>     Repo root (default: git top-level or script-relative fallback)
  --timeout <secs>      Timeout for dotnet test in seconds (default: 900)
  --workdir <dir>       Work dir for logs (default: mktemp)
  --keep-temp           Keep work dir for debugging (default: remove)
  --help                Show this help and exit 0
  --version             Show version and exit 0

Examples:
  $(basename "$0")
  $(basename "$0") --repo-root /path/to/worktree

Exit codes: 0 PASS, 1 FAIL (docs drift found), 2 usage/environment error.
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
    --workdir) WORKDIR="${2:-}"; shift 2 ;;
    --workdir=*) WORKDIR="${1#*=}"; shift ;;
    --keep-temp) KEEP_TEMP=1; shift ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

if ! [[ "$TIMEOUT_SECS" =~ ^[0-9]+$ ]]; then
  echo "Invalid --timeout '$TIMEOUT_SECS': must be a positive integer." >&2
  exit 2
fi

# Resolve repo root: explicit flag > git top-level > script-relative fallback.
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

README="$REPO_ROOT/README.md"
if [[ ! -f "$README" ]]; then
  echo "Missing README: $README" >&2
  exit 2
fi

for cmd in dotnet timeout python3; do
  if ! command -v "$cmd" >/dev/null 2>&1; then
    echo "Missing required command: $cmd" >&2
    exit 2
  fi
done

if [[ -z "$WORKDIR" ]]; then
  WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/docs-drift-XXXXXX")"
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

echo "== docs-drift-probe v$VERSION =="
echo "repo: $REPO_ROOT"

# ---- Arm 1: Tests:N ----
echo "-- Tests:N --"
DOC_N="$(grep -oE 'Tests: [0-9]+' "$README" | head -1 | grep -oE '[0-9]+' || true)"
if [[ -z "$DOC_N" ]]; then
  fail_msg "Tests:N: no 'Tests: N' line in README.md"
else
  echo "README Tests:N = $DOC_N"
  run_gate "$WORKDIR/dotnet-test.log" -- timeout "$TIMEOUT_SECS" dotnet test "$REPO_ROOT/olaf.slnx" --verbosity minimal
  LAST="$(grep -E 'Passed!|Failed!' "$WORKDIR/dotnet-test.log" | tail -1 || true)"
  if [[ -z "$LAST" ]]; then
    fail_msg "Tests:N: no Passed!/Failed! summary in dotnet test output (build error? log: $WORKDIR/dotnet-test.log)"
  else
    # Authoritative passed count from the summary line (Theories expand at
    # runtime, so attribute grep counts NEVER match case totals).
    LIVE_N="$(echo "$LAST" | grep -oE 'Passed:[[:space:]]+[0-9]+' | head -1 | grep -oE '[0-9]+' || true)"
    if [[ -z "$LIVE_N" ]]; then
      fail_msg "Tests:N: cannot parse 'Passed: N' from summary: $LAST"
    elif [[ "$LIVE_N" == "$DOC_N" ]]; then
      pass "Tests:N README $DOC_N == live $LIVE_N"
    else
      fail_msg "Tests:N drift: README $DOC_N != live $LIVE_N; replacement line: Tests: $LIVE_N passing (\`dotnet test\`)."
    fi
  fi
fi

# ---- Arm 2: size-table ----
echo "-- size-table --"
RIDS=(linux-x64 osx-arm64 win-x64)
prev_line=0
rows_ok=1
for rid in "${RIDS[@]}"; do
  line="$(grep -nF "$rid" "$README" | grep -F 'bytes' | head -1 | cut -d: -f1 || true)"
  if [[ -z "$line" ]]; then
    fail_msg "size-table: no README row for RID $rid"
    rows_ok=0
    continue
  fi
  row="$(sed -n "${line}p" "$README")"
  if ! echo "$row" | grep -qE '[0-9,]+ bytes'; then
    fail_msg "size-table: $rid row lacks a bytes column: $row"
    rows_ok=0
    continue
  fi
  if ! echo "$row" | grep -qE '\(~?[0-9]+(\.[0-9]+)? MiB\)'; then
    fail_msg "size-table: $rid row lacks a MiB column: $row"
    rows_ok=0
    continue
  fi
  if (( line <= prev_line )); then
    fail_msg "size-table: $rid row out of order (line $line after $prev_line)"
    rows_ok=0
    continue
  fi
  prev_line="$line"
  pass "size-table: $rid row present in order with byte+MiB columns (line $line)"
done
if (( rows_ok )); then
  pass "size-table: 3 RID rows exist in order"
fi
# MiB-range check per RID (tolerance +-5 MiB, never exact bytes). A missing
# publish dir is SKIP (gitignored build artifact), never FAIL.
for rid in "${RIDS[@]}"; do
  apphost="$REPO_ROOT/src/Olaf.Cli/bin/Release/net10.0/$rid/publish/Olaf.Cli"
  if [[ "$rid" == "win-x64" ]]; then
    apphost="$REPO_ROOT/src/Olaf.Cli/bin/Release/net10.0/$rid/publish/Olaf.Cli.exe"
  fi
  if [[ ! -f "$apphost" ]]; then
    warn "size-table: $rid publish dir absent — SKIP MiB-range (row existence+order still gated above)"
    continue
  fi
  range_rc=0
  range_out="$(python3 - "$README" "$rid" "$apphost" <<'PY' 2>/dev/null
import re, sys, os
readme, rid, apphost = sys.argv[1], sys.argv[2], sys.argv[3]
text = open(readme).read()
row = next((l for l in text.splitlines() if rid in l and "bytes" in l), None)
if row is None:
    print("norow")
    sys.exit(1)
m = re.search(r'\(~?([0-9]+(?:\.[0-9]+)?)\s*MiB\)', row)
if not m:
    print("nomib")
    sys.exit(1)
doc_mib = float(m.group(1))
actual_mib = os.path.getsize(apphost) / (1024 * 1024)
print(f"{doc_mib:.1f} {actual_mib:.1f} {abs(actual_mib - doc_mib):.1f}")
PY
)" || range_rc=$?
  if (( range_rc != 0 )) || [[ -z "$range_out" ]]; then
    fail_msg "size-table: $rid MiB parse failed ($range_out)"
    continue
  fi
  doc_mib="${range_out%% *}"
  rest="${range_out#* }"
  actual_mib="${rest%% *}"
  delta="${rest##* }"
  if python3 -c "import sys; sys.exit(0 if float(sys.argv[1]) <= 5.0 else 1)" "$delta" 2>/dev/null; then
    pass "size-table: $rid MiB in range (doc ~${doc_mib} MiB, actual ~${actual_mib} MiB, delta ${delta} MiB <= 5)"
  else
    fail_msg "size-table: $rid MiB out of range (doc ~${doc_mib} MiB, actual ~${actual_mib} MiB, delta ${delta} MiB > 5)"
  fi
done

# ---- Arm 3: no-tagged-release contradiction gate ----
echo "-- no-tagged-release --"
if grep -qi 'no tagged release yet' "$README"; then
  if grep -qF 'v*` tag builds' "$README"; then
    fail_msg "no-tagged-release: 'no tagged release yet' contradicts the 'v* tag builds' paragraph — docs must resolve (cut a tag or reword)"
  else
    pass "no-tagged-release: hedge present but no v* tag-builds claim to contradict"
  fi
else
  pass "no-tagged-release: no stale hedge"
fi

# ---- Arm 4: pin-consistency (sets TAG for Arms 5-6) ----
echo "-- pin-consistency --"
TAG=""
CSPROJ_VER="$(grep -oE '<Version>[^<]+</Version>' "$REPO_ROOT/src/Olaf.Cli/Olaf.Cli.csproj" | head -1 | sed -E 's|</?Version>||g' || true)"
if [[ -z "$CSPROJ_VER" ]]; then
  fail_msg "pin-consistency: cannot parse <Version> from src/Olaf.Cli/Olaf.Cli.csproj"
else
  TAG="v$CSPROJ_VER"
  PINS="$(grep -oE 'v?[0-9]+\.[0-9]+\.[0-9]+-preview\.[0-9]+' "$README" || true)"
  if [[ -z "$PINS" ]]; then
    fail_msg "pin-consistency: no v*/--version pins in README.md (csproj $CSPROJ_VER)"
  else
    PIN_LINES="$(grep -cE 'v?[0-9]+\.[0-9]+\.[0-9]+-preview\.[0-9]+' "$README" || true)"
    UNIQUES="$(printf '%s\n' "$PINS" | sed -E 's/^v//' | sort -u | tr '\n' ' ')"
    NUNIQ="$(printf '%s\n' "$PINS" | sed -E 's/^v//' | sort -u | wc -l | tr -d ' ')"
    if [[ "$NUNIQ" -ne 1 ]]; then
      fail_msg "pin-consistency: README pins resolve to $NUNIQ distinct versions ($UNIQUES), want exactly 1"
      TAG=""
    elif [[ "${UNIQUES% }" != "$CSPROJ_VER" ]]; then
      fail_msg "pin-consistency: README pin ${UNIQUES% } != csproj $CSPROJ_VER"
      TAG=""
    else
      pass "pin-consistency: $PIN_LINES README pin lines resolve to single tag $TAG == csproj $CSPROJ_VER"
    fi
  fi
fi

# ---- Arm 5: asset-URL identity (live source: release-verify-probe Arm 2) ----
echo "-- asset-URL identity --"
if [[ -z "${TAG:-}" ]]; then
  fail_msg "asset-URL identity: SKIP — no single TAG from pin-consistency"
elif ! command -v gh >/dev/null 2>&1; then
  fail_msg "asset-URL identity: missing required command: gh (private repo — gh API path, never anonymous curl)"
else
  DOC_URLS="$(grep -oE 'https://github\.com/[^ '"'"'`)]*releases/download/[^ '"'"'`)]+' "$README" || true)"
  if [[ -z "$DOC_URLS" ]]; then
    pass "asset-URL identity: no direct download URLs in README — nothing to check"
  else
    BADTAG="$(printf '%s\n' "$DOC_URLS" | grep -vF "/$TAG/" || true)"
    if [[ -n "$BADTAG" ]]; then
      fail_msg "asset-URL identity: README URLs not under $TAG: $(printf '%s' "$BADTAG" | tr '\n' ' ')"
    else
      pass "asset-URL identity: all README download URLs carry $TAG"
    fi
    DOC_NAMES="$(printf '%s\n' "$DOC_URLS" | sed -E 's|.*/||')"
    BARE="$(printf '%s\n' "$DOC_NAMES" | grep -xE 'Olaf\.Cli(\.exe)?' || true)"
    if [[ -n "$BARE" ]]; then
      fail_msg "asset-URL identity: bare apphost asset name(s) in README: $(printf '%s' "$BARE" | tr '\n' ' ')"
    else
      pass "asset-URL identity: no bare Olaf.Cli[.exe] names in README URLs"
    fi
    printf '%s\n' "$DOC_URLS" | sort -u >"$WORKDIR/doc-urls.txt"
    run_gate "$WORKDIR/release-view-assets.json" -- gh release view "$TAG" --json assets
    if [[ "$RC" -ne 0 ]]; then
      fail_msg "asset-URL identity: gh release view $TAG exited $RC (private repo — use gh, never anonymous curl which 404s)"
    else
      LIVE_NAMES="$(python3 - "$WORKDIR/release-view-assets.json" <<'PY' 2>/dev/null
import json, sys
try:
    data = json.load(open(sys.argv[1]))
except Exception:
    sys.exit(1)
for a in data.get("assets", []):
    if isinstance(a, dict) and a.get("name"):
        print(a["name"])
PY
)"
      if [[ -z "$LIVE_NAMES" ]]; then
        fail_msg "asset-URL identity: release $TAG carries zero assets"
      else
        LIVE_BARE="$(printf '%s\n' "$LIVE_NAMES" | grep -xE 'Olaf\.Cli(\.exe)?' || true)"
        if [[ -n "$LIVE_BARE" ]]; then
          fail_msg "asset-URL identity: bare apphost asset name(s) on release: $(printf '%s' "$LIVE_BARE" | tr '\n' ' ')"
        else
          pass "asset-URL identity: no bare Olaf.Cli[.exe] names on release $TAG"
        fi
        cmp_rc=0
        cmp_out="$(python3 - "$WORKDIR/doc-urls.txt" "$WORKDIR/release-view-assets.json" <<'PY' 2>/dev/null
import json, sys
doc = sorted(set(l.strip() for l in open(sys.argv[1]) if l.strip()))
data = json.load(open(sys.argv[2]))
live_urls = sorted(set(a.get("url", "") for a in data.get("assets", []) if isinstance(a, dict) and a.get("url")))
live_names = sorted(set(a.get("name", "") for a in data.get("assets", []) if isinstance(a, dict) and a.get("name")))
doc_names = sorted(set(u.rstrip("/").split("/")[-1] for u in doc))
print("DOC_URLS=" + " ".join(doc))
print("LIVE_URLS=" + " ".join(live_urls))
print("DOC_NAMES=" + " ".join(doc_names))
print("LIVE_NAMES=" + " ".join(live_names))
sys.exit(0 if (doc == live_urls and doc_names == live_names) else 1)
PY
)" || cmp_rc=$?
        if (( cmp_rc != 0 )); then
          fail_msg "asset-URL identity: README URLs/names != release $TAG assets — $cmp_out"
        else
          pass "asset-URL identity: README URLs byte-identical to gh release view $TAG names+URLs"
        fi
      fi
    fi
  fi
fi

# ---- Arm 6: private-note ----
echo "-- private-note --"
if ! grep -qF 'releases/download/' "$README"; then
  pass "private-note: no direct download URLs — note N/A"
else
  FIRST_URL_LINE="$(grep -nF 'releases/download/' "$README" | head -1 | cut -d: -f1)"
  GH_LINE="$(grep -nF 'gh release download' "$README" | head -1 | cut -d: -f1 || true)"
  PRIV_LINE="$(grep -ni 'private' "$README" | head -1 | cut -d: -f1 || true)"
  NOTFOUND_LINE="$(grep -n '404' "$README" | head -1 | cut -d: -f1 || true)"
  note_bad=0
  if [[ -z "$GH_LINE" ]]; then
    fail_msg "private-note: direct download URLs present but no 'gh release download' command in README"
    note_bad=1
  elif (( GH_LINE > FIRST_URL_LINE )) || (( FIRST_URL_LINE - GH_LINE > 20 )); then
    fail_msg "private-note: 'gh release download' (L$GH_LINE) not adjacent above first URL (L$FIRST_URL_LINE, want <=20 lines above)"
    note_bad=1
  fi
  if [[ -z "$PRIV_LINE" || -z "$NOTFOUND_LINE" ]]; then
    fail_msg "private-note: direct download URLs present but private-404 note missing (want 'private' + '404')"
    note_bad=1
  elif (( PRIV_LINE > FIRST_URL_LINE )) || (( FIRST_URL_LINE - PRIV_LINE > 20 )); then
    fail_msg "private-note: private note (L$PRIV_LINE) not adjacent above first URL (L$FIRST_URL_LINE, want <=20 lines above)"
    note_bad=1
  fi
  if (( ! note_bad )); then
    pass "private-note: gh-download command + private-404 note adjacent above first URL (L$FIRST_URL_LINE)"
  fi
fi

echo "== summary =="
if [[ "$fail" -eq 0 ]]; then
  echo "DOCS-DRIFT-PROBE OK: $npass PASS"
  exit 0
else
  echo "DOCS-DRIFT-PROBE FAIL: $npass PASS, see FAIL lines above" >&2
  exit 1
fi
