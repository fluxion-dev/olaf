#!/usr/bin/env bash
# bundler-download-probe.sh -- bundler DownloadUrl locked-version probe for issue #167.
# Asserts the #167 fix: DownloadUrl must be constructed canonical from the
# LOCKED dependency (https://rubygems.org/downloads/{name}-{version}.gem with
# Uri.EscapeDataString on BOTH segments), never lifted verbatim from gem_uri
# (pre-fix: gem_uri names the latest release, so a rails 7.0.8 locked row got
# an 8.1.4 gem link). Zero new HTTP (Go precedent GoLicenseResolver.cs:51-58;
# rejected option (A) versions-list fetch would break CallCount==1 pins).
# Arms (offline-safe unless marked LIVE):
#   S1 source anchors (api/v1/gems fetch, ParseBundlerEnrichment,
#      gem_uri-must-not-feed-DownloadUrl, Go precedent comment); S2 suite
#      evidence (3 locked-in-URL method anchors + enrichment canonical pin +
#      BundlerResolverTests Fact census floor).
#   U1 source: canonical downloads/ construction with both EscapeDataString +
#      no verbatim gem_uri passthrough (THE #167 DEFECT gate); U2 suite pins
#      locked string in URL (7.0.8 present, 8.1.4 absent).
#   R mocked vectors via dotnet test --filter (stubbed HttpMessageHandler,
#      zero live HTTP): mismatch names locked (new regression), match==latest
#      unchanged (existing), 404/transport -> Unknown no-throw (companion).
#   C1 LIVE CLI smoke DLL-direct on synth Gemfile.lock (rails locked 7.0.4):
#      exit 0, locked version kept, downloadUrl exact, latest 8.x absent.
#   R1 canonical format-matrix regression (json/yaml/xml/md).
# Rules: repo-relative, idempotent, no secrets, exit 0/1/2.
VERSION="0.1.0"
set -euo pipefail

# ---- Root resolution (factory depth: tools/factory -> ../../..) ----
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

REPO_ROOT="$ROOT"
TIMEOUT_SECS=180
WORKDIR=""
KEEP_TEMP=0

usage() {
  cat <<'EOF'
Usage: bundler-download-probe.sh [options]

bundler DownloadUrl probe (issue #167): DownloadUrl is constructed canonical
from the LOCKED dependency version, never lifted verbatim from gem_uri
(which names the latest release). Zero new HTTP.

Options:
  --repo-root <dir>   Repo root under test (default: git top-level or
                      script-relative fallback; must contain olaf.slnx)
  --timeout <secs>    Per-scan timeout in seconds (default: 180)
  --workdir <dir>     Work dir (default: mktemp under ${TMPDIR:-/tmp})
  --keep-temp         Keep temp work dir for debugging (default: remove)
  --help              Show this help and exit 0
  --version           Show version and exit 0

Examples:
  bundler-download-probe.sh
  bundler-download-probe.sh --repo-root ../olaf-167 --timeout 120 --keep-temp

Exit codes: 0 PASS, 1 FAIL (probe assertion failed), 2 usage/environment error.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --help) usage; exit 0 ;;
    --version) echo "bundler-download-probe.sh $VERSION"; exit 0 ;;
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

# ---- Canonical pass/fail + advisory warns (from _template.sh) ----
# Binding (plan Step 3): advisory WARNs do NOT count in PASS totals
# (report `N PASS + M WARN`, never merged `N+M PASS`).
fail=0
warns=0
passes=0
pass() { echo "PASS: $*"; passes=$((passes + 1)); }
fail_msg() { echo "FAIL: $*"; fail=1; }
warn_msg() { echo "WARN: $*"; warns=$((warns + 1)); }

# ---- Canonical temp-dir with KEEP_TEMP (from _template.sh) ----
if [[ -z "$WORKDIR" ]]; then
  WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/bundler-download-XXXXXX")"
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

# Cache isolation: every scan below runs with a temp XDG cache so results
# reflect genuine resolution, never cross-tree disk-cache entries (post-#166
# false-PASS lesson: shared cache served stale records and masked the defect).
# XDG_CACHE_HOME wins on ALL OSes per DiskLicenseCache.
export XDG_CACHE_HOME="$WORKDIR/cache"
mkdir -p "$XDG_CACHE_HOME"

