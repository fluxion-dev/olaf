#!/usr/bin/env bash
# pages-verify-probe.sh — Pages home-page verification probe for issue #131.
# P1 (6): docs/index.html exists; HERO_MARKER greppable; no <script element;
#   no local src/href; invented-surface gate (16 removed flags + `olaf scan` +
#   txt/html code-span formats + --reason); every concrete `olaf ...` command
#   line pasted in the page re-executed DLL-direct via run_gate (exit + token).
# P2 (4): all badge img-src URLs curl -fSL 200 (license badge 404 allowed ONLY
#   when LICENSE is absent — advisory WARN, never forced green).
# P3 (3): live URL 200 + HERO_MARKER in served bytes + no <script in served.
# P4 (2): pages.yml run poll (gh run list workflow=pages.yml lookup + timeout
#   gh run watch --exit-status; timeout FAILs, never infinite sleep).
# Pre-deploy (Pages not enabled — gh api pages -> 404): P3/P4 FAIL cleanly
# (never hang: timeout + curl --max-time; never forced green). Full green
# happens post-enable (plan Step 6). Shell MUST be bash. No pipestatus reads
# (fqn-lint pipefail arm). $TIMEOUT_BIN captured before any PATH use.
# Rules: repo-relative, idempotent, no secrets, exit 0/1/2.
VERSION="0.1.0"
set -euo pipefail
shopt -s nullglob

# ---- Flags (additive-only) ----
LIVE_URL="https://fluxion-dev.github.io/olaf/"
TIMEOUT_SECS=600
REPO_ROOT=""
WORKDIR=""
KEEP_TEMP=0
CURL_MAX=30

usage() {
  cat <<EOF
Usage: $(basename "$0") [options]

Pages home-page verification: P1 page gates + P2 badge 200s + P3 live 200 and
hero marker + P4 pages.yml workflow poll.

Options:
  --url <live-url>      Pages URL (default: $LIVE_URL)
  --timeout <secs>      P4 watch timeout in seconds (default: 600)
  --repo-root <dir>     Repo root (default: git top-level or script-relative fallback)
  --workdir <dir>       Work dir for logs/fetches (default: mktemp)
  --keep-temp           Keep work dir for debugging (default: remove)
  --help                Show this help and exit 0
  --version             Show version and exit 0

Examples:
  $(basename "$0") --repo-root .
  $(basename "$0") --url https://fluxion-dev.github.io/olaf/ --timeout 300

Pre-deploy DRY-RUN: Pages not enabled yet, so P3/P4 FAIL cleanly with
"no <thing>" lines (exit 1) proving absence detection. Full green happens
post-enable (plan Step 6).

Exit codes: 0 PASS, 1 FAIL (probe assertion failed), 2 usage/environment error.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --help) usage; exit 0 ;;
    --version) echo "$(basename "$0") $VERSION"; exit 0 ;;
    --url) LIVE_URL="${2:-}"; shift 2 ;;
    --url=*) LIVE_URL="${1#*=}"; shift ;;
    --timeout) TIMEOUT_SECS="${2:-}"; shift 2 ;;
    --timeout=*) TIMEOUT_SECS="${1#*=}"; shift ;;
    --repo-root) REPO_ROOT="${2:-}"; shift 2 ;;
    --repo-root=*) REPO_ROOT="${1#*=}"; shift ;;
    --workdir) WORKDIR="${2:-}"; shift 2 ;;
    --workdir=*) WORKDIR="${1#*=}"; shift ;;
    --keep-temp) KEEP_TEMP=1; shift ;;
    --*) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    *) echo "Unexpected positional: $1" >&2; usage >&2; exit 2 ;;
  esac
done

if ! [[ "$TIMEOUT_SECS" =~ ^[0-9]+$ ]] || [[ "$TIMEOUT_SECS" -eq 0 ]]; then
  echo "Invalid --timeout '$TIMEOUT_SECS': must be a positive integer." >&2; exit 2
fi
if [[ -z "$LIVE_URL" ]]; then
  echo "Invalid --url: must be non-empty." >&2; exit 2
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

for cmd in gh curl timeout python3 dotnet; do
  if ! command -v "$cmd" >/dev/null 2>&1; then
    echo "Missing required command: $cmd" >&2
    exit 2
  fi