# ---- Canonical run_gate (from _template.sh 0.2.11) ----
RC=0
run_gate() {
  local log="$1"; shift
  if [[ "${1:-}" == "--" ]]; then shift; fi
  set +e
  "$@" >"$log" 2>&1
  RC=$?
  set -e
}

# ---- Canonical dep extractors (from _template.sh 0.2.2) ----
dep_field() {
  python3 - "$1" "$2" "$3" <<'PY' 2>/dev/null
import json, sys
path, want, field = sys.argv[1], sys.argv[2].lower(), sys.argv[3]
try:
    with open(path) as f:
        data = json.load(f)
except Exception:
    sys.exit(0)
items = data.get("licenses", data.get("dependencies", data.get("resolved", [])))
if isinstance(items, list):
    for it in items:
        if not isinstance(it, dict):
            continue
        dep = it.get("dependency", it)
        name = str(dep.get("name", it.get("name", "")))
        if name.lower() == want:
            v = dep.get(field, it.get(field, ""))
            if v is True:
                print("true")
            elif v is False:
                print("false")
            elif isinstance(v, str):
                print(v.lower() if field == "direct" else v)
            else:
                print(str(v) if v != "" else "")
            break
PY
}
dep_ver() { dep_field "$1" "$2" "version"; }

RESOLVER="$REPO_ROOT/src/Olaf.Resolvers/BundlerLicenseResolver.cs"
GO_PRECEDENT="$REPO_ROOT/src/Olaf.Resolvers/GoLicenseResolver.cs"
TESTS="$REPO_ROOT/tests/Olaf.Tests/Resolvers/ComposerBundlerResolverTests.cs"
ENRICH_TESTS="$REPO_ROOT/tests/Olaf.Tests/Resolvers/EnrichmentResolverTests.cs"
CSPROJ="$REPO_ROOT/tests/Olaf.Tests/Olaf.Tests.csproj"
FIXTURE="$REPO_ROOT/tests/Olaf.Tests/Fixtures/bundler"

echo "== bundler-download-probe v$VERSION =="
echo "repo: $REPO_ROOT"

echo "-- step 0: build CLI + test project (build-once, DLL-direct after) --"
run_gate "$WORKDIR/build-cli.log" -- dotnet build "$REPO_ROOT/src/Olaf.Cli" --nologo -v minimal
if [[ "$RC" -ne 0 ]]; then fail_msg "dotnet build Olaf.Cli failed (see $WORKDIR/build-cli.log)"; else pass "build Olaf.Cli green"; fi
run_gate "$WORKDIR/build-tests.log" -- dotnet build "$CSPROJ" --nologo -v minimal
if [[ "$RC" -ne 0 ]]; then fail_msg "dotnet build Olaf.Tests failed (see $WORKDIR/build-tests.log)"; else pass "build Olaf.Tests green"; fi
CLI_DLL="$(find "$REPO_ROOT/src/Olaf.Cli/bin/Debug" -name Olaf.Cli.dll 2>/dev/null | head -1)"
if [[ -z "$CLI_DLL" ]]; then
  fail_msg "Olaf.Cli.dll not found under src/Olaf.Cli/bin/Debug"
else
  pass "CLI DLL pinned: ${CLI_DLL#"$REPO_ROOT"/}"
fi
if [[ ! -d "$FIXTURE" ]]; then fail_msg "bundler fixture missing: $FIXTURE"; else pass "bundler fixture present: tests/Olaf.Tests/Fixtures/bundler"; fi

# run_scan for C1 + R1 (writes $WORKDIR/$tag.stdout, sets RC).
run_scan() {
  local tag="$1" indir="$2" fmt="$3"; shift 3
  run_gate "$WORKDIR/$tag.stdout" -- timeout "$TIMEOUT_SECS" dotnet "$CLI_DLL" generate "$indir" --format "$fmt" "$@"
}