done
# Absolute path: captured before any PATH manipulation so timeouts keep
# resolving (bare timeout -> 127 under stripped PATHs).
TIMEOUT_BIN="$(command -v timeout)"

if [[ -z "$WORKDIR" ]]; then
  WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/pages-verify-XXXXXX")"
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

HERO_MARKER="License scanner:"
PAGE="$REPO_ROOT/docs/index.html"
WORKFLOW="$REPO_ROOT/.github/workflows/pages.yml"
# 16 removed flags (cli-ux-probe.sh DELETED_FLAGS, issue #123 collapse).
REMOVED_FLAGS="--input --template --cache-dir --no-cache --refresh-cache --cache-ttl-days --ecosystem --max-image-mb --verbose --quiet --allow --deny --rules --direct-only --include-transitive --group-by-license"
# 5-flag allowlist: every --flag on a page `olaf ...` line must be one of these
# (+ --help/--version affordances).
ALLOWED_FLAGS="--format --out --force --strict --offline --help --version"

echo "== pages-verify-probe v$VERSION =="
echo "repo-root: $REPO_ROOT"
echo "live-url: $LIVE_URL"

# ---- P1 (6): page gates + command re-exec ----
echo "-- P1: page gates --"
if [[ -f "$PAGE" ]]; then
  pass "P1: docs/index.html exists"
else
  fail_msg "P1: docs/index.html missing (pre-Step-1: page not created)"
fi