echo "-- S arms: static surface (offline-safe, stubbed) --"
# S1: source anchors — latest-only metadata fetch, enrichment entry point,
# gem_uri vocab present only as a never-verbatim comment, Go precedent cited.
if [[ -f "$RESOLVER" ]]; then
  if grep -q 'rubygems.org/api/v1/gems/.*EscapeDataString(dependency.Name)' "$RESOLVER"; then
    pass "S1a latest-only metadata fetch anchored (api/v1/gems + EscapeDataString name)"
  else
    fail_msg "S1a api/v1/gems fetch anchor missing"
  fi
  if grep -q 'ParseBundlerEnrichment' "$RESOLVER"; then
    pass "S1b ParseBundlerEnrichment entry point present"
  else
    fail_msg "S1b ParseBundlerEnrichment missing"
  fi
  if grep -q 'gem_uri' "$RESOLVER"; then
    pass "S1c gem_uri vocab referenced (comment-only, never lifted verbatim)"
  else
    warn_msg "S1c gem_uri vocab absent entirely (defect gone, comment drift?)"
  fi
  if grep -q 'GoLicenseResolver.cs:51-58' "$RESOLVER" && [[ -f "$GO_PRECEDENT" ]]; then
    pass "S1d Go precedent cited (GoLicenseResolver.cs:51-58, NO-NEW-HTTP contract)"
  else
    fail_msg "S1d Go precedent citation missing"
  fi
else
  fail_msg "S1 resolver missing: src/Olaf.Resolvers/BundlerLicenseResolver.cs"
fi
# S2: suite evidence — 3 locked-in-URL method anchors + enrichment canonical
# pin + BundlerResolverTests Fact census floor (8 base + 3 new = 11).
if [[ -f "$TESTS" ]]; then
  S2MISS=""
  for anchor in "Should_NameLockedVersionInDownloadUrl_When_GemUriNamesLatestRelease" "Should_NameLockedVersionInDownloadUrl_When_GemUriMatchesLockedVersion" "Should_ReturnUnknownWithoutThrow_When_RubyGemsNotFoundOrTransportFails"; do
    if ! grep -q -- "$anchor" "$TESTS"; then S2MISS="$S2MISS $anchor"; fi
  done
  if [[ -z "$S2MISS" ]]; then
    pass "S2a 3 locked-in-URL suite anchors present (mismatch/match/unknown-companion)"
  else
    fail_msg "S2a suite anchors missing:$S2MISS"
  fi
  BFACTS="$(grep -c -e '\[Fact\]' "$TESTS" || true)"
  if [[ "$BFACTS" -ge 11 ]]; then
    pass "S2b BundlerResolverTests Fact census $BFACTS >= 11 (8 base + 3 #167 new)"
  else
    fail_msg "S2b BundlerResolverTests Fact census $BFACTS (want >= 11)"
  fi
else
  fail_msg "S2 suite missing: tests/Olaf.Tests/Resolvers/ComposerBundlerResolverTests.cs"
fi
if [[ -f "$ENRICH_TESTS" ]]; then
  if grep -q 'Should_HarvestSupplierDownloadWithoutHashes_When_RubyGemsJson' "$ENRICH_TESTS" \
    && grep -q 'https://rubygems.org/downloads/rails-7.0.8.gem' "$ENRICH_TESTS"; then
    pass "S2c enrichment suite pins canonical downloads/rails-7.0.8.gem"
  else
    fail_msg "S2c enrichment canonical pin missing"
  fi
  EFACTS="$(grep -c -e '\[Fact\]' "$ENRICH_TESTS" || true)"
  pass "S2d EnrichmentResolverTests Fact census $EFACTS (informational)"
else
  fail_msg "S2 enrichment suite missing: tests/Olaf.Tests/Resolvers/EnrichmentResolverTests.cs"
fi

echo "-- U arms: locked-version-in-URL (the #167 defect) --"
# U1 source: canonical downloads/ construction with EscapeDataString on BOTH
# segments; FAIL on verbatim gem_uri passthrough (TryGetString gem_uri read or
# bare gemUri fed to EnrichmentHelpers.Create).
if [[ -f "$RESOLVER" ]]; then
  if grep -q 'rubygems.org/downloads/.*EscapeDataString(dependency.Name).*EscapeDataString(dependency.Version)' "$RESOLVER"; then
    pass "U1a canonical downloads/ URL with EscapeDataString on both segments"
  else
    fail_msg "U1a canonical downloads/ construction missing (want downloads/ + escaped name + escaped version)"
  fi
  if grep -qE 'TryGetString\(root, "gem_uri"|[^_a-zA-Z]gemUri[,)]' "$RESOLVER"; then
    fail_msg "U1b verbatim gem_uri passthrough present (names latest, not locked)"
  else
    pass "U1b no verbatim gem_uri passthrough into DownloadUrl"
  fi
else
  fail_msg "U1 resolver missing"
fi
# U2 suite pins the locked string in the URL (7.0.8 present, 8.1.4 absent).
if [[ -f "$TESTS" ]]; then
  if grep -q 'DoesNotContain("8.1.4"' "$TESTS" && grep -q 'downloads/rails-7.0.8.gem' "$TESTS"; then
    pass "U2 suite pins locked 7.0.8 in URL, latest 8.1.4 absent"
  else
    fail_msg "U2 locked-string-in-URL pin missing (want 7.0.8 present + 8.1.4 DoesNotContain)"
  fi
else
  fail_msg "U2 suite missing"
fi

echo "-- R arms: mocked vectors (stubbed HTTP, zero live traffic) --"
run_vector() {
  local label="$1" expected="$2" filter="$3"
  local tag="rvec-$(echo "$label" | tr -c '[:alnum:]' '-' | tr -s '-')"
  run_gate "$WORKDIR/$tag.log" -- dotnet test "$CSPROJ" --no-build --verbosity minimal --filter "$filter"
  local last
  last="$(grep -E 'Passed!|Failed!' "$WORKDIR/$tag.log" | tail -1 || true)"
  if [[ -z "$last" ]]; then
    fail_msg "$label: no Passed!/Failed! summary (build/filter error?)"
    return
  fi
  local passed failed
  passed="$(echo "$last" | sed -n 's/.*Passed:[[:space:]]*\([0-9][0-9]*\).*/\1/p')"
  failed="$(echo "$last" | sed -n 's/.*Failed:[[:space:]]*\([0-9][0-9]*\).*/\1/p')"
  if [[ "$passed" == "$expected" && "$failed" == "0" ]]; then
    pass "$label: Passed $passed/$expected, Failed 0"
  else
    fail_msg "$label: Passed ${passed:-?}/$expected Failed ${failed:-?} (see $WORKDIR/$tag.log)"
  fi
}
run_vector "R-mismatch (locked 7.0.8 wins over stub 8.1.4)" 1 "FullyQualifiedName~Should_NameLockedVersionInDownloadUrl_When_GemUriNamesLatestRelease"
run_vector "R-match (locked==latest unchanged)" 1 "FullyQualifiedName~Should_NameLockedVersionInDownloadUrl_When_GemUriMatchesLockedVersion"
run_vector "R-unknown (404/transport Unknown no-throw)" 1 "FullyQualifiedName~Should_ReturnUnknownWithoutThrow_When_RubyGemsNotFoundOrTransportFails"

echo "-- C1 LIVE CLI smoke: synth Gemfile.lock keeps locked version in downloadUrl --"
# NOTE: the committed bundler fixture pins rails 7.0.8 while C1 synthesizes a
# single-gem lock at 7.0.4 so the "latest 8.x absent" assert is unambiguous
# (live latest rails is 8.x; any 8.x in the URL is the defect signature).
LOCK_FIX="$WORKDIR/lock-fixture"
mkdir -p "$LOCK_FIX"
printf 'GEM\n  remote: https://rubygems.org/\n  specs:\n    rails (7.0.4)\n\nPLATFORMS\n  ruby\n\nDEPENDENCIES\n  rails (= 7.0.4)\n' > "$LOCK_FIX/Gemfile.lock"
run_scan "c1-lock" "$LOCK_FIX" "json"
if [[ "$RC" -ne 0 ]]; then
  fail_msg "C1 lock-fixture scan exited $RC (want 0)"
elif grep -qi 'transport-error\|registry-error' "$WORKDIR/c1-lock.stdout"; then
  warn_msg "C1 offline-degraded (transport/registry error), asserts skipped"
else
  set +e
  python3 - "$WORKDIR/c1-lock.stdout" <<'PY' >"$WORKDIR/c1.out" 2>&1