if [[ -f "$PAGE" ]]; then
  if grep -qF -- "$HERO_MARKER" "$PAGE"; then
    pass "P1: hero marker '$HERO_MARKER' present"
  else
    fail_msg "P1: hero marker '$HERO_MARKER' absent"
  fi
  if grep -q -- '<script' "$PAGE"; then
    fail_msg "P1: <script element found: $(grep -n -- '<script' "$PAGE" | head -3 | tr '\n' ' ')"
  else
    pass "P1: no <script element"
  fi
  LOCALREFS="$(grep -oE -- '(src|href)="[^"]*"' "$PAGE" | grep -vE -- '(src|href)="(https?://|#|mailto:)' || true)"
  if [[ -n "$LOCALREFS" ]]; then
    fail_msg "P1: local src/href refs: $(printf '%s' "$LOCALREFS" | head -3 | tr '\n' ' ')"
  else
    pass "P1: no local src/href refs"
  fi
  # Invented-surface gate: 16 removed flags + `olaf scan` + txt/html code-span
  # formats + --reason must be absent (bare `html` matches markup, so the
  # format arm anchors on code spans / --format values only).
  INVENTED=""
  for flag in $REMOVED_FLAGS; do
    if grep -qF -- "$flag" "$PAGE"; then
      INVENTED="$INVENTED $flag"
    fi
  done
  if grep -qF -- "olaf scan" "$PAGE"; then
    INVENTED="$INVENTED olaf-scan"
  fi
  if grep -qE -- '<code>(txt|html)</code>|--format [^<]*(txt|html)|--reason' "$PAGE"; then
    INVENTED="$INVENTED txt/html/reason-format"
  fi
  if [[ -z "$INVENTED" ]]; then
    pass "P1: invented-surface gate clean (16 removed + olaf-scan + txt/html/reason)"
  else
    fail_msg "P1: invented-surface hits:$INVENTED"
  fi
  # Re-exec: extract concrete `olaf ...` command lines (strip tags/entities,
  # drop placeholder lines), run each DLL-direct, assert exit + stdout token.
  python3 - "$PAGE" "$WORKDIR/page-cmds.txt" <<'PY' 2>/dev/null
import re, sys, html
src = open(sys.argv[1]).read()
text = re.sub(r'<[^>]+>', '', src)
text = html.unescape(text)
cmds = []
for line in text.splitlines():
    s = line.strip()
    # Commands only: `olaf generate ...` or `olaf --flag` (prose lines like
    # `<title>olaf ...` strip to `olaf ...` but never match these two shapes).
    if not re.match(r'^olaf(\s+generate|\s+--)', s):
        continue
    if re.search(r'<DIR>|<fmt>|<file>|<out>|\.\.\.|\[', s):
        continue
    cmds.append(s)
open(sys.argv[2], 'w').write('\n'.join(cmds) + ('\n' if cmds else ''))
PY
  mapfile -t CMDS < "$WORKDIR/page-cmds.txt"
  if [[ "${#CMDS[@]}" -eq 0 ]]; then
    fail_msg "P1: no concrete olaf commands extracted from page"
  else
    echo "  info: extracted ${#CMDS[@]} concrete page command(s)"
    run_gate "$WORKDIR/build.log" -- dotnet build "$REPO_ROOT/src/Olaf.Cli/Olaf.Cli.csproj" --verbosity minimal
    if [[ "$RC" -ne 0 ]]; then
      fail_msg "P1: dotnet build exited $RC (log: $WORKDIR/build.log)"
    else
      CLI_DLL="$(find "$REPO_ROOT/src/Olaf.Cli/bin/Debug" -name Olaf.Cli.dll | head -1)"
      if [[ -z "$CLI_DLL" ]]; then
        fail_msg "P1: built Olaf.Cli.dll not found under src/Olaf.Cli/bin/Debug"
      else
        REEXEC_FAIL=0
        NRE=0
        for cmd in "${CMDS[@]}"; do
          # shellcheck disable=SC2086
          read -r -a ARGS <<< "$cmd"
          if [[ "${ARGS[0]}" != "olaf" ]]; then continue; fi
          ARGS[0]="$CLI_DLL"
          WANT_RC=0
          if [[ "$cmd" == *"--strict"* ]] && [[ "$cmd" == *"Fixtures"* ]]; then
            WANT_RC=1
          fi
          TOKEN=""
          case "$cmd" in
            *"--help"*) TOKEN="generate" ;;
            *"--version"*) TOKEN="0.1.0" ;;
            *"generate"*) TOKEN="total" ;;
          esac
          NRE=$((NRE + 1))
          run_gate "$WORKDIR/reexec-$NRE.log" -- dotnet "$CLI_DLL" "${ARGS[@]:1}"
          if [[ "$RC" -ne "$WANT_RC" ]]; then
            echo "  info: re-exec FAIL exit: '$cmd' -> $RC (want $WANT_RC)"
            REEXEC_FAIL=1
            continue
          fi
          if [[ -n "$TOKEN" ]] && ! grep -qF -- "$TOKEN" "$WORKDIR/reexec-$NRE.log"; then
            echo "  info: re-exec FAIL token: '$cmd' lacks '$TOKEN'"
            REEXEC_FAIL=1
            continue
          fi
          # 5-flag allowlist: every --token on the page line must be allowed.
          BADFLAGS=""
          for tok in $cmd; do
            case "$tok" in
              --*)
                ok=0
                for allowed in $ALLOWED_FLAGS; do
                  if [[ "$tok" == "$allowed" ]]; then ok=1; break; fi
                done
                if (( ! ok )); then BADFLAGS="$BADFLAGS $tok"; fi
                ;;
            esac
          done
          if [[ -n "$BADFLAGS" ]]; then
            echo "  info: re-exec FAIL allowlist: '$cmd' carries:$BADFLAGS"
            REEXEC_FAIL=1
            continue
          fi
          echo "  info: re-exec ok ($RC): $cmd"
        done
        if (( ! REEXEC_FAIL )) && (( NRE > 0 )); then
          pass "P1: $NRE page command(s) re-executed (exit + token + 5-flag allowlist)"
        else
          fail_msg "P1: page command re-exec failed (see info lines above)"
        fi
      fi
    fi
  fi
else
  fail_msg "P1: hero marker check skipped (no page)"
  fail_msg "P1: no-<script check skipped (no page)"
  fail_msg "P1: local-ref check skipped (no page)"
  fail_msg "P1: invented-surface gate skipped (no page)"
  fail_msg "P1: command re-exec skipped (no page)"
fi