import json, sys
data = json.load(open(sys.argv[1]))
items = data.get("licenses", [])
byname = {it.get("name", ""): it for it in items if isinstance(it, dict)}
fails = []
def check(cond, msg):
    print(("PASS " if cond else "FAIL ") + msg)
    if not cond:
        fails.append(msg)
check(data.get("summary", {}).get("total", 0) == 1, "total==1 (synth single-gem lock)")
it = byname.get("rails")
check(it is not None, "rails present")
if it is not None:
    check(it.get("version", "") == "7.0.4", "rails keeps locked 7.0.4")
    dl = it.get("downloadUrl", "") or ""
    check(dl == "https://rubygems.org/downloads/rails-7.0.4.gem", "downloadUrl exact (%s)" % dl)
    check("7.0.4" in dl, "downloadUrl embeds locked 7.0.4")
    check("8." not in dl, "downloadUrl names no 8.x latest")
sys.exit(1 if fails else 0)
PY
  C1RC=$?
  set -e
  while IFS= read -r line; do
    case "$line" in
      PASS*) pass "C1 $line" ;;
      FAIL*) fail_msg "C1 $line" ;;
      *) echo "C1-info: $line" ;;
    esac
  done < "$WORKDIR/c1.out"
  if [[ "$C1RC" -ne 0 && "$fail" -eq 0 ]]; then fail_msg "C1 python gate failed silently"; fi
  # dep_ver cross-check (canonical extractor reuse): locked version verbatim.
  if [[ "$(dep_ver "$WORKDIR/c1-lock.stdout" "rails")" == "7.0.4" ]]; then
    pass "C1 dep_ver cross-check: rails keeps 7.0.4"
  else
    fail_msg "C1 dep_ver cross-check: rails version [$(dep_ver "$WORKDIR/c1-lock.stdout" "rails")] (want 7.0.4)"
  fi
fi

# ---- Canonical R1 format-matrix regression (from _template.sh 0.2.3) ----
r1_matrix_regression() {
  # r1_matrix_regression [formats...]; default: json yaml xml html.
  local formats=("$@")
  (( ${#formats[@]} )) || formats=(json yaml xml html)
  local R1FAIL=0 f pat
  echo "-- R1 format-matrix regression --"
  for f in "${formats[@]}"; do
    run_scan "r1-$f" "$FIXTURE" "$f"
    if [[ "$RC" -ne 0 ]]; then
      if grep -qi 'transport-error\|registry-error' "$WORKDIR/r1-$f.stdout"; then
        warn_msg "R1/$f offline-degraded, skipped"
        continue
      fi
      fail_msg "R1/$f scan exited $RC"; R1FAIL=1; continue
    fi
    case "$f" in
      json) pat='"direct"' ;;
      yaml) pat='direct:' ;;
      xml) pat='<direct>' ;;
      md) pat='| Direct |' ;;
      html) pat='<th>Direct</th>' ;;
      *) fail_msg "R1/$f: no marker for format"; R1FAIL=1; continue ;;
    esac
    if grep -qF -- "$pat" "$WORKDIR/r1-$f.stdout"; then
      pass "R1/$f carries $pat"
    else
      fail_msg "R1/$f missing $pat"; R1FAIL=1
    fi
  done
  if (( ! R1FAIL )); then pass "R1 MATRIX OK: ${formats[*]} untouched"; fi
}
# Deviation from the canonical fragment (documented): html was deleted in #123
# (SupportedFormats is now json|yaml|xml|md|cyclonedx-json|cyclonedx-xml|spdx-json),
# so R1 runs json/yaml/xml/md with the md `| Direct |` marker from
# format-matrix-dump.sh 0.3.0. Narrowed call site with justification.
r1_matrix_regression json yaml xml md

echo "== summary =="
# PASS-vs-WARN pin (binding): WARNs never merge into PASS totals.
echo "BUNDLER-DOWNLOAD-PROBE: $passes PASS + $warns WARN (see PASS/WARN lines above)"
if [[ "$fail" -eq 0 ]]; then
  echo "BUNDLER-DOWNLOAD-PROBE OK"
  exit 0
else
  echo "BUNDLER-DOWNLOAD-PROBE FAIL" >&2
  exit 1
fi