# ---- P2 (4): badge curl-200 ----
echo "-- P2: badge URLs --"
if [[ -f "$PAGE" ]]; then
  grep -oE -- 'src="https://[^"]+"' "$PAGE" | sed 's/^src="//; s/"$//' | sort -u > "$WORKDIR/badge-urls.txt" || true
  NURLS="$(wc -l < "$WORKDIR/badge-urls.txt" | tr -d ' ')"
  if [[ "$NURLS" -eq 0 ]]; then
    for i in 1 2 3 4; do fail_msg "P2: no badge URLs extracted from page (arm $i)"; done
  else
    echo "  info: extracted $NURLS badge URL(s) from page"
    i=0
    while IFS= read -r url; do
      [[ -z "$url" ]] && continue
      i=$((i + 1))
      run_gate "$WORKDIR/badge-$i.log" -- "$TIMEOUT_BIN" "$CURL_MAX" curl -fSL --max-time "$CURL_MAX" -o /dev/null "$url"
      if [[ "$RC" -eq 0 ]]; then
        pass "P2: badge 200: $url"
      elif [[ "$url" == *"license"* ]] && [[ ! -f "$REPO_ROOT/LICENSE" ]]; then
        warn "P2: license badge unreachable ($RC) but LICENSE absent — allowed pre-Step-4: $url"
      else
        fail_msg "P2: badge curl exited $RC: $url (log: $WORKDIR/badge-$i.log)"
      fi
    done < "$WORKDIR/badge-urls.txt"
  fi
else
  for i in 1 2 3 4; do fail_msg "P2: badge arm $i skipped (no page)"; done
fi

# ---- P3 (3): live 200 + hero marker + no script ----
echo "-- P3: live page --"
run_gate "$WORKDIR/live.html" -- "$TIMEOUT_BIN" "$CURL_MAX" curl -fSL --max-time "$CURL_MAX" "$LIVE_URL"
if [[ "$RC" -ne 0 ]]; then
  fail_msg "P3: live URL fetch exited $RC (pre-deploy: Pages not enabled yet): $LIVE_URL"
  fail_msg "P3: hero marker check skipped (no served bytes)"
  fail_msg "P3: no-<script check skipped (no served bytes)"
else
  pass "P3: live URL 200: $LIVE_URL"
  if grep -qF -- "$HERO_MARKER" "$WORKDIR/live.html"; then
    pass "P3: hero marker '$HERO_MARKER' in served bytes"
  else
    fail_msg "P3: hero marker '$HERO_MARKER' absent from served bytes"
  fi
  if grep -q -- '<script' "$WORKDIR/live.html"; then
    fail_msg "P3: <script element in served bytes"
  else
    pass "P3: no <script element in served bytes"
  fi
fi

# ---- P4 (2): pages.yml run poll ----
echo "-- P4: pages.yml workflow --"
if [[ ! -f "$WORKFLOW" ]]; then
  fail_msg "P4: pages.yml absent (pre-Step-2: workflow not created)"
  fail_msg "P4: watch skipped (no workflow file)"
else
  run_gate "$WORKDIR/pages-runs.json" -- gh run list --workflow pages.yml --limit 20 \
    --json databaseId,headBranch,status,conclusion,url
  if [[ "$RC" -ne 0 ]]; then
    fail_msg "P4: gh run list exited $RC (log: $WORKDIR/pages-runs.json)"
    fail_msg "P4: watch skipped (no run list)"
  else
    RUN_ID="$(python3 - "$WORKDIR/pages-runs.json" <<'PY' 2>/dev/null
import json, sys
try:
    runs = json.load(open(sys.argv[1]))
except Exception:
    sys.exit(1)
if isinstance(runs, list) and runs:
    print(runs[0].get("databaseId", ""))
PY
)"
    if [[ -z "$RUN_ID" ]]; then
      fail_msg "P4: no pages.yml workflow run found (pre-deploy: workflow never ran)"
      fail_msg "P4: watch skipped (no run)"
    else
      pass "P4: found pages.yml run $RUN_ID"
      run_gate "$WORKDIR/pages-watch.log" -- "$TIMEOUT_BIN" "$TIMEOUT_SECS" \
        gh run watch "$RUN_ID" --exit-status
      if [[ "$RC" -eq 124 ]]; then
        fail_msg "P4: gh run watch timed out after ${TIMEOUT_SECS}s (run $RUN_ID)"
      elif [[ "$RC" -ne 0 ]]; then
        fail_msg "P4: gh run watch exited $RC — run $RUN_ID did not succeed (log: $WORKDIR/pages-watch.log)"
      else
        pass "P4: run $RUN_ID completed successfully"
      fi
    fi
  fi
fi

echo "== summary =="
if [[ "$fail" -eq 0 ]]; then
  echo "PAGES-VERIFY-PROBE OK: $npass PASS + $nwarn WARN"
  exit 0
else
  echo "PAGES-VERIFY-PROBE FAIL: $npass PASS + $nwarn WARN, see FAIL lines above" >&2
  exit 1
fi
